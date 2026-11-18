using Microsoft.EntityFrameworkCore;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Jobs;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Payroll;

/// <summary>
/// The department's monthly overtime budget (<see cref="OvertimeBudget"/>), as approval uses it. A budget row is per department and
/// work-date month; approving overtime consumes its approved amount (the amount priced at approval) whatever the company's
/// <see cref="PayrollRuleKeys.OvertimeBudgetMode"/>, so <c>ConsumedAmount</c> is finally written. The mode decides only what
/// happens when an approval would take the department over: Off approves, Warn approves and says so, Block refuses.
/// A department with no budget row for the month has no budget: nothing is consumed or refused.
/// </summary>
public static class OvertimeBudgetLedger
{
    /// <summary>The budget for the employee's department in the work date's month (read only, for a refusal before anything changes).</summary>
    public static async Task<OvertimeBudget?> FindAsync(ZayraDbContext db, Guid tenantId, int employeeId, DateOnly workDate, CancellationToken ct)
    {
        var departmentId = await DepartmentOfAsync(db, tenantId, employeeId, ct);
        if (departmentId is null) return null;
        return await Query(db, tenantId, departmentId.Value, workDate).AsNoTracking().FirstOrDefaultAsync(ct);
    }

    /// <summary>The same budget, tracked and row-locked (FOR UPDATE) for the caller's open transaction, to be consumed.</summary>
    public static async Task<OvertimeBudget?> ForUpdateAsync(ZayraDbContext db, Guid tenantId, int employeeId, DateOnly workDate, CancellationToken ct)
    {
        var departmentId = await DepartmentOfAsync(db, tenantId, employeeId, ct);
        if (departmentId is null) return null;
        var query = Query(db, tenantId, departmentId.Value, workDate);
        if (db.Database.IsRelational() && db.Database.CurrentTransaction is not null) query = query.TagWith(RowLockingInterceptor.ForUpdateTag);
        return await query.FirstOrDefaultAsync(ct);
    }

    public static bool Exceeds(OvertimeBudget? budget, decimal amount) =>
        budget is not null && budget.ConsumedAmount + amount > budget.BudgetAmount;

    public static bool Blocks(string mode, OvertimeBudget? budget, decimal amount) =>
        mode == OvertimeBudgetModes.Block && Exceeds(budget, amount);

    public static void Consume(OvertimeBudget? budget, decimal amount)
    {
        if (budget is not null) budget.ConsumedAmount += amount;
    }

    /// <summary>Gives an amount back (overtime converted to time off is no longer an overtime cost). Never below zero.</summary>
    public static void Release(OvertimeBudget? budget, decimal amount)
    {
        if (budget is not null) budget.ConsumedAmount = Math.Max(0m, budget.ConsumedAmount - amount);
    }

    public static object Refusal(OvertimeBudget budget, decimal amount) => new
    {
        code = "overtime_budget_exceeded",
        message = $"Approving this overtime ({amount:0.00}) would take the department over its overtime budget for {budget.Year}-{budget.Month:00} "
                  + $"({budget.ConsumedAmount:0.00} of {budget.BudgetAmount:0.00} used). This company blocks approvals over budget: raise the budget, or approve fewer minutes.",
        messageAr = $"اعتماد هذا العمل الإضافي ({amount:0.00}) يتجاوز ميزانية القسم للعمل الإضافي لشهر {budget.Year}-{budget.Month:00} "
                    + $"(المستخدم {budget.ConsumedAmount:0.00} من {budget.BudgetAmount:0.00}). هذه الشركة تمنع الاعتماد عند تجاوز الميزانية: ارفع الميزانية أو اعتمد دقائق أقل.",
        budget = new { budget.Id, budget.Year, budget.Month, budget.BudgetAmount, budget.ConsumedAmount, budget.Currency },
    };

    public static object Warning(OvertimeBudget budget, decimal amount) => new
    {
        code = "overtime_budget_exceeded",
        message = $"Approved, but this takes the department over its overtime budget for {budget.Year}-{budget.Month:00} ({budget.ConsumedAmount + amount:0.00} of {budget.BudgetAmount:0.00}).",
        messageAr = $"تم الاعتماد، لكنه يتجاوز ميزانية القسم للعمل الإضافي لشهر {budget.Year}-{budget.Month:00} ({budget.ConsumedAmount + amount:0.00} من {budget.BudgetAmount:0.00}).",
    };

    private static IQueryable<OvertimeBudget> Query(ZayraDbContext db, Guid tenantId, Guid departmentId, DateOnly workDate) =>
        db.OvertimeBudgets.Where(b => b.TenantId == tenantId && b.DepartmentId == departmentId && b.ProjectId == null
                                      && b.Year == workDate.Year && b.Month == workDate.Month);

    private static Task<Guid?> DepartmentOfAsync(ZayraDbContext db, Guid tenantId, int employeeId, CancellationToken ct) =>
        db.Employees.AsNoTracking().Where(e => e.TenantId == tenantId && e.Id == employeeId).Select(e => e.DepartmentId).FirstOrDefaultAsync(ct);
}
