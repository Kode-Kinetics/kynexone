using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Zayra.Api.Application.Auth;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Audit;
using Zayra.Api.Infrastructure.FieldEncryption;
using Zayra.Api.Infrastructure.Government;
using Zayra.Api.Infrastructure.Government.Credentials;
using Zayra.Api.Infrastructure.Qiwa;
using Zayra.Api.Models;
using Zayra.Api.Tests.FieldEncryption;
using static Zayra.Api.Tests.Government.GovernmentLiveGateTests;

namespace Zayra.Api.Tests.Government;

/// <summary>
/// Regression tests for the re-check of the foundation (security review, "Re-check of 913aa78dc"): the legal clearance gates
/// production only, ledger keys are per environment, bank and production credentials must name their hosts, a transient Qiwa gate
/// leaves the queue alone, and mismatched Qiwa environments are refused. Written against behaviour, so they run on the parent.
/// </summary>
public sealed class GovernmentRecheckRegressionTests
{
    private static readonly GovernmentLiveGates AllOpen = new(true, true, true, true);

    // ── N2: the legal clearance gates production only ───────────────────────────────────────────────────

    [Theory]
    [InlineData(GovernmentBodies.BankAnb)]
    [InlineData(GovernmentBodies.Qiwa)]
    [InlineData(GovernmentBodies.Gosi)]
    public void ASandboxWrite_WithAllFourGates_NeedsNoLegalClearance(string body)
    {
        var sandbox = GovernmentLivePolicy.DecideLiveWrite(Config(), TestScopes.For("sandbox"), body, AllOpen);

        sandbox.AllowsWrites.Should().BeTrue("the sandbox rung (synthetic data) is usable with the four gates");
        GovernmentLivePolicy.DecideLiveWrite(Config(), TestScopes.For("production"), body, AllOpen).ReasonCode
            .Should().Be(GovernmentLivePolicy.LiveWritesNeedLegalClearance, "production writes still need the recorded legal opinion");
    }

    // ── N4: ledger keys are per environment ─────────────────────────────────────────────────────────────

    private sealed class LiveBank : IGovernmentAdapter
    {
        public string Body => GovernmentBodies.BankAnb;
        public string AdapterName => "live-bank-double";
        public bool IsLiveIntegration => true;
    }

    private static GovernmentExchangeAttempt Intent(Guid tenant, string key, string environment) =>
        new(tenant, tenant, GovernmentBodies.BankAnb, "payroll.submit", GovernmentExchangeChannels.Api, key, GovernmentExchangeStates.SubmitIntended,
            new RequestContext("127.0.0.1", "test", Guid.NewGuid(), tenant), new LiveBank(), Environment: environment, SubjectType: "PaymentBatch",
            SubjectId: TestBatch.Id("B-001"), ArtifactSha256: new string('a', 64), AccountFingerprint: "acct-fp-synthetic", BatchReference: "B-001");

    [Fact]
    public async Task TheSameBatchNumber_InSandboxAndProduction_DoesNotCollide()
    {
        var (recorder, db) = GovernmentExchangeRecorderTests.Create();
        await using var _ = db;
        var tenant = Guid.NewGuid();

        await recorder.BeginAsync(Intent(tenant, "sandbox-key", "sandbox"), default);
        var production = await recorder.BeginAsync(Intent(tenant, "production-key", "production"), default);

        production.Created.Should().BeTrue("a sandbox batch never holds the production batch number");
    }

    [Fact]
    public async Task TheSameBankId_InSandboxAndProduction_DoesNotCollide()
    {
        var (recorder, db) = GovernmentExchangeRecorderTests.Create();
        await using var _ = db;
        var tenant = Guid.NewGuid();
        var sandbox = (await recorder.BeginAsync(Intent(tenant, "sandbox-key", "sandbox"), default)).Exchange.Id;
        var production = (await recorder.BeginAsync(Intent(tenant, "production-key", "production"), default)).Exchange.Id;
        var ctx = new RequestContext("127.0.0.1", "test", null, tenant);
        await recorder.UpdateAsync(tenant, sandbox, new GovernmentExchangeOutcome(GovernmentExchangeStates.Accepted, ctx, ExternalReference: "1001"), default);

        var landed = await recorder.UpdateAsync(tenant, production, new GovernmentExchangeOutcome(GovernmentExchangeStates.Accepted, ctx, ExternalReference: "1001"), default);

        landed.ExternalReference.Should().Be("1001");
    }

    // ── F3: bank and production credentials name their hosts ───────────────────────────────────────────

    private static readonly Microsoft.Extensions.Configuration.IConfiguration AcceptLive =
        Config((GovernmentCredentialStore.AcceptLiveCredentialsKey, "true"));

    [Theory]
    [InlineData(GovernmentBodies.BankAnb, "sandbox")]
    [InlineData(GovernmentBodies.BankAnb, "production")]
    [InlineData(GovernmentBodies.Gosi, "production")]
    public async Task ACredentialWithoutItsHost_IsRefused_ForABankOrForProduction(string body, string environment)
    {
        var kit = new FeKit();
        await using var db = kit.Db(interceptors: false);
        var store = new GovernmentCredentialStore(db, kit.Keys, kit.Kek, kit.Options, new AuditService(db), AcceptLive);
        var tenant = Guid.NewGuid();

        var save = () => store.SaveAsync(tenant, null, body, environment, GovernmentCredentialKinds.ApiKey, "synthetic-secret", null,
            new RequestContext("127.0.0.1", "test", Guid.NewGuid(), tenant), default);

        await save.Should().ThrowAsync<Exception>();
        (await db.GovernmentCredentials.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task ASandboxCredentialOfAGovernmentBody_MayOmitItsHost()
    {
        var kit = new FeKit();
        await using var db = kit.Db(interceptors: false);
        var store = new GovernmentCredentialStore(db, kit.Keys, kit.Kek, kit.Options, new AuditService(db), AcceptLive);
        var tenant = Guid.NewGuid();

        await store.SaveAsync(tenant, null, GovernmentBodies.Gosi, "sandbox", GovernmentCredentialKinds.ApiKey, "synthetic-secret", null,
            new RequestContext("127.0.0.1", "test", Guid.NewGuid(), tenant), default);

        (await db.GovernmentCredentials.CountAsync()).Should().Be(1);
    }

    // ── Item 6: changing only the tenant in the AAD fails decryption (same DEK) ─────────────────────────

    [Fact]
    public async Task UnderOneDek_ChangingOnlyTheTenantInTheAad_FailsDecryption()
    {
        var kit = new FeKit();
        var tenantA = Guid.NewGuid();
        var ring = await kit.Keys.GetRingAsync(tenantA, createIfMissing: true, default);
        var sealedForA = FieldEnvelope.Seal(ring.ActiveDek, checked((byte)ring.ActiveVersion), "synthetic-secret",
            GovernmentCredentialStore.Aad(tenantA, null, GovernmentBodies.Gosi, "sandbox", 1));

        FieldEnvelope.Open(ring.ActiveDek, sealedForA, GovernmentCredentialStore.Aad(tenantA, null, GovernmentBodies.Gosi, "sandbox", 1))
            .Should().Be("synthetic-secret");
        var otherTenant = () => FieldEnvelope.Open(ring.ActiveDek, sealedForA, GovernmentCredentialStore.Aad(Guid.NewGuid(), null, GovernmentBodies.Gosi, "sandbox", 1));
        otherTenant.Should().Throw<System.Security.Cryptography.CryptographicException>();
    }

    // ── Qiwa: a transient closed gate leaves the queue; mismatched environments are refused ─────────────

    private sealed class LiveQiwaDouble : IQiwaApiAdapter
    {
        public int Calls;
        public string AdapterName => "live-double";
        public bool IsLiveIntegration => true;
        public Task<string?> AcquireAccessTokenAsync(string clientId, string clientSecret, string environment, CancellationToken ct)
        { Interlocked.Increment(ref Calls); return Task.FromResult<string?>("synthetic-token"); }
        public Task<QiwaApiResult> PushEmployeeAsync(string accessToken, QiwaEmployeePayload payload, Guid idempotencyKey, CancellationToken ct)
        { Interlocked.Increment(ref Calls); return Task.FromResult(new QiwaApiResult(true, null, null, "{\"status\":\"ok\"}")); }
        public Task<QiwaApiResult> GetEmployeeStatusAsync(string accessToken, string establishmentId, string employeeIdNumber, CancellationToken ct)
            => Task.FromResult(new QiwaApiResult(false, null, null, null));
    }

    private static async Task<QiwaSyncLog> RunWorkerAsync(Microsoft.Extensions.Configuration.IConfiguration config, bool consent, string? credentialEnvironment = null)
    {
        var protection = DataProtectionProvider.Create("ZayraGovRecheckTests");
        var db = new ZayraDbContext(new DbContextOptionsBuilder<ZayraDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var (tenantId, logId) = await GovernmentReviewRegressionTests.SeedQiwaAsync(db, protection);
        if (consent)
            db.TenantFeatureFlags.Add(new TenantFeatureFlag { TenantId = tenantId, FeatureKey = FeatureKeys.GovQiwaApi, IsEnabled = true, ConfigJson = GovernmentApiTests.StampedConsent("identity") });
        if (credentialEnvironment is not null)
            (await db.QiwaApiCredentials.IgnoreQueryFilters().SingleAsync()).Environment = credentialEnvironment;
        await db.SaveChangesAsync();
        var adapter = new LiveQiwaDouble();

        await new QiwaSyncWorker(new GovernmentReviewRegressionTests.SingleDbScopeFactory(db), adapter, new QiwaOAuthTokenCache(), protection,
            NullLogger<QiwaSyncWorker>.Instance, configuration: config).ProcessOnceAsync(CancellationToken.None);

        adapter.Calls.Should().Be(0);
        return await db.QiwaSyncLogs.IgnoreQueryFilters().AsNoTracking().SingleAsync(l => l.Id == logId);
    }

    private static readonly (string, string?)[] LiveQiwa =
    [
        (QiwaLiveAdapterPolicy.LiveSwitchEnvVar, "true"), (QiwaLiveAdapterPolicy.PartnerAgreementKey, "AGR-SYNTHETIC"),
    ];

    [Fact]
    public async Task TheKillSwitch_LeavesQueuedPushesPending()
    {
        var log = await RunWorkerAsync(Config([.. LiveQiwa, (GovernmentLivePolicy.EnabledKey(GovernmentBodies.Qiwa), "false")]), consent: true);
        log.Status.Should().Be(QiwaSyncLogStatuses.Pending, "switching the kill switch back on must resume the queue, not leave dead letters");
    }

    [Fact]
    public async Task ATenantNotYetOptedIn_LeavesQueuedPushesPending()
    {
        var log = await RunWorkerAsync(Config(LiveQiwa), consent: false);
        log.Status.Should().Be(QiwaSyncLogStatuses.Pending);
    }

    [Fact]
    public async Task ConnectionAndCredentialEnvironments_ThatDiffer_AreRefused()
    {
        var log = await RunWorkerAsync(Config(LiveQiwa), consent: true, credentialEnvironment: "production");
        log.Status.Should().Be(QiwaSyncLogStatuses.DeadLetter, "a sandbox connection with a production credential (or the reverse) is a configuration fault");
        log.DeadLetterReason.Should().Contain("environment");
    }
}
