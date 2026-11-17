using System.Globalization;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Zayra.Api.Infrastructure.Finance;
using Zayra.Api.Infrastructure.Payroll.SaudiBankExports;
using Zayra.Api.Models;
using static Zayra.Api.Tests.WpsReturnedFile.WpsReturnedFileImportPathTests;

namespace Zayra.Api.Tests.WpsReturnedFile;

/// <summary>
/// The returned-file reader's second-round guards: the status is classified on the raw value (hidden and formula characters
/// refuse the row), the file date has an upper bound, warnings never use up the fault budget, and the guards that had no test
/// of their own (the status-column switch wiring, the establishment resolver's tenant pin). Synthetic data only.
/// </summary>
public class WpsReturnedFileHardeningTests
{
    private const string Est = "7-1234567";
    private static readonly DateOnly BatchCreated = new(2026, 9, 20);

    private static BankConfirmationBatchContext Ctx(IReadOnlyList<SynthRow> rows) =>
        new(Est, rows.Select((r, i) => new WpsExpectedLine(i + 1, $"EMP{i + 1:D4}", r.MolId, r.Iban, r.Net)).ToList(),
            BatchCreated, rows.Sum(r => r.Net), "CO1", new HashSet<string>());

    private static WpsReturnedFileExpectation Bound(IReadOnlyList<SynthRow> rows) =>
        new(Est, BindToBatch: true, BatchCreatedOn: BatchCreated, BatchTotal: rows.Sum(r => r.Net), ExpectedLineCount: rows.Count);

    private static IReadOnlyList<SynthRow> Paid(int n) => WpsReturnedFileOracle.Rows(n).Select(r => r with { Status = "PAID" }).ToList();

    // ── B2: the status is classified on the RAW value ─────────────────────────

    [Theory]
    [InlineData("PA\u200BID")]          // zero-width space inside
    [InlineData("\u2066PAID\u2069")]    // bidi isolates around
    [InlineData("PAID\u202C")]          // bidi pop
    [InlineData("\uFEFFPAID")]          // zero-width no-break space
    [InlineData("PAID\u00A0")]          // non-breaking space
    [InlineData("=PAID")]
    [InlineData("+PAID")]
    [InlineData("-PAID")]
    [InlineData("@RETURNED")]
    [InlineData(" =PAID")]
    [InlineData("P\u0410ID")]           // Cyrillic capital A that looks like a Latin A
    [InlineData("\uFF30\uFF21\uFF29\uFF24")] // full-width PAID
    public void A_status_with_a_hidden_look_alike_or_formula_character_refuses_the_row_and_is_never_classified(string status)
    {
        var rows = Paid(3).ToList();
        rows[0] = rows[0] with { Status = status };
        var file = WpsReturnedFileOracle.Build(rows);

        WpsReturnedFileStatus.Classify(status).Should().BeNull("a status is classified exactly as written, never after cleaning");

        var read = WpsReturnedFileValidator.Read(file, Bound(rows));
        var first = read.Rows[0];
        first.Blocked.Should().BeTrue();
        first.BlockingCodes.Should().Contain("status_unsafe_characters");
        first.TrnStatus.Should().NotBe("PAID").And.NotBe("RETURNED", "a repaired word is never stored as what the bank wrote");
        read.Findings.Should().Contain(f => f.Code == "status_unsafe_characters" && f.Severity == WpsFindingSeverity.Error
                                            && f.MessageAr.Length > 0);

        var parse = new WpsReturnedFileBankConfirmationParser(statusColumnVerified: true).Parse(file, Ctx(rows));
        parse.Rows.Select(r => r.EmployeeCode).Should().Equal("EMP0002", "EMP0003");
    }

    [Theory]
    [InlineData("PAID", "Paid")]
    [InlineData(" PAID ", "Paid")]
    [InlineData("paid", "Paid")]
    [InlineData("IN PROGRESS", "Pending")]
    [InlineData("RJCT", "Rejected")]
    [InlineData("RETURNED", "Returned")]
    public void A_plain_status_is_classified_and_stored_exactly_as_the_bank_wrote_it(string status, string outcome)
    {
        var rows = Paid(1).Select(r => r with { Status = status }).ToList();
        var read = WpsReturnedFileValidator.Read(WpsReturnedFileOracle.Build(rows), Bound(rows));

        read.Rows[0].Blocked.Should().BeFalse();
        read.Rows[0].TrnStatus.Should().Be(status);
        WpsReturnedFileStatus.Classify(read.Rows[0].TrnStatus).Should().Be(outcome);
    }

    [Fact]
    public void The_stored_transaction_reference_is_still_cleaned_of_bidi_zero_width_and_formula_characters()
    {
        var rows = WpsReturnedFileOracle.Rows(1).Select(r => r with { TrnRef = "\u202E=cmd|\u200Bref", Status = "PAID" }).ToList();
        var r = WpsReturnedFileValidator.Read(WpsReturnedFileOracle.Build(rows), new WpsReturnedFileExpectation(Est));
        r.Rows[0].TrnRef.Should().Be("cmd|ref");
    }

    // ── B4: the file date has an upper bound ──────────────────────────────────

    [Fact]
    public void A_file_dated_within_the_window_after_the_batch_is_read_and_one_day_later_is_refused()
    {
        var rows = Paid(3);
        string On(int days) => BatchCreated.AddDays(days).ToString("yyyyMMdd", CultureInfo.InvariantCulture);

        var inside = WpsReturnedFileValidator.Read(WpsReturnedFileOracle.Build(rows, new WpsReturnedFileOracle.Header(ValueDate: On(WpsReturnedFileValidator.MaxDaysAfterBatch))), Bound(rows));
        inside.HasFileLevelError.Should().BeFalse();

        var outside = WpsReturnedFileValidator.Read(WpsReturnedFileOracle.Build(rows, new WpsReturnedFileOracle.Header(ValueDate: On(WpsReturnedFileValidator.MaxDaysAfterBatch + 1))), Bound(rows));
        outside.HasFileLevelError.Should().BeTrue();
        outside.Findings.Should().Contain(f => f.Code == "file_date_after_batch_window" && f.Severity == WpsFindingSeverity.Error);
        var refusal = outside.Findings.First(f => f.Code == "file_date_after_batch_window");
        refusal.Message.Should().Contain($"{WpsReturnedFileValidator.MaxDaysAfterBatch} days");
        refusal.MessageAr.Should().Contain($"{WpsReturnedFileValidator.MaxDaysAfterBatch} يوماً");
    }

    [Fact]
    public void A_later_bank_file_date_beyond_the_window_refuses_the_file_even_when_the_value_date_fits()
    {
        var rows = Paid(3);
        var file = WpsReturnedFileOracle.Build(rows, new WpsReturnedFileOracle.Header(ValueDate: "20260925", DDate: "20261101"));
        var r = WpsReturnedFileValidator.Read(file, Bound(rows));
        r.Findings.Should().Contain(f => f.Code == "file_date_after_batch_window");
        r.HasFileLevelError.Should().BeTrue();
    }

    [Fact]
    public void The_window_is_shorter_than_a_monthly_cycle()
    {
        WpsReturnedFileValidator.MaxDaysAfterBatch.Should().BeInRange(5, 25,
            "a month later the next batch exists, so this month's file must not still fit last month's batch");
    }

    [Fact]
    public async Task This_months_file_against_last_months_pending_batch_applies_nothing()
    {
        using var db = NewDb();
        var s = Seed(db, 3);   // last month's batch, created 20 September and still Pending
        var thisMonth = WpsReturnedFileOracle.Build(s.Rows.Select(r => r with { Status = "PAID" }).ToList(),
            new WpsReturnedFileOracle.Header(ValueDate: "20261027", FileRef: "WPS2026100001"));   // dated 27 October

        var (result, refusal) = await Import(db, s, thisMonth);
        db.SaveChanges();

        result.Should().BeNull();
        JsonSerializer.Serialize(refusal!.Payload).Should().Contain("file_date_after_batch_window");
        db.PayrollPaymentRecords.Should().OnlyContain(r => r.Status == "Pending");
        db.BankPaymentConfirmations.Should().BeEmpty();
    }

    // ── S1: warnings never use up the fault budget ────────────────────────────

    private static string Whole(decimal d) => decimal.ToInt64(d).ToString(CultureInfo.InvariantCulture);

    private static string WholeNumberFile(IReadOnlyList<SynthRow> rows) =>
        WpsReturnedFileOracle.Build(rows, rowOverride: x =>
            $"[32B-AMT]\t{Whole(x.Net)}\t[59-ACC]\t{x.Iban}\t[59-NAME]\t{x.Name}\t[MOL-ID]\t{x.MolId}\t[MOL-BAS]\t{Whole(x.Basic)}"
          + $"\t[MOL-HAL]\t{Whole(x.Housing)}\t[MOL-OEA]\t{Whole(x.Other)}\t[MOL-DED]\t{Whole(x.Deductions)}\t[TRN-STATUS]\tPAID");

    [Fact]
    public void A_legitimate_60_line_file_with_whole_number_amounts_is_not_refused_and_the_warning_is_listed_once_with_a_count()
    {
        var rows = Paid(60);
        var read = WpsReturnedFileValidator.Read(WholeNumberFile(rows), Bound(rows));

        read.HasFileLevelError.Should().BeFalse();
        read.Findings.Should().NotContain(f => f.Code == "too_many_findings");
        read.Rows.Should().HaveCount(60).And.OnlyContain(r => !r.Blocked);
        var warning = read.Findings.Single(f => f.Code == "amount_no_decimals");
        warning.Severity.Should().Be(WpsFindingSeverity.Warning);
        warning.Count.Should().Be(300, "five amounts on each of 60 lines");

        var parse = new WpsReturnedFileBankConfirmationParser(statusColumnVerified: true).Parse(WholeNumberFile(rows), Ctx(rows));
        parse.Rows.Should().HaveCount(60);
    }

    [Fact]
    public void A_long_signature_block_is_one_warning_and_does_not_refuse_the_file()
    {
        var rows = Paid(3);
        var file = WpsReturnedFileOracle.Build(rows, trailerLines: Enumerable.Range(0, 400).Select(i => $"SIGNATURE LINE {i}").ToList());
        var read = WpsReturnedFileValidator.Read(file, Bound(rows));
        read.HasFileLevelError.Should().BeFalse();
        read.Findings.Single(f => f.Code == "unrecognised_line").Count.Should().Be(400);
    }

    [Fact]
    public void Errors_still_stop_at_the_bound()
    {
        var content = string.Concat(Enumerable.Repeat("[MOL-ID]\t\n", 400)) + "[DEST-ID]\tX\n";
        var read = WpsReturnedFileValidator.Read(content, new WpsReturnedFileExpectation(Est));
        read.HasFileLevelError.Should().BeTrue();
        read.Findings.Should().Contain(f => f.Code == "too_many_findings");
        read.Findings.Count(f => f.Severity == WpsFindingSeverity.Error).Should().BeLessThanOrEqualTo(WpsReturnedFileValidator.MaxFindings + 1);
    }

    // ── S2: the status-column switch is wired from configuration ──────────────

    private static BankConfirmationService ServiceFrom(Zayra.Api.Data.ZayraDbContext db, Dictionary<string, string?> config)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(config).Build();
        var services = new ServiceCollection();
        services.AddBankConfirmationParsers(configuration);
        return new BankConfirmationService(db, services.BuildServiceProvider().GetServices<IBankConfirmationParser>(), ClaimFor(db));
    }

    [Fact]
    public async Task With_only_the_reader_switched_on_the_registered_parser_applies_no_status()
    {
        using var db = NewDb();
        var s = Seed(db, 3);
        var service = ServiceFrom(db, new() { [WpsReturnedFileFeature.ConfigKey] = "true" });

        var (result, refusal) = await service.ImportAsync(s.TenantId, s.BatchId, "f.txt",
            WpsReturnedFileOracle.Build(s.Rows.Select(r => r with { Status = "PAID" }).ToList()), "wps-returned-file",
            new BankConfirmationImportOptions(), null, "x", default);
        db.SaveChanges();

        result.Should().BeNull();
        refusal!.Status.Should().Be(422);
        JsonSerializer.Serialize(refusal.Payload).Should().Contain("status_column_unverified");
        db.PayrollPaymentRecords.Should().OnlyContain(r => r.Status == "Pending");
    }

    [Fact]
    public async Task With_both_switches_on_the_registered_parser_applies_the_status()
    {
        using var db = NewDb();
        var s = Seed(db, 3);
        var service = ServiceFrom(db, new()
        {
            [WpsReturnedFileFeature.ConfigKey] = "true", [WpsReturnedFileFeature.StatusColumnVerifiedKey] = "true",
        });

        var (result, refusal) = await service.ImportAsync(s.TenantId, s.BatchId, "f.txt",
            WpsReturnedFileOracle.Build(s.Rows.Select(r => r with { Status = "PAID" }).ToList()), "wps-returned-file",
            new BankConfirmationImportOptions(), null, "x", default);

        refusal.Should().BeNull();
        result!.PaidApplied.Should().Be(3);
    }

    // ── The establishment resolver's tenant pin ───────────────────────────────

    [Fact]
    public async Task Another_tenants_workspace_wps_agent_id_never_becomes_this_companys_establishment()
    {
        using var db = NewDb();
        var s = Seed(db, 2, establishment: "");
        // ANOTHER tenant holds a workspace-wide (no company) WPS agent id that equals the file's establishment.
        db.GCCComplianceSettings.Add(new GCCComplianceSetting
            { TenantId = Guid.NewGuid(), CompanyId = null, CountryCode = "SA", IsActive = true, WpsAgentId = Est });
        db.SaveChanges();

        (await WpsEstablishmentResolver.ResolveAsync(db, s.TenantId, s.Company.Id, isKsa: true, default)).Id.Should().BeNull();

        var (result, refusal) = await Import(db, s, WpsReturnedFileOracle.Build(s.Rows.Select(r => r with { Status = "PAID" }).ToList()));
        result.Should().BeNull();
        JsonSerializer.Serialize(refusal!.Payload).Should().Contain("establishment_not_configured");
    }
}
