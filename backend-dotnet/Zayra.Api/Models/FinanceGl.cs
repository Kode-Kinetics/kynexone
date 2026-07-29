using Zayra.Api.Domain.Entities;

namespace Zayra.Api.Models;

/// <summary>A line in the tenant's chart of accounts.
/// Phase 2: company-scoped. CompanyId == null is the group/tenant-default account
/// (visible to every company); CompanyId set is a company-specific account.</summary>
public class GlAccount : ITenantOwned, ICompanyScoped
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    // Null = group/tenant default account; set = company-specific account (wins in resolution).
    public Guid? CompanyId { get; set; }
    public string Code { get; set; } = string.Empty;       // e.g. "5001"
    public string Name { get; set; } = string.Empty;       // e.g. "Basic Salary Expense"
    public string AccountType { get; set; } = "Expense";    // Asset | Liability | Expense | Equity | Revenue
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAtUtc { get; set; }
}

/// <summary>
/// Maps a payroll posting driver key (see <see cref="GlDriver"/>) to a GL account.
/// Phase 2: company-scoped override. CompanyId == null is the tenant-default mapping;
/// CompanyId set is a per-company override that wins over the tenant default.
/// When neither exists, posting falls back to the seeded driver default.
/// </summary>
public class GlAccountMapping : ITenantOwned, ICompanyScoped
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    // Null = tenant-default mapping; set = company override (wins).
    public Guid? CompanyId { get; set; }
    public string DriverKey { get; set; } = string.Empty;   // e.g. "EARN:BASIC", "NET_PAYABLE"
    public Guid AccountId { get; set; }
    // Model A segment overlay: optional cost-center tag stamped on this posting line.
    public Guid? SegmentCostCenterId { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAtUtc { get; set; }
}

/// <summary>
/// Persisted, extensible payroll posting driver. Replaces the compiled
/// <see cref="PayrollGlCatalog"/> as the RUNTIME source of truth; the catalog is retained
/// only as the compile-time seed template (single declaration point for the 17 system rows)
/// and as the ultimate fallback when the gl_drivers store is empty.
///
/// Company-scoped: CompanyId == null is a tenant-wide driver; CompanyId set is company-specific.
/// System rows (IsSystem=true) reproduce today's switch byte-for-byte; custom rows are add-only
/// and may only WIN by being strictly more specific than a system row (system wins on a tie).
/// </summary>
public class GlDriver : ITenantOwned, ICompanyScoped
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid? CompanyId { get; set; }            // NULL = tenant-wide driver; set = company-specific
    public string Key { get; set; } = string.Empty; // "EARN:BASIC" or custom "EARN:WELLNESS"
    public string Label { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;    // "Earning" | "Deduction" | "Balancing"
    public string PostingSide { get; set; } = string.Empty; // "DR" | "CR"
    public string AccountType { get; set; } = "Expense";
    public string DefaultCode { get; set; } = string.Empty;
    public string DefaultName { get; set; } = string.Empty;

    // Data-driven routing predicate (reproduces the compiled switch).
    public string? MatchSource { get; set; }        // "Bonus","Statutory","Tax","Loan","Attendance","Leave"; NULL = any source
    public string MatchMode { get; set; } = "Exact"; // "Exact" | "Suffix" | "Prefix" | "Any"
    public string? MatchComponentCode { get; set; } // pattern per MatchMode; NULL only with "Any"
    public bool EmitsEmployerExpensePair { get; set; }
    public string? PairedExpenseDriverKey { get; set; }

    public bool IsSystem { get; set; }
    public bool IsActive { get; set; } = true;
    public int SortOrder { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAtUtc { get; set; }
}

public static class GlDriverCategories
{
    public const string Earning = "Earning";
    public const string Deduction = "Deduction";
    public const string Balancing = "Balancing";
}

public static class GlDriverMatchModes
{
    public const string Exact = "Exact";
    public const string Suffix = "Suffix";
    public const string Prefix = "Prefix";
    public const string Any = "Any";
}

/// <summary>
/// Seed template + ultimate fallback for the payroll posting drivers. This is NOT the runtime
/// source of truth (that is the gl_drivers table via <see cref="GlDriver"/>); it is the single
/// place the 17 default rows are declared so the seeder, the create-tenant path and the empty-store
/// fallback all agree. A driver is an abstract journal line (an earning bucket, a deduction bucket,
/// the employer statutory expense, or net pay).
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

    /// <summary>
    /// Full seed rows for the gl_drivers store: the 17 system drivers with the routing predicates
    /// that reproduce EarningDriverKey/DeductionDriverKey exactly. Single declaration used by the
    /// SeedDefaults endpoint, the create-tenant provisioning path, and the schema data-seed migration.
    /// </summary>
    public static IReadOnlyList<GlDriver> SystemDriverSeeds(Guid tenantId) => new[]
    {
        Sys(tenantId, "EARN:BONUS",            "Earning — Bonus",               GlDriverCategories.Earning,   "DR", "Expense",   "6100", "Employee Bonus Expense",           "Bonus",     "Any",    null,               1),
        Sys(tenantId, "EARN:BASIC",            "Earning — Basic Salary",        GlDriverCategories.Earning,   "DR", "Expense",   "5001", "Basic Salary Expense",             null,        "Exact",  "BASIC",            10),
        Sys(tenantId, "EARN:HOUSING",          "Earning — Housing Allowance",   GlDriverCategories.Earning,   "DR", "Expense",   "5002", "Housing Allowance Expense",        null,        "Exact",  "HOUSING",          11),
        Sys(tenantId, "EARN:TRANSPORT",        "Earning — Transport Allowance", GlDriverCategories.Earning,   "DR", "Expense",   "5003", "Transport Allowance Expense",      null,        "Exact",  "TRANSPORT",        12),
        Sys(tenantId, "EARN:OTHER_ALLOWANCES", "Earning — Other Allowances",    GlDriverCategories.Earning,   "DR", "Expense",   "5004", "Other Allowances Expense",         null,        "Exact",  "OTHER_ALLOWANCES", 13),
        Sys(tenantId, "EARN:OVERTIME",         "Earning — Overtime",            GlDriverCategories.Earning,   "DR", "Expense",   "5005", "Overtime Expense",                 null,        "Exact",  "OVERTIME",         14),
        Sys(tenantId, "EARN:OTHER",            "Earning — Other",               GlDriverCategories.Earning,   "DR", "Expense",   "5099", "Other Earnings",                   null,        "Any",    null,               99),
        Sys(tenantId, "DED:STATUTORY_ER",      "Deduction — Social Ins (ER)",   GlDriverCategories.Deduction, "CR", "Liability", "2106", "Social Insurance Employer Payable","Statutory", "Suffix", "-ER",              20, emitsPair: true, paired: "EMPLOYER_STATUTORY_EXPENSE"),
        Sys(tenantId, "DED:STATUTORY_EE",      "Deduction — Social Ins (EE)",   GlDriverCategories.Deduction, "CR", "Liability", "2101", "Social Insurance Payable (Employee)","Statutory","Any",   null,               21),
        Sys(tenantId, "DED:TAX",               "Deduction — Income Tax",        GlDriverCategories.Deduction, "CR", "Liability", "2102", "Income Tax Payable",               "Tax",       "Any",    null,               22),
        Sys(tenantId, "DED:LOAN",              "Deduction — Loans & Advances",  GlDriverCategories.Deduction, "CR", "Liability", "2107", "Loan & Advance Deductions Payable","Loan",      "Any",    null,               23),
        Sys(tenantId, "DED:ATTENDANCE",        "Deduction — Attendance",        GlDriverCategories.Deduction, "CR", "Liability", "2104", "Attendance Adjustment Payable",    "Attendance","Any",    null,               24),
        Sys(tenantId, "DED:LEAVE",             "Deduction — Leave",             GlDriverCategories.Deduction, "CR", "Liability", "2105", "Leave Deduction Payable",          "Leave",     "Any",    null,               25),
        Sys(tenantId, "DED:FIXED_DEDUCTION",   "Deduction — Fixed",             GlDriverCategories.Deduction, "CR", "Liability", "2103", "Fixed Deductions Payable",         null,        "Exact",  "FIXED_DEDUCTION",  26),
        Sys(tenantId, "DED:OTHER",             "Deduction — Other",             GlDriverCategories.Deduction, "CR", "Liability", "2199", "Other Deductions",                 null,        "Any",    null,               98),
        Sys(tenantId, "EMPLOYER_STATUTORY_EXPENSE", "Employer Statutory Expense",GlDriverCategories.Balancing,"DR", "Expense",   "5101", "Employer Social Insurance Expense",null,        "Any",    null,               100),
        Sys(tenantId, "NET_PAYABLE",           "Net Salaries Payable",          GlDriverCategories.Balancing, "CR", "Liability", "2100", "Salaries Payable",                 null,        "Any",    null,               101),
    };

    private static GlDriver Sys(
        Guid tenantId, string key, string label, string category, string side, string accountType,
        string defCode, string defName, string? matchSource, string matchMode, string? matchCode, int sort,
        bool emitsPair = false, string? paired = null) => new()
    {
        TenantId = tenantId, CompanyId = null, Key = key, Label = label, Category = category,
        PostingSide = side, AccountType = accountType, DefaultCode = defCode, DefaultName = defName,
        MatchSource = matchSource, MatchMode = matchMode, MatchComponentCode = matchCode,
        EmitsEmployerExpensePair = emitsPair, PairedExpenseDriverKey = paired,
        IsSystem = true, IsActive = true, SortOrder = sort,
    };
}
