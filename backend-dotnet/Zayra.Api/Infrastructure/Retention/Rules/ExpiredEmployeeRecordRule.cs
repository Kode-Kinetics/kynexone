using Microsoft.EntityFrameworkCore;
using Zayra.Api.Infrastructure.Data;
using Zayra.Api.Infrastructure.Jobs;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Retention.Rules;

/// <summary>
/// D3 — the rule that finally READS <c>Employee.RetentionUntilUtc</c>.
///
/// <para>THE DEFECT THIS CLOSES. <c>EmployeesController.SoftDeleteEmployeeAsync</c> stamps
/// <c>RetentionUntilUtc = DeletedAtUtc + 7 years</c> and <c>PrivacyStatus =
/// "RetainedForStatutoryAudit"</c> on every deleted employee. Nothing has ever read either column. The
/// product therefore records a retention deadline and then keeps the record — passport number, iqama
/// number, IBAN, date of birth, medical information — for ever. <c>Employee.RedactedAtUtc</c> exists in
/// the schema for the redaction that was never built. This rule is that redaction.</para>
///
/// <para><b>ANONYMISE, NOT DELETE — and this is the load-bearing decision.</b> There is not one foreign
/// key in the database pointing at <c>employees</c>: roughly 130 <c>EmployeeId</c> columns across ~45
/// model files are unenforced integers. So a hard delete would neither cascade nor be blocked — it would
/// silently orphan payroll runs, payslips, GL postings, WPS/SIF records, GOSI references and final
/// settlements, and the database would not say a word. Those are the records Saudi labour law and the
/// tax authority require the employer to be able to produce. Erasing the person's row out from under
/// them destroys the employer's evidence while leaving the (denormalised) payroll copies of the name in
/// place — the worst of both outcomes. Clearing the identifying columns in place satisfies the erasure
/// interest, keeps the statutory record intact, and leaves every relationship valid.</para>
///
/// <para><b>THE STATUTORY OVERRIDE.</b> An employee's erasure clock (deletion + 7y) and a payroll
/// record's retention floor (the record's own date + 7y) are different clocks, and the payroll one can
/// still be running when the employee one stops — a final settlement is normally computed AFTER the
/// leaving date, and any payroll activity after the soft delete pushes the floor past it. Where they
/// conflict this rule KEEPS THE RECORD and writes the reason. It never resolves the conflict in favour
/// of deletion.</para>
///
/// <para><b>WHAT THIS RULE DOES NOT DO, stated plainly.</b> It anonymises the <c>employees</c> row only.
/// Denormalised <c>EmployeeName</c> / <c>EmployeeCode</c> / <c>Iban</c> copies on ~40 dependent tables,
/// the full-PII <c>EmployeeHistory.SnapshotJson</c> blobs, and object-store blobs behind
/// <c>EmployeeDocument.StorageUrl</c> and <c>ProfilePhotoStorageKey</c> all survive. The audit row counts
/// them and names them, so the residual is measured rather than implied. Reaching them is a second
/// phase with its own design (blob deletion is not transactional with the database).</para>
/// </summary>
public sealed class ExpiredEmployeeRecordRule : IRetentionRule
{
    private const string EmployeeScope =
        "Retention sweep reads soft-deleted employees of the job's tenant with no request principal; the " +
        "tenant is re-applied from the claimed job row and the company filter must not apply because an " +
        "expired record may have no company attribution at all.";
    private const string PayrollScope =
        "Retention sweep checks the statutory payroll floor across every legal entity of the job's tenant; " +
        "tenant re-applied from the claimed job row, company scope deliberately dropped because the floor " +
        "is a tenant-level obligation and a transferred employee's evidence may sit under another company.";

    /// <summary>
    /// The value written into a cleared string column. A distinguishable constant rather than an empty
    /// string so an operator reading the table can tell "erased" from "never captured".
    /// </summary>
    public const string Cleared = "[erased]";

    /// <summary><c>PrivacyStatus</c> after a successful anonymisation.</summary>
    public const string AnonymisedStatus = "Anonymised";

    private readonly DataRetentionOptions _options;

    public ExpiredEmployeeRecordRule(DataRetentionOptions options) => _options = options;

    public string RuleKey => RetentionRuleKeys.EmployeeRetentionExpired;

    public async Task<IReadOnlyList<RetentionCandidate>> EvaluateAsync(
        JobExecutionContext ctx, DateTime nowUtc, int maxCandidates, CancellationToken ct)
    {
        // Oldest deadline first: a backlog larger than one batch drains in deadline order rather than
        // leaving the longest-overdue records permanently at the back of an arbitrary window.
        var expired = await Employees(ctx)
            .Where(e => e.IsDeleted && e.RetentionUntilUtc != null && e.RetentionUntilUtc <= nowUtc
                        && e.RedactedAtUtc == null)
            .OrderBy(e => e.RetentionUntilUtc)
            .Take(maxCandidates)
            .Select(e => new { e.Id, e.RetentionUntilUtc, e.DeletedAtUtc })
            .ToListAsync(ct);
        if (expired.Count == 0) return [];

        var ids = expired.Select(x => x.Id).ToList();

        // The statutory floor, per employee: the most recent payroll evidence of any kind. Two sources,
        // because a final settlement is a payroll record that often post-dates the last payslip.
        var lastPayslip = await ScopedBypass
            .TenantWide(ctx.Db.Payslips, ctx.TenantId, PayrollScope)
            .Where(p => ids.Contains(p.EmployeeId))
            .GroupBy(p => p.EmployeeId)
            .Select(g => new { EmployeeId = g.Key, Latest = g.Max(p => p.CreatedAtUtc) })
            .ToDictionaryAsync(x => x.EmployeeId, x => x.Latest, ct);

        var lastSettlement = await ScopedBypass
            .TenantWide(ctx.Db.EmployeeFinalSettlements, ctx.TenantId, PayrollScope)
            .Where(s => ids.Contains(s.EmployeeId))
            .GroupBy(s => s.EmployeeId)
            .Select(g => new { EmployeeId = g.Key, Latest = g.Max(s => s.LastWorkingDay) })
            .ToDictionaryAsync(x => x.EmployeeId, x => x.Latest, ct);

        var candidates = new List<RetentionCandidate>(expired.Count);
        foreach (var e in expired)
        {
            DateTime? evidence = null;
            string? evidenceKind = null;
            if (lastPayslip.TryGetValue(e.Id, out var payslipAt))
            {
                evidence = payslipAt;
                evidenceKind = "payslip";
            }
            if (lastSettlement.TryGetValue(e.Id, out var settledOn))
            {
                var settledAt = settledOn.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
                if (evidence is null || settledAt > evidence)
                {
                    evidence = settledAt;
                    evidenceKind = "final settlement";
                }
            }

            var floor = evidence?.AddYears(_options.StatutoryPayrollRetentionYears);
            if (floor is { } f && f > nowUtc)
            {
                candidates.Add(new RetentionCandidate(
                    ItemKey(e.Id), nameof(Employee), e.Id.ToString(),
                    RetentionDispositions.Retain,
                    $"RETAINED under statute. The erasure deadline ({e.RetentionUntilUtc:yyyy-MM-dd}) has "
                    + $"passed, but the employee's most recent payroll evidence ({evidenceKind}, "
                    + $"{evidence:yyyy-MM-dd}) is still inside the {_options.StatutoryPayrollRetentionYears}-year "
                    + $"payroll retention floor, which does not expire until {f:yyyy-MM-dd}. Saudi labour and "
                    + "tax retention obligations attach to the payroll record and outlast the data subject's "
                    + "erasure interest; the conflict is resolved in favour of keeping the record.",
                    e.RetentionUntilUtc,
                    new { statutoryFloorUntilUtc = f, evidenceKind, evidenceAtUtc = evidence }));
                continue;
            }

            candidates.Add(new RetentionCandidate(
                ItemKey(e.Id), nameof(Employee), e.Id.ToString(),
                RetentionDispositions.Anonymise,
                $"Erasure deadline {e.RetentionUntilUtc:yyyy-MM-dd} elapsed and no payroll record remains "
                + $"inside the {_options.StatutoryPayrollRetentionYears}-year statutory floor"
                + (evidence is null
                    ? " (no payroll evidence of any kind exists for this employee)."
                    : $" (most recent evidence: {evidenceKind}, {evidence:yyyy-MM-dd}).")
                + " Identifying columns are cleared in place; the row and its ~130 unenforced dependent "
                + "references are kept so no payroll, GL or WPS record is orphaned.",
                e.RetentionUntilUtc,
                new { deletedAtUtc = e.DeletedAtUtc, evidenceKind, evidenceAtUtc = evidence }));
        }
        return candidates;
    }

    public async Task<object?> ApplyAsync(
        JobExecutionContext ctx, RetentionCandidate candidate, DateTime nowUtc, CancellationToken ct)
    {
        var id = int.Parse(candidate.EntityId);

        // Tracked, not ExecuteUpdate: ~50 columns, and the row must be re-read inside the item's own
        // transaction so a retry re-derives everything from the current state rather than from a plan
        // built before the fault.
        var employee = await Employees(ctx).FirstOrDefaultAsync(e => e.Id == id, ct);
        if (employee is null || employee.RedactedAtUtc is not null)
            // Idempotent under retry: already gone, or already anonymised by the attempt whose commit
            // acknowledgement was lost. Nothing to do and nothing to correct.
            return new { alreadyAnonymised = true };

        var residual = await CountResidualAsync(ctx, id, ct);
        var cleared = Anonymise(employee, nowUtc);
        return new { clearedColumns = cleared, residual };
    }

    /// <summary>
    /// Clears every directly identifying column on the row, in place. Returns the column names cleared so
    /// the audit row records exactly what was destroyed.
    ///
    /// <para>WHAT IS DELIBERATELY KEPT: joining/leaving dates, contract type, grade, cost centre,
    /// department, nationality and Saudi/non-Saudi. Once the identifiers are gone these no longer
    /// identify anybody, and Nitaqat/Saudisation ratios and headcount history are computed from them —
    /// destroying them would break a statutory return to protect nothing.</para>
    ///
    /// <para><c>EmployeeCode</c> cannot simply be blanked: it is <c>IsRequired()</c> and carries a unique
    /// index on <c>(TenantId, EmployeeCode)</c>, so blanking two employees would collide. It becomes
    /// <c>ANON-{id}</c>, which is unique by construction and carries no information the row's own primary
    /// key does not already carry.</para>
    /// </summary>
    public static IReadOnlyList<string> Anonymise(Employee e, DateTime nowUtc)
    {
        var cleared = new List<string>();

        void Clear(string name, Func<string> get, Action set)
        {
            if (!string.IsNullOrEmpty(get())) cleared.Add(name);
            set();
        }

        // Identity and contact.
        e.EmployeeCode = $"ANON-{e.Id}";
        cleared.Add(nameof(Employee.EmployeeCode));
        Clear(nameof(e.FullName), () => e.FullName, () => e.FullName = Cleared);
        Clear(nameof(e.EnglishName), () => e.EnglishName, () => e.EnglishName = string.Empty);
        Clear(nameof(e.ArabicName), () => e.ArabicName, () => e.ArabicName = string.Empty);
        Clear(nameof(e.PreferredName), () => e.PreferredName, () => e.PreferredName = string.Empty);
        Clear(nameof(e.PersonalEmail), () => e.PersonalEmail, () => e.PersonalEmail = string.Empty);
        Clear(nameof(e.WorkEmail), () => e.WorkEmail, () => e.WorkEmail = string.Empty);
        Clear(nameof(e.Phone), () => e.Phone, () => e.Phone = string.Empty);
        Clear(nameof(e.EmergencyContactName), () => e.EmergencyContactName, () => e.EmergencyContactName = string.Empty);
        Clear(nameof(e.EmergencyContactPhone), () => e.EmergencyContactPhone, () => e.EmergencyContactPhone = string.Empty);
        Clear(nameof(e.ProfilePhotoUrl), () => e.ProfilePhotoUrl, () => e.ProfilePhotoUrl = string.Empty);
        Clear(nameof(e.ProfilePhotoStorageKey), () => e.ProfilePhotoStorageKey, () => e.ProfilePhotoStorageKey = string.Empty);

        // Demographics that identify (nationality is kept — see the remarks).
        if (e.DateOfBirth is not null) cleared.Add(nameof(e.DateOfBirth));
        e.DateOfBirth = null;
        Clear(nameof(e.Gender), () => e.Gender, () => e.Gender = string.Empty);
        Clear(nameof(e.MaritalStatus), () => e.MaritalStatus, () => e.MaritalStatus = string.Empty);

        // Government identifiers — the PDPL-critical cluster. Every one of these is separately indexed
        // for duplicate detection, so leaving any of them populated leaves the value in an index too.
        Clear(nameof(e.PassportNumber), () => e.PassportNumber, () => e.PassportNumber = string.Empty);
        Clear(nameof(e.VisaNumber), () => e.VisaNumber, () => e.VisaNumber = string.Empty);
        Clear(nameof(e.VisaFileNumber), () => e.VisaFileNumber, () => e.VisaFileNumber = string.Empty);
        Clear(nameof(e.IqamaNumber), () => e.IqamaNumber, () => e.IqamaNumber = string.Empty);
        Clear(nameof(e.EmiratesId), () => e.EmiratesId, () => e.EmiratesId = string.Empty);
        Clear(nameof(e.Qid), () => e.Qid, () => e.Qid = string.Empty);
        Clear(nameof(e.CivilId), () => e.CivilId, () => e.CivilId = string.Empty);
        Clear(nameof(e.ResidencyNumber), () => e.ResidencyNumber, () => e.ResidencyNumber = string.Empty);
        Clear(nameof(e.MuqeemNumber), () => e.MuqeemNumber, () => e.MuqeemNumber = string.Empty);
        Clear(nameof(e.LaborCardNumber), () => e.LaborCardNumber, () => e.LaborCardNumber = string.Empty);
        Clear(nameof(e.WorkPermitNumber), () => e.WorkPermitNumber, () => e.WorkPermitNumber = string.Empty);
        Clear(nameof(e.IdNumber), () => e.IdNumber, () => e.IdNumber = string.Empty);
        Clear(nameof(e.GosiReference), () => e.GosiReference, () => e.GosiReference = string.Empty);
        Clear(nameof(e.QiwaContractNumber), () => e.QiwaContractNumber, () => e.QiwaContractNumber = string.Empty);
        Clear(nameof(e.QiwaEmployeeReference), () => e.QiwaEmployeeReference, () => e.QiwaEmployeeReference = string.Empty);
        Clear(nameof(e.WorkPermitReference), () => e.WorkPermitReference, () => e.WorkPermitReference = string.Empty);
        Clear(nameof(e.ContractReference), () => e.ContractReference, () => e.ContractReference = string.Empty);
        Clear(nameof(e.SponsorName), () => e.SponsorName, () => e.SponsorName = string.Empty);

        // Bank details — and the second copy on PayrollPaymentRecord.Iban / SIFFileRecord.Iban is
        // counted, not cleared, by this rule. See the class remarks.
        Clear(nameof(e.BankName), () => e.BankName, () => e.BankName = string.Empty);
        Clear(nameof(e.BankIban), () => e.BankIban, () => e.BankIban = string.Empty);
        Clear(nameof(e.WpsBankDetails), () => e.WpsBankDetails, () => e.WpsBankDetails = string.Empty);

        // Special-category and free-text narrative.
        Clear(nameof(e.MedicalInformation), () => e.MedicalInformation, () => e.MedicalInformation = string.Empty);
        Clear(nameof(e.DisciplinaryRecords), () => e.DisciplinaryRecords, () => e.DisciplinaryRecords = string.Empty);
        Clear(nameof(e.TerminationReason), () => e.TerminationReason, () => e.TerminationReason = string.Empty);

        // The link back to the login, which carries the person's email in another table.
        if (e.UserAccountId is not null) cleared.Add(nameof(e.UserAccountId));
        e.UserAccountId = null;

        e.PrivacyStatus = AnonymisedStatus;
        e.RedactedAtUtc = nowUtc;
        e.UpdatedAtUtc = nowUtc;
        return cleared;
    }

    /// <summary>
    /// The identifiers this rule knowingly leaves behind, counted so the audit row is honest about the
    /// residual instead of implying the person is gone.
    /// </summary>
    private static async Task<object> CountResidualAsync(JobExecutionContext ctx, int employeeId, CancellationToken ct)
    {
        var snapshots = await ScopedBypass
            .TenantWide(ctx.Db.EmployeeHistories, ctx.TenantId, PayrollScope)
            .CountAsync(h => h.EmployeeId == employeeId, ct);
        var documents = await ScopedBypass
            .TenantWide(ctx.Db.EmployeeDocuments, ctx.TenantId, PayrollScope)
            .CountAsync(d => d.EmployeeId == employeeId, ct);
        var settlements = await ScopedBypass
            .TenantWide(ctx.Db.EmployeeFinalSettlements, ctx.TenantId, PayrollScope)
            .CountAsync(s => s.EmployeeId == employeeId, ct);
        return new
        {
            note = "NOT cleared by this rule — denormalised identifiers and object-store blobs remain.",
            employeeHistorySnapshots = snapshots,
            employeeDocuments = documents,
            finalSettlementsWithDenormalisedName = settlements,
        };
    }

    public static string ItemKey(int employeeId) => $"{RetentionRuleKeys.EmployeeRetentionExpired}:employee:{employeeId}";

    private static IQueryable<Employee> Employees(JobExecutionContext ctx) =>
        ScopedBypass.NullableTenantWide(ctx.Db.Employees, ctx.TenantId, EmployeeScope);
}
