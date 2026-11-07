using Microsoft.EntityFrameworkCore;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Payroll;

namespace Zayra.Api.Infrastructure.Compensation;

/// <summary>
/// A merit-award reversal restores the pay the employee had agreed to before the award, so it is a correction and not a new reduction. Whether a change IS a
/// reversal is known only from the link <see cref="Models.CompWorksheetLine.ReversalChangeRequestId"/>, which <c>MeritPlanningService.ReverseAwardAsync</c>
/// writes server-side; nothing a client sends can set it, so an ordinary salary change can never claim the exemption.
/// </summary>
public static class MeritAwardReversals
{
    /// <summary>The salary record the award was planned against (the pre-award package) when <paramref name="changeRequestId"/> is the reversal of an award for this
    /// employee; null for every other change.</summary>
    public static async Task<Guid?> PreAwardRowAsync(ZayraDbContext db, Guid tenantId, int employeeId, Guid changeRequestId, CancellationToken ct) =>
        await db.CompWorksheetLines.AsNoTracking()
            .Where(l => l.TenantId == tenantId && l.EmployeeId == employeeId && l.ReversalChangeRequestId == changeRequestId)
            .Select(l => l.SalaryRowId).FirstOrDefaultAsync(ct);

    /// <summary>True when every item the reversal lowers stays at or above what that item was in the pre-award record (slots and allowance lines). A raised fixed
    /// deduction, or a pre-award record that cannot be found, is not a restoration.</summary>
    public static async Task<bool> RestoresPreAwardPayAsync(ZayraDbContext db, Guid tenantId, Guid preAwardRowId, ReductionResult reduction, CancellationToken ct)
    {
        if (!reduction.IsReduction) return false;
        var before = await db.EmployeeSalaryStructures.AsNoTracking().FirstOrDefaultAsync(s => s.TenantId == tenantId && s.Id == preAwardRowId, ct);
        if (before is null) return false;
        var beforeLines = (await db.EmployeeSalaryAllowances.AsNoTracking().Where(a => a.TenantId == tenantId && a.SalaryStructureId == preAwardRowId).ToListAsync(ct))
            .GroupBy(a => a.Code, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Sum(a => a.Amount), StringComparer.Ordinal);
        var beforeSlots = new Dictionary<string, decimal>(StringComparer.Ordinal)
        {
            ["BASIC"] = before.BasicSalary, ["HOUSING"] = before.HousingAllowance, ["TRANSPORT"] = before.TransportAllowance,
            ["FOOD"] = before.FoodAllowance, ["MOBILE"] = before.MobileAllowance, ["OTHER"] = before.OtherAllowance,
        };
        foreach (var item in reduction.Items)
        {
            if (item.Item == SalaryReductions.FixedDeductionItem) return false;
            var floor = beforeSlots.TryGetValue(item.Item, out var slot) ? slot : beforeLines.GetValueOrDefault(item.Item);
            if (item.To < floor) return false;
        }
        return true;
    }
}
