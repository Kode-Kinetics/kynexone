using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage;
using Zayra.Api.Application.Auth;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Finance;
using Zayra.Api.Infrastructure.Government;
using Zayra.Api.Models;
using static Zayra.Api.Tests.WpsReturnedFile.WpsReturnedFileImportPathTests;

namespace Zayra.Api.Tests.WpsReturnedFile;

/// <summary>
/// The returned-file claim: before anything is applied, the import records in the government exchange ledger (body bank_file,
/// channel file_in, environment production) a bind row (the bank's FILE-REF belongs to ONE payment batch) and a replay row
/// (this exact file is being imported). Another batch is refused (409); the identical file while the first is in flight or after
/// it landed is refused; a save that fails releases the claim (Failed), so the same file can be imported again. Synthetic data.
/// </summary>
public class ReturnedFileClaimTests
{
    private sealed class FailingSave : SaveChangesInterceptor
    {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result,
            CancellationToken cancellationToken = default) => throw new DbUpdateException("synthetic save failure");
        public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result) =>
            throw new DbUpdateException("synthetic save failure");
    }

    /// <summary>One in-memory store; contexts on it share data. <paramref name="failSaves"/> makes that context's saves throw.</summary>
    private sealed class Store
    {
        private readonly string _name = Guid.NewGuid().ToString();
        private readonly InMemoryDatabaseRoot _root = new();

        public ZayraDbContext Db(bool failSaves = false)
        {
            var b = new DbContextOptionsBuilder<ZayraDbContext>().UseInMemoryDatabase(_name, _root);
            if (failSaves) b.AddInterceptors(new FailingSave());
            return new ZayraDbContext(b.Options);
        }

        public BankConfirmationService Service(ZayraDbContext db) =>
            new(db, new IBankConfirmationParser[] { new WpsReturnedFileBankConfirmationParser(statusColumnVerified: true) },
                new ReturnedFileClaim(new GovernmentExchangeRecorder(() => Db())));

        public List<GovernmentExchange> Ledger()
        {
            using var db = Db();
            return db.GovernmentExchanges.IgnoreQueryFilters().AsNoTracking().OrderBy(x => x.CreatedAtUtc).ToList();
        }
    }

    private static string Paid(Seeded s, string? fileRef = null) =>
        WpsReturnedFileOracle.Build(s.Rows.Select(r => r with { Status = "PAID" }).ToList(),
            fileRef is null ? null : new WpsReturnedFileOracle.Header(FileRef: fileRef));

    private static Task<(BankConfirmationImportResult? Result, JournalExportRefusal? Refusal)> Import(
        Store store, ZayraDbContext db, Seeded s, string content, BankConfirmationImportOptions? options = null) =>
        store.Service(db).ImportAsync(s.TenantId, s.BatchId, "returned.txt", content, "wps-returned-file",
            options ?? new BankConfirmationImportOptions(), Guid.NewGuid(), "Payroll Officer", default);

    // ── the ledger body ───────────────────────────────────────────────────────

    [Fact]
    public void The_bank_file_body_is_a_ledger_only_body_and_never_a_counterparty_with_credentials_or_a_live_connection()
    {
        GovernmentBodies.BankFile.Should().Be("bank_file");
        GovernmentBodies.All.Should().NotContain(GovernmentBodies.BankFile, "no credential, base URL, capability or live gate exists for it");
        GovernmentBodies.IsLedgerBody(GovernmentBodies.BankFile).Should().BeTrue();
        GovernmentBodies.IsLedgerBody(GovernmentBodies.BankAnb).Should().BeTrue();
        GovernmentBodies.IsKnown(GovernmentBodies.BankFile).Should().BeFalse();
    }

    [Theory]
    [InlineData(GovernmentExchangeChannels.FileIn, true)]
    [InlineData(GovernmentExchangeChannels.Api, false)]
    public async Task The_recorder_takes_a_bank_file_row_only_on_a_file_channel(string channel, bool accepted)
    {
        var store = new Store();
        var recorder = new GovernmentExchangeRecorder(() => store.Db());
        var attempt = new GovernmentExchangeAttempt(Guid.NewGuid(), Guid.NewGuid(), GovernmentBodies.BankFile, "returned_file.bind", channel,
            "CO|REF", GovernmentExchangeStates.Recorded, new RequestContext("server", "test"), Environment: GovernmentEnvironments.Production);

        var act = () => recorder.BeginAsync(attempt, default);

        if (accepted) (await act()).Created.Should().BeTrue();
        else await act.Should().ThrowAsync<ArgumentException>();
    }

    // ── the claim through the import ──────────────────────────────────────────

    [Fact]
    public async Task An_applied_import_records_a_bind_row_and_a_replay_row_that_land_as_recorded()
    {
        var store = new Store();
        using var db = store.Db();
        var s = Seed(db, 3);
        var service = store.Service(db);

        var (result, refusal) = await service.ImportAsync(s.TenantId, s.BatchId, "r.txt", Paid(s), "wps-returned-file",
            new BankConfirmationImportOptions(), Guid.NewGuid(), "x", default);
        refusal.Should().BeNull();
        await service.SaveAsync(result!, default);

        var ledger = store.Ledger();
        ledger.Should().HaveCount(2).And.OnlyContain(x => x.Body == "bank_file" && x.Channel == "file_in" && x.Environment == "production"
            && x.TenantId == s.TenantId && x.CompanyId == s.Company.Id && x.SubjectId == s.BatchId.ToString() && !x.IsLive);
        ledger.Single(x => x.Operation == ReturnedFileClaim.BindOperation).State.Should().Be(GovernmentExchangeStates.Recorded);
        var replay = ledger.Single(x => x.Operation == ReturnedFileClaim.ImportOperation);
        replay.State.Should().Be(GovernmentExchangeStates.Recorded);
        replay.ArtifactSha256.Should().Be(result!.SourceFileHash);
        s.Records.Should().OnlyContain(r => Reload(db, r).Status == "Paid");
    }

    [Fact]
    public async Task The_same_file_reference_on_another_batch_is_refused_409_even_before_the_first_import_is_saved()
    {
        var store = new Store();
        using var seed = store.Db();
        var a = Seed(seed, 3);
        var b = Seed(seed, 3, existing: a.Company, offset: 10);

        using var first = store.Db();
        using var second = store.Db();
        var (ra, _) = await Import(store, first, a, Paid(a));
        var (rb, refusal) = await Import(store, second, b, Paid(b));    // same FILE-REF, the first is not saved yet

        ra.Should().NotBeNull();
        rb.Should().BeNull();
        refusal!.Status.Should().Be(409);
        JsonSerializer.Serialize(refusal.Payload).Should().Contain(ReturnedFileClaim.BoundToOtherBatchCode);
    }

    [Fact]
    public async Task The_identical_file_while_the_first_import_is_in_flight_is_refused_409()
    {
        var store = new Store();
        using var seed = store.Db();
        var s = Seed(seed, 3);

        using var first = store.Db();
        using var second = store.Db();
        var (r1, _) = await Import(store, first, s, Paid(s));
        var (r2, refusal) = await Import(store, second, s, Paid(s));

        r1.Should().NotBeNull();
        r2.Should().BeNull();
        refusal!.Status.Should().Be(409);
        JsonSerializer.Serialize(refusal.Payload).Should().Contain(ReturnedFileClaim.InProgressCode);
    }

    [Fact]
    public async Task The_banks_later_status_file_for_the_same_batch_is_claimed_as_a_new_file()
    {
        var store = new Store();
        using var db = store.Db();
        var s = Seed(db, 3);
        var service = store.Service(db);
        var (pending, _) = await service.ImportAsync(s.TenantId, s.BatchId, "p.txt",
            WpsReturnedFileOracle.Build(s.Rows.Select(r => r with { Status = "PENDING" }).ToList()), "wps-returned-file", new(), null, "x", default);
        await service.SaveAsync(pending!, default);

        var (paid, refusal) = await service.ImportAsync(s.TenantId, s.BatchId, "q.txt", Paid(s), "wps-returned-file", new(), null, "x", default);
        refusal.Should().BeNull();
        await service.SaveAsync(paid!, default);

        paid!.PaidApplied.Should().Be(3);
        store.Ledger().Count(x => x.Operation == ReturnedFileClaim.BindOperation).Should().Be(1, "one reference, one batch, one bind row");
        store.Ledger().Count(x => x.Operation == ReturnedFileClaim.ImportOperation).Should().Be(2);
    }

    [Fact]
    public async Task A_save_that_fails_after_the_claim_marks_it_failed_and_the_same_file_can_be_imported_again()
    {
        var store = new Store();
        Seeded s;
        using (var seed = store.Db()) s = Seed(seed, 3);

        using (var failing = store.Db(failSaves: true))
        {
            var service = store.Service(failing);
            var (result, refusal) = await service.ImportAsync(s.TenantId, s.BatchId, "r.txt", Paid(s), "wps-returned-file", new(), null, "x", default);
            refusal.Should().BeNull();
            var save = () => service.SaveAsync(result!, default);
            await save.Should().ThrowAsync<DbUpdateException>();
        }
        store.Ledger().Single(x => x.Operation == ReturnedFileClaim.ImportOperation).State.Should().Be(GovernmentExchangeStates.Failed);

        using var retry = store.Db();
        var again = store.Service(retry);
        var (second, none) = await again.ImportAsync(s.TenantId, s.BatchId, "r.txt", Paid(s), "wps-returned-file", new(), null, "x", default);
        none.Should().BeNull("a failed claim is released, so the same file can be imported again");
        await again.SaveAsync(second!, default);
        second!.PaidApplied.Should().Be(3);
        store.Ledger().Where(x => x.Operation == ReturnedFileClaim.ImportOperation).Select(x => x.State)
            .Should().BeEquivalentTo(new[] { GovernmentExchangeStates.Failed, GovernmentExchangeStates.Recorded });
    }

    [Fact]
    public async Task A_dry_run_claims_nothing()
    {
        var store = new Store();
        using var db = store.Db();
        var s = Seed(db, 3);
        var (result, _) = await Import(store, db, s, Paid(s), new BankConfirmationImportOptions(DryRun: true));
        result!.PaidApplied.Should().Be(3);
        store.Ledger().Should().BeEmpty();
    }

    [Fact]
    public async Task Without_a_claim_the_returned_file_import_refuses_to_apply_anything()
    {
        using var db = NewDb();
        var s = Seed(db, 3);
        var service = new BankConfirmationService(db, new IBankConfirmationParser[] { new WpsReturnedFileBankConfirmationParser(statusColumnVerified: true) });

        var act = () => service.ImportAsync(s.TenantId, s.BatchId, "r.txt", Paid(s), "wps-returned-file", new(), null, "x", default);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*claim*");
        db.ChangeTracker.HasChanges().Should().BeFalse();
    }

    [Fact]
    public async Task A_parser_with_no_import_key_is_never_claimed()
    {
        var store = new Store();
        using var db = store.Db();
        var s = Seed(db, 2);
        var service = new BankConfirmationService(db, new IBankConfirmationParser[] { new GenericCsvBankConfirmationParser() },
            new ReturnedFileClaim(new GovernmentExchangeRecorder(() => store.Db())));
        var csv = "EmployeeCode,Status\nEMP0001,PAID\n";

        var (result, _) = await service.ImportAsync(s.TenantId, s.BatchId, "g.csv", csv, "generic-csv", new(), null, "x", default);
        await service.SaveAsync(result!, default);

        result!.PaidApplied.Should().Be(1);
        store.Ledger().Should().BeEmpty();
    }
}
