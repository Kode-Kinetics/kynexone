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
using Zayra.Api.Infrastructure.CountryPack;
using Zayra.Api.Infrastructure.CountryPack.Ksa;
using Zayra.Api.Infrastructure.Documents;
using Zayra.Api.Infrastructure.Documents.Letters;
using Zayra.Api.Infrastructure.Notifications;
using Zayra.Api.Infrastructure.Organization;
using Zayra.Api.Infrastructure.Payroll;
using Zayra.Api.Infrastructure.Policy;
using Zayra.Api.Infrastructure.Seed;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// Rule 11b (OVERTIME_PRIOR_PERIOD_UNPAID, #324) reconciled with overtime.late_approval_mode (OVT-04, the policy wave).
///
/// <para>One behaviour: BackPay means the next run that pays recurring items pays prior-period overtime as an "Overtime Back Pay"
/// line (EARN:OVERTIME) and takes the impact out of PendingPayroll in the same transaction, so rule 11b does not flag it and a later
/// run cannot pay it again. Refuse (the default) pays nothing and rule 11b flags it, naming the setting.</para>
/// </summary>
public class OvertimeLateSettlementFinderTests
{
    private static readonly DateOnly PeriodStart = new(2026, 6, 1);
    private static readonly DateOnly MayDay = new(2026, 5, 28);

    private sealed class World
    {
        public ZayraDbContext Db { get; } = new(new DbContextOptionsBuilder<ZayraDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        public Guid TenantId { get; } = Guid.NewGuid();
        public Guid CompanyId { get; } = Guid.NewGuid();
        public int EmployeeId { get; } = 7;
    }

    private static async Task<(OvertimeRequest Request, OvertimePayrollImpact Impact)> AddAsync(
        World w, decimal amount = 150m, string impactStatus = "PendingPayroll", Guid? companyId = null)
    {
        var request = new OvertimeRequest
        {
            TenantId = w.TenantId, CompanyId = companyId ?? w.CompanyId, EmployeeId = w.EmployeeId, EmployeeName = "E", WorkDate = MayDay,
            StartTimeUtc = MayDay.ToDateTime(new TimeOnly(17, 0), DateTimeKind.Utc), EndTimeUtc = MayDay.ToDateTime(new TimeOnly(19, 0), DateTimeKind.Utc),
            RequestedMinutes = 120, ApprovedMinutes = 120, Status = "Approved",
        };
        w.Db.OvertimeRequests.Add(request);
        var impact = new OvertimePayrollImpact
        {
            TenantId = w.TenantId, OvertimeRequestId = request.Id, EmployeeId = w.EmployeeId, Minutes = 120, Amount = amount,
            Status = impactStatus, ApprovedMultiplier = 1.5m,
        };
        w.Db.OvertimePayrollImpacts.Add(impact);
        w.Db.PayrollRuns.Add(new PayrollRun { TenantId = w.TenantId, CompanyId = w.CompanyId, Year = 2026, Month = 5, Status = "Locked" });
        await w.Db.SaveChangesAsync();
        return (request, impact);
    }

    private static Task<IReadOnlyList<PriorPeriodOvertime>> Flagged(World w, string mode, bool runPays = true, Guid? runId = null) =>
        PriorPeriodOvertimeFinder.FindAsync(w.Db, w.TenantId, w.CompanyId, new[] { w.EmployeeId }, PeriodStart, CancellationToken.None, mode, runPays, runId);

    private static Task<IReadOnlyList<PriorPeriodOvertime>> Settled(World w, string mode, bool runPays = true, Guid? runId = null) =>
        PriorPeriodOvertimeFinder.FindSettleableAsync(w.Db, w.TenantId, w.CompanyId, new[] { w.EmployeeId }, PeriodStart, CancellationToken.None, mode, runPays, runId);

    [Fact]
    public async Task BackPay_RunThatPaysRecurring_SettlesTheOvertime_SoItIsNotFlagged()
    {
        var w = new World();
        var (request, impact) = await AddAsync(w);

        (await Flagged(w, OvertimeLateSettlement.BackPay)).Should().BeEmpty("the mode pays it in this run");
        var settled = await Settled(w, OvertimeLateSettlement.BackPay);
        settled.Should().ContainSingle().Which.Should().Match<PriorPeriodOvertime>(s =>
            s.ImpactId == impact.Id && s.OvertimeRequestId == request.Id && s.Amount == 150m && s.Minutes == 120);
    }

    [Theory]
    [InlineData("Refuse")]
    [InlineData("")]
    [InlineData("hold")]
    public async Task AnyModeOtherThanBackPay_SettlesNothing_AndFlagsIt(string mode)
    {
        var w = new World();
        await AddAsync(w);

        (await Flagged(w, mode)).Should().ContainSingle();
        (await Settled(w, mode)).Should().BeEmpty();
    }

    [Fact]
    public async Task BackPay_ButTheRunPaysNoRecurringItems_SettlesNothing_AndFlagsIt()
    {
        var w = new World();
        await AddAsync(w);

        (await Flagged(w, OvertimeLateSettlement.BackPay, runPays: false)).Should().ContainSingle("a supplemental run does not pay overtime");
        (await Settled(w, OvertimeLateSettlement.BackPay, runPays: false)).Should().BeEmpty();
    }

    [Fact]
    public async Task BackPay_ButNothingToPay_StaysFlagged_ItIsNotSilentlyDropped()
    {
        var w = new World();
        await AddAsync(w, amount: 0m);

        (await Flagged(w, OvertimeLateSettlement.BackPay)).Should().ContainSingle();
        (await Settled(w, OvertimeLateSettlement.BackPay)).Should().BeEmpty();
    }

    [Theory]
    [InlineData("CarriedForward")]
    [InlineData("Processed")]
    public async Task OvertimeAlreadySettledAnotherWay_IsNeverFlaggedNorSettled_InEitherMode(string impactStatus)
    {
        var w = new World();
        await AddAsync(w, impactStatus: impactStatus);

        foreach (var mode in new[] { OvertimeLateSettlement.Refuse, OvertimeLateSettlement.BackPay })
        {
            (await Flagged(w, mode)).Should().BeEmpty();
            (await Settled(w, mode)).Should().BeEmpty();
        }
    }

    [Fact]
    public async Task BackPay_WithABackPayAdjustmentAlreadyOnAnotherLiveRun_IsNotPaidAgain_AndStaysFlagged()
    {
        var w = new World();
        var (request, _) = await AddAsync(w);
        var other = new PayrollRun { TenantId = w.TenantId, CompanyId = w.CompanyId, Year = 2026, Month = 6, Status = "Locked", RunType = "Off-Cycle" };
        w.Db.PayrollRuns.Add(other);
        w.Db.PayrollAdjustments.Add(new PayrollAdjustment
        {
            TenantId = w.TenantId, PayrollRunId = other.Id, EmployeeId = w.EmployeeId, AdjustmentType = OvertimeLateSettlement.AdjustmentType,
            Amount = 150m, Status = "Processed", SourceType = PayrollAdjustmentSources.OvertimeBackPay, SourceId = request.Id,
        });
        await w.Db.SaveChangesAsync();

        (await Settled(w, OvertimeLateSettlement.BackPay)).Should().BeEmpty("it is already attached to another live run: paying it here is a second payment");
        (await Flagged(w, OvertimeLateSettlement.BackPay)).Should().ContainSingle();
    }

    [Fact]
    public async Task BackPay_WithABackPayAdjustmentOnAVoidedRun_OrOnThisRun_IsSettleable()
    {
        var w = new World();
        var (request, _) = await AddAsync(w);
        var voided = new PayrollRun { TenantId = w.TenantId, CompanyId = w.CompanyId, Year = 2026, Month = 6, Status = "Voided" };
        w.Db.PayrollRuns.Add(voided);
        var adj = new PayrollAdjustment
        {
            TenantId = w.TenantId, PayrollRunId = voided.Id, EmployeeId = w.EmployeeId, AdjustmentType = OvertimeLateSettlement.AdjustmentType,
            Amount = 150m, Status = "Approved", SourceType = PayrollAdjustmentSources.OvertimeBackPay, SourceId = request.Id,
        };
        w.Db.PayrollAdjustments.Add(adj);
        await w.Db.SaveChangesAsync();

        (await Settled(w, OvertimeLateSettlement.BackPay)).Should().ContainSingle("a voided run released it");

        var thisRun = new PayrollRun { TenantId = w.TenantId, CompanyId = w.CompanyId, Year = 2026, Month = 6, Status = "Draft" };
        w.Db.PayrollRuns.Add(thisRun);
        adj.PayrollRunId = thisRun.Id;
        await w.Db.SaveChangesAsync();
        (await Settled(w, OvertimeLateSettlement.BackPay, runId: thisRun.Id)).Should().ContainSingle("a re-process reuses its own row");
        (await Settled(w, OvertimeLateSettlement.BackPay, runId: Guid.NewGuid())).Should().BeEmpty("a different live run may not take it");
    }

    [Fact]
    public async Task AnotherCompanysOvertime_IsNeitherFlaggedNorSettled_ByThisCompanysRun()
    {
        var w = new World();
        await AddAsync(w, companyId: Guid.NewGuid());

        (await Flagged(w, OvertimeLateSettlement.BackPay)).Should().BeEmpty();
        (await Settled(w, OvertimeLateSettlement.BackPay)).Should().BeEmpty();
    }
}

/// <summary>The same reconciliation through the real Process / Validate pipeline (SQLite, no container), over consecutive months.</summary>
public class OvertimeLateSettlementRunTests
{
    private static readonly Guid Preparer = Guid.NewGuid();
    private const string Code = "OVERTIME_PRIOR_PERIOD_UNPAID";
    private const string BackPayCode = "ADJ_OVERTIME_BACK_PAY";

    private static (ZayraDbContext Db, SqliteConnection Conn) NewDb()
    {
        var conn = new SqliteConnection("DataSource=:memory:");
        conn.Open();
        var db = new ZayraDbContext(new DbContextOptionsBuilder<ZayraDbContext>().UseSqlite(conn).Options);
        db.Database.EnsureCreated();
        return (db, conn);
    }

    private static PayrollController Payroll(ZayraDbContext db, Guid tenantId)
    {
        var rules = new StubRuleReader()
            .Set("gosi.saudi_employee_rate", 0.09m).Set("gosi.saudi_employer_rate", 0.09m).Set("gosi.saned_rate", 0.0075m)
            .Set("gosi.expat_occupational_hazard_rate", 0.02m).Set("gosi.covered_wage_ceiling_sar", 45_000m)
            .Set("ot.standard_multiplier", 1.5m).Set("ot.standard_monthly_hours", 240m)
            .Set("lop.monthly_day_divisor", 30m).Set("lop.standard_work_minutes_per_day", 480m);
        var claims = new List<Claim>
        {
            new("tenant_id", tenantId.ToString()),
            new(ClaimTypes.NameIdentifier, Preparer.ToString()), new(ClaimTypes.Name, "preparer"), new(ClaimTypes.Role, "Payroll Officer"),
            new("permission", "payroll.export"), new("permission", "payroll.lock"),
            new("permission", "payroll.write"), new("permission", "payroll.approve"),
        };
        var httpCtx = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "test")) };
        var ctrl = new PayrollController(db, new LateScope(), new LateHttp(httpCtx), new _LateNullNotifications(),
            new _LateKsaPackResolver(rules), rules, new _LateNullLetterService(), new NullDocumentStorage(), new PdfRenderGate(4));
        ctrl.ControllerContext = new ControllerContext { HttpContext = httpCtx };
        return ctrl;
    }

    private sealed record Seed(Guid TenantId, Guid CompanyId, Employee Emp, PayrollRun June, Guid RequestId, Guid ImpactId);

    /// <summary>
    /// May's run is Locked (it consumed May); an overtime request from 28 May was approved afterwards and is still PendingPayroll.
    /// June is the open Draft run.
    /// </summary>
    private static async Task<Seed> ArrangeAsync(ZayraDbContext db, string? mode, decimal amount = 150m, string impactStatus = "PendingPayroll")
    {
        var tenantId = Guid.NewGuid();
        var company = new Company
        {
            TenantId = tenantId, LegalNameEn = "Late OT Co", CountryCode = "SAU", Jurisdiction = "KSA-mainland",
            RegistrationNumber = $"REG-{Guid.NewGuid():N}", DefaultCurrency = "SAR", IsActive = true,
        };
        db.Companies.Add(company);
        var structure = new SalaryStructure { TenantId = tenantId, Code = "STR", Name = "Base", Currency = "SAR", EffectiveDate = new DateOnly(2025, 1, 1), IsActive = true };
        db.SalaryStructures.Add(structure);
        await db.SaveChangesAsync();
        var emp = new Employee
        {
            TenantId = tenantId, CompanyId = company.Id, EmployeeCode = "E-LATE-1", FullName = "Late OT Employee", Nationality = "SAU",
            Status = "Active", JoiningDate = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc), ContractType = "Indefinite",
        };
        db.Employees.Add(emp);
        await db.SaveChangesAsync();
        db.EmployeeSalaryStructures.Add(new EmployeeSalaryStructure
        {
            TenantId = tenantId, EmployeeId = emp.Id, SalaryStructureId = structure.Id, BasicSalary = 12_000m, HousingAllowance = 3_000m,
            EffectiveDate = new DateOnly(2024, 1, 1), IsActive = true,
        });
        db.EmployeePayrollProfiles.Add(new EmployeePayrollProfile
        {
            TenantId = tenantId, EmployeeId = emp.Id, Iban = "SA4420000001234567891234", MolId = "MOL-LATE-1", SalaryCurrency = "SAR",
        });
        if (mode is not null)
            db.SystemSettings.Add(new SystemSetting
            {
                TenantId = tenantId, Category = PolicyRegistry.StorageCategory, SettingKey = PolicyRegistry.OvertimeLateApprovalMode, SettingValue = mode,
            });
        var june = new PayrollRun { TenantId = tenantId, CompanyId = company.Id, Year = 2026, Month = 6, Status = "Draft", CreatedByUserId = Preparer };
        db.PayrollRuns.Add(june);
        db.PayrollRuns.Add(new PayrollRun { TenantId = tenantId, CompanyId = company.Id, Year = 2026, Month = 5, Status = "Locked" });
        await db.SaveChangesAsync();

        var workDate = new DateOnly(2026, 5, 28);
        var start = workDate.ToDateTime(new TimeOnly(17, 0), DateTimeKind.Utc);
        var request = new OvertimeRequest
        {
            TenantId = tenantId, CompanyId = company.Id, EmployeeId = emp.Id, EmployeeName = emp.FullName, WorkDate = workDate,
            StartTimeUtc = start, EndTimeUtc = start.AddHours(2), RequestedMinutes = 120, ApprovedMinutes = 120, Status = "Approved",
        };
        db.OvertimeRequests.Add(request);
        await db.SaveChangesAsync();
        var impact = new OvertimePayrollImpact
        {
            TenantId = tenantId, OvertimeRequestId = request.Id, EmployeeId = emp.Id, Minutes = 120, Amount = amount, Status = impactStatus, ApprovedMultiplier = 1.5m,
        };
        db.OvertimePayrollImpacts.Add(impact);
        await db.SaveChangesAsync();
        return new Seed(tenantId, company.Id, emp, june, request.Id, impact.Id);
    }

    private static async Task<PayrollRun> AddJulyAsync(ZayraDbContext db, Seed s)
    {
        var july = new PayrollRun { TenantId = s.TenantId, CompanyId = s.CompanyId, Year = 2026, Month = 7, Status = "Draft", CreatedByUserId = Preparer };
        db.PayrollRuns.Add(july);
        await db.SaveChangesAsync();
        return july;
    }

    private static async Task ProcessOkAsync(ZayraDbContext db, Seed s, PayrollRun run)
    {
        (await Payroll(db, s.TenantId).Process(run.Id, CancellationToken.None)).Should().BeOfType<OkObjectResult>();
        db.ChangeTracker.Clear();
    }

    private static Task<List<PayrollEarning>> BackPayLines(ZayraDbContext db, Guid? runId = null) =>
        db.PayrollEarnings.AsNoTracking().Where(e => e.ComponentCode == BackPayCode && (runId == null || e.PayrollRunId == runId)).ToListAsync();

    [Fact]
    public async Task BackPay_PaysTheLateOvertimeOnce_AcrossTwoConsecutiveMonths_AndRule11bNeverSeesItAgain()
    {
        var (db, conn) = NewDb();
        await using var _ = conn; await using var __ = db;
        var s = await ArrangeAsync(db, OvertimeLateSettlement.BackPay);

        // June: the late overtime is paid in this run, as an overtime back-pay line on the overtime expense driver.
        await ProcessOkAsync(db, s, s.June);
        (await db.PayrollValidationResults.AsNoTracking().AnyAsync(v => v.PayrollRunId == s.June.Id && v.Code == Code))
            .Should().BeFalse("the mode pays it in this run, so rule 11b has nothing to say");
        var juneLines = await BackPayLines(db, s.June.Id);
        juneLines.Should().ContainSingle();
        juneLines[0].Amount.Should().Be(150m);
        juneLines[0].EmployeeId.Should().Be(s.Emp.Id);
        PayrollRunProcessor.EarningDriverKey(juneLines[0].ComponentCode, juneLines[0].Source).Should().Be("EARN:OVERTIME");

        var impact = await db.OvertimePayrollImpacts.AsNoTracking().SingleAsync(i => i.Id == s.ImpactId);
        impact.Status.Should().Be("Processed", "settled in the same transaction as the run's processing");
        impact.PayrollRunId.Should().Be(s.June.Id);
        var adj = await db.PayrollAdjustments.AsNoTracking().SingleAsync(a => a.TenantId == s.TenantId);
        adj.SourceType.Should().Be(PayrollAdjustmentSources.OvertimeBackPay);
        adj.SourceId.Should().Be(s.RequestId, "one back-pay adjustment per request");
        adj.PayrollRunId.Should().Be(s.June.Id);
        adj.Status.Should().Be("Processed");
        (await db.AuditLogs.AsNoTracking().AnyAsync(a => a.TenantId == s.TenantId && a.Action == "overtime.request.backpay_settled_by_run"
            && a.EntityId == s.RequestId.ToString())).Should().BeTrue();

        // /validate of the processed run keeps agreeing with Process.
        (await Payroll(db, s.TenantId).Validate(s.June.Id, CancellationToken.None)).Should().BeOfType<OkObjectResult>();
        db.ChangeTracker.Clear();
        (await db.PayrollValidationResults.AsNoTracking().AnyAsync(v => v.PayrollRunId == s.June.Id && v.Code == Code)).Should().BeFalse();

        // July: nothing is paid or flagged a second time.
        var july = await AddJulyAsync(db, s);
        await ProcessOkAsync(db, s, july);
        (await BackPayLines(db, july.Id)).Should().BeEmpty("the impact left PendingPayroll in June");
        (await BackPayLines(db)).Should().ContainSingle("exactly one back-pay line exists across both months");
        (await db.PayrollAdjustments.AsNoTracking().CountAsync(a => a.TenantId == s.TenantId)).Should().Be(1);
        (await db.PayrollValidationResults.AsNoTracking().AnyAsync(v => v.PayrollRunId == july.Id && v.Code == Code)).Should().BeFalse();
        (await db.OvertimePayrollImpacts.AsNoTracking().SingleAsync(i => i.Id == s.ImpactId)).PayrollRunId.Should().Be(s.June.Id);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("Refuse")]
    public async Task Refuse_PaysNothing_FlagsIt_AndNamesTheSetting_InEveryLaterRun(string? mode)
    {
        var (db, conn) = NewDb();
        await using var _ = conn; await using var __ = db;
        var s = await ArrangeAsync(db, mode);

        await ProcessOkAsync(db, s, s.June);
        var finding = await db.PayrollValidationResults.AsNoTracking().SingleAsync(v => v.PayrollRunId == s.June.Id && v.Code == Code);
        finding.Severity.Should().Be("Error");
        finding.Message.Should().Contain("overtime.late_approval_mode").And.Contain("Refuse")
            .And.Contain("BackPay", "it tells Finance where the behaviour lives and what changes it");
        finding.Message.ToLowerInvariant().Should().NotContain("adjustment");
        (await BackPayLines(db)).Should().BeEmpty();
        (await db.OvertimePayrollImpacts.AsNoTracking().SingleAsync(i => i.Id == s.ImpactId)).Status.Should().Be("PendingPayroll");
        (await db.PayrollAdjustments.AsNoTracking().CountAsync(a => a.TenantId == s.TenantId)).Should().Be(0);

        var july = await AddJulyAsync(db, s);
        await ProcessOkAsync(db, s, july);
        (await db.PayrollValidationResults.AsNoTracking().AnyAsync(v => v.PayrollRunId == july.Id && v.Code == Code))
            .Should().BeTrue("still unpaid, so still named");
        (await BackPayLines(db)).Should().BeEmpty();
    }

    [Fact]
    public async Task BackPay_Validate_DoesNotFlag_AndDoesNotPayAnything()
    {
        var (db, conn) = NewDb();
        await using var _ = conn; await using var __ = db;
        var s = await ArrangeAsync(db, OvertimeLateSettlement.Refuse);
        await ProcessOkAsync(db, s, s.June); // Refuse: flagged, nothing paid
        (await db.PayrollValidationResults.AsNoTracking().AnyAsync(v => v.PayrollRunId == s.June.Id && v.Code == Code)).Should().BeTrue();

        // Finance switches the company to BackPay; /validate of the processed run now agrees the next run pays it.
        var setting = await db.SystemSettings.SingleAsync(x => x.TenantId == s.TenantId && x.SettingKey == PolicyRegistry.OvertimeLateApprovalMode);
        setting.SettingValue = OvertimeLateSettlement.BackPay;
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        (await Payroll(db, s.TenantId).Validate(s.June.Id, CancellationToken.None)).Should().BeOfType<OkObjectResult>();
        db.ChangeTracker.Clear();

        (await db.PayrollValidationResults.AsNoTracking().AnyAsync(v => v.PayrollRunId == s.June.Id && v.Code == Code))
            .Should().BeFalse("the mode pays it in the next run that pays recurring items, so /validate does not call it unpaid");
        (await db.OvertimePayrollImpacts.AsNoTracking().SingleAsync(i => i.Id == s.ImpactId)).Status.Should().Be("PendingPayroll", "/validate is read-only");
        (await db.PayrollAdjustments.AsNoTracking().CountAsync(a => a.TenantId == s.TenantId)).Should().Be(0);
    }

    [Fact]
    public async Task BackPay_WithNothingToPay_IsStillFlagged_AndNamesTheMode()
    {
        var (db, conn) = NewDb();
        await using var _ = conn; await using var __ = db;
        var s = await ArrangeAsync(db, OvertimeLateSettlement.BackPay, amount: 0m);

        await ProcessOkAsync(db, s, s.June);

        var finding = await db.PayrollValidationResults.AsNoTracking().SingleAsync(v => v.PayrollRunId == s.June.Id && v.Code == Code);
        finding.Message.Should().Contain("overtime.late_approval_mode").And.Contain("BackPay");
        (await BackPayLines(db)).Should().BeEmpty();
        (await db.OvertimePayrollImpacts.AsNoTracking().SingleAsync(i => i.Id == s.ImpactId)).Status.Should().Be("PendingPayroll");
    }

    [Fact]
    public async Task BackPay_OvertimeAlreadyCarriedForwardAtApproval_IsPaidByItsAdjustmentOnly_NeverTwice()
    {
        var (db, conn) = NewDb();
        await using var _ = conn; await using var __ = db;
        var s = await ArrangeAsync(db, OvertimeLateSettlement.BackPay, impactStatus: "CarriedForward");
        // What the approval endpoint wrote: an Approved back-pay adjustment on the open June run.
        db.PayrollAdjustments.Add(new PayrollAdjustment
        {
            TenantId = s.TenantId, PayrollRunId = s.June.Id, EmployeeId = s.Emp.Id, AdjustmentType = OvertimeLateSettlement.AdjustmentType,
            Amount = 150m, Status = "Approved", Reason = "Approved late", SourceType = PayrollAdjustmentSources.OvertimeBackPay, SourceId = s.RequestId,
        });
        await db.SaveChangesAsync();

        await ProcessOkAsync(db, s, s.June);

        (await BackPayLines(db, s.June.Id)).Should().ContainSingle("the approval-time adjustment pays it; the run does not add a second line");
        (await db.PayrollAdjustments.AsNoTracking().CountAsync(a => a.TenantId == s.TenantId)).Should().Be(1);
        (await db.OvertimePayrollImpacts.AsNoTracking().SingleAsync(i => i.Id == s.ImpactId)).Status.Should().Be("CarriedForward");
        (await db.PayrollValidationResults.AsNoTracking().AnyAsync(v => v.PayrollRunId == s.June.Id && v.Code == Code)).Should().BeFalse();
    }

    [Fact]
    public async Task BackPay_WithTheBackPayAdjustmentAlreadyOnAnotherLiveRun_PaysNothingHere_AndFlagsIt()
    {
        var (db, conn) = NewDb();
        await using var _ = conn; await using var __ = db;
        var s = await ArrangeAsync(db, OvertimeLateSettlement.BackPay);
        var other = new PayrollRun { TenantId = s.TenantId, CompanyId = s.CompanyId, Year = 2026, Month = 6, Status = "Locked", RunType = "Off-Cycle", IncludesRecurringPay = false };
        db.PayrollRuns.Add(other);
        db.PayrollAdjustments.Add(new PayrollAdjustment
        {
            TenantId = s.TenantId, PayrollRunId = other.Id, EmployeeId = s.Emp.Id, AdjustmentType = OvertimeLateSettlement.AdjustmentType,
            Amount = 150m, Status = "Processed", SourceType = PayrollAdjustmentSources.OvertimeBackPay, SourceId = s.RequestId,
        });
        await db.SaveChangesAsync();

        await ProcessOkAsync(db, s, s.June);

        (await BackPayLines(db, s.June.Id)).Should().BeEmpty("it is already attached to another live run");
        (await db.PayrollValidationResults.AsNoTracking().AnyAsync(v => v.PayrollRunId == s.June.Id && v.Code == Code)).Should().BeTrue();
        (await db.OvertimePayrollImpacts.AsNoTracking().SingleAsync(i => i.Id == s.ImpactId)).Status.Should().Be("PendingPayroll");
    }

    private sealed class LateScope : IDataScopeService
    {
        public Task<DataScope> ResolveAsync(ClaimsPrincipal caller, Guid tenantId, CancellationToken ct) =>
            Task.FromResult(new DataScope { Level = DataScopeLevel.Organization, AllowedEmployeeIds = null });
    }

    private sealed class LateHttp(HttpContext ctx) : IHttpContextAccessor
    {
        public HttpContext? HttpContext { get; set; } = ctx;
    }
}

file sealed class _LateNullNotifications : INotificationService
{
    public Task NotifyAsync(Guid t, Guid? u, string title, string msg, string en, string? eid, CancellationToken ct) => Task.CompletedTask;
    public Task SendEmailAsync(Guid t, string code, string to, string name, Dictionary<string, string> vars, CancellationToken ct) => Task.CompletedTask;
}

file sealed class _LateKsaPackResolver(StubRuleReader rules) : ICountryPackResolver
{
    public IStatutoryDeductionCalculator ResolveDeductionCalculator(string cc, string j) => new KsaDeductionCalculator(rules);
    public IEndOfServiceCalculator ResolveEndOfServiceCalculator(string cc, string j) => new DefaultEndOfServiceCalculator();
    public IWageProtectionExporter ResolveWageProtectionExporter(string cc, string j) => new DefaultWageProtectionExporter();
    public INationalizationTracker ResolveNationalizationTracker(string cc, string j) => new DefaultNationalizationTracker();
    public ILocalizationProfile ResolveLocalizationProfile(string cc, string j) => new DefaultLocalizationProfile();
    public ICountryPackDescriptor ResolveDescriptor(string cc, string j) => new DefaultCountryPackDescriptor();
}

file sealed class _LateNullLetterService : ILetterService
{
    public Task<byte[]> GeneratePayslipPdfAsync(PayslipData d, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
    public Task<byte[]> GenerateAppointmentLetterAsync(LetterData d, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
    public Task<byte[]> GenerateExperienceLetterAsync(LetterData d, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
    public Task<byte[]> GenerateOfferLetterAsync(OfferLetterData d, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
}
