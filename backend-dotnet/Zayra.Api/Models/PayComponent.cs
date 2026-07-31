using Zayra.Api.Domain.Entities;

namespace Zayra.Api.Models;

/// <summary>
/// Tenant/company-scoped, data-driven pay-component DEFINITION. This is the configurability
/// keystone: the payslip's earning/allowance/deduction set is no longer a fixed compiled sequence
/// in <c>PayrollController.Process</c> but a seeded catalog a client can extend/re-order/re-route.
///
/// It is a DEFINITION + CLASSIFICATION + ROUTING record — exactly the role <see cref="GlDriver"/>
/// plays for GL posting — NOT a value store: the AMOUNT of a component is produced by a value
/// provider (a salary-structure column, a client Fixed/Percent value, or a subsystem ledger such as
/// overtime/tax/loan/bonus/statutory). See <c>PayComponentEngine</c>.
///
/// Precedent it deliberately mirrors (do not diverge): <see cref="GlDriver"/> — CompanyId == null is
/// a tenant-wide default; a CompanyId-set row is a company override that WINS. System rows
/// (IsSystem=true) reproduce today's compiled behaviour byte-for-byte and the engine falls back to
/// the compiled sequence when the store is empty, so a not-yet-seeded tenant is unaffected.
///
/// Statutory rows (IsStatutory=true — GOSI/SANED/GPSSA/GRSIA/OH) are DEFINITION-ONLY: their amount is
/// ALWAYS the country-pack result governed by StatutoryRateGuard; the engine never lets config drive a
/// statutory VALUE (only its GL routing / order / label). See <see cref="PayComponentGuard"/>.
/// </summary>
public class PayComponent : ITenantOwned, ICompanyScoped
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }

    /// <summary>NULL = tenant-wide default (visible to every company); set = company override (wins).</summary>
    public Guid? CompanyId { get; set; }

    /// <summary>Stable component code. For NON-family rows this is the emitted <c>PayrollEarning/Deduction.ComponentCode</c>
    /// (e.g. "BASIC","HOUSING","OVERTIME","INCOME_TAX"). For family rows (BONUS/ADJ) it is only the catalog
    /// identity — the emitted per-instance code comes from the source item (e.g. "BONUS_ANNUAL","ADJ_...").</summary>
    public string Code { get; set; } = string.Empty;

    public string NameEn { get; set; } = string.Empty;
    public string NameAr { get; set; } = string.Empty;

    /// <summary>Earning | Deduction | EmployerContribution. See <see cref="PayComponentTypes"/>.
    /// EmployerContribution rows are persisted as PayrollDeduction rows with IsEmployerContribution=true
    /// (they do not reduce employee net) — identical to the pack's ER statutory lines today.</summary>
    public string ComponentType { get; set; } = PayComponentTypes.Earning;

    /// <summary>Fixed | PercentOfBasic | PercentOfGross | Formula | StructureField | Integration | Statutory.
    /// See <see cref="PayComponentCalcMethods"/>. Dispatch precedence in the engine: IsStatutory ⇒ pack;
    /// else <see cref="ProviderKey"/> when set; else this CalcMethod.</summary>
    public string CalcMethod { get; set; } = PayComponentCalcMethods.Fixed;

    /// <summary>For CalcMethod=StructureField: which EmployeeSalaryStructure column feeds the amount.
    /// See <see cref="PayComponentStructureFields"/>. "OtherAllowancesComposite" = Food+Mobile+Other (the
    /// three columns the current engine lumps into one OTHER_ALLOWANCES line).</summary>
    public string? StructureField { get; set; }

    /// <summary>Fixed amount (CalcMethod=Fixed) or percent (PercentOfBasic/PercentOfGross). NULL for the
    /// day-one seeds (their amounts come from a structure column or a subsystem ledger).</summary>
    public decimal? Value { get; set; }

    /// <summary>Whitelisted expression for CalcMethod=Formula (a later client opt-in surface). NULL for all
    /// day-one seeds — the equivalence path never evaluates a string formula.</summary>
    public string? FormulaExpression { get; set; }

    /// <summary>Built-in value-provider key for Integration/Statutory rows: "Bonus","Adjustment","Loan",
    /// "Advance","Leave","Attendance.Short","Attendance.Lop","Overtime","Tax","Statutory".
    /// See <see cref="PayComponentProviders"/>.</summary>
    public string? ProviderKey { get; set; }

    // ── Classification flags (routing/reporting hints) ─────────────────────────
    // NOTE: these are CLASSIFICATION metadata. Per the country-pack contract, the GOSI/GPSSA/GRSIA
    // covered-wage BASE is derived inside the pack from the 4-slot SalaryBreakdown, NOT from GosiSubject —
    // so GosiSubject is a display/report hint and MUST NOT be wired into the statutory base (would change
    // GOSI results and cannot express the per-country base matrix). Bonus GOSI-inclusion stays on BonusType.
    public bool IsTaxable { get; set; }
    public bool GosiSubject { get; set; }
    public bool WpsIncluded { get; set; } = true;
    public bool EosbIncluded { get; set; }

    /// <summary>Ties the emitted line to a gl_drivers Key. NULL ⇒ the existing EarningDriverKey/
    /// DeductionDriverKey source+code routing is used unchanged (the day-one seeds set this only for
    /// documentation parity; GL routing still keys on the persisted line Source+Code).</summary>
    public string? GlDriverKey { get; set; }

    /// <summary>True ⇒ emit the line even when the amount is 0 (reproduces BASIC's unconditional emit).
    /// Every other seeded line suppresses at ≤ 0.</summary>
    public bool EmitWhenZero { get; set; }

    /// <summary>True ⇒ the provider yields 0..n per-instance lines (BONUS/ADJ); Code is only catalog identity.</summary>
    public bool IsFamily { get; set; }

    /// <summary>Stable emission ordinal within the component's stream (earnings vs deductions ordered
    /// independently). Chosen so OrderBy(DisplayOrder) reproduces the current emit order exactly.</summary>
    public int DisplayOrder { get; set; }

    /// <summary>System rows reproduce today's compiled sequence; add-only, immutable core fields.</summary>
    public bool IsSystem { get; set; }

    /// <summary>True ⇒ amount is ALWAYS the country-pack statutory result (StatutoryRateGuard-governed);
    /// config can never drive a statutory value — only its GL routing / order / label.</summary>
    public bool IsStatutory { get; set; }

    public bool IsActive { get; set; } = true;
    public bool IsDeleted { get; set; }

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public Guid? CreatedBy { get; set; }
    public DateTime? UpdatedAtUtc { get; set; }
    public Guid? UpdatedBy { get; set; }
}

public static class PayComponentTypes
{
    public const string Earning = "Earning";
    public const string Deduction = "Deduction";
    public const string EmployerContribution = "EmployerContribution";
}

public static class PayComponentCalcMethods
{
    public const string Fixed = "Fixed";
    public const string PercentOfBasic = "PercentOfBasic";
    public const string PercentOfGross = "PercentOfGross";
    public const string Formula = "Formula";
    public const string StructureField = "StructureField";
    public const string Integration = "Integration";
    public const string Statutory = "Statutory";
}

public static class PayComponentProviders
{
    public const string Bonus = "Bonus";
    public const string Adjustment = "Adjustment";
    public const string Loan = "Loan";
    public const string Advance = "Advance";
    public const string Leave = "Leave";
    public const string AttendanceShort = "Attendance.Short";
    public const string AttendanceLop = "Attendance.Lop";
    public const string Overtime = "Overtime";
    public const string Tax = "Tax";
    public const string Statutory = "Statutory";
}

public static class PayComponentStructureFields
{
    public const string BasicSalary = "BasicSalary";
    public const string HousingAllowance = "HousingAllowance";
    public const string TransportAllowance = "TransportAllowance";
    public const string OtherAllowancesComposite = "OtherAllowancesComposite"; // Food + Mobile + Other
    public const string FixedDeduction = "FixedDeduction";
}

/// <summary>
/// Seed template + ultimate fallback for the data-driven pay components. This is NOT the runtime source
/// of truth (that is the pay_components table via <see cref="PayComponent"/>); it is the single place the
/// system default rows are declared so the seeder, the create-tenant path and the empty-store fallback all
/// agree. Mirrors <see cref="PayrollGlCatalog"/>.
///
/// The rows reproduce PayrollController.Process's per-employee earning/deduction emission EXACTLY:
///   • the DisplayOrder values yield the current emit order under OrderBy(DisplayOrder);
///   • Code/NameEn/Source produce byte-identical PayrollEarning/PayrollDeduction rows;
///   • GlDriverKey matches the current EarningDriverKey/DeductionDriverKey output so GL routing is unchanged.
/// Nothing here may change behaviour on day one — it is the golden-master baseline.
/// </summary>
public static class PayComponentCatalog
{
    public static IReadOnlyList<PayComponent> SystemComponentSeeds(Guid tenantId) => new[]
    {
        // ── Earnings (emit order via DisplayOrder) ─────────────────────────────
        // BONUS: integration family; gross amount per instance. GosiSubject is per-BonusType at runtime
        // (IsIncludedInGosiBase) and folded into the pack base in Process — the static flag stays false.
        Comp(tenantId, "BONUS", "Bonus", "مكافأة", PayComponentTypes.Earning, PayComponentCalcMethods.Integration,
            provider: PayComponentProviders.Bonus, glDriverKey: "EARN:BONUS", order: 10,
            isFamily: true, wps: true),
        // ADJ (+ve): approved positive payroll adjustments, one line each.
        Comp(tenantId, "ADJ", "Adjustment", "تسوية", PayComponentTypes.Earning, PayComponentCalcMethods.Integration,
            provider: PayComponentProviders.Adjustment, glDriverKey: null, order: 20,
            isFamily: true, wps: true),
        // BASIC: covered-wage base, taxable base default, EOSB base. ALWAYS emitted (even at 0).
        Comp(tenantId, "BASIC", "Basic salary", "الراتب الأساسي", PayComponentTypes.Earning, PayComponentCalcMethods.StructureField,
            structureField: PayComponentStructureFields.BasicSalary, glDriverKey: "EARN:BASIC", order: 30,
            emitWhenZero: true, taxable: true, gosi: true, wps: true, eosb: true),
        // HOUSING: part of the GOSI/GPSSA covered wage (pack-derived), not EOSB base.
        Comp(tenantId, "HOUSING", "Housing allowance", "بدل السكن", PayComponentTypes.Earning, PayComponentCalcMethods.StructureField,
            structureField: PayComponentStructureFields.HousingAllowance, glDriverKey: "EARN:HOUSING", order: 40,
            gosi: true, wps: true),
        Comp(tenantId, "TRANSPORT", "Transport allowance", "بدل النقل", PayComponentTypes.Earning, PayComponentCalcMethods.StructureField,
            structureField: PayComponentStructureFields.TransportAllowance, glDriverKey: "EARN:TRANSPORT", order: 50,
            wps: true),
        // OTHER_ALLOWANCES: the current engine LUMPS Food + Mobile + Other into this single line + one GL driver.
        Comp(tenantId, "OTHER_ALLOWANCES", "Other allowances", "بدلات أخرى", PayComponentTypes.Earning, PayComponentCalcMethods.StructureField,
            structureField: PayComponentStructureFields.OtherAllowancesComposite, glDriverKey: "EARN:OTHER_ALLOWANCES", order: 60,
            wps: true),
        // OVERTIME: subsystem-produced (OvertimePayrollImpacts × hourlyRate × multiplier); dynamic label.
        Comp(tenantId, "OVERTIME", "Overtime", "العمل الإضافي", PayComponentTypes.Earning, PayComponentCalcMethods.Integration,
            provider: PayComponentProviders.Overtime, glDriverKey: "EARN:OVERTIME", order: 70,
            wps: true),

        // ── Deductions (emit order via DisplayOrder) ───────────────────────────
        Comp(tenantId, "FIXED_DEDUCTION", "Fixed deduction", "خصم ثابت", PayComponentTypes.Deduction, PayComponentCalcMethods.StructureField,
            structureField: PayComponentStructureFields.FixedDeduction, glDriverKey: "DED:FIXED_DEDUCTION", order: 10),
        // INCOME_TAX: client CompanyTaxPolicy rate × taxable base (bounded by StatutoryRateGuard); dynamic label.
        Comp(tenantId, "INCOME_TAX", "Income tax", "ضريبة الدخل", PayComponentTypes.Deduction, PayComponentCalcMethods.Integration,
            provider: PayComponentProviders.Tax, glDriverKey: "DED:TAX", order: 20),
        Comp(tenantId, "ATTENDANCE", "Late/early attendance deduction", "خصم الحضور", PayComponentTypes.Deduction, PayComponentCalcMethods.Integration,
            provider: PayComponentProviders.AttendanceShort, glDriverKey: "DED:ATTENDANCE", order: 30),
        // LOP: absent days × day-rate; dynamic label. Shares the DED:ATTENDANCE driver today.
        Comp(tenantId, "LOP_DEDUCTION", "Loss of Pay", "خصم الغياب", PayComponentTypes.Deduction, PayComponentCalcMethods.Integration,
            provider: PayComponentProviders.AttendanceLop, glDriverKey: "DED:ATTENDANCE", order: 40),
        Comp(tenantId, "LEAVE", "Leave deduction", "خصم الإجازة", PayComponentTypes.Deduction, PayComponentCalcMethods.Integration,
            provider: PayComponentProviders.Leave, glDriverKey: "DED:LEAVE", order: 50),
        Comp(tenantId, "LOAN_EMI", "Loan instalment", "قسط القرض", PayComponentTypes.Deduction, PayComponentCalcMethods.Integration,
            provider: PayComponentProviders.Loan, glDriverKey: "DED:LOAN", order: 60),
        Comp(tenantId, "ADVANCE_EMI", "Salary advance repayment", "سداد السلفة", PayComponentTypes.Deduction, PayComponentCalcMethods.Integration,
            provider: PayComponentProviders.Advance, glDriverKey: "DED:LOAN", order: 70),
        // ADJ (−ve): approved negative payroll adjustments, one line each (abs amount).
        Comp(tenantId, "ADJ", "Adjustment", "تسوية", PayComponentTypes.Deduction, PayComponentCalcMethods.Integration,
            provider: PayComponentProviders.Adjustment, glDriverKey: null, order: 80,
            isFamily: true),
        // STATUTORY_EE: employee-side pack lines (GOSI-ANN-EE, GOSI-SANED-EE, GPSSA-EE, GRSIA-EE). Value = pack.
        Comp(tenantId, "STATUTORY_EE", "Social insurance (Employee)", "التأمينات (الموظف)", PayComponentTypes.Deduction, PayComponentCalcMethods.Statutory,
            provider: PayComponentProviders.Statutory, glDriverKey: "DED:STATUTORY_EE", order: 90,
            isStatutory: true, isFamily: true, gosi: true),
        // STATUTORY_ER: employer-side pack lines (…-ER); IsEmployerContribution, does not reduce net. Value = pack.
        Comp(tenantId, "STATUTORY_ER", "Social insurance (Employer)", "التأمينات (صاحب العمل)", PayComponentTypes.EmployerContribution, PayComponentCalcMethods.Statutory,
            provider: PayComponentProviders.Statutory, glDriverKey: "DED:STATUTORY_ER", order: 100,
            isStatutory: true, isFamily: true, gosi: true),
    };

    private static PayComponent Comp(
        Guid tenantId, string code, string nameEn, string nameAr, string type, string calcMethod,
        string? structureField = null, string? provider = null, string? glDriverKey = null,
        int order = 0, bool emitWhenZero = false, bool isFamily = false, bool isStatutory = false,
        bool taxable = false, bool gosi = false, bool wps = false, bool eosb = false) => new()
    {
        TenantId = tenantId, CompanyId = null, Code = code, NameEn = nameEn, NameAr = nameAr,
        ComponentType = type, CalcMethod = calcMethod, StructureField = structureField, ProviderKey = provider,
        GlDriverKey = glDriverKey, DisplayOrder = order, EmitWhenZero = emitWhenZero, IsFamily = isFamily,
        IsStatutory = isStatutory, IsTaxable = taxable, GosiSubject = gosi, WpsIncluded = wps, EosbIncluded = eosb,
        IsSystem = true, IsActive = true,
    };
}

/// <summary>
/// Compliance boundary for pay components — the analogue of <see cref="Zayra.Api.Infrastructure.Payroll.StatutoryRateGuard"/>
/// for the component catalog. Identifies which seeded component codes are statutory (their value is
/// pack-governed and NOT client-editable) so a write path / the engine can enforce the carve-out in one place.
/// </summary>
public static class PayComponentGuard
{
    // Statutory component code families: the STATUTORY_EE/ER catalog rows plus the raw pack line codes
    // they re-emit (GOSI-*, SANED via GOSI-SANED-*, GPSSA-*, GRSIA-*). Prefix + exact, case-folded.
    private static readonly string[] StatutoryPrefixes =
    {
        "STATUTORY_", "GOSI", "GPSSA", "GRSIA", "SANED",
    };

    /// <summary>True when a component code denotes a statutory line whose VALUE is owned by the country
    /// pack (StatutoryRateGuard) and can never be driven by client configuration.</summary>
    public static bool IsStatutoryComponentCode(string? code)
    {
        if (string.IsNullOrWhiteSpace(code)) return false;
        var k = code.Trim().ToUpperInvariant();
        return StatutoryPrefixes.Any(p => k.StartsWith(p, StringComparison.Ordinal));
    }
}
