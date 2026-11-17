using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Data;
using Zayra.Api.Application.Auth;
using Zayra.Api.Infrastructure.Audit;
using Zayra.Api.Infrastructure.FieldEncryption;
using Zayra.Api.Infrastructure.Government;
using Zayra.Api.Infrastructure.Government.Credentials;
using Zayra.Api.Models;
using Zayra.Api.Tests.FieldEncryption;

namespace Zayra.Api.Tests.Government;

/// <summary>
/// A3: the government credential store on the existing field encryption. Fails closed without a KEK (security R1); AAD binds
/// tenant + company + body + environment + version; metadata only; rotation and last-used recorded; two tenants never see each
/// other's credential.
/// </summary>
public sealed class GovernmentCredentialStoreTests
{
    // Synthetic only: a 35-character Arabic-and-Latin secret, the real size a pasted client secret has.
    private const string SecretA = "synthetic-secret-سري-اختبار-0001-AAAA";
    private const string SecretB = "synthetic-secret-سري-اختبار-0002-BBBB";

    private static (GovernmentCredentialStore Store, ZayraDbContext Db) Create(FeKit kit, ZayraDbContext? db = null)
    {
        db ??= kit.Db(interceptors: false);
        return (new GovernmentCredentialStore(db, kit.Keys, kit.Kek, kit.Options, new AuditService(db)), db);
    }

    private static RequestContext Ctx(Guid tenant, Guid? user = null) => new("127.0.0.1", "test", user ?? Guid.NewGuid(), tenant);

    [Fact]
    public async Task WithoutAKek_SaveAndReadAreRefused_AndNothingIsWritten()
    {
        var noKek = new ConfigKeyEncryptionKeyProvider(new FieldEncryptionOptions());
        var kit = new FeKit();
        await using var db = kit.Db(interceptors: false);
        var store = new GovernmentCredentialStore(db, new FieldKeyService(kit.Store, noKek), noKek, new FieldEncryptionOptions(), new AuditService(db));
        var tenant = Guid.NewGuid();

        store.IsAvailable.Should().BeFalse();
        var save = () => store.SaveAsync(tenant, null, GovernmentBodies.Gosi, "sandbox", GovernmentCredentialKinds.OAuthClient, SecretA, null, Ctx(tenant), default);
        (await save.Should().ThrowAsync<GovernmentCredentialsUnavailableException>()).Which.Code.Should().Be(GovernmentCredentialStore.UnavailableCode);
        var read = () => store.OpenAsync(tenant, null, GovernmentBodies.Gosi, "sandbox", default);
        await read.Should().ThrowAsync<GovernmentCredentialsUnavailableException>();
        var meta = () => store.GetMetadataAsync(tenant, null, GovernmentBodies.Gosi, "sandbox", default);
        await meta.Should().ThrowAsync<GovernmentCredentialsUnavailableException>();
        (await store.IsUsableAsync(tenant, null, GovernmentBodies.Gosi, "sandbox", default)).Should().BeFalse();
        (await db.GovernmentCredentials.CountAsync()).Should().Be(0);
        (await db.AuditLogs.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task WithAKek_ButFieldEncryptionSwitchedOff_SaveIsStillRefused()
    {
        var kit = new FeKit();
        await using var db = kit.Db(interceptors: false);
        var off = new FieldEncryptionOptions { Mode = FieldEncryptionMode.Off, Keks = kit.Options.Keks, ActiveKekVersion = kit.Options.ActiveKekVersion };
        var store = new GovernmentCredentialStore(db, kit.Keys, kit.Kek, off, new AuditService(db));
        var tenant = Guid.NewGuid();

        kit.Kek.IsConfigured.Should().BeTrue();
        store.IsAvailable.Should().BeFalse("a KEK alone is not field encryption switched on");
        var save = () => store.SaveAsync(tenant, null, GovernmentBodies.BankAnb, "sandbox", GovernmentCredentialKinds.OAuthClient, SecretA, null, Ctx(tenant), default);
        await save.Should().ThrowAsync<GovernmentCredentialsUnavailableException>();
        (await db.GovernmentCredentials.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Save_StoresCiphertextOnly_ReturnsMetadataOnly_AndAudits()
    {
        var kit = new FeKit();
        var (store, db) = Create(kit);
        var tenant = Guid.NewGuid();

        var meta = await store.SaveAsync(tenant, null, GovernmentBodies.Gosi, "sandbox", GovernmentCredentialKinds.OAuthClient, SecretA, null, Ctx(tenant), default);

        meta.Version.Should().Be(1);
        meta.Fingerprint.Should().MatchRegex("^[0-9a-f]{16}$");
        JsonSerializer.Serialize(meta).Should().NotContain(SecretA).And.NotContain("synthetic-secret");
        var row = await db.GovernmentCredentials.SingleAsync();
        System.Text.Encoding.UTF8.GetString(row.Ciphertext).Should().NotContain("synthetic-secret");
        var audit = await db.AuditLogs.SingleAsync();
        audit.Action.Should().Be("government.credential.saved");
        audit.Metadata.Should().NotContain("synthetic-secret");
        (await store.OpenAsync(tenant, null, GovernmentBodies.Gosi, "sandbox", default))!.Secret.Should().Be(SecretA);
    }

    [Fact]
    public async Task Rotation_BumpsTheVersion_RecordsWhoAndWhen_AndLastUsedIsRecordedOnOpen()
    {
        var kit = new FeKit();
        var (store, db) = Create(kit);
        var tenant = Guid.NewGuid();
        var rotator = Guid.NewGuid();
        await store.SaveAsync(tenant, null, GovernmentBodies.BankAnb, "sandbox", GovernmentCredentialKinds.OAuthClient, SecretA, null, Ctx(tenant), default);
        (await store.GetMetadataAsync(tenant, null, GovernmentBodies.BankAnb, "sandbox", default))!.LastUsedAtUtc.Should().BeNull();
        await store.OpenAsync(tenant, null, GovernmentBodies.BankAnb, "sandbox", default);
        (await store.GetMetadataAsync(tenant, null, GovernmentBodies.BankAnb, "sandbox", default))!.LastUsedAtUtc.Should().NotBeNull();

        var rotated = await store.SaveAsync(tenant, null, GovernmentBodies.BankAnb, "sandbox", GovernmentCredentialKinds.OAuthClient, SecretB, null, Ctx(tenant, rotator), default);

        rotated.Version.Should().Be(2);
        rotated.RotatedBy.Should().Be(rotator);
        rotated.RotatedAtUtc.Should().NotBeNull();
        (await store.OpenAsync(tenant, null, GovernmentBodies.BankAnb, "sandbox", default))!.Version.Should().Be(2);
        (await db.AuditLogs.Select(a => a.Action).ToListAsync()).Should().Contain("government.credential.rotated");
    }

    [Fact]
    public async Task TheAad_BindsTenantCompanyBodyEnvironmentAndVersion()
    {
        var kit = new FeKit();
        var (store, db) = Create(kit);
        var tenant = Guid.NewGuid();
        await store.SaveAsync(tenant, null, GovernmentBodies.Gosi, "sandbox", GovernmentCredentialKinds.ApiKey, SecretA, null, Ctx(tenant), default);
        var row = await db.GovernmentCredentials.SingleAsync();

        // The same ciphertext moved to production, to another body, to a company, or replayed under another version, does not open.
        foreach (var mutate in new Action<GovernmentCredential>[]
                 {
                     r => r.Environment = "production",
                     r => r.Body = GovernmentBodies.Muqeem,
                     r => r.CompanyId = Guid.NewGuid(),
                     r => r.CredentialVersion = 2,
                 })
        {
            var (env, body, company, version) = (row.Environment, row.Body, row.CompanyId, row.CredentialVersion);
            mutate(row);
            await db.SaveChangesAsync();
            (await store.OpenAsync(tenant, row.CompanyId, row.Body, row.Environment, default)).Should().BeNull();
            (row.Environment, row.Body, row.CompanyId, row.CredentialVersion) = (env, body, company, version);
            await db.SaveChangesAsync();
        }
        (await store.OpenAsync(tenant, null, GovernmentBodies.Gosi, "sandbox", default))!.Secret.Should().Be(SecretA);
    }

    [Fact]
    public async Task TwoTenants_EachOpensOnlyItsOwn_AndAMovedRowDoesNotOpenInTheOtherTenant()
    {
        var kit = new FeKit();
        var (store, db) = Create(kit);
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();
        await store.SaveAsync(tenantA, null, GovernmentBodies.Gosi, "sandbox", GovernmentCredentialKinds.OAuthClient, SecretA, null, Ctx(tenantA), default);
        await store.SaveAsync(tenantB, null, GovernmentBodies.Gosi, "sandbox", GovernmentCredentialKinds.OAuthClient, SecretB, null, Ctx(tenantB), default);

        (await store.OpenAsync(tenantA, null, GovernmentBodies.Gosi, "sandbox", default))!.Secret.Should().Be(SecretA);
        (await store.OpenAsync(tenantB, null, GovernmentBodies.Gosi, "sandbox", default))!.Secret.Should().Be(SecretB);
        (await store.OpenAsync(Guid.NewGuid(), null, GovernmentBodies.Gosi, "sandbox", default)).Should().BeNull();

        // Tenant B's ciphertext copied onto tenant A's row (a database-level swap) opens for neither.
        var rowA = await db.GovernmentCredentials.SingleAsync(x => x.TenantId == tenantA);
        var rowB = await db.GovernmentCredentials.SingleAsync(x => x.TenantId == tenantB);
        rowA.Ciphertext = rowB.Ciphertext;
        await db.SaveChangesAsync();
        (await store.OpenAsync(tenantA, null, GovernmentBodies.Gosi, "sandbox", default)).Should().BeNull();
    }

    [Fact]
    public async Task ACompanyCredential_WinsOverTheTenantWideOne_AndExpiredIsNeverOpened()
    {
        var kit = new FeKit();
        var (store, db) = Create(kit);
        var tenant = Guid.NewGuid();
        var company = Guid.NewGuid();
        await store.SaveAsync(tenant, null, GovernmentBodies.Gosi, "sandbox", GovernmentCredentialKinds.OAuthClient, SecretA, null, Ctx(tenant), default);
        await store.SaveAsync(tenant, company, GovernmentBodies.Gosi, "sandbox", GovernmentCredentialKinds.OAuthClient, SecretB, null, Ctx(tenant), default);

        (await store.OpenAsync(tenant, company, GovernmentBodies.Gosi, "sandbox", default))!.Secret.Should().Be(SecretB);
        (await store.OpenAsync(tenant, Guid.NewGuid(), GovernmentBodies.Gosi, "sandbox", default))!.Secret.Should().Be(SecretA);

        var companyRow = await db.GovernmentCredentials.SingleAsync(x => x.CompanyId == company);
        companyRow.ExpiresAtUtc = DateTime.UtcNow.AddMinutes(-1);
        await db.SaveChangesAsync();
        (await store.OpenAsync(tenant, company, GovernmentBodies.Gosi, "sandbox", default))!.Secret.Should().Be(SecretA,
            "an expired company credential falls back to the tenant-wide one");
    }

    [Fact]
    public async Task Delete_RemovesTheRow_AndAudits()
    {
        var kit = new FeKit();
        var (store, db) = Create(kit);
        var tenant = Guid.NewGuid();
        await store.SaveAsync(tenant, null, GovernmentBodies.Gosi, "production", GovernmentCredentialKinds.PrivateKey, SecretA, null, Ctx(tenant), default);

        (await store.DeleteAsync(tenant, null, GovernmentBodies.Gosi, "production", Ctx(tenant), default)).Should().BeTrue();

        (await db.GovernmentCredentials.CountAsync()).Should().Be(0);
        (await db.AuditLogs.Select(a => a.Action).ToListAsync()).Should().Contain("government.credential.removed");
        (await store.DeleteAsync(tenant, null, GovernmentBodies.Gosi, "production", Ctx(tenant), default)).Should().BeFalse();
    }

    [Fact]
    public async Task APrivateKeyLongerThanAFieldValue_IsStoredAndOpened()
    {
        var kit = new FeKit();
        var (store, _) = Create(kit);
        var tenant = Guid.NewGuid();
        var pem = "-----BEGIN PRIVATE KEY-----\n" + new string('A', 3200) + "\n-----END PRIVATE KEY-----";

        await store.SaveAsync(tenant, null, GovernmentBodies.Gosi, "sandbox", GovernmentCredentialKinds.PrivateKey, pem, null, Ctx(tenant), default);

        (await store.OpenAsync(tenant, null, GovernmentBodies.Gosi, "sandbox", default))!.Secret.Should().Be(pem);
    }

    [Fact]
    public void AnUnsealedCredential_NeverPrintsItsSecret()
    {
        new UnsealedGovernmentCredential(GovernmentBodies.Gosi, "sandbox", GovernmentCredentialKinds.ApiKey, null, 1, SecretA)
            .ToString().Should().NotContain("synthetic-secret");
    }
}
