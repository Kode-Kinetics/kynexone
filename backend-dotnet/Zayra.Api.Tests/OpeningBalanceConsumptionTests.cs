using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Common;
using Zayra.Api.Application.CountryPack;
using Zayra.Api.Controllers;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.Audit;
using Zayra.Api.Infrastructure.Auth;
using Zayra.Api.Infrastructure.CountryPack;
using Zayra.Api.Infrastructure.CountryPack.Ksa;
using Zayra.Api.Infrastructure.Payroll;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// FOUR CARRIED-IN FIGURES THAT NOTHING READ BACK, and the wrong numbers they produced.
///
/// <para>The migration importer validates, persists and provenance-stamps an opening balance, and the
/// product then computes as though it were never there. Each test below pins the WRONG figure the
/// product produced before the fix beside the right one, because "the carried service is now honoured"
/// is not a claim anybody can check and "SAR 6,492.58 where SAR 110,485.16 was owed" is.</para>
///
/// <list type="number">
/// <item>M1 — <c>PriorServiceStartDate</c>: a migrated leaver settled on their KynexOne tenure alone.</item>
/// <item>M2 — <c>AccruedAmount</c>: the carried 2310 provision invisible, so the gratuity expensed twice.</item>
/// <item>MI1 — a payslip-aggregate bucket carried twice, doubling the year-to-date on every payslip.</item>
/// <item>M3 — an unquoted thousands separator shifting every later column on every sheet.</item>
/// </list>
/// </summary>
public class OpeningBalanceConsumptionTests
{
    private static readonly Guid Actor = Guid.Parse("33333333-3333-3333-3333-333333333333");

    // ══ Harness ═════════════════════════════════════════════════════════════════
    //
    // SQLite rather than InMemory because the importer and the provision ledger both run real
    // relational queries (GroupBy over a projection, IgnoreQueryFilters joins) that InMemory answers
    // more generously than a database does.

    private static (ZayraDbContext db, SqliteConnection conn) NewDb()
    {
        var conn = new SqliteConnection("DataSource=:memory:");
        conn.Open();
        var db = new ZayraDbContext(new DbContextOptionsBuilder<ZayraDbContext>().UseSqlite(conn).Options);
        db.Database.EnsureCreated();
        return (db, conn);
    }

    private static ClaimsPrincipal Principal(Guid tenantId) =>
        new(new ClaimsIdentity(new List<Claim>
        {
            new("tenant_id", tenantId.ToString()),
            new(ClaimTypes.NameIdentifier, Actor.ToString()),
            new(ClaimTypes.Name, "consultant"),
            new(ClaimTypes.Role, "Admin"),
            new("permission", "payroll.read"),
            new("permission", "payroll.write"),
        }, "test"));

    private static PayrollController Payroll(ZayraDbContext db, Guid tenantId)
    {
        var httpCtx = new DefaultHttpContext { User = Principal(tenantId) };
        var rules = new StubRuleReader();
        var ctrl = new PayrollController(
            db, new _ObcScope(), new _ObcHttp(httpCtx), new _ObcNotifications(),
            new _ObcKsaResolver(rules), rules, new _ObcLetters(), new _ObcDocs(),
            new Zayra.Api.Infrastructure.Documents.PdfRenderGate(1));
        ctrl.ControllerContext = new ControllerContext { HttpContext = httpCtx };
        return ctrl;
    }

    private static MigrationImportController Migration(ZayraDbContext db, Guid tenantId)
    {
        var ctrl = new MigrationImportController(db, new Pbkdf2PasswordHasher(), new AuditService(db));
        ctrl.ControllerContext = new ControllerContext
        { HttpContext = new DefaultHttpContext { User = Principal(tenantId) } };
        return ctrl;
    }

    /// <summary>
    /// A KSA leaver whose KynexOne record starts at the September 2026 cutover: basic 10,000 +
    /// housing 2,000 + transport 1,000. The Art. 84 last wage is 13,000 (transport is in by the pack's
    /// [COUNSEL] default) and the tenant has no pay-component catalog, so the pack's statutory base is
    /// what the award is measured on.
    /// </summary>
    private static async Task<(Company Company, Employee Employee)> SeedKsaLeaver(
        ZayraDbContext db, Guid tenantId, DateTime joiningDate)
    {
        var company = new Company
        {
            TenantId = tenantId, LegalNameEn = "Carried Service KSA Co", RegistrationNumber = "1010101010",
            CountryCode = "SAU", Jurisdiction = "KSA-mainland", DefaultCurrency = "SAR", IsActive = true,
        };
        db.Companies.Add(company);
        await db.SaveChangesAsync();

        var emp = new Employee
        {
            TenantId = tenantId, CompanyId = company.Id, EmployeeCode = "EMP-001",
            FullName = "Carried Service Leaver", Status = "Active", Nationality = "SAU",
            ContractType = "Indefinite", JoiningDate = joiningDate, WorkEmail = "emp-001@carried.test",
        };
        db.Employees.Add(emp);
        db.GCCComplianceSettings.Add(new GCCComplianceSetting
        {
            TenantId = tenantId, CountryCode = "SAU", EosbEnabled = true,
        });
        await db.SaveChangesAsync();

        db.EmployeeSalaryStructures.Add(new EmployeeSalaryStructure
        {
            TenantId = tenantId, EmployeeId = emp.Id,
            BasicSalary = 10_000m, HousingAllowance = 2_000m, TransportAllowance = 1_000m,
            Currency = "SAR", EffectiveDate = DateOnly.FromDateTime(joiningDate), IsActive = true,
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return (company, emp);
    }

    private const string CutoverCsv =
        "CompanyRegistrationNumber,CompanyLegalName,CutoverDate,SourceSystem,Status,Notes\n" +
        "1010101010,,2026-09-01,SAP,Active,Wave 1\n";

    private static MigrationPackageRequest Package(string externalId, params (string Section, string Csv)[] sections) =>
        new(externalId, sections.ToDictionary(s => s.Section, s => s.Csv), false);

    private static decimal Eosb(IActionResult r) =>
        (decimal)((OkObjectResult)r).Value!.GetType().GetProperty("eosbAmount")!.GetValue(((OkObjectResult)r).Value)!;

    private static string Notices(IActionResult r)
    {
        var value = ((OkObjectResult)r).Value!;
        var notices = (System.Collections.IEnumerable)value.GetType().GetProperty("statutoryNotices")!.GetValue(value)!;
        return string.Join(" | ", notices.Cast<object>().Select(o => o?.ToString()));
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  M1 — the carried service start, and the settlement that was short without it
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// THE DEFECT, IN MONEY. An employee who joined the previous employer on 2016-09-01 is migrated at
    /// the September 2026 cutover, so their KynexOne <c>JoiningDate</c> is 2026-09-01. They leave on
    /// 2027-08-31 with eleven years of service.
    ///
    /// <para>The importer carries <c>PriorServiceStartDate=2016-09-01</c> and always has. Nothing read
    /// it, so the award was measured from 2026-09-01 — 0.9989 years — and came to
    /// <b>SAR 6,492.58</b> against the <b>SAR 110,485.16</b> Art. 84 actually requires: the leaver was
    /// short by SAR 103,992.58, and the shortfall grows with every year of carried service.</para>
    ///
    /// <para>Art. 84 on the 13,000 last wage: five years at half a month (5 × 0.5 × 13,000 = 32,500)
    /// plus 5.9989 years at a full month (77,985.16).</para>
    /// </summary>
    [Fact]
    public async Task CarriedPriorService_IsHonouredByTheGratuity_NotSilentlyDropped()
    {
        var (db, conn) = NewDb();
        await using var _ = conn; await using var __ = db;
        var tid = Guid.NewGuid();
        await SeedKsaLeaver(db, tid, new DateTime(2026, 9, 1));
        var lastDay = new DateTime(2027, 8, 31);

        // ── Before the carried row exists: measured on KynexOne tenure alone. This is EXACTLY the
        //    figure the product produced for a migrated leaver before this fix.
        var emp = await db.Employees.AsNoTracking().SingleAsync(e => e.TenantId == tid);
        var bare = await Payroll(db, tid).CalculateEosb(
            new EosbCalculationRequest(emp.Id, lastDay, "Termination"), CancellationToken.None);
        Eosb(bare).Should().Be(6_492.58m,
            "without the carried service the award is measured from the 2026-09-01 KynexOne joining date");
        db.ChangeTracker.Clear();

        // ── The consultant carries the real service start in, exactly as the shipped template invites.
        var commit = await Migration(db, tid).Commit(Package("carried-service",
            ("companyCutover", CutoverCsv),
            ("eosbOpeningProvision",
                "EmployeeCode,AsAtDate,AccruedMonths,AccruedAmount,PriorServiceStartDate,Currency,SourceSystem,SourceRecordId\n" +
                "EMP-001,2026-08-31,120,52500,2016-09-01,SAR,SAP,EOSB-001\n")), CancellationToken.None);
        ((MigrationReconciliationDto)((OkObjectResult)commit.Result!).Value!).Errors.Should().BeEmpty();
        db.ChangeTracker.Clear();

        var settled = await Payroll(db, tid).CalculateEosb(
            new EosbCalculationRequest(emp.Id, lastDay, "Termination"), CancellationToken.None);

        Eosb(settled).Should().Be(110_485.16m,
            "Art. 84 measures eleven years of service: 5 x half a month + 5.9989 x a full month of the 13,000 last wage");
        (Eosb(settled) - Eosb(bare)).Should().Be(103_992.58m,
            "this is what every migrated leaver was short by, per eleven years of carried service");

        // The substitution is announced, not applied silently — an approver must be able to see that the
        // award was measured from a date that is not on the employee record.
        Notices(settled).Should().Contain("[MIGRATION]").And.Contain("2016-09-01").And.Contain("2026-09-01");
    }

    /// <summary>
    /// THE OTHER DIRECTION, PINNED. A carried date LATER than the joining date — a stale or mis-keyed
    /// row — must not SHORTEN the award. Nor may the two dates be added together: they describe one
    /// continuous period, and summing them would be the double count the fix exists to avoid.
    /// </summary>
    [Fact]
    public async Task CarriedPriorService_NeverShortensService_AndIsNeverAddedToIt()
    {
        var (db, conn) = NewDb();
        await using var _ = conn; await using var __ = db;
        var tid = Guid.NewGuid();
        await SeedKsaLeaver(db, tid, new DateTime(2016, 9, 1));   // joined HERE eleven years ago
        var lastDay = new DateTime(2027, 8, 31);
        var emp = await db.Employees.AsNoTracking().SingleAsync(e => e.TenantId == tid);

        var before = await Payroll(db, tid).CalculateEosb(
            new EosbCalculationRequest(emp.Id, lastDay, "Termination"), CancellationToken.None);
        Eosb(before).Should().Be(110_485.16m);
        db.ChangeTracker.Clear();

        // A carried row claiming service from 2021 — later than the joining date already on record.
        var commit = await Migration(db, tid).Commit(Package("late-carried",
            ("companyCutover", CutoverCsv),
            ("eosbOpeningProvision",
                "EmployeeCode,AsAtDate,AccruedMonths,AccruedAmount,PriorServiceStartDate,Currency,SourceSystem,SourceRecordId\n" +
                "EMP-001,2026-08-31,60,30000,2021-09-01,SAR,SAP,EOSB-001\n")), CancellationToken.None);
        ((MigrationReconciliationDto)((OkObjectResult)commit.Result!).Value!).Errors.Should().BeEmpty();
        db.ChangeTracker.Clear();

        var after = await Payroll(db, tid).CalculateEosb(
            new EosbCalculationRequest(emp.Id, lastDay, "Termination"), CancellationToken.None);

        Eosb(after).Should().Be(110_485.16m,
            "a later carried date is ignored: it can only be a stale row, and shortening a statutory entitlement on that evidence is unlawful");
        Eosb(after).Should().BeLessThan(2m * 110_485.16m,
            "and the two periods are never summed — one employee, one continuous service period");
        Notices(after).Should().NotContain("[MIGRATION]",
            "nothing was substituted, so there is nothing to announce");
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  M2 — the carried provision the ledger could not see
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// THE DOUBLE EXPENSE. <c>EmployeeEosbOpeningBalance.AccruedAmount</c> is the 2310 provision the
    /// customer brought across on their own balance sheet — the cost of those years of service,
    /// already recognised, in the system they are leaving. <c>EosbProvisionLedger</c> summed only
    /// <c>FinanceGlEntry</c> rows, so it answered "nothing has been provided for this employee" and the
    /// settlement expensed the whole gratuity to 5110 a second time.
    ///
    /// <para>The ledger is the seam the settlement's relief debit is built from, so this is asserted
    /// where the defect is: a carried 52,500 provision must be a live, consumable position on the same
    /// EOSB_PROVISION account the settlement would debit.</para>
    /// </summary>
    [Fact]
    public async Task CarriedEosbProvision_IsALivePositionInTheProvisionLedger()
    {
        var (db, conn) = NewDb();
        await using var _ = conn; await using var __ = db;
        var tid = Guid.NewGuid();
        var (company, _seed) = await SeedKsaLeaver(db, tid, new DateTime(2026, 9, 1));
        var emp = await db.Employees.AsNoTracking().SingleAsync(e => e.TenantId == tid);

        // Before the import there is nothing to relieve, and the whole gratuity is rightly expensed.
        (await EosbProvisionLedger.LoadPositionsAsync(db, tid, new[] { emp.Id }, CancellationToken.None))
            .Should().BeEmpty("no provision has been carried in or accrued");

        var commit = await Migration(db, tid).Commit(Package("carried-provision",
            ("companyCutover", CutoverCsv),
            ("eosbOpeningProvision",
                "EmployeeCode,AsAtDate,AccruedMonths,AccruedAmount,PriorServiceStartDate,Currency,SourceSystem,SourceRecordId\n" +
                "EMP-001,2026-08-31,120,52500,2016-09-01,SAR,SAP,EOSB-001\n")), CancellationToken.None);
        ((MigrationReconciliationDto)((OkObjectResult)commit.Result!).Value!).Errors.Should().BeEmpty();
        db.ChangeTracker.Clear();

        var positions = await EosbProvisionLedger.LoadPositionsAsync(db, tid, new[] { emp.Id }, CancellationToken.None);

        positions.Should().ContainSingle("the carried provision is one position for one employee");
        positions[0].EmployeeId.Should().Be(emp.Id);
        positions[0].CompanyId.Should().Be(company.Id, "the provision belongs to the legal entity that carried it");
        positions[0].Accrued.Should().Be(52_500m);
        positions[0].Consumed.Should().Be(0m);
        positions[0].Remaining.Should().Be(52_500m,
            "52,500 of this employee's gratuity has ALREADY been expensed by the outgoing system and must relieve 2310, not 5110");
        positions[0].ProvisionAccount.Should().Be("2310 - End of Service Benefit Provision",
            "the same account the settlement's relief debit resolves, so a consumption keys onto this position");

        // And a cursor will actually hand it to a settlement.
        var cursor = await EosbProvisionLedger.LoadCursorAsync(db, tid, emp.Id, CancellationToken.None);
        var (position, taken) = cursor.Take(emp.Id, company.Id, 110_485.16m);
        taken.Should().Be(52_500m, "the settlement expenses only the shortfall above what was already provided");
        position!.ProvisionAccount.Should().Be("2310 - End of Service Benefit Provision");
        cursor.Take(emp.Id, company.Id, 1m).Taken.Should().Be(0m, "a position cannot be consumed twice");
    }

    /// <summary>
    /// A RE-MIGRATION RESTATES THE PROVISION; IT DOES NOT ADD A SECOND ONE. Two carried rows for one
    /// employee struck at successive dates are one liability stated twice. Summing them would overstate
    /// the relief and under-expense the settlement — the mirror image of the defect being fixed.
    /// </summary>
    [Fact]
    public async Task TwoCarriedProvisionRows_AreARestatement_NotTwoLiabilities()
    {
        var (db, conn) = NewDb();
        await using var _ = conn; await using var __ = db;
        var tid = Guid.NewGuid();
        var (company, _seed) = await SeedKsaLeaver(db, tid, new DateTime(2026, 9, 1));
        var emp = await db.Employees.AsNoTracking().SingleAsync(e => e.TenantId == tid);

        var commit = await Migration(db, tid).Commit(Package("restated-provision",
            ("companyCutover", CutoverCsv),
            ("eosbOpeningProvision",
                "EmployeeCode,AsAtDate,AccruedMonths,AccruedAmount,PriorServiceStartDate,Currency,SourceSystem,SourceRecordId\n" +
                "EMP-001,2026-07-31,119,51000,2016-09-01,SAR,SAP,EOSB-001\n" +
                "EMP-001,2026-08-31,120,52500,2016-09-01,SAR,SAP,EOSB-002\n")), CancellationToken.None);
        ((MigrationReconciliationDto)((OkObjectResult)commit.Result!).Value!).Errors.Should().BeEmpty();
        db.ChangeTracker.Clear();

        (await db.EmployeeEosbOpeningBalances.AsNoTracking().CountAsync(x => x.TenantId == tid))
            .Should().Be(2, "both cut dates are carried — the provenance of each is a fact");

        var positions = await EosbProvisionLedger.LoadPositionsAsync(db, tid, new[] { emp.Id }, CancellationToken.None);
        positions.Should().ContainSingle();
        positions[0].Accrued.Should().Be(52_500m,
            "the most recently struck row is the provision; 51,000 + 52,500 would be one liability counted twice");
        positions[0].CompanyId.Should().Be(company.Id);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  MI1 — the payslip-aggregate double count
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// THE DOUBLED PAYSLIP. <c>PayrollOpeningBalance</c>'s upsert key includes <c>ComponentCode</c>
    /// while <c>SumOpeningBalance</c> ignores it, so a <c>YTD_GROSS/TOTAL</c> row and the per-component
    /// <c>YTD_GROSS/BASIC</c>, <c>YTD_GROSS/HOUSING</c> rows the template's own ComponentCode column
    /// invites are ALL summed into the payslip. 104,000 carried as a total plus the same 104,000 split
    /// by component reported 208,000 of year-to-date gross on every payslip for the rest of the year.
    ///
    /// <para><c>OpeningBalanceTypes.PayslipAggregates</c> has documented the rejection since it shipped
    /// — "the importer rejects the combination loudly" — and had exactly one occurrence in the whole
    /// solution: its own declaration. This is that rejection.</para>
    /// </summary>
    [Fact]
    public async Task PayslipAggregateCarriedTwice_IsRefusedByName_NotSilentlySummed()
    {
        var (db, conn) = NewDb();
        await using var _ = conn; await using var __ = db;
        var tid = Guid.NewGuid();
        await SeedKsaLeaver(db, tid, new DateTime(2023, 1, 1));

        var commit = await Migration(db, tid).Commit(Package("doubled-ytd",
            ("companyCutover", CutoverCsv),
            ("payrollOpeningBalances",
                "EmployeeCode,Year,BalanceType,ComponentCode,Amount,Currency,SourceSystem,SourceRecordId\n" +
                "EMP-001,2026,YTD_GROSS,TOTAL,104000,SAR,SAP,OB-1\n" +
                "EMP-001,2026,YTD_GROSS,BASIC,80000,SAR,SAP,OB-2\n" +
                "EMP-001,2026,YTD_GROSS,HOUSING,24000,SAR,SAP,OB-3\n")), CancellationToken.None);

        var dto = (MigrationReconciliationDto)((OkObjectResult)commit.Result!).Value!;
        db.ChangeTracker.Clear();

        // Both detail rows are refused, by row number, naming the component codes on both sides.
        dto.Errors.Should().HaveCount(2);
        dto.Errors[0].Should().StartWith("payrollOpeningBalances row 3")
            .And.Contain("TOTAL").And.Contain("BASIC").And.Contain("double");
        dto.Errors[1].Should().StartWith("payrollOpeningBalances row 4").And.Contain("HOUSING");

        // The total survives — refusing the combination must not lose the figure that is right.
        var stored = await db.PayrollOpeningBalances.AsNoTracking().Where(x => x.TenantId == tid).ToListAsync();
        stored.Should().ContainSingle();
        stored[0].ComponentCode.Should().Be("TOTAL");
        stored[0].Amount.Should().Be(104_000m,
            "the payslip's year-to-date gross is 104,000 — the 208,000 the unguarded importer stored was the defect");
    }

    /// <summary>
    /// The same refusal ACROSS imports. A consultant who carries the totals on Monday and the component
    /// split on Tuesday hits exactly the defect an in-file-only guard would wave through, because the
    /// first import is already in the database by then.
    /// </summary>
    [Fact]
    public async Task PayslipAggregate_AlreadyStoredUnderAnotherComponent_IsRefusedOnTheNextImport()
    {
        var (db, conn) = NewDb();
        await using var _ = conn; await using var __ = db;
        var tid = Guid.NewGuid();
        await SeedKsaLeaver(db, tid, new DateTime(2023, 1, 1));

        var first = await Migration(db, tid).Commit(Package("ytd-monday",
            ("companyCutover", CutoverCsv),
            ("payrollOpeningBalances",
                "EmployeeCode,Year,BalanceType,ComponentCode,Amount,Currency,SourceSystem,SourceRecordId\n" +
                "EMP-001,2026,YTD_GROSS,TOTAL,104000,SAR,SAP,OB-1\n")), CancellationToken.None);
        ((MigrationReconciliationDto)((OkObjectResult)first.Result!).Value!).Errors.Should().BeEmpty();
        db.ChangeTracker.Clear();

        var second = await Migration(db, tid).Commit(Package("ytd-tuesday",
            ("payrollOpeningBalances",
                "EmployeeCode,Year,BalanceType,ComponentCode,Amount,Currency,SourceSystem,SourceRecordId\n" +
                "EMP-001,2026,YTD_GROSS,BASIC,80000,SAR,SAP,OB-2\n")), CancellationToken.None);
        var dto = (MigrationReconciliationDto)((OkObjectResult)second.Result!).Value!;
        db.ChangeTracker.Clear();

        dto.Errors.Should().ContainSingle().Which.Should()
            .Contain("EMP-001").And.Contain("YTD_GROSS").And.Contain("TOTAL").And.Contain("BASIC");
        (await db.PayrollOpeningBalances.AsNoTracking().CountAsync(x => x.TenantId == tid)).Should().Be(1);
    }

    /// <summary>
    /// THE GUARD IS NARROW ON PURPOSE. The statutory, tax and covered-wage buckets are carried DETAIL —
    /// nothing sums them into a payslip, and a per-component split there is exactly what the annual GOSI
    /// reconciliation needs. The shipped template's own six-row example must still import cleanly, or
    /// the fix has broken the product to fix the bug.
    /// </summary>
    [Fact]
    public async Task NonPayslipBuckets_StillAcceptPerComponentDetail()
    {
        var (db, conn) = NewDb();
        await using var _ = conn; await using var __ = db;
        var tid = Guid.NewGuid();
        await SeedKsaLeaver(db, tid, new DateTime(2023, 1, 1));

        var commit = await Migration(db, tid).Commit(Package("statutory-detail",
            ("companyCutover", CutoverCsv),
            ("payrollOpeningBalances",
                "EmployeeCode,Year,BalanceType,ComponentCode,Amount,Currency,SourceSystem,SourceRecordId\n" +
                "EMP-001,2026,YTD_GROSS,TOTAL,104000,SAR,SAP,OB-1\n" +
                "EMP-001,2026,YTD_DEDUCTIONS,TOTAL,9360,SAR,SAP,OB-2\n" +
                "EMP-001,2026,YTD_NET,TOTAL,94640,SAR,SAP,OB-3\n" +
                "EMP-001,2026,YTD_STATUTORY_EE,GOSI-ANNUITIES,8100,SAR,SAP,OB-4\n" +
                "EMP-001,2026,YTD_STATUTORY_EE,GOSI-SANED,540,SAR,SAP,OB-5\n" +
                "EMP-001,2026,YTD_COVERED_WAGE,TOTAL,104000,SAR,SAP,OB-6\n")), CancellationToken.None);

        var dto = (MigrationReconciliationDto)((OkObjectResult)commit.Result!).Value!;
        dto.Errors.Should().BeEmpty("statutory detail split by component is the point of those buckets");
        db.ChangeTracker.Clear();
        (await db.PayrollOpeningBalances.AsNoTracking().CountAsync(x => x.TenantId == tid)).Should().Be(6);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  M3 — the silent column shift
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// ONE UNQUOTED THOUSANDS SEPARATOR, AND EVERY LATER COLUMN IS IN THE WRONG FIELD.
    ///
    /// <para>This test shows the shift landing rather than asserting an exception in the abstract:
    /// <c>AccruedAmount=52,500</c> typed without quotes splits into two cells, so 500 is read as
    /// AccruedAmount and the PriorServiceStartDate column receives "SAR". Before the fix that row was
    /// imported — a 52,500 provision stored as 500, and the surplus trailing cell silently discarded.
    /// The parser now refuses the file by row number and by both counts, before anything is written.</para>
    /// </summary>
    [Fact]
    public void RaggedRow_IsRefusedByRowNumberAndCount_InsteadOfShiftingEveryLaterColumn()
    {
        const string header = "EmployeeCode,AsAtDate,AccruedMonths,AccruedAmount,PriorServiceStartDate,Currency,SourceSystem,SourceRecordId\n";
        const string shifted = "EMP-001,2026-08-31,120,52,500,2016-09-01,SAR,SAP,EOSB-001\n";

        // What the row actually contains: nine cells against eight columns.
        Csv.SplitRow(shifted.TrimEnd('\n')).Should().HaveCount(9);

        var ex = Assert.Throws<CsvShapeException>(() => Csv.Parse(header + shifted));
        ex.RowNumber.Should().Be(2, "counting the header as line 1, so the number matches the file");
        ex.CellCount.Should().Be(9);
        ex.HeaderCount.Should().Be(8);
        ex.Message.Should().Contain("row 2").And.Contain("9 cell").And.Contain("8 column");

        // The quoted spelling is what the consultant should have written, and it still parses.
        var fixedRow = Csv.Parse(header + "EMP-001,2026-08-31,120,\"52,500\",2016-09-01,SAR,SAP,EOSB-001\n");
        fixedRow.Should().ContainSingle();
        fixedRow[0]["AccruedAmount"].Should().Be("52,500");
        fixedRow[0]["PriorServiceStartDate"].Should().Be("2016-09-01",
            "the date is in the date column, which is the whole point");
    }

    /// <summary>A row SHORT of the header is refused too — trailing columns cannot be dropped.</summary>
    [Fact]
    public void ShortRow_IsRefused_RatherThanFilledWithEmptyStrings()
    {
        var ex = Assert.Throws<CsvShapeException>(() => Csv.Parse(
            "EmployeeCode,Year,BalanceType,ComponentCode,Amount,Currency,SourceSystem,SourceRecordId\n" +
            "EMP-001,2026,YTD_GROSS,TOTAL,104000\n"));
        ex.CellCount.Should().Be(5);
        ex.HeaderCount.Should().Be(8);
    }

    /// <summary>
    /// The refusal reaches the consultant as a NAMED section error on the import, not as a 500 — a
    /// migration UI built to render per-row rejections must be able to render this one.
    /// </summary>
    [Fact]
    public async Task ShiftedRow_SurfacesAsANamedImportError_AndNothingIsWritten()
    {
        var (db, conn) = NewDb();
        await using var _ = conn; await using var __ = db;
        var tid = Guid.NewGuid();
        await SeedKsaLeaver(db, tid, new DateTime(2026, 9, 1));

        var commit = await Migration(db, tid).Commit(Package("shifted-eosb",
            ("companyCutover", CutoverCsv),
            ("eosbOpeningProvision",
                "EmployeeCode,AsAtDate,AccruedMonths,AccruedAmount,PriorServiceStartDate,Currency,SourceSystem,SourceRecordId\n" +
                "EMP-001,2026-08-31,120,52,500,2016-09-01,SAR,SAP,EOSB-001\n")), CancellationToken.None);

        var dto = (MigrationReconciliationDto)((OkObjectResult)commit.Result!).Value!;
        db.ChangeTracker.Clear();

        dto.Errors.Should().ContainSingle().Which.Should()
            .StartWith("eosbOpeningProvision row 2").And.Contain("9 cell").And.Contain("8 column");
        (await db.EmployeeEosbOpeningBalances.AsNoTracking().CountAsync(x => x.TenantId == tid))
            .Should().Be(0, "a 52,500 provision must not land as 500");

        // The cutover section beside it is unaffected: the refusal is per file, not per package.
        (await db.CompanyCutovers.AsNoTracking().CountAsync(x => x.TenantId == tid)).Should().Be(1);
    }
}

// ── Stubs ───────────────────────────────────────────────────────────────────────────────────────────

file sealed class _ObcScope : Zayra.Api.Application.Common.IDataScopeService
{
    public Task<Zayra.Api.Application.Common.DataScope> ResolveAsync(ClaimsPrincipal caller, Guid tenantId, CancellationToken ct)
        => Task.FromResult(new Zayra.Api.Application.Common.DataScope
        { Level = Zayra.Api.Application.Common.DataScopeLevel.Organization, AllowedEmployeeIds = null });
}

file sealed class _ObcHttp : IHttpContextAccessor
{
    public _ObcHttp(HttpContext ctx) => HttpContext = ctx;
    public HttpContext? HttpContext { get; set; }
}

file sealed class _ObcNotifications : Zayra.Api.Infrastructure.Notifications.INotificationService
{
    public Task NotifyAsync(Guid t, Guid? u, string title, string msg, string entity, string? entityId, CancellationToken ct) => Task.CompletedTask;
    public Task SendEmailAsync(Guid t, string code, string to, string name, Dictionary<string, string> vars, CancellationToken ct) => Task.CompletedTask;
}

file sealed class _ObcLetters : Zayra.Api.Infrastructure.Documents.Letters.ILetterService
{
    public Task<byte[]> GeneratePayslipPdfAsync(Zayra.Api.Infrastructure.Documents.Letters.PayslipData d, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
    public Task<byte[]> GenerateAppointmentLetterAsync(Zayra.Api.Infrastructure.Documents.Letters.LetterData d, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
    public Task<byte[]> GenerateExperienceLetterAsync(Zayra.Api.Infrastructure.Documents.Letters.LetterData d, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
    public Task<byte[]> GenerateOfferLetterAsync(Zayra.Api.Infrastructure.Documents.Letters.OfferLetterData d, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
}

file sealed class _ObcDocs : Zayra.Api.Infrastructure.Documents.IDocumentStorage
{
    public Task<Zayra.Api.Infrastructure.Documents.StoredDocument> SaveAsync(Guid tenantId, IFormFile file, CancellationToken ct)
        => Task.FromResult(new Zayra.Api.Infrastructure.Documents.StoredDocument(file.FileName, file.ContentType, "storage/test", "/tmp/test"));
    public Task<byte[]> GetBytesAsync(Guid tenantId, string storageUrl, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
    public string ResolvePath(string storageUrl) => "/tmp/test";
}

file sealed class _ObcKsaResolver : ICountryPackResolver
{
    private readonly StubRuleReader _rules;
    public _ObcKsaResolver(StubRuleReader rules) => _rules = rules;

    public IStatutoryDeductionCalculator ResolveDeductionCalculator(string cc, string j)
        => cc is "SAU" or "SA" ? new KsaDeductionCalculator(_rules) : new DefaultStatutoryDeductionCalculator();
    public IEndOfServiceCalculator ResolveEndOfServiceCalculator(string cc, string j)
        => cc is "SAU" or "SA" ? new KsaEndOfServiceCalculator(_rules) : new DefaultEndOfServiceCalculator();
    public IWageProtectionExporter ResolveWageProtectionExporter(string cc, string j) => new DefaultWageProtectionExporter();
    public INationalizationTracker ResolveNationalizationTracker(string cc, string j) => new DefaultNationalizationTracker();
    public ILocalizationProfile ResolveLocalizationProfile(string cc, string j) => new DefaultLocalizationProfile();
    public ICountryPackDescriptor ResolveDescriptor(string cc, string j) => new DefaultCountryPackDescriptor();
}
