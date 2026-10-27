using System.Globalization;
using System.Text;
using Zayra.Api.Application.Common;

namespace Zayra.Api.Infrastructure.Payroll.SaudiBankExports;

/// <summary>One employee line. <see cref="Ksa"/> carries every figure and identifier exactly as the
/// payroll run computed it; the extra members are the template-only fields.</summary>
public sealed record CustomBankRow(KsaWageFileRow Ksa, string EmployeeNumber, string? NameAr);

/// <summary>Employer and batch facts for the header/trailer and the totals the file must reconcile to.
/// <see cref="FileDate"/> and <see cref="ValueDate"/> are supplied, never read from a clock, so the same
/// input always yields the same bytes.</summary>
public sealed record CustomBankFileContext(
    string? EstablishmentId, string? Currency, string BatchReference, DateOnly FileDate, DateOnly ValueDate,
    string? DebitAccountIban, string? PaymentReference, decimal ExpectedTotal, int ExpectedCount,
    IReadOnlyCollection<string>? ReferencesAlreadyUsed = null);

public sealed record CustomBankFileResult(
    IReadOnlyList<SaudiBankExportIssueDto> Errors, AnbGeneratedFile? File, int RecordCount, decimal TotalAmount)
{
    public bool Ok => Errors.Count == 0 && File is not null;
}

/// <summary>
/// PURE generator for a customer-configured bank salary file. No I/O, no clock, no culture.
///
/// <para>ORDER OF AUTHORITY: the Saudi wage-file rules (<see cref="KsaWageFileRules"/>) run first and on
/// every row; a Saudi IBAN is required even where the KSA rule would let an ANB internal account pass;
/// the file must reconcile to the batch total and record count. The template is consulted only to LAY
/// OUT rows that already passed — a template can never switch a rule off.</para>
///
/// <para>FAIL CLOSED: nothing is trimmed, rounded, truncated, defaulted or substituted. A value that
/// does not fit its column, has more decimals than the column writes, needs quoting that the quote rule
/// forbids, or cannot be encoded in the chosen code page fails the whole file. Text cells go through the
/// shared formula-injection guard (<see cref="Csv.Guard"/>). Errors name the employee code and the
/// field, never an ID or account value.</para>
/// </summary>
public static class CustomBankFileGenerator
{
    private const int MaxErrors = 500;
    public const int MaxOutputBytes = 25 * 1024 * 1024;
    public const int PreviewRows = 5;

    /// <summary>Bounded error collector: keeps at most <see cref="MaxErrors"/> issues but still remembers
    /// WHICH employees/fields failed (hash sets), so no caller scans the list per row.</summary>
    internal sealed class Sink
    {
        public readonly List<SaudiBankExportIssueDto> Items = new();
        private readonly HashSet<int> _employees = new();
        private readonly HashSet<(int, string)> _fields = new();
        public bool Full => Items.Count >= MaxErrors;
        public bool Truncated { get; private set; }
        public bool HasEmployee(int id) => _employees.Contains(id);
        public bool HasField(int id, string? field) => field is not null && _fields.Contains((id, field));
        public void Add(SaudiBankExportIssueDto e)
        {
            if (e.EmployeeId is int id) { _employees.Add(id); if (e.Field is not null) _fields.Add((id, e.Field)); }
            if (Items.Count >= MaxErrors) { Truncated = true; return; }
            Items.Add(e);
        }
        public void Add(string code, string message, int? employee = null, string? field = null) => Add(new SaudiBankExportIssueDto(code, message, employee, field));
        public IReadOnlyList<SaudiBankExportIssueDto> Result()
        {
            if (!Truncated) return Items;
            var kept = Items.ToList();
            kept.Add(new("more_errors", "Further issues are not listed. Fix these and validate again."));
            return kept;
        }
    }

    /// <param name="previewOnly">Saudi rules run on EVERY row, but only the first <see cref="PreviewRows"/> lines are laid out
    /// and no file is produced: the preview never renders the whole file.</param>
    public static CustomBankFileResult Generate(
        CustomBankTemplateDefinition template, CustomBankFileContext ctx, IReadOnlyList<CustomBankRow> rowsIn, bool previewOnly = false)
    {
        var sink = new Sink();
        var check = CustomBankTemplates.Validate(template);
        if (!check.Ok)
        {
            foreach (var e in check.Errors) sink.Add("template_invalid", $"The template is not valid: {e.Message}", null, e.Field);
            return new(sink.Result(), null, 0, 0m);
        }

        var rows = rowsIn.OrderBy(r => r.Ksa.EmployeeRef).ToList();
        var total = rows.Sum(r => r.Ksa.Net);

        // ── 1. Saudi rules: not negotiable, not template-dependent ─────────────────────────────────
        var ksa = KsaWageFileRules.ValidateAll(
            new KsaWageFileHeader(ctx.EstablishmentId, ctx.Currency, ctx.BatchReference, false, null),
            rows.Select(r => r.Ksa).ToList(), ctx.ReferencesAlreadyUsed);
        foreach (var e in ksa.Errors) sink.Add(e);
        foreach (var r in rows)
        {
            if (sink.Full) break;
            var k = r.Ksa;
            // KSA lets a 16-digit ANB internal account through to ANB's own rules; a custom bank file
            // is IBAN-only, so it is held to the Saudi IBAN rule here.
            if (!string.IsNullOrEmpty(k.Iban) && !IbanValidator.IsSaudiIban(k.Iban) && !sink.HasField(k.EmployeeRef, "employeeAccountNumber"))
                sink.Add(KsaWageFileRules.Codes.IbanNotSaudi, $"{k.EmployeeLabel}: the account must be a valid Saudi IBAN (SA + 22 characters, check digits correct).", k.EmployeeRef, "employeeAccountNumber");
            foreach (var (field, value) in new[] { ("basicSalary", k.Basic), ("housingAllowance", k.Housing), ("otherEarnings", k.Gross - k.Basic - k.Housing), ("salaryDeductions", k.SlipDeductions), ("salaryAmount", k.Net) })
                if (value < 0m && !sink.HasField(k.EmployeeRef, field))
                    sink.Add("amount_negative", $"{k.EmployeeLabel}: {field} is negative.", k.EmployeeRef, field);
        }
        foreach (var dup in rows.GroupBy(r => r.Ksa.EmployeeRef).Where(g => g.Count() > 1))
            sink.Add("duplicate_employee", $"{dup.First().Ksa.EmployeeLabel}: appears more than once in the file.", dup.Key);
        if (rows.Count > CustomBankTemplates.MaxRows)
            sink.Add("record_count_out_of_range", $"A file holds at most {CustomBankTemplates.MaxRows} lines.");
        if (total != ctx.ExpectedTotal)
            sink.Add("total_mismatch", $"The lines add up to {total:0.00}, but the batch total is {ctx.ExpectedTotal:0.00}.", null, "totalAmount");
        if (rows.Count != ctx.ExpectedCount)
            sink.Add("record_count_mismatch", $"The file has {rows.Count} line(s), but the batch has {ctx.ExpectedCount} payment(s) to send.", null, "recordCount");

        var envelopeColumns = template.HeaderRecord.Concat(template.TrailerRecord).ToList();
        if (envelopeColumns.Any(c => c.Field == CustomBankFields.DebitAccountIban) && !IbanValidator.IsSaudiIban(ctx.DebitAccountIban))
            sink.Add("debit_account_invalid", "The company's debit account must be a valid Saudi IBAN. Set it in the bank export settings.", null, "debitAccountIban");

        // ── 2. Layout, bounded ──────────────────────────────────────────────────────────────────────
        var enc = CustomBankTemplates.StrictEncoding(template.Encoding)!;
        var writer = new Writer(template, enc, sink);
        var eol = template.LineEnding == CustomBankTemplates.Lf ? "\n" : "\r\n";
        var lines = new List<string>();
        long bytes = 0;
        bool Push(string line)
        {
            bytes += enc.GetByteCount(line) + eol.Length;
            lines.Add(line);
            if (bytes <= MaxOutputBytes) return true;
            sink.Add("file_too_large", $"The file would exceed {MaxOutputBytes / (1024 * 1024)} MB.");
            return false;
        }
        var ok = true;
        if (template.Layout == CustomBankTemplates.Delimited && template.HeaderRow) ok = Push(writer.TitleRow(template.Columns));
        if (ok && template.HeaderRecord.Count > 0) ok = Push(writer.Envelope(template.HeaderRecord, ctx, rows.Count, total, "headerRecord"));
        var detailLines = new List<string>(previewOnly ? PreviewRows : rows.Count);
        var detailRows = new List<CustomBankRow>(detailLines.Capacity);
        foreach (var r in rows)
        {
            if (!ok || sink.Full || (previewOnly && detailLines.Count >= PreviewRows)) break;
            var line = writer.Detail(template.Columns, r, ctx);
            detailLines.Add(line); detailRows.Add(r);
            ok = Push(line);
        }
        if (ok && template.TrailerRecord.Count > 0) Push(writer.Envelope(template.TrailerRecord, ctx, rows.Count, total, "trailerRecord"));

        // ── 3. Read every rendered line back with the template's own layout ───────────────────────────
        if (sink.Items.Count == 0 && !previewOnly)
            foreach (var e in VerifyRendered(template, detailLines, detailRows, ctx.ExpectedTotal)) sink.Add(e);

        if (sink.Items.Count > 0 || previewOnly) return new(sink.Result(), null, rows.Count, total);

        var text = string.Join(eol, lines) + (template.EndWithLineBreak ? eol : string.Empty);
        byte[] body;
        try { body = enc.GetBytes(text); }
        catch (EncoderFallbackException)
        {
            return new(new[] { new SaudiBankExportIssueDto("value_not_encodable", "The file contains a character the chosen encoding cannot represent. Nothing was substituted.") }, null, rows.Count, total);
        }
        var preamble = template.Encoding == CustomBankTemplates.Utf8Bom ? new byte[] { 0xEF, 0xBB, 0xBF } : Array.Empty<byte>();
        var all = new byte[preamble.Length + body.Length];
        preamble.CopyTo(all, 0);
        body.CopyTo(all, preamble.Length);
        return new(Array.Empty<SaudiBankExportIssueDto>(), new AnbGeneratedFile($"salary-{ctx.BatchReference}.{template.FileExtension}", all, AnbConnectCsvGenerator.Sha256Hex(all)), rows.Count, total);
    }

    // ── Read-back verification ───────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Parses each rendered detail line back with the template's OWN layout (delimited split with quoting, or
    /// fixed-width byte slices, and the same decimal/minor-unit rules) and checks that the cell in the IBAN slot
    /// is the row's IBAN, the cell in the net-pay slot is the row's net pay and the cell in the ID slot is the
    /// row's national ID; then that the parsed amounts add up to <paramref name="expectedTotal"/>. The file
    /// must say what the batch says, whatever the template does.
    /// </summary>
    public static IReadOnlyList<SaudiBankExportIssueDto> VerifyRendered(
        CustomBankTemplateDefinition t, IReadOnlyList<string> detailLines, IReadOnlyList<CustomBankRow> rows, decimal expectedTotal)
    {
        var sink = new Sink();
        var enc = CustomBankTemplates.StrictEncoding(t.Encoding)!;
        var cols = t.Columns;
        int Slot(string f) => cols.FindIndex(c => c.Field == f);
        int iIban = Slot(CustomBankFields.BeneficiaryIban), iNet = Slot(CustomBankFields.NetPay), iId = Slot(CustomBankFields.NationalId);
        if (iIban < 0 || iNet < 0 || iId < 0 || detailLines.Count != rows.Count)
        {
            sink.Add("render_unverifiable", "The rendered file could not be checked against the batch.");
            return sink.Result();
        }
        decimal sum = 0m;
        for (var n = 0; n < rows.Count && !sink.Full; n++)
        {
            var k = rows[n].Ksa;
            var cells = t.Layout == CustomBankTemplates.FixedWidth ? SliceFixed(detailLines[n], cols, enc) : SplitDelimited(detailLines[n], t.Delimiter);
            if (cells is null || cells.Count != cols.Count)
            {
                sink.Add("render_unparseable", $"{k.EmployeeLabel}: the rendered line does not read back in the template's own layout.", k.EmployeeRef);
                continue;
            }
            string Raw(int i) => t.Layout == CustomBankTemplates.FixedWidth ? Unpad(cells[i], cols[i], CustomBankFields.Find(cols[i].Field)!.Kind) : cells[i];
            if (!string.Equals(Raw(iIban), k.Iban, StringComparison.Ordinal))
                sink.Add("render_mismatch_iban", $"{k.EmployeeLabel}: the IBAN slot of the file does not hold this employee's IBAN.", k.EmployeeRef, CustomBankFields.BeneficiaryIban);
            if (!string.Equals(Raw(iId), k.MolId, StringComparison.Ordinal))
                sink.Add("render_mismatch_id", $"{k.EmployeeLabel}: the ID slot of the file does not hold this employee's national ID.", k.EmployeeRef, CustomBankFields.NationalId);
            var amount = ParseAmount(Raw(iNet), cols[iNet]);
            if (amount is null || amount != k.Net)
                sink.Add("render_mismatch_amount", $"{k.EmployeeLabel}: the net-pay slot of the file does not hold this employee's net pay.", k.EmployeeRef, CustomBankFields.NetPay);
            else sum += amount.Value;
        }
        if (sink.Items.Count == 0 && sum != expectedTotal)
            sink.Add("render_total_mismatch", $"The amounts read back from the file add up to {sum:0.00}, but the batch total is {expectedTotal:0.00}.", null, CustomBankFields.TotalAmount);
        return sink.Result();
    }

    private static string Unpad(string cell, CustomBankColumn c, string kind)
    {
        var pad = c.Pad.Length == 1 ? c.Pad[0] : ' ';
        var right = c.Align == "right" || kind is CustomBankFields.KindAmount or CustomBankFields.KindInteger;
        return right ? cell.TrimStart(pad) : cell.TrimEnd(pad);
    }

    private static List<string>? SliceFixed(string line, List<CustomBankColumn> cols, Encoding enc)
    {
        var bytes = enc.GetBytes(line);
        if (bytes.Length != cols.Sum(c => c.Width ?? 0)) return null;
        var cells = new List<string>(cols.Count);
        var at = 0;
        try
        {
            foreach (var c in cols) { var w = c.Width ?? 0; cells.Add(enc.GetString(bytes, at, w)); at += w; }
        }
        catch (DecoderFallbackException) { return null; }
        return cells;
    }

    private static List<string>? SplitDelimited(string line, string delimiter)
    {
        var d = delimiter[0];
        var cells = new List<string>();
        var i = 0;
        while (true)
        {
            var sb = new StringBuilder();
            if (i < line.Length && line[i] == '"')
            {
                i++;
                while (true)
                {
                    if (i >= line.Length) return null;
                    if (line[i] == '"')
                    {
                        if (i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i += 2; continue; }
                        i++; break;
                    }
                    sb.Append(line[i++]);
                }
                if (i < line.Length && line[i] != d) return null;
            }
            else
            {
                while (i < line.Length && line[i] != d) { if (line[i] == '"') return null; sb.Append(line[i++]); }
            }
            cells.Add(sb.ToString());
            if (i >= line.Length) return cells;
            i++;   // the delimiter
            if (i >= line.Length) { cells.Add(string.Empty); return cells; }
        }
    }

    private static decimal? ParseAmount(string raw, CustomBankColumn c)
    {
        var s = c.ThousandsSeparator.Length > 0 ? raw.Replace(c.ThousandsSeparator, string.Empty) : raw;
        var dec = Math.Clamp(c.AmountDecimals, 0, 4);
        if (c.DecimalSeparator.Length == 0)
        {
            if (s.Length == 0) return 0m;
            if (!s.All(char.IsAsciiDigit) || s.Length > 18) return null;
            var v = (decimal)long.Parse(s, CultureInfo.InvariantCulture);
            for (var i = 0; i < dec; i++) v /= 10m;
            return v;
        }
        if (c.DecimalSeparator != ".") s = s.Replace(c.DecimalSeparator, ".");
        return decimal.TryParse(s, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var r) ? r : null;
    }

    /// <summary>
    /// The first <paramref name="maxRows"/> lines in the template's real layout, for the preview. The caller
    /// passes rows whose IBAN and ID are ALREADY masked (<see cref="Redact"/>), so no real identifier reaches the
    /// response. This renders layout only: it does not run the Saudi rules (the caller ran them on the real rows)
    /// and never produces a file. <paramref name="recordCount"/> and <paramref name="totalAmount"/> are the
    /// whole batch's, as the trailer would carry them.
    /// </summary>
    public static (IReadOnlyList<string> Lines, IReadOnlyList<SaudiBankExportIssueDto> Errors) Preview(
        CustomBankTemplateDefinition template, CustomBankFileContext ctx, IReadOnlyList<CustomBankRow> redactedRows,
        int maxRows, int recordCount, decimal totalAmount)
    {
        var sink = new Sink();
        var check = CustomBankTemplates.Validate(template);
        if (!check.Ok)
        {
            foreach (var e in check.Errors) sink.Add("template_invalid", $"The template is not valid: {e.Message}", null, e.Field);
            return (Array.Empty<string>(), sink.Result());
        }
        var writer = new Writer(template, CustomBankTemplates.StrictEncoding(template.Encoding)!, sink);
        var lines = new List<string>();
        if (template.Layout == CustomBankTemplates.Delimited && template.HeaderRow) lines.Add(writer.TitleRow(template.Columns));
        if (template.HeaderRecord.Count > 0) lines.Add(writer.Envelope(template.HeaderRecord, ctx, recordCount, totalAmount, "headerRecord"));
        foreach (var r in redactedRows.OrderBy(r => r.Ksa.EmployeeRef).Take(maxRows)) lines.Add(writer.Detail(template.Columns, r, ctx));
        if (template.TrailerRecord.Count > 0) lines.Add(writer.Envelope(template.TrailerRecord, ctx, recordCount, totalAmount, "trailerRecord"));
        return (lines, sink.Result());
    }

    /// <summary>Masks an identifier for the preview: same length (so fixed-width layout is faithful), every
    /// character but the first <paramref name="keep"/> replaced by <c>X</c>. An IBAN keeps its country, check
    /// digits and bank code (not secret; the bank-code column still resolves).</summary>
    public static string? Redact(string? value, int keep) =>
        string.IsNullOrEmpty(value) ? value : value[..Math.Min(keep, value.Length)] + new string('X', Math.Max(0, value.Length - keep));

    // ── value rendering ────────────────────────────────────────────────────────────────────────────

    private sealed class Writer
    {
        private readonly CustomBankTemplateDefinition _t;
        private readonly Encoding _enc;
        private readonly Sink _errors;
        private readonly bool _fixed;

        public Writer(CustomBankTemplateDefinition t, Encoding enc, Sink errors)
        { _t = t; _enc = enc; _errors = errors; _fixed = t.Layout == CustomBankTemplates.FixedWidth; }

        public string TitleRow(List<CustomBankColumn> cols) =>
            string.Join(_t.Delimiter, cols.Select(c => Quote(Csv.Guard(string.IsNullOrEmpty(c.Title) ? c.Field : c.Title))));

        public string Detail(List<CustomBankColumn> cols, CustomBankRow row, CustomBankFileContext ctx)
        {
            var k = row.Ksa;
            // A row the Saudi rules already refused is reported once, under its Saudi code.
            if (_errors.HasEmployee(k.EmployeeRef)) return string.Empty;
            var cells = new List<string>(cols.Count);
            foreach (var c in cols)
            {
                object? v = c.Field switch
                {
                    CustomBankFields.EmployeeNumber => row.EmployeeNumber,
                    CustomBankFields.NameEn => k.Name,
                    CustomBankFields.NameAr => row.NameAr,
                    CustomBankFields.NationalId => k.MolId,
                    CustomBankFields.BeneficiaryIban => k.Iban,
                    CustomBankFields.BeneficiaryBankCode => BankCode(k.Iban),
                    CustomBankFields.BeneficiaryBic => k.BankBic,
                    CustomBankFields.NetPay => k.Net,
                    CustomBankFields.Basic => k.Basic,
                    CustomBankFields.Housing => k.Housing,
                    CustomBankFields.OtherEarnings => k.Gross - k.Basic - k.Housing,
                    CustomBankFields.Deductions => k.SlipDeductions,
                    CustomBankFields.Currency => ctx.Currency,
                    CustomBankFields.PaymentReference => ctx.PaymentReference,
                    CustomBankFields.Literal => c.Literal,
                    _ => null,
                };
                cells.Add(Cell(c, v, k.EmployeeRef, k.EmployeeLabel));
            }
            return Join(cells);
        }

        public string Envelope(List<CustomBankColumn> cols, CustomBankFileContext ctx, int count, decimal total, string record)
        {
            var cells = new List<string>(cols.Count);
            foreach (var c in cols)
            {
                object? v = c.Field switch
                {
                    CustomBankFields.FileDate => ctx.FileDate,
                    CustomBankFields.ValueDate => ctx.ValueDate,
                    CustomBankFields.EstablishmentId => ctx.EstablishmentId,
                    CustomBankFields.DebitAccountIban => ctx.DebitAccountIban,
                    CustomBankFields.TotalAmount => total,
                    CustomBankFields.RecordCount => count,
                    CustomBankFields.BatchReference => ctx.BatchReference,
                    CustomBankFields.Literal => c.Literal,
                    _ => null,
                };
                cells.Add(Cell(c, v, null, record));
            }
            return Join(cells);
        }

        private string Join(List<string> cells) => _fixed ? string.Concat(cells) : string.Join(_t.Delimiter, cells);

        private static string? BankCode(string? iban) =>
            iban is { Length: 24 } && KsaWageFileRules.SarieIdByIbanBankCode.TryGetValue(iban.Substring(4, 2), out var id) ? id : null;

        private string Cell(CustomBankColumn c, object? value, int? employee, string who)
        {
            string Fail(string code, string what)
            {
                _errors.Add(code, $"{who}: {c.Field} {what}", employee, c.Field);
                return string.Empty;
            }
            var entry = CustomBankFields.Find(c.Field)!;
            string text;
            switch (entry.Kind)
            {
                case CustomBankFields.KindAmount:
                {
                    if (value is not decimal d) return Fail("field_missing", "has no value.");
                    if (d < 0m) return Fail("amount_negative", "is negative.");
                    var dec = Math.Clamp(c.AmountDecimals, 0, 4);
                    if (decimal.Round(d, dec) != d)
                        return Fail("amount_precision_loss", $"has more than {dec} decimal(s) and this column writes {dec}. Nothing is rounded.");
                    text = Amount(d, dec, c.DecimalSeparator, c.ThousandsSeparator);
                    break;
                }
                case CustomBankFields.KindDate:
                    if (value is not DateOnly date) return Fail("field_missing", "has no value.");
                    text = date.ToString(c.DateFormat, CultureInfo.InvariantCulture);
                    break;
                case CustomBankFields.KindInteger:
                    if (value is not int n) return Fail("field_missing", "has no value.");
                    text = n.ToString(CultureInfo.InvariantCulture);
                    break;
                default:
                {
                    var s = value as string;
                    if (string.IsNullOrWhiteSpace(s))
                    {
                        if (!c.AllowBlank || c.Field is CustomBankFields.BeneficiaryIban or CustomBankFields.NationalId)
                            return Fail("field_empty", "is empty. A blank value is never written.");
                        s = string.Empty;
                    }
                        if (s.Any(char.IsControl)) return Fail("value_control_character", "contains a tab, line break or other control character.");
                    if (s.Any(CustomBankTemplates.IsHiddenChar)) return Fail("value_hidden_character", "contains an invisible or direction-control character.");
                    if (s.Any(CustomBankTemplates.IsHiddenChar)) return Fail("value_hidden_character", "contains an invisible or direction-control character.");
                    if (c.UpperCase) s = s.ToUpperInvariant();
                    text = Csv.Guard(s);
                    break;
                }
            }
            if (c.MaxLength is { } ml && text.Length > ml)
                return Fail("value_too_long", $"has {text.Length} characters; this column allows {ml}. Nothing is cut.");
            try { _enc.GetByteCount(text); }
            catch (EncoderFallbackException) { return Fail("value_not_encodable", "contains a character the chosen encoding cannot represent. Nothing is substituted."); }

            if (_fixed)
            {
                var width = c.Width ?? 0;
                var bytes = _enc.GetByteCount(text);
                if (bytes > width) return Fail("value_too_wide", $"needs {bytes} bytes; the column is {width} wide. Nothing is cut.");
                var pad = new string(c.Pad.Length == 1 ? c.Pad[0] : ' ', width - bytes);
                // Amounts and integers are always right-aligned in a fixed-width record, whatever the template says.
                var right = c.Align == "right" || entry.Kind is CustomBankFields.KindAmount or CustomBankFields.KindInteger;
                return right ? pad + text : text + pad;
            }
            return Quote(text);
        }

        private string Quote(string text)
        {
            var needs = text.Contains(_t.Delimiter, StringComparison.Ordinal) || text.Contains('"') || text.Contains('\r') || text.Contains('\n');
            switch (_t.QuoteRule)
            {
                case CustomBankTemplates.QuoteAlways:
                    return "\"" + text.Replace("\"", "\"\"") + "\"";
                case CustomBankTemplates.QuoteMinimal:
                    return needs ? "\"" + text.Replace("\"", "\"\"") + "\"" : text;
                default:
                    if (needs)
                    {
                        _errors.Add("value_needs_quoting", "A value contains the delimiter or a quote and this template does not quote. Turn quoting on or change the delimiter.");
                        return string.Empty;
                    }
                    return text;
            }
        }

        private static string Amount(decimal d, int decimals, string decimalSeparator, string thousands)
        {
            if (decimalSeparator.Length == 0)
            {
                // Minor units: 1234.50 with 2 decimals is written 123450.
                var scaled = d;
                for (var i = 0; i < decimals; i++) scaled *= 10m;
                return decimal.Truncate(scaled).ToString("0", CultureInfo.InvariantCulture);
            }
            var s = d.ToString("F" + decimals, CultureInfo.InvariantCulture);
            var dot = s.IndexOf('.');
            var whole = dot < 0 ? s : s[..dot];
            var frac = dot < 0 ? string.Empty : s[(dot + 1)..];
            if (thousands.Length > 0)
            {
                var sb = new StringBuilder();
                for (var i = 0; i < whole.Length; i++)
                {
                    if (i > 0 && (whole.Length - i) % 3 == 0) sb.Append(thousands);
                    sb.Append(whole[i]);
                }
                whole = sb.ToString();
            }
            return frac.Length == 0 ? whole : whole + decimalSeparator + frac;
        }
    }
}
