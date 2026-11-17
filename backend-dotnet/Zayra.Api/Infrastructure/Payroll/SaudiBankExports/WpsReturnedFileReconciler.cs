using System.Globalization;
using Zayra.Api.Application.Common;
using Zayra.Api.Infrastructure.Finance;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Payroll.SaudiBankExports;

/// <summary>One payment record of OUR payroll run, as the reconciler needs it. <see cref="MolId"/> is the wage-file government ID.</summary>
public sealed record WpsExpectedLine(
    int EmployeeId, string EmployeeCode, string? MolId, string? Iban, decimal Amount, bool Cancelled = false);

public static class WpsReconciliationClass
{
    public const string Matched = "matched";
    public const string AmountMismatch = "amount_mismatch";
    public const string AccountMismatch = "account_mismatch";
    public const string UnknownEmployee = "unknown_employee";
    public const string MissingFromFile = "missing_from_file";
    public const string Rejected = "rejected";
    public const string StatusNotRecognised = "status_not_recognised";
    public const string InvalidRow = "invalid_row";
    public const string Ambiguous = "ambiguous_employee";
}

/// <summary>
/// One employee's outcome. IBAN and government ID are masked (last 4 only); amounts are shown because they are the
/// point of the comparison. English and Arabic reasons.
/// </summary>
public sealed record WpsReconciledLine(
    string Classification, int? EmployeeId, string? EmployeeCode, int? FileLine, string? MaskedMolId, string? MaskedIban,
    decimal? ExpectedAmount, decimal? FileAmount, string? Outcome, string? ReasonCode, string ReasonEn, string ReasonAr);

public sealed record WpsReconciliation(IReadOnlyList<WpsReconciledLine> Lines)
{
    public int Count(string classification) => Lines.Count(l => l.Classification == classification);
}

/// <summary>
/// Matches each row of a bank-returned file to OUR payment record by employee (the wage-file government ID, [MOL-ID])
/// and amount. PURE: it classifies and explains; it changes nothing and applies nothing.
///
/// <para>Priority for a matched employee: a returned line is <see cref="WpsReconciliationClass.Rejected"/> before anything
/// else (the money did not move, so the other figures no longer matter); then a different account, then a different
/// amount, then a status word we do not recognise; otherwise <see cref="WpsReconciliationClass.Matched"/>.</para>
///
/// <para>The per-line status words are NOT proven by the repo (TRN-STATUS is named only in a research note), so the
/// existing <see cref="BankOutcomeVocabulary"/> decides: an unrecognised word is never guessed into paid or rejected.</para>
/// </summary>
public static class WpsReturnedFileReconciler
{
    public static WpsReconciliation Reconcile(WpsReturnedFileReadResult file, IReadOnlyList<WpsExpectedLine> expected)
    {
        var byMol = new Dictionary<string, List<WpsExpectedLine>>(StringComparer.Ordinal);
        foreach (var item in expected.Where(p => !string.IsNullOrWhiteSpace(p.MolId)))
        {
            if (!byMol.TryGetValue(item.MolId!, out var list)) byMol[item.MolId!] = list = new List<WpsExpectedLine>();
            list.Add(item);
        }

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
                var codes = string.Join(", ", row.BlockingCodes);
                lines.Add(new(WpsReconciliationClass.InvalidRow, one?.EmployeeId, one?.EmployeeCode, row.Line, maskedId, maskedIban,
                    one?.Amount, row.Net, null, row.BlockingCodes.FirstOrDefault(),
                    $"Line {row.Line} of the file failed the format checks ({codes}), so it was not used and this salary is not confirmed.",
                    $"فشل السطر {row.Line} من الملف في فحوص التنسيق ({codes})، لذلك لم يُستخدم وهذا الراتب غير مؤكد."));
                continue;
            }

            if (hits is null)
            {
                lines.Add(new(WpsReconciliationClass.UnknownEmployee, null, null, row.Line, maskedId, maskedIban, null, row.Net, null, null,
                    $"The file has a salary line for a government ID ending {Last4(row.MolId)} that is not in this payment batch. It was not applied.",
                    $"يحتوي الملف على سطر راتب لهوية تنتهي بـ {Last4(row.MolId)} غير موجودة في دفعة الدفع هذه. لم يتم تطبيقه."));
                continue;
            }

            if (hits.Count > 1)
            {
                lines.Add(new(WpsReconciliationClass.Ambiguous, null, null, row.Line, maskedId, maskedIban, null, row.Net, null, null,
                    $"More than one payment record in this batch has the government ID ending {Last4(row.MolId)}, so line {row.Line} cannot be matched to one employee. It was not applied.",
                    $"أكثر من سجل دفع في هذه الدفعة له هوية تنتهي بـ {Last4(row.MolId)}، لذلك لا يمكن ربط السطر {row.Line} بموظف واحد. لم يتم تطبيقه."));
                continue;
            }

            var e = hits[0];
            var outcome = BankOutcomeVocabulary.Canonicalise(row.TrnStatus);
            var statusRaw = WpsReturnedFileValidator.Safe(row.TrnStatus);
            WpsReconciledLine Line(string cls, string? reasonCode, string en, string ar) =>
                new(cls, e.EmployeeId, e.EmployeeCode, row.Line, maskedId, maskedIban, e.Amount, row.Net, outcome, reasonCode, en, ar);

            if (outcome == BankConfirmationOutcomes.Returned)
            {
                lines.Add(Line(WpsReconciliationClass.Rejected, statusRaw,
                    $"The bank marked this line {statusRaw}, so the salary was not paid. The list of reason codes is not in the specification we hold: ask the bank what {statusRaw} means.",
                    $"وضع البنك على هذا السطر الحالة {statusRaw}، لذلك لم يُدفع الراتب. قائمة رموز الأسباب غير موجودة في المواصفة التي نملكها: اسأل البنك عن معنى {statusRaw}."));
            }
            else if (!string.IsNullOrEmpty(e.Iban) && !SameAccount(row.Iban, e.Iban))
            {
                lines.Add(Line(WpsReconciliationClass.AccountMismatch, null,
                    $"The file credits a different bank account ({maskedIban}) than the one on this payment record ({Mask(e.Iban)}). It was not applied.",
                    $"يحوّل الملف إلى حساب بنكي مختلف ({maskedIban}) عن الحساب في سجل الدفع هذا ({Mask(e.Iban)}). لم يتم تطبيقه."));
            }
            else if (row.Net is { } net && Math.Abs(net - e.Amount) > 0.005m)
            {
                lines.Add(Line(WpsReconciliationClass.AmountMismatch, null,
                    $"The file shows amount {Money(net)} but the payroll run has {Money(e.Amount)}. It is held until someone reviews the difference.",
                    $"يُظهر الملف مبلغاً قدره {Money(net)} بينما كشف الرواتب فيه {Money(e.Amount)}. يُعلّق حتى تتم مراجعة الفرق."));
            }
            else if (!string.IsNullOrWhiteSpace(row.TrnStatus) && outcome is null)
            {
                lines.Add(Line(WpsReconciliationClass.StatusNotRecognised, statusRaw,
                    $"The file gives the status {statusRaw}, which is not a status we recognise. Nothing was applied for this employee: it needs to be mapped explicitly.",
                    $"يعطي الملف الحالة {statusRaw} وهي غير معروفة لدينا. لم يتم تطبيق أي شيء لهذا الموظف: يلزم ربطها يدوياً."));
            }
            else
            {
                var confirms = outcome == BankConfirmationOutcomes.Paid;
                lines.Add(Line(WpsReconciliationClass.Matched, null,
                    "Matches the payroll run: same employee and amount." + (outcome is null
                        ? " The file carries no payment status, so this confirms the figures only, not that the money arrived."
                        : confirms ? " The bank reports it paid." : " The bank reports it still being processed."),
                    "مطابق لكشف الرواتب: الموظف والمبلغ نفسهما." + (outcome is null
                        ? " لا يحمل الملف حالة دفع، لذلك يؤكد الأرقام فقط وليس وصول المبلغ."
                        : confirms ? " يفيد البنك بأنه دُفع." : " يفيد البنك بأنه قيد المعالجة.")));
            }
        }

        foreach (var gone in expected.Where(p => !claimed.Contains(p.EmployeeId) && !p.Cancelled))
            lines.Add(new(WpsReconciliationClass.MissingFromFile, gone.EmployeeId, gone.EmployeeCode, null, Mask(gone.MolId), Mask(gone.Iban),
                gone.Amount, null, null, null,
                "This employee's salary is in the payroll run but not in the file, so the payment is unconfirmed.",
                "راتب هذا الموظف موجود في كشف الرواتب ولكنه غير موجود في الملف، لذلك الدفع غير مؤكد."));

        return new WpsReconciliation(lines);
    }

    private static string? Mask(string? value) => string.IsNullOrWhiteSpace(value) ? null : SensitiveValueMask.MaskId(value);

    private static string Last4(string? value)
    {
        var v = (value ?? string.Empty).Trim();
        return v.Length <= 4 ? "***" : v[^4..];
    }

    private static string Money(decimal d) => d.ToString("0.00", CultureInfo.InvariantCulture);

    private static bool SameAccount(string? a, string? b) =>
        string.Equals(Normalise(a), Normalise(b), StringComparison.Ordinal);

    private static string Normalise(string? s) =>
        new string((s ?? string.Empty).Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());
}
