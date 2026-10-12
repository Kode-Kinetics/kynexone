using System.Text;
using FluentAssertions;
using Zayra.Api.Infrastructure.Finance;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// Every ERP journal layout is pinned BYTE FOR BYTE against a golden file in Fixtures/JournalExports. The sample
/// is chosen to exercise everything a real month throws at a formatter: two companies (one with an ERP company
/// code, one falling back to its CR number), one-sided payroll rows and two-sided loan/bonus rows, an Arabic
/// description, a quoted/comma description, a bonus and its reversal contra, and a GL account with an external
/// (ERP) code.
///
/// <para>A golden file is a contract with an ERP import: if a change alters it, the test fails and the diff is
/// reviewed against the vendor's layout before the file is regenerated (JOURNAL_GOLDEN_UPDATE=1).</para>
/// </summary>
public class JournalExportFormatterGoldenTests
{
    internal static readonly Guid ExportId = Guid.Parse("5f0c2a9e-1b7d-4c3e-9a10-0123456789ab");
    private static readonly Guid CompanyA = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid CompanyB = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000002");

    public static IEnumerable<object[]> Formatters() => AllFormatters().Select(f => new object[] { f.Key });

    internal static IReadOnlyList<IJournalExportFormatter> AllFormatters() => new IJournalExportFormatter[]
    {
        new GenericCsvJournalFormatter(), new QuickBooksIifJournalFormatter(), new OracleGlInterfaceCsvFormatter(),
        new OracleFusionFbdiJournalFormatter(), new SapS4GeneralJournalFormatter(), new D365GeneralJournalFormatter(),
        new OdooAccountMoveFormatter(), new NetSuiteJournalFormatter(), new XeroManualJournalFormatter(),
        new ZohoBooksJournalFormatter(), new QuickBooksOnlineJournalFormatter(),
    };

    [Theory]
    [MemberData(nameof(Formatters))]
    public void Format_MatchesItsGoldenFile_ByteForByte(string key)
    {
        var formatter = AllFormatters().Single(f => f.Key == key);
        var bytes = formatter.Format(SampleDocument());
        var path = Path.Combine(SourceFixtureDir(), $"{key}.{formatter.Extension}");

        if (Environment.GetEnvironmentVariable("JOURNAL_GOLDEN_UPDATE") == "1")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, bytes);
        }

        File.Exists(path).Should().BeTrue($"the golden file for {key} must exist (run once with JOURNAL_GOLDEN_UPDATE=1 and review it)");
        bytes.Should().Equal(File.ReadAllBytes(path),
            $"{key} must produce exactly the reviewed layout; regenerate the golden only after checking the diff against the vendor's import spec");
    }

    [Theory]
    [MemberData(nameof(Formatters))]
    public void Format_IsDeterministic_AndCarriesTheExportId(string key)
    {
        var formatter = AllFormatters().Single(f => f.Key == key);
        var a = formatter.Format(SampleDocument());
        var b = formatter.Format(SampleDocument());
        a.Should().Equal(b, "a download regenerates from the frozen lines and must hash identically");

        var text = Encoding.Latin1.GetString(a);
        var full = ExportId.ToString();
        var shortTag = ExportId.ToString("N")[..8].ToUpperInvariant();
        (text.Contains(full, StringComparison.OrdinalIgnoreCase) || text.Contains(ExportId.ToString("N"), StringComparison.OrdinalIgnoreCase)
            || text.Contains(shortTag, StringComparison.Ordinal) || DerivedFromExportId(formatter, text))
            .Should().BeTrue($"{key} must carry the export id (or a value derived from it) as an idempotency key");
    }

    [Theory]
    [MemberData(nameof(Formatters))]
    public void EveryCompany_GetsItsOwnBalancedDocuments_WithItsOwnCode(string key)
    {
        var doc = SampleDocument();
        var docs = doc.PostingDocuments();
        docs.Select(d => d.CompanyCode).Distinct().Should().BeEquivalentTo(new[] { "SA01", "1010-555" });
        docs.Should().OnlyContain(d => d.Debits == d.Credits, "every ERP document must balance on its own");
        docs.Should().OnlyContain(d => d.Lines.All(l => l.CompanyCode == d.CompanyCode), "a document never mixes companies");

        var text = Encoding.UTF8.GetString(AllFormatters().Single(f => f.Key == key).Format(doc));
        System.Text.RegularExpressions.Regex.IsMatch(text, @"(^|[,\t])GROUP([,\t\r\n]|$)", System.Text.RegularExpressions.RegexOptions.Multiline)
            .Should().BeFalse("a line is stamped with its company's ERP code, never a group placeholder");
    }

    [Fact]
    public void QuickBooksIif_IsWindows1252_UsesNamesNotCodeDashName_AndUniqueShortDocNums()
    {
        var bytes = new QuickBooksIifJournalFormatter().Format(SampleDocument());
        bytes.Should().NotContain((byte)0xD8, "Arabic must never be written as UTF-8 lead bytes into a 1252 file");
        var text = Encoding.Latin1.GetString(bytes);
        text.Should().NotContain(" - Salaries", "ACCNT is the account name only");
        text.Should().Contain("\t6100-SAL\t", "the external code wins when mapped");

        var trns = text.Split("\r\n").Where(l => l.StartsWith("TRNS\t", StringComparison.Ordinal)).ToList();
        var docNums = trns.Select(l => l.Split('\t')[8]).ToList();
        docNums.Should().OnlyHaveUniqueItems().And.OnlyContain(d => d.Length <= 20);
        trns.Select(l => l.Split('\t')[6]).Should().OnlyContain(c => c.Length == 0, "CLASS is not a CR number");
    }

    private static bool DerivedFromExportId(IJournalExportFormatter f, string text) =>
        f is QuickBooksIifJournalFormatter or QuickBooksOnlineJournalFormatter or ZohoBooksJournalFormatter
        && SampleDocument().ShortRefs("KX", 12).Values.All(v => text.Contains(v, StringComparison.Ordinal));

    /// <summary>The fixed sample. Companies: A has erp_company_code SA01; B has none and falls back to its CR number.</summary>
    internal static JournalExportDocument SampleDocument(Guid? exportId = null)
    {
        var run = Guid.Parse("11111111-2222-3333-4444-555555555555");
        var loan = Guid.Parse("66666666-7777-8888-9999-000000000000");
        var bonus = Guid.Parse("12121212-3434-5656-7878-909090909090");
        var bonusEntry = Guid.Parse("e4000000-0000-0000-0000-000000000004");
        var entries = new List<FinanceGlEntry>
        {
            Entry("e1000000-0000-0000-0000-000000000001", CompanyA, "Payroll", run, "PR-2026-07", "Accrual",
                "5000 - Salaries", "", 10_000m, "July payroll, salaries"),
            Entry("e2000000-0000-0000-0000-000000000002", CompanyA, "Payroll", run, "PR-2026-07", "Accrual",
                "", "2100 - Salaries Payable", 10_000m, "رواتب شهر يوليو"),
            Entry("e3000000-0000-0000-0000-000000000003", CompanyA, "Loan", loan, "LN-2026-00001", "Disbursement",
                "1400 - Employee Loans Receivable", "1000 - Bank", 2_500m, "Loan LN-2026-00001 disbursement"),
            Entry(bonusEntry.ToString(), CompanyB, "Bonus", bonus, "BON-2026-0007", "BonusPayment",
                "5200 - Bonus Expense", "2300 - Bonus Payable", 1_200.505m, "Q2 bonus \"special\" award", isReversed: true),
            Entry("e5000000-0000-0000-0000-000000000005", CompanyB, "Bonus", bonus, "BON-2026-0007", "Reversal",
                "2300 - Bonus Payable", "5200 - Bonus Expense", 1_200.505m, "Reversal of BON-2026-0007", reversalOf: bonusEntry),
        };
        string CodeOf(FinanceGlEntry e) => e.CompanyId == CompanyA ? "SA01" : "1010-555";
        string? ExternalOf(FinanceGlEntry e, string code) => e.CompanyId == CompanyA && code == "5000" ? "6100-SAL" : null;

        var lines = JournalExportBuilder.Expand(entries, CodeOf, ExternalOf);
        JournalExportBuilder.Verify(lines, entries).Ok.Should().BeTrue("the sample must itself tie out per company");
        return new JournalExportDocument
        {
            TenantId = Guid.Parse("99999999-9999-9999-9999-999999999999"),
            TenantName = string.Empty,
            CompanyId = null,
            CompanyCode = "GROUP",
            CompanyName = string.Empty,
            Period = "2026-07",
            PayrollRunId = null,
            Currency = "SAR",
            GeneratedAtUtc = new DateTime(2026, 8, 3, 9, 30, 0, DateTimeKind.Utc),
            Lines = lines,
            ExportId = exportId ?? ExportId,
            Kind = JournalExportKinds.Full,
        };
    }

    private static FinanceGlEntry Entry(string id, Guid company, string module, Guid source, string sourceRef, string evt,
        string dr, string cr, decimal amount, string description, bool isReversed = false, Guid? reversalOf = null) => new()
    {
        Id = Guid.Parse(id), TenantId = Guid.Parse("99999999-9999-9999-9999-999999999999"), CompanyId = company,
        SourceModule = module, SourceEntityId = source, SourceEntityRef = sourceRef, EventType = evt,
        DebitAccount = dr, CreditAccount = cr, Amount = amount, Currency = "SAR",
        EntryDate = new DateOnly(2026, 8, 3), Period = "2026-07", Description = description,
        IsReversed = isReversed, ReversalOfEntryId = reversalOf,
    };

    private static string SourceFixtureDir()
    {
        if (Environment.GetEnvironmentVariable("JOURNAL_GOLDEN_UPDATE") != "1")
            return Path.Combine(AppContext.BaseDirectory, "Fixtures", "JournalExports");
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Zayra.Api.Tests.csproj"))) dir = dir.Parent;
        return Path.Combine(dir!.FullName, "Fixtures", "JournalExports");
    }
}
