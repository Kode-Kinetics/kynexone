using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Controllers;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Audit;
using Zayra.Api.Infrastructure.Auth;
using Zayra.Api.Models;
using Kit = Zayra.Api.Tests.DebtRacePostgresKit;

namespace Zayra.Api.Tests;

/// <summary>Shared seed, package and read helpers for the opening-balance import tests that drive loans and advances on Postgres.</summary>
internal static class OpeningBalanceImportKit
{
    internal const string OpeningBalanceEvent = "OpeningBalance";

    internal static async Task<(Kit.Borrower B, DateOnly Cutover, string Number)> SeedAsync(PostgresFixture fx, string prefix = "LEG")
    {
        var b = await Kit.SeedBorrowerAsync(fx, 100m, 2);
        return (b, new DateOnly(b.Today.Year, b.Today.Month, 1), $"{prefix}-{Guid.NewGuid():N}"[..20]);
    }

    /// <summary>Commits one package and returns its row errors (none expected unless <paramref name="expectErrors"/>).</summary>
    internal static async Task<IReadOnlyList<string>> CommitAsync(PostgresFixture fx, Kit.Borrower b, string section, string csv,
        bool withCutover = false, bool expectErrors = false, bool dryRun = false, string[]? roles = null, Guid? userId = null,
        string[]? denied = null)
    {
        var sections = new Dictionary<string, string> { [section] = csv };
        if (withCutover) sections["companyCutover"] = await CutoverCsvAsync(fx, b);
        await using var db = fx.CreateDb();
        var controller = Migration(db, b.Tenant, roles ?? new[] { "Admin" }, userId, denied);
        var result = dryRun
            ? await controller.Preview(new MigrationPackageRequest($"pkg-{Guid.NewGuid():N}", sections, true), default)
            : await controller.Commit(new MigrationPackageRequest($"pkg-{Guid.NewGuid():N}", sections, false), default);
        var dto = Assert.IsType<MigrationReconciliationDto>(Assert.IsType<OkObjectResult>(result.Result).Value);
        if (!expectErrors) Assert.True(dto.Errors.Count == 0, string.Join(" | ", dto.Errors));
        return dto.Errors.ToList();
    }

    internal static async Task<string> CutoverCsvAsync(PostgresFixture fx, Kit.Borrower b)
    {
        await using var read = fx.CreateDb();
        var registration = (await read.Companies.AsNoTracking().SingleAsync(c => c.Id == b.CompanyId)).RegistrationNumber;
        var cutover = new DateOnly(b.Today.Year, b.Today.Month, 1);
        return $"CompanyRegistrationNumber,CompanyLegalName,CutoverDate,SourceSystem,Status,Notes\n{registration},,{cutover:yyyy-MM-dd},SAP,Active,Wave 1\n";
    }

    internal static string LoanCsv(Kit.Borrower b, string number, int paid, decimal outstanding, DateOnly firstUnpaid,
        decimal original = 600m, decimal instalment = 100m, int total = 6) =>
        "EmployeeCode,LoanNumber,LoanTypeCode,LoanTypeName,OriginalAmount,InstallmentAmount,TotalInstallments,InstallmentsPaid,OutstandingBalance,FirstUnpaidDueDate,DisbursementDate,Currency,SourceSystem,SourceRecordId\n" +
        $"{b.EmployeeCode},{number},LEGACY,Legacy loan,{original},{instalment},{total},{paid},{outstanding},{firstUnpaid:yyyy-MM-dd},2025-01-01,SAR,SAP,LN-1\n";

    internal static string AdvanceCsv(Kit.Borrower b, string number, int paid, decimal outstanding, DateOnly firstUnpaid,
        decimal original = 600m, decimal instalment = 100m, int total = 6) =>
        "EmployeeCode,AdvanceNumber,OriginalAmount,InstallmentAmount,TotalInstallments,InstallmentsPaid,OutstandingBalance,FirstUnpaidDueDate,Currency,SourceSystem,SourceRecordId\n" +
        $"{b.EmployeeCode},{number},{original},{instalment},{total},{paid},{outstanding},{firstUnpaid:yyyy-MM-dd},SAR,SAP,ADV-1\n";

    /// <summary>A loan type the package's loan rows name (LEGACY), as HR would have set it up.</summary>
    internal static async Task EnsureLoanTypeAsync(PostgresFixture fx, Kit.Borrower b, string code = "LEGACY")
    {
        await using var db = fx.CreateDb();
        if (await db.LoanTypes.AnyAsync(t => t.TenantId == b.Tenant && t.Code == code)) return;
        db.LoanTypes.Add(new LoanType { TenantId = b.Tenant, Code = code, NameEn = "Legacy loan", IsActive = true, MaxAmount = 0m, MaxInstallments = 12 });
        await db.SaveChangesAsync();
    }

    /// <summary>A second employee of the borrower's company. Returns (id, code).</summary>
    internal static async Task<(int Id, string Code)> AddColleagueAsync(PostgresFixture fx, Kit.Borrower b)
    {
        await using var db = fx.CreateDb();
        var e = new Employee
        {
            TenantId = b.Tenant, CompanyId = b.CompanyId, EmployeeCode = $"COL-{Guid.NewGuid():N}"[..14], FullName = "Colleague",
            Nationality = "Saudi", Status = "Active", JoiningDate = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        };
        db.Employees.Add(e);
        await db.SaveChangesAsync();
        return (e.Id, e.EmployeeCode);
    }

    /// <summary>Links the borrower's employee record to the user <paramref name="userId"/> (their own login).</summary>
    internal static async Task LinkBorrowerToUserAsync(PostgresFixture fx, Kit.Borrower b, Guid userId)
    {
        await using var db = fx.CreateDb();
        await db.Employees.Where(e => e.Id == b.EmployeeId).ExecuteUpdateAsync(x => x.SetProperty(e => e.UserAccountId, userId));
    }

    /// <summary>Links the borrower to a login ONLY through an employee_user_accounts row (Employee.UserAccountId stays empty).</summary>
    internal static async Task LinkBorrowerByAccountRowOnlyAsync(PostgresFixture fx, Kit.Borrower b, Guid userId)
    {
        await using var db = fx.CreateDb();
        var tag = userId.ToString("N")[..12];
        db.Users.Add(new Zayra.Api.Domain.Entities.User
        {
            Id = userId, TenantId = b.Tenant, Email = $"link-{tag}@kynexone.local", NormalizedEmail = $"LINK-{tag}@KYNEXONE.LOCAL",
            FullName = "Linked Importer", PasswordHash = "x", IsActive = true, Status = "Active", AccessMode = AccessModes.FullPortal,
        });
        db.EmployeeUserAccounts.Add(new EmployeeUserAccount
        {
            TenantId = b.Tenant, EmployeeId = b.EmployeeId, UserId = userId,
            AccessMode = AccessModes.FullPortal, Status = "Active", RequiresPasswordSetup = false,
        });
        await db.SaveChangesAsync();
        await db.Employees.Where(e => e.Id == b.EmployeeId).ExecuteUpdateAsync(x => x.SetProperty(e => e.UserAccountId, (Guid?)null));
    }

    internal static async Task<EmployeeLoan> LoanAsync(PostgresFixture fx, Kit.Borrower b, string number)
    {
        await using var db = fx.CreateDb();
        return await db.EmployeeLoans.AsNoTracking().SingleAsync(l => l.TenantId == b.Tenant && l.LoanNumber == number);
    }

    internal static async Task<SalaryAdvance> AdvanceAsync(PostgresFixture fx, Kit.Borrower b, string number)
    {
        await using var db = fx.CreateDb();
        return await db.SalaryAdvances.AsNoTracking().SingleAsync(a => a.TenantId == b.Tenant && a.AdvanceNumber == number);
    }

    internal static async Task<List<FinanceGlEntry>> OpeningJournalAsync(PostgresFixture fx, Kit.Borrower b, Guid entityId)
    {
        await using var db = fx.CreateDb();
        return await db.FinanceGlEntries.AsNoTracking()
            .Where(g => g.TenantId == b.Tenant && g.SourceEntityId == entityId && g.EventType == OpeningBalanceEvent && !g.IsReversed)
            .OrderBy(g => g.CreatedAtUtc).ToListAsync();
    }

    internal static async Task ClosePeriodAsync(PostgresFixture fx, Kit.Borrower b, DateOnly inPeriod)
    {
        await using var db = fx.CreateDb();
        db.GlPeriodCloses.Add(new GlPeriodClose
        {
            TenantId = b.Tenant, CompanyId = b.CompanyId, Period = $"{inPeriod:yyyy-MM}", Status = GlPeriodStatuses.Closed,
            ClosedAtUtc = DateTime.UtcNow, ClosedByName = "Controller",
        });
        await db.SaveChangesAsync();
    }

    /// <summary>Permission keys a role's seeded bundle carries, as far as the import cares.</summary>
    private static IEnumerable<string> BundleOf(string role) => role switch
    {
        "Admin" => new[] { "finance.opening_balances.import", "employees.bulk_import" },
        "Finance" or "Finance Approver" or "Finance Controller" => new[] { "finance.opening_balances.import" },
        // AuthSeeder: HR Manager holds every employees.* key, employees.bulk_import among them; never the finance import key.
        "HR Manager" => new[] { "employees.bulk_import" },
        _ => Array.Empty<string>(),
    };

    internal static MigrationImportController Migration(ZayraDbContext db, Guid tid, params string[] roles) => Migration(db, tid, roles, null, null);

    /// <param name="denied">Keys removed from the caller's permissions, as a per-user Deny override removes them.</param>
    internal static MigrationImportController Migration(ZayraDbContext db, Guid tid, string[] roles, Guid? userId, string[]? denied) =>
        Migration(db, Principal(tid, roles, userId, denied));

    internal static MigrationImportController Migration(ZayraDbContext db, ClaimsPrincipal user) =>
        new(db, new Pbkdf2PasswordHasher(), new AuditService(db))
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = user } },
        };

    /// <summary>The importer: the roles, their seeded bundles less any <paramref name="denied"/> key, and, when
    /// <paramref name="companyScope"/> is set, access to that one company only (otherwise no scope claim, as before).</summary>
    internal static ClaimsPrincipal Principal(Guid tid, string[] roles, Guid? userId = null, string[]? denied = null, Guid? companyScope = null)
    {
        roles = roles.Length == 0 ? new[] { "Admin" } : roles;
        var permissions = new[] { "payroll.read", "payroll.write" }.Concat(roles.SelectMany(BundleOf)).Distinct()
            .Except(denied ?? Array.Empty<string>());
        var claims = new[]
            {
                new Claim("tenant_id", tid.ToString()), new Claim(ClaimTypes.NameIdentifier, (userId ?? Guid.NewGuid()).ToString()),
                new Claim(ClaimTypes.Name, "consultant"),
            }.Concat(roles.Select(r => new Claim(ClaimTypes.Role, r)))
             .Concat(permissions.Select(p => new Claim("permission", p)))
             .ToList();
        if (companyScope is Guid company)
            claims.Add(new Claim("entity_access", System.Text.Json.JsonSerializer.Serialize(new { c = company, r = roles[0] })));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));
    }

    /// <summary>A context whose query filters apply <paramref name="user"/>'s company scope, as a request's context does.</summary>
    internal static ZayraDbContext ScopedDb(PostgresFixture fx, ClaimsPrincipal user) =>
        fx.CreateDbWithAccessor(new HttpContextAccessor { HttpContext = new DefaultHttpContext { User = user } });
}
