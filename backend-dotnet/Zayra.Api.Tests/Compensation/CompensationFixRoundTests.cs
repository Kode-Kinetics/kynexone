using System.Security.Claims;
using System.Text;
using System.Text.Json;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Zayra.Api.Application.Approvals;
using Zayra.Api.Application.Employees;
using Zayra.Api.Controllers;
using Zayra.Api.Infrastructure.Audit;
using Zayra.Api.Infrastructure.Compensation;
using Zayra.Api.Infrastructure.Employees;
using Zayra.Api.Models;
using static Zayra.Api.Tests.Compensation.MeritTestKit;

namespace Zayra.Api.Tests.Compensation;

/// <summary>Review fix round (P1-2, P1-3, P2-4…P2-9, P2-12) on real PostgreSQL.</summary>
[Trait("Category", "Integration")]
[Collection("Integration")]
public sealed class CompensationFixRoundTests(PostgresFixture fx)
{
    // ───────────── P1-2: eligibility is re-evaluated at apply ─────────────

    [Fact]
    public async Task ResignedTerminatedOrRegradedAfterApproval_TheAwardIsRefusedAtApply_TheRestAreScheduled()
    {
        var w = await SeedAsync(fx);
        var approved = await ApprovedCycleAsync(w);
        await using (var db = fx.CreateDb())
        {
            // A2 resigns (offboarding opened), A3 is terminated, A4 moves to grade G2 — all after approval, before apply.
            db.EmployeeOffboardings.Add(new EmployeeOffboarding { TenantId = w.Tenant, EmployeeId = w.E["A2"], NoticeDate = new DateOnly(2026, 10, 5), NoticePeriodDays = 30, LastWorkingDay = new DateOnly(2026, 11, 4), Status = "InProgress" });
            (await db.Employees.SingleAsync(e => e.Id == w.E["A3"])).Status = "Terminated";
            (await db.Employees.SingleAsync(e => e.Id == w.E["A4"])).GradeId = w.G2;
            await db.SaveChangesAsync();
        }
        CompCycleDto applied;
        await using (var db = fx.CreateDb()) applied = await Service(db).ApplyAsync(Hr(w), approved.Id, default);
        var lines = applied.Apply!.Lines.ToDictionary(l => l.EmployeeId);
        lines[w.E["A2"]].BlockedReason.Should().StartWith("No longer eligible").And.Contain("On notice");
        lines[w.E["A3"]].BlockedReason.Should().Contain("Terminated");
        lines[w.E["A4"]].BlockedReason.Should().Contain("grade changed");
        applied.Apply.Scheduled.Should().Be(8);
        lines[w.E["A2"]].ChangeRequestId.Should().BeNull();
    }

    // ───────────── P1-3: stranded worksheets, coverage, assignment ─────────────

    [Fact]
    public async Task ManagerRemovedMidReview_WorksheetReturns_TeamBecomesUncovered_HrReassignsAndCloses()
    {
        var w = await SeedAsync(fx);
        var (cycle, _, b) = await SubmittedAsync(fx, w);
        // Manager B is removed while B's worksheet waits for approval.
        await using (var db = fx.CreateDb())
        {
            var ctrl = HrmHierarchyTests.BuildImportControllerInternal(db, w.Tenant);
            ctrl.HttpContext.User = new ClaimsPrincipal(new ClaimsIdentity(new[]
            {
                new Claim("tenant_id", w.Tenant.ToString()), new Claim(ClaimTypes.NameIdentifier, w.UserHr.ToString()),
                new Claim(ClaimTypes.Role, "Admin"), new Claim("permission", "employees.write"), new Claim("permission", "employees.delete"),
            }, "Test"));
            (await ctrl.Delete(w.ManagerB, default)).Should().BeOfType<NoContentResult>();
        }
        await using (var db = fx.CreateDb())
        {
            var sheet = await db.CompWorksheets.AsNoTracking().SingleAsync(s => s.Id == b.Id);
            (sheet.Status, sheet.Revision).Should().Be((CompWorksheetStatuses.Returned, 2), "a worksheet never stays Submitted without a live approval");
            sheet.DecisionComments.Should().Contain("closed without a decision");
            (await db.ApprovalRequests.AsNoTracking().SingleAsync(a => a.Id == b.ApprovalRequestId)).Status.Should().Be("Cancelled");
            (await db.AuditLogs.IgnoreQueryFilters().CountAsync(a => a.EntityId == b.Id.ToString() && a.Action == "compensation.worksheet.approval_closed")).Should().Be(1);

            var svc = Service(db);
            var view = await svc.GetCycleAsync(Hr(w), cycle.Id, default);
            view.Uncovered.Select(u => u.EmployeeId).Should().Contain([w.E["B1"], w.E["B6"]]);
            await FluentActions.Invoking(async () => { var s = await svc.GetWorksheetAsync(Hr(w), b.Id, default); await svc.SubmitWorksheetAsync(Hr(w), b.Id, new SubmitCompWorksheetRequest(s.Version), default); })
                .Should().ThrowAsync<CompRuleException>().Where(e => e.Code == "owner_inactive");

            // HR opens a worksheet for HR-2 (no team of their own) and assigns B's people to it.
            var hr2Sheet = await svc.CreateWorksheetAsync(Hr(w), cycle.Id, w.E["HR-2"], default);
            hr2Sheet.Lines.Should().BeEmpty();
            foreach (var code in new[] { "B1", "B2", "B3", "B4", "B5", "B6" })
                hr2Sheet = await svc.AssignAsync(Hr(w), hr2Sheet.Id, w.E[code], default);
            hr2Sheet.Lines.Should().HaveCount(6).And.OnlyContain(l => l.AssignedByHr);
            (await svc.GetCycleAsync(Hr(w), cycle.Id, default)).Uncovered.Should().BeEmpty();
            await FluentActions.Invoking(() => svc.AssignAsync(Hr(w), hr2Sheet.Id, w.E["HR-2"], default)).Should().ThrowAsync<CompForbiddenException>().Where(e => e.Code == "self_award");
            // The removed manager's worksheet now has nobody left; HR closes it.
            (await svc.GetWorksheetAsync(Hr(w), b.Id, default)).Lines.Should().BeEmpty();
            var closed = await svc.CloseWorksheetAsync(Hr(w), b.Id, "Owner left the company; team moved to HR-2.", default);
            closed.Worksheets.Should().Contain(s => s.Id == b.Id && s.Status == CompWorksheetStatuses.Cancelled);
        }
    }

    [Fact]
    public async Task ReassignedAfterPlanningOpened_ToAManagerWithoutAWorksheet_IsUncovered_UntilHrOpensOne()
    {
        var w = await SeedAsync(fx);
        var cycle = await OpenAsync(fx, w, await ConfiguredCycleAsync(fx, w));
        await using (var db = fx.CreateDb())
        {
            (await db.Employees.SingleAsync(e => e.Id == w.E["B6"])).ManagerEmployeeId = w.E["HR-1"];
            await db.SaveChangesAsync();
        }
        await using (var db = fx.CreateDb())
        {
            var svc = Service(db);
            var view = await svc.GetCycleAsync(Hr(w), cycle.Id, default);
            view.Uncovered.Should().ContainSingle(u => u.EmployeeId == w.E["B6"]).Which.Reason.Should().Contain("has no worksheet in this cycle");
            (await WorksheetOfAsync(fx, w, cycle.Id, managerB: true)).Lines.Should().NotContain(l => l.EmployeeId == w.E["B6"]);
            var sheet = await svc.CreateWorksheetAsync(Hr(w), cycle.Id, w.E["HR-1"], default);
            sheet.Lines.Should().ContainSingle(l => l.EmployeeId == w.E["B6"]);
            (await svc.GetCycleAsync(Hr(w), cycle.Id, default)).Uncovered.Should().BeEmpty();
        }
    }

    // ───────────── P2-5: benchmark reads ─────────────

    [Fact]
    public async Task AManagerSeesOnlyTheMarketPositionOfTheirLines_NeverThePercentileTables()
    {
        var w = await SeedAsync(fx);
        await using var db = fx.CreateDb();
        var bench = new PayBenchmarkService(db, new AuditService(db));
        var set = (await bench.ImportAsync(Hr(w), new PayBenchmarkImportMeta(w.Company, "Synthetic", "Fixture", "Synthetic fixture, test only.", new DateOnly(2026, 6, 1), "SA", "SAR", PayBenchmarkBases.MonthlyBasic, true),
            "s.csv", Encoding.UTF8.GetBytes("job_family,level,sample_size,p25,p50,p75,p90\nEngineering,L1,10,5800,6500,7200,8000\n"), default)).Set!;
        var (_, rows) = await bench.GetAsync(Hr(w), set.Id, default);
        await bench.MapRowAsync(Hr(w), rows[0].Id, new MapBenchmarkRowRequest(w.DesigEng, null), default);
        await FluentActions.Invoking(() => bench.ListAsync(Manager(w), null, default)).Should().ThrowAsync<CompForbiddenException>();
        await FluentActions.Invoking(() => bench.GetAsync(Manager(w), set.Id, default)).Should().ThrowAsync<CompForbiddenException>();
        var cycle = await OpenAsync(fx, w, await ConfiguredCycleAsync(fx, w));
        var asManager = (await WorksheetOfAsync(fx, w, cycle.Id)).Lines.Single(l => l.EmployeeId == w.E["A1"]).Benchmark;
        (asManager.State, asManager.Position, asManager.P50Cycle).Should().Be(("current", 0.9231m, (decimal?)null));
        var asHr = (await Service(db).GetWorksheetAsync(Hr(w), (await WorksheetOfAsync(fx, w, cycle.Id)).Id, default)).Lines.Single(l => l.EmployeeId == w.E["A1"]).Benchmark;
        asHr.P50Cycle.Should().Be(6500.00m);
    }

    // ───────────── P2-6: the last editor does not decide ─────────────

    [Fact]
    public async Task HrWhoEditedAManagersWorksheet_CannotDecideItsApproval()
    {
        var w = await SeedAsync(fx);
        var cycle = await OpenAsync(fx, w, await ConfiguredCycleAsync(fx, w));
        var sheet = await WorksheetOfAsync(fx, w, cycle.Id, managerB: true);
        await using (var db = fx.CreateDb())
        {
            var svc = Service(db);
            sheet = await svc.SaveWorksheetAsync(Hr(w, second: true), sheet.Id, new SaveCompWorksheetRequest(sheet.Version, null, ProposalsB(w)), default);
            sheet = await svc.SubmitWorksheetAsync(Manager(w, true), sheet.Id, new SubmitCompWorksheetRequest(sheet.Version), default);
        }
        await FluentActions.Invoking(() => DecideAsync(fx, w, sheet.ApprovalRequestId!.Value, w.UserHr2, ["HR Manager"], "Approve", "ok"))
            .Should().ThrowAsync<InvalidOperationException>().WithMessage("*changed or submitted this revision*");
        await DecideAsync(fx, w, sheet.ApprovalRequestId!.Value, w.UserHr, ["HR Manager"], "Approve", "a different HR approver");
    }

    // ───────────── P2-7: budgets and FX lock once anything is submitted ─────────────

    [Fact]
    public async Task BudgetsAndFxLockOnceSubmitted_ReopenPlanningReturnsTheWorksheets_CancelDuringReview()
    {
        var w = await SeedAsync(fx);
        var (cycle, a, b) = await SubmittedAsync(fx, w);
        await ApproveBothStepsAsync(fx, w, a.ApprovalRequestId!.Value);
        await using (var db = fx.CreateDb())
        {
            var svc = Service(db);
            await FluentActions.Invoking(() => svc.SaveBudgetsAsync(Hr(w), cycle.Id, new SaveCompBudgetsRequest([new(w.Eng, CompBudgetPoolTypes.PercentOfEligibleBasic, 10m, null)]), default))
                .Should().ThrowAsync<CompRuleException>().Where(e => e.Code == "planning_locked");
            await FluentActions.Invoking(() => svc.SaveFxAsync(Hr(w), cycle.Id, new SaveCompFxRequest([new("AED", 1.10m, "changed", new DateOnly(2026, 10, 1))]), default))
                .Should().ThrowAsync<CompRuleException>().Where(e => e.Code == "planning_locked");

            var view = await svc.GetCycleAsync(Hr(w), cycle.Id, default);
            view = await svc.ReopenPlanningAsync(Hr(w), cycle.Id, new CompCycleTransitionRequest(view.Version, "Budget raised by the CFO"), default);
            view.Worksheets.Where(s => s.IsPrimary).Should().OnlyContain(s => s.Status == CompWorksheetStatuses.Returned && s.Revision == 2);
            (await db.ApprovalRequests.AsNoTracking().SingleAsync(x => x.Id == b.ApprovalRequestId)).Status.Should().Be("Cancelled", "the pending approval is closed");
            view = await svc.SaveBudgetsAsync(Hr(w), cycle.Id, new SaveCompBudgetsRequest([new(w.Eng, CompBudgetPoolTypes.PercentOfEligibleBasic, 5m, null), new(w.Ops, CompBudgetPoolTypes.FixedAmount, null, 1500m)]), default);
            view.Budgets.Single(x => x.DepartmentId == w.Eng).Pool.Should().Be(1981.00m); // 39,620.00 × 5 %
        }

        // Cancel during Review.
        var w2 = await SeedAsync(fx);
        var (c2, a2, b2) = await SubmittedAsync(fx, w2);
        await ApproveBothStepsAsync(fx, w2, a2.ApprovalRequestId!.Value);
        await ApproveBothStepsAsync(fx, w2, b2.ApprovalRequestId!.Value);
        await using (var db = fx.CreateDb())
        {
            var svc = Service(db);
            var view = await svc.GetCycleAsync(Hr(w2), c2.Id, default);
            view = await svc.StartReviewAsync(Hr(w2), c2.Id, new CompCycleTransitionRequest(view.Version), default);
            view = await svc.CancelAsync(Hr(w2), c2.Id, new CompCycleTransitionRequest(view.Version, "Freeze"), default);
            view.Status.Should().Be(CompCycleStatuses.Cancelled);
            view.Worksheets.Should().OnlyContain(s => s.Status == CompWorksheetStatuses.Cancelled);
            (await db.EmployeeChangeRequests.CountAsync(x => x.TenantId == w2.Tenant)).Should().Be(0);
        }
    }

    // ───────────── P2-9: past effective dates ─────────────

    [Fact]
    public async Task APastEffectiveDate_ShowsTheRetroCost_AndApprovingNeedsAnAcknowledgement()
    {
        var w = await SeedAsync(fx);
        var effective = new DateOnly(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1).AddMonths(-1);
        await using (var db = fx.CreateDb())
        {
            db.PayrollRuns.Add(new PayrollRun { TenantId = w.Tenant, CompanyId = w.Company, Year = effective.Year, Month = effective.Month, Status = "Locked", CreatedAtUtc = DateTime.UtcNow });
            await db.SaveChangesAsync();
        }
        var (cycle, a, b) = await SubmittedAsync(fx, w, effective);
        await ApproveBothStepsAsync(fx, w, a.ApprovalRequestId!.Value);
        await ApproveBothStepsAsync(fx, w, b.ApprovalRequestId!.Value);
        await using var db2 = fx.CreateDb();
        var svc = Service(db2);
        var view = await svc.GetCycleAsync(Hr(w), cycle.Id, default);
        view.Retro!.PaidPeriods.Should().Equal($"{effective:yyyy-MM}");
        view = await svc.StartReviewAsync(Hr(w), cycle.Id, new CompCycleTransitionRequest(view.Version), default);
        view.Retro!.RetroCostCycle.Should().Be(2923.59m, "one month already paid × the monthly increase");
        var refused = await FluentActions.Invoking(() => svc.ApproveCycleAsync(Finance(w), cycle.Id, new CompCycleTransitionRequest(view.Version), default)).Should().ThrowAsync<CompRuleException>();
        refused.Which.Code.Should().Be("retro_acknowledgement_required");
        refused.Which.Message.Should().Contain("2,923.59 SAR");
        view = await svc.ApproveCycleAsync(Finance(w), cycle.Id, new CompCycleTransitionRequest(view.Version, null, AcknowledgeRetroCost: true), default);
        view.Status.Should().Be(CompCycleStatuses.Approved);
        (await db2.AuditLogs.IgnoreQueryFilters().SingleAsync(x => x.EntityId == cycle.Id.ToString() && x.Action == "compensation.cycle.approved")).Metadata.Should().Contain("\"retroAcknowledged\":true");
    }

    [Fact]
    public async Task AReversalIntoALockedPayrollMonth_NeedsAnApproversExplicitAcknowledgement()
    {
        var w = await SeedAsync(fx);
        var applied = await AppliedAsync(fx, w);
        Guid a1Line, a1Change;
        await using (var db = fx.CreateDb())
        {
            var line = await db.CompWorksheetLines.SingleAsync(l => l.CycleId == applied.Id && l.EmployeeId == w.E["A1"] && l.ChangeRequestId != null);
            (a1Line, a1Change) = (line.Id, line.ChangeRequestId!.Value);
            db.PayrollRuns.Add(new PayrollRun { TenantId = w.Tenant, CompanyId = w.Company, Year = Effective.Year, Month = Effective.Month, Status = "Locked", CreatedAtUtc = DateTime.UtcNow });
            await db.SaveChangesAsync();
        }
        (await RunJobAsync(fx, w.Tenant, a1Change, Effective)).Outcome.Should().Be(EffectiveChangeOutcome.Applied);
        await using (var db = fx.CreateDb())
        {
            var wf = new ApprovalWorkflow { TenantId = w.Tenant, Code = "EMP-CHANGE", Name = "Employee change", EntityName = nameof(EmployeeChangeRequest), IsDefault = true, IsActive = true };
            wf.Steps.Add(new ApprovalWorkflowStep { TenantId = w.Tenant, WorkflowId = wf.Id, StepOrder = 1, StepName = "HR", ApproverType = "Role", ApproverRole = "HR Manager", IsFinalStep = true });
            db.ApprovalWorkflows.Add(wf);
            await db.SaveChangesAsync();
            var svc = Service(db);
            var req = new ReverseCompAwardRequest(Effective, "Awarded in error");
            (await FluentActions.Invoking(() => svc.ReverseAwardAsync(Hr(w), a1Line, req, default)).Should().ThrowAsync<CompRuleException>()).Which.Code.Should().Be("locked_periods");
            var managerOnly = Caller(w, w.UserHr, "HR manage only", w.E["HR-1"], ["HR Manager"], [CompPermissions.Manage]);
            await FluentActions.Invoking(() => svc.ReverseAwardAsync(managerOnly, a1Line, req with { AcknowledgeLockedPeriods = true }, default)).Should().ThrowAsync<CompForbiddenException>();
            var ok = await svc.ReverseAwardAsync(Hr(w), a1Line, req with { AcknowledgeLockedPeriods = true }, default);
            ok.ReversalStatus.Should().Be(EmployeeChangeStatuses.PendingApproval);
            (await db.AuditLogs.IgnoreQueryFilters().SingleAsync(x => x.Action == "compensation.award.reversal_requested" && x.TenantId == w.Tenant)).Metadata.Should().Contain("\"lockedPeriodsAcknowledged\":true");
        }
    }

    [Fact]
    public async Task AReversalNobodyCouldDecide_IsRefusedAsARuleError_AndWritesNoChange()
    {
        var w = await SeedAsync(fx);
        var applied = await AppliedAsync(fx, w);
        Guid a1Line, a1Change;
        await using (var db = fx.CreateDb())
        {
            var line = await db.CompWorksheetLines.SingleAsync(l => l.CycleId == applied.Id && l.EmployeeId == w.E["A1"] && l.ChangeRequestId != null);
            (a1Line, a1Change) = (line.Id, line.ChangeRequestId!.Value);
        }
        (await RunJobAsync(fx, w.Tenant, a1Change, Effective)).Outcome.Should().Be(EffectiveChangeOutcome.Applied);
        await using (var db = fx.CreateDb())
        {
            // The employee-change workflow goes to a role nobody holds, and nobody holds Admin or approvals.override.
            var wf = new ApprovalWorkflow { TenantId = w.Tenant, Code = "EMP-CHANGE", Name = "Employee change", EntityName = nameof(EmployeeChangeRequest), IsDefault = true, IsActive = true };
            wf.Steps.Add(new ApprovalWorkflowStep { TenantId = w.Tenant, WorkflowId = wf.Id, StepOrder = 1, StepName = "Committee", ApproverType = "Role", ApproverRole = "Pay Committee", IsFinalStep = true });
            db.ApprovalWorkflows.Add(wf);
            await db.SaveChangesAsync();
            var before = await db.EmployeeChangeRequests.IgnoreQueryFilters().CountAsync(x => x.TenantId == w.Tenant && x.EmployeeId == w.E["A1"]);

            var refused = await FluentActions.Invoking(() => Service(db).ReverseAwardAsync(Hr(w), a1Line,
                new ReverseCompAwardRequest(Effective.AddMonths(1), "Awarded in error"), default)).Should().ThrowAsync<CompRuleException>();

            refused.Which.Code.Should().Be(ApprovalNoEligibleDeciderException.ErrorCode);
            refused.Which.Message.Should().Contain("Pay Committee");
            (await db.EmployeeChangeRequests.IgnoreQueryFilters().CountAsync(x => x.TenantId == w.Tenant && x.EmployeeId == w.E["A1"])).Should().Be(before);
            (await db.CompWorksheetLines.AsNoTracking().SingleAsync(l => l.Id == a1Line)).ReversalChangeRequestId.Should().BeNull();
        }
    }

    // ───────────── P2-12: concurrency ─────────────

    [Fact]
    public async Task ParallelWorksheetSaves_OneWinsOneConflicts_ParallelApply_SchedulesEachAwardOnce()
    {
        var w = await SeedAsync(fx);
        var cycle = await OpenAsync(fx, w, await ConfiguredCycleAsync(fx, w));
        var sheet = await WorksheetOfAsync(fx, w, cycle.Id);
        async Task<string> Save(decimal pct)
        {
            await using var db = fx.CreateDb();
            try { await Service(db).SaveWorksheetAsync(Manager(w), sheet.Id, new SaveCompWorksheetRequest(sheet.Version, null, [Pct(w, "A1", pct)]), default); return "ok"; }
            catch (CompConflictException) { return "conflict"; }
        }
        var saves = await Task.WhenAll(Task.Run(() => Save(3m)), Task.Run(() => Save(4m)));
        saves.Should().BeEquivalentTo(["ok", "conflict"]);

        var w2 = await SeedAsync(fx);
        var approved = await ApprovedCycleAsync(w2);
        async Task Apply() { await using var db = fx.CreateDb(); await Service(db).ApplyAsync(Hr(w2), approved.Id, default); }
        await Task.WhenAll(Task.Run(Apply), Task.Run(Apply), Task.Run(Apply));
        await using var verify = fx.CreateDb();
        var changes = await verify.CompWorksheetLines.Where(l => l.CycleId == approved.Id && l.ChangeRequestId != null).Select(l => l.ChangeRequestId).ToListAsync();
        changes.Should().HaveCount(11).And.OnlyHaveUniqueItems();
        (await verify.EmployeeChangeRequests.CountAsync(x => x.TenantId == w2.Tenant)).Should().Be(11);
    }

    // ───────────── P2-4: XLSX hardening ─────────────

    [Fact]
    public async Task APaddedXfdSheet_OrTooManyRows_IsRefusedWithoutReadingEverything()
    {
        var w = await SeedAsync(fx);
        await using var db = fx.CreateDb();
        var bench = new PayBenchmarkService(db, new AuditService(db));
        var meta = new PayBenchmarkImportMeta(w.Company, "x", "Fixture", "Synthetic fixture, test only.", new DateOnly(2026, 6, 1), "SA", "SAR", PayBenchmarkBases.MonthlyBasic, true);
        var padded = await bench.ImportAsync(Hr(w), meta, "padded.xlsx", Xlsx(("A1", "job_family"), ("XFD1", "x")), default);
        padded.Imported.Should().BeFalse();
        padded.Issues.Should().ContainSingle().Which.Message.Should().Contain("XFD1");
        var many = new List<(string, string)> { ("A1", "job_family") };
        for (var r = 2; r <= PayBenchmarkService.MaxRows + 60; r++) many.Add(($"A{r}", "Engineering"));
        (await bench.ImportAsync(Hr(w), meta, "many.xlsx", Xlsx(many.ToArray()), default)).Issues.Should().ContainSingle().Which.Message.Should().Contain("At most");
    }

    internal static byte[] Xlsx(params (string Ref, string Text)[] cells)
    {
        using var ms = new MemoryStream();
        using (var doc = SpreadsheetDocument.Create(ms, SpreadsheetDocumentType.Workbook))
        {
            var wb = doc.AddWorkbookPart();
            wb.Workbook = new Workbook();
            var ws = wb.AddNewPart<WorksheetPart>();
            var data = new SheetData();
            foreach (var group in cells.GroupBy(c => uint.Parse(new string(c.Ref.SkipWhile(char.IsLetter).ToArray()))))
            {
                var row = new Row { RowIndex = group.Key };
                foreach (var (r, t) in group) row.Append(new Cell { CellReference = r, DataType = CellValues.InlineString, InlineString = new InlineString(new Text(t)) });
                data.Append(row);
            }
            ws.Worksheet = new Worksheet(data);
            wb.Workbook.AppendChild(new Sheets(new Sheet { Id = wb.GetIdOfPart(ws), SheetId = 1, Name = "S" }));
        }
        return ms.ToArray();
    }

    // ───────────── P2-8: payroll salary import ─────────────

    [Fact]
    public async Task SalaryImport_DeduplicatesInMemory_AndIsAtomicWhenTheDatabaseRefusesAConcurrentDuplicate()
    {
        var w = await SeedAsync(fx);
        string Code(string k) { using var db = fx.CreateDb(); return db.Employees.AsNoTracking().Single(e => e.Id == w.E[k]).EmployeeCode; }
        var b5 = Code("B5"); var b6 = Code("B6");
        const string header = "EmployeeCode,SalaryStructureCode,BasicSalary,HousingAllowance,TransportAllowance,FoodAllowance,MobileAllowance,OtherAllowance,FixedDeduction,Currency,EffectiveDate";
        // Same employee and date twice in one file: the second row is refused, the first applied.
        await using (var db = fx.CreateDb())
        {
            var result = await Payroll(db, w.Tenant).ImportEmployeeSalaries(new ImportSalaryStructuresRequest($"{header}\n{b5},GRADE-G1,6900,1700,500,0,0,0,0,SAR,2027-02-01\n{b5},GRADE-G1,7000,1700,500,0,0,0,0,SAR,2027-02-01\n"), default);
            var body = JsonSerializer.Serialize((result as OkObjectResult)!.Value);
            body.Should().Contain("duplicates row 2");
        }
        await using (var db = fx.CreateDb())
            (await db.EmployeeSalaryStructures.SingleAsync(s => s.EmployeeId == w.E["B5"] && s.EffectiveDate == new DateOnly(2027, 2, 1) && s.IsActive)).BasicSalary.Should().Be(6900m);

        // A concurrent writer holds an uncommitted active row for (B6, 2027-03-01); it commits while the import waits on it.
        await using var rival = fx.CreateDb();
        await using var conn = (NpgsqlConnection)rival.Database.GetDbConnection();
        await conn.OpenAsync();
        await using var tx = await conn.BeginTransactionAsync();
        await using (var cmd = new NpgsqlCommand("INSERT INTO employee_salary_structures (id, tenant_id, employee_id, salary_structure_id, basic_salary, housing_allowance, transport_allowance, food_allowance, mobile_allowance, other_allowance, fixed_deduction, effective_date, currency, is_active, created_at_utc, housing_basis, transport_basis) VALUES (gen_random_uuid(), @t, @e, gen_random_uuid(), 7400, 0, 0, 0, 0, 0, 0, '2027-03-01', 'SAR', true, now(), 'Amount', 'Amount')", conn, tx))
        { cmd.Parameters.AddWithValue("t", w.Tenant); cmd.Parameters.AddWithValue("e", w.E["B6"]); await cmd.ExecuteNonQueryAsync(); }
        var import = Task.Run(async () =>
        {
            await using var db = fx.CreateDb();
            return await Payroll(db, w.Tenant).ImportEmployeeSalaries(new ImportSalaryStructuresRequest(
                $"{header}\n{b5},GRADE-G1,7100,1700,500,0,0,0,0,SAR,2027-03-01\n{b6},GRADE-G1,7300,1800,500,0,0,0,0,SAR,2027-03-01\n"), default);
        });
        await Task.Delay(1500);
        await tx.CommitAsync();
        var refused = await import;
        refused.Should().BeOfType<UnprocessableEntityObjectResult>();
        JsonSerializer.Serialize(((ObjectResult)refused).Value).Should().Contain("salary_row_conflict").And.Contain("Row 3");
        await using var verify = fx.CreateDb();
        (await verify.EmployeeSalaryStructures.CountAsync(s => s.EmployeeId == w.E["B5"] && s.EffectiveDate == new DateOnly(2027, 3, 1))).Should().Be(0, "the import is atomic: B5's row was not kept");
        (await verify.EmployeeSalaryStructures.CountAsync(s => s.EmployeeId == w.E["B6"] && s.EffectiveDate == new DateOnly(2027, 3, 1) && s.IsActive)).Should().Be(1);
    }

    [Fact]
    public void TheSalaryRowConflictParser_ReadsPostgresDetail()
    {
        var ex = new PostgresException("conflicting key value violates exclusion constraint", "ERROR", "ERROR", PostgresErrorCodes.ExclusionViolation,
            detail: "Key (tenant_id, employee_id, effective_date)=(6f0c…, 12, 2027-03-01) conflicts with existing key (6f0c…, 12, 2027-03-01).",
            constraintName: MeritPlanningSql.SalaryRowExclusionName);
        PayrollController.SalaryRowConflict(new DbUpdateException("x", ex)).Should().Be((12, new DateOnly(2027, 3, 1)));
        PayrollController.SalaryRowConflict(new InvalidOperationException("other")).Should().BeNull();
    }

    private async Task<CompCycleDto> ApprovedCycleAsync(MeritWorld w)
    {
        var (cycle, a, b) = await SubmittedAsync(fx, w);
        await ApproveBothStepsAsync(fx, w, a.ApprovalRequestId!.Value);
        await ApproveBothStepsAsync(fx, w, b.ApprovalRequestId!.Value);
        await using var db = fx.CreateDb();
        var svc = Service(db);
        var view = await svc.GetCycleAsync(Hr(w), cycle.Id, default);
        view = await svc.StartReviewAsync(Hr(w), cycle.Id, new CompCycleTransitionRequest(view.Version), default);
        return await svc.ApproveCycleAsync(Finance(w), cycle.Id, new CompCycleTransitionRequest(view.Version), default);
    }
}
