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
        // POD-C3 — recovery of a prior void's ALREADY-DISBURSED net pay. It credits the 1420 ASSET the
        // void debited, NOT a payable: net then equals "what we should have paid − what we already paid",
        // which is the definition of a recovery. Routing it to DED:OTHER (2199) instead would credit a
        // liability nobody owes and leave 1420 ageing forever — the exact gap POD-B3 handed to C3.
        new Driver("DED:RECEIVABLE_RECOVERY","Deduction — Overpayment Recovery","1420","Employee Overpayment Receivable","Asset"),
        // Balancing entries
        new Driver("EMPLOYER_STATUTORY_EXPENSE", "Employer Statutory Expense", "5101", "Employer Social Insurance Expense", "Expense"),
        new Driver("NET_PAYABLE",           "Net Salaries Payable",          "2100", "Salaries Payable",                "Liability"),
        // POD-B1 SETTLEMENT — the cash/bank asset the net-pay and statutory-remittance disbursements
        // credit when a liability is cleared. Name/code deliberately match the "1000 - Cash/Bank"
        // string the Loans/Advances/Bonus modules already post to (AdvancesController.cs:336,
        // BonusesController.cs:588) so the whole product books cash to ONE consolidated account.
        new Driver("CASH_BANK",             "Cash / Bank",                   "1000", "Cash/Bank",                       "Asset"),
        // POD-B1b — control accounts owned by the bonus / loan / advance modules. Codes+names are
        // byte-identical to the string literals those controllers hard-coded before this pod
        // (BonusesController.cs:517 "2300 - Bonus Payable", LoansController.cs:181
        // "1400 - Employee Loans Receivable", AdvancesController.cs:183 "1410 - Employee Salary
        // Advances"), so routing them through ResolveGlAccount resolves the SAME account label for
        // every existing tenant via the compiled fallback — with the added ability to remap per company.
        new Driver("BONUS_PAYABLE",         "Bonus Payable",                 "2300", "Bonus Payable",                   "Liability"),
        new Driver("LOAN_RECEIVABLE",       "Employee Loans Receivable",     "1400", "Employee Loans Receivable",       "Asset"),
        new Driver("ADVANCE_RECEIVABLE",    "Employee Salary Advances",      "1410", "Employee Salary Advances",        "Asset"),
        // POD-B3 RECOVERY — where a voided run's ALREADY-DISBURSED cash is carried. Both are ASSETS: the
        // money left the bank, the run that justified it no longer exists, so what each party holds is
        // recoverable. Without them a void of a settled/remitted month could only be expressed by
        // crediting Cash/Bank back, which is a lie the bank statement disproves.
        new Driver("EMPLOYEE_RECEIVABLE",   "Employee Overpayment Receivable","1420", "Employee Overpayment Receivable","Asset"),
        new Driver("STATUTORY_PREPAID",     "Prepaid Statutory Remittance",  "1430", "Prepaid Statutory Remittance",    "Asset"),
        // ── POD-C1 TERMINATION SETTLEMENT ────────────────────────────────────────────────────────
        // Before this pod there was NO end-of-service account and NO driver anywhere in the catalog, so
        // the gratuity liability never reached the books at all. Codes 5110/5111/5112/5113 sit next to
        // 5101 (the other employer-borne people cost) and 2310/2320 next to 2300 Bonus Payable.
        // 5006/5007 were deliberately AVOIDED: gl_accounts carries a real UNIQUE (tenant, company, code)
        // and the shipped GlPhase2Tests create custom client drivers on exactly those two codes, so
        // seeding tenant defaults there would collide.
        new Driver("EARN:EOSB",             "Earning — End of Service",      "5110", "End of Service Benefit Expense",  "Expense"),
        new Driver("EARN:LEAVE_ENCASHMENT", "Earning — Leave Encashment",    "5111", "Leave Encashment Expense",        "Expense"),
        new Driver("EARN:NOTICE_PAY",       "Earning — Notice Pay in Lieu",  "5112", "Payment in Lieu of Notice",       "Expense"),
        // A settlement-side deduction (notice shortfall, other final deductions) is NOT a debt owed to a
        // third party — it REDUCES what the employer owes. Routing it to DED:OTHER (2199) would credit a
        // payable that RemitGroups.ForSource has no remittance path for, leaving permanent junk on the
        // balance sheet. It credits a CONTRA-EXPENSE instead, so net employment cost is right and every
        // account this pod touches provably returns to zero.
        new Driver("DED:SETTLEMENT_RECOVERY","Deduction — Final Settlement Recovery","5113","Final Settlement Deductions (contra)","Expense"),
        // The gratuity PROVISION POD-C2 will accrue monthly. C1 only CONSUMES it (see EosbProvisionLedger)
        // and posts nothing to it until C2 exists, so today every settlement expenses the full gratuity.
        new Driver("EOSB_PROVISION",        "End of Service Benefit Provision","2310","End of Service Benefit Provision","Liability"),
        // What the leaver is owed between approval and disbursement. Cleared by the payroll run's own
        // Lock journal, exactly the way POD-B1b clears Bonus Payable.
        new Driver("SETTLEMENT_PAYABLE",    "Final Settlement Payable",      "2320", "Final Settlement Payable",        "Liability"),
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
        // POD-C3 — Exact match on the RECEIVABLE_RECOVERY component code so it is selected ahead of the
        // catch-all DED:OTHER, exactly the way DED:FIXED_DEDUCTION is. AccountType is Asset: the credit
        // RELIEVES the 1420 receivable the void recognised.
        Sys(tenantId, "DED:RECEIVABLE_RECOVERY","Deduction — Overpayment Recovery",GlDriverCategories.Deduction,"CR","Asset",  "1420", "Employee Overpayment Receivable",  null,        "Exact",  "RECEIVABLE_RECOVERY", 27),
        Sys(tenantId, "EMPLOYER_STATUTORY_EXPENSE", "Employer Statutory Expense",GlDriverCategories.Balancing,"DR", "Expense",   "5101", "Employer Social Insurance Expense",null,        "Any",    null,               100),
        Sys(tenantId, "NET_PAYABLE",           "Net Salaries Payable",          GlDriverCategories.Balancing, "CR", "Liability", "2100", "Salaries Payable",                 null,        "Any",    null,               101),
        // POD-B1 — Cash/Bank disbursement account. Category=Balancing so component routing
        // (ResolveDriverForComponent filters Earning/Deduction only) NEVER selects it; it is resolved
        // exclusively by explicit ResolveGlAccount("CASH_BANK", …) from the settlement/remittance builders,
        // exactly like NET_PAYABLE. Adding it here auto-populates PayrollGlCatalog.Defaults so posting
        // resolves "1000 - Cash/Bank" for every tenant via the compiled fallback with no data migration.
        Sys(tenantId, "CASH_BANK",             "Cash / Bank",                   GlDriverCategories.Balancing, "CR", "Asset",     "1000", "Cash/Bank",                        null,        "Any",    null,               102),
        // POD-B1b — bonus/loan/advance control accounts. Category=Balancing for the same reason as
        // CASH_BANK: ResolveDriverForComponent only ever considers Earning/Deduction rows, so a payroll
        // component can NEVER be routed here by accident. They are resolved exclusively by explicit
        // ResolveGlAccount("BONUS_PAYABLE"/"LOAN_RECEIVABLE"/"ADVANCE_RECEIVABLE", …) from the bonus,
        // loan and advance posting paths. Listing them in PayrollGlCatalog.Drivers auto-populates
        // Defaults, so every tenant resolves the historical account label with zero data migration.
        Sys(tenantId, "BONUS_PAYABLE",         "Bonus Payable",                 GlDriverCategories.Balancing, "CR", "Liability", "2300", "Bonus Payable",                    null,        "Any",    null,               103),
        Sys(tenantId, "LOAN_RECEIVABLE",       "Employee Loans Receivable",     GlDriverCategories.Balancing, "DR", "Asset",     "1400", "Employee Loans Receivable",        null,        "Any",    null,               104),
        Sys(tenantId, "ADVANCE_RECEIVABLE",    "Employee Salary Advances",      GlDriverCategories.Balancing, "DR", "Asset",     "1410", "Employee Salary Advances",         null,        "Any",    null,               105),
        // POD-B3 — recovery receivables. Category=Balancing for the same reason as CASH_BANK: component
        // routing only ever considers Earning/Deduction rows, so no payroll component can land here by
        // accident; they are resolved exclusively by explicit ResolveGlAccount from the void path.
        Sys(tenantId, "EMPLOYEE_RECEIVABLE",   "Employee Overpayment Receivable",GlDriverCategories.Balancing,"DR","Asset",     "1420", "Employee Overpayment Receivable",  null,        "Any",    null,               106),
        Sys(tenantId, "STATUTORY_PREPAID",     "Prepaid Statutory Remittance",  GlDriverCategories.Balancing, "DR", "Asset",     "1430", "Prepaid Statutory Remittance",     null,        "Any",    null,               107),
        // ── POD-C1 — settlement EARNING drivers, matched EXACTLY on the component code ────────────
        // Exact (rank 3) so they are selected ahead of the catch-all EARN:OTHER, which is seeded `Any`
        // (:171) and would otherwise CLAIM every settlement code and route the gratuity to 5099 Other
        // Earnings — the same defect POD-C3 had to fix for ARREARS_*. EarningDriverKeyFor carries the
        // matching precedence rule for tenants that have gl_drivers rows but have not re-seeded yet.
        Sys(tenantId, "EARN:EOSB",             "Earning — End of Service",      GlDriverCategories.Earning,   "DR", "Expense",   "5110", "End of Service Benefit Expense",   null,        "Exact",  "EOSB_GRATUITY",    15),
        Sys(tenantId, "EARN:LEAVE_ENCASHMENT", "Earning — Leave Encashment",    GlDriverCategories.Earning,   "DR", "Expense",   "5111", "Leave Encashment Expense",         null,        "Exact",  "LEAVE_ENCASHMENT", 16),
        Sys(tenantId, "EARN:NOTICE_PAY",       "Earning — Notice Pay in Lieu",  GlDriverCategories.Earning,   "DR", "Expense",   "5112", "Payment in Lieu of Notice",        null,        "Exact",  "NOTICE_PAY",       17),
        // Matched on the SOURCE rather than a code list, so a client that adds its own settlement-side
        // deduction code still lands on the contra-expense instead of 2199. `Any` + a non-null MatchSource
        // outranks DED:OTHER (`Any`, MatchSource null) on ResolveDriverForComponent's
        // ThenByDescending(MatchSource != null) tie-break.
        Sys(tenantId, "DED:SETTLEMENT_RECOVERY","Deduction — Final Settlement Recovery",GlDriverCategories.Deduction,"CR","Expense","5113","Final Settlement Deductions (contra)","Settlement","Any",null,            28),
        // Category=Balancing for the same reason as CASH_BANK / BONUS_PAYABLE: ResolveDriverForComponent
        // only ever considers Earning/Deduction rows, so no payroll component can be routed to a control
        // account by accident. They are resolved EXCLUSIVELY by explicit GlAccountResolver.AccountLabel.
        Sys(tenantId, "EOSB_PROVISION",        "End of Service Benefit Provision",GlDriverCategories.Balancing,"CR","Liability","2310", "End of Service Benefit Provision", null,        "Any",    null,               108),
        Sys(tenantId, "SETTLEMENT_PAYABLE",    "Final Settlement Payable",      GlDriverCategories.Balancing, "CR", "Liability", "2320", "Final Settlement Payable",         null,        "Any",    null,               109),
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

/// <summary>Lifecycle states for a GL accounting period.</summary>
public static class GlPeriodStatuses
{
    public const string Open   = "Open";
    public const string Closed = "Closed";
}

/// <summary>
/// Canonical EventType tags for the payroll GL lifecycle. One declaration point so the accrual
/// (Lock), the POD-B1 settlement/remittance clearing journals, their reversals, and the void contra
/// all agree — and so idempotency/tie-out queries never rely on scattered string literals.
/// </summary>
public static class GlEventTypes
{
    public const string Accrual               = "PayrollLock";                  // liabilities created on Lock
    public const string NetSettlement         = "PayrollNetSettlement";         // DR 2100 / CR 1000 on Pay
    public const string NetSettlementReversal = "PayrollNetSettlementReversal"; // contra of the above
    public const string Void                  = "PayrollVoid";                  // full-run void contra
    public const string RemitPrefix           = "PayrollRemit:";                // + group (GOSI/TAX/LOAN)
    public const string RemitReversalPrefix   = "PayrollRemitReversal:";        // + group

    // ── POD-B3 — RECLASSIFICATION on a void whose CASH ALREADY LEFT ─────────────────────────────────
    // A void must NEVER contra a disbursement whose funds really moved: crediting Cash/Bank back would
    // put money on the books that the bank statement does not have, and the Replacement run would then
    // disburse a second time with no receivable recognised against the first. When the operator states
    // the funds were DISBURSED (not recalled), the void leaves the settlement/remittance journal standing
    // and posts a reclassification instead — CR the control liability (so it still closes to zero) / DR a
    // receivable — so Cash/Bank keeps the true outflow and what was actually paid out is carried as
    // recoverable. Netting that receivable into the replacement is retro/arrears work (POD-C3).
    /// <summary>Net pay left the bank on a run that is now voided: DR Employee Overpayment Receivable / CR 2100.</summary>
    public const string NetSettlementReclass  = "PayrollNetSettlementReclass";
    /// <summary>Statutory cash left the bank on a run that is now voided: DR Prepaid Statutory / CR 2101-2106-2102.</summary>
    public const string RemitReclassPrefix    = "PayrollRemitReclass:";         // + group

    public static string Remit(string group)         => RemitPrefix + group;
    public static string RemitReversal(string group) => RemitReversalPrefix + group;
    public static string RemitReclass(string group)  => RemitReclassPrefix + group;

    /// <summary>
    /// POD-B3 — is this event itself the UNDOING of a payroll journal (a contra or a reclassification)?
    /// </summary>
    public static bool IsPayrollUnwindContra(string? eventType) =>
        eventType == Void
     || eventType == NetSettlementReversal
     || eventType == NetSettlementReclass
     // POD-C1 — the settlement accrual's contra is an UNDO and must never itself be reversed: a run void
     // that reversed a cancellation contra would silently RE-APPLY a settlement the operator cancelled,
     // putting 2320 back on the books with nothing to disburse it. Its sibling
     // SettlementPayrollClearing is deliberately NOT here — that one is an ORIGINATING journal (it is
     // what DEBITS 2320 inside the Lock accrual), so IsOriginatingPayrollJournal must keep returning
     // true for it or a void could not re-open the payable it consumed.
     || eventType == SettlementAccrualReversal
     || eventType == EosbProvisionConsumptionReversal
     || (eventType is not null && (eventType.StartsWith(RemitReversalPrefix, StringComparison.Ordinal)
                                || eventType.StartsWith(RemitReclassPrefix, StringComparison.Ordinal)));

    /// <summary>
    /// POD-B3 — is this event an ORIGINATING payroll journal (something a later reversal may act on), as
    /// opposed to a contra / reclassification that is itself the undoing of one?
    ///
    /// <para>Load-bearing for the void. Before B3 the void contra'd EVERY live Payroll row for the run,
    /// which included the contras written by <c>settle/reverse</c> and <c>remit/reverse</c> (those are
    /// persisted with <c>IsReversed = false</c>, because nothing has reversed THEM). Reversing a reversal
    /// RE-APPLIES the original: a run whose settlement had been reversed and was then voided had its net
    /// pay silently re-settled by the void, moving cash on the books a second time. Only originating
    /// journals are ever unwound now.</para>
    ///
    /// <para>Defined as "NOT a contra" rather than as a list of known originating tags, and that direction
    /// is deliberate: payroll GL rows written before the EventType vocabulary existed carry an EMPTY tag,
    /// and an allow-list would silently stop reversing them — on the ~55 live tenants, whose oldest locked
    /// runs are exactly those rows. Default-reverse, explicitly skip the undo events.</para>
    /// </summary>
    public static bool IsOriginatingPayrollJournal(string? eventType) => !IsPayrollUnwindContra(eventType);

    // ── POD-B1b — bonus lifecycle ────────────────────────────────────────────────────────────────
    // Expense is recognised ONCE, at accrual. Every later event only moves the payable.
    /// <summary>Approve: DR bonus expense / CR Bonus Payable (gross).</summary>
    public const string BonusAccrual = "BonusAccrual";
    /// <summary>Legacy accrual tag written by ApproveBatch before POD-B1b. Read (never written) so
    /// batches approved on the old model are still recognised + cleared by the payroll clearing path.</summary>
    public const string BonusAccrualLegacy = "BonusApproval";
    /// <summary>Contra of an accrual when an approved batch is cancelled.</summary>
    public const string BonusAccrualReversal = "BonusAccrualReversal";
    /// <summary>Paid through payroll: the Lock journal DRs the stored accrual account instead of
    /// re-expensing the bonus. Emitted as part of the payroll accrual journal, not a separate one.</summary>
    public const string BonusPayrollClearing = "BonusPayrollClearing";
    /// <summary>Paid outside payroll (MarkBatchPaid): DR Bonus Payable / CR tax + cash.</summary>
    public const string BonusPayment = "BonusPayment";

    /// <summary>Every event that DEBITS (clears) the bonus payable. Used to compute how much of an
    /// accrual is still outstanding so a batch can never be cleared twice.</summary>
    public static readonly string[] BonusClearingEvents =
        { BonusPayrollClearing, BonusPayment, BonusAccrualReversal };

    // ── POD-C1 — TERMINATION SETTLEMENT lifecycle ────────────────────────────────────────────────
    // Expense is recognised ONCE, at APPROVAL. Every later event only moves the payable — the same
    // doctrine POD-B1b established for bonuses, and the reason 2320 provably returns to zero.
    /// <summary>Approve: DR gratuity/encashment/notice expense (net of any POD-C2 provision consumed) /
    /// CR 2320 Final Settlement Payable (GROSS of the settlement's earning lines).</summary>
    public const string SettlementAccrual = "FinalSettlementAccrual";
    /// <summary>Contra of the accrual when an approved settlement is cancelled.</summary>
    public const string SettlementAccrualReversal = "FinalSettlementAccrualReversal";
    /// <summary>Disbursed through payroll: the run's Lock journal DEBITS the settlement's STORED payable
    /// account instead of re-expensing the settlement. Emitted INSIDE the payroll accrual journal (like
    /// BonusPayrollClearing), never as a journal of its own.</summary>
    public const string SettlementPayrollClearing = "FinalSettlementPayrollClearing";
    /// <summary>POD-C1 (F2) — residual employee debt reclassified on payment: DR 1420 / CR 1400|1410.</summary>
    public const string SettlementResidualReclass = "FinalSettlementResidualReclass";

    // ── POD-C2 SEAM — the monthly EOSB liability accrual ─────────────────────────────────────────
    // C1 builds and READS this sub-ledger; it never WRITES an accrual. POD-C2 posts
    // EosbProvisionAccrual (DR 5110 expense / CR 2310 provision) monthly per employee, and this pod's
    // approve path then consumes the position first and expenses only the shortfall — with no rework.
    /// <summary>POD-C2 (not written here): DR EOSB expense / CR 2310 provision, per employee per month.</summary>
    public const string EosbProvisionAccrual = "EosbProvisionAccrual";
    /// <summary>POD-C1: DR 2310 provision — the settlement CONSUMES what C2 provided for this employee.</summary>
    public const string EosbProvisionConsumption = "EosbProvisionConsumption";
    /// <summary>Contra of a consumption when the settlement that took it is cancelled (re-opens the
    /// provision for the same employee).</summary>
    public const string EosbProvisionConsumptionReversal = "EosbProvisionConsumptionReversal";
}

/// <summary>Stable, remap-immune identifiers embedded in bonus/loan/advance line Descriptions —
/// the bonus counterpart of <see cref="PayrollGlDescriptions"/>. The payroll clearing path locates a
/// batch's accrual by (SourceModule, SourceEntityId, EventType, CompanyId) and then DRs its STORED
/// account, so these strings are for humans + reporting, never for balance arithmetic.</summary>
public static class BonusGlDescriptions
{
    public const string AccrualPrefix        = "Bonus accrual: ";
    public const string PayrollClearingPrefix = "Bonus payable cleared via payroll: ";
    public const string PaymentPrefix        = "Bonus payment: ";
    public const string TaxWithheldPrefix    = "Bonus tax withheld: ";
    public const string ReversalPrefix       = "Bonus accrual reversal: ";
    /// <summary>Deduction component code for the tax withheld on a bonus paid through payroll.
    /// Source="Tax" ⇒ routes to DED:TAX (2102) and remits with the TAX group like any other PIT.</summary>
    public const string PayrollTaxComponentCode = "BONUS_TAX";
    public const string PayrollTaxComponentName = "Bonus tax withheld";
}

/// <summary>Stable, remap-immune identifiers embedded in the accrual line Descriptions. The settlement
/// and remittance builders locate the exact accrual lines to clear by these (and then DR the lines'
/// STORED account/amount verbatim), so a chart-of-accounts remap between Lock and Pay/Remit can never
/// make a control account drift off zero.</summary>
public static class PayrollGlDescriptions
{
    public const string NetPayable      = "Net salaries payable";
    public const string DeductionPrefix = "Payroll deduction: ";
}

/// <summary>Statutory remittance groups for POD-B1. A group maps a deduction Source to the set of
/// control accounts a single remittance clears (they go to different authorities on different dates).</summary>
public static class RemitGroups
{
    public const string Gosi = "GOSI";  // Source == "Statutory" → clears 2101 (EE) + 2106 (ER)
    public const string Tax  = "TAX";   // Source == "Tax"       → clears 2102
    public const string Loan = "LOAN";  // Source == "Loan"      → clears 2107
    public const string All  = "All";   // every applicable group in one call

    public static readonly string[] Concrete = { Gosi, Tax, Loan };

    /// <summary>Deduction Source → remittance group, or null when the source is not remitted here.</summary>
    public static string? ForSource(string source) => source switch
    {
        "Statutory" => Gosi,
        "Tax"       => Tax,
        "Loan"      => Loan,
        _           => null,
    };
}

/// <summary>
/// POD-B1 — GL period-close control. The ABSENCE of a Closed row for a scope+period means the period
/// is OPEN (open is the default, so no backfill is needed for the ~55 live tenants). A Status="Closed"
/// row BLOCKS new GL postings dated into that period (enforced by
/// <see cref="Zayra.Api.Infrastructure.Payroll.PeriodCloseGuard"/> on the Lock / settle / remit / void
/// posting paths). Reopen is an explicit, reason-gated, audited transition back to Open.
///
/// Scope tier: <see cref="ICompanyScoped"/> (config, NOT operational): a group-wide close
/// (CompanyId == null) is a legitimate tenant-wide shared row that MUST be visible to every company's
/// list view — the poison-default hardening of ICompanyScopedOperational would wrongly hide it from
/// company-scoped users. A company-specific close (CompanyId set) closes only that company's period.
/// </summary>
public class GlPeriodClose : ITenantOwned, ICompanyScoped
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    // Null = group/tenant-wide close (blocks every company for the period); set = company-specific close.
    public Guid? CompanyId { get; set; }
    public string Period { get; set; } = string.Empty;   // "YYYY-MM"
    public string Status { get; set; } = GlPeriodStatuses.Closed;

    public DateTime? ClosedAtUtc { get; set; }
    public Guid? ClosedByUserId { get; set; }
    public string? ClosedByName { get; set; }
    public string? ClosedReason { get; set; }

    public DateTime? ReopenedAtUtc { get; set; }
    public Guid? ReopenedByUserId { get; set; }
    public string? ReopenedByName { get; set; }
    public string? ReopenReason { get; set; }

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAtUtc { get; set; }
}
