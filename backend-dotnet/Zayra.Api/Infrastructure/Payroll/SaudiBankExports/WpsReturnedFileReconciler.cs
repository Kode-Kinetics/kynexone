using Zayra.Api.Application.Common;
using Zayra.Api.Infrastructure.Finance;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Payroll.SaudiBankExports;

/// <summary>One payment record of OUR payroll run, as the reconciler needs it. <see cref="MolId"/> is the wage-file government ID.</summary>
public sealed record WpsExpectedLine(
    int EmployeeId, string EmployeeCode, string? MolId, string? Iban, decimal Amount, bool Cancelled = false);

public static class WpsReconciliationClass
{
    /// <summary>Matches our record and the bank reports it paid.</summary>
    public const string MatchedPaid = "matched_paid";
    /// <summary>Matches our record and the bank reports it still being processed.</summary>
    public const string MatchedPending = "matched_pending";
    /// <summary>Matches our record but the file carries no payment status: the figures agree, nothing is confirmed.</summary>
    public const string MatchedFiguresOnly = "matched_figures_only";
    /// <summary>Matches our record; the bank says it accepted the file but does not confirm the credit. Informational: no status change.</summary>
    public const string MatchedAccepted = "matched_accepted";
    public const string AmountMismatch = "amount_mismatch";
    public const string AccountMismatch = "account_mismatch";
    /// <summary>Our payment record has no IBAN, so the account cannot be checked.</summary>
    public const string AccountMissing = "account_missing";
    public const string UnknownEmployee = "unknown_employee";
    public const string MissingFromFile = "missing_from_file";
    /// <summary>The bank refused the instruction: "Not paid: rejected by bank". The bank statement shows whether any money moved.</summary>
    public const string Rejected = "rejected";
    /// <summary>The money left and came back.</summary>
    public const string Returned = "returned";
    /// <summary>ISO ACWC or a bank reason code (DUPL included) in the status field: nothing is recorded until a person checks.</summary>
    public const string HeldForPerson = "held_for_person";
    public const string StatusNotRecognised = "status_not_recognised";
    public const string InvalidRow = "invalid_row";
    public const string Ambiguous = "ambiguous_employee";
}

/// <summary>
/// The status words THIS reader accepts for a line. Their meaning comes from the shared <see cref="BankOutcomeVocabulary"/>
/// (<see cref="BankOutcomeVocabulary.Read"/>), so one word means one thing everywhere: Paid, Returned ("the money came back")
/// or Rejected ("not paid: rejected by bank"). The reader stays STRICTER than the shared vocabulary: it gives an outcome only
/// for the words in <see cref="OutcomeWords"/> (its Pending list is narrower), "COMPLETE" and "ACCEPTED" are read as
/// accepted-only, and no single letters, no "OK"/"YES", ISO ACSP pending, never paid. The per-line status tag itself is
/// unverified (see the rule catalogue), so anything not listed here confirms nothing.
///
/// <para>The status is classified on the RAW value as written. A value with any character outside plain printable ASCII
/// (zero-width, bidi, non-breaking space, a look-alike letter) or starting with a formula character (= + - @) is not
/// <see cref="IsPlain"/>: the reader refuses that row and nothing is ever stripped first and then classified.</para>
///
/// <para>Held for a person, never an outcome: ISO ACWC (accepted with change) and an ISO reason code written in the status
/// field (<see cref="BankStatusReasonCatalogue"/>), including DUPL, which may mean the salary WAS paid by an earlier payment.</para>
/// </summary>
public static class WpsReturnedFileStatus
{
    // Paid, Returned and Rejected words: the shared vocabulary's own lists, read through BankOutcomeVocabulary.Read.
    private static readonly HashSet<string> Decisive = new(StringComparer.OrdinalIgnoreCase)
    {
        "PAID", "SETTLED", "CREDITED", "ACSC", "ACCC",
        "RETURN", "RETURNED", "RTRN", "BOUNCE", "BOUNCED",
        "RJCT", "REJECT", "REJECTED", "DECLINED", "FAIL", "FAILED", "FAILURE", "CANCELLED_BY_BANK",
    };
    // Narrower than the shared Pending list (no SUBMITTED, QUEUED, AWAITING, INPROGRESS).
    private static readonly HashSet<string> Pending = new(StringComparer.OrdinalIgnoreCase)
        { "PENDING", "PDNG", "PROCESSING", "IN_PROGRESS", "ACSP", "ACWP", "ACCP", "ACTC", "RCVD", "RECEIVED" };
    private static readonly HashSet<string> AcceptedOnly = new(StringComparer.OrdinalIgnoreCase)
        { "SUCCESS", "SUCCESSFUL", "SUCCEEDED", "COMPLETED", "COMPLETE", "EXECUTED", "ACCEPTED" };

    /// <summary>Every word this reader gives an outcome for. Each reads exactly as the shared vocabulary reads it (pinned by a test).</summary>
    public static IReadOnlyCollection<string> OutcomeWords => Decisive.Concat(Pending).ToList();

    /// <summary>
    /// True when the raw status is safe to classify as written: printable ASCII only, and not starting with = + - @. Leading and
    /// trailing ASCII spaces are allowed (they hide nothing). Null or empty is plain (no status).
    /// </summary>
    public static bool IsPlain(string? raw)
    {
        if (string.IsNullOrEmpty(raw)) return true;
        foreach (var c in raw)
            if (c is < ' ' or > '~') return false;
        var token = raw.Trim(' ');
        return token.Length == 0 || token[0] is not ('=' or '+' or '-' or '@');
    }

    private static string? Token(string? raw)
    {
        if (!IsPlain(raw)) return null;
        var token = (raw ?? string.Empty).Trim(' ').Replace(' ', '_');
        return token.Length == 0 ? null : token;
    }

    /// <summary>True for ISO ACSC/ACCC: the bank's own settlement is complete, which does not by itself prove the employee was credited.</summary>
    public static bool IsSettledByBank(string? status) =>
        Token(status) is { } t && BankOutcomeVocabulary.Read(t).Reason == BankOutcomeReasons.SettledByBank;

    /// <summary>True for a word that means "the bank accepted the file" without confirming the credit.</summary>
    public static bool IsAcceptedOnly(string? status) => Token(status) is { } t && AcceptedOnly.Contains(t);

    /// <summary>
    /// "Paid", "Returned", "Rejected" or "Pending" (<see cref="BankConfirmationOutcomes"/>), or null for any other word and for any
    /// value that is not <see cref="IsPlain"/>. Classified on the raw value; nothing is cleaned first.
    /// </summary>
    public static string? Classify(string? status)
    {
        if (Token(status) is not { } token) return null;
        if (!Decisive.Contains(token) && !Pending.Contains(token)) return null;
        return BankOutcomeVocabulary.Read(token).Outcome;
    }

    /// <summary>
    /// Why a line is held for a person, or null. ISO ACWC (accepted with a change) and an ISO reason code in the status field
    /// (DUPL included) confirm nothing: <paramref name="reasonCode"/> is the catalogue code, or null for ACWC.
    /// </summary>
    public static bool IsHeldForPerson(string? status, out string? reasonCode)
    {
        reasonCode = null;
        if (Token(status) is not { } token) return false;
        if (BankOutcomeVocabulary.Read(token).Reason == BankOutcomeReasons.AcceptedWithChangeHeld) return true;
        if (!BankStatusReasonCatalogue.IsKnown(token)) return false;
        reasonCode = BankStatusReasonCatalogue.Describe(token).Code;
        return true;
    }
}

/// <summary>
/// One employee's outcome. IBAN and government ID are masked (last 4 only); amounts are structured fields, never in the text.
/// English and Arabic reasons are separate fields.
/// </summary>
public sealed record WpsReconciledLine(
    string Classification, int? EmployeeId, string? EmployeeCode, int? FileLine, string? MaskedMolId, string? MaskedIban,
    decimal? ExpectedAmount, decimal? FileAmount, string? Outcome, string? ReasonCode, string ReasonEn, string ReasonAr);

public sealed record WpsReconciliation(IReadOnlyList<WpsReconciledLine> Lines)
{
    public int Count(string classification) => Lines.Count(l => l.Classification == classification);
    public string NoticeEn => WpsReturnedFileReconciler.ScopeNoticeEn;
    public string NoticeAr => WpsReturnedFileReconciler.ScopeNoticeAr;
}

/// <summary>
/// Matches each row of a bank-returned file to OUR payment record by employee (the wage-file government ID, [MOL-ID]),
/// account and amount. PURE: it classifies and explains; it changes nothing and applies nothing.
///
/// <para>Order for a matched employee: our record has no IBAN (cannot be checked); the file names a different account; the
/// bank rejected the line (not paid) or returned it (the money came back); the amount differs; accepted-only; held for a person
/// (ACWC or a bank reason code, DUPL included); a status word outside <see cref="WpsReturnedFileStatus"/>; otherwise one of
/// matched paid / pending / figures-only. A rejected or returned line must pass the account check too.</para>
/// </summary>
public static class WpsReturnedFileReconciler
{
    public const string ScopeNoticeEn =
        "This check compares the file your bank returned with your payroll run. It does not show whether Mudad accepted it or whether you meet wage-protection requirements.";
    public const string ScopeNoticeAr =
        "يقارن هذا الفحص الملف الذي أعاده البنك بمسير الرواتب لديك. ولا يبيّن ما إذا كانت منصة مدد قد قبلته أو ما إذا كنت تستوفي متطلبات حماية الأجور.";

    // Latin codes and masked values inside Arabic text are isolated so they keep their order in right-to-left text.
    private static string Iso(string? value) => "\u2066" + value + "\u2069";

    public static WpsReconciliation Reconcile(WpsReturnedFileReadResult file, IReadOnlyList<WpsExpectedLine> expected)
    {
        var byMol = new Dictionary<string, List<WpsExpectedLine>>(StringComparer.Ordinal);
        foreach (var item in expected.Where(p => !string.IsNullOrWhiteSpace(p.MolId)))
        {
            if (!byMol.TryGetValue(item.MolId!, out var list)) byMol[item.MolId!] = list = new List<WpsExpectedLine>();
            list.Add(item);
        }

        // The first error the reader reported against each line, for the plain-language reason of an invalid line.
        var firstError = new Dictionary<int, WpsReturnedFileFinding>();
        foreach (var f in file.Findings)
            if (f.Severity == WpsFindingSeverity.Error && f.Line is int ln) firstError.TryAdd(ln, f);

        var claimed = new HashSet<int>();
        var lines = new List<WpsReconciledLine>(file.Rows.Count + expected.Count);

        foreach (var row in file.Rows)
        {
            var hits = !string.IsNullOrEmpty(row.MolId) && byMol.TryGetValue(row.MolId!, out var h) ? h : null;
            if (hits is not null) foreach (var x in hits) claimed.Add(x.EmployeeId);
            var maskedId = Mask(row.MolId);
            var maskedIban = Mask(row.Iban);

            if (row.Blocked)
            {
                var one = hits is { Count: 1 } ? hits[0] : null;
                firstError.TryGetValue(row.Line, out var why);
                lines.Add(new(WpsReconciliationClass.InvalidRow, one?.EmployeeId, one?.EmployeeCode, row.Line, maskedId, maskedIban,
                    one?.Amount, row.Net, null, row.BlockingCodes.FirstOrDefault(),
                    $"This line could not be used, so this salary is not confirmed. {why?.Message}",
                    $"تعذّر استخدام هذا السطر، لذلك فإن هذا الراتب غير مؤكد. {why?.MessageAr}"));
                continue;
            }

            if (hits is null)
            {
                lines.Add(new(WpsReconciliationClass.UnknownEmployee, null, null, row.Line, maskedId, maskedIban, null, row.Net, null, null,
                    $"The file has a salary line for an ID ending {Last4(row.MolId)} that is not in this payment batch. It was not applied. Check the ID; if it is not a typo, tell Finance, because a payment may have gone to someone outside this payroll.",
                    $"يحتوي الملف على سطر راتب لرقم الهوية أو الإقامة المنتهي بـ {Iso(Last4(row.MolId))} وهو غير موجود في دفعة الرواتب هذه. لم يُطبَّق. تحقق من الرقم؛ وإن لم يكن خطأً في الكتابة فأبلغ الإدارة المالية لاحتمال تحويل مبلغ لشخص خارج مسير الرواتب."));
                continue;
            }

            if (hits.Count > 1)
            {
                lines.Add(new(WpsReconciliationClass.Ambiguous, null, null, row.Line, maskedId, maskedIban, null, row.Net, null, null,
                    $"More than one payment record in this batch has the ID ending {Last4(row.MolId)}, so this line cannot be matched to one employee. It was not applied.",
                    $"أكثر من سجل دفع في هذه الدفعة له رقم هوية أو إقامة ينتهي بـ {Iso(Last4(row.MolId))}، لذلك لا يمكن ربط هذا السطر بموظف واحد. لم يُطبَّق."));
                continue;
            }

            var e = hits[0];
            var outcome = WpsReturnedFileStatus.Classify(row.TrnStatus);
            var statusRaw = WpsReturnedFileValidator.Safe(row.TrnStatus);
            WpsReconciledLine Line(string cls, string? reasonCode, string en, string ar) =>
                new(cls, e.EmployeeId, e.EmployeeCode, row.Line, maskedId, maskedIban, e.Amount, row.Net, outcome, reasonCode, en, ar);
            WpsReconciledLine HeldForReason(string code)
            {
                var reason = BankStatusReasonCatalogue.Describe(code);
                // A duplicate usually means the EARLIER payment went through: never "not paid", never "owed".
                return code == "DUPL"
                    ? Line(WpsReconciliationClass.HeldForPerson, code,
                        $"{reason.English} This salary may already have been paid by the earlier payment. Check on the bank statement whether the earlier payment arrived before doing anything else. Nothing was applied for this employee.",
                        $"{reason.Arabic} قد يكون هذا الراتب قد صُرف بالعملية السابقة. تحقق من كشف حساب البنك هل وصلت العملية السابقة قبل أي إجراء آخر. لم يُطبَّق أي شيء لهذا الموظف.")
                    : Line(WpsReconciliationClass.HeldForPerson, code,
                        $"The status field holds the bank reason code {code}: {reason.English} It does not say whether the salary was paid. Check the bank statement. Nothing was applied for this employee.",
                        $"يحمل حقل الحالة رمز السبب من البنك {Iso(code)}: {reason.Arabic} ولا يبيّن ما إذا كان الراتب قد صُرف. راجع كشف حساب البنك. لم يُطبَّق أي شيء لهذا الموظف.");
            }

            if (string.IsNullOrEmpty(e.Iban))
            {
                lines.Add(Line(WpsReconciliationClass.AccountMissing, null,
                    "This payment record has no bank account (IBAN) on file, so the line cannot be checked against it. It was not applied. Ask HR to complete the employee's bank details.",
                    "ليس لسجل الدفع هذا حساب بنكي (آيبان) مسجل، لذلك لا يمكن التحقق من السطر. لم يُطبَّق. يرجى مطالبة الموارد البشرية باستكمال بيانات البنك للموظف."));
            }
            else if (!SameAccount(row.Iban, e.Iban))
            {
                lines.Add(Line(WpsReconciliationClass.AccountMismatch, null,
                    $"The file credits a different bank account ({maskedIban}) than the one on this payment record ({Mask(e.Iban)}). The salary may have been sent to the account in the file. Confirm with the bank, then update the employee's IBAN or re-issue. It was not applied.",
                    $"يحوّل الملف إلى حساب بنكي مختلف ({Iso(maskedIban)}) عن الحساب في سجل الدفع هذا ({Iso(Mask(e.Iban))}). قد يكون الراتب أُرسل إلى الحساب المذكور في الملف. تأكد من البنك ثم حدّث آيبان الموظف أو أعد الإصدار. لم يُطبَّق."));
            }
            else if (outcome == BankConfirmationOutcomes.Rejected)
            {
                // The bank refused the instruction: not paid. The file has no reason column, so the reason is "none given".
                var label = BankOutcomeLabels.NotPaidRejectedByBank;
                var noReason = BankStatusReasonCatalogue.Describe(null);
                lines.Add(Line(WpsReconciliationClass.Rejected, null,
                    $"{label.English}. The bank reported this line as {statusRaw}. {noReason.English}. Check the bank statement to confirm no money left. If none did, this salary is still owed to the employee: pay it through a supplementary payment run after correcting any bank detail.",
                    $"{label.Arabic}. أفاد البنك بأن حالة هذا السطر {Iso(statusRaw)}. {noReason.Arabic}. راجع كشف حساب البنك للتأكد من عدم خروج أي مبلغ. فإن لم يخرج مبلغ فالراتب ما زال مستحقًا للموظف: ادفعه عبر مسير دفع إضافي بعد تصحيح أي بيان بنكي."));
            }
            else if (outcome == BankConfirmationOutcomes.Returned)
            {
                var label = BankOutcomeLabels.ReturnedMoneyCameBack;
                var noReason = BankStatusReasonCatalogue.Describe(null);
                lines.Add(Line(WpsReconciliationClass.Returned, null,
                    $"{label.English}. The bank reported this line as {statusRaw}. {noReason.English}. Check the bank statement, correct the employee's bank details if needed, then pay again.",
                    $"{label.Arabic}. أفاد البنك بأن حالة هذا السطر {Iso(statusRaw)}. {noReason.Arabic}. راجع كشف حساب البنك وصحّح بيانات بنك الموظف عند الحاجة ثم أعد الصرف."));
            }
            else if (row.Net is { } net && Math.Abs(net - e.Amount) > 0.005m)
            {
                var lower = net < e.Amount;
                lines.Add(Line(WpsReconciliationClass.AmountMismatch, null,
                    lower
                        ? "The amount in the file is lower than the payroll run amount for this employee. The employee may have received less than they are owed; the difference may still be owed. It is held until someone reviews it."
                        : "The amount in the file is higher than the payroll run amount for this employee. The employee may have been overpaid. Do not recover it from pay until Finance and HR decide how. It is held until someone reviews it.",
                    lower
                        ? "المبلغ في الملف أقل من مبلغ مسير الرواتب لهذا الموظف. قد يكون الموظف استلم أقل من مستحقه وقد يبقى الفرق مستحقًا له. يبقى السطر معلقًا حتى تتم مراجعة الفرق."
                        : "المبلغ في الملف أكبر من مبلغ مسير الرواتب لهذا الموظف. قد يكون الموظف استلم أكثر من مستحقه. لا تسترد الفرق من الراتب قبل أن تقرر الإدارة المالية والموارد البشرية الآلية. يبقى السطر معلقًا حتى تتم مراجعة الفرق."));
            }
            else if (WpsReturnedFileStatus.IsAcceptedOnly(row.TrnStatus))
            {
                lines.Add(Line(WpsReconciliationClass.MatchedAccepted, statusRaw,
                    $"The bank reports {statusRaw}, which means it accepted the file but does not confirm that the money was credited. No status was changed. Check the bank statement.",
                    $"أفاد البنك بالحالة {Iso(statusRaw)} ومعناها أنه قبل الملف دون تأكيد قيد المبلغ. لم تتغير أي حالة. راجع كشف حساب البنك."));
            }
            else if (WpsReturnedFileStatus.IsHeldForPerson(row.TrnStatus, out var heldCode))
            {
                lines.Add(heldCode is null
                    ? Line(WpsReconciliationClass.HeldForPerson, statusRaw,
                        $"{BankOutcomeLabels.HeldAcceptedWithChange.English}. Nothing was applied for this employee.",
                        $"{BankOutcomeLabels.HeldAcceptedWithChange.Arabic}. لم يُطبَّق أي شيء لهذا الموظف.")
                    : HeldForReason(heldCode));
            }
            else if (!string.IsNullOrWhiteSpace(row.TrnStatus) && outcome is null)
            {
                lines.Add(Line(WpsReconciliationClass.StatusNotRecognised, statusRaw,
                    $"The file gives the status {statusRaw}, which is not a status we recognise. Nothing was applied for this employee: it needs to be mapped explicitly.",
                    $"يعطي الملف الحالة {Iso(statusRaw)} وهي غير معروفة لدينا. لم يُطبَّق أي شيء لهذا الموظف: يلزم ربطها يدوياً."));
            }
            else if (outcome == BankConfirmationOutcomes.Paid && WpsReturnedFileStatus.IsSettledByBank(row.TrnStatus))
            {
                // ISO ACSC/ACCC: the bank's settlement ran. That is not proof the employee was credited (CTO ruling 8.4.4).
                lines.Add(Line(WpsReconciliationClass.MatchedPaid, null,
                    "Matches the payroll run (same employee, account and amount). The bank reports this payment as settled by the bank. That does not by itself prove the employee received it: confirm on the bank statement.",
                    "مطابق لمسير الرواتب (الموظف والحساب والمبلغ متطابقة). أفاد البنك بأن الدفعة تمت تسويتها لدى البنك، وهذا وحده لا يثبت وصول المبلغ إلى الموظف: تأكد من كشف حساب البنك."));
            }
            else if (outcome == BankConfirmationOutcomes.Paid)
            {
                lines.Add(Line(WpsReconciliationClass.MatchedPaid, null,
                    "Matches the payroll run (same employee, account and amount) and the bank reports it paid.",
                    "مطابق لمسير الرواتب (الموظف والحساب والمبلغ متطابقة) ويفيد البنك بأنه دُفع."));
            }
            else if (outcome == BankConfirmationOutcomes.Pending)
            {
                lines.Add(Line(WpsReconciliationClass.MatchedPending, null,
                    "Matches the payroll run (same employee, account and amount) and the bank reports it still being processed.",
                    "مطابق لمسير الرواتب (الموظف والحساب والمبلغ متطابقة) ويفيد البنك بأنه قيد المعالجة."));
            }
            else
            {
                lines.Add(Line(WpsReconciliationClass.MatchedFiguresOnly, null,
                    "Matches the payroll run, but the file carries no payment status, so this confirms the figures only, not that the money arrived.",
                    "مطابق لمسير الرواتب، لكن الملف لا يحمل حالة دفع، لذلك يؤكد الأرقام فقط وليس وصول المبلغ."));
            }
        }

        foreach (var gone in expected.Where(p => !claimed.Contains(p.EmployeeId) && !p.Cancelled))
        {
            var noId = string.IsNullOrWhiteSpace(gone.MolId);
            lines.Add(new(WpsReconciliationClass.MissingFromFile, gone.EmployeeId, gone.EmployeeCode, null, Mask(gone.MolId), Mask(gone.Iban),
                gone.Amount, null, null, null,
                noId
                    ? "This employee has no ID on record, so no line of the file can be matched to this salary. Do not treat it as paid. Ask HR to add the ID, then check the bank statement."
                    : "This employee's salary is in the payroll run but not in the file. Do not treat it as paid. Check the bank statement and ask the bank.",
                noId
                    ? "ليس لهذا الموظف رقم هوية أو إقامة مسجل، لذلك لا يمكن ربط أي سطر في الملف بهذا الراتب. لا تعتبره مدفوعاً. يرجى مطالبة الموارد البشرية بإضافة الرقم ثم راجع كشف حساب البنك."
                    : "راتب هذا الموظف موجود في مسير الرواتب ولكنه غير موجود في الملف. لا تعتبره مدفوعاً. راجع كشف حساب البنك واسأل البنك."));
        }

        return new WpsReconciliation(lines);
    }

    private static string? Mask(string? value) => string.IsNullOrWhiteSpace(value) ? null : SensitiveValueMask.MaskId(value);

    private static string Last4(string? value)
    {
        var v = (value ?? string.Empty).Trim();
        return v.Length <= 4 ? "***" : v[^4..];
    }

    private static bool SameAccount(string? a, string? b) =>
        string.Equals(Normalise(a), Normalise(b), StringComparison.Ordinal);

    private static string Normalise(string? s) =>
        new string((s ?? string.Empty).Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());
}
