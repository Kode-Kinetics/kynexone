using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Zayra.Api.Infrastructure.Payroll.SaudiBankExports;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Finance;

/// <summary>
/// What the import knows about the payment batch, loaded by the service (tenant- and company-scoped) and handed to a
/// parser that asks for it. It is read-only data: a parser cannot change a record through it.
/// <see cref="ImportedFileRefKeys"/> are the "company|FILE-REF" keys of files already imported for this tenant.
/// </summary>
public sealed record BankConfirmationBatchContext(
    string? MolEstablishmentId,
    IReadOnlyList<WpsExpectedLine> Expected,
    DateOnly? BatchCreatedOn = null,
    decimal? BatchTotal = null,
    string? FileRefScope = null,
    IReadOnlySet<string>? ImportedFileRefKeys = null);

/// <summary>
/// A parser that needs the batch to read a file correctly. <see cref="BankConfirmationService"/> calls the context
/// overload when a parser implements this; every existing parser is untouched and still gets <c>Parse(content)</c>.
/// <see cref="CanonicalContent"/> is what the duplicate-file guard hashes, so a byte-level tweak of the same file is still
/// recognised as the same file.
/// </summary>
public interface IContextualBankConfirmationParser : IBankConfirmationParser
{
    BankConfirmationParseResult Parse(string content, BankConfirmationBatchContext context);
    string CanonicalContent(string content);
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

public static class BankConfirmationParserRegistration
{
    /// <summary>Registers the bank-confirmation parsers. The returned-file reader is added only when its flag is on.</summary>
    public static IServiceCollection AddBankConfirmationParsers(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton<IBankConfirmationParser, GenericCsvBankConfirmationParser>();
        services.AddSingleton<IBankConfirmationParser, WpsAckBankConfirmationParser>();
        if (WpsReturnedFileFeature.IsEnabled(configuration))
            services.AddSingleton<IBankConfirmationParser, WpsReturnedFileBankConfirmationParser>();
        return services;
    }
}

/// <summary>
/// Reads a WPS file that the employer's BANK produced and signed, reconciles it against the payment batch by employee,
/// account and amount, and hands the existing bank-confirmation import canonical rows. It only READS: it never writes,
/// strips or re-signs the file, and it checks consistency, not authenticity (it does not verify the bank's signature).
///
/// <para>What is actually checked before any line is passed on: the establishment equals the company's; the file is dated
/// on or after this batch was created and its total does not exceed the batch's; its FILE-REF was not imported before for
/// this company; the header total equals the sum of the lines; and the lines are not altered (no duplicate employee or
/// account, net equals the breakdown, every account equals the one on our record). Any of these failing refuses the WHOLE
/// file. A single faulty line yields no row for that line. A status word outside <see cref="WpsReturnedFileStatus"/>
/// confirms nothing. The per-line status tag is itself unverified.</para>
/// </summary>
public sealed class WpsReturnedFileBankConfirmationParser : IContextualBankConfirmationParser
{
    public string Key => "wps-returned-file";
    public string DisplayName => "Bank-returned WPS file (TAB-delimited, read-only)";
    public string Disclaimer =>
        "UNVERIFIED LAYOUT. Reads the file your BANK produced and signed; it never changes or re-signs it, it does not verify the "
      + "bank's signature, and it is not a wage-protection submission or a Mudad acceptance. The tab and [TAG] layout and the per-line "
      + "status field are assumed from field names the product already cites and have not been confirmed against a real bank file. "
      + "A file is refused as a whole if its establishment, date, total or FILE-REF do not fit this payment batch, or if any account "
      + "differs from the payroll record. Lines are matched to payment records by government ID, account and amount.";

    private const int MaxErrors = 200;

    /// <summary>The establishment and the employee records are needed to read this file safely, so the plain overload refuses.</summary>
    public BankConfirmationParseResult Parse(string content) =>
        new(Array.Empty<BankConfirmationRow>(), new[]
        {
            new BankConfirmationParseError(0, "context_required",
                "This format needs the payment batch to check the establishment and match employees; it cannot be read without it.",
                "يحتاج هذا التنسيق إلى دفعة الرواتب للتحقق من المنشأة ومطابقة الموظفين؛ لا يمكن قراءته بدونها."),
        });

    public string CanonicalContent(string content) => WpsReturnedFileValidator.CanonicalForm(content);

    public BankConfirmationParseResult Parse(string content, BankConfirmationBatchContext context)
    {
        var expectation = new WpsReturnedFileExpectation(
            context.MolEstablishmentId, BindToBatch: true, BatchCreatedOn: context.BatchCreatedOn, BatchTotal: context.BatchTotal,
            ExpectedLineCount: context.Expected.Count, FileRefScope: context.FileRefScope, ImportedFileRefKeys: context.ImportedFileRefKeys);
        var read = WpsReturnedFileValidator.Read(content, expectation);
        var errors = read.Findings
            .Where(f => f.Severity == WpsFindingSeverity.Error)
            .Select(f => new BankConfirmationParseError(f.Line ?? 0, f.Code, f.Message, f.MessageAr))
            .ToList();
        var rows = new List<BankConfirmationRow>();

        BankConfirmationParseResult Done(string? importKey = null)
        {
            if (errors.Count > MaxErrors)
            {
                var extra = errors.Count - MaxErrors;
                // File-level reasons (row 0) are kept ahead of per-line ones.
                errors = errors.Where(e => e.RowNumber == 0).Concat(errors.Where(e => e.RowNumber != 0)).Take(MaxErrors).ToList();
                errors.Add(new(0, "more_errors", $"{extra} more fault(s) are not listed.", $"يوجد {extra} خطأ إضافي لم يُعرض."));
            }
            errors.Add(new(0, "scope_notice", WpsReturnedFileReconciler.ScopeNoticeEn, WpsReturnedFileReconciler.ScopeNoticeAr));
            return new BankConfirmationParseResult(rows, errors, importKey);
        }

        if (read.HasFileLevelError) return Done();

        var rec = WpsReturnedFileReconciler.Reconcile(read, context.Expected);

        // Nothing in the file fits this batch: it is another batch's file (for example last month's).
        var identified = rec.Lines.Count(l => l.EmployeeId is not null && l.FileLine is not null);
        if (identified == 0)
        {
            AddFileError("no_line_matches_batch");
            return Done();
        }
        // A different account than our record is a sign the file was altered (or the employee's bank changed after approval):
        // the whole file is refused so nobody applies the rest by accident.
        if (rec.Lines.Any(l => l.Classification == WpsReconciliationClass.AccountMismatch))
        {
            foreach (var l in rec.Lines.Where(l => l.Classification == WpsReconciliationClass.AccountMismatch))
                errors.Add(new(l.FileLine ?? 0, l.Classification, l.ReasonEn, l.ReasonAr));
            AddFileError("file_refused_account_mismatch");
            return Done();
        }

        var fileRows = read.Rows.ToDictionary(r => r.Line);
        var noStatus = 0;
        foreach (var line in rec.Lines)
        {
            switch (line.Classification)
            {
                case WpsReconciliationClass.MatchedFiguresOnly:
                    noStatus++;
                    break;
                case WpsReconciliationClass.MatchedPaid or WpsReconciliationClass.MatchedPending or WpsReconciliationClass.Rejected
                    or WpsReconciliationClass.AmountMismatch when line.Outcome is not null:
                {
                    var fr = fileRows[line.FileLine!.Value];
                    if (string.IsNullOrWhiteSpace(line.EmployeeCode))
                    {
                        errors.Add(new(fr.Line, "employee_code_missing",
                            "The employee has no employee number, so the import cannot apply this line.",
                            "ليس للموظف رقم وظيفي، لذلك لا يمكن للاستيراد تطبيق هذا السطر."));
                        break;
                    }
                    var returned = line.Classification == WpsReconciliationClass.Rejected;
                    // The held amount and the returned line carry a reason the officer must see, in both languages.
                    if (line.Classification is WpsReconciliationClass.AmountMismatch or WpsReconciliationClass.Rejected)
                        errors.Add(new(fr.Line, line.Classification, line.ReasonEn, line.ReasonAr));
                    // A returned line is recorded as returned whatever amount the file shows; the import's own hold covers every other line.
                    rows.Add(new BankConfirmationRow(
                        RowNumber: fr.Line, WpsReference: null, EmployeeCode: line.EmployeeCode, Iban: null,
                        Amount: returned ? null : fr.Net,
                        RawOutcome: fr.TrnStatus ?? string.Empty, Outcome: line.Outcome,
                        ReasonCode: returned ? line.ReasonCode : null,
                        ReasonText: returned ? line.ReasonEn : null,
                        BankReference: fr.TrnRef, ValueDate: fr.TrnDate));
                    break;
                }
                default:
                    // Unknown / missing / ambiguous / invalid / account missing / status not recognised: reported, never applied.
                    errors.Add(new(line.FileLine ?? 0, line.Classification, line.ReasonEn, line.ReasonAr));
                    break;
            }
        }

        if (noStatus > 0)
            errors.Add(new(0, "status_missing",
                $"{noStatus} line(s) match the payroll run but carry no payment status in the file, so they confirm the figures only. Nothing was applied for them.",
                $"{noStatus} سطر (أسطر) تطابق مسير الرواتب لكنها بلا حالة دفع في الملف، لذلك تؤكد الأرقام فقط. لم يُطبَّق أي شيء لها."));

        return Done(rows.Count > 0 ? read.FileRefKey : null);

        void AddFileError(string code)
        {
            var (en, ar) = WpsReturnedFileValidator.MessageFor(code);
            errors.Add(new(0, code, en, ar));
        }
    }
}
