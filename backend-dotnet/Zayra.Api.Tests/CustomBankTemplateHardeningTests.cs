using System.Diagnostics;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Zayra.Api.Infrastructure.Payroll;
using Zayra.Api.Infrastructure.Payroll.SaudiBankExports;

namespace Zayra.Api.Tests;

/// <summary>Attacks on the template layer: a template must never make the file say something other than the
/// batch says, and must stay inside its resource bounds. Synthetic data only.</summary>
public class CustomBankTemplateHardeningTests
{
    private static CustomBankColumn C(string field, Action<CustomBankColumn>? tweak = null)
    {
        var c = new CustomBankColumn { Field = field };
        tweak?.Invoke(c);
        return c;
    }

    private static CustomBankTemplateDefinition Delim(params CustomBankColumn[] cols) =>
        new() { Name = "T", Layout = CustomBankTemplates.Delimited, Columns = cols.ToList() };

    private static CustomBankTemplateDefinition Fix(params CustomBankColumn[] cols) =>
        new() { Name = "T", Layout = CustomBankTemplates.FixedWidth, FileExtension = "txt", Columns = cols.ToList() };

    private static CustomBankColumn[] Core() => new[]
    {
        C(CustomBankFields.NationalId), C(CustomBankFields.BeneficiaryIban), C(CustomBankFields.NetPay),
    };

    private static CustomBankColumn[] FixedCore() => new[]
    {
        C(CustomBankFields.NationalId, c => c.Width = 10), C(CustomBankFields.BeneficiaryIban, c => c.Width = 24),
        C(CustomBankFields.NetPay, c => { c.Width = 10; c.Align = "right"; c.Pad = "0"; c.DecimalSeparator = ""; }),
    };

    private static IReadOnlyList<string> Codes(CustomBankTemplateDefinition t) => CustomBankTemplates.Validate(t).Errors.Select(e => e.Code).ToList();

    private static CustomBankRow[] Rows() => new[] { CustomBankFileGeneratorTests.Row1(), CustomBankFileGeneratorTests.Row2() };

    private static string[] LinesOf(CustomBankFileResult r) =>
        Encoding.UTF8.GetString(r.File!.Content).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);

    // ── P1: the file says what the batch says ──────────────────────────────────────────────────────

    [Theory]
    [InlineData(CustomBankFields.BeneficiaryIban)]
    [InlineData(CustomBankFields.NetPay)]
    [InlineData(CustomBankFields.NationalId)]
    public void PayeeMoneyAndIdentity_MustAppearExactlyOnce(string field)
    {
        var missing = Core().Where(c => c.Field != field).ToArray();
        Codes(Delim(missing)).Should().Contain("required_column_missing");
        var twice = Core().Append(C(field)).ToArray();
        Codes(Delim(twice)).Should().Contain("column_duplicated");
    }

    [Theory]
    [InlineData("SA0380000000608010167519")]
    [InlineData("sa03")]
    [InlineData("12345")]
    [InlineData("ref 987654321")]
    [InlineData("١٢٣٤٥")]   // Arabic-Indic digits
    public void Literal_ThatLooksLikeAnIbanOrAccount_IsRefused(string literal)
    {
        var t = Delim(Core()); t.Columns.Add(C(CustomBankFields.Literal, c => c.Literal = literal));
        Codes(t).Should().Contain("literal_looks_like_account");
        var titled = Delim(Core()); titled.Columns[0].Title = literal;
        Codes(titled).Should().Contain("title_looks_like_account");
    }

    [Fact]
    public void Literal_WithAtMostFourDigits_IsFine()
    {
        var t = Delim(Core()); t.Columns.Add(C(CustomBankFields.Literal, c => c.Literal = "V1 2024"));
        CustomBankTemplates.Validate(t).Ok.Should().BeTrue();
    }

    [Fact]
    public void ReadBack_AcceptsAnHonestFile_Delimited_AndFixedWidth()
    {
        foreach (var t in new[] { Delim(Core()), Fix(FixedCore()) })
        {
            var r = CustomBankFileGenerator.Generate(t, CustomBankFileGeneratorTests.Ctx(), Rows());
            r.Ok.Should().BeTrue(string.Join("; ", r.Errors.Select(e => e.Code)));
        }
    }

    [Fact]
    public void ReadBack_CatchesASwappedIban_Delimited()
    {
        var t = Delim(Core());
        var lines = LinesOf(CustomBankFileGenerator.Generate(t, CustomBankFileGeneratorTests.Ctx(), Rows()));
        lines[0] = lines[0].Replace(CustomBankFileGeneratorTests.IbanA, CustomBankFileGeneratorTests.IbanB);
        var errs = CustomBankFileGenerator.VerifyRendered(t, lines, Rows(), 11550.50m);
        errs.Should().Contain(e => e.Code == "render_mismatch_iban" && e.EmployeeId == 1);
    }

    [Fact]
    public void ReadBack_CatchesAnotherAmountInTheNetSlot_AndAWrongId()
    {
        var t = Delim(Core());
        var lines = LinesOf(CustomBankFileGenerator.Generate(t, CustomBankFileGeneratorTests.Ctx(), Rows()));
        var cells = lines[1].Split(',');
        cells[2] = "5000.00";   // a basic-salary figure where the bank reads net pay
        cells[0] = "2999999999";
        lines[1] = string.Join(",", cells);
        var errs = CustomBankFileGenerator.VerifyRendered(t, lines, Rows(), 11550.50m);
        errs.Should().Contain(e => e.Code == "render_mismatch_amount" && e.EmployeeId == 2);
        errs.Should().Contain(e => e.Code == "render_mismatch_id" && e.EmployeeId == 2);
    }

    [Fact]
    public void ReadBack_CatchesAShiftedColumn_Unparseable()
    {
        var t = Delim(Core());
        var lines = LinesOf(CustomBankFileGenerator.Generate(t, CustomBankFileGeneratorTests.Ctx(), Rows()));
        lines[0] += ",extra";
        CustomBankFileGenerator.VerifyRendered(t, lines, Rows(), 11550.50m).Should().Contain(e => e.Code == "render_unparseable");
    }

    [Fact]
    public void ReadBack_CatchesATotalThatDoesNotAddUp()
    {
        var t = Delim(Core());
        var lines = LinesOf(CustomBankFileGenerator.Generate(t, CustomBankFileGeneratorTests.Ctx(), Rows()));
        CustomBankFileGenerator.VerifyRendered(t, lines, Rows(), 99m).Should().Contain(e => e.Code == "render_total_mismatch");
    }

    [Fact]
    public void ReadBack_FixedWidth_CatchesTamperedSlices()
    {
        var t = Fix(FixedCore());
        var lines = LinesOf(CustomBankFileGenerator.Generate(t, CustomBankFileGeneratorTests.Ctx(), Rows()));
        lines[0] = lines[0][..10] + CustomBankFileGeneratorTests.IbanB + lines[0][34..];
        lines[1] = lines[1][..34] + "0000999999";
        var errs = CustomBankFileGenerator.VerifyRendered(t, lines, Rows(), 11550.50m);
        errs.Should().Contain(e => e.Code == "render_mismatch_iban" && e.EmployeeId == 1);
        errs.Should().Contain(e => e.Code == "render_mismatch_amount" && e.EmployeeId == 2);
        lines[0] = lines[0][..^1];   // a short line
        CustomBankFileGenerator.VerifyRendered(t, lines, Rows(), 11550.50m).Should().Contain(e => e.Code == "render_unparseable");
    }

    [Fact]
    public void ReadBack_HandlesQuotedValuesWithDelimitersAndQuotes_AndThousandsSeparators()
    {
        var t = Delim(C(CustomBankFields.NationalId), C(CustomBankFields.BeneficiaryIban), C(CustomBankFields.NetPay, c => { c.DecimalSeparator = ","; c.ThousandsSeparator = "."; }),
            C(CustomBankFields.NameAr, c => c.AllowBlank = true));
        var rows = new[] { new CustomBankRow(CustomBankFileGeneratorTests.Ksa1(), "E1", "a,\"b\" c"), CustomBankFileGeneratorTests.Row2("x") };
        var r = CustomBankFileGenerator.Generate(t, CustomBankFileGeneratorTests.Ctx(), rows);
        r.Ok.Should().BeTrue(string.Join("; ", r.Errors.Select(e => e.Code)));
        Encoding.UTF8.GetString(r.File!.Content).Should().Contain("6.300,00").And.Contain("\"a,\"\"b\"\" c\"");
    }

    // ── P2-1: padding and alignment ────────────────────────────────────────────────────────────────

    [Fact]
    public void DigitPad_OnATextColumn_IsRefused()
    {
        var cols = FixedCore(); cols[0].Pad = "0"; cols[0].Align = "right";
        Codes(Fix(cols)).Should().Contain("pad_digit_not_allowed");
        var name = FixedCore().Append(C(CustomBankFields.NameEn, c => { c.Width = 20; c.Pad = "7"; })).ToArray();
        Codes(Fix(name)).Should().Contain("pad_digit_not_allowed");
    }

    [Fact]
    public void DigitPad_InADelimitedFile_IsRefused()
    {
        var cols = Core(); cols[2].Pad = "0";
        Codes(Delim(cols)).Should().Contain(new[] { "pad_digit_not_allowed", "option_not_applicable" });
    }

    [Fact]
    public void LeftAlignedMinorUnitsWithZeroPad_AreWrittenRightAligned_AndReadBackCleanly()
    {
        var cols = FixedCore(); cols[2].Align = "left";   // would put "6300" + zeros = 630000000 (x10000)
        var t = Fix(cols);
        CustomBankTemplates.Validate(t).Ok.Should().BeTrue();
        var r = CustomBankFileGenerator.Generate(t, CustomBankFileGeneratorTests.Ctx(), Rows());
        r.Ok.Should().BeTrue(string.Join("; ", r.Errors.Select(e => e.Code)));
        var lines = LinesOf(r);
        lines[0].Should().EndWith("0000630000", "an amount is never left-aligned in a fixed-width record");
        lines[1].Should().EndWith("0000525050");
    }

    // ── P2-2: resource bounds ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void FixedWidthRecord_WiderThan2000Bytes_IsRefused()
    {
        var cols = FixedCore(); cols[0].Width = 1990;
        Codes(Fix(cols)).Should().Contain("record_too_wide");
    }

    private static List<CustomBankRow> Many(int n, Func<int, string>? name = null)
    {
        var list = new List<CustomBankRow>(n);
        for (var i = 1; i <= n; i++)
        {
            var iban = IbanValidator.WithValidCheckDigits("SA0080" + i.ToString("D18"));
            var id = "1" + i.ToString("D9");
            list.Add(new CustomBankRow(new KsaWageFileRow(i, $"E{i}", "Saudi", id, iban, "RJHISARI", name?.Invoke(i) ?? "Omar Test", 1m, 1m, 0m, 1m, 0m), $"E{i}", null));
        }
        return list;
    }

    private static CustomBankFileContext CtxFor(int n) =>
        CustomBankFileGeneratorTests.Ctx(total: n, count: n);

    [Fact]
    public void ErrorCollection_IsBounded_AndFast_ForAHugeBadBatch()
    {
        var rows = Many(5000, _ => "=bad");   // every row trips the name rule
        var sw = Stopwatch.StartNew();
        var r = CustomBankFileGenerator.Generate(Delim(Core()), CtxFor(5000), rows);
        sw.Stop();
        r.File.Should().BeNull();
        r.Errors.Count.Should().BeLessThanOrEqualTo(501);
        r.Errors.Should().Contain(e => e.Code == "more_errors");
        sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public void OutputLargerThan25Mb_FailsEarly()
    {
        var cols = FixedCore().Concat(Enumerable.Range(0, 3).Select(_ => C(CustomBankFields.Literal, c => { c.Literal = "X"; c.Width = 480; }))).ToArray();   // 1,484 bytes a line x 20,000
        var r = CustomBankFileGenerator.Generate(Fix(cols), CtxFor(20000), Many(20000));
        r.File.Should().BeNull();
        r.Errors.Should().Contain(e => e.Code == "file_too_large");
    }

    [Fact]
    public void PreviewOnly_RendersFiveRows_ButStillRunsTheSaudiRulesOnAll()
    {
        var t = Delim(Core().Append(C(CustomBankFields.NameEn, c => c.MaxLength = 5)).ToArray());
        var rows = Many(8, i => i == 7 ? "Long Name Here" : "Omar");   // only row 7 overflows the layout
        var preview = CustomBankFileGenerator.Generate(t, CtxFor(8), rows, previewOnly: true);
        preview.File.Should().BeNull();
        preview.Errors.Should().BeEmpty("row 7 is beyond the five rows a preview lays out");
        CustomBankFileGenerator.Generate(t, CtxFor(8), rows).Errors.Should().Contain(e => e.Code == "value_too_long" && e.EmployeeId == 7);

        var bad = Many(8); bad[6] = bad[6] with { Ksa = bad[6].Ksa with { MolId = "123" } };
        CustomBankFileGenerator.Generate(t, CtxFor(8), bad, previewOnly: true).Errors.Should().Contain(e => e.Code == "mol_id_invalid" && e.EmployeeId == 7);
    }

    // ── P3 ─────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void OptionsThatDoNotApply_MustStayAtDefaults()
    {
        var a = Core(); a[0].DecimalSeparator = ",";
        Codes(Delim(a)).Should().Contain("option_not_applicable");
        var b = Core(); b[1].DateFormat = "ddMMyyyy";
        Codes(Delim(b)).Should().Contain("option_not_applicable");
        var c = Core(); c[0].Width = 10;
        Codes(Delim(c)).Should().Contain("option_not_applicable");
        var d = Core(); d[0].ThousandsSeparator = ",";
        Codes(Delim(d)).Should().Contain("option_not_applicable");
    }

    [Theory]
    [InlineData("\u200E")]   // left-to-right mark
    [InlineData("\u202E")]   // right-to-left override
    [InlineData("\u200B")]   // zero-width space
    [InlineData("\u2028")]   // line separator
    [InlineData("\u2029")]   // paragraph separator
    public void InvisibleCharacters_AreRefused_InLiteralsTitlesNamesAndCells(string ch)
    {
        var lit = Delim(Core()); lit.Columns.Add(C(CustomBankFields.Literal, c => c.Literal = "AB" + ch + "CD"));
        Codes(lit).Should().Contain("literal_invalid");
        var title = Delim(Core()); title.Columns[0].Title = "T" + ch;
        Codes(title).Should().Contain("title_invalid");
        var name = Delim(Core()); name.Name = "N" + ch;
        Codes(name).Should().Contain("template_name_invalid");

        var t = Delim(Core().Append(C(CustomBankFields.NameAr)).ToArray());
        var rows = new[] { CustomBankFileGeneratorTests.Row1("عمر" + ch), CustomBankFileGeneratorTests.Row2("سارة") };
        CustomBankFileGenerator.Generate(t, CustomBankFileGeneratorTests.Ctx(), rows).Errors
            .Should().Contain(e => (e.Code == "value_hidden_character" || e.Code == "value_control_character"));
    }

    [Fact]
    public void ActiveVersion_MustBeApproved()
    {
        var rec = new CustomBankTemplateRecord
        {
            ActiveVersion = 1,
            Versions = { new CustomBankTemplateVersion { Version = 1, Status = CustomBankTemplates.Pending } },
        };
        rec.Active.Should().BeNull("a pending version is never the active one, even if ActiveVersion points at it");
        rec.Versions[0].Status = CustomBankTemplates.Approved;
        rec.Active.Should().NotBeNull();
    }

    [Fact]
    public void DefinitionHash_IsStable_AndChangesWithAnyEdit()
    {
        var t = Delim(Core());
        var h = CustomBankTemplates.Hash(t);
        CustomBankTemplates.Hash(CustomBankTemplates.Clone(t)).Should().Be(h);
        t.Delimiter = ";";
        CustomBankTemplates.Hash(t).Should().NotBe(h);
    }
}
