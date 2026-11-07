using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Controllers;
using Zayra.Api.Infrastructure.Payroll;
using Zayra.Api.Infrastructure.Policy;
using Zayra.Api.Models;
using static Zayra.Api.Tests.OvertimeFixKit;

namespace Zayra.Api.Tests;

/// <summary>
/// OVT-04. Overtime approved after the payroll period of its work date has closed. overtime.late_approval_mode:
/// Refuse (default) answers 409 overtime_period_closed naming the next open period and leaves the request untouched;
/// BackPay (reason required) carries it into the next open run as an adjustment that keeps the original work date
/// and reason, and never touches the closed period.
/// </summary>
public class OvertimeLateApprovalModeTests
{
    private static readonly string[] Adm = { "Admin" };
    private static readonly DateOnly WorkDay = new(2026, 6, 10);

    private static async Task<(World W, OvertimeRequest R)> ArrangeAsync(string? mode, bool closedPeriod = true, bool openRun = true)
    {
        var w = await SeedAsync();
        if (mode is not null)
            w.Db.SystemSettings.Add(new SystemSetting
            {
                TenantId = w.TenantId, Category = PolicyRegistry.StorageCategory,
                SettingKey = PolicyRegistry.OvertimeLateApprovalMode, SettingValue = mode,
            });
        if (closedPeriod)
            w.Db.AttendanceLockPeriods.Add(new AttendanceLockPeriod
            {
                TenantId = w.TenantId, PeriodStart = new DateOnly(2026, 6, 1), PeriodEnd = new DateOnly(2026, 6, 30), LockType = "Payroll", Status = "Locked",
            });
        if (openRun)
            w.Db.PayrollRuns.Add(new PayrollRun { TenantId = w.TenantId, Year = 2026, Month = 7, Status = "Draft" });
        var r = await AddRequestAsync(w, status: "PendingManager", minutes: 120, workDate: WorkDay);
        await w.Db.SaveChangesAsync();
        return (w, r);
    }

    private static Task<ActionResult> Approve(World w, OvertimeRequest r, string? notes = null) =>
        Controller(w, Guid.NewGuid(), Adm).Approve(r.Id, new OvertimeDecisionRequest(0, notes), CancellationToken.None);

    private static (string? Code, string Json) Body(ActionResult result)
    {
        var value = (result as ObjectResult)!.Value!;
        var json = JsonSerializer.Serialize(value);
        using var doc = JsonDocument.Parse(json);
        return (doc.RootElement.TryGetProperty("code", out var c) ? c.GetString() : null, json);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("Refuse")]
    public async Task Refuse_IsTheDefault_409_NamesTheNextOpenPeriod_AndLeavesTheRequestUntouched(string? mode)
    {
        var (w, r) = await ArrangeAsync(mode);

        var result = await Approve(w, r, "late but real");

        result.Should().BeOfType<ConflictObjectResult>();
        var (code, json) = Body(result);
        code.Should().Be("overtime_period_closed");
        json.Should().Contain("2026-07", "the body names the next OPEN payroll period");
        var after = await w.Db.OvertimeRequests.AsNoTracking().SingleAsync(x => x.Id == r.Id);
        after.Status.Should().Be("PendingManager", "it is neither deleted nor altered, so it can be settled later");
        after.ApprovedMinutes.Should().Be(0);
        (await w.Db.OvertimePayrollImpacts.CountAsync()).Should().Be(0);
        (await w.Db.OvertimeCalculations.CountAsync()).Should().Be(0);
        (await w.Db.OvertimeApprovals.CountAsync()).Should().Be(0);
        (await w.Db.PayrollAdjustments.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task OpenPeriod_ApprovesAsBefore_InEitherMode()
    {
        var (w, r) = await ArrangeAsync("Refuse", closedPeriod: false);
        (await Approve(w, r)).Should().BeOfType<OkObjectResult>();
        (await w.Db.OvertimePayrollImpacts.SingleAsync()).Status.Should().Be("PendingPayroll");
        (await w.Db.PayrollAdjustments.CountAsync()).Should().Be(0);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task BackPay_WithoutAReason_IsRefused_AndChangesNothing(string? notes)
    {
        var (w, r) = await ArrangeAsync("BackPay");

        var result = await Approve(w, r, notes);

        result.Should().BeOfType<BadRequestObjectResult>();
        Body(result).Code.Should().Be("overtime_backpay_reason_required");
        (await w.Db.OvertimeRequests.AsNoTracking().SingleAsync()).Status.Should().Be("PendingManager");
        (await w.Db.PayrollAdjustments.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task BackPay_CarriesIntoTheNextOpenRun_KeepingWorkDateReasonAndAudit_WithoutTouchingTheClosedPeriod()
    {
        var (w, r) = await ArrangeAsync("BackPay");

        var result = await Approve(w, r, "Approved late after the June run");

        result.Should().BeOfType<OkObjectResult>();
        var approved = await w.Db.OvertimeRequests.AsNoTracking().SingleAsync();
        approved.Status.Should().Be("Approved");
        approved.WorkDate.Should().Be(WorkDay, "the ORIGINAL work date is kept");

        var julyRun = await w.Db.PayrollRuns.AsNoTracking().SingleAsync();
        var adj = await w.Db.PayrollAdjustments.AsNoTracking().SingleAsync();
        adj.PayrollRunId.Should().Be(julyRun.Id, "carried into the next open run, never the closed one");
        adj.Status.Should().Be("Approved");
        adj.SourceType.Should().Be(PayrollAdjustmentSources.OvertimeBackPay);
        adj.SourceId.Should().Be(r.Id, "one adjustment per request: exactly-once");
        adj.Amount.Should().BeGreaterThan(0m);
        adj.Reason.Should().Contain("2026-06-10").And.Contain("Approved late after the June run");
        using var snap = JsonDocument.Parse(adj.SourceSnapshotJson);
        snap.RootElement.GetProperty("originalWorkDate").GetString().Should().Be("2026-06-10");
        snap.RootElement.GetProperty("reason").GetString().Should().Be("Approved late after the June run");

        // The impact must not be picked up again (nor later converted to leave and paid twice).
        (await w.Db.OvertimePayrollImpacts.SingleAsync()).Status.Should().Be("CarriedForward");
        (await w.Db.OvertimeAuditLogs.AnyAsync(x => x.Action == "overtime.request.backpay_carried" && x.MetadataJson.Contains("Approved late after the June run")))
            .Should().BeTrue();
        (await w.Db.AttendanceLockPeriods.CountAsync()).Should().Be(1, "the closed period's lock is untouched");
        (await w.Db.AttendanceLockPeriods.AsNoTracking().SingleAsync()).Status.Should().Be("Locked");
    }

    [Fact]
    public async Task BackPay_WithNoOpenRunToCarryInto_Refuses409_AndLeavesTheRequestPending()
    {
        var (w, r) = await ArrangeAsync("BackPay", openRun: false);

        var result = await Approve(w, r, "late");

        result.Should().BeOfType<ConflictObjectResult>();
        var (code, json) = Body(result);
        code.Should().Be("overtime_backpay_no_open_run");
        json.Should().Contain("2026-07");
        (await w.Db.OvertimeRequests.AsNoTracking().SingleAsync()).Status.Should().Be("PendingManager");
        (await w.Db.PayrollAdjustments.CountAsync()).Should().Be(0);
        (await w.Db.OvertimePayrollImpacts.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task ACarriedForwardRequest_CannotAlsoBeConvertedToCompOff()
    {
        var (w, r) = await ArrangeAsync("BackPay");
        (await Approve(w, r, "late")).Should().BeOfType<OkObjectResult>();

        var result = await Controller(w, Guid.NewGuid(), Adm).CreateCompOffConversion(new CompOffConversionRequest(r.Id, 0.25m), CancellationToken.None);

        result.Result.Should().BeOfType<ConflictObjectResult>("the cash is already committed to the next run");
        (await w.Db.CompOffCredits.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Refuse_NamesTheFirstMonthThatIsNotAlsoClosed()
    {
        var (w, r) = await ArrangeAsync(null);
        w.Db.AttendanceLockPeriods.Add(new AttendanceLockPeriod { TenantId = w.TenantId, PeriodStart = new DateOnly(2026, 7, 1), PeriodEnd = new DateOnly(2026, 7, 31), LockType = "Payroll", Status = "Locked" });
        await w.Db.SaveChangesAsync();

        Body(await Approve(w, r)).Json.Should().Contain("2026-08");
    }

    private static Company NewCompany(Guid tenantId, bool active = true) => new()
    {
        TenantId = tenantId, LegalNameEn = $"Co {Guid.NewGuid():N}", CountryCode = "SAU", Jurisdiction = "KSA-mainland",
        RegistrationNumber = $"REG-{Guid.NewGuid():N}", DefaultCurrency = "SAR", IsActive = active,
    };

    [Fact]
    public async Task BackPay_LegacyEmployeeWithNoCompany_InASingleCompanyTenant_FindsTheDraftRunOfThatCompany()
    {
        var (w, r) = await ArrangeAsync("BackPay", openRun: false);
        var company = NewCompany(w.TenantId);
        w.Db.Companies.Add(company);
        w.Db.PayrollRuns.Add(new PayrollRun { TenantId = w.TenantId, CompanyId = company.Id, Year = 2026, Month = 7, Status = "Draft" });
        await w.Db.SaveChangesAsync();
        r.CompanyId.Should().BeNull("premise: a legacy request");
        w.Subject.CompanyId.Should().BeNull("premise: a legacy employee");

        var result = await Approve(w, r, "late");

        result.Should().BeOfType<OkObjectResult>("payroll treats a legacy employee as the tenant's only company's");
        var julyRun = await w.Db.PayrollRuns.AsNoTracking().SingleAsync();
        (await w.Db.PayrollAdjustments.AsNoTracking().SingleAsync()).PayrollRunId.Should().Be(julyRun.Id);
    }

    [Fact]
    public async Task BackPay_LegacyEmployeeWithNoCompany_InAMultiCompanyTenant_RefusesToGuess()
    {
        var (w, r) = await ArrangeAsync("BackPay", openRun: false);
        var a = NewCompany(w.TenantId);
        w.Db.Companies.AddRange(a, NewCompany(w.TenantId));
        w.Db.PayrollRuns.Add(new PayrollRun { TenantId = w.TenantId, CompanyId = a.Id, Year = 2026, Month = 7, Status = "Draft" });
        await w.Db.SaveChangesAsync();

        var result = await Approve(w, r, "late");

        result.Should().BeOfType<ConflictObjectResult>();
        Body(result).Code.Should().Be("overtime_backpay_no_open_run");
        (await w.Db.PayrollAdjustments.CountAsync()).Should().Be(0);
        (await w.Db.OvertimeRequests.AsNoTracking().SingleAsync()).Status.Should().Be("PendingManager");
    }

    [Fact]
    public async Task BackPay_CarriesIntoADraftReplacementRun_WhenTheRegularRunWasVoided()
    {
        var (w, r) = await ArrangeAsync("BackPay", openRun: false);
        w.Db.PayrollRuns.Add(new PayrollRun { TenantId = w.TenantId, Year = 2026, Month = 7, Status = "Voided", RunType = PayrollRunTypes.Regular });
        var replacement = new PayrollRun { TenantId = w.TenantId, Year = 2026, Month = 7, Status = "Draft", RunType = PayrollRunTypes.Replacement };
        w.Db.PayrollRuns.Add(replacement);
        await w.Db.SaveChangesAsync();

        (await Approve(w, r, "late")).Should().BeOfType<OkObjectResult>();

        (await w.Db.PayrollAdjustments.AsNoTracking().SingleAsync()).PayrollRunId.Should().Be(replacement.Id);
    }

    [Fact]
    public async Task BackPay_NeverCarriesIntoASupplementalRun()
    {
        var (w, r) = await ArrangeAsync("BackPay", openRun: false);
        w.Db.PayrollRuns.Add(new PayrollRun { TenantId = w.TenantId, Year = 2026, Month = 7, Status = "Draft", RunType = PayrollRunTypes.Supplementary, IncludesRecurringPay = false });
        await w.Db.SaveChangesAsync();

        var result = await Approve(w, r, "late");

        result.Should().BeOfType<ConflictObjectResult>();
        Body(result).Code.Should().Be("overtime_backpay_no_open_run");
        (await w.Db.PayrollAdjustments.CountAsync()).Should().Be(0);
    }
}
