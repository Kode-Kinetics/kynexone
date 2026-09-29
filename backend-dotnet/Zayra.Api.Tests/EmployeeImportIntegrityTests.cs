using System.Globalization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Controllers;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Models;
using Xunit;

namespace Zayra.Api.Tests;

/// <summary>
/// Data-integrity invariants for the employee CSV import, all asserted against PERSISTED state.
///
/// <para>1. ATOMICITY. The import used to commit inside its Pass-1 loop: the auto-code generator ended in an
/// unconditional SaveChanges, so every employee staged so far was flushed and committed, row by row, outside
/// any transaction. A file that failed on a later row therefore left the earlier people in the database while
/// the response told the operator that the import "could not be saved… please review the file and retry" —
/// and the retry produced duplicates. Worse, a failure after the first of the FOUR persistence boundaries left
/// committed employees with no EmployeeImportGap rows at all, which is the whole review surface for a file.</para>
///
/// <para>2. NO INVENTED JOINING DATE. An unreadable JoiningDate was coerced to TODAY without the parse result
/// ever being inspected — including for the CSV template's own "YYYY-MM-DD" placeholder. Joining date drives
/// the salary structure's effective date, the reporting line, probation and every end-of-service accrual, so
/// that is a statutory figure invented from nothing.</para>
///
/// <para>3. CULTURE-INDEPENDENT PARSING. Date and number cells were read with the AMBIENT culture while the
/// amounts actually persisted were read with InvariantCulture, so "03/04/2025" meant March 4 or April 3
/// depending on the container's locale.</para>
/// </summary>
public class EmployeeImportIntegrityTests
{
    private const string ImportCountry = "IN";

    private static ZayraDbContext CreateInMemoryDb() =>
        new(new DbContextOptionsBuilder<ZayraDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    /// <summary>Atomicity can only be asserted on a provider that HAS transactions.</summary>
    private static (ZayraDbContext db, SqliteConnection conn) CreateSqliteDb()
    {
        var conn = new SqliteConnection("DataSource=:memory:");
        conn.Open();
        var db = new ZayraDbContext(new DbContextOptionsBuilder<ZayraDbContext>().UseSqlite(conn).Options);
        db.Database.EnsureCreated();
        return (db, conn);
    }

    private static async Task<Guid> SeedTenant(ZayraDbContext db)
    {
        var id = Guid.NewGuid();
        db.Tenants.Add(new Tenant { Id = id, Name = "Zayra", Slug = $"z-{id:N}" });
        db.TenantSubscriptions.Add(new TenantSubscription { TenantId = id, MaxEmployees = 1000, Plan = "Enterprise", Status = "Active" });
        db.CompanyComplianceProfiles.Add(new CompanyComplianceProfile
        {
            TenantId = id,
            CompanyId = null,
            CountryCode = ImportCountry,
            Jurisdiction = string.Empty,
            CompliancePack = string.Empty,
            EffectiveFrom = new DateOnly(2020, 1, 1),
            Status = CompanyPolicyStatuses.Active,
            RequiredFieldsJson = """[{"key":"FullName","category":"personal","failClosed":true}]""",
        });
        await db.SaveChangesAsync();
        return id;
    }

    private static EmployeesController Controller(ZayraDbContext db, Guid tenantId) =>
        HrmHierarchyTests.BuildImportControllerInternal(db, tenantId);

    private static Task<IActionResult> Import(EmployeesController ctrl, string csv) =>
        ctrl.Import(new EmployeesController.ImportEmployeesRequest(csv), CancellationToken.None);

    // ── (a) ALL-OR-NOTHING ──────────────────────────────────────────────────────────────────────────
    // A save that fails AFTER the employees were written (here: the payroll-profile save) must take the
    // employees with it. On main every persistence boundary committed on its own, so the 422 below was returned
    // with the people already in the database — and the retry duplicated them.
    private sealed class FailWhenSaving<T> : Microsoft.EntityFrameworkCore.Diagnostics.SaveChangesInterceptor where T : class
    {
        public override ValueTask<Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<int>> SavingChangesAsync(
            Microsoft.EntityFrameworkCore.Diagnostics.DbContextEventData eventData,
            Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<int> result, CancellationToken ct = default)
        {
            if (eventData.Context!.ChangeTracker.Entries<T>().Any(e => e.State == EntityState.Added))
                throw new DbUpdateException("Injected failure while saving " + typeof(T).Name);
            return base.SavingChangesAsync(eventData, result, ct);
        }
    }

    [Fact]
    public async Task Import_PersistsNothing_WhenALaterSaveFails()
    {
        var conn = new SqliteConnection("DataSource=:memory:");
        conn.Open();
        using var __ = conn;
        Guid tenantId;
        await using (var seed = new ZayraDbContext(new DbContextOptionsBuilder<ZayraDbContext>().UseSqlite(conn).Options))
        {
            seed.Database.EnsureCreated();
            tenantId = await SeedTenant(seed);
            seed.EmployeeIdRules.Add(new EmployeeIdRule
            {
                TenantId = tenantId, CompanyPrefix = "EMP", UseYear = false, UseCountryPrefix = false,
                UseDepartmentPrefix = false, PaddingLength = 4, NextSequence = 1, IsActive = true,
            });
            await seed.SaveChangesAsync();
        }

        await using var db = new ZayraDbContext(new DbContextOptionsBuilder<ZayraDbContext>().UseSqlite(conn)
            .AddInterceptors(new FailWhenSaving<EmployeePayrollProfile>()).Options);
        var csv =
            "FullName,CountryCode,JoiningDate,BankName\n" +
            $"Alice,{ImportCountry},2024-01-01,Bank A\n" +
            $"Bob,{ImportCountry},2024-01-02,Bank B\n";

        var result = await Import(Controller(db, tenantId), csv);

        var refused = Assert.IsType<UnprocessableEntityObjectResult>(result);
        var body = System.Text.Json.JsonSerializer.SerializeToElement(refused.Value);
        Assert.Equal("payroll", body.GetProperty("stage").GetString());
        Assert.Equal(0, body.GetProperty("created").GetInt32());
        Assert.Equal(2, body.GetProperty("failed").GetInt32());
        db.ChangeTracker.Clear();
        Assert.Empty(await db.Employees.IgnoreQueryFilters().Where(e => e.TenantId == tenantId).ToListAsync());
        Assert.Empty(await db.EmployeeImportGaps.IgnoreQueryFilters().Where(g => g.TenantId == tenantId).ToListAsync());
        // The generated-code sequence rolls back with everything else, or the retry skips codes forever.
        var rule = await db.EmployeeIdRules.IgnoreQueryFilters().SingleAsync(r => r.TenantId == tenantId);
        Assert.Equal(1, rule.NextSequence);
    }

    // ── (a2) GENERATED CODES SKIP EVERY TAKEN CODE ──────────────────────────────────────────────────
    // The sequence is at 1, but EMP-0002 is taken by an existing employee, EMP-0003 by an explicit code in
    // the SAME file, and EMP-0004 by a soft-deleted employee (the unique index still holds it). On main the
    // blank-code rows were handed EMP-0001, EMP-0002, EMP-0003 blindly and the whole file failed on the
    // unique index. Now they get the next FREE codes, and the file imports.
    [Fact]
    public async Task GeneratedCodes_SkipCodesTakenInTheTenantOrInTheSameFile()
    {
        var (db, conn) = CreateSqliteDb();
        await using var _ = db;
        using var __ = conn;
        var tenantId = await SeedTenant(db);
        db.EmployeeIdRules.Add(new EmployeeIdRule
        {
            TenantId = tenantId, CompanyPrefix = "EMP", UseYear = false, UseCountryPrefix = false,
            UseDepartmentPrefix = false, PaddingLength = 4, NextSequence = 1, IsActive = true,
        });
        db.Employees.Add(new Employee
        {
            TenantId = tenantId, EmployeeCode = "EMP-0002", FullName = "Already Here", EnglishName = "Already Here",
            CountryCode = ImportCountry, Status = EmployeeStatuses.Draft, JoiningDate = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        });
        db.Employees.Add(new Employee
        {
            TenantId = tenantId, EmployeeCode = "EMP-0004", FullName = "Deleted Once", EnglishName = "Deleted Once",
            CountryCode = ImportCountry, Status = "Inactive", IsDeleted = true, JoiningDate = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var csv =
            "EmployeeCode,FullName,CountryCode,JoiningDate\n" +
            $",Alice,{ImportCountry},2024-01-01\n" +
            $"EMP-0003,Bob,{ImportCountry},2024-01-02\n" +
            $",Carol,{ImportCountry},2024-01-03\n" +
            $",Dan,{ImportCountry},2024-01-04\n";

        var ok = Assert.IsType<OkObjectResult>(await Import(Controller(db, tenantId), csv));
        Assert.Equal(4, System.Text.Json.JsonSerializer.SerializeToElement(ok.Value).GetProperty("created").GetInt32());

        db.ChangeTracker.Clear();
        var codes = await db.Employees.IgnoreQueryFilters()
            .Where(e => e.TenantId == tenantId && !e.IsDeleted)
            .ToDictionaryAsync(e => e.FullName, e => e.EmployeeCode);
        Assert.Equal("EMP-0001", codes["Alice"]);
        Assert.Equal("EMP-0003", codes["Bob"]);
        Assert.Equal("EMP-0005", codes["Carol"]);   // 0002 existing, 0003 in the file, 0004 soft-deleted
        Assert.Equal("EMP-0006", codes["Dan"]);
        var rule = await db.EmployeeIdRules.IgnoreQueryFilters().SingleAsync(r => r.TenantId == tenantId);
        Assert.Equal(7, rule.NextSequence);
    }

    // ── (b) AN UNREADABLE JOINING DATE IS FLAGGED, NOT GUESSED ──────────────────────────────────────
    [Fact]
    public async Task Import_UnreadableJoiningDate_IsNotTodayAndRecordsAGap()
    {
        await using var db = CreateInMemoryDb();
        var tenantId = await SeedTenant(db);

        // The placeholder the product's own CSV template ships in the JoiningDate column.
        var csv =
            "FullName,CountryCode,JoiningDate\n" +
            $"Alice Unparsed,{ImportCountry},YYYY-MM-DD\n";

        var result = await Import(Controller(db, tenantId), csv);
        Assert.IsType<OkObjectResult>(result); // accept-never-block: the person still imports

        var employee = await db.Employees.SingleAsync(e => e.TenantId == tenantId && e.FullName == "Alice Unparsed");
        Assert.NotEqual(DateTime.UtcNow.Date, employee.JoiningDate.Date); // NOT silently today
        Assert.Equal(default, employee.JoiningDate);                      // left unset, so readiness reports it missing

        var gap = await db.EmployeeImportGaps
            .SingleAsync(g => g.TenantId == tenantId && g.EmployeeId == employee.Id && g.GapType == "data:unparsedDate");
        Assert.Equal("YYYY-MM-DD", gap.RawValue); // the operator gets the raw cell back
        Assert.Contains("JoiningDate", gap.Detail, StringComparison.Ordinal);
    }

    // ── (b2) …and nothing is derived from it: an Active row lands Draft, BLOCKED on the joining date, with no
    // salary structure dated year 1 and no reporting line starting in year 1. On main the row landed Active
    // with JoiningDate = today; with the unknown-date fix alone it would have landed Active with year-1 dates.
    [Fact]
    public async Task Import_UnreadableJoiningDate_ForcesDraft_BlocksActivation_AndDerivesNoDates()
    {
        await using var db = CreateInMemoryDb();
        var tenantId = await SeedTenant(db);
        db.Grades.Add(new Grade { TenantId = tenantId, Code = "G1", Name = "Grade 1", MinSalary = 0, MaxSalary = 100000, Currency = "SAR", IsActive = true });
        await db.SaveChangesAsync();

        var csv =
            "EmployeeCode,FullName,CountryCode,JoiningDate,Status,Grade,BasicSalary,ManagerEmployeeCode,BankName\n" +
            $"M1,Manager Known,{ImportCountry},2023-05-01,Active,G1,9000,,Bank M\n" +
            $"E1,Alice Unparsed,{ImportCountry},31/31/2024,Active,G1,5000,M1,Bank E\n";

        var ok = Assert.IsType<OkObjectResult>(await Import(Controller(db, tenantId), csv));
        var body = System.Text.Json.JsonSerializer.SerializeToElement(ok.Value);
        Assert.Equal(2, body.GetProperty("created").GetInt32());

        var alice = await db.Employees.SingleAsync(e => e.TenantId == tenantId && e.EmployeeCode == "E1");
        Assert.Equal(default, alice.JoiningDate);
        Assert.Equal(EmployeeStatuses.Draft, alice.Status);            // asked for Active; forced Draft
        Assert.Equal("Blocked", alice.ReadinessState);
        Assert.True(alice.ActivationBlockersCount >= 1);
        // No salary structure: its effective date IS the joining date. The profile (bank details) still lands.
        Assert.Empty(await db.EmployeeSalaryStructures.Where(s => s.EmployeeId == alice.Id).ToListAsync());
        Assert.Contains(await db.EmployeeImportGaps.Where(g => g.EmployeeId == alice.Id).Select(g => g.GapType).ToListAsync(),
            t => t == "pay:salaryReview");
        Assert.Equal("Bank E", (await db.EmployeePayrollProfiles.SingleAsync(p => p.EmployeeId == alice.Id)).BankName);
        // The manager link is kept, but it starts on the day it was recorded — never year 1.
        var line = await db.ReportingLines.SingleAsync(l => l.EmployeeId == alice.Id);
        Assert.True(line.EffectiveFrom.Year >= 2026, $"reporting line derived from an unknown joining date: {line.EffectiveFrom:O}");
        // The known row is untouched by any of this.
        var manager = await db.Employees.SingleAsync(e => e.TenantId == tenantId && e.EmployeeCode == "M1");
        Assert.Equal(EmployeeStatuses.Active, manager.Status);
        Assert.Equal(new DateOnly(2023, 5, 1),
            (await db.EmployeeSalaryStructures.SingleAsync(s => s.EmployeeId == manager.Id)).EffectiveDate);

        // And the activation gate refuses it until someone states the real date.
        var activate = await new Zayra.Api.Infrastructure.Employees.EmployeeActivationGuard(db)
            .EvaluateEmployeeAsync(tenantId, alice.Id, CancellationToken.None);
        Assert.Contains(activate!.Value.Readiness.Blocking, i => i.Key == "JoiningDate");
    }

    // ── (c) THE SAME CELL MEANS THE SAME DAY IN EVERY LOCALE ────────────────────────────────────────
    [Fact]
    public async Task Import_ParsesDatesInvariantly_UnderANonInvariantCurrentCulture()
    {
        var previousCulture = CultureInfo.CurrentCulture;
        // en-GB reads "03/04/2025" as day-first (3 April); the invariant format the CSV is written in reads
        // it month-first (4 March). Before the fix the container's locale decided which one a customer got.
        CultureInfo.CurrentCulture = new CultureInfo("en-GB");
        try
        {
            Assert.Equal(new DateOnly(2025, 4, 3), DateOnly.Parse("03/04/2025", CultureInfo.CurrentCulture));

            await using var db = CreateInMemoryDb();
            var tenantId = await SeedTenant(db);

            var csv =
                "FullName,CountryCode,JoiningDate,PassportExpiryDate\n" +
                $"Nadia Locale,{ImportCountry},03/04/2025,03/04/2025\n";

            var result = await Import(Controller(db, tenantId), csv);
            Assert.IsType<OkObjectResult>(result);

            var employee = await db.Employees.SingleAsync(e => e.TenantId == tenantId && e.FullName == "Nadia Locale");
            Assert.Equal(new DateOnly(2025, 3, 4), employee.PassportExpiryDate);
            Assert.Equal(new DateOnly(2025, 3, 4), DateOnly.FromDateTime(employee.JoiningDate));

            // A value that DID parse must not be reported as a gap.
            Assert.Empty(await db.EmployeeImportGaps
                .Where(g => g.TenantId == tenantId && g.GapType == "data:unparsedDate").ToListAsync());
        }
        finally
        {
            CultureInfo.CurrentCulture = previousCulture;
        }
    }

    // ── (c2) An unreadable EXPIRY no longer disappears without a trace ──────────────────────────────
    [Fact]
    public async Task Import_UnreadableExpiryDate_RecordsAGapInsteadOfSilentlyBecomingNull()
    {
        await using var db = CreateInMemoryDb();
        var tenantId = await SeedTenant(db);

        var csv =
            "FullName,CountryCode,JoiningDate,PassportExpiryDate\n" +
            $"Omar Expiry,{ImportCountry},2024-01-01,31-13-2025\n";

        Assert.IsType<OkObjectResult>(await Import(Controller(db, tenantId), csv));

        var employee = await db.Employees.SingleAsync(e => e.TenantId == tenantId && e.FullName == "Omar Expiry");
        Assert.Null(employee.PassportExpiryDate);
        var gap = await db.EmployeeImportGaps
            .SingleAsync(g => g.TenantId == tenantId && g.EmployeeId == employee.Id && g.GapType == "data:unparsedDate");
        Assert.Equal("31-13-2025", gap.RawValue);
    }
}
