using Zayra.Api.Domain.Entities;

namespace Zayra.Api.Models;

/// <summary>
/// One Contractual (or per-employee Facility) entitlement of one employee, frozen for one contract term.
///
/// <para><b>Capability.</b> Release A requirement 1/2 (rev 8.3 §2.2): the contract-year package. A grade cell
/// (<see cref="GradeEntitlement"/>) says what a grade gets <i>today</i>; this row says what <i>this employee</i>
/// was given for <i>this term</i>, so a later change to the grade table never rewrites a signed package
/// (Art. 59 acquired rights). No existing table can carry it: the salary row may hold only QiwaWage cash.</para>
///
/// <para><b>Who writes it.</b> Only <c>IEntitlementWriter</c> (Release A slice R2). Close-only: a row's values
/// never change; the only permitted updates are shortening <see cref="EffectiveTo"/> and confirming a
/// migrated row (<see cref="VerificationState"/> Unverified → Verified). DELETE is refused by trigger.</para>
///
/// <para><b>What the database enforces</b> (see the ReleaseAEntitlementsAndRenewals migration): the value
/// shape and source/provenance CHECKs, no overlapping rows per (employee, component) (EXCLUDE), and a deferred
/// containment trigger — the row lies inside its contract term, on a contract that is neither Draft nor
/// Superseded, for the same company; a Carried row equals its origin.</para>
///
/// <para><see cref="EmployeeId"/> is the employee's <b>PublicId</b> (uuid), matching
/// <see cref="EmployeeContract.EmployeeId"/>; APIs speak the int <c>employees.id</c>.</para>
/// </summary>
public class EmployeeEntitlement : ITenantOwned, ICompanyScopedOperational
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }

    /// <summary>Employer of record. NOT NULL in the database; nullable here only to satisfy <see cref="ICompanyScoped"/>.</summary>
    public Guid? CompanyId { get; set; }

    /// <summary>The employee's PublicId (FK employees(tenant_id, public_id)).</summary>
    public Guid EmployeeId { get; set; }

    /// <summary>The contract term this row is frozen for (FK employee_contracts(tenant_id, employee_id, id)).</summary>
    public Guid ContractId { get; set; }

    public string PayComponentCode { get; set; } = string.Empty;

    /// <summary>Contractual or Facility. QiwaWage cash never lives here.</summary>
    public string EntitlementClass { get; set; } = PayEntitlementClasses.Contractual;

    public string ValueType { get; set; } = GradeEntitlementValueTypes.EligibilityOnly;
    public decimal? Amount { get; set; }
    public decimal? Rate { get; set; }
    public decimal? MaxOutstandingAmount { get; set; }
    public string? CoverageTier { get; set; }
    public short? Quantity { get; set; }
    public string DependantScope { get; set; } = DependantScopes.None;
    public short? MaxDependants { get; set; }
    public string? LimitPeriod { get; set; }

    /// <summary>Witness: the cash figure this row resolved to when written (e.g. 25% of basic → SAR 2,000).</summary>
    public decimal? ResolvedAmount { get; set; }

    /// <summary>Witness for a PercentOfBasic row: the salary row whose basic was used. Required exactly then.</summary>
    public Guid? ResolvedBasisSalaryId { get; set; }

    /// <summary>See <see cref="EntitlementSources"/>.</summary>
    public string Source { get; set; } = EntitlementSources.GradeDefault;

    /// <summary>See <see cref="EntitlementVerificationStates"/>. Migrated rows start Unverified until HR confirms.</summary>
    public string VerificationState { get; set; } = EntitlementVerificationStates.Verified;

    /// <summary>The grade cell a GradeDefault row came from (FK carries the component code).</summary>
    public Guid? GradeEntitlementId { get; set; }

    /// <summary>The approval that authorised an Exception or Correction (and only those).</summary>
    public Guid? ApprovalRequestId { get; set; }

    public Guid? RenewalCaseId { get; set; }

    /// <summary>For a Carried row (holdover, re-resolution): the row it copies. Its provenance is the origin's.</summary>
    public Guid? CarriedFromEntitlementId { get; set; }

    /// <summary>For a Correction: the contract or Qiwa document that proves the corrected value.</summary>
    public Guid? CorrectionBasisDocumentId { get; set; }

    public DateOnly EffectiveFrom { get; set; }
    public DateOnly? EffectiveTo { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public Guid? CreatedBy { get; set; }

    public bool IsInEffect(DateOnly on) => EffectiveFrom <= on && (EffectiveTo is null || EffectiveTo.Value >= on);
}

/// <summary>Value set of <c>employee_entitlements.source</c>.</summary>
public static class EntitlementSources
{
    /// <summary>Copied from the grade cell in force when the term was frozen.</summary>
    public const string GradeDefault = "GradeDefault";
    /// <summary>Differs from the grade cell, under an approval (renewal offer or exception).</summary>
    public const string Exception = "Exception";
    /// <summary>A prospective correction down to the documented contract/Qiwa value, under an approval and a document.</summary>
    public const string Correction = "Correction";
    /// <summary>Loaded for an existing employee by the freeze job; Unverified until HR confirms.</summary>
    public const string Migrated = "Migrated";
    /// <summary>A copy of an earlier row (holdover into a provisional term, PercentOfBasic re-resolution).</summary>
    public const string Carried = "Carried";
    public static readonly string[] All = [GradeDefault, Exception, Correction, Migrated, Carried];
}

/// <summary>Value set of <c>employee_entitlements.verification_state</c>.</summary>
public static class EntitlementVerificationStates
{
    public const string Unverified = "Unverified";
    public const string Verified = "Verified";
    public static readonly string[] All = [Unverified, Verified];
}
