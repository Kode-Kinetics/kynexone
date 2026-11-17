using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Auth;
using Zayra.Api.Infrastructure.Audit;
using Zayra.Api.Infrastructure.Government;
using Zayra.Api.Models;

namespace Zayra.Api.Tests.Government;

/// <summary>
/// Architecture 3.5 on the ledger: one open submission per batch whatever the key, the bank's id belongs to one attempt and is
/// written once, transitions follow a monotonic table, Unknown has exactly two exits and only a second person takes them, the
/// sealed audit row carries the money fields, and the caller's own unit of work is never touched.
/// </summary>
public sealed class GovernmentLedgerStateMachineTests
{
    private sealed class LiveBank : IGovernmentAdapter
    {
        public string Body => GovernmentBodies.BankAnb;
        public string AdapterName => "live-bank-double";
        public bool IsLiveIntegration => true;
    }

    private static readonly Guid Starter = Guid.NewGuid();
    private static RequestContext Ctx(Guid tenant, Guid? user = null) => new("127.0.0.1", "test", user ?? Starter, tenant);
    private static string Hash(char c) => new(c, 64);

    private static GovernmentExchangeAttempt Intent(Guid tenant, string key, string batch = "B-001", char artifact = 'a') =>
        new(tenant, tenant, GovernmentBodies.BankAnb, "payroll.submit", GovernmentExchangeChannels.Api, key, GovernmentExchangeStates.SubmitIntended,
            Ctx(tenant), new LiveBank(), Environment: "sandbox", SubjectType: "PaymentBatch", SubjectId: batch, ArtifactSha256: Hash(artifact),
            ArtifactSecondarySha256: Hash('b'), AccountFingerprint: "acct-fp-synthetic", BatchReference: batch, TotalAmount: 2_997_000.50m, ItemCount: 3000);

    private static GovernmentExchangeOutcome To(Guid tenant, string state, string? bankId = null) => new(state, Ctx(tenant), ExternalReference: bankId);

    [Fact]
    public async Task OneBatch_HasOneOpenSubmission_WhateverKeyTheClientSends()
    {
        var (recorder, db) = GovernmentExchangeRecorderTests.Create();
        await using var _ = db;
        var tenant = Guid.NewGuid();
        await recorder.BeginAsync(Intent(tenant, "key-1"), default);

        var second = () => recorder.BeginAsync(Intent(tenant, "key-2"), default);

        (await second.Should().ThrowAsync<GovernmentExchangeConflictException>()).Which.Code.Should().Be(GovernmentExchangeRecorder.BatchHeldCode);
        (await db.GovernmentExchanges.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task TheSameKey_WithDifferentContent_IsAConflict_NotTheOldRow()
    {
        var (recorder, db) = GovernmentExchangeRecorderTests.Create();
        await using var _ = db;
        var tenant = Guid.NewGuid();
        await recorder.BeginAsync(Intent(tenant, "key-1"), default);

        var swapped = () => recorder.BeginAsync(Intent(tenant, "key-1", artifact: 'c'), default);

        (await swapped.Should().ThrowAsync<GovernmentExchangeConflictException>()).Which.Code.Should().Be(GovernmentExchangeRecorder.KeyReusedCode);
    }

    [Fact]
    public async Task Transitions_FollowTheTable_AndEverythingElseIsRefused()
    {
        var (recorder, db) = GovernmentExchangeRecorderTests.Create();
        await using var _ = db;
        var tenant = Guid.NewGuid();
        var id = (await recorder.BeginAsync(Intent(tenant, "key-1"), default)).Exchange.Id;

        var skip = () => recorder.UpdateAsync(tenant, id, To(tenant, GovernmentExchangeStates.Completed), default);
        (await skip.Should().ThrowAsync<GovernmentExchangeConflictException>()).Which.Code.Should().Be(GovernmentExchangeRecorder.IllegalTransitionCode);

        foreach (var state in new[] { GovernmentExchangeStates.Accepted, GovernmentExchangeStates.Validated, GovernmentExchangeStates.InProgress, GovernmentExchangeStates.Completed })
            await recorder.UpdateAsync(tenant, id, To(tenant, state, state == GovernmentExchangeStates.Accepted ? "ANB-1" : null), default);

        foreach (var back in new[] { GovernmentExchangeStates.InProgress, GovernmentExchangeStates.Accepted, GovernmentExchangeStates.Attempted, GovernmentExchangeStates.Failed })
        {
            var regress = () => recorder.UpdateAsync(tenant, id, To(tenant, back), default);
            await regress.Should().ThrowAsync<GovernmentExchangeConflictException>($"{GovernmentExchangeStates.Completed} never moves to {back}");
        }
        var row = await db.GovernmentExchanges.AsNoTracking().SingleAsync();
        (row.State, row.StateVersion).Should().Be((GovernmentExchangeStates.Completed, 5));

        // A file recorded by hand is terminal too.
        var recorded = (await recorder.BeginAsync(new GovernmentExchangeAttempt(tenant, tenant, GovernmentBodies.Gosi, "contributions.file",
            GovernmentExchangeChannels.FileOut, "file-1", GovernmentExchangeStates.Recorded, Ctx(tenant)), default)).Exchange.Id;
        var reopen = () => recorder.UpdateAsync(tenant, recorded, To(tenant, GovernmentExchangeStates.Attempted), default);
        await reopen.Should().ThrowAsync<GovernmentExchangeConflictException>();
    }

    [Fact]
    public async Task TheBanksId_IsWrittenOnce_AndBelongsToOneAttempt()
    {
        var (recorder, db) = GovernmentExchangeRecorderTests.Create();
        await using var _ = db;
        var tenant = Guid.NewGuid();
        var first = (await recorder.BeginAsync(Intent(tenant, "key-1", batch: "B-001"), default)).Exchange.Id;
        var second = (await recorder.BeginAsync(Intent(tenant, "key-2", batch: "B-002"), default)).Exchange.Id;
        await recorder.UpdateAsync(tenant, first, To(tenant, GovernmentExchangeStates.Accepted, "ANB-1"), default);

        var rebind = () => recorder.UpdateAsync(tenant, first, To(tenant, GovernmentExchangeStates.Validated, "ANB-9"), default);
        (await rebind.Should().ThrowAsync<GovernmentExchangeConflictException>()).Which.Code.Should().Be(GovernmentExchangeRecorder.BankReferenceCode);
        var steal = () => recorder.UpdateAsync(tenant, second, To(tenant, GovernmentExchangeStates.Accepted, "ANB-1"), default);
        (await steal.Should().ThrowAsync<GovernmentExchangeConflictException>()).Which.Code.Should().Be(GovernmentExchangeRecorder.BankReferenceCode);
    }

    [Fact]
    public async Task Unknown_HasTwoExits_TakenOnlyByASecondPerson_WithAReason()
    {
        var (recorder, db) = GovernmentExchangeRecorderTests.Create();
        await using var _ = db;
        var tenant = Guid.NewGuid();
        var id = (await recorder.BeginAsync(Intent(tenant, "key-1"), default)).Exchange.Id;
        await recorder.UpdateAsync(tenant, id, To(tenant, GovernmentExchangeStates.Unknown), default);

        // No automatic way out, and never by the person who started it.
        var poll = () => recorder.UpdateAsync(tenant, id, To(tenant, GovernmentExchangeStates.Accepted, "ANB-1"), default);
        await poll.Should().ThrowAsync<GovernmentExchangeConflictException>();
        var self = () => recorder.ResolveUnknownAsync(tenant, id, GovernmentExchangeResolutions.BankDoesNot, null, "checked with the bank", Ctx(tenant, Starter), default);
        (await self.Should().ThrowAsync<GovernmentExchangeConflictException>()).Which.Code.Should().Be(GovernmentExchangeRecorder.ResolutionRefusedCode);
        var checker = Guid.NewGuid();
        var noId = () => recorder.ResolveUnknownAsync(tenant, id, GovernmentExchangeResolutions.BankHasIt, null, "the bank has it", Ctx(tenant, checker), default);
        await noId.Should().ThrowAsync<GovernmentExchangeConflictException>();

        // Exit (b): the bank does not have it; the intent is released and the batch may be submitted again.
        var released = await recorder.ResolveUnknownAsync(tenant, id, GovernmentExchangeResolutions.BankDoesNot, null, "bank confirmed no batch", Ctx(tenant, checker), default);
        (released.State, released.ResolvedByUserId, released.Resolution).Should().Be((GovernmentExchangeStates.ReleasedAfterUnknown, checker, GovernmentExchangeResolutions.BankDoesNot));
        var again = await recorder.BeginAsync(Intent(tenant, "key-2"), default);
        again.Created.Should().BeTrue("a released intent no longer holds the batch");

        // Exit (a): the bank has it, with its id.
        await recorder.UpdateAsync(tenant, again.Exchange.Id, To(tenant, GovernmentExchangeStates.Unknown), default);
        var landed = await recorder.ResolveUnknownAsync(tenant, again.Exchange.Id, GovernmentExchangeResolutions.BankHasIt, "ANB-7", "found by batch number", Ctx(tenant, checker), default);
        (landed.State, landed.ExternalReference).Should().Be((GovernmentExchangeStates.Accepted, "ANB-7"));
    }

    [Fact]
    public async Task TheSealedAuditRow_CarriesTheMoneyFields_AndTheStateBefore()
    {
        var (recorder, db) = GovernmentExchangeRecorderTests.Create();
        await using var _ = db;
        var tenant = Guid.NewGuid();
        var id = (await recorder.BeginAsync(Intent(tenant, "key-1"), default)).Exchange.Id;
        await recorder.UpdateAsync(tenant, id, To(tenant, GovernmentExchangeStates.Accepted, "ANB-1"), default);

        var audit = await db.AuditLogs.AsNoTracking().Where(a => a.Action == "government.exchange.updated").SingleAsync();
        audit.Metadata.Should().Contain(Hash('a')).And.Contain(Hash('b')).And.Contain("ANB-1").And.Contain("B-001")
            .And.Contain("2997000.50").And.Contain("\"before\":{\"state\":\"SubmitIntended\"").And.Contain("key-1");
        AuditService.VerifyChain(await db.AuditLogs.AsNoTracking().Where(a => a.TenantId == tenant).OrderBy(a => a.CreatedAtUtc).ThenBy(a => a.Id).ToListAsync())
            .IsValid.Should().BeTrue();
    }

    [Fact]
    public async Task TheCallersUnitOfWork_IsNeitherSavedNorDiscarded()
    {
        var options = new DbContextOptionsBuilder<Zayra.Api.Data.ZayraDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        await using var caller = new Zayra.Api.Data.ZayraDbContext(options);
        var tenant = Guid.NewGuid();
        Zayra.Api.Application.Auth.IAuditService callerAudit = new AuditService(caller);
        callerAudit.Stage("payroll.batch_prepared", "PaymentBatch", "42", Ctx(tenant), "{}", null);
        var pending = caller.ChangeTracker.Entries().Count(e => e.State == EntityState.Added);
        var recorder = new GovernmentExchangeRecorder(() => new Zayra.Api.Data.ZayraDbContext(options));

        await recorder.BeginAsync(Intent(tenant, "key-1"), default);
        var duplicate = () => recorder.BeginAsync(Intent(tenant, "key-2"), default); // refused: the batch is held
        await duplicate.Should().ThrowAsync<GovernmentExchangeConflictException>();

        caller.ChangeTracker.Entries().Count(e => e.State == EntityState.Added).Should().Be(pending, "the ledger never saves or discards the caller's work");
        await caller.SaveChangesAsync();
        (await caller.AuditLogs.AsNoTracking().CountAsync(a => a.Action == "payroll.batch_prepared")).Should().Be(1);
    }
}
