using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Auth;
using Zayra.Api.Infrastructure.Audit;
using Zayra.Api.Infrastructure.Government;
using Zayra.Api.Models;

namespace Zayra.Api.Tests.Government;

/// <summary>
/// The ledger's unique key on a real database: two people (or a worker and a retry) pressing Submit at the same moment with
/// the same idempotency key produce exactly one attempt row and one audit row, and both callers get that row back.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration")]
public sealed class GovernmentExchangePostgresTests(PostgresFixture fixture)
{
    [Fact]
    public async Task TwoConcurrentBegins_WithTheSameKey_YieldOneRow()
    {
        Guid tenantId;
        await using (var seed = fixture.CreateDb())
            tenantId = await PostgresFixture.SeedMinimalTenant(seed);

        var key = $"batch-{Guid.NewGuid():N}";
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        async Task<GovernmentExchangeRecordResult> BeginAsync()
        {
            await using var db = fixture.CreateDb();
            var recorder = new GovernmentExchangeRecorder(db, new AuditService(db));
            await gate.Task;
            return await recorder.BeginAsync(new GovernmentExchangeAttempt(tenantId, null, GovernmentBodies.BankAnb, "payroll.submit",
                GovernmentExchangeChannels.Api, key, GovernmentExchangeStates.SubmitIntended,
                new RequestContext("127.0.0.1", "test", null, tenantId), Environment: "sandbox",
                RequestBody: "synthetic"u8.ToArray()), CancellationToken.None);
        }

        var runs = Enumerable.Range(0, 8).Select(_ => Task.Run(BeginAsync)).ToArray();
        gate.SetResult();
        var results = await Task.WhenAll(runs);

        Assert.Single(results, r => r.Created);
        Assert.Single(results.Select(r => r.Exchange.Id).Distinct());
        await using var verify = fixture.CreateDb();
        Assert.Equal(1, await verify.GovernmentExchanges.IgnoreQueryFilters().CountAsync(x => x.TenantId == tenantId && x.IdempotencyKey == key));
        Assert.Equal(1, await verify.AuditLogs.IgnoreQueryFilters().CountAsync(x => x.TenantId == tenantId && x.Action == "government.exchange.recorded"));
    }

    [Fact]
    public async Task TheDatabase_RefusesALiveOnlyStateOnANonLiveRow()
    {
        Guid tenantId;
        await using (var seed = fixture.CreateDb())
            tenantId = await PostgresFixture.SeedMinimalTenant(seed);
        await using var db = fixture.CreateDb();
        db.GovernmentExchanges.Add(new GovernmentExchange
        {
            TenantId = tenantId, Body = GovernmentBodies.BankAnb, Operation = "payroll.submit", Channel = GovernmentExchangeChannels.Manual,
            IdempotencyKey = Guid.NewGuid().ToString(), State = GovernmentExchangeStates.Accepted, Adapter = "manual", IsLive = false,
        });
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }
}
