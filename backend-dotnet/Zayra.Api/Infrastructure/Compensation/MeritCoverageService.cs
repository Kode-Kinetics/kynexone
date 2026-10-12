using Microsoft.EntityFrameworkCore;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Compensation;

// Coverage: who plans whom. HR (compensation.manage) can open a worksheet for any active manager, assign an employee to a
// worksheet owner explicitly (no manager on record, a removed manager, or a reporting line changed after planning opened),
// and close a worksheet whose team has moved. Every action is audited.
public sealed partial class MeritPlanningService
{
    public async Task<CompWorksheetDto> CreateWorksheetAsync(CompCaller caller, Guid cycleId, int managerEmployeeId, CancellationToken ct)
    {
        RequireManage(caller);
        CompWorksheet created = null!;
        var cycle = await InTransactionAsync(async () =>
        {
            var c = await LockCycleAsync(caller, cycleId, ct);
            if (c.Status != CompCycleStatuses.Planning) throw new CompRuleException("cycle_not_planning", "Worksheets can be opened while the cycle is in planning.");
            var owner = await Zayra.Api.Infrastructure.Data.ScopedBypass.NullableTenantWide(db.Employees, c.TenantId,
                    "Merit coverage: a worksheet owner may be employed by another company of the tenant. Tenant re-applied.")
                .AsNoTracking().Where(e => e.Id == managerEmployeeId && !e.IsDeleted).Select(e => new { e.Id, e.Status, e.FullName }).FirstOrDefaultAsync(ct)
                ?? throw new CompNotFoundException("That employee was not found.");
            if (MeritBasisLoader.SeparatedStatuses.Contains(owner.Status)) throw new CompRuleException("owner_inactive", $"{owner.FullName} is {owner.Status} and cannot own a worksheet.");
            var mine = await db.CompWorksheets.AsNoTracking().Where(w => w.CycleId == c.Id && w.ManagerEmployeeId == managerEmployeeId).ToListAsync(ct);
            if (mine.Any(w => w.IsPrimary && w.Status != CompWorksheetStatuses.Cancelled))
                throw new CompRuleException("worksheet_exists", $"{owner.FullName} already has a worksheet in this cycle.");
            var name = "Main";
            for (var n = 2; mine.Any(w => string.Equals(w.ScenarioName, name, StringComparison.OrdinalIgnoreCase)); n++) name = $"Main ({n})";
            var ctx = await _basis.LoadAsync(c, ct);
            created = new CompWorksheet { TenantId = c.TenantId, CompanyId = c.CompanyId, CycleId = c.Id, ManagerEmployeeId = managerEmployeeId, ScenarioName = name, IsPrimary = true, CreatedByUserId = caller.UserId };
            db.CompWorksheets.Add(created);
            foreach (var b in TeamOf(ctx, managerEmployeeId))
            {
                var line = NewLine(c, created);
                MeritLineCalculator.Compute(line, b, ctx, 0m, null, null);
                StampAssignment(line, ctx, managerEmployeeId);
                db.CompWorksheetLines.Add(line);
            }
            await SaveAsync(ct);
            return c;
        }, ct);
        await AuditAsync(caller, "compensation.worksheet.opened_by_hr", nameof(CompWorksheet), created.Id, new { cycleId, managerEmployeeId }, ct);
        return await ProjectWorksheetAsync(caller, cycle, created, ct);
    }

    /// <summary>Puts one eligible employee on a worksheet owner's (primary, editable) worksheet, overriding the reporting
    /// line for this cycle. The employee leaves any other editable worksheet; a submitted or approved one must be returned first.</summary>
    public async Task<CompWorksheetDto> AssignAsync(CompCaller caller, Guid worksheetId, int employeeId, CancellationToken ct)
    {
        RequireManage(caller);
        CompWorksheet target = null!;
        int? from = null;
        var cycle = await InTransactionAsync(async () =>
        {
            target = await db.CompWorksheets.FirstOrDefaultAsync(w => w.TenantId == caller.TenantId && w.Id == worksheetId, ct) ?? throw new CompNotFoundException("Worksheet not found.");
            var c = await LockCycleAsync(caller, target.CycleId, ct);
            if (c.Status != CompCycleStatuses.Planning) throw new CompRuleException("cycle_not_planning", "Employees can be assigned while the cycle is in planning.");
            if (!target.IsPrimary || !CompWorksheetStatuses.IsEditable(target.Status))
                throw new CompRuleException("worksheet_locked", "Assign to the owner's submitted scenario while it is a draft or returned.");
            if (employeeId == target.ManagerEmployeeId) throw new CompForbiddenException("self_award", "Segregation of duties: a worksheet owner cannot plan their own pay.");
            var ctx = await _basis.LoadAsync(c, ct);
            if (!ctx.Employees.TryGetValue(employeeId, out var b) || !b.Eligible)
                throw new CompRuleException("not_eligible", "Only an eligible employee of this cycle can be assigned.");
            // Nobody plans the pay of someone above them in their own reporting chain.
            var chain = await ManagerChainAsync(c.TenantId, target.ManagerEmployeeId, ct);
            if (chain.Contains(employeeId)) throw new CompForbiddenException("chain", "Segregation of duties: this employee is in the worksheet owner's own management chain.");
            from = ctx.CoveringOwner(b);
            if (from == target.ManagerEmployeeId && ctx.Assignments.ContainsKey(employeeId)) return c; // already assigned here
            var current = from is int owner && owner != target.ManagerEmployeeId
                ? await db.CompWorksheets.Where(w => w.CycleId == c.Id && w.ManagerEmployeeId == owner && w.Status != CompWorksheetStatuses.Cancelled).ToListAsync(ct)
                : [];
            if (current.Any(w => w.IsPrimary && !CompWorksheetStatuses.IsEditable(w.Status)))
                throw new CompRuleException("source_locked", "The employee is on a worksheet that is submitted or approved. Return it first (Reopen planning), then assign.");
            var currentIds = current.Select(w => w.Id).ToList();
            db.CompWorksheetLines.RemoveRange(await db.CompWorksheetLines.Where(l => currentIds.Contains(l.WorksheetId) && l.EmployeeId == employeeId).ToListAsync(ct));
            var line = await db.CompWorksheetLines.FirstOrDefaultAsync(l => l.WorksheetId == target.Id && l.EmployeeId == employeeId, ct);
            if (line is null) { line = NewLine(c, target); db.CompWorksheetLines.Add(line); }
            MeritLineCalculator.Compute(line, b, ctx, 0m, null, null);
            line.AssignedByUserId = caller.UserId;
            target.Version++;
            target.UpdatedAtUtc = DateTime.UtcNow;
            target.UpdatedByUserId = caller.UserId;
            target.NoteEditor(caller.UserId);
            foreach (var w in current) { w.NoteEditor(caller.UserId); w.Version++; }
            await SaveAsync(ct);
            return c;
        }, ct);
        await AuditAsync(caller, "compensation.worksheet.employee_assigned", nameof(CompWorksheet), target.Id, new { employeeId, fromOwner = from, toOwner = target.ManagerEmployeeId }, ct);
        return await ProjectWorksheetAsync(caller, cycle, target, ct);
    }

    /// <summary>Closes an editable worksheet (its team moved elsewhere, or its owner left). Its people become uncovered.</summary>
    public async Task<CompCycleDto> CloseWorksheetAsync(CompCaller caller, Guid worksheetId, string? reason, CancellationToken ct)
    {
        RequireManage(caller);
        var why = RequireReason(reason);
        CompWorksheet sheet = null!;
        var cycle = await InTransactionAsync(async () =>
        {
            sheet = await db.CompWorksheets.FirstOrDefaultAsync(w => w.TenantId == caller.TenantId && w.Id == worksheetId, ct) ?? throw new CompNotFoundException("Worksheet not found.");
            var c = await LockCycleAsync(caller, sheet.CycleId, ct);
            if (c.Status != CompCycleStatuses.Planning) throw new CompRuleException("cycle_not_planning", "Worksheets can be closed while the cycle is in planning.");
            if (!CompWorksheetStatuses.IsEditable(sheet.Status)) throw new CompRuleException("worksheet_locked", "Only a draft or returned worksheet can be closed.");
            sheet.Status = CompWorksheetStatuses.Cancelled;
            // Cleared so HR can open a fresh primary worksheet for the same owner (one primary per owner and cycle).
            sheet.IsPrimary = false;
            sheet.DecisionComments = why;
            sheet.Version++;
            sheet.UpdatedAtUtc = DateTime.UtcNow;
            sheet.UpdatedByUserId = caller.UserId;
            await SaveAsync(ct);
            return c;
        }, ct);
        await AuditAsync(caller, "compensation.worksheet.closed", nameof(CompWorksheet), sheet.Id, new { reason = why }, ct);
        return await ProjectCycleAsync(cycle, ct);
    }

    private async Task<HashSet<int>> ManagerChainAsync(Guid tenantId, int employeeId, CancellationToken ct)
    {
        var managerOf = await Zayra.Api.Infrastructure.Data.ScopedBypass.NullableTenantWide(db.Employees, tenantId,
                "Merit coverage SoD: the management chain above a worksheet owner crosses companies. Tenant re-applied; ids only.")
            .AsNoTracking().Where(e => !e.IsDeleted).Select(e => new { e.Id, e.ManagerEmployeeId }).ToDictionaryAsync(e => e.Id, e => e.ManagerEmployeeId, ct);
        var chain = new HashSet<int>();
        for (int? cursor = managerOf.GetValueOrDefault(employeeId); cursor is int m && chain.Add(m); cursor = managerOf.GetValueOrDefault(m)) { }
        return chain;
    }
}
