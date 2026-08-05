namespace Zayra.Api.Infrastructure.Payroll;

/// <summary>
/// POD-B3 — WHICH blocking validation errors an approver may consciously override, and which are not
/// judgements at all.
///
/// <para>The problem this closes: <c>PayrollValidationResult.IsResolved</c> was declared, read at Approve,
/// Lock, the overview card and the readiness wizard — and SET BY NOTHING. So any Error on a Processed run
/// was exit-only-via-Void: Approve/Lock 422, re-Process refuses a non-Draft run, DeleteRun is Draft-only,
/// and the population selector is frozen for Processed runs, which meant the remedy some errors themselves
/// suggest ("exclude this employee from the run") was unreachable. That dead end is what POD-B3 removes.</para>
///
/// <para>But an override endpoint with no policy would be worse than the dead end. The split below is the
/// whole control:</para>
///
/// <list type="bullet">
/// <item><b>NON-OVERRIDABLE</b> — the error asserts an ARITHMETIC IMPOSSIBILITY or a cross-run payment
///   integrity fact. Overriding it does not accept a business risk, it produces a wrong journal or a
///   second salary. The exit for these is to FIX the run: <c>POST runs/{id}/reopen</c> (no GL posted) or
///   void → Replacement (GL posted).</item>
/// <item><b>OVERRIDABLE</b> — the error asserts a DATA-QUALITY or CLASSIFICATION judgement that a
///   competent approver can genuinely make and be accountable for (a contractor with no IBAN paid by
///   cheque; a nationality/GOSI classification the pack got wrong for a dual national).</item>
/// </list>
///
/// <para><b>ALREADY_PAID_THIS_PERIOD is deliberately non-overridable.</b> It is the only control in this
/// codebase that looks ACROSS runs, and the engine's own remedy text offers three exits — exclude the
/// employee, switch to a supplemental basis, or void the other run. None of them is "override it".
/// Overriding it authorises paying one person two full salaries in one month in a product whose next step
/// wires money via WPS. Its exit is <c>reopen</c>, which unfreezes the population selector so the operator
/// can do what the error actually asks.</para>
///
/// <para><b>DEFAULT-DENY.</b> <see cref="IsOverridable"/> tests membership of the ALLOW list, not absence
/// from the deny list, so a validation code added later (by this team or a country pack) is
/// non-overridable until someone deliberately classifies it. Both lists are published so the deny side is
/// legible to an auditor rather than merely implied.</para>
/// </summary>
public static class PayrollValidationOverridePolicy
{
    /// <summary>
    /// Errors an approver MAY clear with a recorded reason. Anything not here is refused.
    /// </summary>
    public static readonly IReadOnlySet<string> Overridable = new HashSet<string>(StringComparer.Ordinal)
    {
        // Bank details — an employee genuinely paid outside the wage file (cheque / cash / foreign account).
        "MISSING_IBAN",
        "INVALID_IBAN",
        // Statutory classification — the pack derives Saudi/GCC/expat from a free-text nationality field.
        // A dual national or a GOSI exemption certificate is a real, documentable judgement.
        "GOSI_MISSING_FOR_SAUDI",
        "GOSI_APPLIED_TO_EXPAT",
        // Overtime hours approved against a zero basic (allowance-only contractor) — the approver can
        // state the rate basis rather than being blocked out of the month.
        "OT_RATE_UNRESOLVED",
        // A lapsed salary assignment on someone the run is not actually paying a structure-derived wage to.
        "MISSING_SALARY_STRUCTURE",
        // Company country set at the group rather than the entity — a configuration judgement, and the
        // statutory pack guard in Process already refuses to write payslips without a resolvable pack.
        "COUNTRY_CODE_MISSING",
    };

    /// <summary>
    /// Errors that can NEVER be overridden, published explicitly so the refusal is auditable rather than
    /// an accident of the allow list. Kept in sync with <see cref="PayrollValidationEngine"/>.
    /// </summary>
    public static readonly IReadOnlySet<string> NonOverridable = new HashSet<string>(StringComparer.Ordinal)
    {
        // Cross-run double payment. Exit = reopen + exclude, or void the other run. Never an override.
        "ALREADY_PAID_THIS_PERIOD",
        // Arithmetic impossibilities: overriding produces an unbalanced or wrong journal, not a decision.
        "NEGATIVE_NET",
        "ZERO_NET_WITH_GROSS",
        "GL_WILL_NOT_BALANCE",
        "TOTALS_GROSS_MISMATCH",
        "TOTALS_DEDUCTIONS_MISMATCH",
        "TOTALS_NET_MISMATCH",
        // The same employee twice in one run — a data fault with one correct answer.
        "DUPLICATE_EMPLOYEE",
        // No company ⇒ no country pack ⇒ the statutory figures on the slips are not merely doubtful,
        // they were never computed.
        "COMPANY_NOT_RESOLVED",
    };

    /// <summary>Default-deny: only codes on the published ALLOW list may be overridden.</summary>
    public static bool IsOverridable(string? code) =>
        !string.IsNullOrWhiteSpace(code) && Overridable.Contains(code!);

    /// <summary>Minimum length of the mandatory override reason — long enough to be a sentence, so
    /// "ok" / "fix" cannot pass for accountability.</summary>
    public const int MinimumReasonLength = 10;
}
