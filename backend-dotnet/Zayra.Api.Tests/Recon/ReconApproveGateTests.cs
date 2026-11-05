using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Controllers;
using Zayra.Api.Infrastructure.Payroll.ManpowerRecon;
using Zayra.Api.Models;

namespace Zayra.Api.Tests.Recon;

/// <summary>
/// Approve is refused without a recorded reconciliation sign-off for the CURRENT figures - at both approval levels, for every
/// role including Admin - unless the company turned the rule off, in which case the approval goes through and says so in the audit.
/// </summary>
public class ReconApproveGateTests
{
    private static readonly string[] ApprovePermissions = { "payroll.read", "payroll.approve" };

    private static async Task<ManpowerReconServiceTests.Kit> ReadyRunAsync(string currentStatus = "Processed")
    {
        var k = await ManpowerReconServiceTests.Kit.CreateAsync();
        k.AddEmployee(1); k.AddSlip(k.Prior, 1, 1000); k.AddSlip(k.Current, 1, 1000);
        k.Current.Status = currentStatus;
        await k.Db.SaveChangesAsync();
        return k;
    }

    private static PayrollRunReconSignoff Signoff(ManpowerReconServiceTests.Kit k, int? version = null, Guid? priorId = null, int? priorVersion = null)
    {
        var s = new PayrollRunReconSignoff
        {
            TenantId = k.TenantId, CompanyId = k.CompanyId, PayrollRunId = k.Current.Id, ProcessVersion = version ?? k.Current.ProcessVersion,
            PriorRunId = priorId, PriorProcessVersion = priorVersion, SnapshotHash = new string('a', 64), SnapshotJson = "{}",
            SignedOffByUserId = Guid.NewGuid(), SignedOffByName = "Reviewer", Status = ReconSignoffStatuses.Active,
        };
        k.Db.PayrollRunReconSignoffs.Add(s);
        return s;
    }

    private static Task<IActionResult> Approve(ManpowerReconServiceTests.Kit k, string role) =>
        ReconHarness.Payroll(k.Db, k.TenantId, role, ApprovePermissions).Approve(k.Current.Id, new PayrollDecisionRequest("Looks right"), default);

    private static JsonElement Conflict(IActionResult r)
    {
        r.Should().BeOfType<ConflictObjectResult>();
        return ReconHarness.Json(((ObjectResult)r).Value);
    }

    [Theory]
    [InlineData("Admin", "Processed")]
    [InlineData("Finance Approver", "Processed")]
    [InlineData("Finance Controller", "PendingFinanceReview")]
    [InlineData("HR Manager", "Processed")]
    [InlineData("Payroll Manager", "Processed")]
    public async Task WithoutASignoff_EveryRoleAtEveryLevel_IsRefused_WithTheMissingReason(string role, string status)
    {
        var k = await ReadyRunAsync(status);
        var body = Conflict(await Approve(k, role));
        body.GetProperty("error").GetString().Should().Be("reconciliation_not_reviewed");
        body.GetProperty("reason").GetString().Should().Be("Missing");
        body.GetProperty("processVersion").GetInt32().Should().Be(k.Current.ProcessVersion);
        (await k.Db.PayrollRuns.AsNoTracking().SingleAsync(r => r.Id == k.Current.Id)).Status.Should().Be(status, "a refused approval changes nothing");
    }

    [Fact]
    public async Task WithAnActiveSignoff_ForTheCurrentVersion_AdminApprovesDirectly()
    {
        var k = await ReadyRunAsync();
        Signoff(k);
        await k.Db.SaveChangesAsync();
        (await Approve(k, "Admin")).Should().BeOfType<OkObjectResult>();
        (await k.Db.PayrollRuns.AsNoTracking().SingleAsync(r => r.Id == k.Current.Id)).Status.Should().Be("Approved");
    }

    [Fact]
    public async Task WithAnActiveSignoff_TheHrLevelOneApprovalAlsoPasses()
    {
        var k = await ReadyRunAsync();
        Signoff(k);
        await k.Db.SaveChangesAsync();
        (await Approve(k, "HR Manager")).Should().BeOfType<OkObjectResult>();
        (await k.Db.PayrollRuns.AsNoTracking().SingleAsync(r => r.Id == k.Current.Id)).Status.Should().Be("PendingFinanceReview");
    }

    [Fact]
    public async Task ASignoffForAnEarlierProcessVersion_IsStale_AndTheRefusalNamesBothVersions()
    {
        var k = await ReadyRunAsync();
        k.Current.ProcessVersion = 3;
        Signoff(k, version: 2);
        await k.Db.SaveChangesAsync();
        var body = Conflict(await Approve(k, "Admin"));
        body.GetProperty("reason").GetString().Should().Be("Stale");
        body.GetProperty("processVersion").GetInt32().Should().Be(3);
        body.GetProperty("signedVersion").GetInt32().Should().Be(2);
    }

    [Fact]
    public async Task ASupersededSignoff_DoesNotCount()
    {
        var k = await ReadyRunAsync();
        var s = Signoff(k);
        s.Status = ReconSignoffStatuses.Superseded; s.SupersededAtUtc = DateTime.UtcNow; s.SupersededReason = ReconSupersededReasons.Reprocessed;
        await k.Db.SaveChangesAsync();
        Conflict(await Approve(k, "Admin")).GetProperty("reason").GetString().Should().Be("Missing");
    }

    [Fact]
    public async Task WhenThePriorRunWasReprocessedAfterTheSignoff_TheRefusalIsPriorChanged_AndTheSignoffIsSuperseded()
    {
        var k = await ReadyRunAsync();
        Signoff(k, priorId: k.Prior.Id, priorVersion: 1);
        k.Prior.ProcessVersion = 2;
        await k.Db.SaveChangesAsync();

        Conflict(await Approve(k, "Admin")).GetProperty("reason").GetString().Should().Be("PriorChanged");
        var stored = await k.Db.PayrollRunReconSignoffs.AsNoTracking().SingleAsync();
        stored.Status.Should().Be(ReconSignoffStatuses.Superseded);
        stored.SupersededReason.Should().Be(ReconSupersededReasons.PriorChanged);
    }

    [Fact]
    public async Task WhenThePriorRunWasVoidedAfterTheSignoff_TheRefusalIsPriorChanged()
    {
        var k = await ReadyRunAsync();
        Signoff(k, priorId: k.Prior.Id, priorVersion: 1);
        k.Prior.Status = "Voided";
        await k.Db.SaveChangesAsync();
        Conflict(await Approve(k, "Admin")).GetProperty("reason").GetString().Should().Be("PriorChanged");
    }

    [Fact]
    public async Task APriorThatIsUnchanged_DoesNotBlockApproval()
    {
        var k = await ReadyRunAsync();
        Signoff(k, priorId: k.Prior.Id, priorVersion: k.Prior.ProcessVersion);
        await k.Db.SaveChangesAsync();
        (await Approve(k, "Admin")).Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task NoRoleBypassesTheGate_NotEvenAnAdminHoldingEveryPermission()
    {
        var k = await ReadyRunAsync();
        var ctrl = ReconHarness.Payroll(k.Db, k.TenantId, "Admin", new[] { "payroll.read", "payroll.approve", "payroll.lock", "payroll.write", "payroll.rules.manage" });
        Conflict(await ctrl.Approve(k.Current.Id, new PayrollDecisionRequest(null), default));
    }

    [Fact]
    public async Task WithTheRuleTurnedOff_ApprovalGoesThroughWithoutASignoff_AndTheAuditSaysSo()
    {
        var k = await ReadyRunAsync();
        k.Db.CompanyPayrollRules.Add(new CompanyPayrollRule
        { TenantId = k.TenantId, CompanyId = k.CompanyId, RuleKey = "recon_signoff_required", RuleValue = "false", EffectiveFrom = new DateOnly(2020, 1, 1) });
        await k.Db.SaveChangesAsync();

        (await Approve(k, "Admin")).Should().BeOfType<OkObjectResult>();
        var audit = await k.Db.PayrollAuditLogs.AsNoTracking().Where(a => a.Action == "payroll.recon.approved_without_signoff").ToListAsync();
        audit.Should().ContainSingle();
        audit[0].EntityId.Should().Be(k.Current.Id.ToString());
    }

    [Fact]
    public async Task WithTheRuleOffButASignoffPresent_NoWaiverIsRecorded()
    {
        var k = await ReadyRunAsync();
        k.Db.CompanyPayrollRules.Add(new CompanyPayrollRule
        { TenantId = k.TenantId, CompanyId = k.CompanyId, RuleKey = "recon_signoff_required", RuleValue = "false", EffectiveFrom = new DateOnly(2020, 1, 1) });
        Signoff(k);
        await k.Db.SaveChangesAsync();
        (await Approve(k, "Admin")).Should().BeOfType<OkObjectResult>();
        (await k.Db.PayrollAuditLogs.AsNoTracking().CountAsync(a => a.Action == "payroll.recon.approved_without_signoff")).Should().Be(0);
    }

    [Fact]
    public async Task AnOffCycleRun_IsNotGated()
    {
        var k = await ManpowerReconServiceTests.Kit.CreateAsync(currentType: PayrollRunTypes.OffCycle);
        (await Approve(k, "Admin")).Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task AReplacementRun_IsGated_BecauseItOwnsThePeriod()
    {
        var k = await ManpowerReconServiceTests.Kit.CreateAsync(currentType: PayrollRunTypes.Replacement);
        Conflict(await Approve(k, "Admin")).GetProperty("reason").GetString().Should().Be("Missing");
    }

    [Fact]
    public async Task TheGate_RunsBeforeTheOtherAcknowledgements_SoTheReviewerSeesTheReconciliationFirst()
    {
        var k = await ReadyRunAsync();
        k.Db.PayrollRunEmployeeSelections.Add(new PayrollRunEmployeeSelection
        { TenantId = k.TenantId, CompanyId = k.CompanyId, PayrollRunId = k.Current.Id, EmployeeId = 1, Mode = "Exclude", Reason = "hold", Outcome = PayrollRunSelectionOutcomes.Excluded });
        await k.Db.SaveChangesAsync();
        Conflict(await Approve(k, "Admin")).GetProperty("error").GetString().Should().Be("reconciliation_not_reviewed");
    }
}
