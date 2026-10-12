using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Approvals;
using Zayra.Api.Application.Employees;
using Zayra.Api.Models;

using Zayra.Api.Infrastructure.Data;

namespace Zayra.Api.Infrastructure.Compensation;

// Worksheets and scenarios: the manager's half of MeritPlanningService.
public sealed partial class MeritPlanningService
{
    public const int MaxScenariosPerManager = 5;

    public async Task<CompMyWorksheetsDto> MyWorksheetsAsync(CompCaller caller, CancellationToken ct)
    {
        if (caller.EmployeeId is not int me) return new CompMyWorksheetsDto([], []);
        // The caller's OWN worksheets, read tenant-wide: a manager employed in one company may own the worksheet for a team
        // in another, and the company filter would otherwise hide it and leave them with an empty list. Pinned to the
        // tenant and to ManagerEmployeeId == the caller; nothing else is read past the filter.
        var sheets = await ScopedBypass.TenantWide(db.CompWorksheets, caller.TenantId, OwnWorksheetsBypass).AsNoTracking()
            .Where(w => w.ManagerEmployeeId == me).OrderByDescending(w => w.CreatedAtUtc).ToListAsync(ct);
        var cycleIds = sheets.Select(s => s.CycleId).Distinct().ToList();
        var cycles = await ScopedBypass.TenantWide(db.CompCycles, caller.TenantId, OwnWorksheetsBypass).AsNoTracking()
            .Where(c => cycleIds.Contains(c.Id)).ToListAsync(ct);
        var sheetIds = sheets.Select(s => s.Id).ToList();
        // Which of them this login can open: the rest are listed with CompanyAccess = false and a way forward.
        var openable = (await db.CompWorksheets.AsNoTracking().Where(w => sheetIds.Contains(w.Id)).Select(w => w.Id).ToListAsync(ct)).ToHashSet();
        var totals = await ScopedBypass.TenantWide(db.CompWorksheetLines, caller.TenantId, OwnWorksheetsBypass).AsNoTracking().Where(l => sheetIds.Contains(l.WorksheetId))
            .GroupBy(l => l.WorksheetId).Select(g => new { g.Key, Count = g.Count(), Total = g.Sum(l => l.ProratedIncrease) }).ToListAsync(ct);
        var name = await db.Employees.AsNoTracking().Where(e => e.Id == me).Select(e => e.FullName).FirstOrDefaultAsync(ct) ?? "";
        return new CompMyWorksheetsDto(await SummariesAsync(cycles, ct, tenantWideFor: caller.TenantId), sheets.Select(s =>
        {
            var t = totals.FirstOrDefault(x => x.Key == s.Id);
            return new CompWorksheetSummaryDto(s.Id, s.ManagerEmployeeId, name, s.ScenarioName, s.IsPrimary, s.Status, s.Revision, t?.Count ?? 0, t?.Total ?? 0m,
                false, s.ApprovalRequestId, s.DecisionComments, s.Version, s.CycleId, CompanyAccess: openable.Contains(s.Id));
        }).ToList());
    }

    public async Task<CompWorksheetDto> GetWorksheetAsync(CompCaller caller, Guid worksheetId, CancellationToken ct)
    {
        var sheet = await db.CompWorksheets.AsNoTracking().FirstOrDefaultAsync(w => w.TenantId == caller.TenantId && w.Id == worksheetId, ct);
        if (sheet is null) await ThrowIfOwnWorksheetOutsideCompanyAccessAsync(caller, worksheetId, ct);
        if (sheet is null || !CanView(caller, sheet)) throw new CompNotFoundException("Worksheet not found.");
        var cycle = await FindCycleAsync(caller, sheet.CycleId, track: false, ct);
        return await ProjectWorksheetAsync(caller, cycle, sheet, ct);
    }

    public async Task<CompWorksheetDto> CreateScenarioAsync(CompCaller caller, Guid cycleId, CreateCompScenarioRequest request, CancellationToken ct)
    {
        var name = (request.ScenarioName ?? string.Empty).Trim();
        if (name.Length is 0 or > 80) throw new CompRuleException("invalid_scenario", "Give the scenario a name of 1 to 80 characters.");
        var managerId = request.ManagerEmployeeId ?? caller.EmployeeId ?? throw new CompForbiddenException("forbidden", "Your login is not linked to an employee, so you have no worksheet.");
        var probe = new CompWorksheet { ManagerEmployeeId = managerId };
        if (!CanEdit(caller, probe)) throw new CompForbiddenException("forbidden", "You can only add scenarios to your own worksheet.");
        CompWorksheet created = null!;
        var cycle = await InTransactionAsync(async () =>
        {
            var c = await LockCycleAsync(caller, cycleId, ct);
            if (c.Status != CompCycleStatuses.Planning) throw new CompRuleException("cycle_not_planning", "Scenarios can be added while the cycle is in planning.");
            var mine = await db.CompWorksheets.AsNoTracking().Where(w => w.CycleId == c.Id && w.ManagerEmployeeId == managerId && w.Status != CompWorksheetStatuses.Cancelled).ToListAsync(ct);
            if (mine.Count == 0) throw new CompRuleException("no_team", "This manager has no worksheet in the cycle (no eligible direct reports).");
            if (mine.Count >= MaxScenariosPerManager) throw new CompRuleException("too_many_scenarios", $"A manager can keep up to {MaxScenariosPerManager} scenarios.");
            if (mine.Any(w => string.Equals(w.ScenarioName, name, StringComparison.OrdinalIgnoreCase))) throw new CompRuleException("duplicate_scenario", $"A scenario called '{name}' already exists.");
            var source = request.CopyFromWorksheetId is Guid from ? mine.FirstOrDefault(w => w.Id == from) ?? throw new CompNotFoundException("The scenario to copy was not found.")
                : mine.First(w => w.IsPrimary);
            var sourceLines = await db.CompWorksheetLines.AsNoTracking().Where(l => l.WorksheetId == source.Id).ToListAsync(ct);
            var ctx = await _basis.LoadAsync(c, ct);
            created = new CompWorksheet { TenantId = c.TenantId, CompanyId = c.CompanyId, CycleId = c.Id, ManagerEmployeeId = managerId, ScenarioName = name, IsPrimary = false, CreatedByUserId = caller.UserId };
            created.NoteEditor(caller.UserId);
            db.CompWorksheets.Add(created);
            foreach (var b in TeamOf(ctx, managerId))
            {
                var prior = sourceLines.FirstOrDefault(l => l.EmployeeId == b.Employee.Id);
                var line = NewLine(c, created);
                var (amt, pct) = MeritLineCalculator.StoredProposal(prior);
                MeritLineCalculator.Compute(line, b, ctx, amt, pct, prior?.Justification);
                StampAssignment(line, ctx, managerId);
                db.CompWorksheetLines.Add(line);
            }
            await SaveAsync(ct);
            return c;
        }, ct);
        await AuditAsync(caller, "compensation.worksheet.scenario_created", nameof(CompWorksheet), created.Id, new { cycleId, managerId, name, copiedFrom = request.CopyFromWorksheetId }, ct);
        return await ProjectWorksheetAsync(caller, cycle, created, ct);
    }

    /// <summary>Makes a scenario the one that is submitted. Refused while the current primary is submitted or approved.</summary>
    public async Task<CompWorksheetDto> SetPrimaryAsync(CompCaller caller, Guid worksheetId, SubmitCompWorksheetRequest request, CancellationToken ct)
    {
        CompWorksheet target = null!;
        var cycle = await InTransactionAsync(async () =>
        {
            target = await db.CompWorksheets.FirstOrDefaultAsync(w => w.TenantId == caller.TenantId && w.Id == worksheetId, ct) ?? throw new CompNotFoundException("Worksheet not found.");
            if (!CanEdit(caller, target)) throw new CompNotFoundException("Worksheet not found.");
            var c = await LockCycleAsync(caller, target.CycleId, ct);
            if (c.Status != CompCycleStatuses.Planning) throw new CompRuleException("cycle_not_planning", "The submitted scenario can be chosen while the cycle is in planning.");
            if (target.Version != request.Version) throw new CompConflictException("This scenario changed. Reload it and try again.");
            if (target.IsPrimary) return c;
            if (target.Status != CompWorksheetStatuses.Draft) throw new CompRuleException("scenario_not_draft", "Only a draft scenario can become the one you submit.");
            var current = await db.CompWorksheets.FirstOrDefaultAsync(w => w.CycleId == c.Id && w.ManagerEmployeeId == target.ManagerEmployeeId && w.IsPrimary, ct);
            if (current is not null)
            {
                if (!CompWorksheetStatuses.IsEditable(current.Status))
                    throw new CompRuleException("primary_locked", $"'{current.ScenarioName}' is {current.Status.ToLowerInvariant()}; it stays the submitted scenario.");
                current.IsPrimary = false;
                current.Status = CompWorksheetStatuses.Draft;
                current.Version++;
                await SaveAsync(ct); // two statements: the one-primary unique index is checked per row
            }
            target.IsPrimary = true;
            target.Version++;
            target.NoteEditor(caller.UserId);
            await SaveAsync(ct);
            return c;
        }, ct);
        await AuditAsync(caller, "compensation.worksheet.primary_changed", nameof(CompWorksheet), target.Id, new { target.ScenarioName }, ct);
        return await ProjectWorksheetAsync(caller, cycle, target, ct);
    }

    public async Task<CompWorksheetDto> SaveWorksheetAsync(CompCaller caller, Guid worksheetId, SaveCompWorksheetRequest request, CancellationToken ct)
    {
        // A percent is stored to 4 decimals and tested as entered; more precision would be silently rounded, so it is refused.
        var tooPrecise = (request.Lines ?? []).FirstOrDefault(l => l.ProposedIncrease is null && l.ProposedPercent is decimal p && decimal.Round(p, 4) != p);
        if (tooPrecise is not null)
            throw new CompBadRequestException("percent_precision", FormattableString.Invariant($"Employee {tooPrecise.EmployeeId}: {tooPrecise.ProposedPercent} % has more than 4 decimal places. Enter at most 4."));
        CompWorksheet sheet = null!;
        decimal total = 0;
        var cycle = await InTransactionAsync(async () =>
        {
            (sheet, var c, var ctx, var lines) = await LoadEditableAsync(caller, worksheetId, request.Version, ct);
            var inputs = (request.Lines ?? []).ToDictionary(l => l.EmployeeId);
            var team = TeamOf(ctx, sheet.ManagerEmployeeId).ToList();
            var stranger = inputs.Keys.Except(team.Select(b => b.Employee.Id)).FirstOrDefault();
            if (stranger != 0) throw new CompRuleException("not_on_worksheet", $"Employee {stranger} is not an eligible direct report on this worksheet.");
            Sync(c, sheet, ctx, lines, team, inputs);
            sheet.OverspendJustification = Clean(request.OverspendJustification, 2000);
            sheet.Version++;
            sheet.UpdatedAtUtc = DateTime.UtcNow;
            sheet.UpdatedByUserId = caller.UserId;
            sheet.NoteEditor(caller.UserId);
            await SaveAsync(ct);
            total = lines.Where(l => db.Entry(l).State != EntityState.Deleted).Sum(l => l.ProratedIncrease);
            return c;
        }, ct);
        await AuditAsync(caller, "compensation.worksheet.saved", nameof(CompWorksheet), sheet.Id, new { sheet.Revision, sheet.Version, proposedTotal = total }, ct);
        return await ProjectWorksheetAsync(caller, cycle, sheet, ct);
    }

    /// <summary>
    /// Submits the primary scenario into the shared approval engine. Everything is recomputed from pay of record first,
    /// and every blocking rule is checked against what will actually be applied: the governed salary validation, the
    /// guideline justification, the overspend justification and one award per employee per cycle.
    /// </summary>
    public async Task<CompWorksheetDto> SubmitWorksheetAsync(CompCaller caller, Guid worksheetId, SubmitCompWorksheetRequest request, CancellationToken ct)
    {
        CompWorksheet sheet = null!;
        string snapshotHash = "";
        bool overspend = false;
        decimal total = 0;
        var cycle = await InTransactionAsync(async () =>
        {
            (sheet, var c, var ctx, var lines) = await LoadEditableAsync(caller, worksheetId, request.Version, ct);
            if (!sheet.IsPrimary) throw new CompRuleException("not_primary", "Only the scenario marked for submission can be submitted. Choose it first.");
            var ownerActive = await Zayra.Api.Infrastructure.Data.ScopedBypass.NullableTenantWide(db.Employees, c.TenantId,
                    "Merit submission: the worksheet owner may be employed by another company of the tenant. Tenant re-applied; existence only.")
                .AsNoTracking().AnyAsync(e => e.Id == sheet.ManagerEmployeeId && !e.IsDeleted && !MeritBasisLoader.SeparatedStatuses.Contains(e.Status), ct);
            if (!ownerActive)
                throw new CompRuleException("owner_inactive", "The worksheet owner is no longer an active employee. HR can assign this team to another worksheet owner and close this worksheet.");
            var team = TeamOf(ctx, sheet.ManagerEmployeeId).ToList();
            var blockers = Sync(c, sheet, ctx, lines, team, new Dictionary<int, CompLineInput>()).Select(m => new CompBlockerDto("line_blocked", m)).ToList();
            var live = lines.Where(l => db.Entry(l).State != EntityState.Deleted).ToList();
            var awarded = live.Where(l => l.ProratedIncreasePay > 0).ToList();
            foreach (var line in live.Where(l => l.OutOfGuideline && string.IsNullOrWhiteSpace(l.Justification)))
                blockers.Add(new("justification_required", FormattableString.Invariant($"{ctx.Employees[line.EmployeeId].Employee.FullName}: {line.ProposedPercent:0.##} % is outside the guideline ({line.GuidelineMinPercent?.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) ?? "none"}–{line.GuidelineMaxPercent?.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) ?? "none"} %). Add a justification.")));
            foreach (var line in awarded)
            {
                var b = ctx.Employees[line.EmployeeId];
                if (b.SalaryRow is null) continue; // already a blocker
                try
                {
                    await EmployeeSalaryBreakdownChanges.ValidateAsync(db, b.Employee,
                        MeritLineCalculator.Package(b.SalaryRow, b.SalaryStructureCode, line.NewBasic, c.EffectiveDate, line.PayCurrency), ct);
                }
                catch (InvalidOperationException ex) { blockers.Add(new("salary_rule", $"{b.Employee.FullName}: {ex.Message}")); }
            }
            var otherSheets = await db.CompWorksheets.AsNoTracking().Where(w => w.CycleId == c.Id && w.IsPrimary && w.Id != sheet.Id && w.Status != CompWorksheetStatuses.Cancelled).ToListAsync(ct);
            var otherIds = otherSheets.Select(o => o.Id).ToList();
            var otherLines = await db.CompWorksheetLines.AsNoTracking().Where(l => otherIds.Contains(l.WorksheetId)).ToListAsync(ct);
            var lockedIds = otherSheets.Where(o => o.Status is CompWorksheetStatuses.Submitted or CompWorksheetStatuses.Approved).Select(o => o.Id).ToHashSet();
            foreach (var dup in awarded.Where(l => otherLines.Any(o => lockedIds.Contains(o.WorksheetId) && o.EmployeeId == l.EmployeeId && o.ProratedIncreasePay > 0)))
                blockers.Add(new("duplicate_award", $"{ctx.Employees[dup.EmployeeId].Employee.FullName} already has an award on another manager's submitted worksheet."));
            if (caller.EmployeeId is int me && live.Any(l => l.EmployeeId == me))
                throw new CompForbiddenException("self_award", "Segregation of duties: this worksheet includes your own pay, so you cannot submit it.");

            var budgetSheets = otherSheets.Append(sheet).ToDictionary(w => w.Id);
            var budgets = BudgetSummary(ctx, CurrentLines(otherLines.Concat(live), budgetSheets, ctx));
            var touched = awarded.Select(l => l.DepartmentId ?? Guid.Empty).ToHashSet();
            var over = budgets.Where(b => b.Overspent && touched.Contains(b.DepartmentId)).ToList();
            overspend = over.Count > 0;
            if (overspend && string.IsNullOrWhiteSpace(sheet.OverspendJustification))
                blockers.Add(new("overspend_justification_required",
                    $"This worksheet takes {string.Join(", ", over.Select(b => FormattableString.Invariant($"{b.DepartmentName} to {b.Proposed:#,##0.00} of {b.Pool:#,##0.00} {c.Currency}")))}. Explain the overspend; it goes to the approvers flagged."));
            if (blockers.Count > 0) throw new CompRuleException("submit_blocked", $"The worksheet cannot be submitted yet ({blockers.Count} issue(s)).", blockers);

            total = live.Sum(l => l.ProratedIncrease);
            snapshotHash = SnapshotHash(live);
            var manager = await db.Employees.AsNoTracking().Where(e => e.Id == sheet.ManagerEmployeeId).Select(e => new { e.FullName, e.CompanyId }).FirstOrDefaultAsync(ct);
            ApprovalRequestDto approval;
            try
            {
                approval = await approvals.CreateRequestAsync(c.TenantId, new CreateApprovalRequest(null, CompensationApprovalSync.EntityName, sheet.Id.ToString(),
                    Truncate(FormattableString.Invariant($"Merit worksheet - {manager?.FullName} - {c.Name} (revision {sheet.Revision}): {awarded.Count} award(s), {total:#,##0.00} {c.Currency}")
                             + (overspend ? " - OVER BUDGET, justification attached" : ""), 240),
                    sheet.ManagerEmployeeId, c.CompanyId, overspend ? "High" : "Normal"), caller.Context, ct);
            }
            catch (ApprovalRouteNotConfiguredException)
            {
                throw new CompRuleException("no_approval_route", "No approval workflow is configured for merit worksheets. Add one for 'CompensationWorksheet' under Approvals.");
            }
            sheet.ApprovalRequestId = approval.Id;
            sheet.Status = CompWorksheetStatuses.Submitted;
            sheet.SubmittedAtUtc = DateTime.UtcNow;
            sheet.SubmittedByUserId = caller.UserId;
            sheet.NoteEditor(caller.UserId);
            sheet.DecisionComments = null;
            sheet.DecidedAtUtc = null;
            sheet.Version++;
            await SaveAsync(ct);
            return c;
        }, ct);
        await AuditAsync(caller, "compensation.worksheet.submitted", nameof(CompWorksheet), sheet.Id,
            new { cycleId = cycle.Id, sheet.Revision, approvalRequestId = sheet.ApprovalRequestId, proposedTotal = total, overspend, linesSha256 = snapshotHash }, ct);
        var approverUser = await db.ApprovalRequests.AsNoTracking().Where(a => a.Id == sheet.ApprovalRequestId).Select(a => a.CurrentApproverUserId).FirstOrDefaultAsync(ct);
        if (approverUser is Guid u)
            await NotifyUserAsync(caller.TenantId, u, CompEventCodes.WorksheetSubmitted, nameof(CompWorksheet), sheet.Id,
                $"Merit worksheet to approve: {cycle.Name}", FormattableString.Invariant($"A merit worksheet ({total:#,##0.00} {cycle.Currency}) is waiting for your decision in Approvals."), ct);
        return await ProjectWorksheetAsync(caller, cycle, sheet, ct);
    }

    // ───────────────────────────── worksheet internals ─────────────────────────────

    private bool CanView(CompCaller caller, CompWorksheet sheet) =>
        caller.CanReadAll || (caller.Can(CompPermissions.Plan) && caller.EmployeeId == sheet.ManagerEmployeeId);

    private bool CanEdit(CompCaller caller, CompWorksheet sheet) =>
        caller.CanManage || (caller.Can(CompPermissions.Plan) && caller.EmployeeId == sheet.ManagerEmployeeId);

    private const string OwnWorksheetsBypass =
        "merit planning: a manager's own worksheets (ManagerEmployeeId = the caller), whichever company the team belongs to";

    /// <summary>
    /// The caller's own worksheet exists but sits in a company their login cannot open. Editing it would read and write
    /// that company's pay records, which the company boundary forbids, so it is refused with the way forward rather than
    /// answered as "not found". Someone else's worksheet stays a 404.
    /// </summary>
    private async Task ThrowIfOwnWorksheetOutsideCompanyAccessAsync(CompCaller caller, Guid worksheetId, CancellationToken ct)
    {
        if (caller.EmployeeId is not int me) return;
        var own = await ScopedBypass.TenantWide(db.CompWorksheets, caller.TenantId, OwnWorksheetsBypass).AsNoTracking()
            .Where(w => w.Id == worksheetId && w.ManagerEmployeeId == me).Select(w => new { w.CompanyId }).FirstOrDefaultAsync(ct);
        if (own is null) return;
        var company = await ScopedBypass.TenantWide(db.Companies, caller.TenantId, OwnWorksheetsBypass).AsNoTracking()
            .Where(c => c.Id == own.CompanyId).Select(c => c.LegalNameEn).FirstOrDefaultAsync(ct) ?? "another company";
        throw new CompForbiddenException("company_access_required",
            $"This worksheet is for your team in {company}, which your login cannot open. Ask HR to give you access to {company} (User Management), or to complete the worksheet for you.");
    }

    private async Task<(CompWorksheet Sheet, CompCycle Cycle, MeritBasisLoader.CycleContext Ctx, List<CompWorksheetLine> Lines)> LoadEditableAsync(
        CompCaller caller, Guid worksheetId, int version, CancellationToken ct)
    {
        var sheet = await db.CompWorksheets.FirstOrDefaultAsync(w => w.TenantId == caller.TenantId && w.Id == worksheetId, ct);
        if (sheet is null) await ThrowIfOwnWorksheetOutsideCompanyAccessAsync(caller, worksheetId, ct);
        if (sheet is null || !CanView(caller, sheet)) throw new CompNotFoundException("Worksheet not found.");
        if (!CanEdit(caller, sheet)) throw new CompForbiddenException("read_only", "You can view this worksheet but not change it.");
        var cycle = await db.CompCycles.AsNoTracking().FirstAsync(c => c.Id == sheet.CycleId, ct);
        if (cycle.Status != CompCycleStatuses.Planning) throw new CompRuleException("cycle_not_planning", $"Worksheets can be changed while the cycle is in planning; it is {cycle.Status}.");
        if (!CompWorksheetStatuses.IsEditable(sheet.Status)) throw new CompRuleException("worksheet_locked", $"This worksheet is {sheet.Status.ToLowerInvariant()} and locked.");
        if (sheet.Version != version) throw new CompConflictException("This worksheet was changed elsewhere. Reload it; your edits were not saved.");
        var ctx = await _basis.LoadAsync(cycle, ct);
        if (caller.EmployeeId is int me && me != sheet.ManagerEmployeeId && TeamOf(ctx, sheet.ManagerEmployeeId).Any(b => b.Employee.Id == me))
            throw new CompForbiddenException("self_award", "Segregation of duties: this worksheet includes your own pay, so you cannot change it.");
        var lines = await db.CompWorksheetLines.Where(l => l.WorksheetId == sheet.Id).ToListAsync(ct);
        return (sheet, cycle, ctx, lines);
    }

    /// <summary>Re-aligns the line set with the manager's current eligible direct reports and recomputes every line.
    /// Returns the blocking messages (per line).</summary>
    private List<string> Sync(CompCycle cycle, CompWorksheet sheet, MeritBasisLoader.CycleContext ctx, List<CompWorksheetLine> lines,
        IReadOnlyList<EmployeeBasis> team, IReadOnlyDictionary<int, CompLineInput> inputs)
    {
        var blocking = new List<string>();
        var teamIds = team.Select(b => b.Employee.Id).ToHashSet();
        foreach (var gone in lines.Where(l => !teamIds.Contains(l.EmployeeId)).ToList()) db.CompWorksheetLines.Remove(gone);
        foreach (var b in team)
        {
            var line = lines.FirstOrDefault(l => l.EmployeeId == b.Employee.Id);
            if (line is null) { line = NewLine(cycle, sheet); db.CompWorksheetLines.Add(line); lines.Add(line); }
            var (amount, percent) = MeritLineCalculator.StoredProposal(line); var justification = line.Justification;
            if (inputs.TryGetValue(b.Employee.Id, out var input))
            {
                amount = input.ProposedIncrease; percent = input.ProposedIncrease is null ? input.ProposedPercent : null;
                justification = Clean(input.Justification, 2000);
            }
            blocking.AddRange(MeritLineCalculator.Compute(line, b, ctx, amount, percent, justification));
            StampAssignment(line, ctx, sheet.ManagerEmployeeId);
        }
        return blocking;
    }

    private async Task<CompWorksheetDto> ProjectWorksheetAsync(CompCaller caller, CompCycle cycle, CompWorksheet sheet, CancellationToken ct)
    {
        sheet = await db.CompWorksheets.AsNoTracking().FirstAsync(w => w.Id == sheet.Id, ct);
        var ownChanges = await db.CompWorksheetLines.AsNoTracking().Where(l => l.CycleId == cycle.Id && l.ChangeRequestId != null).Select(l => l.ChangeRequestId!.Value).ToListAsync(ct);
        var ctx = await _basis.LoadAsync(cycle, ct, ownChanges);
        var stored = await db.CompWorksheetLines.AsNoTracking().Where(l => l.WorksheetId == sheet.Id).ToListAsync(ct);
        var editable = cycle.Status == CompCycleStatuses.Planning && CompWorksheetStatuses.IsEditable(sheet.Status);
        var canEdit = editable && CanEdit(caller, sheet);

        // Editable: shown as it would be saved now (live pay of record). Locked: exactly what was submitted/approved.
        var shown = new List<(CompWorksheetLine Line, IReadOnlyList<string> Blocking)>();
        if (editable)
            foreach (var b in TeamOf(ctx, sheet.ManagerEmployeeId))
            {
                var prior = stored.FirstOrDefault(l => l.EmployeeId == b.Employee.Id);
                var line = prior is null ? NewLine(cycle, sheet) : Clone(prior);
                var (amt, pct) = MeritLineCalculator.StoredProposal(prior);
                shown.Add((line, MeritLineCalculator.Compute(line, b, ctx, amt, pct, prior?.Justification)));
            }
        else
            shown.AddRange(stored.Select(l => (l, (IReadOnlyList<string>)Array.Empty<string>())));

        var changeIds = shown.Where(s => s.Line.ChangeRequestId != null).Select(s => s.Line.ChangeRequestId!.Value).ToList();
        var changeStatus = await db.EmployeeChangeRequests.AsNoTracking().Where(x => changeIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, x => x.Status, ct);
        var missingIds = shown.Select(s => s.Line.EmployeeId).Where(id => !ctx.Employees.ContainsKey(id)).ToList();
        var names = await NamesAsync(cycle.TenantId, missingIds.Append(sheet.ManagerEmployeeId), ct);

        var lineDtos = shown.Select(s =>
        {
            var l = s.Line;
            ctx.Employees.TryGetValue(l.EmployeeId, out var b);
            var g = b?.Grade;
            return new CompLineDto(l.Id, l.EmployeeId, b?.Employee.EmployeeCode ?? "", b?.Employee.FullName ?? names.GetValueOrDefault(l.EmployeeId) ?? $"#{l.EmployeeId}",
                b?.DepartmentName ?? "", b?.DesignationName ?? "", g?.Name ?? "", l.PayCurrency, l.FxRate, l.CurrentBasic, l.CurrentGross, l.CurrentBasicCycle, l.CurrentGrossCycle,
                g?.MinSalary, g?.MidSalary, g?.MaxSalary, g?.Currency, l.CompaRatio, l.RangePosition, l.Rating, l.EligibleDays, l.ProrationFactor,
                l.GuidelineMinPercent, l.GuidelineTargetPercent, l.GuidelineMaxPercent, l.ProposedIncrease, l.ProposedPercent, l.ProratedIncrease, l.ProratedIncreasePay,
                l.NewBasic, l.NewGross, l.NewCompaRatio, l.OutOfGuideline, l.Justification,
                l.Flags.Split(',', StringSplitOptions.RemoveEmptyEntries), s.Blocking,
                l.ProposedAsPercent, l.AssignedByUserId != null,
                Market(caller, b?.Benchmark),
                l.ChangeRequestId, l.ChangeRequestId is Guid cid ? changeStatus.GetValueOrDefault(cid) : null, l.ApplyBlockedReason);
        }).OrderBy(l => l.EmployeeName).ToList();

        var excluded = ctx.Employees.Values.Where(b => !b.Eligible && b.Employee.ManagerEmployeeId == sheet.ManagerEmployeeId)
            .Select(b => new CompExcludedDto(b.Employee.Id, b.Employee.EmployeeCode, b.Employee.FullName, b.IneligibleReason ?? "Not eligible.")).ToList();

        // Budget impact: every other live primary worksheet as saved, plus this one as shown.
        var otherSheetRows = await db.CompWorksheets.AsNoTracking().Where(w => w.CycleId == cycle.Id && w.IsPrimary && w.Id != sheet.Id && w.Status != CompWorksheetStatuses.Cancelled).ToListAsync(ct);
        var others = otherSheetRows.Select(w => w.Id).ToList();
        var otherLines = await db.CompWorksheetLines.AsNoTracking().Where(l => others.Contains(l.WorksheetId)).ToListAsync(ct);
        var mineForBudget = sheet.IsPrimary && sheet.Status != CompWorksheetStatuses.Cancelled ? shown.Select(s => s.Line) : [];
        var touched = shown.Select(s => s.Line.DepartmentId ?? Guid.Empty).ToHashSet();
        var impactSheets = otherSheetRows.Append(sheet).ToDictionary(w => w.Id);
        var impact = BudgetSummary(ctx, CurrentLines(otherLines.Concat(mineForBudget), impactSheets, ctx)).Where(b => touched.Contains(b.DepartmentId)).ToList();
        var overspend = impact.Any(b => b.Overspent) && shown.Any(s => s.Line.ProratedIncrease > 0);

        var blockers = new List<CompBlockerDto>();
        if (editable)
        {
            blockers.AddRange(shown.SelectMany(s => s.Blocking).Select(m => new CompBlockerDto("line_blocked", m)));
            foreach (var s in shown.Where(s => s.Line.OutOfGuideline && string.IsNullOrWhiteSpace(s.Line.Justification)))
                blockers.Add(new("justification_required", $"{lineDtos.First(d => d.EmployeeId == s.Line.EmployeeId).EmployeeName}: outside the guideline — add a justification."));
            if (overspend && string.IsNullOrWhiteSpace(sheet.OverspendJustification)) blockers.Add(new("overspend_justification_required", "This worksheet is over budget — explain the overspend before submitting."));
            if (!sheet.IsPrimary) blockers.Add(new("not_primary", "This scenario is not the one marked for submission."));
        }
        var scenarios = await db.CompWorksheets.AsNoTracking().Where(w => w.CycleId == cycle.Id && w.ManagerEmployeeId == sheet.ManagerEmployeeId && w.Status != CompWorksheetStatuses.Cancelled)
            .OrderByDescending(w => w.IsPrimary).ThenBy(w => w.ScenarioName).Select(w => new { w.Id, w.ScenarioName, w.IsPrimary, w.Status }).ToListAsync(ct);
        var scenarioIds = scenarios.Select(x => x.Id).ToList();
        var scenarioTotals = await db.CompWorksheetLines.AsNoTracking().Where(l => scenarioIds.Contains(l.WorksheetId)).GroupBy(l => l.WorksheetId)
            .Select(g => new { g.Key, Total = g.Sum(l => l.ProratedIncrease) }).ToDictionaryAsync(x => x.Key, x => x.Total, ct);

        var managerName = ctx.Employees.TryGetValue(sheet.ManagerEmployeeId, out var mb) ? mb.Employee.FullName : names.GetValueOrDefault(sheet.ManagerEmployeeId) ?? "";
        return new CompWorksheetDto(sheet.Id, cycle.Id, cycle.Name, cycle.Status, cycle.Currency, cycle.EffectiveDate, sheet.ManagerEmployeeId, managerName,
            sheet.ScenarioName, sheet.IsPrimary, sheet.Status, sheet.Revision, sheet.Version, sheet.OverspendJustification, sheet.ApprovalRequestId,
            sheet.DecisionComments, sheet.SubmittedAtUtc, lineDtos, excluded, impact, shown.Sum(s => s.Line.ProratedIncrease), overspend, canEdit,
            canEdit && sheet.IsPrimary && blockers.Count == 0, blockers,
            scenarios.Select(x => new CompScenarioRefDto(x.Id, x.ScenarioName, x.IsPrimary, x.Status, scenarioTotals.GetValueOrDefault(x.Id))).ToList());
    }

    /// <summary>A planner sees where pay sits against the market (position, state, label), never the licensed figure.</summary>
    private static CompBenchmarkPositionDto Market(CompCaller caller, CompBenchmarkPositionDto? b)
    {
        b ??= new CompBenchmarkPositionDto("missing", null, null, false, null, null, null, null, null);
        return PayBenchmarkService.CanRead(caller) ? b : b with { P50Cycle = null, SetId = null };
    }

    private static CompWorksheetLine Clone(CompWorksheetLine l) => new()
    {
        Id = l.Id, TenantId = l.TenantId, CompanyId = l.CompanyId, CycleId = l.CycleId, WorksheetId = l.WorksheetId, EmployeeId = l.EmployeeId,
        ProposedIncrease = l.ProposedIncrease, ProposedPercent = l.ProposedPercent, ProposedAsPercent = l.ProposedAsPercent, Justification = l.Justification,
        ChangeRequestId = l.ChangeRequestId, ApplyBlockedReason = l.ApplyBlockedReason, AssignedByUserId = l.AssignedByUserId,
    };

    private static string SnapshotHash(IEnumerable<CompWorksheetLine> lines)
    {
        var canonical = JsonSerializer.Serialize(lines.OrderBy(l => l.EmployeeId).Select(l => new object[]
            { l.EmployeeId, l.CurrentBasic, l.CurrentGross, l.ProposedIncrease, l.ProratedIncrease, l.ProratedIncreasePay, l.NewBasic, l.FxRate ?? 0m, l.Justification ?? "" }));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }

    private static string? Clean(string? text, int max)
    {
        var t = (text ?? string.Empty).Trim();
        if (t.Length > max) throw new CompRuleException("too_long", $"Text is limited to {max} characters.");
        return t.Length == 0 ? null : t;
    }
}
