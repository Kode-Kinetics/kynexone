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
        bool withCutover = false, bool expectErrors = false, bool dryRun = false, params string[] roles)
    {
        var sections = new Dictionary<string, string> { [section] = csv };
        if (withCutover) sections["companyCutover"] = await CutoverCsvAsync(fx, b);
        await using var db = fx.CreateDb();
        var controller = Migration(db, b.Tenant, roles.Length == 0 ? new[] { "Admin" } : roles);
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

    internal static MigrationImportController Migration(ZayraDbContext db, Guid tid, params string[] roles) =>
        new(db, new Pbkdf2PasswordHasher(), new AuditService(db))
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(new[]
                    {
                        new Claim("tenant_id", tid.ToString()), new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
                        new Claim(ClaimTypes.Name, "consultant"),
                        new Claim("permission", "payroll.read"), new Claim("permission", "payroll.write"),
                    }.Concat((roles.Length == 0 ? new[] { "Admin" } : roles).Select(r => new Claim(ClaimTypes.Role, r))), "test")),
                },
            },
        };
}
