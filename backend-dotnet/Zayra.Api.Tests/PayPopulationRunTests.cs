using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.CountryPack;
using Zayra.Api.Controllers;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.CountryPack;
using Zayra.Api.Infrastructure.CountryPack.Ksa;
using Zayra.Api.Infrastructure.Documents.Letters;
using Zayra.Api.Infrastructure.Payroll;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// Payroll pay population (spec section 1, acceptance tests P1-P10, P14). Payroll pays everybody who is employed in
/// the period, whatever the status label says, and every employee a run leaves out is listed with a reason code.
/// Driven end to end through PayrollController.Process and the run population preview.
/// Fixture: KSA company, basic 6,000 + housing 1,500 per employee.
/// </summary>
public class PayPopulationRunTests
{
    private static readonly Guid Maker = Guid.NewGuid();
    private static readonly Guid Checker = Guid.NewGuid();
    private const decimal Basic = 6_000m;
    private const decimal Housing = 1_500m;

    // ══ Harness ═══════════════════════════════════════════════════════════════════════════════════

    private static (ZayraDbContext db, SqliteConnection conn) NewDb()
    {
        var conn = new SqliteConnection("DataSource=:memory:");
        conn.Open();
        var db = new ZayraDbContext(new DbContextOptionsBuilder<ZayraDbContext>().UseSqlite(conn).Options);
        db.Database.EnsureCreated();
        return (db, conn);
    }

    private static readonly Guid FinanceUser = Guid.NewGuid();

    private static DefaultHttpContext Ctx(Guid tenantId, Guid userId, string role = "Admin") => new()
    {
        User = new ClaimsPrincipal(new ClaimsIdentity(new List<Claim>
        {
            new("tenant_id", tenantId.ToString()),
            new(ClaimTypes.NameIdentifier, userId.ToString()),
            new(ClaimTypes.Name, userId == Maker ? "maker" : "checker"),
            new(ClaimTypes.Role, role),
            new("permission", "payroll.read"),
            new("permission", "payroll.write"),
            new("permission", "payroll.approve"),
            new("permission", "payroll.lock"),
        }, "test")),
    };

    private static PayrollController Payroll(ZayraDbContext db, Guid tenantId, Guid userId, string role = "Admin")
    {
        var http = Ctx(tenantId, userId, role);
        return new PayrollController(
            db, new _PpScope(), new _PpHttp(http), new _PpNotifications(),
            new _PpPackResolver(), _PpRules.Rules, new _PpLetters(), new _PpStorage(),
            new Zayra.Api.Infrastructure.Documents.PdfRenderGate(4))
        { ControllerContext = new ControllerContext { HttpContext = http } };
    }

    private sealed record Co(Guid TenantId, Guid CompanyId, Guid StructureId);

    private static async Task<Co> SeedCompany(ZayraDbContext db, Guid tenantId, string name = "Pay Population KSA Co")
    {
        var company = new Company
        {
            TenantId = tenantId, LegalNameEn = name,
            CountryCode = "SAU", Jurisdiction = "KSA-mainland", IsActive = true, DefaultCurrency = "SAR",
        };
        db.Companies.Add(company);
        var structure = new SalaryStructure
        {
            TenantId = tenantId, CompanyId = company.Id, Code = "PP" + Guid.NewGuid().ToString("N")[..6], Name = "Base",
            Currency = "SAR", EffectiveDate = new DateOnly(2024, 1, 1), IsActive = true,
        };
        db.SalaryStructures.Add(structure);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return new Co(tenantId, company.Id, structure.Id);
    }

    private static int _seq;

    private static async Task<Employee> AddEmployee(ZayraDbContext db, Co co, string status,
        DateTime? joined = null, bool salary = true, bool deleted = false)
    {
        var n = Interlocked.Increment(ref _seq);
        var e = new Employee
        {
            TenantId = co.TenantId, CompanyId = co.CompanyId, EmployeeCode = $"PP{n:D4}", FullName = $"Employee {n} ({status})",
            Status = status, JoiningDate = joined ?? new DateTime(2023, 1, 1),
            WorkEmail = $"pp{n}@pp.test", Nationality = "Saudi", ContractType = "Indefinite",
            IsDeleted = deleted,
        };
        db.Employees.Add(e);
        await db.SaveChangesAsync();
        if (salary)
        {
            db.EmployeeSalaryStructures.Add(new EmployeeSalaryStructure
            {
                TenantId = co.TenantId, EmployeeId = e.Id, SalaryStructureId = co.StructureId,
                BasicSalary = Basic, HousingAllowance = Housing,
                EffectiveDate = new DateOnly(2024, 1, 1), IsActive = true,
                CreatedAtUtc = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            });
            db.EmployeePayrollProfiles.Add(new EmployeePayrollProfile
            {
                TenantId = co.TenantId, EmployeeId = e.Id,
                Iban = "SA4420000001234567891234", MolId = $"MOL-{e.EmployeeCode}", SalaryCurrency = "SAR",
            });
        }
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return e;
    }

    private static async Task AddOffboarding(ZayraDbContext db, Co co, Employee e, DateOnly lwd, string status = "InProgress")
    {
        db.EmployeeOffboardings.Add(new EmployeeOffboarding
        {
            TenantId = co.TenantId, CompanyId = co.CompanyId, EmployeeId = e.Id, EmployeeCode = e.EmployeeCode, EmployeeName = e.FullName,
            NoticeDate = lwd.AddDays(-30), LastWorkingDay = lwd, Status = status,
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
    }

    private static async Task<PayrollRun> AddRun(ZayraDbContext db, Co co, int year, int month)
    {
        var run = new PayrollRun
        {
            TenantId = co.TenantId, CompanyId = co.CompanyId, Year = year, Month = month,
            Status = "Draft", RunType = PayrollRunTypes.Regular, IncludesRecurringPay = true,
            CreatedByUserId = Maker,
        };
        db.PayrollRuns.Add(run);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return run;
    }

    private static async Task Process(ZayraDbContext db, Co co, Guid runId)
    {
        var result = await Payroll(db, co.TenantId, Maker).Process(runId, CancellationToken.None);
        result.Should().BeOfType<OkObjectResult>(JsonSerializer.Serialize((result as ObjectResult)?.Value));
        db.ChangeTracker.Clear();
    }

    private static Task<PayrollSlip?> Slip(ZayraDbContext db, Guid runId, int employeeId) =>
        db.PayrollSlips.AsNoTracking().FirstOrDefaultAsync(s => s.RunId == runId && s.EmployeeId == employeeId);

    private static Task<List<PayrollValidationResult>> Findings(ZayraDbContext db, Guid runId) =>
        db.PayrollValidationResults.AsNoTracking().Where(r => r.PayrollRunId == runId).ToListAsync();

    private sealed record LeftOutRow(int EmployeeId, string ReasonCode);

    private static async Task<(List<LeftOutRow> Rows, Dictionary<string, int> Counts, JsonElement Root)> Preview(ZayraDbContext db, Co co, Guid runId)
    {
        var result = await Payroll(db, co.TenantId, Maker).GetRunPopulation(runId, CancellationToken.None);
        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        var root = JsonDocument.Parse(JsonSerializer.Serialize(ok.Value)).RootElement;
        db.ChangeTracker.Clear();
        var rows = root.GetProperty("leftOut").EnumerateArray()
            .Select(x => new LeftOutRow(x.GetProperty("employeeId").GetInt32(), x.GetProperty("reasonCode").GetString()!)).ToList();
        var counts = root.GetProperty("leftOutCounts").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetInt32());
        return (rows, counts, root);
    }

    // ══ P1 — Probation, Confirmed and On leave are paid ═════════════════════════════════════════════

    [Fact]
    public async Task P1_ProbationConfirmedAndOnLeave_EachGetOneSlip_AndNoneIsLeftOut()
    {
        var (db, conn) = NewDb();
        await using var _ = conn; await using var __ = db;
        var co = await SeedCompany(db, Guid.NewGuid());
        var probation = await AddEmployee(db, co, "Probation");
        var confirmed = await AddEmployee(db, co, "Confirmed");
        var onLeave = await AddEmployee(db, co, "On leave");
        var run = await AddRun(db, co, 2026, 3);

        await Process(db, co, run.Id);

        foreach (var e in new[] { probation, confirmed, onLeave })
        {
            var slip = await Slip(db, run.Id, e.Id);
            slip.Should().NotBeNull($"'{e.Status}' is employed and must be paid");
            slip!.BasicSalary.Should().Be(Basic);
        }
        var (rows, _, _) = await Preview(db, co, run.Id);
        rows.Select(r => r.EmployeeId).Should().NotIntersectWith(new[] { probation.Id, confirmed.Id, onLeave.Id });
    }

    // ══ P2 — a resignation with an offboarding is paid to the last working day, then stops ═══════════

    [Fact]
    public async Task P2_ResignedWithOffboarding_FullOctober_FourThirtiethsOfNovember_ThenLeftOutAsLeftBeforePeriod()
    {
        var (db, conn) = NewDb();
        await using var _ = conn; await using var __ = db;
        var co = await SeedCompany(db, Guid.NewGuid());
        var e = await AddEmployee(db, co, "Resigned");
        await AddOffboarding(db, co, e, new DateOnly(2025, 11, 4));

        var oct = await AddRun(db, co, 2025, 10);
        await Process(db, co, oct.Id);
        (await Slip(db, oct.Id, e.Id))!.BasicSalary.Should().Be(Basic, "October is a full month of notice");

        var nov = await AddRun(db, co, 2025, 11);
        await Process(db, co, nov.Id);
        var novSlip = (await Slip(db, nov.Id, e.Id))!;
        novSlip.BasicSalary.Should().Be(800m, "1-4 November = 4/30 of 6,000");
        novSlip.IsFinalWageMonth.Should().BeTrue();

        var dec = await AddRun(db, co, 2025, 12);
        var (rows, counts, _) = await Preview(db, co, dec.Id);
        rows.Should().ContainSingle(r => r.EmployeeId == e.Id).Which.ReasonCode.Should().Be(PayPopulationReasons.LeftBeforePeriod);
        counts[PayPopulationReasons.LeftBeforePeriod].Should().Be(1);
    }

    // ══ P3 — a resignation with no last working day is paid in full and warned ══════════════════════

    [Theory]
    [InlineData("Resigned")]
    [InlineData("Notice period")]
    public async Task P3_ResignedWithoutOffboarding_IsPaidInFull_AndWarnedNoLastWorkingDay(string status)
    {
        var (db, conn) = NewDb();
        await using var _ = conn; await using var __ = db;
        var co = await SeedCompany(db, Guid.NewGuid());
        var e = await AddEmployee(db, co, status);
        var run = await AddRun(db, co, 2026, 3);

        await Process(db, co, run.Id);

        (await Slip(db, run.Id, e.Id))!.BasicSalary.Should().Be(Basic);
        (await Findings(db, run.Id)).Should().Contain(f => f.Code == PayPopulationReasons.WarnNoLastWorkingDay && f.EmployeeId == e.Id);
    }

    // ══ P4 — Suspended is a pay hold, listed ════════════════════════════════════════════════════════

    [Fact]
    public async Task P4_Suspended_GetsNoSlip_AndIsListedAsSuspendedPayHold()
    {
        var (db, conn) = NewDb();
        await using var _ = conn; await using var __ = db;
        var co = await SeedCompany(db, Guid.NewGuid());
        await AddEmployee(db, co, "Active");
        var s = await AddEmployee(db, co, "Suspended");
        var run = await AddRun(db, co, 2026, 3);

        await Process(db, co, run.Id);

        (await Slip(db, run.Id, s.Id)).Should().BeNull();
        (await Findings(db, run.Id)).Should().Contain(f => f.Code == PayPopulationReasons.SuspendedPayHold && f.EmployeeId == s.Id);
        var (rows, _, _) = await Preview(db, co, run.Id);
        rows.Should().ContainSingle(r => r.EmployeeId == s.Id).Which.ReasonCode.Should().Be(PayPopulationReasons.SuspendedPayHold);
    }

    // ══ P5 — no cap: every left-out employee is listed, with counts per reason ══════════════════════

    [Fact]
    public async Task P5_ThirtyLeftOutAcrossReasons_AreAllListed_WithCountsPerReason()
    {
        var (db, conn) = NewDb();
        await using var _ = conn; await using var __ = db;
        var co = await SeedCompany(db, Guid.NewGuid());
        await AddEmployee(db, co, "Active");
        for (var i = 0; i < 12; i++) await AddEmployee(db, co, "Suspended");
        for (var i = 0; i < 10; i++) await AddEmployee(db, co, "Terminated");          // no date anywhere
        for (var i = 0; i < 8; i++) await AddEmployee(db, co, "Invited");
        var run = await AddRun(db, co, 2026, 3);

        await Process(db, co, run.Id);

        var (rows, counts, _) = await Preview(db, co, run.Id);
        rows.Should().HaveCount(30, "the old report stopped at 25");
        counts[PayPopulationReasons.SuspendedPayHold].Should().Be(12);
        counts[PayPopulationReasons.ExitStatusNoDate].Should().Be(10);
        counts[PayPopulationReasons.NotYetEmployed].Should().Be(8);
        (await Findings(db, run.Id)).Count(f => f.Code == PayPopulationReasons.ExitStatusNoDate).Should().Be(10);
    }

    // ══ P6 / P7 — not yet employed ══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task P6_PreEmploymentStatuses_AreListedOnlyWhenTheyHaveASalaryStructure()
    {
        var (db, conn) = NewDb();
        await using var _ = conn; await using var __ = db;
        var co = await SeedCompany(db, Guid.NewGuid());
        await AddEmployee(db, co, "Active");
        var draftNoPay = await AddEmployee(db, co, "Draft", salary: false);
        var invitedNoPay = await AddEmployee(db, co, "Invited", salary: false);
        var preboardingPaid = await AddEmployee(db, co, "Pre-boarding");
        var draftPaid = await AddEmployee(db, co, "Draft");
        var run = await AddRun(db, co, 2026, 3);

        await Process(db, co, run.Id);

        (await Slip(db, run.Id, preboardingPaid.Id)).Should().BeNull();
        (await Slip(db, run.Id, draftPaid.Id)).Should().BeNull();
        var (rows, _, _) = await Preview(db, co, run.Id);
        rows.Select(r => r.EmployeeId).Should().NotContain(new[] { draftNoPay.Id, invitedNoPay.Id });
        rows.Where(r => r.EmployeeId == preboardingPaid.Id || r.EmployeeId == draftPaid.Id)
            .Should().HaveCount(2).And.OnlyContain(r => r.ReasonCode == PayPopulationReasons.NotYetEmployed);
    }

    [Fact]
    public async Task P7_ActiveJoiningAfterThePeriod_GetsNoSlip_AndIsListedNotYetEmployed()
    {
        var (db, conn) = NewDb();
        await using var _ = conn; await using var __ = db;
        var co = await SeedCompany(db, Guid.NewGuid());
        await AddEmployee(db, co, "Active");
        var joiner = await AddEmployee(db, co, "Active", joined: new DateTime(2026, 4, 5));
        var run = await AddRun(db, co, 2026, 3);

        await Process(db, co, run.Id);

        (await Slip(db, run.Id, joiner.Id)).Should().BeNull();
        var (rows, _, _) = await Preview(db, co, run.Id);
        rows.Should().ContainSingle(r => r.EmployeeId == joiner.Id).Which.ReasonCode.Should().Be(PayPopulationReasons.NotYetEmployed);
    }

    // ══ P8 — soft-deleted leavers with wages owed ═══════════════════════════════════════════════════

    [Fact]
    public async Task P8_SoftDeletedLeaverWithWagesOwed_IsPaidToTheLastWorkingDay_AndWarned()
    {
        var (db, conn) = NewDb();
        await using var _ = conn; await using var __ = db;
        var co = await SeedCompany(db, Guid.NewGuid());
        await AddEmployee(db, co, "Active");
        var owed = await AddEmployee(db, co, "Offboarded", deleted: true);
        await AddOffboarding(db, co, owed, new DateOnly(2026, 2, 20));
        var notOwed = await AddEmployee(db, co, "Active", deleted: true);
        var feb = await AddRun(db, co, 2026, 2);

        await Process(db, co, feb.Id);

        var slip = await Slip(db, feb.Id, owed.Id);
        slip.Should().NotBeNull("wages earned to 20 February are owed whatever the record flag says");
        slip!.BasicSalary.Should().Be(4_000m, "20/30 of 6,000");
        slip.IsFinalWageMonth.Should().BeTrue();
        var findings = await Findings(db, feb.Id);
        findings.Should().Contain(f => f.Code == PayPopulationReasons.WarnDeletedButWagesOwed && f.EmployeeId == owed.Id);
        (await Slip(db, feb.Id, notOwed.Id)).Should().BeNull();
        var (rows, _, _) = await Preview(db, co, feb.Id);
        rows.Should().ContainSingle(r => r.EmployeeId == notOwed.Id).Which.ReasonCode.Should().Be(PayPopulationReasons.DeletedNoWagesOwed);
    }

    // ══ P9 — company scope holds on every branch ════════════════════════════════════════════════════

    [Fact]
    public async Task P9_ALeaverInCompanyB_IsAbsentFromCompanyAsRun_OnEveryBranch()
    {
        var (db, conn) = NewDb();
        await using var _ = conn; await using var __ = db;
        var tid = Guid.NewGuid();
        var a = await SeedCompany(db, tid, "Company A");
        var b = await SeedCompany(db, tid, "Company B");
        await AddEmployee(db, a, "Active");
        var bLeaver = await AddEmployee(db, b, "Resigned");
        await AddOffboarding(db, b, bLeaver, new DateOnly(2026, 3, 10));
        var bDeleted = await AddEmployee(db, b, "Offboarded", deleted: true);
        await AddOffboarding(db, b, bDeleted, new DateOnly(2026, 3, 10));
        var bProbation = await AddEmployee(db, b, "Probation");
        var bSuspended = await AddEmployee(db, b, "Suspended");
        var run = await AddRun(db, a, 2026, 3);

        await Process(db, a, run.Id);

        var bIds = new[] { bLeaver.Id, bDeleted.Id, bProbation.Id, bSuspended.Id };
        (await db.PayrollSlips.AsNoTracking().Where(s => s.RunId == run.Id).Select(s => s.EmployeeId).ToListAsync())
            .Should().NotIntersectWith(bIds);
        (await Findings(db, run.Id)).Where(f => f.EmployeeId != null).Select(f => f.EmployeeId!.Value).Should().NotIntersectWith(bIds);
        var (rows, _, _) = await Preview(db, a, run.Id);
        rows.Select(r => r.EmployeeId).Should().NotIntersectWith(bIds);
    }

    // ══ P10 — the final wage month is paid once ═════════════════════════════════════════════════════

    [Fact]
    public async Task P10_AFinalWageSlipStopsTheNextRun_AndVoidingItMakesThemEligibleAgain()
    {
        var (db, conn) = NewDb();
        await using var _ = conn; await using var __ = db;
        var co = await SeedCompany(db, Guid.NewGuid());
        await AddEmployee(db, co, "Active");
        var e = await AddEmployee(db, co, "Resigned");
        await AddOffboarding(db, co, e, new DateOnly(2026, 7, 15));
        var june = await AddRun(db, co, 2026, 6);
        await Process(db, co, june.Id);
        // The June slip was (wrongly or by an early settlement) marked as the final wage month.
        var juneSlip = await db.PayrollSlips.FirstAsync(s => s.RunId == june.Id && s.EmployeeId == e.Id);
        juneSlip.IsFinalWageMonth = true;
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var july = await AddRun(db, co, 2026, 7);
        var (rows, _, _) = await Preview(db, co, july.Id);
        rows.Should().ContainSingle(r => r.EmployeeId == e.Id).Which.ReasonCode.Should().Be(PayPopulationReasons.FinalWageAlreadyPaid);

        juneSlip = await db.PayrollSlips.FirstAsync(s => s.Id == juneSlip.Id);
        juneSlip.Status = "Voided";
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        await Process(db, co, july.Id);
        (await Slip(db, july.Id, e.Id)).Should().NotBeNull("the voided final-wage slip no longer stops them");
    }

    // ══ Exit statuses: dated by offboarding or status history, else listed ═════════════════════════

    [Fact]
    public async Task ATerminationDatedOnlyInStatusHistory_IsPaidToThatDate()
    {
        var (db, conn) = NewDb();
        await using var _ = conn; await using var __ = db;
        var co = await SeedCompany(db, Guid.NewGuid());
        await AddEmployee(db, co, "Active");
        var e = await AddEmployee(db, co, "Terminated");
        db.EmployeeStatusHistories.Add(new EmployeeStatusHistory
        {
            TenantId = co.TenantId, EmployeeId = e.Id, OldStatus = "Active", NewStatus = "Terminated",
            EffectiveDate = new DateOnly(2026, 3, 15), Reason = "test",
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var run = await AddRun(db, co, 2026, 3);

        await Process(db, co, run.Id);

        var slip = (await Slip(db, run.Id, e.Id))!;
        slip.BasicSalary.Should().Be(3_000m, "1-15 March = 15/30 of 6,000");
        slip.IsFinalWageMonth.Should().BeTrue();
    }

    [Theory]
    [InlineData("Retired")]
    [InlineData("Deceased")]
    [InlineData("Exited")]
    public async Task AnExitStatusWithNoDateAnywhere_IsNotPaid_AndIsListedExitStatusNoDate(string status)
    {
        var (db, conn) = NewDb();
        await using var _ = conn; await using var __ = db;
        var co = await SeedCompany(db, Guid.NewGuid());
        await AddEmployee(db, co, "Active");
        var e = await AddEmployee(db, co, status);
        var run = await AddRun(db, co, 2026, 3);

        await Process(db, co, run.Id);

        (await Slip(db, run.Id, e.Id)).Should().BeNull();
        (await Findings(db, run.Id)).Should().Contain(f => f.Code == PayPopulationReasons.ExitStatusNoDate && f.EmployeeId == e.Id);
    }

    [Fact]
    public async Task InactiveAndArchived_AreListedInactiveOrArchived()
    {
        var (db, conn) = NewDb();
        await using var _ = conn; await using var __ = db;
        var co = await SeedCompany(db, Guid.NewGuid());
        await AddEmployee(db, co, "Active");
        var inactive = await AddEmployee(db, co, "Inactive");
        var archived = await AddEmployee(db, co, "Archived");
        var run = await AddRun(db, co, 2026, 3);

        await Process(db, co, run.Id);

        (await Slip(db, run.Id, inactive.Id)).Should().BeNull();
        var (rows, _, _) = await Preview(db, co, run.Id);
        rows.Where(r => r.EmployeeId == inactive.Id || r.EmployeeId == archived.Id)
            .Should().HaveCount(2).And.OnlyContain(r => r.ReasonCode == PayPopulationReasons.InactiveOrArchived);
    }

    // ══ P14 — "On leave" by hand with no leave behind it ════════════════════════════════════════════

    [Fact]
    public async Task P14_OnLeaveWithNoApprovedLeaveInThePeriod_IsPaidInFull_AndWarned()
    {
        var (db, conn) = NewDb();
        await using var _ = conn; await using var __ = db;
        var co = await SeedCompany(db, Guid.NewGuid());
        var bare = await AddEmployee(db, co, "On leave");
        var covered = await AddEmployee(db, co, "On leave");
        db.LeaveRequests.Add(new LeaveRequest
        {
            TenantId = co.TenantId, CompanyId = co.CompanyId, EmployeeId = covered.Id,
            StartDate = new DateOnly(2026, 2, 20), EndDate = new DateOnly(2026, 3, 5), Status = "Approved",
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var run = await AddRun(db, co, 2026, 3);

        await Process(db, co, run.Id);

        (await Slip(db, run.Id, bare.Id))!.BasicSalary.Should().Be(Basic);
        var findings = await Findings(db, run.Id);
        findings.Should().Contain(f => f.Code == PayPopulationReasons.WarnOnLeaveNoLeaveRequest && f.EmployeeId == bare.Id);
        findings.Should().NotContain(f => f.Code == PayPopulationReasons.WarnOnLeaveNoLeaveRequest && f.EmployeeId == covered.Id);
    }


    // ══ P13 — people newly paid by this fix show in the reconciliation against the prior month ═════

    [Fact]
    public async Task P13_PeopleNewlyPaid_ShowAsAddsInTheReconciliationAgainstThePriorMonth()
    {
        var (db, conn) = NewDb();
        await using var _ = conn; await using var __ = db;
        var co = await SeedCompany(db, Guid.NewGuid());
        await AddEmployee(db, co, "Active");
        var feb = await AddRun(db, co, 2026, 2);
        await Process(db, co, feb.Id);
        (await db.PayrollRuns.FirstAsync(r => r.Id == feb.Id)).Status = "Locked";
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        // Employed all along, but never paid before (the old population was Status == "Active" only).
        for (var i = 0; i < 3; i++) await AddEmployee(db, co, i == 0 ? "Probation" : i == 1 ? "Confirmed" : "On leave");
        var mar = await AddRun(db, co, 2026, 3);
        await Process(db, co, mar.Id);

        var caller = new Zayra.Api.Infrastructure.Payroll.ManpowerRecon.ReconCaller(null, "Reviewer", true,
            Zayra.Api.Application.Common.EntityScopeContext.GroupLevel,
            new Zayra.Api.Application.Common.DataScope { Level = Zayra.Api.Application.Common.DataScopeLevel.Organization, AllowedEmployeeIds = null });
        var built = await new Zayra.Api.Infrastructure.Payroll.ManpowerRecon.ManpowerReconService(db)
            .BuildAsync(co.TenantId, mar.Id, caller, null, CancellationToken.None);

        var bridge = built.Build!.Bridge!;
        bridge.Opening.Should().Be(1);
        bridge.Closing.Should().Be(4);
        bridge.Count(Zayra.Api.Infrastructure.Payroll.ManpowerRecon.ReconCategory.LateJoiner)
            .Should().Be(3, "the reviewer must see +3 newly paid before Approve, flagged as paid for the first time");
        bridge.Ties.Should().BeTrue();
    }


    // ══ Section 2 (R5b) — a month slice of an unpaid leave is not scaled a second time ═══════════════

    [Fact]
    public async Task AMonthSliceOfUnpaidLeave_IsScaledOnlyByItsOwnDaysInsideTheEmploymentWindow()
    {
        var (db, conn) = NewDb();
        await using var _ = conn; await using var __ = db;
        var co = await SeedCompany(db, Guid.NewGuid());
        await AddEmployee(db, co, "Active");
        var e = await AddEmployee(db, co, "Resigned");
        await AddOffboarding(db, co, e, new DateOnly(2026, 2, 3));
        var leave = new LeaveRequest
        {
            TenantId = co.TenantId, CompanyId = co.CompanyId, EmployeeId = e.Id, StartDate = new DateOnly(2026, 1, 25),
            EndDate = new DateOnly(2026, 2, 5), TotalDays = 10m, Status = "Approved", DayType = "Full",
        };
        db.LeaveRequests.Add(leave);
        db.LeavePayrollImpacts.AddRange(
            new LeavePayrollImpact { TenantId = co.TenantId, LeaveRequestId = leave.Id, EmployeeId = e.Id, PayPeriod = "2026-01",
                ImpactType = "Leave Deduction (Unpaid)", Days = 5m, Amount = 1_000m, Status = "Processed",
                SliceFrom = new DateOnly(2026, 1, 25), SliceTo = new DateOnly(2026, 1, 31) },
            new LeavePayrollImpact { TenantId = co.TenantId, LeaveRequestId = leave.Id, EmployeeId = e.Id, PayPeriod = "2026-02",
                ImpactType = "Leave Deduction (Unpaid)", Days = 5m, Amount = 1_000m, Status = "Pending",
                SliceFrom = new DateOnly(2026, 2, 1), SliceTo = new DateOnly(2026, 2, 5) });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var feb = await AddRun(db, co, 2026, 2);

        await Process(db, co, feb.Id);

        var leaveDeduction = (await db.PayrollDeductions.AsNoTracking()
            .Where(d => d.PayrollRunId == feb.Id && d.EmployeeId == e.Id && d.ComponentCode == "LEAVE").Select(d => d.Amount).ToListAsync()).Sum();
        leaveDeduction.Should().Be(600m,
            "the February slice covers 1-5 Feb and 1-3 Feb are inside employment: 3/5 of 1,000, not 3/12 of it (the whole request's days)");
    }

    // ══ Section 4 — runs past Processed are never rewritten ═════════════════════════════════════════

    [Theory]
    [InlineData("Processed")]
    [InlineData("PendingFinanceReview")]
    [InlineData("Approved")]
    [InlineData("Locked")]
    [InlineData("Paid")]
    public async Task ARunPastProcessing_IsNeverReprocessed_SoNewlyEligiblePeopleCannotRewriteIt(string status)
    {
        var (db, conn) = NewDb();
        await using var _ = conn; await using var __ = db;
        var co = await SeedCompany(db, Guid.NewGuid());
        await AddEmployee(db, co, "Active");
        var run = await AddRun(db, co, 2026, 3);
        await Process(db, co, run.Id);
        var slipsBefore = await db.PayrollSlips.AsNoTracking().CountAsync(s => s.RunId == run.Id);
        (await db.PayrollRuns.FirstAsync(r => r.Id == run.Id)).Status = status;
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        await AddEmployee(db, co, "Probation");

        var result = await Payroll(db, co.TenantId, Maker).Process(run.Id, CancellationToken.None);

        result.Should().BeOfType<BadRequestObjectResult>();
        (await db.PayrollSlips.AsNoTracking().CountAsync(s => s.RunId == run.Id)).Should().Be(slipsBefore);
    }

    // ══ P3 (decision 3) — warn, don't block: Finance acknowledges the count before Approve ════════════

    private static async Task<(Co Co, PayrollRun Run, Employee Resigned)> ProcessedRunWithOneUndatedResignation(ZayraDbContext db)
    {
        var co = await SeedCompany(db, Guid.NewGuid());
        await AddEmployee(db, co, "Active");
        var resigned = await AddEmployee(db, co, "Resigned");
        await AddEmployee(db, co, "Suspended");
        var run = await AddRun(db, co, 2026, 3);
        await Process(db, co, run.Id);
        return (co, run, resigned);
    }

    [Fact]
    public async Task P3_ApproveIsRefusedUntilThePayWarningCountIsStated_ThenItGoesThrough()
    {
        var (db, conn) = NewDb();
        await using var _ = conn; await using var __ = db;
        var (co, run, _) = await ProcessedRunWithOneUndatedResignation(db);
        await ReconTestSupport.SignOffAsync(db, run.Id);
        db.ChangeTracker.Clear();

        var refused = await Payroll(db, co.TenantId, Checker).Approve(run.Id, new PayrollDecisionRequest("ok"), CancellationToken.None);
        var body = JsonDocument.Parse(JsonSerializer.Serialize(refused.Should().BeOfType<ConflictObjectResult>().Subject.Value)).RootElement;
        body.GetProperty("error").GetString().Should().Be("pay_warnings_not_acknowledged");
        body.GetProperty("payWarningCount").GetInt32().Should().Be(1);
        db.ChangeTracker.Clear();

        (await Payroll(db, co.TenantId, Checker).Approve(run.Id, new PayrollDecisionRequest("ok", ExpectedPayWarningCount: 2), CancellationToken.None))
            .Should().BeOfType<ConflictObjectResult>("the count must match exactly, like a cash count");
        db.ChangeTracker.Clear();

        (await Payroll(db, co.TenantId, Checker).Approve(run.Id, new PayrollDecisionRequest("ok", ExpectedPayWarningCount: 1), CancellationToken.None))
            .Should().BeOfType<OkObjectResult>();
        (await db.PayrollRuns.AsNoTracking().FirstAsync(r => r.Id == run.Id)).Status.Should().Be("Approved");
    }

    [Fact]
    public async Task Validate_KeepsTheLeftOutListAndThePayWarnings()
    {
        var (db, conn) = NewDb();
        await using var _ = conn; await using var __ = db;
        var (co, run, resigned) = await ProcessedRunWithOneUndatedResignation(db);

        (await Payroll(db, co.TenantId, Maker).Validate(run.Id, CancellationToken.None)).Should().BeOfType<OkObjectResult>();
        db.ChangeTracker.Clear();

        var findings = await Findings(db, run.Id);
        findings.Should().Contain(f => f.Code == PayPopulationReasons.WarnNoLastWorkingDay && f.EmployeeId == resigned.Id,
            "/validate rebuilds the findings wholesale, and Approve counts this one");
        findings.Should().Contain(f => f.Code == PayPopulationReasons.SuspendedPayHold);
        findings.Should().NotContain(f => f.Code == "MISSING_SALARY_STRUCTURE");
    }

    [Fact]
    public async Task ThePopulationPreview_ReportsThePayWarningCountApproveExpects()
    {
        var (db, conn) = NewDb();
        await using var _ = conn; await using var __ = db;
        var (co, run, resigned) = await ProcessedRunWithOneUndatedResignation(db);

        var (_, _, root) = await Preview(db, co, run.Id);

        root.GetProperty("payWarningCount").GetInt32().Should().Be(1);
        root.GetProperty("payWarnings")[0].GetProperty("employeeId").GetInt32().Should().Be(resigned.Id);
        root.GetProperty("payWarnings")[0].GetProperty("reasonCode").GetString().Should().Be(PayPopulationReasons.WarnNoLastWorkingDay);
    }

    [Fact]
    public async Task ARunWithNoPayWarnings_ApprovesWithoutAnyNewAcknowledgement()
    {
        var (db, conn) = NewDb();
        await using var _ = conn; await using var __ = db;
        var co = await SeedCompany(db, Guid.NewGuid());
        await AddEmployee(db, co, "Active");
        await AddEmployee(db, co, "Probation");
        var run = await AddRun(db, co, 2026, 3);
        await Process(db, co, run.Id);
        await ReconTestSupport.SignOffAsync(db, run.Id);
        db.ChangeTracker.Clear();

        (await Payroll(db, co.TenantId, Checker).Approve(run.Id, new PayrollDecisionRequest("ok"), CancellationToken.None))
            .Should().BeOfType<OkObjectResult>("an ordinary run must approve exactly as before");
    }

    // ══ Money audit fixes ═════════════════════════════════════════════════════════════════════════════

    private static async Task AddStatusChange(ZayraDbContext db, Co co, Employee e, string from, string to, DateOnly effective)
    {
        db.EmployeeStatusHistories.Add(new EmployeeStatusHistory
        {
            TenantId = co.TenantId, EmployeeId = e.Id, OldStatus = from, NewStatus = to, EffectiveDate = effective, Reason = "test",
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
    }

    [Fact]
    public async Task AReactivatedEmployee_WithAnOldCompletedOffboarding_IsPaid()
    {
        var (db, conn) = NewDb();
        await using var _ = conn; await using var __ = db;
        var co = await SeedCompany(db, Guid.NewGuid());
        var e = await AddEmployee(db, co, "Active");
        await AddOffboarding(db, co, e, new DateOnly(2025, 6, 30), status: "Completed");
        await AddStatusChange(db, co, e, "Active", "Offboarded", new DateOnly(2025, 6, 30));
        await AddStatusChange(db, co, e, "Offboarded", "Active", new DateOnly(2025, 9, 1));
        var run = await AddRun(db, co, 2026, 3);

        await Process(db, co, run.Id);

        (await Slip(db, run.Id, e.Id)).Should().NotBeNull("set back to Active after the old offboarding, so employed and paid")
            .And.Subject.As<PayrollSlip>().BasicSalary.Should().Be(Basic);
        (await Findings(db, run.Id)).Should().NotContain(f => f.EmployeeId == e.Id && f.Code == PayPopulationReasons.LeftBeforePeriod);
    }

    [Fact]
    public async Task AnActiveEmployee_WithAPastLastWorkingDayAndNoReactivation_IsAWarningTheApproveCountIncludes()
    {
        var (db, conn) = NewDb();
        await using var _ = conn; await using var __ = db;
        var co = await SeedCompany(db, Guid.NewGuid());
        await AddEmployee(db, co, "Active");
        var e = await AddEmployee(db, co, "Active");
        await AddOffboarding(db, co, e, new DateOnly(2025, 6, 30), status: "Completed");
        var run = await AddRun(db, co, 2026, 3);

        await Process(db, co, run.Id);

        (await Slip(db, run.Id, e.Id)).Should().BeNull("no recorded reactivation: the last working day still applies");
        var finding = (await Findings(db, run.Id)).Should().ContainSingle(f => f.EmployeeId == e.Id).Subject;
        finding.Code.Should().Be(PayPopulationReasons.WarnEmployedStatusNotPaid);
        finding.Severity.Should().Be("Warning", "a person whose status says employed is never left out as Info");
        var (_, _, root) = await Preview(db, co, run.Id);
        root.GetProperty("payWarningCount").GetInt32().Should().Be(1);

        await ReconTestSupport.SignOffAsync(db, run.Id);
        db.ChangeTracker.Clear();
        var refused = await Payroll(db, co.TenantId, Checker).Approve(run.Id, new PayrollDecisionRequest("ok"), CancellationToken.None);
        refused.Should().BeOfType<ConflictObjectResult>().Which.Value!.ToString().Should().Contain("pay_warnings_not_acknowledged");
        db.ChangeTracker.Clear();
        (await Payroll(db, co.TenantId, Checker).Approve(run.Id, new PayrollDecisionRequest("ok", ExpectedPayWarningCount: 1), CancellationToken.None))
            .Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task ADraftPreview_CountsAnEmployedPersonLeftOut_InThePayWarningCount()
    {
        var (db, conn) = NewDb();
        await using var _ = conn; await using var __ = db;
        var co = await SeedCompany(db, Guid.NewGuid());
        await AddEmployee(db, co, "Active");
        var e = await AddEmployee(db, co, "Probation");
        await AddOffboarding(db, co, e, new DateOnly(2025, 6, 30), status: "Completed");
        var run = await AddRun(db, co, 2026, 3);

        var (rows, _, root) = await Preview(db, co, run.Id);

        rows.Should().ContainSingle(r => r.EmployeeId == e.Id).Which.ReasonCode.Should().Be(PayPopulationReasons.WarnEmployedStatusNotPaid);
        root.GetProperty("payWarningCount").GetInt32().Should().Be(1, "the Draft preview predicts the count Approve will ask for");
    }

    [Fact]
    public async Task LevelOneApproval_ThenValidate_KeepsTheAcknowledgement_AndFinanceApprovesWithTheSameCount()
    {
        var (db, conn) = NewDb();
        await using var _ = conn; await using var __ = db;
        var (co, run, resigned) = await ProcessedRunWithOneUndatedResignation(db);
        await ReconTestSupport.SignOffAsync(db, run.Id);
        db.ChangeTracker.Clear();

        // Level 1: an HR Manager moves the run to Finance, acknowledging the one warned person.
        (await Payroll(db, co.TenantId, Checker, "HR Manager").Approve(run.Id, new PayrollDecisionRequest("ok"), CancellationToken.None))
            .Should().BeOfType<ConflictObjectResult>("level 1 is held to the same acknowledgement");
        db.ChangeTracker.Clear();
        (await Payroll(db, co.TenantId, Checker, "HR Manager").Approve(run.Id, new PayrollDecisionRequest("ok", ExpectedPayWarningCount: 1), CancellationToken.None))
            .Should().BeOfType<OkObjectResult>();
        (await db.PayrollRuns.AsNoTracking().FirstAsync(r => r.Id == run.Id)).Status.Should().Be("PendingFinanceReview");
        db.ChangeTracker.Clear();

        // HR now records a last working day before the period. Re-deriving would drop the warning and the count Finance
        // must acknowledge; past Processed, /validate rewrites nothing.
        await AddOffboarding(db, co, resigned, new DateOnly(2026, 2, 10));
        var before = (await Findings(db, run.Id)).Select(f => (f.Code, f.EmployeeId, f.Severity)).OrderBy(x => x.Code).ThenBy(x => x.EmployeeId).ToList();
        (await Payroll(db, co.TenantId, Maker).Validate(run.Id, CancellationToken.None)).Should().BeOfType<OkObjectResult>();
        db.ChangeTracker.Clear();
        var after = (await Findings(db, run.Id)).Select(f => (f.Code, f.EmployeeId, f.Severity)).OrderBy(x => x.Code).ThenBy(x => x.EmployeeId).ToList();
        after.Should().Equal(before, "a run past Processed is read-only to /validate");

        (await Payroll(db, co.TenantId, FinanceUser, "Finance Controller").Approve(run.Id, new PayrollDecisionRequest("ok"), CancellationToken.None))
            .Should().BeOfType<ConflictObjectResult>("Finance acknowledges the same person level 1 did");
        db.ChangeTracker.Clear();
        (await Payroll(db, co.TenantId, FinanceUser, "Finance Controller").Approve(run.Id, new PayrollDecisionRequest("ok", ExpectedPayWarningCount: 1), CancellationToken.None))
            .Should().BeOfType<OkObjectResult>();
        (await db.PayrollRuns.AsNoTracking().FirstAsync(r => r.Id == run.Id)).Status.Should().Be("Approved");
    }

    [Fact]
    public async Task Validate_RaisesABlockingReprocessError_WhenAPaidEmployeeNoLongerHasTheirStanding()
    {
        var (db, conn) = NewDb();
        await using var _ = conn; await using var __ = db;
        var co = await SeedCompany(db, Guid.NewGuid());
        await AddEmployee(db, co, "Active");
        var e = await AddEmployee(db, co, "Active");
        var run = await AddRun(db, co, 2026, 3);
        await Process(db, co, run.Id);
        (await Slip(db, run.Id, e.Id)).Should().NotBeNull();
        (await db.Employees.FirstAsync(x => x.Id == e.Id)).Status = "Suspended";
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        (await Payroll(db, co.TenantId, Maker).Validate(run.Id, CancellationToken.None)).Should().BeOfType<OkObjectResult>();
        db.ChangeTracker.Clear();

        var error = (await Findings(db, run.Id)).Should().ContainSingle(f => f.Code == PayPopulationReasons.StandingChangedReprocess).Subject;
        error.Severity.Should().Be("Error");
        error.EmployeeId.Should().Be(e.Id);
        error.Message.Should().Contain("Re-process").And.Contain("أعد معالجة");
        await ReconTestSupport.SignOffAsync(db, run.Id);
        db.ChangeTracker.Clear();
        (await Payroll(db, co.TenantId, Checker).Approve(run.Id, new PayrollDecisionRequest("ok"), CancellationToken.None))
            .Should().BeOfType<UnprocessableEntityObjectResult>("a payslip computed under a standing the person no longer has blocks approval");
    }

    [Fact]
    public async Task Validate_RaisesTheReprocessError_WhenALastWorkingDayInThePeriodIsRecordedAfterProcess()
    {
        var (db, conn) = NewDb();
        await using var _ = conn; await using var __ = db;
        var co = await SeedCompany(db, Guid.NewGuid());
        await AddEmployee(db, co, "Active");
        var e = await AddEmployee(db, co, "Active");
        var run = await AddRun(db, co, 2026, 3);
        await Process(db, co, run.Id);
        await AddOffboarding(db, co, e, new DateOnly(2026, 3, 10));

        (await Payroll(db, co.TenantId, Maker).Validate(run.Id, CancellationToken.None)).Should().BeOfType<OkObjectResult>();
        db.ChangeTracker.Clear();

        (await Findings(db, run.Id)).Should().Contain(f => f.Code == PayPopulationReasons.StandingChangedReprocess && f.EmployeeId == e.Id && f.Severity == "Error",
            "the slip pays a full month; the person now leaves on 10 March");
    }

    [Fact]
    public async Task Validate_CountsPayWarningsOnlyForPeopleTheRunPays()
    {
        var (db, conn) = NewDb();
        await using var _ = conn; await using var __ = db;
        var (co, run, resigned) = await ProcessedRunWithOneUndatedResignation(db);
        var lateResigned = await AddEmployee(db, co, "Resigned");   // added after Process: no slip on this run

        (await Payroll(db, co.TenantId, Maker).Validate(run.Id, CancellationToken.None)).Should().BeOfType<OkObjectResult>();
        db.ChangeTracker.Clear();

        var warned = (await Findings(db, run.Id)).Where(f => PayPopulationReasons.AcknowledgeBeforeApprove.Contains(f.Code)).Select(f => f.EmployeeId).ToList();
        warned.Should().Equal(new int?[] { resigned.Id }, "a warning about someone the run did not pay would change the count Approve asks for");
        warned.Should().NotContain(lateResigned.Id);
    }

    [Fact]
    public async Task ALoneSurvivingSlice_IsScaledByItsOwnDates()
    {
        // 26 Apr - 2 May 2026: 1-2 May are Fri/Sat, so the only slice is April's 5 days (26-30 Apr) = TotalDays.
        var (db, conn) = NewDb();
        await using var _ = conn; await using var __ = db;
        var co = await SeedCompany(db, Guid.NewGuid());
        await AddEmployee(db, co, "Active");
        var e = await AddEmployee(db, co, "Resigned");
        await AddOffboarding(db, co, e, new DateOnly(2026, 4, 28));
        var leave = new LeaveRequest
        {
            TenantId = co.TenantId, CompanyId = co.CompanyId, EmployeeId = e.Id, StartDate = new DateOnly(2026, 4, 26),
            EndDate = new DateOnly(2026, 5, 2), TotalDays = 5m, Status = "Approved", DayType = "Full",
        };
        db.LeaveRequests.Add(leave);
        db.LeavePayrollImpacts.Add(new LeavePayrollImpact
        {
            TenantId = co.TenantId, LeaveRequestId = leave.Id, EmployeeId = e.Id, PayPeriod = "2026-04",
            ImpactType = "Leave Deduction (Unpaid)", Days = 5m, Amount = 1_000m, Status = "Pending",
            SliceFrom = new DateOnly(2026, 4, 26), SliceTo = new DateOnly(2026, 4, 30),
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var apr = await AddRun(db, co, 2026, 4);

        await Process(db, co, apr.Id);

        var leaveDeduction = (await db.PayrollDeductions.AsNoTracking()
            .Where(d => d.PayrollRunId == apr.Id && d.EmployeeId == e.Id && d.ComponentCode == "LEAVE").Select(d => d.Amount).ToListAsync()).Sum();
        leaveDeduction.Should().Be(600m, "26-28 April of the slice's 26-30 April: 3/5 of 1,000, not 3/7 (the whole request's span)");
    }

    [Fact]
    public async Task ASickLeaveScaleImpact_IsScaledByTheWholeRequest_NotMistakenForAMonthSlice()
    {
        // Art. 117 scale row: Days = unpaid-equivalent days (2.5 of 12), charged whole to the start month, no slice dates.
        var (db, conn) = NewDb();
        await using var _ = conn; await using var __ = db;
        var co = await SeedCompany(db, Guid.NewGuid());
        await AddEmployee(db, co, "Active");
        var e = await AddEmployee(db, co, "Resigned");
        await AddOffboarding(db, co, e, new DateOnly(2026, 1, 27));
        var leave = new LeaveRequest
        {
            TenantId = co.TenantId, CompanyId = co.CompanyId, EmployeeId = e.Id, StartDate = new DateOnly(2026, 1, 25),
            EndDate = new DateOnly(2026, 2, 5), TotalDays = 10m, Status = "Approved", DayType = "Full",
        };
        db.LeaveRequests.Add(leave);
        db.LeavePayrollImpacts.Add(new LeavePayrollImpact
        {
            TenantId = co.TenantId, LeaveRequestId = leave.Id, EmployeeId = e.Id, PayPeriod = "2026-01",
            ImpactType = "Leave Deduction (KSA Art.117 sick-leave scale: 2.5d@0%)", Days = 2.5m, Amount = 1_200m, Status = "Pending",
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var jan = await AddRun(db, co, 2026, 1);

        await Process(db, co, jan.Id);

        var leaveDeduction = (await db.PayrollDeductions.AsNoTracking()
            .Where(d => d.PayrollRunId == jan.Id && d.EmployeeId == e.Id && d.ComponentCode == "LEAVE").Select(d => d.Amount).ToListAsync()).Sum();
        leaveDeduction.Should().Be(300m, "25-27 January of the whole 25 Jan - 5 Feb request: 3/12 of 1,200, not 3/7");
    }

    [Fact]
    public async Task ACrossMonthUnpaidLeave_IsDeductedAcrossTwoProcessedRuns()
    {
        var (db, conn) = NewDb();
        await using var _ = conn; await using var __ = db;
        var co = await SeedCompany(db, Guid.NewGuid());
        var e = await AddEmployee(db, co, "Active");
        var hr = await AddEmployee(db, co, "Active");
        Guid empUser = Guid.NewGuid(), hrUser = Guid.NewGuid();
        (await db.Employees.FirstAsync(x => x.Id == e.Id)).UserAccountId = empUser;
        (await db.Employees.FirstAsync(x => x.Id == hr.Id)).UserAccountId = hrUser;
        var unpaid = new LeaveType { TenantId = co.TenantId, Code = "UNPAID", NameEn = "Unpaid Leave", Category = "Unpaid", IsActive = true, IsPaid = false };
        db.LeaveTypes.Add(unpaid);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var leaves = await TestApprovalConfig.LeaveServiceAsync(db, co.TenantId, "HR");
        var request = await leaves.SubmitRequestAsync(co.TenantId, new LeaveRequest
        {
            TenantId = co.TenantId, EmployeeId = e.Id, LeaveTypeId = unpaid.Id,
            StartDate = new DateOnly(2026, 1, 25), EndDate = new DateOnly(2026, 2, 5), DayType = "Full", Reason = "family matter",
        }, empUser);
        await leaves.ApproveRequestAsync(co.TenantId, request.Id, hrUser, "HR", "ok");
        db.ChangeTracker.Clear();

        var jan = await AddRun(db, co, 2026, 1);
        await Process(db, co, jan.Id);
        var feb = await AddRun(db, co, 2026, 2);
        await Process(db, co, feb.Id);

        async Task<decimal> LeaveDeduction(Guid runId) => (await db.PayrollDeductions.AsNoTracking()
            .Where(d => d.PayrollRunId == runId && d.EmployeeId == e.Id && d.ComponentCode == "LEAVE").Select(d => d.Amount).ToListAsync()).Sum();
        (await LeaveDeduction(jan.Id)).Should().Be(1_000m, "25-29 January: 5 working days at 200");
        (await LeaveDeduction(feb.Id)).Should().Be(1_000m, "1-5 February: 5 working days at 200");
        (await db.LeavePayrollImpacts.AsNoTracking().Where(i => i.LeaveRequestId == request.Id).Select(i => i.Status).ToListAsync())
            .Should().HaveCount(2).And.OnlyContain(s => s == "Processed", "each month's slice is consumed by its own run, once");
    }

    [Fact]
    public async Task ASoftDeletedLeaverPaidBySettlementRun_IsStillWarnedDeletedButWagesOwed()
    {
        var (db, conn) = NewDb();
        await using var _ = conn; await using var __ = db;
        var co = await SeedCompany(db, Guid.NewGuid());
        await AddEmployee(db, co, "Active");
        var leaver = await AddEmployee(db, co, "Offboarded", deleted: true);
        await AddOffboarding(db, co, leaver, new DateOnly(2026, 2, 20), status: "Completed");
        db.EmployeeFinalSettlements.Add(new EmployeeFinalSettlement
        {
            TenantId = co.TenantId, CompanyId = co.CompanyId, EmployeeId = leaver.Id, EmployeeCode = leaver.EmployeeCode, EmployeeName = leaver.FullName,
            TerminationReason = "Resignation", Status = FinalSettlementStatuses.Approved, GrossPayable = 9_000m,
            LastWorkingDay = new DateOnly(2026, 2, 20),
        });
        await db.SaveChangesAsync();
        var run = await AddRun(db, co, 2026, 3);
        (await db.PayrollRuns.FirstAsync(r => r.Id == run.Id)).SettlesFinalSettlements = true;
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var (_, _, root) = await Preview(db, co, run.Id);

        root.GetProperty("payWarnings").EnumerateArray()
            .Select(w => (w.GetProperty("employeeId").GetInt32(), w.GetProperty("reasonCode").GetString()))
            .Should().Contain((leaver.Id, PayPopulationReasons.WarnDeletedButWagesOwed),
                "a deleted record paid by the settlement rail is still a deleted record paid");
    }

    [Fact]
    public async Task ARehiresOldFinalWageSlipWithNoPaidToDate_DoesNotStopTheCurrentServicePeriod()
    {
        var (db, conn) = NewDb();
        await using var _ = conn; await using var __ = db;
        var co = await SeedCompany(db, Guid.NewGuid());
        await AddEmployee(db, co, "Active");
        var e = await AddEmployee(db, co, "Resigned", joined: new DateTime(2026, 1, 1));   // re-hired 1 January 2026
        // The first service period ended in June 2025; its final-wage slip predates PaidToDate (null).
        var oldRun = new PayrollRun
        {
            TenantId = co.TenantId, CompanyId = co.CompanyId, Year = 2025, Month = 6, Status = "Locked",
            RunType = PayrollRunTypes.Regular, IncludesRecurringPay = true, CreatedByUserId = Maker,
        };
        db.PayrollRuns.Add(oldRun);
        db.PayrollSlips.Add(new PayrollSlip
        {
            TenantId = co.TenantId, CompanyId = co.CompanyId, RunId = oldRun.Id, EmployeeId = e.Id, EmployeeCode = e.EmployeeCode,
            Status = "Processed", IsFinalWageMonth = true, PaidToDate = null, BasicSalary = Basic, GrossSalary = Basic, NetSalary = Basic,
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        await AddOffboarding(db, co, e, new DateOnly(2026, 3, 15));   // the current service period ends 15 March
        var run = await AddRun(db, co, 2026, 3);

        await Process(db, co, run.Id);

        var slip = await Slip(db, run.Id, e.Id);
        slip.Should().NotBeNull("the June 2025 final wage belongs to the earlier service period");
        slip!.BasicSalary.Should().Be(3_000m, "1-15 March = 15/30 of 6,000");
        slip.IsFinalWageMonth.Should().BeTrue();
    }
}

// ── File-scoped service stubs ────────────────────────────────────────────────────────────────────

file static class _PpRules
{
    internal static readonly StubRuleReader Rules = new StubRuleReader()
        .Set("gosi.saudi_employee_rate",            0.09m)
        .Set("gosi.saudi_employer_rate",            0.09m)
        .Set("gosi.saned_rate",                     0.0075m)
        .Set("gosi.expat_occupational_hazard_rate", 0.02m)
        .Set("gosi.covered_wage_ceiling_sar",       45_000m)
        .Set("ot.standard_multiplier",              1.5m)
        .Set("ot.standard_monthly_hours",           240m)
        .Set("lop.monthly_day_divisor",             30m)
        .Set("lop.standard_work_minutes_per_day",   480m);
}

file sealed class _PpScope : Zayra.Api.Application.Common.IDataScopeService
{
    public Task<Zayra.Api.Application.Common.DataScope> ResolveAsync(ClaimsPrincipal caller, Guid tenantId, CancellationToken ct)
        => Task.FromResult(new Zayra.Api.Application.Common.DataScope
        {
            Level = Zayra.Api.Application.Common.DataScopeLevel.Organization,
            AllowedEmployeeIds = null,
        });
}

file sealed class _PpHttp : IHttpContextAccessor
{
    public _PpHttp(HttpContext ctx) => HttpContext = ctx;
    public HttpContext? HttpContext { get; set; }
}

file sealed class _PpNotifications : Zayra.Api.Infrastructure.Notifications.INotificationService
{
    public Task NotifyAsync(Guid tenantId, Guid? userId, string title, string message, string entityName, string? entityId, CancellationToken ct)
        => Task.CompletedTask;
    public Task SendEmailAsync(Guid tenantId, string templateCode, string toAddress, string toName, Dictionary<string, string> variables, CancellationToken ct)
        => Task.CompletedTask;
}

file sealed class _PpPackResolver : ICountryPackResolver
{
    private static readonly KsaDeductionCalculator _calc = new(_PpRules.Rules);
    public IStatutoryDeductionCalculator ResolveDeductionCalculator(string cc, string j)
        => cc is "SAU" or "SA" ? _calc : new DefaultStatutoryDeductionCalculator();
    public IEndOfServiceCalculator ResolveEndOfServiceCalculator(string cc, string j) => new DefaultEndOfServiceCalculator();
    public IWageProtectionExporter ResolveWageProtectionExporter(string cc, string j) => new KsaWageProtectionExporter();
    public INationalizationTracker ResolveNationalizationTracker(string cc, string j) => new DefaultNationalizationTracker();
    public ILocalizationProfile ResolveLocalizationProfile(string cc, string j) => new DefaultLocalizationProfile();
    public ICountryPackDescriptor ResolveDescriptor(string cc, string j) => new DefaultCountryPackDescriptor();
}

file sealed class _PpLetters : ILetterService
{
    public Task<byte[]> GeneratePayslipPdfAsync(PayslipData d, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
    public Task<byte[]> GenerateAppointmentLetterAsync(LetterData d, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
    public Task<byte[]> GenerateExperienceLetterAsync(LetterData d, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
    public Task<byte[]> GenerateOfferLetterAsync(OfferLetterData d, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
}

file sealed class _PpStorage : Zayra.Api.Infrastructure.Documents.IDocumentStorage
{
    public Task<Zayra.Api.Infrastructure.Documents.StoredDocument> SaveAsync(Guid tenantId, IFormFile file, CancellationToken ct)
        => Task.FromResult(new Zayra.Api.Infrastructure.Documents.StoredDocument(file.FileName, file.ContentType, "storage/test", "/tmp/test"));
    public Task<byte[]> GetBytesAsync(Guid tenantId, string storageUrl, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
    public string ResolvePath(string storageUrl) => "/tmp/test";
}
