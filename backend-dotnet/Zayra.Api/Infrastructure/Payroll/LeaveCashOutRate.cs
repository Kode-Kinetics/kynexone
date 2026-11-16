using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Payroll;

/// <summary>
/// THE one place the leave day-rate basis is decided. A final settlement encashes untaken leave at
/// <c>base / 30</c> where <c>base</c> follows the company's <c>leave_cashout_basis</c> rule; the monthly leave
/// provision values the same days at the same rate, so the two can never disagree about what a day is worth.
/// <see cref="LeaveEncashmentCalculator"/> does the division by 30 and the per-row rounding for both callers.
/// </summary>
public static class LeaveCashOutRate
{
    /// <summary>The whole monthly package: the structure's basic and fixed allowances plus the customer-defined cash allowances.</summary>
    public static decimal MonthlyGross(EmployeeSalaryStructure? salary, decimal basicWage, decimal cashAllowanceTotal)
        => basicWage + (salary?.HousingAllowance ?? 0m) + (salary?.TransportAllowance ?? 0m)
         + (salary?.FoodAllowance ?? 0m) + (salary?.MobileAllowance ?? 0m) + (salary?.OtherAllowance ?? 0m) + cashAllowanceTotal;

    /// <summary>The monthly amount the day rate is taken from, per the company's basis rule (anything unrecognised is the gross package).</summary>
    public static decimal BaseFor(string basisRule, decimal basicWage, decimal housing, decimal monthlyGross) => basisRule switch
    {
        CompanyPayrollRules.BasisBasic => basicWage,
        CompanyPayrollRules.BasisBasicHousing => basicWage + housing,
        _ => monthlyGross,
    };

    /// <summary>The basis as the settlement line narrates it ("gross/30").</summary>
    public static string LabelFor(string basisRule) => basisRule switch
    {
        CompanyPayrollRules.BasisBasic => "basic/30",
        CompanyPayrollRules.BasisBasicHousing => "(basic+housing)/30",
        _ => LeaveEncashmentCalculator.DayRateBasis,
    };
}
