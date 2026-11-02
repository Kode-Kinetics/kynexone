using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Approvals;
using Zayra.Api.Application.Employees;
using Zayra.Api.Infrastructure.Compensation;
using Zayra.Api.Infrastructure.Employees;
using Zayra.Api.Models;
using static Zayra.Api.Tests.Compensation.MeritTestKit;

namespace Zayra.Api.Tests.Compensation;

/// <summary>
/// CMP-03/08/09 on real PostgreSQL: reject/return → revise → approve, strict SoD, refusal of unauthorised callers,
/// overlapping salary changes, idempotent apply, exactly-once effective-dated application by the existing
/// EffectiveChangeJobHandler, the payroll calculation consuming the new basic once, cancellation before the effective
/// date, reversal through a correcting governed change, scenarios that never reach payroll, and tenant/company isolation.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration")]
public sealed class CompensationLifecycleTests(PostgresFixture fx)
{
    // ───────────────────────────── CMP-03 / CMP-09: approval path ─────────────────────────────

    [Fact]
    public async Task RejectReturns_ManagerRevises_TwoStepApprovalLocksTheWorksheet()
    {
        var w = await SeedAsync(fx);
        var (_, a, _) = await SubmittedAsync(fx, w);
        var firstApproval = a.ApprovalRequestId!.Value;

        // A return needs a reason.
        await FluentActions.Invoking(() => DecideAsync(fx, w, firstApproval, w.UserHr2, ["HR Manager"], "Reject", null))
            .Should().ThrowAsync<InvalidOperationException>().WithMessage("*needs a comment*");
        await DecideAsync(fx, w, firstApproval, w.UserHr2, ["HR Manager"], "Reject", "A3 is above guideline without enough evidence; bring it to 4 %.");

        var returned = await WorksheetOfAsync(fx, w, a.CycleId);
        (returned.Status, returned.Revision, returned.DecisionComments).Should().Be((CompWorksheetStatuses.Returned, 2, "A3 is above guideline without enough evidence; bring it to 4 %."));
        returned.CanEdit.Should().BeTrue();

        await using (var db = fx.CreateDb())
        {
            var svc = Service(db);
            var revised = ProposalsA(w).Select(l => l.EmployeeId == w.E["A3"] ? Pct(w, "A3", 4m) : l).ToList();
            returned = await svc.SaveWorksheetAsync(Manager(w), returned.Id, new SaveCompWorksheetRequest(returned.Version, returned.OverspendJustification, revised), default);
            returned.Lines.Single(l => l.EmployeeId == w.E["A3"]).OutOfGuideline.Should().BeFalse();
            returned = await svc.SubmitWorksheetAsync(Manager(w), returned.Id, new SubmitCompWorksheetRequest(returned.Version), default);
        }
        returned.ApprovalRequestId.Should().NotBe(firstApproval, "a revision is a new approval request");
        await DecideAsync(fx, w, returned.ApprovalRequestId!.Value, w.UserHr2, ["HR Manager"], "Approve", "Fine now");
        (await WorksheetOfAsync(fx, w, a.CycleId)).Status.Should().Be(CompWorksheetStatuses.Submitted, "step 1 of 2 changes nothing");
        await DecideAsync(fx, w, returned.ApprovalRequestId!.Value, w.UserFinance, ["Finance Approver"], "Approve", "Within plan");
        var approved = await WorksheetOfAsync(fx, w, a.CycleId);
        approved.Status.Should().Be(CompWorksheetStatuses.Approved);
        approved.CanEdit.Should().BeFalse();

        await using var check = fx.CreateDb();
        await FluentActions.Invoking(() => Service(check).SaveWorksheetAsync(Hr(w), approved.Id, new SaveCompWorksheetRequest(approved.Version, null, []), default))
            .Should().ThrowAsync<CompRuleException>().Where(e => e.Code == "worksheet_locked");
        var actions = await check.AuditLogs.IgnoreQueryFilters().Where(x => x.EntityId == approved.Id.ToString()).Select(x => x.Action).ToListAsync();
        actions.Should().Contain(["compensation.worksheet.submitted", "compensation.worksheet.returned", "compensation.worksheet.saved", "compensation.worksheet.approved"]);
    }

    [Fact]
    public async Task SeparationOfDuties_NoOneDecidesTheirOwnWorksheetOrTheirOwnPay()
    {
        var w = await SeedAsync(fx);
        var (cycle, a, _) = await SubmittedAsync(fx, w);
        var approvalId = a.ApprovalRequestId!.Value;

        // The manager who submitted it (requester and subject).
        await FluentActions.Invoking(() => DecideAsync(fx, w, approvalId, w.UserManagerA, ["HR Manager"], "Approve", "mine"))
            .Should().ThrowAsync<InvalidOperationException>().WithMessage("*requester cannot approve*");
        // Someone whose own pay is a line on the worksheet, even holding the step's role.
        var a2User = Guid.NewGuid();
        await using (var db = fx.CreateDb()) { (await db.Employees.SingleAsync(e => e.Id == w.E["A2"])).UserAccountId = a2User; await db.SaveChangesAsync(); }
        await FluentActions.Invoking(() => DecideAsync(fx, w, approvalId, a2User, ["HR Manager"], "Approve", "ok"))
            .Should().ThrowAsync<InvalidOperationException>().WithMessage("*includes your own pay*");
        // The step's role without the compensation approver permission.
        await FluentActions.Invoking(() => DecideAsync(fx, w, approvalId, w.UserHr2, ["HR Manager"], "Approve", "ok", ["approvals.decide"]))
            .Should().ThrowAsync<InvalidOperationException>().WithMessage("*compensation approver permission*");
        // One person cannot decide both steps.
        await DecideAsync(fx, w, approvalId, w.UserHr2, ["HR Manager"], "Approve", "step 1");
        await FluentActions.Invoking(() => DecideAsync(fx, w, approvalId, w.UserHr2, ["Finance Approver"], "Approve", "step 2"))
            .Should().ThrowAsync<InvalidOperationException>().WithMessage("*already decided an earlier step*");

        // HR holding compensation.manage whose own pay sits on a worksheet cannot edit it.
        await using (var db = fx.CreateDb())
        {
            var hrOnTheSheet = Caller(w, a2User, "HR on sheet", w.E["A2"], ["HR Manager"], HrPermissions);
            var b = await WorksheetOfAsync(fx, w, cycle.Id, managerB: true);
            // B's sheet is submitted; use a returned/draft situation via a fresh scenario on A's manager: A2 is on A's team.
            await FluentActions.Invoking(() => Service(db).CreateScenarioAsync(hrOnTheSheet, cycle.Id, new CreateCompScenarioRequest(w.ManagerA, "Stretch", null), default))
                .Should().NotThrowAsync("creating a scenario reads nothing of the caller's own line");
            var stretch = await db.CompWorksheets.AsNoTracking().SingleAsync(s => s.CycleId == cycle.Id && s.ScenarioName == "Stretch");
            await FluentActions.Invoking(() => Service(db).SaveWorksheetAsync(hrOnTheSheet, stretch.Id, new SaveCompWorksheetRequest(stretch.Version, null, []), default))
                .Should().ThrowAsync<CompForbiddenException>().Where(e => e.Code == "self_award");
            b.Lines.Should().NotContain(l => l.EmployeeId == w.ManagerB, "a manager's own line is never on their own worksheet");
        }

        // The cycle approver must not be the person who sent it to review.
        await DecideAsync(fx, w, approvalId, w.UserFinance, ["Finance Approver"], "Approve", "ok");
        var bId = (await WorksheetOfAsync(fx, w, cycle.Id, managerB: true)).ApprovalRequestId!.Value;
        await ApproveBothStepsAsync(fx, w, bId);
        await using (var db = fx.CreateDb())
        {
            var svc = Service(db);
            var view = await svc.GetCycleAsync(Hr(w), cycle.Id, default);
            view = await svc.StartReviewAsync(Hr(w), cycle.Id, new CompCycleTransitionRequest(view.Version), default);
            await FluentActions.Invoking(() => svc.ApproveCycleAsync(Hr(w), cycle.Id, new CompCycleTransitionRequest(view.Version), default))
                .Should().ThrowAsync<CompForbiddenException>().Where(e => e.Code == "maker_checker");
        }
    }

    [Fact]
    public async Task ManagersOwnLine_IsUncovered_WhenTheyHaveNoManager_NeverOnTheirOwnSheet()
    {
        var w = await SeedAsync(fx);
        await using var db = fx.CreateDb();
        var svc = Service(db);
        // No grade exclusion this time: the managers themselves are eligible.
        var cycle = await svc.CreateCycleAsync(Hr(w), new CreateCompCycleRequest(w.Company, "Everyone", PeriodStart, PeriodEnd, Effective,
            Eligibility(w) with { ExcludeGradeIds = [] }), default);
        cycle.Uncovered.Select(u => u.EmployeeId).Should().Contain([w.ManagerA, w.ManagerB]);
        await svc.SaveGuidelinesAsync(Hr(w), cycle.Id, new SaveCompGuidelinesRequest(Matrix), default);
        await svc.SaveBudgetsAsync(Hr(w), cycle.Id, new SaveCompBudgetsRequest([new(w.Eng, CompBudgetPoolTypes.FixedAmount, null, 1000m)]), default);
        cycle = await svc.SaveFxAsync(Hr(w), cycle.Id, new SaveCompFxRequest([new("AED", 1.02m, "SAMA", new DateOnly(2026, 10, 1))]), default);
        cycle = await svc.OpenPlanningAsync(Hr(w), cycle.Id, new CompCycleTransitionRequest(cycle.Version), default);
        var sheetA = await WorksheetOfAsync(fx, w, cycle.Id);
        sheetA.Lines.Should().NotContain(l => l.EmployeeId == w.ManagerA);
    }

    [Fact]
    public async Task UnauthorisedCallers_AreRefused()
    {
        var w = await SeedAsync(fx);
        var (cycle, a, _) = await SubmittedAsync(fx, w);
        await using var db = fx.CreateDb();
        var svc = Service(db);
        await FluentActions.Invoking(() => svc.CreateCycleAsync(Manager(w), new CreateCompCycleRequest(w.Company, "x", PeriodStart, PeriodEnd, Effective, Eligibility(w)), default))
            .Should().ThrowAsync<CompForbiddenException>();
        await FluentActions.Invoking(() => svc.GetCycleAsync(Manager(w), cycle.Id, default)).Should().ThrowAsync<CompForbiddenException>();
        await FluentActions.Invoking(() => svc.GetWorksheetAsync(Manager(w, true), a.Id, default))
            .Should().ThrowAsync<CompNotFoundException>("another manager's worksheet does not exist for them");
        var stranger = Caller(w, w.UserStranger, "Stranger", null, ["Employee"], ["ess.read"]);
        await FluentActions.Invoking(() => svc.ApplyAsync(stranger, cycle.Id, default)).Should().ThrowAsync<CompForbiddenException>();
        await FluentActions.Invoking(() => new PayEquityService(db, new Zayra.Api.Infrastructure.Audit.AuditService(db)).GetAsync(Finance(w), cycle.Id, default))
            .Should().ThrowAsync<CompForbiddenException>("pay equity needs compensation.equity.read");

        // HTTP gate: every action is [HasPermission]-gated (any compensation.* at class level, the specific key on writes).
        var type = typeof(Zayra.Api.Controllers.Compensation.MeritPlanningController);
        type.GetCustomAttributes(typeof(Zayra.Api.Infrastructure.Authorization.HasPermissionAttribute), true).Should().ContainSingle();
        var writes = type.GetMethods().Where(m => m.GetCustomAttributes(typeof(Microsoft.AspNetCore.Mvc.Routing.HttpMethodAttribute), true)
            .Any(x => x is HttpPostAttribute or HttpPutAttribute)).ToList();
        writes.Should().NotBeEmpty();
        writes.Should().OnlyContain(m => m.GetCustomAttributes(typeof(Zayra.Api.Infrastructure.Authorization.HasPermissionAttribute), true).Length == 1, "every write names its own permission");
    }

    // ───────────────────────────── CMP-08 / CMP-09: apply, effective date, payroll ─────────────────────────────

    [Fact]
    public async Task Apply_IsIdempotent_RefusesOverlaps_JobAppliesOnce_PayrollPaysTheNewBasicOnce_ScenarioNeverReachesPayroll()
    {
        var w = await SeedAsync(fx);
        await GiveEveryonePayrollProfilesAsync(w);
        // A scenario with a much bigger A1 award that is never the submitted one.
        var cycle = await OpenAsync(fx, w, await ConfiguredCycleAsync(fx, w));
        await using (var db = fx.CreateDb())
        {
            var stretch = await Service(db).CreateScenarioAsync(Manager(w), cycle.Id, new CreateCompScenarioRequest(null, "Stretch", null), default);
            await Service(db).SaveWorksheetAsync(Manager(w), stretch.Id, new SaveCompWorksheetRequest(stretch.Version, null, [Pct(w, "A1", 8m)]), default);
        }
        var a = await WorksheetOfAsync(fx, w, cycle.Id);
        var b = await WorksheetOfAsync(fx, w, cycle.Id, managerB: true);
        await using (var db = fx.CreateDb())
        {
            var svc = Service(db);
            a = await svc.SaveWorksheetAsync(Manager(w), a.Id, new SaveCompWorksheetRequest(a.Version, "Retention.", ProposalsA(w)), default);
            a = await svc.SubmitWorksheetAsync(Manager(w), a.Id, new SubmitCompWorksheetRequest(a.Version), default);
            b = await svc.SaveWorksheetAsync(Manager(w, true), b.Id, new SaveCompWorksheetRequest(b.Version, null, ProposalsB(w)), default);
            b = await svc.SubmitWorksheetAsync(Manager(w, true), b.Id, new SubmitCompWorksheetRequest(b.Version), default);
        }
        await ApproveBothStepsAsync(fx, w, a.ApprovalRequestId!.Value);
        await ApproveBothStepsAsync(fx, w, b.ApprovalRequestId!.Value);

        // An unrelated salary change for A2 is already waiting for approval: its award must not be scheduled on top.
        Guid pendingId;
        await using (var db = fx.CreateDb())
        {
            var pending = new EmployeeChangeRequest { TenantId = w.Tenant, EmployeeId = w.E["A2"], EffectiveDate = new DateOnly(2026, 12, 1), SensitiveFields = "salaryBreakdown", ProposedChangesJson = "{}" };
            db.EmployeeChangeRequests.Add(pending);
            await db.SaveChangesAsync();
            pendingId = pending.Id;
            var svc = Service(db);
            var view = await svc.GetCycleAsync(Hr(w), cycle.Id, default);
            view = await svc.StartReviewAsync(Hr(w), cycle.Id, new CompCycleTransitionRequest(view.Version), default);
            await svc.ApproveCycleAsync(Finance(w), cycle.Id, new CompCycleTransitionRequest(view.Version), default);
        }

        CompCycleDto applied;
        await using (var db = fx.CreateDb()) applied = await Service(db).ApplyAsync(Hr(w), cycle.Id, default);
        applied.Status.Should().Be(CompCycleStatuses.Applied);
        applied.Apply!.Scheduled.Should().Be(10);
        applied.Apply.Lines.Single(l => l.EmployeeId == w.E["A2"]).BlockedReason.Should().Contain("Another salary change");
        // Run apply again: nothing new.
        await using (var db = fx.CreateDb()) applied = await Service(db).ApplyAsync(Hr(w), cycle.Id, default);
        applied.Apply!.Scheduled.Should().Be(10);
        (await CycleChangeCountAsync(cycle.Id)).Should().Be(10);
        // Resolve the overlap, run apply again: A2 is picked up, once.
        await using (var db = fx.CreateDb()) { (await db.EmployeeChangeRequests.SingleAsync(x => x.Id == pendingId)).Status = EmployeeChangeStatuses.Rejected; await db.SaveChangesAsync(); }
        await using (var db = fx.CreateDb()) await Service(db).ApplyAsync(Hr(w), cycle.Id, default);
        await using (var db = fx.CreateDb()) applied = await Service(db).ApplyAsync(Hr(w), cycle.Id, default);
        applied.Apply!.Scheduled.Should().Be(11);
        (await CycleChangeCountAsync(cycle.Id)).Should().Be(11);

        // The award is an approved, future-dated governed change with a sealed baseline — not a salary row.
        Guid a1Change;
        await using (var db = fx.CreateDb())
        {
            var line = await db.CompWorksheetLines.SingleAsync(l => l.CycleId == cycle.Id && l.EmployeeId == w.E["A1"] && l.WorksheetId == a.Id);
            a1Change = line.ChangeRequestId!.Value;
            var change = await db.EmployeeChangeRequests.SingleAsync(x => x.Id == a1Change);
            (change.Status, change.EffectiveDate, change.ApprovedByUserId, change.ApprovalRequestId).Should()
                .Be((EmployeeChangeStatuses.ApprovedPendingEffectiveDate, Effective, (Guid?)w.UserFinance, (Guid?)null));
            var package = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(change.ProposedChangesJson)!["salaryBreakdown"];
            package.GetProperty("basicSalary").GetDecimal().Should().Be(6360.00m);
            package.GetProperty("housingAllowance").GetDecimal().Should().Be(1500m);
            package.GetProperty("transportAllowance").GetDecimal().Should().Be(500m);
            package.GetProperty("currency").GetString().Should().Be("SAR");
            (await db.EmployeeHistories.CountAsync(h => h.EmployeeId == w.E["A1"] && h.EventType == EmployeeChangeBaseline.ScheduledEventType)).Should().Be(1);
            (await db.EmployeeSalaryStructures.CountAsync(s => s.EmployeeId == w.E["A1"])).Should().Be(1, "nothing is written before the effective date");
            // The scenario's 8 % never became a change.
            var scenarioLine = await db.CompWorksheetLines.SingleAsync(l => l.EmployeeId == w.E["A1"] && l.WorksheetId != a.Id);
            scenarioLine.ChangeRequestId.Should().BeNull();
            (await db.EmployeeChangeRequests.CountAsync(x => x.EmployeeId == w.E["A1"])).Should().Be(1);
        }

        // The day before: not due. On the day: applied. Again: already handled.
        (await RunJobAsync(fx, w.Tenant, a1Change, Effective.AddDays(-1))).Outcome.Should().Be(EffectiveChangeOutcome.AlreadyHandled);
        (await RunJobAsync(fx, w.Tenant, a1Change, Effective)).Outcome.Should().Be(EffectiveChangeOutcome.Applied);
        (await RunJobAsync(fx, w.Tenant, a1Change, Effective)).Outcome.Should().Be(EffectiveChangeOutcome.AlreadyHandled);
        await using (var db = fx.CreateDb())
        {
            var rows = await db.EmployeeSalaryStructures.Where(s => s.EmployeeId == w.E["A1"] && s.IsActive).OrderBy(s => s.EffectiveDate).ToListAsync();
            rows.Select(r => (r.EffectiveDate, r.BasicSalary, r.HousingAllowance)).Should().Equal((new DateOnly(2024, 1, 1), 6000m, 1500m), (Effective, 6360m, 1500m));
            (await db.Employees.SingleAsync(e => e.Id == w.E["A1"])).Salary.Should().Be(8360m);
            (await db.EmployeeChangeRequests.SingleAsync(x => x.Id == a1Change)).Status.Should().Be(EmployeeChangeStatuses.ApprovedApplied);
        }

        // The approved pool does not move when the awards change pay of record (frozen from Review onward).
        await using (var db = fx.CreateDb())
        {
            var after = await Service(db).GetCycleAsync(Hr(w), cycle.Id, default);
            (after.Totals.EligibleBasicCycle, after.Totals.Pool, after.Totals.Proposed).Should().Be((86120.00m, 3084.80m, 2923.59m));
        }

        // Payroll for the effective month pays the new basic; the month after pays it again (not twice) with no arrears.
        var nov = await ProcessRunAsync(w, 2026, 11);
        (await SlipBasicAsync(nov, w.E["A1"])).Should().Be(6360m);
        (await SlipBasicAsync(nov, w.E["B1"])).Should().Be(9000m, "B1's award has not been applied by the job yet");
        await using (var db = fx.CreateDb()) { (await db.PayrollRuns.SingleAsync(r => r.Id == nov)).Status = "Locked"; await db.SaveChangesAsync(); }
        var dec = await ProcessRunAsync(w, 2026, 12);
        (await SlipBasicAsync(dec, w.E["A1"])).Should().Be(6360m);
        await using (var db = fx.CreateDb())
            (await db.PayrollArrearsLines.Where(l => l.PayrollRunId == dec && l.EmployeeId == w.E["A1"]).SumAsync(l => l.Amount)).Should().Be(0m, "November already paid the new basic");

        // Cancelling now is refused: an award already took effect (it is reversed one by one instead).
        await using (var db = fx.CreateDb())
        {
            var view = await Service(db).GetCycleAsync(Hr(w), cycle.Id, default);
            await FluentActions.Invoking(() => Service(db).CancelAsync(Hr(w), cycle.Id, new CompCycleTransitionRequest(view.Version, "Budget withdrawn"), default))
                .Should().ThrowAsync<CompRuleException>().Where(e => e.Code == "awards_applied");
        }

        // Reversal: a correcting change on the ordinary employee-change approval route, then the same job applies it.
        await InstallEmployeeChangeWorkflowAsync(w);
        CompApplyLineDto reversal;
        await using (var db = fx.CreateDb())
        {
            var lineId = await db.CompWorksheetLines.Where(l => l.ChangeRequestId == a1Change).Select(l => l.Id).SingleAsync();
            await FluentActions.Invoking(() => Service(db).ReverseAwardAsync(Hr(w), lineId, new ReverseCompAwardRequest(new DateOnly(2026, 12, 15), "Awarded in error"), default))
                .Should().ThrowAsync<CompRuleException>().Where(e => e.Code == "effective_not_first");
            reversal = await Service(db).ReverseAwardAsync(Hr(w), lineId, new ReverseCompAwardRequest(new DateOnly(2027, 1, 1), "Awarded in error: rating was revised on appeal"), default);
        }
        reversal.ReversalStatus.Should().Be(EmployeeChangeStatuses.PendingApproval);
        Guid reversalApproval;
        await using (var db = fx.CreateDb())
        {
            var change = await db.EmployeeChangeRequests.SingleAsync(x => x.Id == reversal.ReversalChangeRequestId);
            JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(change.ProposedChangesJson)!["salaryBreakdown"].GetProperty("basicSalary").GetDecimal().Should().Be(6000m);
            reversalApproval = change.ApprovalRequestId!.Value;
        }
        await FluentActions.Invoking(() => DecideAsync(fx, w, reversalApproval, w.UserHr, ["HR Manager"], "Approve", "self"))
            .Should().ThrowAsync<InvalidOperationException>("the requester of the reversal cannot approve it");
        await DecideAsync(fx, w, reversalApproval, w.UserHr2, ["HR Manager"], "Approve", "Confirmed with payroll", ["approvals.decide", CompPermissions.Approve, "payroll.approve"]); // a pay change is decided by a Finance (payroll.approve) holder
        (await RunJobAsync(fx, w.Tenant, reversal.ReversalChangeRequestId!.Value, new DateOnly(2027, 1, 1))).Outcome.Should().Be(EffectiveChangeOutcome.Applied);
        await using (var db = fx.CreateDb()) { (await db.PayrollRuns.SingleAsync(r => r.Id == dec)).Status = "Locked"; await db.SaveChangesAsync(); }
        var jan = await ProcessRunAsync(w, 2027, 1);
        (await SlipBasicAsync(jan, w.E["A1"])).Should().Be(6000m);
        await using (var db = fx.CreateDb())
            (await db.EmployeeSalaryStructures.CountAsync(s => s.EmployeeId == w.E["A1"] && s.IsActive)).Should().Be(3, "a reversal is a new dated row; nothing is deleted");
    }

    [Fact]
    public async Task CancellationBeforeTheEffectiveDate_WithdrawsTheAwards_AndPayrollKeepsTheOldBasic()
    {
        var w = await SeedAsync(fx);
        await GiveEveryonePayrollProfilesAsync(w);
        var applied = await AppliedAsync(fx, w);
        applied.Apply!.Scheduled.Should().Be(11);
        Guid a1Change;
        await using (var db = fx.CreateDb())
        {
            a1Change = await db.CompWorksheetLines.Where(l => l.CycleId == applied.Id && l.EmployeeId == w.E["A1"] && l.ChangeRequestId != null).Select(l => l.ChangeRequestId!.Value).SingleAsync();
            var cancelled = await Service(db).CancelAsync(Hr(w), applied.Id, new CompCycleTransitionRequest(applied.Version, "Board froze increases"), default);
            cancelled.Status.Should().Be(CompCycleStatuses.Cancelled);
            cancelled.Apply!.Cancelled.Should().Be(11);
        }
        (await RunJobAsync(fx, w.Tenant, a1Change, Effective)).Outcome.Should().Be(EffectiveChangeOutcome.AlreadyHandled);
        var nov = await ProcessRunAsync(w, 2026, 11);
        (await SlipBasicAsync(nov, w.E["A1"])).Should().Be(6000m);
        await using var verify = fx.CreateDb();
        (await verify.EmployeeChangeRequests.SingleAsync(x => x.Id == a1Change)).Status.Should().Be(EmployeeChangeStatuses.Cancelled);
        (await verify.AuditLogs.IgnoreQueryFilters().CountAsync(x => x.EntityId == a1Change.ToString() && x.Action == "compensation.award.withdrawn")).Should().Be(1);
    }

    [Fact]
    public async Task AStaleWorksheetVersion_IsAConflict_NotALostUpdate()
    {
        var w = await SeedAsync(fx);
        var cycle = await OpenAsync(fx, w, await ConfiguredCycleAsync(fx, w));
        var sheet = await WorksheetOfAsync(fx, w, cycle.Id);
        await using var db = fx.CreateDb();
        var svc = Service(db);
        await svc.SaveWorksheetAsync(Manager(w), sheet.Id, new SaveCompWorksheetRequest(sheet.Version, null, [Pct(w, "A1", 3m)]), default);
        await FluentActions.Invoking(() => svc.SaveWorksheetAsync(Hr(w), sheet.Id, new SaveCompWorksheetRequest(sheet.Version, null, [Pct(w, "A1", 5m)]), default))
            .Should().ThrowAsync<CompConflictException>();
        (await WorksheetOfAsync(fx, w, cycle.Id)).Lines.Single(l => l.EmployeeId == w.E["A1"]).ProposedPercent.Should().Be(3.0000m);
    }

    // ───────────────────────────── isolation ─────────────────────────────

    [Fact]
    public async Task TenantB_AndCompanyB_CannotReachTheCycle()
    {
        var w = await SeedAsync(fx);
        var cycle = await ConfiguredCycleAsync(fx, w);
        var other = await SeedAsync(fx);
        await using (var db = fx.CreateDb())
        {
            await FluentActions.Invoking(() => Service(db).GetCycleAsync(Hr(other), cycle.Id, default)).Should().ThrowAsync<CompNotFoundException>();
            (await Service(db).ListCyclesAsync(Hr(other), default)).Should().NotContain(c => c.Id == cycle.Id);
        }
        // A user scoped to company B of the same tenant: the DbContext company filter hides company A's cycle.
        var accessor = new HttpContextAccessor
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(new[]
                {
                    new Claim("tenant_id", w.Tenant.ToString()), new Claim(ClaimTypes.NameIdentifier, w.UserHr.ToString()),
                    new Claim("entity_access", JsonSerializer.Serialize(new { c = w.CompanyB, r = "Viewer" })),
                }, "Test")),
            },
        };
        await using var scoped = fx.CreateDbWithAccessor(accessor);
        (await scoped.CompCycles.AnyAsync(c => c.Id == cycle.Id)).Should().BeFalse();
        await FluentActions.Invoking(() => Service(scoped).GetCycleAsync(Hr(w), cycle.Id, default)).Should().ThrowAsync<CompNotFoundException>();
        await FluentActions.Invoking(() => Service(scoped).CreateCycleAsync(Hr(w), new CreateCompCycleRequest(w.Company, "x", PeriodStart, PeriodEnd, Effective, Eligibility(w)), default))
            .Should().ThrowAsync<UnauthorizedAccessException>("company A is outside this user's scope; the DbContext write guard refuses it");
    }

    // ───────────────────────────── helpers ─────────────────────────────

    private async Task<int> CycleChangeCountAsync(Guid cycleId)
    {
        await using var db = fx.CreateDb();
        var ids = await db.CompWorksheetLines.Where(l => l.CycleId == cycleId && l.ChangeRequestId != null).Select(l => l.ChangeRequestId!.Value).ToListAsync();
        ids.Should().OnlyHaveUniqueItems();
        return await db.EmployeeChangeRequests.CountAsync(x => ids.Contains(x.Id));
    }

    private async Task GiveEveryonePayrollProfilesAsync(MeritWorld w)
    {
        await using var db = fx.CreateDb();
        var n = 0;
        foreach (var id in w.E.Values)
            db.EmployeePayrollProfiles.Add(new EmployeePayrollProfile
            {
                TenantId = w.Tenant, EmployeeId = id, Iban = "SA0380000000608010167519", MolId = $"MOL{++n:000000}", SalaryCurrency = "SAR", BankName = "Test Bank",
            });
        await db.SaveChangesAsync();
    }

    private async Task InstallEmployeeChangeWorkflowAsync(MeritWorld w)
    {
        await using var db = fx.CreateDb();
        var wf = new ApprovalWorkflow { TenantId = w.Tenant, Code = "EMP-CHANGE", Name = "Employee change", EntityName = nameof(EmployeeChangeRequest), IsDefault = true, IsActive = true };
        wf.Steps.Add(new ApprovalWorkflowStep { TenantId = w.Tenant, WorkflowId = wf.Id, StepOrder = 1, StepName = "HR", ApproverType = "Role", ApproverRole = "HR Manager", IsFinalStep = true });
        db.ApprovalWorkflows.Add(wf);
        await db.SaveChangesAsync();
    }

    private async Task<Guid> ProcessRunAsync(MeritWorld w, int year, int month)
    {
        await using var db = fx.CreateDb();
        var run = new PayrollRun { TenantId = w.Tenant, CompanyId = w.Company, Year = year, Month = month, CreatedAtUtc = new DateTime(year, month, 1, 0, 0, 0, DateTimeKind.Utc) };
        db.PayrollRuns.Add(run);
        await db.SaveChangesAsync();
        var result = await Payroll(db, w.Tenant).Process(run.Id, default);
        result.Should().BeOfType<OkObjectResult>(because: JsonSerializer.Serialize((result as ObjectResult)?.Value));
        return run.Id;
    }

    private async Task<decimal> SlipBasicAsync(Guid runId, int employeeId)
    {
        await using var db = fx.CreateDb();
        return await db.PayrollSlips.Where(s => s.RunId == runId && s.EmployeeId == employeeId).Select(s => s.BasicSalary).SingleAsync();
    }
}
