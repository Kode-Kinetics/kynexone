using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Recruitment;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Email;
using Zayra.Api.Infrastructure.Recruitment;
using Zayra.Api.Models;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using System.Data.Common;
using Zayra.Api.Infrastructure.Documents;

namespace Zayra.Api.Tests;

[Collection("Integration")]
[Trait("Category", "Integration")]
public sealed class OfferDispatchPostgresTests(PostgresFixture fixture)
{
    private readonly OfferMemoryStorage storage = new();
    private readonly ExplicitOfferEmail email = new();
    private OfferDispatchService Service(ZayraDbContext db, IDocumentStorage? documentStorage = null)
    {
        var artifacts = new OfferArtifactService(db, documentStorage ?? storage, OfferLifecycleServiceTests.Config());
        return new(db, new(db, OfferLifecycleServiceTests.Config(), artifacts), artifacts, email);
    }
    private async Task<OfferLifecycleSeed> Frozen()
    {
        var seed = await OfferArtifactPostgresTests.ManagedDraftAsync(fixture);
        await using var db = fixture.CreateDb();
        db.SystemSettings.Add(new() { TenantId = seed.Actor.TenantId, Category = OfferRules.PolicyCategory, SettingKey = OfferRules.PolicyKey, SettingValue = "false", DataType = "bool" });
        await db.SaveChangesAsync();
        await new OfferArtifactService(db, storage, OfferLifecycleServiceTests.Config()).FreezeAsync(seed.Actor, seed.OfferId, 2);
        return seed;
    }
    private static OfferEmailRequest Request(string key = "reviewed-1") => new(4, key, "Employment offer", "Please review your attached offer.", "reviewed-sender-v1");

    [Theory]
    [InlineData("io")]
    [InlineData("http")]
    [InlineData("cancel")]
    public async Task ObservedPreflightStorageFailureIsFailedAndReleasesWithoutTransport(string failure)
    {
        var seed = await Frozen(); await using var db = fixture.CreateDb();
        var queued = await Service(db).QueueAsync(seed.Actor, seed.OfferId, Request());
        using var cancellation = new CancellationTokenSource();
        var service = Service(db, new FailingDispatchReadStorage(failure, cancellation));
        await service.ProcessAsync(seed.Actor, seed.OfferId, queued.Id, cancellation.Token);
        await AssertObservedFailure(seed, queued.Id, markerCommitted: false);
        await service.ProcessAsync(seed.Actor, seed.OfferId, queued.Id);
        email.Calls.Should().Be(0);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ObservedFinalTenantCheckFailureIsFailedAndReleasesWithoutTransport(bool cancel)
    {
        var seed = await Frozen(); using var cancellation = new CancellationTokenSource();
        await using var db = new ZayraDbContext(new DbContextOptionsBuilder<ZayraDbContext>()
            .UseNpgsql(fixture.ConnectionString, o => o.EnableRetryOnFailure())
            .AddInterceptors(Zayra.Api.Infrastructure.Jobs.RowLockingInterceptor.Instance,
                new FailFinalTenantCheck(cancel, cancellation)).Options);
        var service = Service(db); var queued = await service.QueueAsync(seed.Actor, seed.OfferId, Request());
        await service.ProcessAsync(seed.Actor, seed.OfferId, queued.Id, cancellation.Token);
        await AssertObservedFailure(seed, queued.Id, markerCommitted: true);
        await service.ProcessAsync(seed.Actor, seed.OfferId, queued.Id);
        email.Calls.Should().Be(0);
    }

    private async Task AssertObservedFailure(OfferLifecycleSeed seed, Guid dispatchId, bool markerCommitted)
    {
        await using var db = fixture.CreateDb();
        var dispatch = await db.OfferDispatches.AsNoTracking().SingleAsync(d => d.Id == dispatchId);
        dispatch.Outcome.Should().Be("Failed"); dispatch.TransportCompletedAtUtc.Should().NotBeNull();
        dispatch.FinalizedAtUtc.Should().BeNull();
        (dispatch.TransportStartedAtUtc is not null).Should().Be(markerCommitted);
        (await db.JobApplications.AsNoTracking().SingleAsync(a => a.Id == seed.ApplicationId)).OfferReservationId.Should().BeNull();
        var offer = await db.OfferLetters.AsNoTracking().SingleAsync(o => o.Id == seed.OfferId);
        offer.Status.Should().Be("Draft"); offer.CommunicationOutcome.Should().Be("Failed");
        var result = await db.OfferDispatchAttempts.AsNoTracking().SingleAsync(a => a.DispatchId == dispatchId && a.Stage == "Result");
        result.Outcome.Should().Be("Failed"); result.LeaseToken.Should().Be(dispatch.LeaseToken!.Value);
        result.TenantId.Should().Be(seed.Actor.TenantId); result.CompanyId.Should().Be(seed.Actor.CompanyId);
        result.OfferId.Should().Be(seed.OfferId); result.ApplicationId.Should().Be(seed.ApplicationId);
        result.Code.Should().NotContain("private-provider-detail");
        email.Calls.Should().Be(0);
    }

    [Fact]
    public async Task QueueReplayIsAtomicAndPayloadConflictDoesNotSend()
    {
        var seed = await Frozen(); await using var db = fixture.CreateDb(); var service = Service(db);
        var queued = await service.QueueAsync(seed.Actor, seed.OfferId, Request());
        (await service.QueueAsync(seed.Actor, seed.OfferId, Request())).Id.Should().Be(queued.Id);
        (await db.OfferDispatches.CountAsync(d=>d.OfferId==seed.OfferId)).Should().Be(1);
        (await db.JobApplications.AsNoTracking().SingleAsync(a=>a.Id==seed.ApplicationId)).OfferReservationId.Should().Be(queued.Id);
        var conflict = await FluentActions.Awaiting(()=>service.QueueAsync(seed.Actor,seed.OfferId,Request() with { Message="Changed" })).Should().ThrowAsync<OfferLifecycleException>();
        conflict.Which.Code.Should().Be("offer_idempotency_conflict"); email.Calls.Should().Be(0);
    }

    [Fact]
    public async Task ConcurrentClicksAndTwoClaimsHaveOneDispatchAndOneLease()
    {
        var seed = await Frozen();
        async Task<OfferDispatch> Queue() { await using var db=fixture.CreateDb(); return await Service(db).QueueAsync(seed.Actor,seed.OfferId,Request()); }
        var queued=await Task.WhenAll(Queue(),Queue()); queued[0].Id.Should().Be(queued[1].Id);
        async Task<OfferDispatch?> Claim() { await using var db=fixture.CreateDb(); return await Service(db).ClaimAsync(seed.Actor,seed.OfferId,queued[0].Id); }
        var claims=await Task.WhenAll(Claim(),Claim()); claims.Count(x=>x is not null).Should().Be(1);
        await using var verify=fixture.CreateDb(); (await verify.OfferDispatchAttempts.CountAsync(x=>x.DispatchId==queued[0].Id)).Should().Be(1);
    }

    [Theory]
    [InlineData("Captured")]
    [InlineData("NotConfigured")]
    [InlineData("Failed")]
    [InlineData("Unknown")]
    [InlineData("AcceptedByRelay")]
    public async Task OutcomesAreDurableAndOnlyAcceptedIssues(string outcome)
    {
        var seed=await Frozen(); email.Outcome=outcome; await using var db=fixture.CreateDb(); var service=Service(db);
        var queued=await service.QueueAsync(seed.Actor,seed.OfferId,Request());
        await service.ProcessAsync(seed.Actor,seed.OfferId,queued.Id);
        await service.ProcessAsync(seed.Actor,seed.OfferId,queued.Id);
        var dispatch=await db.OfferDispatches.AsNoTracking().SingleAsync(x=>x.Id==queued.Id);
        dispatch.Outcome.Should().Be(outcome); email.Calls.Should().Be(1);
        (await db.OfferLetters.AsNoTracking().SingleAsync(x=>x.Id==seed.OfferId)).Status.Should().Be(outcome=="AcceptedByRelay"?"Sent":"Draft");
        var app=await db.JobApplications.AsNoTracking().SingleAsync(x=>x.Id==seed.ApplicationId);
        if(outcome=="Unknown") app.OfferReservationId.Should().Be(queued.Id); else app.OfferReservationId.Should().BeNull();
        (await db.OfferDispatchAttempts.CountAsync(x=>x.DispatchId==queued.Id&&x.Stage=="Result")).Should().Be(1);
    }

    [Fact]
    public async Task FrozenRecipientAndPdfAreUsedAfterCandidateChanges()
    {
        var seed=await Frozen(); await using var db=fixture.CreateDb(); var service=Service(db);
        var queued=await service.QueueAsync(seed.Actor,seed.OfferId,Request());
        var candidate=await db.Candidates.SingleAsync(c=>c.TenantId==seed.Actor.TenantId); candidate.Email="changed@example.invalid"; await db.SaveChangesAsync();
        await service.ProcessAsync(seed.Actor,seed.OfferId,queued.Id);
        email.Recipient.Should().Be("candidate@example.invalid"); email.Pdf.Should().Equal(storage.Bytes.Values.Single()); email.Tenant.Should().Be(seed.Actor.TenantId);
    }

    [Fact]
    public async Task QueuedCancellationReleasesButClaimedCancellationCannot()
    {
        var seed=await Frozen(); await using var db=fixture.CreateDb(); var service=Service(db);
        var queued=await service.QueueAsync(seed.Actor,seed.OfferId,Request());
        (await service.CancelAsync(seed.Actor,seed.OfferId,queued.Id,new(queued.Version,"Candidate asked to wait"))).Outcome.Should().Be("Cancelled");
        await service.ProcessAsync(seed.Actor,seed.OfferId,queued.Id); email.Calls.Should().Be(0);
        var offer=await db.OfferLetters.AsNoTracking().SingleAsync(o=>o.Id==seed.OfferId);
        var second=await service.QueueAsync(seed.Actor,seed.OfferId,Request("second") with { ExpectedVersion=offer.Version });
        var claimed=await service.ClaimAsync(seed.Actor,seed.OfferId,second.Id); claimed.Should().NotBeNull();
        await FluentActions.Awaiting(()=>service.CancelAsync(seed.Actor,seed.OfferId,second.Id,new(claimed!.Version,"Cancel"))).Should().ThrowAsync<OfferLifecycleException>();
        (await db.JobApplications.AsNoTracking().SingleAsync(a=>a.Id==seed.ApplicationId)).OfferReservationId.Should().Be(second.Id);
    }

    [Fact]
    public async Task StaleSendingBecomesUnknownWithoutAnotherTransport()
    {
        var seed=await Frozen(); await using var db=fixture.CreateDb(); var service=Service(db);
        var queued=await service.QueueAsync(seed.Actor,seed.OfferId,Request());
        var claim=await service.ClaimAsync(seed.Actor,seed.OfferId,queued.Id); claim.Should().NotBeNull();
        await db.OfferDispatches.Where(d=>d.Id==queued.Id).ExecuteUpdateAsync(s=>s.SetProperty(d=>d.LeaseUntilUtc,DateTime.UtcNow.AddMinutes(-1)));
        await service.ProcessAsync(seed.Actor,seed.OfferId,queued.Id);
        (await db.OfferDispatches.AsNoTracking().SingleAsync(d=>d.Id==queued.Id)).Outcome.Should().Be("Unknown"); email.Calls.Should().Be(0);
        (await db.JobApplications.AsNoTracking().SingleAsync(a=>a.Id==seed.ApplicationId)).OfferReservationState.Should().Be("Unknown");
    }

    [Theory]
    [InlineData("inactive")]
    [InlineData("approval")]
    public async Task ChangedEligibilityFailsBeforeTransport(string change)
    {
        var seed=await Frozen(); await using var db=fixture.CreateDb(); var service=Service(db);
        var queued=await service.QueueAsync(seed.Actor,seed.OfferId,Request());
        if(change=="inactive") { var t=await db.Tenants.SingleAsync(t=>t.Id==seed.Actor.TenantId);t.IsActive=false; }
        else { var policy=await db.SystemSettings.SingleAsync(s=>s.TenantId==seed.Actor.TenantId&&s.SettingKey==OfferRules.PolicyKey);policy.SettingValue="true"; }
        await db.SaveChangesAsync(); await service.ProcessAsync(seed.Actor,seed.OfferId,queued.Id);
        email.Calls.Should().Be(0); (await db.OfferDispatches.AsNoTracking().SingleAsync(d=>d.Id==queued.Id)).Outcome.Should().Be("Failed");
    }

    [Fact]
    public async Task WrongCompanyCannotQueueClaimOrCancel()
    {
        var seed=await Frozen(); await using var db=fixture.CreateDb(); var service=Service(db); var wrong=seed.Actor with {CompanyId=Guid.NewGuid()};
        await FluentActions.Awaiting(()=>service.QueueAsync(wrong,seed.OfferId,Request())).Should().ThrowAsync<OfferLifecycleException>();
        var queued=await service.QueueAsync(seed.Actor,seed.OfferId,Request());
        await FluentActions.Awaiting(()=>service.ClaimAsync(wrong,seed.OfferId,queued.Id)).Should().ThrowAsync<OfferLifecycleException>();
        await FluentActions.Awaiting(()=>service.CancelAsync(wrong,seed.OfferId,queued.Id,new(1,"Cancel"))).Should().ThrowAsync<OfferLifecycleException>(); email.Calls.Should().Be(0);
    }

    [Theory]
    [InlineData(OfferReconciliationAction.ApplyRetainedRelayEvidence)]
    [InlineData(OfferReconciliationAction.RecordConfirmedExternalSending)]
    [InlineData(OfferReconciliationAction.AuthorizeRiskAcknowledgedResend)]
    public async Task UnknownRequiresTypedValidatedEvidenceAndRiskReplacementIsAtomic(OfferReconciliationAction action)
    {
        var seed=await Frozen(); email.Outcome="Unknown"; await using var db=fixture.CreateDb(); var service=Service(db);
        var queued=await service.QueueAsync(seed.Actor,seed.OfferId,Request()); await service.ProcessAsync(seed.Actor,seed.OfferId,queued.Id);
        var current=await db.OfferDispatches.AsNoTracking().SingleAsync(d=>d.Id==queued.Id);
        var request=new OfferDispatchReconcileRequest(current.Version,current.ReservationVersion,action,"Operator reviewed ambiguity");
        await FluentActions.Awaiting(()=>service.ReconcileAsync(seed.Actor,seed.OfferId,current.Id,request)).Should().ThrowAsync<OfferLifecycleException>();
        if(action==OfferReconciliationAction.ApplyRetainedRelayEvidence) return;
        var offer=await db.OfferLetters.AsNoTracking().SingleAsync(o=>o.Id==seed.OfferId);
        if(action==OfferReconciliationAction.AuthorizeRiskAcknowledgedResend)
        {
            // Failure after release was staged must roll the entire transaction back; acceptance
            // cannot observe a gap or a lost reservation while the replacement is validated.
            await FluentActions.Awaiting(()=>service.ReconcileAsync(seed.Actor,seed.OfferId,current.Id,request with {
                AcknowledgeDuplicateRisk=true,Replacement=Request("invalid-replacement") with {ExpectedVersion=offer.Version,
                    ConfirmResend=true,ResendReason="Acknowledged",ReviewedSenderFingerprint="changed"} })).Should().ThrowAsync<OfferLifecycleException>();
            (await db.JobApplications.AsNoTracking().SingleAsync(a=>a.Id==seed.ApplicationId)).OfferReservationId.Should().Be(current.Id);
            (await db.OfferDispatches.CountAsync(d=>d.OfferId==seed.OfferId)).Should().Be(1);
        }
        var resolved=await service.ReconcileAsync(seed.Actor,seed.OfferId,current.Id,request with {
            EvidenceReference="outbox/confirmed-1",OccurredAtUtc=DateTime.UtcNow,AcknowledgeDuplicateRisk=true,
            Replacement=Request("risk-replacement") with {ExpectedVersion=offer.Version,ConfirmResend=true,ResendReason="Risk acknowledged"} });
        var app=await db.JobApplications.AsNoTracking().SingleAsync(a=>a.Id==seed.ApplicationId);
        if(action==OfferReconciliationAction.RecordConfirmedExternalSending)
        { resolved.Outcome.Should().Be("External"); app.OfferReservationId.Should().BeNull(); (await db.OfferLetters.AsNoTracking().SingleAsync(o=>o.Id==seed.OfferId)).CommunicationOutcome.Should().Be("External"); }
        else
        { resolved.Id.Should().NotBe(current.Id); resolved.Outcome.Should().Be("Queued"); app.OfferReservationId.Should().Be(resolved.Id);
          (await db.OfferDispatches.AsNoTracking().SingleAsync(d=>d.Id==current.Id)).Outcome.Should().Be("Unknown"); }
        email.Calls.Should().Be(1); (await db.OfferDispatchAttempts.CountAsync(a=>a.DispatchId==current.Id&&a.Outcome=="Unknown")).Should().Be(1);
    }

    [Fact]
    public async Task AcceptedResultSurvivesFinalizationFailureAndReconcilesWithoutSendingAgain()
    {
        var seed=await Frozen();
        await using var db=new ZayraDbContext(new DbContextOptionsBuilder<ZayraDbContext>().UseNpgsql(fixture.ConnectionString,o=>o.EnableRetryOnFailure())
            .AddInterceptors(Zayra.Api.Infrastructure.Jobs.RowLockingInterceptor.Instance,new RejectIssueOnce()).Options);
        var service=Service(db); var queued=await service.QueueAsync(seed.Actor,seed.OfferId,Request());
        await FluentActions.Awaiting(()=>service.ProcessAsync(seed.Actor,seed.OfferId,queued.Id)).Should().ThrowAsync<IOException>();
        var retained=await db.OfferDispatches.AsNoTracking().SingleAsync(d=>d.Id==queued.Id);
        retained.Outcome.Should().Be("AcceptedByRelay"); retained.FinalizedAtUtc.Should().BeNull();
        (await db.OfferLetters.AsNoTracking().SingleAsync(o=>o.Id==seed.OfferId)).Status.Should().Be("Draft");
        await service.ProcessAsync(seed.Actor,seed.OfferId,queued.Id); email.Calls.Should().Be(1);
        await service.ReconcileAsync(seed.Actor,seed.OfferId,queued.Id,new(retained.Version,retained.ReservationVersion,OfferReconciliationAction.ApplyRetainedRelayEvidence,"Apply persisted relay evidence"));
        (await db.OfferLetters.AsNoTracking().SingleAsync(o=>o.Id==seed.OfferId)).Status.Should().Be("Sent");
        (await db.JobApplications.AsNoTracking().SingleAsync(a=>a.Id==seed.ApplicationId)).OfferReservationId.Should().BeNull(); email.Calls.Should().Be(1);
    }

    [Fact]
    public async Task ErasureCannotRemoveEvidenceDuringPausedTransportAndInactiveTenantNeverSends()
    {
        var seed=await Frozen(); var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        email.DuringTransport=async()=>{entered.TrySetResult(); await release.Task;};
        await using var workerDb=fixture.CreateDb(); var service=Service(workerDb); var queued=await service.QueueAsync(seed.Actor,seed.OfferId,Request());
        var running=service.ProcessAsync(seed.Actor,seed.OfferId,queued.Id);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(20)); await using var purgeDb=fixture.CreateDb();
            var tenant=await purgeDb.Tenants.SingleAsync(t=>t.Id==seed.Actor.TenantId);tenant.IsActive=false;tenant.Slug+="__deleted_dispatch"; await purgeDb.SaveChangesAsync();
            var refusal=await FluentActions.Awaiting(()=>OfferArtifactErasure.EraseAsync(purgeDb,storage,seed.Actor.TenantId,default)).Should().ThrowAsync<IOException>();
            refusal.Which.Message.Should().Contain("dispatch"); storage.Bytes.Should().ContainSingle();
            (await purgeDb.OfferDispatchAttempts.CountAsync(a=>a.DispatchId==queued.Id)).Should().Be(2);
        }
        finally { release.TrySetResult(); await running; }
    }

    [Fact]
    public async Task PersistedRequestAndAttemptEvidenceCannotBeEdited()
    {
        var seed=await Frozen(); await using var db=fixture.CreateDb();var service=Service(db);
        var queued=await service.QueueAsync(seed.Actor,seed.OfferId,Request()); await service.ProcessAsync(seed.Actor,seed.OfferId,queued.Id);
        var row=await db.OfferDispatches.SingleAsync(d=>d.Id==queued.Id);row.RecipientEmail="changed@example.invalid";
        await FluentActions.Awaiting(()=>db.SaveChangesAsync()).Should().ThrowAsync<InvalidOperationException>();db.ChangeTracker.Clear();
        var attempt=await db.OfferDispatchAttempts.FirstAsync(a=>a.DispatchId==queued.Id);attempt.Outcome="Invented";
        await FluentActions.Awaiting(()=>db.SaveChangesAsync()).Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task WorkerRequiresBothExplicitGatesBeforeResolvingAnyWork()
    {
        using var services=new ServiceCollection().BuildServiceProvider();var config=new ConfigurationBuilder().AddInMemoryCollection().Build();
        var worker=new OfferDispatchWorker(services.GetRequiredService<IServiceScopeFactory>(),config,NullLogger<OfferDispatchWorker>.Instance);
        (await worker.DrainOnceAsync()).Should().Be(0);
        config[OfferLifecycleService.EnablementKey]="true";(await worker.DrainOnceAsync()).Should().Be(0);
        config[OfferLifecycleService.EnablementKey]="false";config[OfferDispatchWorker.EnablementKey]="true";(await worker.DrainOnceAsync()).Should().Be(0);
    }

    [Fact]
    public async Task CancelAndClaimRaceHasExactlyOneWinner()
    {
        var seed=await Frozen();await using var setup=fixture.CreateDb();var queued=await Service(setup).QueueAsync(seed.Actor,seed.OfferId,Request());
        async Task<string> Cancel(){await using var db=fixture.CreateDb();try{await Service(db).CancelAsync(seed.Actor,seed.OfferId,queued.Id,new(1,"Pause"));return "Cancelled";}catch(OfferLifecycleException){return "Conflict";}}
        async Task<string> Claim(){await using var db=fixture.CreateDb();return await Service(db).ClaimAsync(seed.Actor,seed.OfferId,queued.Id) is null?"NoClaim":"Sending";}
        var results=await Task.WhenAll(Cancel(),Claim());
        (results.Contains("Cancelled") ^ results.Contains("Sending")).Should().BeTrue();
        var row=await setup.OfferDispatches.AsNoTracking().SingleAsync(d=>d.Id==queued.Id);
        var app=await setup.JobApplications.AsNoTracking().SingleAsync(a=>a.Id==seed.ApplicationId);
        if(row.Outcome=="Cancelled") app.OfferReservationId.Should().BeNull();else app.OfferReservationId.Should().Be(queued.Id);
        email.Calls.Should().Be(0);
    }

    [Fact]
    public async Task PurgerWinningApplicationFenceLeavesWaitingWorkerUnableToSend()
    {
        var seed=await Frozen();await using var setup=fixture.CreateDb();var queued=await Service(setup).QueueAsync(seed.Actor,seed.OfferId,Request());
        var tenant=await setup.Tenants.SingleAsync(t=>t.Id==seed.Actor.TenantId);tenant.IsActive=false;tenant.Slug+="__deleted_queued";await setup.SaveChangesAsync();
        var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        storage.BeforeDelete=async()=>{entered.TrySetResult();await release.Task;};
        await using var purgeDb=fixture.CreateDb();var purge=OfferArtifactErasure.EraseAsync(purgeDb,storage,seed.Actor.TenantId,default);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(20));await using var workerDb=fixture.CreateDb();var work=Service(workerDb).ProcessAsync(seed.Actor,seed.OfferId,queued.Id);
        release.TrySetResult();await purge;
        await FluentActions.Awaiting(()=>work).Should().ThrowAsync<OfferLifecycleException>();email.Calls.Should().Be(0);storage.Bytes.Should().BeEmpty();
    }

    [Fact]
    public async Task ExpiredOfferFailsBeforeSubmissionAndSenderChangeCannotQueue()
    {
        var seed=await Frozen();await using var db=fixture.CreateDb();var service=Service(db);
        var changed=await FluentActions.Awaiting(()=>service.QueueAsync(seed.Actor,seed.OfferId,Request() with {ReviewedSenderFingerprint="stale"})).Should().ThrowAsync<OfferLifecycleException>();changed.Which.Code.Should().Be("offer_sender_changed");
        var queued=await service.QueueAsync(seed.Actor,seed.OfferId,Request());
        // Fixture clock advance: EnsureCreated omits the production immutability trigger. This
        // direct test-only change models time passing; no production path edits frozen deadlines.
        await db.OfferLetters.Where(o=>o.Id==seed.OfferId).ExecuteUpdateAsync(s=>s.SetProperty(o=>o.ResponseDeadline,DateTime.UtcNow.AddMinutes(-1)));
        await service.ProcessAsync(seed.Actor,seed.OfferId,queued.Id);email.Calls.Should().Be(0);
        (await db.OfferDispatches.AsNoTracking().SingleAsync(d=>d.Id==queued.Id)).Outcome.Should().Be("Failed");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LateAcceptedResultIsRetainedAfterRiskReplacementWithoutFinalizingReplacement(bool claimReplacement)
    {
        var seed=await Frozen();var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        email.DuringTransport=async()=>{entered.TrySetResult();await release.Task;};
        await using var workerDb=fixture.CreateDb();var worker=Service(workerDb);var old=await worker.QueueAsync(seed.Actor,seed.OfferId,Request());
        var running=worker.ProcessAsync(seed.Actor,seed.OfferId,old.Id);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(20));
        try
        {
            await using var reconcileDb=fixture.CreateDb();var service=Service(reconcileDb);
            await reconcileDb.OfferDispatches.Where(d=>d.Id==old.Id).ExecuteUpdateAsync(s=>s.SetProperty(d=>d.LeaseUntilUtc,DateTime.UtcNow.AddMinutes(-1)));
            await service.ProcessAsync(seed.Actor,seed.OfferId,old.Id);
            var stale=await reconcileDb.OfferDispatches.AsNoTracking().SingleAsync(d=>d.Id==old.Id);var offer=await reconcileDb.OfferLetters.AsNoTracking().SingleAsync(o=>o.Id==seed.OfferId);
            var next=await service.ReconcileAsync(seed.Actor,seed.OfferId,old.Id,new(stale.Version,stale.ReservationVersion,OfferReconciliationAction.AuthorizeRiskAcknowledgedResend,"Operator permits duplicate risk",
                AcknowledgeDuplicateRisk:true,Replacement:Request("replacement-late") with{ExpectedVersion=offer.Version,ResendReason="Confirmed duplicate risk",ConfirmResend=true}));
            if(claimReplacement) await service.ClaimAsync(seed.Actor,seed.OfferId,next.Id);
            var beforeAppVersion=await reconcileDb.JobApplications.AsNoTracking().Where(a=>a.Id==seed.ApplicationId).Select(a=>a.Version).SingleAsync();
            var beforeOfferVersion=await reconcileDb.OfferLetters.AsNoTracking().Where(o=>o.Id==seed.OfferId).Select(o=>o.Version).SingleAsync();
            release.TrySetResult();await running;
            var app=await reconcileDb.JobApplications.AsNoTracking().SingleAsync(a=>a.Id==seed.ApplicationId);
            app.OfferReservationId.Should().Be(next.Id);app.OfferReservationState.Should().Be(claimReplacement?"Sending":"Queued");
            app.Version.Should().Be(beforeAppVersion);
            var unchanged=await reconcileDb.OfferLetters.AsNoTracking().SingleAsync(o=>o.Id==seed.OfferId);
            unchanged.Status.Should().Be("Draft");unchanged.Version.Should().Be(beforeOfferVersion);unchanged.CommunicationOutcome.Should().Be(claimReplacement?"Sending":"Queued");
            var late=await reconcileDb.OfferDispatchAttempts.AsNoTracking().SingleAsync(a=>a.DispatchId==old.Id&&a.Stage=="LateResult");
            late.Outcome.Should().Be("AcceptedByRelay");late.OfferId.Should().Be(seed.OfferId);late.CompanyId.Should().Be(seed.Actor.CompanyId);late.TenantId.Should().Be(seed.Actor.TenantId);
            (await reconcileDb.OfferDispatches.AsNoTracking().SingleAsync(d=>d.Id==old.Id)).Outcome.Should().Be("AcceptedByRelay");
            await service.ProcessAsync(seed.Actor,seed.OfferId,old.Id);email.Calls.Should().Be(1);
            (await service.GetAsync(seed.Actor,seed.OfferId,old.Id)).AllowedActions.Should().BeEmpty();
        }
        finally{release.TrySetResult();try{await running;}catch(OfferLifecycleException){}}
    }

    [Fact]
    public async Task WrongLeaseCannotWriteTransportEvidenceOrFinalize()
    {
        var seed=await Frozen();await using var db=fixture.CreateDb();var service=Service(db);var queued=await service.QueueAsync(seed.Actor,seed.OfferId,Request());
        email.DuringTransport=async()=>
        {
            await using var competing=fixture.CreateDb();
            await competing.OfferDispatches.Where(d=>d.Id==queued.Id).ExecuteUpdateAsync(s=>s.SetProperty(d=>d.LeaseToken,Guid.NewGuid()));
        };
        var failure=await FluentActions.Awaiting(()=>service.ProcessAsync(seed.Actor,seed.OfferId,queued.Id)).Should().ThrowAsync<OfferLifecycleException>();
        failure.Which.Code.Should().Be("offer_lease_changed");
        (await db.OfferDispatchAttempts.CountAsync(a=>a.DispatchId==queued.Id&&(a.Stage=="Result"||a.Stage=="LateResult"))).Should().Be(0);
        (await db.OfferLetters.AsNoTracking().SingleAsync(o=>o.Id==seed.OfferId)).Status.Should().Be("Draft");email.Calls.Should().Be(1);
    }

    [Fact]
    public async Task ExpiredUnknownDispatchDoesNotAdvertiseForbiddenReissueActions()
    {
        var seed=await Frozen();email.Outcome="Unknown";await using var db=fixture.CreateDb();var service=Service(db);
        var queued=await service.QueueAsync(seed.Actor,seed.OfferId,Request());await service.ProcessAsync(seed.Actor,seed.OfferId,queued.Id);
        (await service.GetAsync(seed.Actor,seed.OfferId,queued.Id)).AllowedActions.Should().Contain("AuthorizeRiskAcknowledgedResend");
        await db.OfferLetters.Where(o=>o.Id==seed.OfferId).ExecuteUpdateAsync(s=>s.SetProperty(o=>o.ResponseDeadline,DateTime.UtcNow.AddMinutes(-1)));
        (await service.GetAsync(seed.Actor,seed.OfferId,queued.Id)).AllowedActions.Should().BeEmpty();
    }
}

file sealed class FailingDispatchReadStorage(string failure, CancellationTokenSource cancellation) : IDocumentStorage
{
    public Task<StoredDocument> SaveAsync(Guid tenantId, Microsoft.AspNetCore.Http.IFormFile file, CancellationToken ct) => throw new NotSupportedException();
    public string ResolvePath(string storageUrl) => throw new NotSupportedException();
    public Task<byte[]> GetBytesAsync(Guid tenantId, string key, CancellationToken ct = default)
    {
        if (failure == "cancel") { cancellation.Cancel(); ct.ThrowIfCancellationRequested(); }
        throw failure == "http" ? new HttpRequestException("private-provider-detail") : new IOException("private-provider-detail");
    }
}

file sealed class FailFinalTenantCheck(bool cancel, CancellationTokenSource cancellation) : DbCommandInterceptor
{
    private bool failed;
    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData e,
        InterceptionResult<DbDataReader> result, CancellationToken ct = default)
    {
        if (!failed && command.CommandText.Contains("FROM tenants")
            && e.Context!.ChangeTracker.Entries<OfferDispatch>().Any(d => d.Entity.TransportStartedAtUtc is not null))
        {
            failed = true;
            if (cancel) { cancellation.Cancel(); ct.ThrowIfCancellationRequested(); }
            throw new Npgsql.PostgresException("private-provider-detail", "ERROR", "ERROR", "42501");
        }
        return ValueTask.FromResult(result);
    }
}

file sealed class RejectIssueOnce : SaveChangesInterceptor
{
    private bool rejected;
    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData e,InterceptionResult<int> result,CancellationToken ct=default)
    {
        if(!rejected&&e.Context!.ChangeTracker.Entries<OfferLetter>().Any(x=>x.State==EntityState.Modified&&x.Entity.Status=="Sent"))
        {rejected=true;throw new IOException("synthetic finalization persistence failure");}
        return ValueTask.FromResult(result);
    }
}

internal sealed class ExplicitOfferEmail : IEmailService
{
    internal string Outcome="AcceptedByRelay"; internal int Calls; internal string? Recipient; internal Guid Tenant; internal byte[]? Pdf;
    internal Func<Task>? DuringTransport;
    public Task<bool> IsConfiguredAsync(CancellationToken ct=default)=>Task.FromResult(true);
    public Task SendAsync(string address,string name,string subject,string body,IReadOnlyList<EmailAttachment>? attachments=null,CancellationToken cancellationToken=default)=>throw new InvalidOperationException("Unbound transport forbidden");
    public Task<EffectiveEmailSender> GetEffectiveSenderAsync(Guid tenantId,CancellationToken ct=default)=>Task.FromResult(new EffectiveEmailSender(true,"Relay","Tenant","hr@example.invalid","People","reviewed-sender-v1",false));
    public async Task<EmailDeliveryResult> DeliverAsync(Guid tenantId,string reviewedSenderFingerprint,string toAddress,string toName,string subject,string htmlBody,IReadOnlyList<EmailAttachment>? attachments=null,CancellationToken cancellationToken=default)
    {
        if(reviewedSenderFingerprint!="reviewed-sender-v1") throw new EmailTransportException("offer_sender_changed",false);
        Interlocked.Increment(ref Calls); Recipient=toAddress;Tenant=tenantId;Pdf=attachments?.Single().Data;
        if(DuringTransport is not null) await DuringTransport();
        return Outcome switch { "AcceptedByRelay"=>EmailDeliveryResult.Accepted("synthetic"), "Captured"=>EmailDeliveryResult.CapturedNotSent("synthetic"), "NotConfigured"=>EmailDeliveryResult.NoRelay,
            "Failed"=>throw new EmailTransportException("smtp_connect_failed",false), _=>throw new IOException("synthetic unknown submission") };
    }
}
