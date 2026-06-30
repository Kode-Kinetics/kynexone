using Zayra.Api.Domain.Entities;

namespace Zayra.Api.Models;

/// <summary>A line in the tenant's chart of accounts.</summary>
public class GlAccount : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public string Code { get; set; } = string.Empty;       // e.g. "5001"
    public string Name { get; set; } = string.Empty;       // e.g. "Basic Salary Expense"
    public string AccountType { get; set; } = "Expense";    // Asset | Liability | Expense | Equity | Revenue
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAtUtc { get; set; }
}

/// <summary>
/// Maps a payroll posting <see cref="PayrollGlCatalog"/> driver key to a GL account.
/// When absent, posting falls back to the built-in defaults so behaviour is unchanged.
/// </summary>
public class GlAccountMapping : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public string DriverKey { get; set; } = string.Empty;   // e.g. "EARN:BASIC", "NET_PAYABLE"
    public Guid AccountId { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAtUtc { get; set; }
}

/// <summary>
/// The fixed set of payroll posting "drivers" and their built-in default accounts.
/// A driver is an abstract line in the payroll journal (an earning bucket, a deduction
/// bucket, the employer statutory expense, or net pay). Tenants may remap any driver to
/// their own chart via <see cref="GlAccountMapping"/>; unmapped drivers use these defaults.
/// </summary>
public static class PayrollGlCatalog
{
    public record Driver(string Key, string Label, string DefaultCode, string DefaultName, string AccountType);

    public static readonly IReadOnlyList<Driver> Drivers = new[]
    {
        // Earnings (debit / expense)
        new Driver("EARN:BASIC",            "Earning — Basic Salary",        "5001", "Basic Salary Expense",            "Expense"),
        new Driver("EARN:HOUSING",          "Earning — Housing Allowance",   "5002", "Housing Allowance Expense",       "Expense"),
        new Driver("EARN:TRANSPORT",        "Earning — Transport Allowance", "5003", "Transport Allowance Expense",     "Expense"),
        new Driver("EARN:OTHER_ALLOWANCES", "Earning — Other Allowances",    "5004", "Other Allowances Expense",        "Expense"),
        new Driver("EARN:OVERTIME",         "Earning — Overtime",            "5005", "Overtime Expense",                "Expense"),
        new Driver("EARN:OTHER",            "Earning — Other",               "5099", "Other Earnings",                  "Expense"),
        new Driver("EARN:BONUS",            "Earning — Bonus",               "6100", "Employee Bonus Expense",          "Expense"),
        // Deductions (credit / liability)
        new Driver("DED:STATUTORY_EE",      "Deduction — Social Insurance (Employee)", "2101", "Social Insurance Payable (Employee)", "Liability"),
        new Driver("DED:STATUTORY_ER",      "Deduction — Social Insurance (Employer)", "2106", "Social Insurance Employer Payable",   "Liability"),
        new Driver("DED:TAX",               "Deduction — Income Tax",        "2102", "Income Tax Payable",              "Liability"),
        new Driver("DED:LOAN",              "Deduction — Loans & Advances",  "2107", "Loan & Advance Deductions Payable","Liability"),
        new Driver("DED:ATTENDANCE",        "Deduction — Attendance",        "2104", "Attendance Adjustment Payable",   "Liability"),
        new Driver("DED:LEAVE",             "Deduction — Leave",             "2105", "Leave Deduction Payable",         "Liability"),
        new Driver("DED:FIXED_DEDUCTION",   "Deduction — Fixed",             "2103", "Fixed Deductions Payable",        "Liability"),
        new Driver("DED:OTHER",             "Deduction — Other",             "2199", "Other Deductions",                "Liability"),
        // Balancing entries
        new Driver("EMPLOYER_STATUTORY_EXPENSE", "Employer Statutory Expense", "5101", "Employer Social Insurance Expense", "Expense"),
        new Driver("NET_PAYABLE",           "Net Salaries Payable",          "2100", "Salaries Payable",                "Liability"),
    };

    public static IReadOnlyDictionary<string, (string Code, string Name)> Defaults { get; } =
        Drivers.ToDictionary(d => d.Key, d => (d.DefaultCode, d.DefaultName));
}
