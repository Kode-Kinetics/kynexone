using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Common;
using Zayra.Api.Controllers;
using Zayra.Api.Infrastructure.Payroll;
using Zayra.Api.Infrastructure.Payroll.ManpowerRecon;
using Zayra.Api.Models;

namespace Zayra.Api.Tests.Recon;

/// <summary>The sign-off and withdrawal controls, one refusal at a time, then the happy path and what it stores.</summary>
public class ReconSignoffFlowTests
{
    /// <summary>Employee 1: +20% on basic (flagged). Employee 2: held out (needs a person-level reason).</summary>
    private static async Task<ManpowerReconServiceTests.Kit> SeedAsync()
    {
        var k = await ManpowerReconServiceTests.Kit.CreateAsync();
        k.AddEmployee(1); k.AddSlip(k.Prior, 1, 1000); k.AddSlip(k.Current, 1, 1200);
        k.AddEmployee(2); k.AddSlip(k.Prior, 2, 800);
        k.Db.PayrollRunEmployeeSelections.Add(new PayrollRunEmployeeSelection
        { TenantId = k.TenantId, CompanyId = k.CompanyId, PayrollRunId = k.Current.Id, EmployeeId = 2, Mode = "Exclude", Reason = "hold", Outcome = PayrollRunSelectionOutcomes.Excluded });
        await k.Db.SaveChangesAsync();
        return k;
    }

    private static PayrollReconController Signer(ManpowerReconServiceTests.Kit k, Guid? userId = null, DataScope? scope = null) =>
        ReconHarness.Recon(k.Db, k.TenantId, "Finance Approver", ReconHarness.SignPermissions, userId, dataScope: scope);

    private static string ErrorOf(IActionResult r) => ReconHarness.Json(((ObjectResult)r).Value).GetProperty("error").GetString()!;
    private static int StatusOf(IActionResult r) => PayrollReconAccessTests.Status(r);

    // ── Refusals ─────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task AStaleProcessVersion_IsRefused()
    {
        var k = await SeedAsync();
        var ctrl = Signer(k);
        var req = await ReconHarness.ValidRequestAsync(ctrl, k.Current.Id);
        var res = await ctrl.Signoff(k.Current.Id, req with { ExpectedProcessVersion = req.ExpectedProcessVersion - 1 }, default);
        StatusOf(res).Should().Be(409);
        ErrorOf(res).Should().Be("signoff_stale");
        (await k.Db.PayrollRunReconSignoffs.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task AStaleSnapshotHash_IsRefused_BecauseTheFiguresMovedUnderTheReviewer()
    {
        var k = await SeedAsync();
        var ctrl = Signer(k);
        var req = await ReconHarness.ValidRequestAsync(ctrl, k.Current.Id);
        var res = await ctrl.Signoff(k.Current.Id, req with { ExpectedSnapshotHash = new string('0', 64) }, default);
        StatusOf(res).Should().Be(409);
        ErrorOf(res).Should().Be("signoff_stale");
    }

    [Fact]
    public async Task ADifferentPriorRunFromTheOneReviewed_ChangesTheHash_SoItIsStale()
    {
        var k = await SeedAsync();
        var earlier = k.AddRun(2026, 6, "Locked");
        await k.Db.SaveChangesAsync();
        var ctrl = Signer(k);
        var reviewedAgainstDefault = await ReconHarness.ValidRequestAsync(ctrl, k.Current.Id);
        var res = await ctrl.Signoff(k.Current.Id, reviewedAgainstDefault with { PriorRunId = earlier.Id }, default);
        StatusOf(res).Should().Be(409, "the hash covers the comparison run, so a switched prior cannot ride an old review");
    }

    [Fact]
    public async Task TheSignoffEndpoint_ReadsNullBeforeASignoff_AndTheSignoffAfterIt()
    {
        var k = await SeedAsync();
        var ctrl = Signer(k);
        var before = await ctrl.GetSignoff(k.Current.Id, null, default);
        before.Should().BeOfType<ContentResult>().Which.Content.Should().Be("null");
        await ctrl.Signoff(k.Current.Id, await ReconHarness.ValidRequestAsync(ctrl, k.Current.Id), default);
        ReconHarness.Body<ReconSignoffDto>(await ctrl.GetSignoff(k.Current.Id, null, default)).Status.Should().Be("Active");
    }

    [Fact]
    public async Task TheFlaggedEmployeeCount_MustBeRestated_AndMatch()
    {
        var k = await SeedAsync();
        var ctrl = Signer(k);
        var req = await ReconHarness.ValidRequestAsync(ctrl, k.Current.Id);
        req.AcknowledgedFlaggedEmployeeCount.Should().Be(1);
        var res = await ctrl.Signoff(k.Current.Id, req with { AcknowledgedFlaggedEmployeeCount = 0 }, default);
        StatusOf(res).Should().Be(409);
        ErrorOf(res).Should().Be("flagged_employees_not_acknowledged");
        ReconHarness.Json(((ObjectResult)res).Value).GetProperty("flaggedEmployeeCount").GetInt32().Should().Be(1);
    }

    [Fact]
    public async Task EveryFlaggedItemAndEveryUnpaidEmployee_NeedsAWrittenExplanation()
    {
        var k = await SeedAsync();
        var ctrl = Signer(k);
        var req = await ReconHarness.ValidRequestAsync(ctrl, k.Current.Id);

        var res = await ctrl.Signoff(k.Current.Id, req with { Explanations = Array.Empty<ReconExplanationRequest>() }, default);
        StatusOf(res).Should().Be(422);
        ErrorOf(res).Should().Be("variance_explanation_required");
        var missing = ReconHarness.Json(((ObjectResult)res).Value).GetProperty("missing").EnumerateArray()
            .Select(m => $"{m.GetProperty("scope").GetString()}:{m.GetProperty("key").GetString()}").ToList();
        missing.Should().BeEquivalentTo(new[] { "Item:Earning:BASIC", "Employee:2" });
        ReconHarness.Json(((ObjectResult)res).Value).GetProperty("missingKeys").EnumerateArray().Select(k => k.GetString())
            .Should().BeEquivalentTo(new[] { "Earning:BASIC", "2" });
    }

    [Fact]
    public async Task AnExplanationShorterThanTheMinimum_CountsAsMissing()
    {
        var k = await SeedAsync();
        var ctrl = Signer(k);
        var req = await ReconHarness.ValidRequestAsync(ctrl, k.Current.Id);
        var shortOne = req.Explanations!.Select(e => e with { Text = new string('x', PayrollValidationOverridePolicy.MinimumReasonLength - 1) }).ToList();
        var res = await ctrl.Signoff(k.Current.Id, req with { Explanations = shortOne }, default);
        StatusOf(res).Should().Be(422);
        ErrorOf(res).Should().Be("variance_explanation_required");

        var exact = req.Explanations!.Select(e => e with { Text = new string('x', PayrollValidationOverridePolicy.MinimumReasonLength) }).ToList();
        StatusOf(await ctrl.Signoff(k.Current.Id, req with { Explanations = exact }, default)).Should().Be(201);
    }

    [Fact]
    public async Task AnExplanationWithABlankKeyOrAnUnknownScope_IsABadRequest()
    {
        var k = await SeedAsync();
        var ctrl = Signer(k);
        var req = await ReconHarness.ValidRequestAsync(ctrl, k.Current.Id);
        var bad = req.Explanations!.Append(new ReconExplanationRequest("Galaxy", "X", "a perfectly long explanation")).ToList();
        var res = await ctrl.Signoff(k.Current.Id, req with { Explanations = bad }, default);
        StatusOf(res).Should().Be(400);
        ErrorOf(res).Should().Be("invalid_explanation");
        var dup = req.Explanations!.Concat(req.Explanations!).ToList();
        StatusOf(await ctrl.Signoff(k.Current.Id, req with { Explanations = dup }, default)).Should().Be(400);
    }

    [Fact]
    public async Task ARunWhoseLinesDoNotMatchItsSlips_CannotBeSigned()
    {
        var k = await SeedAsync();
        var slip = await k.Db.PayrollSlips.FirstAsync(s => s.RunId == k.Current.Id && s.EmployeeId == 1);
        slip.GrossSalary += 75m;
        await k.Db.SaveChangesAsync();
        var ctrl = Signer(k);
        var req = await ReconHarness.ValidRequestAsync(ctrl, k.Current.Id);
        var res = await ctrl.Signoff(k.Current.Id, req, default);
        StatusOf(res).Should().Be(422);
        ErrorOf(res).Should().Be("recon_integrity_failed");
    }

    [Theory]
    [InlineData("Draft")]
    [InlineData("Approved")]
    [InlineData("Locked")]
    [InlineData("Voided")]
    public async Task OnlyAProcessedOrPendingFinanceReviewRun_CanBeSigned(string status)
    {
        var k = await SeedAsync();
        var ctrl = Signer(k);
        var req = await ReconHarness.ValidRequestAsync(ctrl, k.Current.Id);
        k.Current.Status = status;
        await k.Db.SaveChangesAsync();
        var res = await ctrl.Signoff(k.Current.Id, req, default);
        StatusOf(res).Should().Be(400);
        ErrorOf(res).Should().Be("run_not_signable");
    }

    [Fact]
    public async Task APendingFinanceReviewRun_CanBeSigned()
    {
        var k = await SeedAsync();
        var ctrl = Signer(k);
        var req = await ReconHarness.ValidRequestAsync(ctrl, k.Current.Id);
        k.Current.Status = "PendingFinanceReview";
        await k.Db.SaveChangesAsync();
        StatusOf(await ctrl.Signoff(k.Current.Id, req, default)).Should().Be(201);
    }

    [Fact]
    public async Task WhileTheRunIsBeingProcessed_NothingCanBeSigned()
    {
        var k = await SeedAsync();
        var ctrl = Signer(k);
        var req = await ReconHarness.ValidRequestAsync(ctrl, k.Current.Id);
        k.Db.BackgroundJobs.Add(new BackgroundJob
        {
            TenantId = k.TenantId, JobType = PayrollRunProcessJobHandler.JobType, IdempotencyKey = PayrollRunProcessJobHandler.IdempotencyKey(k.Current.Id),
            Status = BackgroundJobStatuses.Running,
        });
        await k.Db.SaveChangesAsync();
        var res = await ctrl.Signoff(k.Current.Id, req, default);
        StatusOf(res).Should().Be(409);
        ErrorOf(res).Should().Be("run_processing");
    }

    [Fact]
    public async Task AFinishedProcessingJob_DoesNotBlock()
    {
        var k = await SeedAsync();
        k.Db.BackgroundJobs.Add(new BackgroundJob
        {
            TenantId = k.TenantId, JobType = PayrollRunProcessJobHandler.JobType, IdempotencyKey = PayrollRunProcessJobHandler.IdempotencyKey(k.Current.Id),
            Status = BackgroundJobStatuses.Succeeded,
        });
        await k.Db.SaveChangesAsync();
        var ctrl = Signer(k);
        StatusOf(await ctrl.Signoff(k.Current.Id, await ReconHarness.ValidRequestAsync(ctrl, k.Current.Id), default)).Should().Be(201);
    }

    [Theory]
    [InlineData("creator")]
    [InlineData("processor")]
    public async Task ThePersonWhoCreatedOrProcessedTheRun_CannotSignIt(string who)
    {
        var k = await SeedAsync();
        var me = Guid.NewGuid();
        if (who == "creator") k.Current.CreatedByUserId = me; else k.Current.ProcessedByUserId = me;
        await k.Db.SaveChangesAsync();
        var ctrl = Signer(k, me);
        var req = await ReconHarness.ValidRequestAsync(ctrl, k.Current.Id);
        var res = await ctrl.Signoff(k.Current.Id, req, default);
        StatusOf(res).Should().Be(403);
        ErrorOf(res).Should().Be("maker_checker_violation");
    }

    // ── The happy path, and what it stores ───────────────────────────────────────────────────────

    [Fact]
    public async Task ASignoff_RecordsTheReviewedVersionHashPriorAndExplanations_WithNoPayByEmployee()
    {
        var k = await SeedAsync();
        var me = Guid.NewGuid();
        var ctrl = Signer(k, me);
        var live = ReconHarness.Body<ReconDto>(await ctrl.Get(k.Current.Id, null, default));
        var req = await ReconHarness.ValidRequestAsync(ctrl, k.Current.Id);

        var res = await ctrl.Signoff(k.Current.Id, req, default);
        StatusOf(res).Should().Be(201);
        var dto = ReconHarness.Body<ReconSignoffDto>(res);
        dto.Status.Should().Be(ReconSignoffStatuses.Active);
        dto.SignedOffByName.Should().Be("Reviewer");
        dto.SnapshotHash.Should().Be(live.SnapshotHash);

        k.Db.ChangeTracker.Clear();
        var stored = await k.Db.PayrollRunReconSignoffs.Include(s => s.Explanations).SingleAsync();
        stored.ProcessVersion.Should().Be(k.Current.ProcessVersion);
        stored.PriorRunId.Should().Be(k.Prior.Id);
        stored.PriorProcessVersion.Should().Be(k.Prior.ProcessVersion);
        stored.SignedOffByUserId.Should().Be(me);
        stored.CompanyId.Should().Be(k.CompanyId);
        stored.Notes.Should().Be("Reviewed against last month.");
        stored.Explanations.Select(e => $"{e.Scope}:{e.Key}").Should().BeEquivalentTo(new[] { "Item:Earning:BASIC", "Employee:2" });
        stored.SnapshotJson.Should().Contain("\"volume\"").And.NotContain("Employee 1").And.NotContain("\"E1\"");
        (await k.Db.PayrollAuditLogs.AsNoTracking().CountAsync(a => a.Action == "payroll.recon.signed_off")).Should().Be(1);
    }

    [Fact]
    public async Task AfterSigning_TheReconciliationShowsTheSignoff_AndTheItemExplanations()
    {
        var k = await SeedAsync();
        var ctrl = Signer(k);
        await ctrl.Signoff(k.Current.Id, await ReconHarness.ValidRequestAsync(ctrl, k.Current.Id), default);

        var dto = ReconHarness.Body<ReconDto>(await ctrl.Get(k.Current.Id, null, default));
        dto.Signoff!.Status.Should().Be(ReconSignoffStatuses.Active);
        dto.Value.Sections.Single(s => s.Kind == "Earning").Items.Single(i => i.Code == "BASIC").Explanation.Should().StartWith("Reviewed:");
        ReconHarness.Body<ReconSignoffDto>(await ctrl.GetSignoff(k.Current.Id, null, default)).Id.Should().Be(dto.Signoff.Id);
    }

    [Fact]
    public async Task ASecondSignoff_WhileOneIsActive_IsRefused()
    {
        var k = await SeedAsync();
        var ctrl = Signer(k);
        var req = await ReconHarness.ValidRequestAsync(ctrl, k.Current.Id);
        StatusOf(await ctrl.Signoff(k.Current.Id, req, default)).Should().Be(201);
        var again = await ctrl.Signoff(k.Current.Id, req, default);
        StatusOf(again).Should().Be(409);
        ErrorOf(again).Should().Be("already_signed_off");
        (await k.Db.PayrollRunReconSignoffs.CountAsync(s => s.Status == ReconSignoffStatuses.Active)).Should().Be(1);
    }

    [Fact]
    public async Task APriorRunChosenExplicitly_IsBoundIntoTheSignoff()
    {
        var k = await SeedAsync();
        var chosen = k.AddRun(2026, 8, "Locked");
        chosen.ProcessVersion = 7;
        await k.Db.SaveChangesAsync();
        var ctrl = Signer(k);
        var req = await ReconHarness.ValidRequestAsync(ctrl, k.Current.Id, chosen.Id);
        StatusOf(await ctrl.Signoff(k.Current.Id, req, default)).Should().Be(201);
        var stored = await k.Db.PayrollRunReconSignoffs.AsNoTracking().SingleAsync();
        stored.PriorRunId.Should().Be(chosen.Id);
        stored.PriorProcessVersion.Should().Be(7);
    }

    [Fact]
    public async Task AnInvalidPriorRun_IsRefused_WithoutSigning()
    {
        var k = await SeedAsync();
        var ctrl = Signer(k);
        var req = await ReconHarness.ValidRequestAsync(ctrl, k.Current.Id);
        var res = await ctrl.Signoff(k.Current.Id, req with { PriorRunId = Guid.NewGuid() }, default);
        StatusOf(res).Should().Be(400);
        ErrorOf(res).Should().Be("prior_run_invalid");
    }

    [Fact]
    public async Task AFirstRunWithNoPrior_CanBeSigned_WithoutEmployeeMovementExplanations()
    {
        var k = await ManpowerReconServiceTests.Kit.CreateAsync(withPrior: false);
        k.AddEmployee(1); k.AddSlip(k.Current, 1, 1000);
        await k.Db.SaveChangesAsync();
        var ctrl = Signer(k);
        var req = await ReconHarness.ValidRequestAsync(ctrl, k.Current.Id);
        req.Explanations.Should().BeEmpty();
        StatusOf(await ctrl.Signoff(k.Current.Id, req, default)).Should().Be(201);
        (await k.Db.PayrollRunReconSignoffs.AsNoTracking().SingleAsync()).PriorRunId.Should().BeNull();
    }

    [Fact]
    public async Task ASignoff_IsAllowedEvenWhenTheCompanyTurnedTheRequirementOff()
    {
        var k = await SeedAsync();
        k.Db.CompanyPayrollRules.Add(new CompanyPayrollRule
        { TenantId = k.TenantId, CompanyId = k.CompanyId, RuleKey = "recon_signoff_required", RuleValue = "false", EffectiveFrom = new DateOnly(2020, 1, 1) });
        await k.Db.SaveChangesAsync();
        var ctrl = Signer(k);
        ReconHarness.Body<ReconDto>(await ctrl.Get(k.Current.Id, null, default)).SignoffRequired.Should().BeFalse();
        StatusOf(await ctrl.Signoff(k.Current.Id, await ReconHarness.ValidRequestAsync(ctrl, k.Current.Id), default)).Should().Be(201);
    }

    // ── Withdrawing ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task WithdrawingASignoff_NeedsAReason_AndSupersedesItAsWithdrawn()
    {
        var k = await SeedAsync();
        var ctrl = Signer(k);
        await ctrl.Signoff(k.Current.Id, await ReconHarness.ValidRequestAsync(ctrl, k.Current.Id), default);

        var noReason = await ctrl.WithdrawSignoff(k.Current.Id, new ReconWithdrawRequest("  "), default);
        StatusOf(noReason).Should().Be(400);
        ErrorOf(noReason).Should().Be("reason_required");
        StatusOf(await ctrl.WithdrawSignoff(k.Current.Id, new ReconWithdrawRequest("too short"), default)).Should().Be(400);

        var ok = await ctrl.WithdrawSignoff(k.Current.Id, new ReconWithdrawRequest("The housing figure was wrong."), default);
        StatusOf(ok).Should().Be(200);
        k.Db.ChangeTracker.Clear();
        var stored = await k.Db.PayrollRunReconSignoffs.AsNoTracking().SingleAsync();
        stored.Status.Should().Be(ReconSignoffStatuses.Superseded);
        stored.SupersededReason.Should().Be(ReconSupersededReasons.Withdrawn);
        var audit = await k.Db.PayrollAuditLogs.AsNoTracking().Where(a => a.Action == "payroll.recon.withdrawn").ToListAsync();
        audit.Should().ContainSingle();
        audit[0].MetadataJson.Should().Contain("The housing figure was wrong.");
    }

    [Fact]
    public async Task WithdrawingWithNothingActive_IsNotFound()
    {
        var k = await SeedAsync();
        var res = await Signer(k).WithdrawSignoff(k.Current.Id, new ReconWithdrawRequest("a long enough reason"), default);
        StatusOf(res).Should().Be(404);
        ErrorOf(res).Should().Be("no_active_signoff");
    }

    [Fact]
    public async Task AfterAWithdrawal_TheRunCanBeSignedAgain()
    {
        var k = await SeedAsync();
        var ctrl = Signer(k);
        await ctrl.Signoff(k.Current.Id, await ReconHarness.ValidRequestAsync(ctrl, k.Current.Id), default);
        await ctrl.WithdrawSignoff(k.Current.Id, new ReconWithdrawRequest("Recheck the housing figure."), default);
        StatusOf(await ctrl.Signoff(k.Current.Id, await ReconHarness.ValidRequestAsync(ctrl, k.Current.Id), default)).Should().Be(201);
        (await k.Db.PayrollRunReconSignoffs.CountAsync()).Should().Be(2);
        (await k.Db.PayrollRunReconSignoffs.CountAsync(s => s.Status == ReconSignoffStatuses.Active)).Should().Be(1);
    }

    [Fact]
    public async Task ARunNoLongerAwaitingApproval_CannotHaveItsSignoffWithdrawn()
    {
        var k = await SeedAsync();
        var ctrl = Signer(k);
        await ctrl.Signoff(k.Current.Id, await ReconHarness.ValidRequestAsync(ctrl, k.Current.Id), default);
        k.Current.Status = "Approved";
        await k.Db.SaveChangesAsync();
        StatusOf(await ctrl.WithdrawSignoff(k.Current.Id, new ReconWithdrawRequest("a long enough reason"), default)).Should().Be(400);
    }
}
