using System.Text;
using FluentAssertions;
using Zayra.Api.Infrastructure.Payroll;
using Zayra.Api.Infrastructure.Payroll.SaudiBankExports;

namespace Zayra.Api.Tests;

/// <summary>Pure-generator tests for customer-configured bank salary files. All identifiers are synthetic.</summary>
public class CustomBankFileGeneratorTests
{
    internal static readonly string IbanA = IbanValidator.WithValidCheckDigits("SA0080000000608010167519");   // 80 = RJHI
    internal static readonly string IbanB = IbanValidator.WithValidCheckDigits("SA0030000012345678901234");   // 30 = ARNB

    internal static KsaWageFileRow Ksa1() => new(1, "E1", "Saudi", "1012345678", IbanA, "RJHISARI", "Omar Test",
        6750m, 5000m, 1250m, 6300m, 450m);
    internal static KsaWageFileRow Ksa2() => new(2, "E2", "Egyptian", "2012345678", IbanB, "ARNBSARI", "Sara Test",
        5250.50m, 4000.50m, 1000m, 5250.50m, 0m);

    internal static CustomBankRow Row1(string? ar = null) => new(Ksa1(), "E1", ar);
    internal static CustomBankRow Row2(string? ar = null) => new(Ksa2(), "E2", ar);

    internal static CustomBankFileContext Ctx(decimal total = 11550.50m, int count = 2, string? debit = null) =>
        new("7001234567", "SAR", "2026092601", new DateOnly(2026, 9, 26), new DateOnly(2026, 10, 1), debit, "Payroll Sep 2026", total, count);

    private static CustomBankColumn C(string field, string title = "", Action<CustomBankColumn>? tweak = null)
    {
        var c = new CustomBankColumn { Field = field, Title = title };
        tweak?.Invoke(c);
        return c;
    }

    private static CustomBankTemplateDefinition Delimited(params CustomBankColumn[] cols) => new()
    {
        Name = "Test layout", Layout = CustomBankTemplates.Delimited, Columns = cols.ToList(),
    };

    private static CustomBankTemplateDefinition Fixed(params CustomBankColumn[] cols) => new()
    {
        Name = "Test fixed", Layout = CustomBankTemplates.FixedWidth, FileExtension = "txt", Columns = cols.ToList(),
    };

    // A property, not a field: every use gets fresh column objects, so one test cannot mutate another's layout.
    private static CustomBankColumn[] Basic => new[]
    {
        C(CustomBankFields.NationalId), C(CustomBankFields.NameEn), C(CustomBankFields.BeneficiaryBankCode),
        C(CustomBankFields.BeneficiaryIban), C(CustomBankFields.NetPay),
    };

    private static CustomBankFileResult Run(CustomBankTemplateDefinition t, IReadOnlyList<CustomBankRow>? rows = null, CustomBankFileContext? ctx = null) =>
        CustomBankFileGenerator.Generate(t, ctx ?? Ctx(), rows ?? new[] { Row2(), Row1() });

    // ── Byte-for-byte ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Delimited_HeaderTrailerAndDetail_AreByteExact_WithTotalsAndCount()
    {
        var t = Delimited(Basic);
        t.HeaderRecord = new()
        {
            C(CustomBankFields.Literal, "", c => c.Literal = "HDR"), C(CustomBankFields.EstablishmentId),
            C(CustomBankFields.ValueDate, "", c => c.DateFormat = "yyyyMMdd"), C(CustomBankFields.RecordCount),
        };
        t.TrailerRecord = new()
        {
            C(CustomBankFields.Literal, "", c => c.Literal = "TRL"), C(CustomBankFields.RecordCount), C(CustomBankFields.TotalAmount),
        };

        var r = Run(t);

        r.Ok.Should().BeTrue(string.Join("; ", r.Errors.Select(e => e.Code + ":" + e.Message)));
        r.File!.Name.Should().Be("salary-2026092601.csv");
        r.File.Content.Take(3).Should().NotEqual(new byte[] { 0xEF, 0xBB, 0xBF });
        Encoding.UTF8.GetString(r.File.Content).Should().Be(
            "HDR,7001234567,20261001,2\r\n"
            + $"1012345678,Omar Test,RJHI,{IbanA},6300.00\r\n"
            + $"2012345678,Sara Test,ARNB,{IbanB},5250.50\r\n"
            + "TRL,2,11550.50\r\n");
        r.RecordCount.Should().Be(2);
        r.TotalAmount.Should().Be(11550.50m);
    }

    [Fact]
    public void Delimited_TitleRow_Bom_Lf_NoFinalBreak_Semicolon_AlwaysQuoted()
    {
        var t = Delimited(C(CustomBankFields.NationalId, "ID"), C(CustomBankFields.NetPay, "Net", c => { c.DecimalSeparator = ","; c.ThousandsSeparator = "."; }));
        t.Columns.Add(C(CustomBankFields.BeneficiaryIban, "IBAN"));
        t.Delimiter = ";"; t.QuoteRule = CustomBankTemplates.QuoteAlways; t.HeaderRow = true;
        t.Encoding = CustomBankTemplates.Utf8Bom; t.LineEnding = CustomBankTemplates.Lf; t.EndWithLineBreak = false;

        var r = Run(t, new[] { Row1(), Row2() });

        r.Ok.Should().BeTrue(string.Join("; ", r.Errors.Select(e => e.Code)));
        r.File!.Content.Take(3).Should().Equal(0xEF, 0xBB, 0xBF);
        Encoding.UTF8.GetString(r.File.Content, 3, r.File.Content.Length - 3).Should().Be(
            "\"ID\";\"Net\";\"IBAN\"\n"
            + $"\"1012345678\";\"6.300,00\";\"{IbanA}\"\n"
            + $"\"2012345678\";\"5.250,50\";\"{IbanB}\"");
    }

    [Fact]
    public void FixedWidth_Windows1256_ArabicName_IsByteExact()
    {
        var t = Fixed(
            C(CustomBankFields.NationalId, "", c => c.Width = 10),
            C(CustomBankFields.NameAr, "", c => c.Width = 12),
            C(CustomBankFields.BeneficiaryIban, "", c => c.Width = 24),
            C(CustomBankFields.NetPay, "", c => { c.Width = 10; c.Align = "right"; c.Pad = "0"; c.DecimalSeparator = ""; }));
        t.Encoding = CustomBankTemplates.Windows1256;
        const string omar = "عمر";   // ع م ر

        var r = Run(t, new[] { Row1(omar) }, Ctx(6300m, 1));

        r.Ok.Should().BeTrue(string.Join("; ", r.Errors.Select(e => e.Code + ":" + e.Message)));
        var bytes = r.File!.Content;
        bytes.Skip(10).Take(3).Should().Equal(new byte[] { 0xDA, 0xE3, 0xD1 }, "ع م ر in windows-1256");
        bytes.Skip(13).Take(9).Should().OnlyContain(b => b == 0x20, "the name pads to 12 BYTES with spaces");
        var expected = Encoding.ASCII.GetBytes("1012345678").Concat(new byte[] { 0xDA, 0xE3, 0xD1 })
            .Concat(Encoding.ASCII.GetBytes(new string(' ', 9) + IbanA + "0000630000\r\n")).ToArray();
        bytes.Should().Equal(expected);
        r.File.Name.Should().EndWith(".txt");
    }

    [Fact]
    public void FixedWidth_RightAlignedZeroPaddedMinorUnits_AndBlankPadding()
    {
        var t = Fixed(
            C(CustomBankFields.NetPay, "", c => { c.Width = 12; c.Align = "right"; c.Pad = "0"; c.DecimalSeparator = ""; }),
            C(CustomBankFields.BeneficiaryBankCode, "", c => c.Width = 6),
            C(CustomBankFields.BeneficiaryIban, "", c => c.Width = 24));
        t.Columns.Insert(0, C(CustomBankFields.NationalId, "", c => c.Width = 10));

        var r = Run(t, new[] { Row2() }, Ctx(5250.50m, 1));

        r.Ok.Should().BeTrue(string.Join("; ", r.Errors.Select(e => e.Code)));
        Encoding.ASCII.GetString(r.File!.Content).Should().Be($"2012345678" + "000000525050" + "ARNB  " + IbanB + "\r\n");
    }

    [Fact]
    public void SameInput_SameBytesAndHash()
    {
        var t = Delimited(Basic);
        var a = Run(t); var b = Run(t, new[] { Row1(), Row2() });
        a.File!.Sha256.Should().Be(b.File!.Sha256);
        a.File.Content.Should().Equal(b.File.Content);
    }

    // ── No silent truncation / rounding / substitution ─────────────────────────────────────────────

    [Theory]
    [InlineData(CustomBankFields.BeneficiaryIban, 10)]
    [InlineData(CustomBankFields.NationalId, 6)]
    [InlineData(CustomBankFields.NameEn, 4)]
    public void MaxLength_FailsTheFile_NeverCutsAnIbanIdOrName(string field, int max)
    {
        var cols = Basic.Select(c => c.Field == field ? C(field, "", x => x.MaxLength = max) : C(c.Field)).ToArray();
        var r = Run(Delimited(cols));
        r.File.Should().BeNull();
        r.Errors.Should().Contain(e => e.Code == "value_too_long" && e.Field == field);
    }

    [Fact]
    public void FixedWidth_ValueWiderThanColumn_Fails()
    {
        var t = Fixed(C(CustomBankFields.NationalId, "", c => c.Width = 8), C(CustomBankFields.BeneficiaryIban, "", c => c.Width = 24), C(CustomBankFields.NetPay, "", c => c.Width = 10));
        var r = Run(t);
        r.File.Should().BeNull();
        r.Errors.Should().Contain(e => e.Code == "value_too_wide" && e.Field == CustomBankFields.NationalId);
    }

    [Fact]
    public void Amount_WithMoreDecimalsThanTheColumn_FailsInsteadOfRounding()
    {
        var t = Delimited(C(CustomBankFields.BeneficiaryIban), C(CustomBankFields.NetPay, "", c => c.AmountDecimals = 0), C(CustomBankFields.NationalId));
        var r = Run(t);
        r.File.Should().BeNull();
        r.Errors.Should().Contain(e => e.Code == "amount_precision_loss" && e.EmployeeId == 2);
        r.Errors.Should().NotContain(e => e.EmployeeId == 1, "6300.00 fits zero decimals exactly");
    }

    [Fact]
    public void Windows1256_UnrepresentableCharacter_FailsInsteadOfQuestionMark()
    {
        var t = Delimited(C(CustomBankFields.BeneficiaryIban), C(CustomBankFields.NetPay), C(CustomBankFields.NameAr), C(CustomBankFields.NationalId));
        t.Encoding = CustomBankTemplates.Windows1256;
        var r = Run(t, new[] { Row1("你好"), Row2("سارة") }, Ctx());
        r.File.Should().BeNull();
        r.Errors.Should().Contain(e => e.Code == "value_not_encodable" && e.EmployeeId == 1);
        r.Errors.Should().NotContain(e => e.EmployeeId == 2);

        t.Encoding = CustomBankTemplates.Utf8;
        Run(t, new[] { Row1("你好"), Row2("سارة") }).Ok.Should().BeTrue("UTF-8 holds both");
    }

    [Fact]
    public void BlankOptionalValue_IsRefused_UnlessTheColumnAllowsBlank()
    {
        var t = Delimited(C(CustomBankFields.BeneficiaryIban), C(CustomBankFields.NetPay), C(CustomBankFields.NameAr), C(CustomBankFields.NationalId));
        Run(t).Errors.Should().Contain(e => e.Code == "field_empty" && e.Field == CustomBankFields.NameAr);
        t.Columns[2].AllowBlank = true;
        Run(t).Ok.Should().BeTrue();
    }

    [Fact]
    public void QuoteNone_RefusesAValueThatNeedsQuoting_NeverBreaksTheRow()
    {
        var t = Delimited(C(CustomBankFields.BeneficiaryIban), C(CustomBankFields.NetPay), C(CustomBankFields.NameAr), C(CustomBankFields.NationalId));
        t.QuoteRule = CustomBankTemplates.QuoteNone;
        Run(t, new[] { Row1("عمر, أحمد"), Row2("سارة") }).Errors.Should().Contain(e => e.Code == "value_needs_quoting");

        t.QuoteRule = CustomBankTemplates.QuoteMinimal;
        var ok = Run(t, new[] { Row1("عمر, أحمد"), Row2("سارة") });
        Encoding.UTF8.GetString(ok.File!.Content).Should().Contain("\"عمر, أحمد\"");
    }

    // ── Saudi rules override the template ──────────────────────────────────────────────────────────

    public static IEnumerable<object[]> BadSaudiRows()
    {
        var ibanBad = IbanA[..^1] + (IbanA[^1] == '0' ? '1' : '0');   // breaks mod-97
        yield return new object[] { Ksa1() with { Iban = ibanBad }, "iban_checksum_failed" };
        yield return new object[] { Ksa1() with { Iban = "GB82WEST12345698765432" }, "iban_not_saudi" };
        yield return new object[] { Ksa1() with { Iban = IbanA[..20] }, "iban_length_invalid" };
        yield return new object[] { Ksa1() with { Iban = null }, "iban_missing" };
        yield return new object[] { Ksa1() with { MolId = "10123456" }, "mol_id_invalid" };
        yield return new object[] { Ksa1() with { MolId = "2012345678" }, "mol_id_prefix_mismatch" };
        yield return new object[] { Ksa1() with { Net = 0m, Gross = 450m, Basic = 450m, Housing = 0m }, "net_not_positive" };
        yield return new object[] { Ksa1() with { SlipDeductions = 400m }, "deductions_unreconciled" };
        yield return new object[] { Ksa1() with { Basic = 7000m }, "other_earnings_negative" };
        yield return new object[] { Ksa1() with { BankBic = "NCBKSARI" }, "bank_code_mismatch" };
        // A 16-digit ANB internal account passes the KSA rule (ANB's own path), but a custom bank file is IBAN-only.
        yield return new object[] { Ksa1() with { Iban = "0108057386290038", BankBic = "ARNBSARI" }, "iban_not_saudi" };
    }

    [Theory]
    [MemberData(nameof(BadSaudiRows))]
    public void SaudiRules_BlockTheFile_EvenWithAPermissiveTemplate(KsaWageFileRow bad, string code)
    {
        // The most permissive layout there is: every optional guard off, nothing but the pay line.
        var t = Delimited(C(CustomBankFields.EmployeeNumber), C(CustomBankFields.BeneficiaryIban, "", c => c.AllowBlank = true),
            C(CustomBankFields.NetPay), C(CustomBankFields.NationalId));
        var r = Run(t, new[] { new CustomBankRow(bad, "E1", null), Row2() }, Ctx(bad.Net + 5250.50m));
        r.File.Should().BeNull();
        r.Errors.Should().Contain(e => e.Code == code && e.EmployeeId == 1, string.Join("; ", r.Errors.Select(e => e.Code)));
    }

    [Fact]
    public void BatchTotalAndRecordCount_MustReconcile_ForTheFile()
    {
        var t = Delimited(Basic);
        Run(t, ctx: Ctx(total: 1m)).Errors.Should().Contain(e => e.Code == "total_mismatch");
        Run(t, ctx: Ctx(count: 3)).Errors.Should().Contain(e => e.Code == "record_count_mismatch");
    }

    [Fact]
    public void Currency_MustBeSar_AndEstablishmentValid_AndReferenceNotReused()
    {
        var t = Delimited(Basic);
        Run(t, ctx: Ctx() with { Currency = "USD" }).Errors.Should().Contain(e => e.Code == "currency_not_sar");
        Run(t, ctx: Ctx() with { EstablishmentId = "" }).Errors.Should().Contain(e => e.Code == "mol_establishment_id_missing");
        Run(t, ctx: Ctx() with { ReferencesAlreadyUsed = new[] { "2026092601" } }).Errors.Should().Contain(e => e.Code == "file_reference_reused");
        Run(t, ctx: Ctx() with { BatchReference = "lower-case" }).Errors.Should().Contain(e => e.Code == "file_reference_invalid");
    }

    [Fact]
    public void BeneficiaryBankCode_IsDerivedFromTheIban_AndBicIsCheckedAgainstIt()
    {
        var t = Delimited(C(CustomBankFields.BeneficiaryBankCode), C(CustomBankFields.BeneficiaryBic), C(CustomBankFields.BeneficiaryIban), C(CustomBankFields.NetPay), C(CustomBankFields.NationalId));
        var r = Run(t);
        r.Ok.Should().BeTrue();
        Encoding.UTF8.GetString(r.File!.Content).Should().StartWith("RJHI,RJHISARI,").And.Contain("\r\nARNB,ARNBSARI,");
    }

    [Fact]
    public void DebitAccount_UsedByATemplate_MustBeAValidSaudiIban()
    {
        var t = Delimited(Basic);
        t.HeaderRecord = new() { C(CustomBankFields.DebitAccountIban) };
        Run(t, ctx: Ctx(debit: null)).Errors.Should().Contain(e => e.Code == "debit_account_invalid");
        Run(t, ctx: Ctx(debit: "SA0380000000608010167518")).Errors.Should().Contain(e => e.Code == "debit_account_invalid");
        Run(t, ctx: Ctx(debit: IbanA)).Ok.Should().BeTrue();
    }

    // ── Formula / CSV injection ────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("=HYPERLINK(\"x\")")]
    [InlineData("+cmd")]
    [InlineData("-cmd")]
    [InlineData("@SUM(A1)")]
    [InlineData("  =cmd")]
    public void TextCell_StartingWithAFormulaTrigger_IsNeutralisedByTheSharedGuard(string name)
    {
        var t = Delimited(C(CustomBankFields.BeneficiaryIban), C(CustomBankFields.NetPay), C(CustomBankFields.NameAr), C(CustomBankFields.NationalId));
        var r = Run(t, new[] { Row1(name), Row2("سارة") });
        r.Ok.Should().BeTrue(string.Join("; ", r.Errors.Select(e => e.Code)));
        var line = Encoding.UTF8.GetString(r.File!.Content).Split("\r\n")[0];
        var cell = line[(line.IndexOf(',', line.IndexOf(',') + 1) + 1)..];
        cell.TrimStart('"').Should().StartWith("'", "the cell is prefixed so a spreadsheet shows text, not a formula");
    }

    [Fact]
    public void EnglishName_WithFormulaPrefix_IsRefusedByTheSaudiRule()
    {
        var bad = Ksa1() with { Name = "=cmd|' /C calc'!A0" };
        Run(Delimited(Basic), new[] { new CustomBankRow(bad, "E1", null), Row2() }).Errors
            .Should().Contain(e => e.Code == "name_formula_prefix");
    }

    [Fact]
    public void LiteralColumn_WithFormulaPrefix_IsRefusedWhenTheTemplateIsProposed()
    {
        var t = Delimited(Basic); t.Columns.Add(C(CustomBankFields.Literal, "", c => c.Literal = "=1+1"));
        CustomBankTemplates.Validate(t).Errors.Should().Contain(e => e.Code == "literal_formula_prefix");
    }

    [Fact]
    public void ControlCharacterInAText_FailsTheFile()
    {
        var t = Delimited(C(CustomBankFields.BeneficiaryIban), C(CustomBankFields.NetPay), C(CustomBankFields.NameAr), C(CustomBankFields.NationalId));
        Run(t, new[] { Row1("a\tb"), Row2("سارة") }).Errors.Should().Contain(e => e.Code == "value_control_character");
        Run(t, new[] { Row1("a\r\nb"), Row2("سارة") }).Errors.Should().Contain(e => e.Code == "value_control_character");
    }

    // ── Template validation ────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Validate_AcceptsTheStarter_AndRequiresIbanAndNet()
    {
        CustomBankTemplates.Validate(CustomBankTemplateStarters.Find(CustomBankTemplateStarters.GenericCsv)!.Definition).Ok.Should().BeTrue();
        CustomBankTemplates.Validate(Delimited(C(CustomBankFields.NetPay))).Errors.Should().Contain(e => e.Code == "required_column_missing");
        CustomBankTemplates.Validate(Delimited(C(CustomBankFields.BeneficiaryIban))).Errors.Should().Contain(e => e.Code == "required_column_missing");
    }

    [Fact]
    public void Validate_RefusesUnknownFieldWrongRecordAndMissingWidth()
    {
        var t = Delimited(Basic); t.Columns.Add(C("salary_formula"));
        CustomBankTemplates.Validate(t).Errors.Should().Contain(e => e.Code == "field_unknown");

        t = Delimited(Basic); t.Columns.Add(C(CustomBankFields.TotalAmount));
        CustomBankTemplates.Validate(t).Errors.Should().Contain(e => e.Code == "field_not_allowed_here");

        t = Delimited(Basic); t.HeaderRecord = new() { C(CustomBankFields.NetPay) };
        CustomBankTemplates.Validate(t).Errors.Should().Contain(e => e.Code == "field_not_allowed_here");

        var f = Fixed(C(CustomBankFields.BeneficiaryIban), C(CustomBankFields.NetPay, "", c => c.Width = 10));
        CustomBankTemplates.Validate(f).Errors.Should().Contain(e => e.Code == "width_required");
    }

    [Fact]
    public void Validate_RefusesOutOfVocabularyOptions()
    {
        var t = Delimited(Basic);
        t.Encoding = "utf-16"; t.Delimiter = "ab"; t.LineEnding = "cr"; t.FileExtension = "exe"; t.QuoteRule = "sometimes";
        var codes = CustomBankTemplates.Validate(t).Errors.Select(e => e.Code).ToList();
        codes.Should().Contain(new[] { "encoding_unsupported", "delimiter_unsupported", "line_ending_unsupported", "file_extension_unsupported", "quote_rule_unsupported" });

        t = Delimited(C(CustomBankFields.BeneficiaryIban), C(CustomBankFields.NetPay, "", c => { c.DecimalSeparator = ","; c.DateFormat = "yyyyMMdd"; }));
        t.QuoteRule = CustomBankTemplates.QuoteNone; t.Delimiter = ",";
        CustomBankTemplates.Validate(t).Errors.Should().Contain(e => e.Code == "separator_conflicts_with_delimiter");

        t = Delimited(Basic); t.Columns[4].MaxLength = 0;
        CustomBankTemplates.Validate(t).Errors.Should().Contain(e => e.Code == "max_length_invalid");
    }

    [Fact]
    public void Validate_LiteralNotEncodableInTheChosenCodePage_IsRefusedUpFront()
    {
        var t = Delimited(Basic); t.Encoding = CustomBankTemplates.Windows1256;
        t.Columns.Add(C(CustomBankFields.Literal, "", c => c.Literal = "你好"));
        CustomBankTemplates.Validate(t).Errors.Should().Contain(e => e.Code == "text_not_encodable");
    }

    [Fact]
    public void UnknownJsonMember_IsRejected_SoATemplateCannotCarryUnmodelledBehaviour()
    {
        CustomBankTemplates.Deserialize("{\"name\":\"x\",\"script\":\"1+1\"}").Should().BeNull();
    }

    [Fact]
    public void FormatId_ParsesOnlyTheExactCanonicalForm()
    {
        var id = Guid.NewGuid();
        CustomBankTemplates.TryParseFormatId($"custom:{id:D}", out var parsed).Should().BeTrue();
        parsed.Should().Be(id);
        CustomBankTemplates.TryParseFormatId($"CUSTOM:{id:D}", out _).Should().BeFalse();
        CustomBankTemplates.TryParseFormatId($"custom:{id:N}", out _).Should().BeFalse();
        CustomBankTemplates.TryParseFormatId($"custom:{id:D}".ToUpperInvariant(), out _).Should().BeFalse();
        CustomBankTemplates.TryParseFormatId($"custom:{Guid.Empty:D}", out _).Should().BeFalse();
        CustomBankTemplates.TryParseFormatId("custom:", out _).Should().BeFalse();
        CustomBankTemplates.TryParseFormatId("anb-connect-csv-v1", out _).Should().BeFalse();
        SaudiBankExportFormats.Find($"custom:{id:D}").Should().BeNull("custom ids are not in the static registry; they resolve per company");
    }

    // ── Preview rendering ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Preview_ShowsTheRealLayout_WithIbanAndIdMasked_AndOnlyFirstFiveRows()
    {
        var t = Delimited(Basic); t.HeaderRow = true;
        var rows = Enumerable.Range(1, 8).Select(i => new CustomBankRow(Ksa1() with
        {
            EmployeeRef = i, EmployeeLabel = $"E{i}",
            Iban = CustomBankFileGenerator.Redact(IbanA, 6), MolId = CustomBankFileGenerator.Redact("1012345678", 0),
        }, $"E{i}", null)).ToList();

        var (lines, errors) = CustomBankFileGenerator.Preview(t, Ctx(), rows, 5, 8, 50400m);

        errors.Should().BeEmpty();
        lines.Should().HaveCount(1 + 5);
        lines[1].Should().Be($"XXXXXXXXXX,Omar Test,RJHI,{IbanA[..6]}{new string('X', 18)},6300.00");
        string.Concat(lines).Should().NotContain(IbanA).And.NotContain("1012345678");
    }

    [Fact]
    public void Redact_KeepsLength_SoFixedWidthPreviewIsFaithful()
    {
        CustomBankFileGenerator.Redact(IbanA, 6)!.Length.Should().Be(IbanA.Length);
        CustomBankFileGenerator.Redact("1012345678", 0).Should().Be("XXXXXXXXXX");
        CustomBankFileGenerator.Redact(null, 0).Should().BeNull();
    }
}
