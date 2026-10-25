using System.Text;
using System.Text.RegularExpressions;
using FluentAssertions;
using Zayra.Api.Infrastructure.Finance;
using Zayra.Api.Models;
using static Zayra.Api.Tests.CostExportHarness;

namespace Zayra.Api.Tests;

/// <summary>
/// WP-D slice 2: the cost centre on journal exports (contract 2.11; T-055, T-056, T-057, T-060). The goldens in
/// Fixtures/JournalExports/*.costcentre.* were derived from the unchanged base goldens BEFORE the formatters changed;
/// the base goldens are never altered (T-055 stays in JournalExportFormatterGoldenTests).
/// </summary>
public class JournalExportCostCentreFormatTests
{
    private static readonly Guid CompanyA = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid CompanyB = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000002");
    private static readonly Guid TenantId = Guid.Parse("99999999-9999-9999-9999-999999999999");

    private static readonly string[] Carriers =
    {
        "generic-csv", "sap-s4-gje", "oracle-gl-interface-csv", "oracle-fusion-fbdi", "d365-fo-general-journal",
        "quickbooks-iif", "quickbooks-online-journal",
    };
    private static readonly string[] NonCarriers = { "odoo-account-move", "netsuite-journal-csv", "xero-manual-journal", "zoho-books-journal" };

    public static IEnumerable<object[]> CarrierKeys() => Carriers.Select(k => new object[] { k });
    public static IEnumerable<object[]> NonCarrierKeys() => NonCarriers.Select(k => new object[] { k });

    private static IJournalExportFormatter Formatter(string key) =>
        JournalExportFormatterGoldenTests.AllFormatters().Single(f => f.Key == key);

    /// <summary>The golden sample with cost centres on the P&amp;L lines: CC200 (no gl_segment) on the bonus expense and its
    /// reversal, CC100 with gl_segment 6100-OPS on the salary expense. Liabilities, bank and loans stay uncosted.</summary>
    private static JournalExportDocument CostedSample(IReadOnlyDictionary<string, string>? options = null)
    {
        FinanceGlEntry E(string id, Guid company, string module, Guid source, string sref, string evt, string dr, string cr,
            decimal amount, string desc, bool reversed = false, Guid? reversalOf = null,
            string? cc = null, string? name = null, string? seg = null) => new()
        {
            Id = Guid.Parse(id), TenantId = TenantId, CompanyId = company, SourceModule = module, SourceEntityId = source,
            SourceEntityRef = sref, EventType = evt, DebitAccount = dr, CreditAccount = cr, Amount = amount, Currency = "SAR",
            EntryDate = new DateOnly(2026, 8, 3), Period = "2026-07", Description = desc, IsReversed = reversed,
            ReversalOfEntryId = reversalOf, CostCenterId = cc is null ? null : Guid.NewGuid(), CostCenterCode = cc,
            CostCenterName = name, CostCenterGlSegment = seg,
        };
        var run = Guid.Parse("11111111-2222-3333-4444-555555555555");
        var loan = Guid.Parse("66666666-7777-8888-9999-000000000000");
        var bonus = Guid.Parse("12121212-3434-5656-7878-909090909090");
        var bonusEntry = Guid.Parse("e4000000-0000-0000-0000-000000000004");
        var entries = new List<FinanceGlEntry>
        {
            E("e1000000-0000-0000-0000-000000000001", CompanyA, "Payroll", run, "PR-2026-07", "Accrual",
                "5000 - Salaries", "", 10_000m, "July payroll, salaries", cc: "CC100", name: "Operations", seg: "6100-OPS"),
            E("e2000000-0000-0000-0000-000000000002", CompanyA, "Payroll", run, "PR-2026-07", "Accrual",
                "", "2100 - Salaries Payable", 10_000m, "رواتب شهر يوليو"),
            E("e3000000-0000-0000-0000-000000000003", CompanyA, "Loan", loan, "LN-2026-00001", "Disbursement",
                "1400 - Employee Loans Receivable", "1000 - Bank", 2_500m, "Loan LN-2026-00001 disbursement"),
            E(bonusEntry.ToString(), CompanyB, "Bonus", bonus, "BON-2026-0007", "BonusPayment",
                "5200 - Bonus Expense", "2300 - Bonus Payable", 1_200.505m, "Q2 bonus \"special\" award", reversed: true,
                cc: "CC200", name: "Sales"),
            E("e5000000-0000-0000-0000-000000000005", CompanyB, "Bonus", bonus, "BON-2026-0007", "Reversal",
                "2300 - Bonus Payable", "5200 - Bonus Expense", 1_200.505m, "Reversal of BON-2026-0007", reversalOf: bonusEntry,
                cc: "CC200", name: "Sales"),
        };
        string CodeOf(FinanceGlEntry e) => e.CompanyId == CompanyA ? "SA01" : "1010-555";
        string? ExternalOf(FinanceGlEntry e, string code) => e.CompanyId == CompanyA && code == "5000" ? "6100-SAL" : null;
        bool? PnL(FinanceGlEntry e, string code) => code.StartsWith('5');   // 5xxx Expense; 1xxx/2xxx balance sheet

        var lines = JournalExportBuilder.Expand(entries, CodeOf, ExternalOf, PnL);
        JournalExportBuilder.Verify(lines, entries, PnL).Ok.Should().BeTrue("the sample must itself tie out, per account and per cost centre");
        return new JournalExportDocument
        {
            TenantId = TenantId, TenantName = string.Empty, CompanyId = null, CompanyCode = "GROUP", CompanyName = string.Empty,
            Period = "2026-07", PayrollRunId = null, Currency = "SAR",
            GeneratedAtUtc = new DateTime(2026, 8, 3, 9, 30, 0, DateTimeKind.Utc), Lines = lines,
            ExportId = JournalExportFormatterGoldenTests.ExportId, Kind = JournalExportKinds.Full,
            Options = options ?? new Dictionary<string, string>(),
        };
    }

    private static IReadOnlyDictionary<string, string> OptionsFor(string key) => key switch
    {
        "oracle-gl-interface-csv" or "oracle-fusion-fbdi" => new Dictionary<string, string> { ["segments"] = "{company}|{account}|{costcenter}" },
        "d365-fo-general-journal" => new Dictionary<string, string> { ["dimensionFormat"] = "{account}-{costcenter}" },
        _ => new Dictionary<string, string>(),
    };

    private static string Fixture(string name) =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "JournalExports", name);

    // ── Expand: where the snapshot lands ──────────────────────────────────────────────────────────

    [Fact]
    public void Expand_PutsTheCostCentreOnTheProfitAndLossSideOfATwoSidedRow_AndOnTheOnlyLineOfAOneSidedRow()
    {
        var doc = CostedSample();

        var costed = doc.Lines.Where(l => l.CostCenterCode is not null).ToList();
        costed.Select(l => (l.AccountCode, l.Side, l.CostCenterCode)).Should().BeEquivalentTo(new[]
        {
            ("5200", "DR", "CC200"), ("5200", "CR", "CC200"), ("5000", "DR", "CC100"),
        });
        costed.Single(l => l.CostCenterCode == "CC100").CostCenterName.Should().Be("Operations");
        costed.Single(l => l.CostCenterCode == "CC100").CostCenterGlSegment.Should().Be("6100-OPS");
        costed.Single(l => l.CostCenterCode == "CC100").CostCentreErpCode.Should().Be("6100-OPS", "gl_segment wins when set");
        costed.First(l => l.CostCenterCode == "CC200").CostCentreErpCode.Should().Be("CC200", "else the code");
        doc.Lines.Where(l => l.AccountCode is "2300" or "2100" or "1400" or "1000").Should().OnlyContain(l => l.CostCenterCode == null);
        doc.HasCostCentres.Should().BeTrue();
    }

    [Fact]
    public void Expand_WithoutAClassification_KeepsTheSnapshotOnBothSidesOfATwoSidedRow()
    {
        var row = Row(TenantId, "BonusPayment", "5200 - Bonus Expense", "2300 - Bonus Payable", 5m, ccCode: "CC1", ccName: "N");
        var lines = JournalExportBuilder.Expand(new[] { row }, "SA01");

        lines.Should().OnlyContain(l => l.CostCenterCode == "CC1", "never drop a snapshot because the account type is unknown");
    }

    [Fact]
    public void HasCostCentres_IsFalseWhenNoLineCarriesOne()
    {
        var doc = Doc(new[] { Row(TenantId, "Accrual", "5000 - Salaries", "", 5m), Row(TenantId, "Accrual", "", "2100 - Payable", 5m) });

        doc.HasCostCentres.Should().BeFalse();
    }

    // ── Goldens (T-056) ───────────────────────────────────────────────────────────────────────────

    [Theory]
    [MemberData(nameof(CarrierKeys))]
    public void ACostedDocument_MatchesItsCostCentreGolden_ByteForByte(string key)
    {
        var formatter = Formatter(key);
        var bytes = formatter.Format(CostedSample(OptionsFor(key)));
        var path = Fixture($"{key}.costcentre.{formatter.Extension}");

        File.Exists(path).Should().BeTrue($"the cost-centre golden for {key} is written from the contract before the formatter");
        bytes.Should().Equal(File.ReadAllBytes(path), $"{key} must emit the reviewed cost-centre layout");
    }

    [Theory]
    [MemberData(nameof(NonCarrierKeys))]
    public void AFormatThatCannotCarryACostCentre_EmitsExactlyItsOrdinaryBytes(string key)
    {
        var formatter = Formatter(key);

        formatter.Format(CostedSample()).Should().Equal(formatter.Format(JournalExportFormatterGoldenTests.SampleDocument()),
            "no silent columns: an ERP template with no dimension field is unchanged");
    }

    [Theory]
    [MemberData(nameof(CarrierKeys))]
    public void WithoutAnyCostedLine_TheOutputIsTheOrdinaryGolden_EvenWithTheCostCentreOptionsSet(string key)
    {
        var formatter = Formatter(key);
        var plain = JournalExportFormatterGoldenTests.SampleDocument();
        var withOptions = new JournalExportDocument
        {
            TenantId = plain.TenantId, TenantName = plain.TenantName, CompanyId = plain.CompanyId, CompanyCode = plain.CompanyCode,
            CompanyName = plain.CompanyName, Period = plain.Period, PayrollRunId = plain.PayrollRunId, Currency = plain.Currency,
            GeneratedAtUtc = plain.GeneratedAtUtc, Lines = plain.Lines, ExportId = plain.ExportId, Kind = plain.Kind,
            Options = key.StartsWith("oracle") ? new Dictionary<string, string> { ["segments"] = "{company}|{account}" } : OptionsFor(key),
        };

        var expected = File.ReadAllBytes(Fixture($"{key}.{formatter.Extension}"));
        formatter.Format(withOptions).Should().Equal(expected, "byte-identical for a document with no costed line (T-055)");
    }

    // ── Disclosure ────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void EveryFormat_EitherCarriesTheCostCentre_OrSaysSoInItsDisclaimer()
    {
        foreach (var f in JournalExportFormatterGoldenTests.AllFormatters())
        {
            f.Disclaimer.ToLowerInvariant().Should().Contain("cost centre", $"{f.Key} must say whether it carries a cost centre");
            f.CarriesCostCentre.Should().Be(Carriers.Contains(f.Key), f.Key);
        }
        Formatter("oracle-gl-interface-csv").Options.Single(o => o.Key == "segments").Description.Should().Contain("{costcenter}");
        Formatter("oracle-fusion-fbdi").Options.Single(o => o.Key == "segments").Description.Should().Contain("{costcenter}");
        Formatter("d365-fo-general-journal").Options.Should().Contain(o => o.Key == "dimensionFormat" && o.DefaultValue == "");
    }

    // ── Formula injection, structure and encoding (T-057) ─────────────────────────────────────────

    private static readonly string[] Payloads =
    {
        "=1+1", "+cmd|' /C calc'!A0", "-2+3", "@SUM(A1)", "\tTAB", "\rCR", "a,b", "a\"b", "line\nbreak",
        "مركز التكلفة", "😀 emoji", "{account}", "{company}", " =lead space",
    };

    private static JournalExportDocument Poisoned(string payload, IReadOnlyDictionary<string, string> options) => new()
    {
        TenantId = TenantId, TenantName = string.Empty, CompanyId = CompanyA, CompanyCode = "SA01", CompanyName = string.Empty,
        Period = "2026-07", PayrollRunId = null, Currency = "SAR", GeneratedAtUtc = new DateTime(2026, 8, 3, 9, 30, 0, DateTimeKind.Utc),
        ExportId = JournalExportFormatterGoldenTests.ExportId, Kind = JournalExportKinds.Full, Options = options,
        Lines = JournalExportBuilder.Expand(new[]
        {
            Row(TenantId, "Accrual", "5000 - Salaries", "", 10m, ccCode: payload, ccName: payload, ccSegment: payload),
            Row(TenantId, "Accrual", "", "2100 - Payable", 10m),
        }, "SA01"),
    };

    private static IReadOnlyDictionary<string, string> PoisonOptions(string key) => key switch
    {
        "oracle-gl-interface-csv" or "oracle-fusion-fbdi" => new Dictionary<string, string> { ["segments"] = "{costcenter}|{account}" },
        "d365-fo-general-journal" => new Dictionary<string, string> { ["dimensionFormat"] = "{costcenter}-{account}" },
        _ => new Dictionary<string, string>(),
    };

    private static readonly Regex Numeric = new(@"^-?\d+(\.\d+)?$", RegexOptions.Compiled);

    [Theory]
    [MemberData(nameof(CarrierKeys))]
    public void CostCentreText_NeverBecomesAFormula_AndNeverBreaksTheFileShape(string key)
    {
        foreach (var payload in Payloads)
        {
            var bytes = Formatter(key).Format(Poisoned(payload, PoisonOptions(key)));
            var ctx = $"{key} / {payload.Replace("\n", "\\n").Replace("\r", "\\r").Replace("\t", "\\t")}";

            if (key == "quickbooks-iif")
            {
                var text = Encoding.Latin1.GetString(bytes);
                text.Should().NotContain("?", $"{ctx}: a character 1252 cannot carry is dropped, never written as a placeholder");
                var records = text.Split("\r\n", StringSplitOptions.None).Where(l => l.StartsWith("TRNS\t") || l.StartsWith("SPL\t")).ToList();
                records.Should().HaveCount(2, ctx);
                records.Select(l => l.Split('\t', StringSplitOptions.None).Length).Should().OnlyContain(n => n == 10, $"{ctx}: a tab or line break in a cost centre must not shift columns");
                text.Split("\r\n").Count(l => l.Length > 0).Should().Be(3 + 2 + 1, ctx);   // 3 header rows, 2 lines, ENDTRNS
                continue;
            }

            var rows = ParseCsv(Utf8(bytes));
            var width = rows[0].Count;
            rows.Should().HaveCount(key == "oracle-fusion-fbdi" ? 2 : 3, $"{ctx}: row count survives a re-parse");
            rows.Should().OnlyContain(r => r.Count == width, $"{ctx}: column count survives a re-parse");
            foreach (var cell in rows.SelectMany(r => r).Where(c => c.Length > 0 && "=+-@\t\r".Contains(c[0])))
                Numeric.IsMatch(cell).Should().BeTrue($"{ctx}: cell '{cell}' starts with a formula trigger and is not an amount");
        }
    }

    [Fact]
    public void GenericCsv_RoundTripsTheCostCentre_InUtf8_WithTheGuardApostropheOnlyWhereATriggerLeads()
    {
        foreach (var payload in Payloads)
        {
            var rows = ParseCsv(Utf8(Formatter("generic-csv").Format(Poisoned(payload, new Dictionary<string, string>()))));
            var h = rows[0];
            var costed = rows[1];
            var expected = payload.Length > 0 && "=+-@\t\r".Contains(payload[0]) ? "'" + payload : payload;

            costed[h.IndexOf("CostCentre")].Should().Be(expected);
            costed[h.IndexOf("CostCentreName")].Should().Be(expected);
            costed[h.IndexOf("CostCentreErpCode")].Should().Be(expected);   // gl_segment (same text) wins
        }
        Utf8(Formatter("generic-csv").Format(Poisoned("مركز التكلفة", new Dictionary<string, string>()))).Should().Contain("مركز التكلفة");
        Utf8(Formatter("generic-csv").Format(Poisoned("😀 emoji", new Dictionary<string, string>()))).Should().Contain("😀 emoji");
    }

    [Fact]
    public void ACostCentreContainingATemplateToken_IsNeverExpandedAgain()
    {
        var oracle = ParseCsv(Utf8(Formatter("oracle-gl-interface-csv").Format(
            Poisoned("{account}", new Dictionary<string, string> { ["segments"] = "{costcenter}|{account}|{company}" }))));
        var h = oracle[0];
        oracle[1][h.IndexOf("SEGMENT1")].Should().Be("{account}", "the value is inserted, never re-scanned for tokens");
        oracle[1][h.IndexOf("SEGMENT2")].Should().Be("5000");

        var d365 = ParseCsv(Utf8(Formatter("d365-fo-general-journal").Format(
            Poisoned("{company}", new Dictionary<string, string> { ["dimensionFormat"] = "{account}-{costcenter}" }))));
        d365[1][d365[0].IndexOf("ACCOUNTDISPLAYVALUE")].Should().Be("5000-{company}");
    }

    // ── Per-cost-centre tie-out (T-058 unit) ──────────────────────────────────────────────────────

    [Fact]
    public void Verify_ProvesEveryCostCentresDebitsAndCredits_AgainstTheSourceRows()
    {
        var doc = CostedSample();
        bool? PnL(FinanceGlEntry e, string code) => code.StartsWith('5');

        var ok = JournalExportBuilder.Verify(doc.Lines, SourceOf(doc), PnL);
        ok.Ok.Should().BeTrue();

        // Move the salary expense onto the wrong cost centre: accounts, totals and companies still balance.
        var tampered = doc.Lines.Select(l => l.CostCenterCode == "CC100" ? l with { CostCenterCode = "CC200" } : l).ToList();
        var bad = JournalExportBuilder.Verify(tampered, SourceOf(doc), PnL);

        bad.Ok.Should().BeFalse();
        bad.Failure.Should().Be(JournalExportErrors.CostCentreTotalMismatch).And.Be("cost_centre_total_mismatch");
        bad.CostCentreDeltas.Should().NotBeNullOrEmpty();
    }

    /// <summary>Rebuilds the ledger rows a sample document came from (one row per entry id), for an independent recompute.</summary>
    private static IReadOnlyList<FinanceGlEntry> SourceOf(JournalExportDocument doc) =>
        doc.Lines.GroupBy(l => l.EntryId).Select(g =>
        {
            var dr = g.FirstOrDefault(l => l.Side == "DR");
            var cr = g.FirstOrDefault(l => l.Side == "CR");
            var any = g.First();
            var cost = g.FirstOrDefault(l => l.CostCenterCode != null);
            return new FinanceGlEntry
            {
                Id = g.Key, TenantId = TenantId, CompanyId = any.CompanyId, SourceModule = any.SourceModule, SourceEntityId = any.SourceEntityId,
                SourceEntityRef = any.SourceEntityRef, EventType = any.EventType, Period = any.Period, Currency = any.Currency,
                EntryDate = any.SystemPostedDate, Description = any.Description, Amount = any.Amount,
                DebitAccount = dr is null ? "" : dr.AccountCode + " - " + dr.AccountName,
                CreditAccount = cr is null ? "" : cr.AccountCode + " - " + cr.AccountName,
                CostCenterId = cost is null ? null : Guid.NewGuid(), CostCenterCode = cost?.CostCenterCode,
                CostCenterName = cost?.CostCenterName, CostCenterGlSegment = cost?.CostCenterGlSegment,
                ReversalOfEntryId = any.ReversalOfEntryId,
            };
        }).ToList();
}
