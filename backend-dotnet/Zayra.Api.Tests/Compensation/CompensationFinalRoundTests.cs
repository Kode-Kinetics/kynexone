using System.Security.Claims;
using System.Text;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Zayra.Api.Application.Employees;
using Zayra.Api.Controllers.Compensation;
using Zayra.Api.Infrastructure.Audit;
using Zayra.Api.Infrastructure.Compensation;
using Zayra.Api.Infrastructure.Employees;
using Zayra.Api.Models;
using static Zayra.Api.Tests.Compensation.MeritTestKit;

namespace Zayra.Api.Tests.Compensation;

/// <summary>Final review round: leavers after apply, single-award withdrawal, retro at apply, every editor barred from
/// deciding, percent precision, and the XLSX part cap — on real PostgreSQL.</summary>
[Trait("Category", "Integration")]
[Collection("Integration")]
public sealed class CompensationFinalRoundTests(PostgresFixture fx)
{
    private async Task<Dictionary<string, Guid>> ChangesAsync(MeritWorld w, Guid cycleId, params string[] codes)
    {
        await using var db = fx.CreateDb();
        var lines = await db.CompWorksheetLines.AsNoTracking().Where(l => l.CycleId == cycleId && l.ChangeRequestId != null).ToListAsync();
        return codes.ToDictionary(c => c, c => lines.Single(l => l.EmployeeId == w.E[c]).ChangeRequestId!.Value);
    }

    // ───────────── P2: a leaver after apply is not paid the award ─────────────

    [Fact]
    public async Task OnNoticeTerminatedOrRegradedAfterApply_TheAwardIsCancelledOnTheEffectiveDate_HrIsNotified_TheRestApply()
    {
        var w = await SeedAsync(fx);
        var applied = await AppliedAsync(fx, w);
        var changes = await ChangesAsync(w, applied.Id, "A1", "A2", "A3", "A4");
        await using (var db = fx.CreateDb())
        {
            // After apply, before the effective date: A2 hands in notice (offboarding opened), A3's status leaves the
            // cycle's eligible statuses without being one the job treats as separated, A4 moves to grade G2.
            db.EmployeeOffboardings.Add(new EmployeeOffboarding { TenantId = w.Tenant, EmployeeId = w.E["A2"], NoticeDate = new DateOnly(2026, 10, 20), NoticePeriodDays = 30, LastWorkingDay = new DateOnly(2026, 11, 19), Status = "InProgress" });
            (await db.Employees.SingleAsync(e => e.Id == w.E["A3"])).Status = "Suspended";
            (await db.Employees.SingleAsync(e => e.Id == w.E["A4"])).GradeId = w.G2;
            await db.SaveChangesAsync();
        }

        (await RunJobAsync(fx, w.Tenant, changes["A1"], Effective)).Outcome.Should().Be(EffectiveChangeOutcome.Applied);
        foreach (var (code, why) in new[] { ("A2", "On notice"), ("A3", "Status is Suspended"), ("A4", "grade changed") })
        {
            var result = await RunJobAsync(fx, w.Tenant, changes[code], Effective);
            result.Outcome.Should().Be(EffectiveChangeOutcome.Cancelled, code);
            result.ReasonCode.Should().Be(MeritEffectiveChangeGate.ReasonCode);
            result.Reason.Should().Contain(why);
            result.NotifyUserId.Should().Be(w.UserHr, "HR, who scheduled the awards, is told");
            (await RunJobAsync(fx, w.Tenant, changes[code], Effective)).Outcome.Should().Be(EffectiveChangeOutcome.AlreadyHandled, "decided exactly once");
        }

        await using var verify = fx.CreateDb();
        (await verify.EmployeeChangeRequests.AsNoTracking().SingleAsync(c => c.Id == changes["A2"])).Status.Should().Be(EmployeeChangeStatuses.Cancelled);
        (await verify.EmployeeSalaryStructures.AsNoTracking().AnyAsync(s => s.EmployeeId == w.E["A2"] && s.EffectiveDate == Effective)).Should().BeFalse("nothing is written for a cancelled award");
        (await verify.AuditLogs.IgnoreQueryFilters().CountAsync(a => a.EntityId == changes["A2"].ToString() && a.Action == EffectiveChangeJobHandler.CancelledAction)).Should().Be(1);
        (await verify.EmployeeHistories.IgnoreQueryFilters().CountAsync(h => h.EmployeeId == w.E["A2"] && h.EventType == EffectiveChangeJobHandler.CancelledEventType)).Should().Be(1);
        var view = await Service(verify).GetCycleAsync(Hr(w), applied.Id, default);
        var a2 = view.Apply!.Lines.Single(l => l.EmployeeId == w.E["A2"]);
        (a2.ChangeStatus, a2.BlockedReason).Should().Match<(string?, string?)>(x => x.Item1 == EmployeeChangeStatuses.Cancelled && x.Item2!.Contains("no longer eligible"));
        view.Apply.Applied.Should().Be(1);
        view.Apply.Cancelled.Should().Be(3);
    }

    [Fact]
    public async Task TheGate_IgnoresChangesThatDidNotComeFromAMeritCycle_AndIsRegisteredByAddCompensation()
    {
        var w = await SeedAsync(fx);
        await using var db = fx.CreateDb();
        var employee = await db.Employees.AsNoTracking().SingleAsync(e => e.Id == w.E["A1"]);
        var other = new EmployeeChangeRequest { Id = Guid.NewGuid(), TenantId = w.Tenant, EmployeeId = employee.Id, EffectiveDate = Effective };
        (await new MeritEffectiveChangeGate().CheckAsync(db, w.Tenant, other, employee, Effective, default)).Should().BeNull();
        new ServiceCollection().AddCompensation().Should().Contain(d => d.ServiceType == typeof(IEffectiveChangeGate) && d.ImplementationType == typeof(MeritEffectiveChangeGate));
    }

    // ───────────── P2: withdraw one award without cancelling the cycle ─────────────

    [Fact]
    public async Task HrWithdrawsOneScheduledAward_TheRestStayScheduled_AnAppliedAwardMustBeReversedInstead()
    {
        var w = await SeedAsync(fx);
        var applied = await AppliedAsync(fx, w);
        var changes = await ChangesAsync(w, applied.Id, "A1", "A2");
        Guid a1Line;
        await using (var db = fx.CreateDb())
            a1Line = (await db.CompWorksheetLines.AsNoTracking().SingleAsync(l => l.ChangeRequestId == changes["A1"])).Id;

        await using (var db = fx.CreateDb())
        {
            var svc = Service(db);
            await FluentActions.Invoking(() => svc.WithdrawAwardAsync(Manager(w), a1Line, new WithdrawCompAwardRequest("Not my call"), default))
                .Should().ThrowAsync<CompForbiddenException>();
            await FluentActions.Invoking(() => svc.WithdrawAwardAsync(Hr(w), a1Line, new WithdrawCompAwardRequest(" "), default))
                .Should().ThrowAsync<CompRuleException>().Where(e => e.Code == "reason_required");
            var line = await svc.WithdrawAwardAsync(Hr(w), a1Line, new WithdrawCompAwardRequest("Performance concern raised after the cycle closed."), default);
            line.ChangeStatus.Should().Be(EmployeeChangeStatuses.Cancelled);
            line.BlockedReason.Should().StartWith("Withdrawn by HR");
            await FluentActions.Invoking(() => svc.WithdrawAwardAsync(Hr(w), a1Line, new WithdrawCompAwardRequest("Again"), default))
                .Should().ThrowAsync<CompRuleException>().Where(e => e.Code == "award_closed");
        }
        (await RunJobAsync(fx, w.Tenant, changes["A1"], Effective)).Outcome.Should().Be(EffectiveChangeOutcome.AlreadyHandled, "a withdrawn award never reaches pay");
        (await RunJobAsync(fx, w.Tenant, changes["A2"], Effective)).Outcome.Should().Be(EffectiveChangeOutcome.Applied);

        await using var verify = fx.CreateDb();
        var cycle = await verify.CompCycles.AsNoTracking().SingleAsync(c => c.Id == applied.Id);
        cycle.Status.Should().Be(CompCycleStatuses.Applied, "the cycle is not cancelled");
        (await verify.EmployeeChangeRequests.AsNoTracking().CountAsync(c => changes.Values.Contains(c.Id) && c.Status == EmployeeChangeStatuses.ApprovedApplied)).Should().Be(1);
        var others = await (from l in verify.CompWorksheetLines.AsNoTracking()
                            join c in verify.EmployeeChangeRequests.AsNoTracking() on l.ChangeRequestId equals c.Id
                            where l.CycleId == applied.Id && c.Id != changes["A1"] && c.Id != changes["A2"]
                            select c.Status).ToListAsync();
        others.Should().NotBeEmpty().And.OnlyContain(s => s == EmployeeChangeStatuses.ApprovedPendingEffectiveDate);
        var audit = await verify.AuditLogs.IgnoreQueryFilters().SingleAsync(a => a.EntityId == changes["A1"].ToString() && a.Action == "compensation.award.withdrawn");
        audit.Metadata.Should().Contain("Performance concern").And.Contain("\"single\":true");
        (await Service(verify).GetCycleAsync(Hr(w), applied.Id, default)).Apply!.Cancelled.Should().Be(1);

        Guid a2Line = (await verify.CompWorksheetLines.AsNoTracking().SingleAsync(l => l.ChangeRequestId == changes["A2"])).Id;
        await FluentActions.Invoking(() => Service(verify).WithdrawAwardAsync(Hr(w), a2Line, new WithdrawCompAwardRequest("Too late"), default))
            .Should().ThrowAsync<CompRuleException>().Where(e => e.Code == "award_effective");
    }

    // ───────────── P3: a payroll month run between approval and apply ─────────────

    [Fact]
    public async Task APayrollMonthRunBetweenApprovalAndApply_NeedsAnApproversAcknowledgementAtApply()
    {
        var w = await SeedAsync(fx);
        var (cycle, a, b) = await SubmittedAsync(fx, w);
        await ApproveBothStepsAsync(fx, w, a.ApprovalRequestId!.Value);
        await ApproveBothStepsAsync(fx, w, b.ApprovalRequestId!.Value);
        await using (var db = fx.CreateDb())
        {
            var svc = Service(db);
            var view = await svc.GetCycleAsync(Hr(w), cycle.Id, default);
            view = await svc.StartReviewAsync(Hr(w), cycle.Id, new CompCycleTransitionRequest(view.Version), default);
            view = await svc.ApproveCycleAsync(Finance(w), cycle.Id, new CompCycleTransitionRequest(view.Version), default);
            view.Retro.Should().BeNull("nothing was paid when it was approved");
        }
        await using (var db = fx.CreateDb())
        {
            db.PayrollRuns.Add(new PayrollRun { TenantId = w.Tenant, CompanyId = w.Company, Year = Effective.Year, Month = Effective.Month, Status = "Processed", CreatedAtUtc = DateTime.UtcNow });
            await db.SaveChangesAsync();
        }
        var hrWithoutApprove = Caller(w, w.UserHr2, "HR Two", w.E["HR-2"], ["HR Manager"], HrPermissions.Where(p => p != CompPermissions.Approve).ToArray());
        await using (var db = fx.CreateDb())
        {
            var svc = Service(db);
            var refused = await FluentActions.Invoking(() => svc.ApplyAsync(Hr(w), cycle.Id, default)).Should().ThrowAsync<CompRuleException>();
            refused.Which.Code.Should().Be("retro_acknowledgement_required");
            refused.Which.Message.Should().Contain($"{Effective:yyyy-MM}").And.Contain("2,923.59 SAR");
            await FluentActions.Invoking(() => svc.ApplyAsync(hrWithoutApprove, cycle.Id, default, acknowledgeRetroCost: true))
                .Should().ThrowAsync<CompForbiddenException>().Where(e => e.Code == "approver_required");
            (await db.EmployeeChangeRequests.AsNoTracking().CountAsync(c => c.TenantId == w.Tenant && c.Status == EmployeeChangeStatuses.ApprovedPendingEffectiveDate)).Should().Be(0, "nothing was scheduled");
        }
        await using (var db = fx.CreateDb())
        {
            var view = await Service(db).ApplyAsync(Hr(w), cycle.Id, default, acknowledgeRetroCost: true);
            view.Apply!.Scheduled.Should().BeGreaterThan(0);
            (await db.CompCycles.AsNoTracking().SingleAsync(c => c.Id == cycle.Id)).RetroAcknowledgedPeriods.Should().Be($"{Effective:yyyy-MM}");
            (await db.AuditLogs.IgnoreQueryFilters().SingleAsync(x => x.EntityId == cycle.Id.ToString() && x.Action == "compensation.cycle.retro_acknowledged_at_apply"))
                .Metadata.Should().Contain("2923.59");
            // Re-running apply with nothing new paid needs no further acknowledgement.
            await Service(db).ApplyAsync(Hr(w), cycle.Id, default);
        }
    }

    // ───────────── P3: every editor of the revision is barred ─────────────

    [Fact]
    public async Task EveryoneWhoEditedTheRevision_IsBarredFromDecidingIt_NotOnlyTheLastEditor()
    {
        var w = await SeedAsync(fx);
        var cycle = await OpenAsync(fx, w, await ConfiguredCycleAsync(fx, w));
        var sheet = await WorksheetOfAsync(fx, w, cycle.Id, managerB: true);
        await using (var db = fx.CreateDb())
        {
            var svc = Service(db);
            sheet = await svc.SaveWorksheetAsync(Hr(w, second: true), sheet.Id, new SaveCompWorksheetRequest(sheet.Version, null, ProposalsB(w)), default);
            // The manager saves after HR-2, so HR-2 is no longer the LAST editor — but did change this revision.
            sheet = await svc.SaveWorksheetAsync(Manager(w, true), sheet.Id, new SaveCompWorksheetRequest(sheet.Version, null, ProposalsB(w)), default);
            sheet = await svc.SubmitWorksheetAsync(Manager(w, true), sheet.Id, new SubmitCompWorksheetRequest(sheet.Version), default);
            (await db.CompWorksheets.AsNoTracking().SingleAsync(s => s.Id == sheet.Id)).UpdatedByUserId.Should().Be(w.UserManagerB);
        }
        await FluentActions.Invoking(() => DecideAsync(fx, w, sheet.ApprovalRequestId!.Value, w.UserHr2, ["HR Manager"], "Approve", "ok"))
            .Should().ThrowAsync<InvalidOperationException>().WithMessage("*changed or submitted this revision*");
        // HR-1 returns it; the new revision starts with no editors, so HR-2 may decide the resubmission.
        await DecideAsync(fx, w, sheet.ApprovalRequestId!.Value, w.UserHr, ["HR Manager"], "Reject", "Recheck B4.");
        await using (var db = fx.CreateDb())
        {
            var stored = await db.CompWorksheets.AsNoTracking().SingleAsync(s => s.Id == sheet.Id);
            (stored.Revision, stored.RevisionEditorIds).Should().Be((2, ""));
            var svc = Service(db);
            sheet = await svc.GetWorksheetAsync(Manager(w, true), sheet.Id, default);
            sheet = await svc.SubmitWorksheetAsync(Manager(w, true), sheet.Id, new SubmitCompWorksheetRequest(sheet.Version), default);
        }
        await DecideAsync(fx, w, sheet.ApprovalRequestId!.Value, w.UserHr2, ["HR Manager"], "Approve", "revision 2 was not edited by HR-2");
    }

    // ───────────── P3: entered percent precision ─────────────

    [Fact]
    public async Task APercentWithMoreThanFourDecimals_IsA400_AndNothingIsSaved()
    {
        var w = await SeedAsync(fx);
        var cycle = await OpenAsync(fx, w, await ConfiguredCycleAsync(fx, w));
        var sheet = await WorksheetOfAsync(fx, w, cycle.Id);
        await using (var db = fx.CreateDb())
        {
            var bad = new SaveCompWorksheetRequest(sheet.Version, null, [Pct(w, "A1", 3.12345m)]);
            await FluentActions.Invoking(() => Service(db).SaveWorksheetAsync(Manager(w), sheet.Id, bad, default))
                .Should().ThrowAsync<CompBadRequestException>().Where(e => e.Code == "percent_precision");

            var ctrl = new MeritPlanningController(db, Service(db), new PayBenchmarkService(db, new AuditService(db)), new PayEquityService(db, new AuditService(db)))
            {
                ControllerContext = new ControllerContext
                {
                    HttpContext = new DefaultHttpContext
                    {
                        User = new ClaimsPrincipal(new ClaimsIdentity(new[]
                        {
                            new Claim("tenant_id", w.Tenant.ToString()), new Claim(ClaimTypes.NameIdentifier, w.UserManagerA.ToString()),
                            new Claim(ClaimTypes.Role, "Manager"), new Claim("permission", CompPermissions.Plan),
                        }, "Test")),
                    },
                },
            };
            var result = await ctrl.SaveWorksheet(sheet.Id, bad, default);
            result.Should().BeOfType<BadRequestObjectResult>();
        }
        await using (var db = fx.CreateDb())
        {
            (await db.CompWorksheets.AsNoTracking().SingleAsync(s => s.Id == sheet.Id)).Version.Should().Be(sheet.Version, "nothing was saved");
            var ok = await Service(db).SaveWorksheetAsync(Manager(w), sheet.Id, new SaveCompWorksheetRequest(sheet.Version, null, [Pct(w, "A1", 3.1234m)]), default);
            ok.Lines.Single(l => l.EmployeeId == w.E["A1"]).ProposedPercent.Should().Be(3.1234m);
        }
    }

    // ───────────── P3: XLSX part cap ─────────────

    [Fact]
    public async Task AnXlsxPartOverTwoMillionCharacters_IsRefused()
    {
        PayBenchmarkService.MaxCharactersInPart.Should().Be(2_000_000);
        var w = await SeedAsync(fx);
        await using var db = fx.CreateDb();
        var bench = new PayBenchmarkService(db, new AuditService(db));
        var meta = new PayBenchmarkImportMeta(w.Company, "Synthetic", "Fixture", "Synthetic fixture, test only.", new DateOnly(2026, 6, 1), "SA", "SAR", PayBenchmarkBases.MonthlyBasic, true);
        var huge = CompensationFixRoundTests.Xlsx(("A1", "job_family"), ("A2", new string('x', 2_100_000)));
        var result = await bench.ImportAsync(Hr(w), meta, "huge.xlsx", huge, default);
        result.Set.Should().BeNull();
        result.Issues.Should().ContainSingle().Which.Message.Should().StartWith("The file could not be read", "the part is refused while it is read, not after");
        (await db.PayBenchmarkSets.AsNoTracking().AnyAsync(s => s.TenantId == w.Tenant)).Should().BeFalse();
        // A normal sheet still imports.
        var fine = Encoding.UTF8.GetBytes("job_family,level,sample_size,p25,p50,p75,p90\nEngineering,L1,10,5800,6500,7200,8000\n");
        (await bench.ImportAsync(Hr(w), meta, "fine.csv", fine, default)).Set.Should().NotBeNull();
    }
}
