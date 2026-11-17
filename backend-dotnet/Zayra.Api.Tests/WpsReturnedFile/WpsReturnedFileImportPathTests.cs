using System.Text.Json;
using System.Text.RegularExpressions;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Finance;
using Zayra.Api.Infrastructure.Payroll.SaudiBankExports;
using Zayra.Api.Models;

namespace Zayra.Api.Tests.WpsReturnedFile;

/// <summary>
/// The returned-file reader plugged into the EXISTING bank-confirmation import (BankConfirmationService). The existing
/// parsers and the import's own rules (amount holds, idempotency, late returns) are untouched; this only adds a parser
/// that is selected explicitly with format=wps-returned-file. Synthetic data only.
/// </summary>
public class WpsReturnedFileImportPathTests
{
    private const string Key = "wps-returned-file";
    private static readonly JsonSerializerOptions Readable = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    private static readonly Regex Arabic = new("[\u0600-\u06FF]");

    private static ZayraDbContext NewDb() =>
        new(new DbContextOptionsBuilder<ZayraDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static BankConfirmationService Service(ZayraDbContext db) =>
        new(db, new IBankConfirmationParser[]
        {
            new GenericCsvBankConfirmationParser(), new WpsAckBankConfirmationParser(), new WpsReturnedFileBankConfirmationParser(statusColumnVerified: true),
        });

    private sealed record Seeded(Guid TenantId, Guid BatchId, IReadOnlyList<SynthRow> Rows, IReadOnlyList<PayrollPaymentRecord> Records, Company Company);

    /// <summary>A company with a configured establishment, a run, a batch, and one Pending payment record per synthetic row.</summary>
    private static readonly DateTime BatchCreatedUtc = new(2026, 9, 20, 0, 0, 0, DateTimeKind.Utc);

    private static Seeded Seed(ZayraDbContext db, int count, string establishment = "7-1234567", Guid? tenant = null, string? arabicName = null,
        Company? existing = null, int offset = 0, DateTime? createdUtc = null)
    {
        var tenantId = tenant ?? existing?.TenantId ?? Guid.NewGuid();
        var company = existing ?? new Company { TenantId = tenantId, LegalNameEn = "Synthetic Co", CountryCode = "SA", DefaultCurrency = "SAR", WpsEmployerId = establishment };
        if (existing is null) db.Companies.Add(company);
        var run = new PayrollRun { TenantId = tenantId, CompanyId = company.Id, Year = 2026, Month = 9, Status = "Locked" };
        db.PayrollRuns.Add(run);
        var batch = new PayrollPaymentBatch { TenantId = tenantId, PayrollRunId = run.Id, Currency = "SAR", BatchNumber = "B-1", CreatedAtUtc = createdUtc ?? BatchCreatedUtc };
        db.PayrollPaymentBatches.Add(batch);

        var rows = WpsReturnedFileOracle.Rows(count + offset, arabicName).Skip(offset).ToList();
        var employees = rows.Select((r, i) => new Employee
        {
            TenantId = tenantId, CompanyId = company.Id, EmployeeCode = $"EMP{i + 1:D4}", FullName = $"Employee {i + 1}",
            IdNumber = r.MolId, Nationality = r.MolId[0] == '1' ? "Saudi" : "Egyptian", Status = "Active",
        }).ToList();
        db.Employees.AddRange(employees);
        db.SaveChanges();

        var records = rows.Select((r, i) => new PayrollPaymentRecord
        {
            TenantId = tenantId, PaymentBatchId = batch.Id, EmployeeId = employees[i].Id, Amount = r.Net, Iban = r.Iban,
            Status = "Pending", WpsReference = $"WPS-EMP{i + 1:D4}-202609",
        }).ToList();
        db.PayrollPaymentRecords.AddRange(records);
        db.SaveChanges();
        return new Seeded(tenantId, batch.Id, rows, records, company);
    }

    private static Task<(BankConfirmationImportResult? Result, JournalExportRefusal? Refusal)> Import(
        ZayraDbContext db, Seeded s, string content, BankConfirmationImportOptions? options = null, string format = Key) =>
        Service(db).ImportAsync(s.TenantId, s.BatchId, "returned.txt", content, format,
            options ?? new BankConfirmationImportOptions(), Guid.NewGuid(), "Payroll Officer", default);

    private static PayrollPaymentRecord Reload(ZayraDbContext db, PayrollPaymentRecord r) =>
        db.PayrollPaymentRecords.AsNoTracking().Single(x => x.Id == r.Id);

    // ── Selection: OFF unless chosen ─────────────────────────────────────────

    private static IReadOnlyList<string> RegisteredKeys(Dictionary<string, string?> config)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(config).Build();
        var services = new ServiceCollection();
        BankConfirmationParserRegistration.AddBankConfirmationParsers(services, configuration);
        return services.BuildServiceProvider().GetServices<IBankConfirmationParser>().Select(p => p.Key).ToList();
    }

    [Fact]
    public void The_returned_file_format_is_not_registered_or_listed_unless_the_flag_is_on()
    {
        RegisteredKeys(new()).Should().Equal("generic-csv", "wps-ack");
        RegisteredKeys(new() { [WpsReturnedFileFeature.ConfigKey] = "false" }).Should().Equal("generic-csv", "wps-ack");
        RegisteredKeys(new() { [WpsReturnedFileFeature.ConfigKey] = "true" }).Should().Equal("generic-csv", "wps-ack", Key);
        WpsReturnedFileFeature.IsEnabled(new ConfigurationBuilder().Build()).Should().BeFalse();
    }

    [Fact]
    public async Task Without_the_parser_registered_the_format_is_unknown_and_nothing_is_read()
    {
        using var db = NewDb();
        var s = Seed(db, 2);
        var service = new BankConfirmationService(db, new IBankConfirmationParser[] { new GenericCsvBankConfirmationParser() });

        var (result, refusal) = await service.ImportAsync(s.TenantId, s.BatchId, "f.txt", WpsReturnedFileOracle.Build(s.Rows), Key,
            new BankConfirmationImportOptions(), null, "x", default);

        result.Should().BeNull();
        refusal!.Status.Should().Be(400);
    }

    [Fact]
    public void The_plain_parse_entry_point_refuses_to_read_without_the_batch_context()
    {
        var parse = new WpsReturnedFileBankConfirmationParser().Parse(WpsReturnedFileOracle.Build(WpsReturnedFileOracle.Rows(2)));
        parse.Rows.Should().BeEmpty();
        parse.Errors.Should().ContainSingle(e => e.Error == "context_required");
    }

    [Fact]
    public void Existing_parsers_keep_their_keys_and_the_new_one_is_labelled_unverified()
    {
        new GenericCsvBankConfirmationParser().Key.Should().Be("generic-csv");
        new WpsAckBankConfirmationParser().Key.Should().Be("wps-ack");
        var p = new WpsReturnedFileBankConfirmationParser();
        p.Key.Should().Be(Key);
        p.Disclaimer.Should().Contain("UNVERIFIED").And.Contain("bank");
        p.Disclaimer.Should().NotContainAny("WPS compliant", "Mudad certified");
    }

    // ── Happy path and the five buckets ──────────────────────────────────────

    [Fact]
    public async Task Paid_rejected_and_absent_lines_are_each_reconciled_to_their_own_payment_record()
    {
        using var db = NewDb();
        var s = Seed(db, 5);
        var rows = s.Rows.Select(r => r with { Status = "PAID", TrnRef = "BNKREF" }).ToList();
        rows[1] = rows[1] with { Status = "REJECTED" };
        var file = WpsReturnedFileOracle.Build(rows.Take(4).ToList());   // employee 5 absent

        var (result, refusal) = await Import(db, s, file);
        refusal.Should().BeNull();
        db.SaveChanges();

        result!.PaidApplied.Should().Be(3);
        result.ReturnedApplied.Should().Be(1);
        Reload(db, s.Records[0]).Status.Should().Be("Paid");
        Reload(db, s.Records[1]).Status.Should().Be("Returned");
        Reload(db, s.Records[4]).Status.Should().Be("Pending", "an employee the file does not mention stays unconfirmed");
        result.Unconfirmed.Should().HaveCount(1);

        var rejected = db.BankPaymentConfirmations.Single(c => c.PaymentRecordId == s.Records[1].Id);
        rejected.Outcome.Should().Be("Returned");
        rejected.ReasonCode.Should().Be("REJECTED");
        rejected.ReasonText.Should().Contain("bank statement").And.NotContain("not paid");
        rejected.MatchedBy.Should().Be(BankConfirmationMatchModes.EmployeeCode);
        rejected.BankReference.Should().Be("BNKREF");

        var missing = JsonSerializer.Serialize(result.ParseErrors, Readable);
        missing.Should().Contain("missing_from_file").And.Contain("\"rejected\"").And.Contain("scope_notice");
        Arabic.IsMatch(missing).Should().BeTrue("every reason is also given in Arabic, in its own field");
        missing.Should().Contain("detailAr").And.Contain("does not show whether Mudad accepted");
    }

    [Fact]
    public async Task A_wrong_establishment_applies_nothing_and_says_why()
    {
        using var db = NewDb();
        var s = Seed(db, 3, establishment: "7-9999999");
        var file = WpsReturnedFileOracle.Build(s.Rows.Select(r => r with { Status = "PAID" }).ToList());

        var (result, refusal) = await Import(db, s, file);

        result.Should().BeNull();
        refusal!.Status.Should().Be(422);
        JsonSerializer.Serialize(refusal.Payload).Should().Contain("establishment_mismatch");
        db.SaveChanges();
        db.PayrollPaymentRecords.Should().OnlyContain(r => r.Status == "Pending");
        db.BankPaymentConfirmations.Should().BeEmpty();
    }

    [Fact]
    public async Task A_header_total_mismatch_applies_nothing()
    {
        using var db = NewDb();
        var s = Seed(db, 3);
        var rows = s.Rows.Select(r => r with { Status = "PAID" }).ToList();
        var file = WpsReturnedFileOracle.Build(rows, declaredTotal: rows.Sum(r => r.Net) + 5m);

        var (result, refusal) = await Import(db, s, file);

        result.Should().BeNull();
        JsonSerializer.Serialize(refusal!.Payload).Should().Contain("header_total_mismatch");
        db.PayrollPaymentRecords.Should().OnlyContain(r => r.Status == "Pending");
    }

    [Fact]
    public async Task An_amount_that_differs_from_our_record_is_held_by_the_existing_import_rule()
    {
        using var db = NewDb();
        var s = Seed(db, 3);
        var rec = s.Records[1];
        rec.Amount += 10m;
        db.SaveChanges();

        var (result, _) = await Import(db, s, WpsReturnedFileOracle.Build(s.Rows.Select(r => r with { Status = "PAID" }).ToList()));
        db.SaveChanges();

        result!.AmountMismatchesHeld.Should().HaveCount(1);
        Reload(db, rec).Status.Should().Be("Pending", "a figure that disagrees is never applied silently");
        result.PaidApplied.Should().Be(2);
    }

    [Fact]
    public async Task A_government_id_we_do_not_have_is_reported_and_applied_to_nobody()
    {
        using var db = NewDb();
        var s = Seed(db, 4);
        // two known employees plus one the batch does not have (same net as the fourth, so the total still fits the batch)
        var stranger = s.Rows[3] with { MolId = "1555555555", Iban = WpsReturnedFileOracle.SaIban(80, 555_555_555_555L), Status = "PAID" };
        var rows = s.Rows.Take(2).Select(r => r with { Status = "PAID" }).Append(stranger).ToList();

        var (result, _) = await Import(db, s, WpsReturnedFileOracle.Build(rows));
        db.SaveChanges();

        result!.PaidApplied.Should().Be(2);
        var errors = JsonSerializer.Serialize(result.ParseErrors);
        errors.Should().Contain("unknown_employee").And.NotContain("1555555555");
        db.BankPaymentConfirmations.Should().HaveCount(2);
    }

    [Fact]
    public async Task A_row_naming_a_different_bank_account_than_our_record_refuses_the_whole_file()
    {
        using var db = NewDb();
        var s = Seed(db, 3);
        var rec = s.Records[0];
        rec.Iban = WpsReturnedFileOracle.SaIban(10, 424_242_424_242L);
        db.SaveChanges();

        var (result, refusal) = await Import(db, s, WpsReturnedFileOracle.Build(s.Rows.Select(r => r with { Status = "PAID" }).ToList()));
        db.SaveChanges();

        result.Should().BeNull("one altered account refuses the file, so the clean lines are not applied either");
        JsonSerializer.Serialize(refusal!.Payload).Should().Contain("file_refused_account_mismatch");
        db.PayrollPaymentRecords.Should().OnlyContain(r => r.Status == "Pending");
        db.BankPaymentConfirmations.Should().BeEmpty();
    }

    [Fact]
    public async Task A_rejected_line_is_recorded_as_returned_even_when_its_amount_differs()
    {
        using var db = NewDb();
        var s = Seed(db, 2);
        s.Records[0].Amount += 25m;
        db.SaveChanges();
        var rows = s.Rows.Select(r => r with { Status = "PAID" }).ToList();
        rows[0] = rows[0] with { Status = "REJECTED" };

        var (result, _) = await Import(db, s, WpsReturnedFileOracle.Build(rows));
        db.SaveChanges();

        result!.ReturnedApplied.Should().Be(1);
        result.AmountMismatchesHeld.Should().BeEmpty();
        Reload(db, s.Records[0]).Status.Should().Be("Returned");
    }

    [Fact]
    public async Task A_rejected_line_with_no_iban_on_our_record_is_held()
    {
        using var db = NewDb();
        var s = Seed(db, 2);
        s.Records[0].Iban = "";
        db.SaveChanges();
        var rows = s.Rows.Select(r => r with { Status = "PAID" }).ToList();
        rows[0] = rows[0] with { Status = "REJECTED" };

        var (result, _) = await Import(db, s, WpsReturnedFileOracle.Build(rows));
        db.SaveChanges();

        Reload(db, s.Records[0]).Status.Should().Be("Pending");
        JsonSerializer.Serialize(result!.ParseErrors).Should().Contain("account_missing");
    }

    [Fact]
    public async Task A_file_with_no_status_column_validates_but_confirms_nothing()
    {
        using var db = NewDb();
        var s = Seed(db, 3);

        var (result, refusal) = await Import(db, s, WpsReturnedFileOracle.Build(s.Rows));
        db.SaveChanges();

        result.Should().BeNull("a file with no status confirms nothing, so there is nothing to apply");
        refusal!.Status.Should().Be(422);
        JsonSerializer.Serialize(refusal.Payload).Should().Contain("status_missing");
        db.PayrollPaymentRecords.Should().OnlyContain(r => r.Status == "Pending");
        db.BankPaymentConfirmations.Should().BeEmpty();
    }

    [Fact]
    public async Task A_status_word_outside_the_vocabulary_confirms_nothing_for_that_line()
    {
        using var db = NewDb();
        var s = Seed(db, 3);
        var rows = s.Rows.Select(r => r with { Status = "PAID" }).ToList();
        rows[2] = rows[2] with { Status = "ZZ91" };

        var (result, _) = await Import(db, s, WpsReturnedFileOracle.Build(rows));
        db.SaveChanges();

        result!.PaidApplied.Should().Be(2);
        Reload(db, s.Records[2]).Status.Should().Be("Pending");
    }

    [Fact]
    public async Task Until_the_status_column_is_verified_no_status_is_applied_to_any_record()
    {
        using var db = NewDb();
        var s = Seed(db, 3);
        var unverified = new BankConfirmationService(db, new IBankConfirmationParser[] { new WpsReturnedFileBankConfirmationParser() });
        var rows = s.Rows.Select(r => r with { Status = "PAID" }).ToList();
        rows[1] = rows[1] with { Status = "REJECTED" };

        var (result, refusal) = await unverified.ImportAsync(s.TenantId, s.BatchId, "f.txt", WpsReturnedFileOracle.Build(rows), Key,
            new BankConfirmationImportOptions(), null, "x", default);
        db.SaveChanges();

        result.Should().BeNull();
        JsonSerializer.Serialize(refusal!.Payload).Should().Contain("status_column_unverified");
        db.PayrollPaymentRecords.Should().OnlyContain(r => r.Status == "Pending");
        db.BankPaymentConfirmations.Should().BeEmpty();
    }

    [Fact]
    public void The_status_column_switch_is_off_unless_set()
    {
        WpsReturnedFileFeature.StatusColumnVerified(new ConfigurationBuilder().Build()).Should().BeFalse();
        WpsReturnedFileFeature.StatusColumnVerified(new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { [WpsReturnedFileFeature.StatusColumnVerifiedKey] = "true" }).Build()).Should().BeTrue();
    }

    [Fact]
    public async Task An_accepted_word_changes_no_status_and_is_reported()
    {
        using var db = NewDb();
        var s = Seed(db, 3);
        var rows = s.Rows.Select(r => r with { Status = "PAID" }).ToList();
        rows[2] = rows[2] with { Status = "SUCCESS" };

        var (result, _) = await Import(db, s, WpsReturnedFileOracle.Build(rows));
        db.SaveChanges();

        result!.PaidApplied.Should().Be(2);
        Reload(db, s.Records[2]).Status.Should().Be("Pending");
        JsonSerializer.Serialize(result.ParseErrors).Should().Contain("status_accepted_only");
    }

    [Fact]
    public async Task A_dry_run_changes_nothing()
    {
        using var db = NewDb();
        var s = Seed(db, 3);

        var (result, _) = await Import(db, s, WpsReturnedFileOracle.Build(s.Rows.Select(r => r with { Status = "PAID" }).ToList()),
            new BankConfirmationImportOptions(DryRun: true));
        db.SaveChanges();

        result!.PaidApplied.Should().Be(3, "the dry run reports what would happen");
        db.PayrollPaymentRecords.Should().OnlyContain(r => r.Status == "Pending");
        db.BankPaymentConfirmations.Should().BeEmpty();
    }

    [Fact]
    public async Task Importing_the_same_file_twice_changes_nothing_the_second_time()
    {
        using var db = NewDb();
        var s = Seed(db, 3);
        var file = WpsReturnedFileOracle.Build(s.Rows.Select(r => r with { Status = "PAID" }).ToList());

        await Import(db, s, file);
        db.SaveChanges();
        var (second, _) = await Import(db, s, file);

        second!.Duplicate.Should().BeTrue();
        db.BankPaymentConfirmations.Should().HaveCount(3);
    }

    // ── Replay: the file must belong to THIS batch ───────────────────────────

    [Fact]
    public async Task Last_months_file_against_this_months_batch_applies_nothing()
    {
        using var db = NewDb();
        var s = Seed(db, 3, createdUtc: new DateTime(2026, 10, 5, 0, 0, 0, DateTimeKind.Utc));   // this month's batch
        var lastMonth = WpsReturnedFileOracle.Build(s.Rows.Select(r => r with { Status = "PAID" }).ToList(),
            new WpsReturnedFileOracle.Header(ValueDate: "20260930"));                              // dated 30 September

        var (result, refusal) = await Import(db, s, lastMonth);
        db.SaveChanges();

        result.Should().BeNull();
        JsonSerializer.Serialize(refusal!.Payload).Should().Contain("file_date_before_batch");
        db.PayrollPaymentRecords.Should().OnlyContain(r => r.Status == "Pending");
    }

    [Fact]
    public async Task A_file_larger_than_the_batch_is_refused()
    {
        using var db = NewDb();
        var s = Seed(db, 3);
        var stranger = WpsReturnedFileOracle.Rows(4)[3] with { MolId = "1555555555", Iban = WpsReturnedFileOracle.SaIban(80, 555_555_555_555L) };
        var rows = s.Rows.Append(stranger).Select(r => r with { Status = "PAID" }).ToList();

        var (result, refusal) = await Import(db, s, WpsReturnedFileOracle.Build(rows));
        db.SaveChanges();

        result.Should().BeNull("a file whose total exceeds the batch's cannot belong to it");
        JsonSerializer.Serialize(refusal!.Payload).Should().Contain("file_total_exceeds_batch");
        db.PayrollPaymentRecords.Should().OnlyContain(r => r.Status == "Pending");
    }

    [Fact]
    public async Task A_trivially_edited_copy_of_an_imported_file_is_still_the_same_file()
    {
        using var db = NewDb();
        var s = Seed(db, 3);
        var rows = s.Rows.Select(r => r with { Status = "PAID" }).ToList();
        await Import(db, s, WpsReturnedFileOracle.Build(rows));
        db.SaveChanges();

        var edited = WpsReturnedFileOracle.Build(rows, newline: "\r\n", bom: true) + "\r\n\r\n";
        var (second, _) = await Import(db, s, edited);

        second!.Duplicate.Should().BeTrue("line endings, BOM and blank lines do not make a new file");
        db.BankPaymentConfirmations.Should().HaveCount(3);
    }

    [Fact]
    public async Task A_file_reference_already_imported_for_the_company_is_refused_even_on_another_batch_with_changed_content()
    {
        using var db = NewDb();
        var a = Seed(db, 3);
        var first = a.Rows.Select(r => r with { Status = "PAID" }).ToList();
        var (ok, _) = await Import(db, a, WpsReturnedFileOracle.Build(first));
        ok!.PaidApplied.Should().Be(3);
        db.SaveChanges();

        // a second batch of the same company, whose file reuses the same FILE-REF
        var b = Seed(db, 3, existing: a.Company, offset: 10);
        var replay = WpsReturnedFileOracle.Build(b.Rows.Select(r => r with { Status = "PAID" }).ToList());   // same default FILE-REF
        var (result, refusal) = await Import(db, b, replay);
        db.SaveChanges();

        result.Should().BeNull();
        JsonSerializer.Serialize(refusal!.Payload).Should().Contain("file_ref_already_imported");
        db.PayrollPaymentRecords.AsNoTracking().Where(r => r.PaymentBatchId == b.BatchId).Should().OnlyContain(r => r.Status == "Pending");

        // a new reference is accepted
        var fresh = WpsReturnedFileOracle.Build(b.Rows.Select(r => r with { Status = "PAID" }).ToList(), new WpsReturnedFileOracle.Header(FileRef: "WPS2026090002"));
        var (accepted, _) = await Import(db, b, fresh);
        accepted!.PaidApplied.Should().Be(3);
    }

    [Fact]
    public async Task A_refused_file_does_not_use_up_its_file_reference()
    {
        using var db = NewDb();
        var s = Seed(db, 3);
        var rows = s.Rows.Select(r => r with { Status = "PAID" }).ToList();
        var bad = WpsReturnedFileOracle.Build(rows, declaredTotal: rows.Sum(r => r.Net) + 1m);
        (await Import(db, s, bad)).Result.Should().BeNull();
        db.SaveChanges();

        var (good, _) = await Import(db, s, WpsReturnedFileOracle.Build(rows));
        good!.PaidApplied.Should().Be(3);
    }

    // ── Establishment: the same resolution the outbound file uses ────────────

    [Fact]
    public async Task A_company_with_no_number_of_its_own_uses_the_workspace_wps_agent_id_like_the_outbound_file()
    {
        using var db = NewDb();
        var s = Seed(db, 2, establishment: "");
        db.GCCComplianceSettings.Add(new GCCComplianceSetting { TenantId = s.TenantId, CompanyId = null, CountryCode = "SA", IsActive = true, WpsAgentId = "7-1234567" });
        db.SaveChanges();

        var (result, refusal) = await Import(db, s, WpsReturnedFileOracle.Build(s.Rows.Select(r => r with { Status = "PAID" }).ToList()));

        refusal.Should().BeNull();
        result!.PaidApplied.Should().Be(2);
    }

    [Fact]
    public async Task Two_different_recorded_establishments_leave_the_company_without_one_and_the_file_is_refused()
    {
        using var db = NewDb();
        var s = Seed(db, 2, establishment: "");
        db.SystemSettings.Add(new SystemSetting
        {
            TenantId = s.TenantId, Category = SaudiBankExportService.SettingsCategory, SettingKey = SaudiBankExportService.SettingsKey(s.Company.Id),
            SettingValue = JsonSerializer.Serialize(new { molEstablishmentId = "7-1111111" }),
        });
        db.GCCComplianceSettings.Add(new GCCComplianceSetting { TenantId = s.TenantId, CompanyId = null, CountryCode = "SA", IsActive = true, WpsAgentId = "7-2222222" });
        db.SaveChanges();

        var (result, refusal) = await Import(db, s, WpsReturnedFileOracle.Build(s.Rows.Select(r => r with { Status = "PAID" }).ToList(),
            new WpsReturnedFileOracle.Header(MolEstablishmentId: "7-1111111")));

        result.Should().BeNull("outbound refuses on the conflict, so the returned file cannot pick one silently");
        JsonSerializer.Serialize(refusal!.Payload).Should().Contain("establishment_not_configured");
    }

    // ── Scope ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Another_tenants_employee_with_the_same_government_id_is_never_matched()
    {
        using var db = NewDb();
        var mine = Seed(db, 2);
        var theirs = Seed(db, 2);   // a second tenant holding the SAME synthetic ids
        var file = WpsReturnedFileOracle.Build(mine.Rows.Select(r => r with { Status = "PAID" }).ToList());

        var (result, _) = await Import(db, mine, file);
        db.SaveChanges();

        result!.PaidApplied.Should().Be(2);
        db.PayrollPaymentRecords.AsNoTracking().Where(r => r.TenantId == theirs.TenantId).Should().OnlyContain(r => r.Status == "Pending");
        db.BankPaymentConfirmations.Should().OnlyContain(c => c.TenantId == mine.TenantId);
    }

    [Fact]
    public async Task A_company_with_no_establishment_on_record_refuses_the_file()
    {
        using var db = NewDb();
        var s = Seed(db, 2, establishment: "");

        var (result, refusal) = await Import(db, s, WpsReturnedFileOracle.Build(s.Rows.Select(r => r with { Status = "PAID" }).ToList()));

        result.Should().BeNull();
        JsonSerializer.Serialize(refusal!.Payload).Should().Contain("establishment_not_configured");
    }

    [Fact]
    public async Task A_file_for_the_other_company_of_the_same_tenant_is_refused_and_each_company_uses_its_own_establishment()
    {
        using var db = NewDb();
        var a = Seed(db, 2, establishment: "7-1111111");
        // A second company in the SAME tenant with its own establishment and its own batch.
        var b = Seed(db, 2, establishment: "7-2222222", tenant: a.TenantId);

        var forA = WpsReturnedFileOracle.Build(a.Rows.Select(r => r with { Status = "PAID" }).ToList(),
            new WpsReturnedFileOracle.Header(MolEstablishmentId: "7-1111111"));

        var (refused, refusal) = await Import(db, b, forA);
        refused.Should().BeNull("company B's batch must not accept a file that carries company A's establishment");
        JsonSerializer.Serialize(refusal!.Payload).Should().Contain("establishment_mismatch");

        var (ok, none) = await Import(db, a, forA);
        none.Should().BeNull();
        ok!.PaidApplied.Should().Be(2);
        db.SaveChanges();
        db.PayrollPaymentRecords.AsNoTracking().Where(r => r.PaymentBatchId == b.BatchId).Should().OnlyContain(r => r.Status == "Pending");
    }

    // ── Size ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_3000_line_returned_file_is_reconciled_through_the_import()
    {
        using var db = NewDb();
        var s = Seed(db, 3000, arabicName: new string('م', 35));
        var rows = s.Rows.Select(r => r with { Status = "PAID" }).ToList();
        rows[7] = rows[7] with { Status = "REJECTED" };

        var (result, refusal) = await Import(db, s, WpsReturnedFileOracle.Build(rows));
        refusal.Should().BeNull();

        result!.PaidApplied.Should().Be(2999);
        result.ReturnedApplied.Should().Be(1);
        result.Unconfirmed.Should().BeEmpty();
        result.Coverage.RecordCount.Should().Be(3000);
    }
}
