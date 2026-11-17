using System.Reflection;
using System.Runtime.CompilerServices;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Zayra.Api.Infrastructure.Audit;
using Zayra.Api.Infrastructure.Government;
using Zayra.Api.Infrastructure.Government.Credentials;
using Zayra.Api.Infrastructure.Payroll.SaudiBankExports.Api;
using Zayra.Api.Infrastructure.Payroll.SaudiBankExports.Channels;
using Zayra.Api.Infrastructure.Qiwa;
using Zayra.Api.Models;
using Zayra.Api.Tests.FieldEncryption;
using static Zayra.Api.Tests.Government.GovernmentLiveGateTests;

namespace Zayra.Api.Tests.Government;

/// <summary>
/// Final gate security P3-3 and P3-7: no ToString prints a wage or a token, and the capabilities screen reads whether a
/// credential is on file (present, unrevoked, unexpired) without ever opening the secret. Synthetic data.
/// </summary>
public sealed class GovernmentWave1HygieneTests
{
    // ── P3-3: ToString prints counts, never amounts or a token ───────────────

    [Fact]
    public void AnAnbStatusLine_PrintsNoAmount()
    {
        var line = new AnbStatusLine("1000000001", "0108061100000001", 12345.67m, "ARNBSARI", "Synthetic Employee", "P-1", "PAID",
            null, "TRN-1", null, null);

        var text = line.ToString();

        text.Should().NotContain("12345").And.NotContain("0108061100000001").And.NotContain("Synthetic");
    }

    [Fact]
    public void ABankBatchBinding_PrintsCountsOnly()
    {
        var binding = new BankBatchBinding(new[] { new BankSubmittedLine("1000000001", "0108061100000001", "W1") }, 98765.43m,
            DateTimeOffset.UnixEpoch);

        var text = binding.ToString();

        text.Should().Contain("1").And.NotContain("98765");
    }

    [Fact]
    public void TheAnbClientsPreparedSubmit_NeverPrintsTheToken()
    {
        const string token = "synthetic-bearer-token-0001";
        var prepared = typeof(AnbPayrollApiClient).GetNestedType("Prepared", BindingFlags.NonPublic)!;
        var instance = RuntimeHelpers.GetUninitializedObject(prepared);
        prepared.GetProperty("Token")!.SetValue(instance, token);

        instance.ToString().Should().NotContain(token);
    }

    // ── P3-7: the capabilities GET never opens a secret ──────────────────────

    private sealed class LiveGosiDouble : IGosiProvider
    {
        public string Body => GovernmentBodies.Gosi;
        public string AdapterName => "synthetic-live";
        public bool IsLiveIntegration => true;
        public Task<GovernmentProviderCapabilities> GetCapabilitiesAsync(CancellationToken ct) =>
            Task.FromResult(new GovernmentProviderCapabilities(Body, "Synthetic", true, [], "synthetic"));
    }

    [Fact]
    public async Task Capabilities_ReadTheCredentialOnFile_WithoutOpeningIt()
    {
        var kit = new FeKit();
        await using var db = kit.Db(interceptors: false);
        var tenant = Guid.NewGuid();
        db.TenantFeatureFlags.Add(new TenantFeatureFlag { TenantId = tenant, FeatureKey = FeatureKeys.GovGosiApi, IsEnabled = true, ConfigJson = GovernmentApiTests.StampedConsent() });
        await db.SaveChangesAsync();
        var config = Config((GovernmentCredentialStore.AcceptLiveCredentialsKey, "true"),
            (GovernmentLivePolicy.PartnerAgreementKey(GovernmentBodies.Gosi), "AGR-SYNTHETIC"));
        var store = new GovernmentCredentialStore(db, kit.Keys, kit.Kek, kit.Options, new AuditService(db), config);
        await store.SaveAsync(tenant, null, GovernmentBodies.Gosi, "production", GovernmentCredentialKinds.PrivateKey, "synthetic-secret-0001", null,
            new Zayra.Api.Application.Auth.RequestContext("ip", "ua", Guid.NewGuid(), tenant), default,
            new GovernmentCredentialBinding(BaseUrlHost: "api.gosi.example.test"));

        // Tamper with the sealed secret's tag, keeping its length: any attempt to open it now fails. If the screen opened the
        // secret it would read "no credential"; reading only that a credential is on file, it still reads connected.
        var row = await db.GovernmentCredentials.SingleAsync();
        var sealedSecret = row.Ciphertext.ToArray();
        sealedSecret[^1] ^= 0xFF;
        row.Ciphertext = sealedSecret;
        await db.SaveChangesAsync();
        (await store.IsUsableAsync(tenant, null, GovernmentBodies.Gosi, "production", default)).Should().BeFalse("the secret no longer opens");

        var service = new GovernmentCapabilitiesService(db, config, new SandboxQiwaApiAdapter(NullLogger<SandboxQiwaApiAdapter>.Instance),
            [new LiveGosiDouble(), new DisabledMuqeemProvider(), new DisabledTakamolProvider()], store);
        var gosi = (await service.GetAsync(tenant, default)).Single(c => c.Body == GovernmentBodies.Gosi);

        gosi.Mode.Should().Be(GovernmentCapabilityModes.Live, "the screen reads that a credential is on file, it never opens it");
        (await db.GovernmentCredentials.AsNoTracking().SingleAsync()).LastUsedAtUtc.Should().BeNull();
    }

    [Fact]
    public async Task Capabilities_StillReadNoCredential_ForARevokedOrExpiredOne()
    {
        var kit = new FeKit();
        await using var db = kit.Db(interceptors: false);
        var tenant = Guid.NewGuid();
        var config = Config((GovernmentCredentialStore.AcceptLiveCredentialsKey, "true"));
        var store = new GovernmentCredentialStore(db, kit.Keys, kit.Kek, kit.Options, new AuditService(db), config);
        var ctx = new Zayra.Api.Application.Auth.RequestContext("ip", "ua", Guid.NewGuid(), tenant);
        await store.SaveAsync(tenant, null, GovernmentBodies.Gosi, "sandbox", GovernmentCredentialKinds.PrivateKey, "synthetic-secret-0002",
            DateTime.UtcNow.AddDays(1), ctx, default, new GovernmentCredentialBinding(BaseUrlHost: "api.gosi.example.test"));
        // The store refuses a past expiry on save, so the sandbox credential is aged past it directly.
        var sandbox = await db.GovernmentCredentials.SingleAsync(x => x.Environment == "sandbox");
        sandbox.ExpiresAtUtc = DateTime.UtcNow.AddMinutes(-1);
        await db.SaveChangesAsync();
        await store.SaveAsync(tenant, null, GovernmentBodies.Gosi, "production", GovernmentCredentialKinds.PrivateKey, "synthetic-secret-0003",
            null, ctx, default, new GovernmentCredentialBinding(BaseUrlHost: "api.gosi.example.test"));
        await store.RevokeAsync(tenant, null, GovernmentBodies.Gosi, "production", ctx, default);

        (await store.IsOnFileAsync(tenant, null, GovernmentBodies.Gosi, "sandbox", default)).Should().BeFalse("expired");
        (await store.IsOnFileAsync(tenant, null, GovernmentBodies.Gosi, "production", default)).Should().BeFalse("revoked");
    }
}
