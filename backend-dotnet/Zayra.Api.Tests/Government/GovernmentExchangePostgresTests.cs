using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Auth;
using Zayra.Api.Application.Common;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Audit;
using Zayra.Api.Infrastructure.Government;
using Zayra.Api.Models;

namespace Zayra.Api.Tests.Government;

/// <summary>
/// The ledger's keys and tokens on a real database: concurrent begins with one key (one row), concurrent begins with different
/// keys for one batch (one row), a key held by another company (409, never 500), concurrent updates (no lost update), the
/// database honesty CHECK, and a caller with an open transaction and its own pending audit row (untouched).
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration")]
public sealed class GovernmentExchangePostgresTests(PostgresFixture fixture)
{
    private sealed class LiveBank : IGovernmentAdapter
    {
        public string Body => GovernmentBodies.BankAnb;
        public string AdapterName => "live-bank-double";
        public bool IsLiveIntegration => true;
    }

    private async Task<(Guid TenantId, Guid CompanyA, Guid CompanyB)> SeedAsync()
    {
        await using var seed = fixture.CreateDb();
        var tenantId = await PostgresFixture.SeedMinimalTenant(seed);
        var a = new Company { Id = Guid.NewGuid(), TenantId = tenantId, LegalNameEn = "Synthetic A", IsActive = true };
        var b = new Company { Id = Guid.NewGuid(), TenantId = tenantId, LegalNameEn = "Synthetic B", IsActive = true };
        seed.Companies.AddRange(a, b);
        await seed.SaveChangesAsync();
        return (tenantId, a.Id, b.Id);
    }

    private static GovernmentExchangeAttempt Intent(Guid tenant, Guid company, string key, string batch) =>
        new(tenant, company, GovernmentBodies.BankAnb, "payroll.submit", GovernmentExchangeChannels.Api, key, GovernmentExchangeStates.SubmitIntended,
            new RequestContext("127.0.0.1", "test", Guid.NewGuid(), tenant), new LiveBank(), Environment: "sandbox", SubjectType: "PaymentBatch",
            SubjectId: batch, RequestBody: "synthetic"u8.ToArray(), ArtifactSha256: new string('a', 64), AccountFingerprint: "acct-fp-synthetic",
            BatchReference: batch, TotalAmount: 1000m, ItemCount: 3000);

    private static async Task<T[]> RaceAsync<T>(int n, Func<Task<T>> work)
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runs = Enumerable.Range(0, n).Select(_ => Task.Run(async () => { await gate.Task; return await work(); })).ToArray();
        gate.SetResult();
        return await Task.WhenAll(runs);
    }

    private static async Task<object> Try<T>(Func<Task<T>> work)
    {
        try { return (await work())!; }
        catch (GovernmentExchangeConflictException conflict) { return conflict; }
    }

    [Fact]
    public async Task TwoConcurrentBegins_WithTheSameKey_YieldOneRow()
    {
        var (tenantId, company, _) = await SeedAsync();
        var key = $"batch-{Guid.NewGuid():N}";
        var recorder = new GovernmentExchangeRecorder(fixture.CreateDb);

        var results = await RaceAsync(8, () => recorder.BeginAsync(Intent(tenantId, company, key, "B-1"), CancellationToken.None));

        Assert.Single(results, r => r.Created);
        Assert.Single(results.Select(r => r.Exchange.Id).Distinct());
        await using var verify = fixture.CreateDb();
        Assert.Equal(1, await verify.GovernmentExchanges.IgnoreQueryFilters().CountAsync(x => x.TenantId == tenantId && x.IdempotencyKey == key));
        Assert.Equal(1, await verify.AuditLogs.IgnoreQueryFilters().CountAsync(x => x.TenantId == tenantId && x.Action == "government.exchange.recorded"));
    }

    [Fact]
    public async Task ConcurrentBegins_WithDifferentKeys_ForOneBatch_YieldOneRow_AndConflicts()
    {
        var (tenantId, company, _) = await SeedAsync();
        var recorder = new GovernmentExchangeRecorder(fixture.CreateDb);

        var results = await RaceAsync(8, () => Try(() => recorder.BeginAsync(Intent(tenantId, company, $"key-{Guid.NewGuid():N}", "B-SAME"), CancellationToken.None)));

        Assert.Single(results.OfType<GovernmentExchangeRecordResult>(), r => r.Created);
        Assert.Equal(7, results.OfType<GovernmentExchangeConflictException>().Count());
        await using var verify = fixture.CreateDb();
        Assert.Equal(1, await verify.GovernmentExchanges.IgnoreQueryFilters().CountAsync(x => x.TenantId == tenantId && x.BatchReference == "B-SAME"));
    }

    [Fact]
    public async Task AKeyHeldByAnotherCompany_IsA409_NotA500()
    {
        var (tenantId, companyA, companyB) = await SeedAsync();
        await new GovernmentExchangeRecorder(fixture.CreateDb).BeginAsync(Intent(tenantId, companyA, "shared-key", "B-A"), CancellationToken.None);
        var userB = new HttpContextAccessor
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(
                [
                    new Claim("tenant_id", tenantId.ToString()), new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
                    new Claim(EntityScopeContext.V2ClaimType, JsonSerializer.Serialize(new { v = 2, m = "companies", c = new[] { companyB.ToString() } })),
                ], "test")),
            },
        };
        var recorderB = new GovernmentExchangeRecorder(() => fixture.CreateDbWithAccessor(userB));

        var collide = () => recorderB.BeginAsync(Intent(tenantId, companyB, "shared-key", "B-B"), CancellationToken.None);

        var conflict = await Assert.ThrowsAsync<GovernmentExchangeConflictException>(collide);
        Assert.Equal(GovernmentExchangeRecorder.KeyInUseCode, conflict.Code);
    }

    [Fact]
    public async Task ConcurrentUpdates_LoseNothing_EachWinnerBumpsTheVersion()
    {
        var (tenantId, company, _) = await SeedAsync();
        var recorder = new GovernmentExchangeRecorder(fixture.CreateDb);
        var id = (await recorder.BeginAsync(Intent(tenantId, company, $"k-{Guid.NewGuid():N}", "B-POLL"), CancellationToken.None)).Exchange.Id;
        var ctx = new RequestContext("127.0.0.1", "test", null, tenantId);
        await recorder.UpdateAsync(tenantId, id, new GovernmentExchangeOutcome(GovernmentExchangeStates.Accepted, ctx, ExternalReference: $"ANB-{Guid.NewGuid():N}"), CancellationToken.None);

        var results = await RaceAsync(8, () => Try(() => recorder.UpdateAsync(tenantId, id,
            new GovernmentExchangeOutcome(GovernmentExchangeStates.Accepted, ctx, IsPoll: true), CancellationToken.None)));

        var winners = results.OfType<GovernmentExchange>().Count();
        Assert.True(winners >= 1);
        await using var verify = fixture.CreateDb();
        var row = await verify.GovernmentExchanges.IgnoreQueryFilters().SingleAsync(x => x.Id == id);
        Assert.Equal(2 + winners, row.StateVersion);
        Assert.Equal(winners, row.PollCount);
    }

    private static GovernmentExchangeAttempt FileRecord(Guid tenant, Guid company, string key) =>
        new(tenant, company, GovernmentBodies.Gosi, "contributions.file", GovernmentExchangeChannels.FileOut, key, GovernmentExchangeStates.Recorded,
            new RequestContext("127.0.0.1", "test", Guid.NewGuid(), tenant));

    [Fact]
    public async Task InsideTheCallersTransaction_ARecordUsesASavepoint_AnIntentIsRefused_AndTheCallersWorkIsLeftAlone()
    {
        var (tenantId, company, _) = await SeedAsync();
        await using var caller = fixture.CreateDb();
        GovernmentExchangeRecordResult first = null!, replay = null!;
        InvalidOperationException intent = null!, outcome = null!;
        // The production provider retries, so a user transaction runs inside its execution strategy (as the product's own do).
        await caller.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            await using var tx = await caller.Database.BeginTransactionAsync();
            IAuditService callerAudit = new AuditService(caller);
            // The caller has already written (and so holds the tenant's audit lock) inside its transaction.
            callerAudit.Stage("payroll.batch_prepared", "PaymentBatch", "B-TX", new RequestContext("127.0.0.1", "test", null, tenantId), "{}", company);
            await caller.SaveChangesAsync();
            var recorder = new GovernmentExchangeRecorder(fixture.CreateDb, ambient: caller);

            // A record of something filed by hand joins the transaction under a savepoint, on the caller's own connection.
            first = await recorder.BeginAsync(FileRecord(tenantId, company, "file-key"), CancellationToken.None);
            replay = await recorder.BeginAsync(FileRecord(tenantId, company, "file-key"), CancellationToken.None);
            // An intent, or an outcome, is refused here: it would not be durable.
            intent = await Assert.ThrowsAsync<InvalidOperationException>(() => recorder.BeginAsync(Intent(tenantId, company, "tx-key", "B-TX"), CancellationToken.None));
            outcome = await Assert.ThrowsAsync<InvalidOperationException>(() => recorder.UpdateAsync(tenantId, first.Exchange.Id,
                new GovernmentExchangeOutcome(GovernmentExchangeStates.Failed, new RequestContext("127.0.0.1", "test", null, tenantId)), CancellationToken.None));

            // Unsaved work of the caller is never flushed by the ledger: it is refused instead.
            callerAudit.Stage("payroll.batch_note", "PaymentBatch", "B-TX", new RequestContext("127.0.0.1", "test", null, tenantId), "{}", company);
            var unsaved = await Assert.ThrowsAsync<InvalidOperationException>(
                () => recorder.BeginAsync(FileRecord(tenantId, company, "file-key-2"), CancellationToken.None));
            Assert.StartsWith(GovernmentExchangeRecorder.CallerHasUnsavedChangesCode, unsaved.Message);
            await caller.SaveChangesAsync();
            await tx.CommitAsync();
        });

        Assert.True(first.Created);
        Assert.Null(first.Receipt);
        Assert.False(replay.Created);
        Assert.StartsWith(GovernmentExchangeRecorder.IntentMustBeCommittedCode, intent.Message);
        Assert.StartsWith(GovernmentExchangeRecorder.IntentMustBeCommittedCode, outcome.Message);
        await using var verify = fixture.CreateDb();
        Assert.Equal(1, await verify.AuditLogs.IgnoreQueryFilters().CountAsync(x => x.TenantId == tenantId && x.Action == "payroll.batch_prepared"));
        Assert.Equal(1, await verify.AuditLogs.IgnoreQueryFilters().CountAsync(x => x.TenantId == tenantId && x.Action == "payroll.batch_note"));
        Assert.Equal(1, await verify.GovernmentExchanges.IgnoreQueryFilters().CountAsync(x => x.TenantId == tenantId));
        Assert.Equal(0, await verify.GovernmentExchanges.IgnoreQueryFilters().CountAsync(x => x.TenantId == tenantId && x.BatchReference == "B-TX"));
    }

    [Fact]
    public async Task AnOuterTransactionRolledBackAfterTheIntentCall_CannotLeadToASend()
    {
        var (tenantId, company, _) = await SeedAsync();
        var handler = new GovernmentLiveGateTests.CountingHandler();
        var http = new GovernmentHttp(new GovernmentLiveGateTests.SingleHandlerFactory(handler),
            GovernmentLiveGateTests.Config((GovernmentHttp.BaseUrlKey(GovernmentBodies.BankAnb, "sandbox"), "https://bank-sandbox.example.test")));
        var write = GovernmentLivePolicy.DecideLiveWrite(null, GovernmentBodies.BankAnb, new GovernmentLiveGates(true, true, true, true), "sandbox");
        await using var caller = fixture.CreateDb();

        await caller.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            await using var tx = await caller.Database.BeginTransactionAsync();
            IAuditService callerAudit = new AuditService(caller);
            callerAudit.Stage("payroll.batch_prepared", "PaymentBatch", "B-RB", new RequestContext("127.0.0.1", "test", null, tenantId), "{}", company);
            await caller.SaveChangesAsync();
            var recorder = new GovernmentExchangeRecorder(fixture.CreateDb, ambient: caller);

            GovernmentExchangeIntentReceipt? receipt = null;
            try { receipt = (await recorder.BeginAsync(Intent(tenantId, company, "rb-key", "B-RB"), CancellationToken.None)).Receipt; }
            catch (InvalidOperationException refused) { Assert.StartsWith(GovernmentExchangeRecorder.IntentMustBeCommittedCode, refused.Message); }
            Assert.Null(receipt);
            await Assert.ThrowsAsync<GovernmentTransportRefusedException>(async () =>
            {
                using var _ = await http.ExchangeAsync(write, "sandbox", HttpMethod.Post, "/payroll/v2", new StringContent("{}"), null, CancellationToken.None, receipt);
            });
            await tx.RollbackAsync();
        });

        Assert.Equal(0, handler.Calls);
        await using var verify = fixture.CreateDb();
        Assert.Equal(0, await verify.GovernmentExchanges.IgnoreQueryFilters().CountAsync(x => x.TenantId == tenantId));

        // Committed first, outside any transaction: the receipt opens exactly one send.
        var committed = await new GovernmentExchangeRecorder(fixture.CreateDb).BeginAsync(Intent(tenantId, company, "rb-key", "B-RB"), CancellationToken.None);
        Assert.NotNull(committed.Receipt);
        using (await http.ExchangeAsync(write, "sandbox", HttpMethod.Post, "/payroll/v2", new StringContent("{}"), null, CancellationToken.None, committed.Receipt)) { }
        Assert.Equal(1, handler.Calls);
        // A replay of the same key gets no receipt: a retried job can never send twice.
        Assert.Null((await new GovernmentExchangeRecorder(fixture.CreateDb).BeginAsync(Intent(tenantId, company, "rb-key", "B-RB"), CancellationToken.None)).Receipt);
    }

    [Fact]
    public async Task SandboxAndProduction_WithTheSameBatchNumberAndBankId_DoNotCollide()
    {
        var (tenantId, company, _) = await SeedAsync();
        var recorder = new GovernmentExchangeRecorder(fixture.CreateDb);
        var ctx = new RequestContext("127.0.0.1", "test", null, tenantId);

        var sandbox = await recorder.BeginAsync(Intent(tenantId, company, "env-sandbox", "B-ENV"), CancellationToken.None);
        var production = await recorder.BeginAsync(Intent(tenantId, company, "env-production", "B-ENV") with { Environment = "production" }, CancellationToken.None);
        await recorder.UpdateAsync(tenantId, sandbox.Exchange.Id, new GovernmentExchangeOutcome(GovernmentExchangeStates.Accepted, ctx, ExternalReference: "1001"), CancellationToken.None);
        await recorder.UpdateAsync(tenantId, production.Exchange.Id, new GovernmentExchangeOutcome(GovernmentExchangeStates.Accepted, ctx, ExternalReference: "1001"), CancellationToken.None);

        Assert.True(sandbox.Created);
        Assert.True(production.Created);
        await using var verify = fixture.CreateDb();
        Assert.Equal(2, await verify.GovernmentExchanges.IgnoreQueryFilters().CountAsync(x => x.TenantId == tenantId && x.ExternalReference == "1001"));
        // Within one environment the batch is still held.
        await Assert.ThrowsAsync<GovernmentExchangeConflictException>(() =>
            recorder.BeginAsync(Intent(tenantId, company, "env-sandbox-2", "B-ENV"), CancellationToken.None));
    }

    [Fact]
    public async Task TheDatabase_RefusesALiveOnlyStateOnANonLiveRow_AndAnIntentWithoutItsBatch()
    {
        var (tenantId, company, _) = await SeedAsync();
        await using (var db = fixture.CreateDb())
        {
            db.GovernmentExchanges.Add(new GovernmentExchange
            {
                TenantId = tenantId, Body = GovernmentBodies.BankAnb, Operation = "payroll.submit", Channel = GovernmentExchangeChannels.Manual,
                IdempotencyKey = Guid.NewGuid().ToString(), State = GovernmentExchangeStates.Accepted, Adapter = "manual", IsLive = false,
            });
            await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        }
        await using (var db = fixture.CreateDb())
        {
            db.GovernmentExchanges.Add(new GovernmentExchange
            {
                TenantId = tenantId, CompanyId = company, Body = GovernmentBodies.BankAnb, Operation = "payroll.submit", Channel = GovernmentExchangeChannels.Api,
                IdempotencyKey = Guid.NewGuid().ToString(), State = GovernmentExchangeStates.SubmitIntended, Adapter = "x", IsLive = true,
                ArtifactSha256 = new string('a', 64), SubjectId = "B-9",
            });
            await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        }
    }
}
