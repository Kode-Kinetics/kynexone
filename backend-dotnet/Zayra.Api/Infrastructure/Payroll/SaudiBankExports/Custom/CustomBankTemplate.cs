using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Zayra.Api.Application.Common;

namespace Zayra.Api.Infrastructure.Payroll.SaudiBankExports;

/// <summary>
/// A customer-configured bank salary-file layout ("custom bank template"). It decides LAYOUT ONLY:
/// which payroll fields appear, in which order and how each is written. It never decides whether an
/// invalid row is exported — the Saudi wage-file rules run on every row regardless of the template.
///
/// <para>This is the customer's own configuration of a layout they read off their bank's sample file.
/// It is NOT a bank-certified format: acceptance by the bank is unverified until the customer has
/// uploaded a test file (see <see cref="CustomBankTemplates.AcceptanceStatus"/>).</para>
///
/// <para>Closed vocabularies everywhere: fields come from <see cref="CustomBankFields"/>, every option
/// from a fixed list, date patterns from a whitelist. Nothing here is a free-form expression, a format
/// string or a path, so a template cannot reach data or behaviour outside the catalog.</para>
/// </summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class CustomBankTemplateDefinition
{
    public string Name { get; set; } = string.Empty;
    /// <summary>Free text: which bank sample file this layout was modelled on (audit trail only).</summary>
    public string SampleReference { get; set; } = string.Empty;
    /// <summary><c>delimited</c> or <c>fixed-width</c>.</summary>
    public string Layout { get; set; } = CustomBankTemplates.Delimited;
    /// <summary><c>utf-8</c>, <c>utf-8-bom</c> or <c>windows-1256</c>.</summary>
    public string Encoding { get; set; } = CustomBankTemplates.Utf8;
    /// <summary><c>crlf</c> or <c>lf</c>.</summary>
    public string LineEnding { get; set; } = CustomBankTemplates.Crlf;
    /// <summary>Whether the last line is also terminated by a line ending.</summary>
    public bool EndWithLineBreak { get; set; } = true;
    /// <summary><c>csv</c>, <c>txt</c> or <c>dat</c>.</summary>
    public string FileExtension { get; set; } = "csv";
    /// <summary>Delimited only: <c>,</c> <c>;</c> <c>|</c> or a tab.</summary>
    public string Delimiter { get; set; } = ",";
    /// <summary>Delimited only: <c>none</c> (a value that needs quoting is refused), <c>minimal</c> or <c>always</c>.</summary>
    public string QuoteRule { get; set; } = CustomBankTemplates.QuoteMinimal;
    /// <summary>Delimited only: write a first row of column titles.</summary>
    public bool HeaderRow { get; set; }
    /// <summary>Optional first record (before the detail lines). Empty = none.</summary>
    public List<CustomBankColumn> HeaderRecord { get; set; } = new();
    /// <summary>One detail line per employee, in this column order.</summary>
    public List<CustomBankColumn> Columns { get; set; } = new();
    /// <summary>Optional last record (after the detail lines). Empty = none.</summary>
    public List<CustomBankColumn> TrailerRecord { get; set; } = new();
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class CustomBankColumn
{
    /// <summary>A key of <see cref="CustomBankFields"/> valid for the record this column sits in.</summary>
    public string Field { get; set; } = string.Empty;
    /// <summary>Only for the <c>literal</c> field: the fixed text written in every line.</summary>
    public string? Literal { get; set; }
    /// <summary>Column title for the delimited title row.</summary>
    public string Title { get; set; } = string.Empty;
    /// <summary>Fixed-width only: the field's width, in BYTES of the chosen encoding.</summary>
    public int? Width { get; set; }
    /// <summary>Fixed-width only: <c>left</c> or <c>right</c>.</summary>
    public string Align { get; set; } = "left";
    /// <summary>Fixed-width only: one printable ASCII padding character.</summary>
    public string Pad { get; set; } = " ";
    /// <summary>Amounts: number of decimals written. A value with more decimals is refused, never rounded.</summary>
    public int AmountDecimals { get; set; } = 2;
    /// <summary>Amounts: <c>.</c>, <c>,</c> or empty (no separator: the amount is written in minor units).</summary>
    public string DecimalSeparator { get; set; } = ".";
    /// <summary>Amounts: empty, <c>,</c>, <c>.</c> or a space.</summary>
    public string ThousandsSeparator { get; set; } = string.Empty;
    /// <summary>Dates: one of <see cref="CustomBankTemplates.DateFormats"/>.</summary>
    public string DateFormat { get; set; } = "yyyyMMdd";
    public bool UpperCase { get; set; }
    /// <summary>Longest value accepted. A longer value FAILS the file; it is never cut.</summary>
    public int? MaxLength { get; set; }
    /// <summary>Allow an empty value for an optional text field (never for IBAN, ID or net pay).</summary>
    public bool AllowBlank { get; set; }
}

public static class CustomBankFields
{
    // Detail (per employee) fields.
    public const string EmployeeNumber = "employeeNumber";
    public const string NameEn = "nameEn";
    public const string NameAr = "nameAr";
    public const string NationalId = "nationalId";
    public const string BeneficiaryIban = "beneficiaryIban";
    /// <summary>The 4-letter SARIE bank id DERIVED from the IBAN's bank code.</summary>
    public const string BeneficiaryBankCode = "beneficiaryBankCode";
    /// <summary>The employee's recorded bank BIC (checked against the IBAN's bank).</summary>
    public const string BeneficiaryBic = "beneficiaryBic";
    public const string NetPay = "netPay";
    public const string Basic = "basicSalary";
    public const string Housing = "housingAllowance";
    public const string OtherEarnings = "otherEarnings";
    public const string Deductions = "deductions";
    public const string Currency = "currency";
    /// <summary>The company's bank-export narrative (settings), the same text on every line.</summary>
    public const string PaymentReference = "paymentReference";
    public const string Literal = "literal";
    // Header / trailer fields.
    public const string FileDate = "fileDate";
    public const string ValueDate = "valueDate";
    public const string EstablishmentId = "establishmentId";
    public const string DebitAccountIban = "debitAccountIban";
    public const string TotalAmount = "totalAmount";
    public const string RecordCount = "recordCount";
    public const string BatchReference = "batchReference";

    public const string KindText = "text", KindAmount = "amount", KindDate = "date", KindInteger = "integer";

    public sealed record Entry(string Key, string Kind, bool Detail, bool Envelope);

    public static readonly IReadOnlyList<Entry> Catalog = new[]
    {
        new Entry(EmployeeNumber, KindText, true, false),
        new Entry(NameEn, KindText, true, false),
        new Entry(NameAr, KindText, true, false),
        new Entry(NationalId, KindText, true, false),
        new Entry(BeneficiaryIban, KindText, true, false),
        new Entry(BeneficiaryBankCode, KindText, true, false),
        new Entry(BeneficiaryBic, KindText, true, false),
        new Entry(NetPay, KindAmount, true, false),
        new Entry(Basic, KindAmount, true, false),
        new Entry(Housing, KindAmount, true, false),
        new Entry(OtherEarnings, KindAmount, true, false),
        new Entry(Deductions, KindAmount, true, false),
        new Entry(Currency, KindText, true, false),
        new Entry(PaymentReference, KindText, true, false),
        new Entry(Literal, KindText, true, true),
        new Entry(FileDate, KindDate, false, true),
        new Entry(ValueDate, KindDate, false, true),
        new Entry(EstablishmentId, KindText, false, true),
        new Entry(DebitAccountIban, KindText, false, true),
        new Entry(TotalAmount, KindAmount, false, true),
        new Entry(RecordCount, KindInteger, false, true),
        new Entry(BatchReference, KindText, false, true),
    };

    public static Entry? Find(string? key) => Catalog.FirstOrDefault(e => string.Equals(e.Key, key, StringComparison.Ordinal));
}

public static class CustomBankTemplates
{
    public const string FormatPrefix = "custom:";
    public const string Delimited = "delimited", FixedWidth = "fixed-width";
    public const string Utf8 = "utf-8", Utf8Bom = "utf-8-bom", Windows1256 = "windows-1256";
    public const string Crlf = "crlf", Lf = "lf";
    public const string QuoteNone = "none", QuoteMinimal = "minimal", QuoteAlways = "always";
    public const string Pending = "Pending", Approved = "Approved", Rejected = "Rejected", Superseded = "Superseded", Retired = "Retired";
    public const int MaxColumns = 40;
    public const int MaxRows = 20_000;

    /// <summary>Honest status string shown wherever a custom template is named.</summary>
    public const string AcceptanceStatus = "customer-configured-layout; bank-acceptance-not-verified";

    public static readonly IReadOnlyList<string> Layouts = new[] { Delimited, FixedWidth };
    public static readonly IReadOnlyList<string> Encodings = new[] { Utf8, Utf8Bom, Windows1256 };
    public static readonly IReadOnlyList<string> LineEndings = new[] { Crlf, Lf };
    public static readonly IReadOnlyList<string> Extensions = new[] { "csv", "txt", "dat" };
    public static readonly IReadOnlyList<string> Delimiters = new[] { ",", ";", "|", "\t" };
    public static readonly IReadOnlyList<string> QuoteRules = new[] { QuoteNone, QuoteMinimal, QuoteAlways };
    public static readonly IReadOnlyList<string> DateFormats = new[] { "yyyyMMdd", "yyMMdd", "yyyy-MM-dd", "dd/MM/yyyy", "ddMMyyyy", "dd-MM-yyyy" };
    public static readonly IReadOnlyList<string> DecimalSeparators = new[] { ".", ",", "" };
    public static readonly IReadOnlyList<string> ThousandsSeparators = new[] { "", ",", ".", " " };

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Unicode format (bidi, zero-width), line and paragraph separators: invisible in a cell and a
    /// way to make a value read differently from what it holds.</summary>
    public static bool IsHiddenChar(char c) =>
        char.GetUnicodeCategory(c) is System.Globalization.UnicodeCategory.Format or System.Globalization.UnicodeCategory.LineSeparator
            or System.Globalization.UnicodeCategory.ParagraphSeparator;

    public const int MaxRecordWidth = 2000;
    public const int KeptRetired = 5;

    private static readonly System.Text.RegularExpressions.Regex IbanLike =
        new(@"SA\p{Nd}|\p{Nd}{5}", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    public static string FormatId(Guid templateId) => $"{FormatPrefix}{templateId:D}";

    /// <summary>Exact, ordinal parse of <c>custom:&lt;guid&gt;</c>. Anything else is not a custom id.</summary>
    public static bool TryParseFormatId(string? formatId, out Guid templateId)
    {
        templateId = Guid.Empty;
        if (formatId is null || !formatId.StartsWith(FormatPrefix, StringComparison.Ordinal)) return false;
        var rest = formatId[FormatPrefix.Length..];
        return rest.Length == 36 && Guid.TryParseExact(rest, "D", out templateId) && templateId != Guid.Empty
            && string.Equals(rest, templateId.ToString("D"), StringComparison.Ordinal);
    }

    public static string Serialize(CustomBankTemplateDefinition d) => JsonSerializer.Serialize(d, Json);

    public static CustomBankTemplateDefinition? Deserialize(string json)
    {
        try { return JsonSerializer.Deserialize<CustomBankTemplateDefinition>(json, Json); }
        catch (JsonException) { return null; }
    }

    /// <summary>SHA-256 of the canonical (declaration-ordered) JSON: what a checker approves is bound to this.</summary>
    public static string Hash(CustomBankTemplateDefinition d) =>
        Convert.ToHexString(SHA256.HashData(new UTF8Encoding(false).GetBytes(Serialize(d)))).ToLowerInvariant();

    public static CustomBankTemplateDefinition Clone(CustomBankTemplateDefinition d) =>
        Deserialize(Serialize(d)) ?? new CustomBankTemplateDefinition();

    /// <summary>Encoder that THROWS on a character the code page cannot hold (no <c>?</c> substitution).</summary>
    public static System.Text.Encoding? StrictEncoding(string? name)
    {
        switch (name)
        {
            case Utf8: return new UTF8Encoding(false, throwOnInvalidBytes: true);
            case Utf8Bom: return new UTF8Encoding(true, throwOnInvalidBytes: true);
            case Windows1256:
                System.Text.Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
                return System.Text.Encoding.GetEncoding(1256, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
            default: return null;
        }
    }

    // ── Definition validation (layout sanity; row data is judged at generate time) ─────────────────

    public sealed record DefinitionCheck(IReadOnlyList<SaudiBankExportIssueDto> Errors, IReadOnlyList<SaudiBankExportWarningDto> Warnings)
    {
        public bool Ok => Errors.Count == 0;
    }

    public static DefinitionCheck Validate(CustomBankTemplateDefinition? d)
    {
        var errors = new List<SaudiBankExportIssueDto>();
        var warnings = new List<SaudiBankExportWarningDto>();
        void Err(string code, string message, string? field = null) => errors.Add(new(code, message, null, field));
        if (d is null) { Err("template_required", "The template is required.", "template"); return new(errors, warnings); }

        if (string.IsNullOrWhiteSpace(d.Name) || d.Name.Length > 80 || d.Name.Any(char.IsControl) || d.Name.Any(IsHiddenChar))
            Err("template_name_invalid", "The template needs a name of 1 to 80 characters.", "name");
        if ((d.SampleReference ?? string.Empty).Length > 200 || (d.SampleReference ?? string.Empty).Any(char.IsControl))
            Err("sample_reference_invalid", "The sample reference is at most 200 characters, on one line.", "sampleReference");
        if (!Layouts.Contains(d.Layout)) Err("layout_unsupported", "Layout must be delimited or fixed-width.", "layout");
        if (!Encodings.Contains(d.Encoding)) Err("encoding_unsupported", "Encoding must be utf-8, utf-8-bom or windows-1256.", "encoding");
        if (!LineEndings.Contains(d.LineEnding)) Err("line_ending_unsupported", "Line ending must be crlf or lf.", "lineEnding");
        if (!Extensions.Contains(d.FileExtension)) Err("file_extension_unsupported", "File extension must be csv, txt or dat.", "fileExtension");
        var delimited = d.Layout == Delimited;
        if (delimited)
        {
            if (!Delimiters.Contains(d.Delimiter)) Err("delimiter_unsupported", "Delimiter must be a comma, semicolon, pipe or tab.", "delimiter");
            if (!QuoteRules.Contains(d.QuoteRule)) Err("quote_rule_unsupported", "Quote rule must be none, minimal or always.", "quoteRule");
        }

        d.HeaderRecord ??= new(); d.Columns ??= new(); d.TrailerRecord ??= new();
        if (d.Columns.Count == 0) Err("columns_required", "Add at least one detail column.", "columns");
        foreach (var (name, list, detail) in new[] { ("headerRecord", d.HeaderRecord, false), ("columns", d.Columns, true), ("trailerRecord", d.TrailerRecord, false) })
        {
            if (list.Count > MaxColumns) { Err("too_many_columns", $"At most {MaxColumns} columns per record.", name); continue; }
            for (var i = 0; i < list.Count; i++) ValidateColumn(d, list[i], detail, $"{name}[{i}]", errors);
        }

        if (d.Columns.Count > 0)
        {
            // Exactly once each: the file's money, payee and identity must each have ONE slot the bank reads.
            foreach (var (key, label) in new[] { (CustomBankFields.BeneficiaryIban, "beneficiary IBAN"), (CustomBankFields.NetPay, "net pay"), (CustomBankFields.NationalId, "national ID/Iqama") })
            {
                var n = d.Columns.Count(c => c.Field == key);
                if (n == 0) Err("required_column_missing", $"The detail line must include the {label}.", "columns");
                else if (n > 1) Err("column_duplicated", $"The detail line must include the {label} exactly once.", "columns");
            }
        }
        if (d.Layout == FixedWidth)
            foreach (var (name, list) in new[] { ("headerRecord", d.HeaderRecord), ("columns", d.Columns), ("trailerRecord", d.TrailerRecord) })
                if (list.Sum(c => c?.Width ?? 0) > MaxRecordWidth)
                    Err("record_too_wide", $"A fixed-width record is at most {MaxRecordWidth} bytes wide.", name);
        if (errors.Count == 0 && StrictEncoding(d.Encoding) is { } enc)
        {
            // A literal or title the code page cannot hold would fail every generate; say so now.
            foreach (var text in d.HeaderRecord.Concat(d.Columns).Concat(d.TrailerRecord).SelectMany(c => new[] { c.Literal, c.Title }).Concat(new[] { d.Name }))
            {
                if (string.IsNullOrEmpty(text)) continue;
                try { enc.GetBytes(text); }
                catch (EncoderFallbackException) { Err("text_not_encodable", "A literal or title contains a character the chosen encoding cannot represent.", "encoding"); break; }
            }
        }
        return new(errors, warnings);
    }

    private static void ValidateColumn(CustomBankTemplateDefinition d, CustomBankColumn c, bool detail, string at, List<SaudiBankExportIssueDto> errors)
    {
        void Err(string code, string message) => errors.Add(new(code, message, null, at));
        if (c is null) { Err("column_invalid", "Empty column."); return; }
        var entry = CustomBankFields.Find(c.Field);
        if (entry is null) { Err("field_unknown", "This field is not in the catalog."); return; }
        if (detail ? !entry.Detail : !entry.Envelope)
        {
            Err("field_not_allowed_here", detail ? "This field is only valid in the header or trailer record." : "This field is only valid in the detail lines.");
            return;
        }
        var title = c.Title ?? string.Empty;
        if (title.Length > 60 || title.Any(char.IsControl) || title.Any(IsHiddenChar)) Err("title_invalid", "The column title is at most 60 characters, on one line, without invisible characters.");
        else if (IbanLike.IsMatch(title)) Err("title_looks_like_account", "A title must not look like an IBAN or account number.");
        if (c.Field == CustomBankFields.Literal)
        {
            if (string.IsNullOrEmpty(c.Literal)) Err("literal_required", "A literal column needs its text.");
            else
            {
                if (c.Literal.Length > 60 || c.Literal.Any(char.IsControl) || c.Literal.Any(IsHiddenChar)) Err("literal_invalid", "A literal is at most 60 characters, on one line, without invisible characters.");
                if (IbanLike.IsMatch(c.Literal)) Err("literal_looks_like_account", "A literal must not look like an IBAN or account number (SA followed by digits, or a run of more than 4 digits).");
                if (!string.Equals(Csv.Guard(c.Literal), c.Literal, StringComparison.Ordinal))
                    Err("literal_formula_prefix", "A literal must not start with =, +, - or @.");
            }
        }
        else if (!string.IsNullOrEmpty(c.Literal)) Err("literal_not_allowed", "Only a literal column carries literal text.");

        if (c.MaxLength is { } ml && (ml < 1 || ml > 500)) Err("max_length_invalid", "Maximum length must be 1 to 500.");
        if (!new[] { "left", "right" }.Contains(c.Align)) Err("align_invalid", "Alignment must be left or right.");
        if (c.Pad is null || c.Pad.Length != 1 || c.Pad[0] < ' ' || c.Pad[0] > '~') Err("pad_invalid", "Padding must be one printable ASCII character.");
        else if (char.IsAsciiDigit(c.Pad[0]) && !(d.Layout == FixedWidth && entry.Kind is CustomBankFields.KindAmount or CustomBankFields.KindInteger))
            Err("pad_digit_not_allowed", "A digit may pad only an amount or count column in a fixed-width file.");
        // Settings that do not apply to a column must stay at their defaults, so nothing hidden is configured.
        if (entry.Kind != CustomBankFields.KindAmount && (c.AmountDecimals != 2 || c.DecimalSeparator != "." || c.ThousandsSeparator != string.Empty))
            Err("option_not_applicable", "Decimal and thousands settings apply to amount columns only.");
        if (entry.Kind != CustomBankFields.KindDate && c.DateFormat != "yyyyMMdd")
            Err("option_not_applicable", "A date format applies to date columns only.");
        if (d.Layout == Delimited && (c.Width is not null || c.Align != "left" || c.Pad != " "))
            Err("option_not_applicable", "Width, alignment and padding apply to fixed-width files only.");
        if (entry.Kind == CustomBankFields.KindAmount)
        {
            if (c.AmountDecimals is < 0 or > 4) Err("decimals_invalid", "Decimals must be 0 to 4.");
            if (!DecimalSeparators.Contains(c.DecimalSeparator)) Err("decimal_separator_invalid", "Decimal separator must be a dot, a comma or empty.");
            if (!ThousandsSeparators.Contains(c.ThousandsSeparator)) Err("thousands_separator_invalid", "Thousands separator must be empty, a comma, a dot or a space.");
            else if (c.ThousandsSeparator.Length > 0 && c.ThousandsSeparator == c.DecimalSeparator)
                Err("separator_conflict", "The thousands and decimal separators must differ.");
            else if (c.DecimalSeparator.Length == 0 && c.ThousandsSeparator.Length > 0)
                Err("separator_conflict", "Minor-unit amounts (no decimal separator) cannot use a thousands separator.");
        }
        if (entry.Kind == CustomBankFields.KindDate && !DateFormats.Contains(c.DateFormat)) Err("date_format_invalid", "Choose a date format from the list.");
        if (d.Layout == FixedWidth)
        {
            if (c.Width is not { } w || w < 1 || w > 500) Err("width_required", "Fixed-width columns need a width of 1 to 500.");
            else if (c.MaxLength is { } m && m > w) Err("max_length_exceeds_width", "Maximum length cannot exceed the column width.");
        }
        else if (d.Layout == Delimited && d.QuoteRule == QuoteNone)
        {
            if (entry.Kind == CustomBankFields.KindAmount && (c.DecimalSeparator == d.Delimiter || c.ThousandsSeparator == d.Delimiter))
                Err("separator_conflicts_with_delimiter", "An amount separator equals the delimiter and quoting is off; choose another separator or turn quoting on.");
        }
    }
}
