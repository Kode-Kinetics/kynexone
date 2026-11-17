using Microsoft.Extensions.Configuration;
using Zayra.Api.Infrastructure.Payroll.SaudiBankExports;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Finance;

/// <summary>
/// What the import knows about the payment batch, loaded by the service (tenant- and company-scoped) and handed to a
/// parser that asks for it. It is read-only data: a parser cannot change a record through it.
/// </summary>
public sealed record BankConfirmationBatchContext(string? MolEstablishmentId, IReadOnlyList<WpsExpectedLine> Expected);

/// <summary>
/// A parser that needs the batch to read a file correctly. <see cref="BankConfirmationService"/> calls the context
/// overload when a parser implements this; every existing parser is untouched and still gets <c>Parse(content)</c>.
/// </summary>
public interface IContextualBankConfirmationParser : IBankConfirmationParser
{
    BankConfirmationParseResult Parse(string content, BankConfirmationBatchContext context);
}

/// <summary>
/// The switch for <see cref="WpsReturnedFileBankConfirmationParser"/>. OFF unless <c>Payroll:WpsReturnedFileParser:Enabled</c>
/// is true; when off the parser is not registered at all, so it is neither listed nor selectable. When on it still only
/// runs when an import explicitly asks for <c>format=wps-returned-file</c>.
/// </summary>
public static class WpsReturnedFileFeature
{
    public const string ConfigKey = "Payroll:WpsReturnedFileParser:Enabled";

    public static bool IsEnabled(IConfiguration? configuration) =>
        bool.TryParse(configuration?[ConfigKey], out var on) && on;
}

/// <summary>
/// Reads a WPS file that the employer's BANK produced and signed (the file that goes to Mudad), reconciles it against
/// the payment batch by employee and amount, and hands the existing bank-confirmation import canonical rows.
///
/// <para>It only READS. It never writes, strips or re-signs the file, and it is not a WPS submission or a certificate of
/// conformance. Anything it cannot prove is refused rather than guessed: a file-level fault (total mismatch, wrong
/// establishment, a file-level reject code) yields no rows at all; a faulty line yields no row for that line; a status word
/// outside <see cref="BankOutcomeVocabulary"/> confirms nothing. A line is only passed on when the employee matches by
/// government ID AND the account matches; the amount is passed on so the import's own mismatch hold applies.</para>
/// </summary>
public sealed class WpsReturnedFileBankConfirmationParser : IContextualBankConfirmationParser
{
    public string Key => "wps-returned-file";
    public string DisplayName => "Bank-returned WPS file (TAB-delimited, read-only)";
    public string Disclaimer =>
        "UNVERIFIED LAYOUT. Reads the file your BANK produced and signed; it never changes or re-signs it and is not a "
      + "wage-protection submission. The tab and [TAG] layout is assumed from the field names the product already cites and has "
      + "not been confirmed against a real bank file; the per-line status field is also unconfirmed. Lines are matched to payment "
      + "records by government ID and amount. A file that does not tie out (header total, establishment, FILE-REF) applies nothing.";

    /// <summary>The establishment and the employee records are needed to read this file safely, so the plain overload refuses.</summary>
    public BankConfirmationParseResult Parse(string content) =>
        new(Array.Empty<BankConfirmationRow>(), new[]
        {
            new BankConfirmationParseError(0, "context_required",
                "This format needs the payment batch to check the establishment and match employees; it cannot be read without it. "
              + "| يحتاج هذا التنسيق إلى دفعة الدفع للتحقق من المنشأة ومطابقة الموظفين؛ لا يمكن قراءته بدونها."),
        });

    public BankConfirmationParseResult Parse(string content, BankConfirmationBatchContext context)
    {
        var read = WpsReturnedFileValidator.Read(content, new WpsReturnedFileExpectation(context.MolEstablishmentId));
        var errors = read.Findings
            .Where(f => f.Severity == WpsFindingSeverity.Error)
            .Select(f => new BankConfirmationParseError(f.Line ?? 0, f.Code, Both(f.Message, f.MessageAr)))
            .ToList();
        var rows = new List<BankConfirmationRow>();
        if (read.HasFileLevelError) return new BankConfirmationParseResult(rows, errors);

        var rec = WpsReturnedFileReconciler.Reconcile(read, context.Expected);
        var fileRows = read.Rows.ToDictionary(r => r.Line);
        var noStatus = 0;

        foreach (var line in rec.Lines)
        {
            switch (line.Classification)
            {
                case WpsReconciliationClass.Matched when line.Outcome is null:
                    noStatus++;
                    break;
                case WpsReconciliationClass.Matched or WpsReconciliationClass.Rejected or WpsReconciliationClass.AmountMismatch
                    when line.Outcome is not null:
                {
                    var fr = fileRows[line.FileLine!.Value];
                    if (string.IsNullOrWhiteSpace(line.EmployeeCode))
                    {
                        errors.Add(new(fr.Line, "employee_code_missing", Both(
                            "The employee has no employee code, so the import cannot apply this line.",
                            "ليس للموظف رمز موظف، لذلك لا يمكن للاستيراد تطبيق هذا السطر.")));
                        break;
                    }
                    var returned = line.Classification == WpsReconciliationClass.Rejected;
                    if (line.Classification == WpsReconciliationClass.AmountMismatch)
                        errors.Add(new(fr.Line, line.Classification, Both(line.ReasonEn, line.ReasonAr)));
                    rows.Add(new BankConfirmationRow(
                        RowNumber: fr.Line, WpsReference: null, EmployeeCode: line.EmployeeCode, Iban: null, Amount: fr.Net,
                        RawOutcome: fr.TrnStatus ?? string.Empty, Outcome: line.Outcome,
                        ReasonCode: returned ? line.ReasonCode : null,
                        ReasonText: returned ? Both(line.ReasonEn, line.ReasonAr) : null,
                        BankReference: fr.TrnRef, ValueDate: fr.TrnDate));
                    break;
                }
                default:
                    // Unknown / missing / ambiguous / invalid / account mismatch / status not recognised: reported, never applied.
                    errors.Add(new(line.FileLine ?? 0, line.Classification, Both(line.ReasonEn, line.ReasonAr)));
                    break;
            }
        }

        if (noStatus > 0)
            errors.Add(new(0, "status_missing", Both(
                $"{noStatus} line(s) match the payroll run but carry no payment status in the file, so they confirm the figures only. Nothing was applied for them.",
                $"{noStatus} سطر(أسطر) تطابق كشف الرواتب لكنها بلا حالة دفع في الملف، لذلك تؤكد الأرقام فقط. لم يتم تطبيق أي شيء لها.")));

        return new BankConfirmationParseResult(rows, errors);
    }

    private static string Both(string en, string ar) => $"{en} | {ar}";
}
