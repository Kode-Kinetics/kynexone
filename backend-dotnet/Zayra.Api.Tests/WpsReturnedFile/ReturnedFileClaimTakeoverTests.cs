using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Finance;
using Zayra.Api.Infrastructure.Government;
using Zayra.Api.Models;
using static Zayra.Api.Tests.WpsReturnedFile.WpsReturnedFileImportPathTests;

namespace Zayra.Api.Tests.WpsReturnedFile;

/// <summary>
/// The returned-file claim over time (final gate P2-8 and P2-9): a claim left Attempted by a request that died is taken over after
/// <see cref="ReturnedFileClaim.StaleAfter"/>, and an import whose claim was taken over (or is too old to be safe) while it stalled
/// is refused at save with 409 and applies nothing (fencing). Synthetic data.
/// </summary>
public class ReturnedFileClaimTakeoverTests
{
    /// <summary>One in-memory store; contexts on it share data.</summary>
    private sealed class Store
    {
        private readonly string _name = Guid.NewGuid().ToString();
        private readonly InMemoryDatabaseRoot _root = new();

        public ZayraDbContext Db() => new(new DbContextOptionsBuilder<ZayraDbContext>().UseInMemoryDatabase(_name, _root).Options);

        public BankConfirmationService Service(ZayraDbContext db) =>
            new(db, new IBankConfirmationParser[] { new WpsReturnedFileBankConfirmationParser(statusColumnVerified: true) },
                new ReturnedFileClaim(new GovernmentExchangeRecorder(() => Db())));

        public List<GovernmentExchange> Imports()
        {
            using var db = Db();
            return db.GovernmentExchanges.IgnoreQueryFilters().AsNoTracking()
                .Where(x => x.Operation == ReturnedFileClaim.ImportOperation).OrderBy(x => x.CreatedAtUtc).ToList();
        }

        /// <summary>Moves a claim's start back in time, as if its request had stalled for <paramref name="by"/>.</summary>
        public void Age(Guid claimId, TimeSpan by)
        {
            using var db = Db();
            var row = db.GovernmentExchanges.IgnoreQueryFilters().Single(x => x.Id == claimId);
            row.CreatedAtUtc -= by;
            db.SaveChanges();
        }
    }

    private static string Paid(Seeded s) => WpsReturnedFileOracle.Build(s.Rows.Select(r => r with { Status = "PAID" }).ToList());

    private static Task<(BankConfirmationImportResult? Result, JournalExportRefusal? Refusal)> Import(Store store, ZayraDbContext db, Seeded s) =>
        store.Service(db).ImportAsync(s.TenantId, s.BatchId, "returned.txt", Paid(s), "wps-returned-file", new BankConfirmationImportOptions(),
            Guid.NewGuid(), "Payroll Officer", default);

    private static TimeSpan PastStale => ReturnedFileClaim.StaleAfter + TimeSpan.FromMinutes(1);

    [Fact]
    public async Task A_claim_left_attempted_by_a_dead_request_blocks_until_stale_then_is_taken_over_by_exactly_the_next_import()
    {
        var store = new Store();
        Seeded s;
        using (var seed = store.Db()) s = Seed(seed, 3);

        using (var dead = store.Db())
        {
            var (abandoned, _) = await Import(store, dead, s);   // claimed, never saved: the request died
            abandoned.Should().NotBeNull();
        }

        using (var tooSoon = store.Db())
        {
            var (_, refusal) = await Import(store, tooSoon, s);
            refusal!.Status.Should().Be(409);
            JsonSerializer.Serialize(refusal.Payload).Should().Contain(ReturnedFileClaim.InProgressCode);
        }

        store.Age(store.Imports().Single().Id, PastStale);

        using var retry = store.Db();
        var service = store.Service(retry);
        var (taken, none) = await Import(store, retry, s);
        none.Should().BeNull("a claim older than StaleAfter is passed over");
        (await service.SaveAsync(taken!, default)).Should().BeNull();
        taken!.PaidApplied.Should().Be(3);
        store.Imports().Select(x => x.State).Should().Equal(GovernmentExchangeStates.Attempted, GovernmentExchangeStates.Recorded);
    }

    [Fact]
    public async Task An_import_whose_claim_was_taken_over_while_it_stalled_is_refused_at_save_and_applies_nothing()
    {
        var store = new Store();
        Seeded s;
        using (var seed = store.Db()) s = Seed(seed, 3);

        using var stalled = store.Db();
        var stalledService = store.Service(stalled);
        var (first, _) = await Import(store, stalled, s);   // claimed and computed, then the request stalls before its save
        first.Should().NotBeNull();
        store.Age(store.Imports().Single().Id, PastStale);

        using (var other = store.Db())
        {
            var otherService = store.Service(other);
            var (second, refusal) = await Import(store, other, s);
            refusal.Should().BeNull("the stale claim is taken over");
            (await otherService.SaveAsync(second!, default)).Should().BeNull();
        }

        var fenced = await stalledService.SaveAsync(first!, default);

        fenced.Should().NotBeNull();
        fenced!.Status.Should().Be(409);
        JsonSerializer.Serialize(fenced.Payload).Should().Contain(ReturnedFileClaim.TakenOverCode);
        stalled.ChangeTracker.HasChanges().Should().BeFalse("nothing of the stalled import is left to be saved by accident");
        using var check = store.Db();
        check.BankPaymentConfirmations.Count().Should().Be(3, "only the import that holds the live claim applied its lines");
        check.PayrollAuditLogs.Count(a => a.Action == "payroll.bank_confirmation.imported").Should().Be(1);
    }

    [Fact]
    public async Task An_import_whose_claim_is_too_old_to_be_safe_is_refused_at_save_and_the_file_can_be_imported_again()
    {
        var store = new Store();
        Seeded s;
        using (var seed = store.Db()) s = Seed(seed, 3);

        using (var stalled = store.Db())
        {
            var service = store.Service(stalled);
            var (first, _) = await Import(store, stalled, s);
            store.Age(store.Imports().Single().Id, PastStale);   // nobody took it over yet, but anyone now may

            var fenced = await service.SaveAsync(first!, default);

            fenced!.Status.Should().Be(409);
        }
        using (var check = store.Db()) check.BankPaymentConfirmations.Should().BeEmpty();

        using var retry = store.Db();
        var again = store.Service(retry);
        var (second, refusal) = await Import(store, retry, s);
        refusal.Should().BeNull();
        (await again.SaveAsync(second!, default)).Should().BeNull();
        second!.PaidApplied.Should().Be(3);
    }
}
