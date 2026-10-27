using System.Security.Cryptography;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Zayra.Api.Infrastructure.FieldEncryption;
using Zayra.Api.Models;

namespace Zayra.Api.Tests.FieldEncryption;

public class FieldKeyCustodyTests
{
    private const string Pass = "correct horse battery staple 42";
    private static readonly EncryptedField IqamaField = EncryptedFieldCatalog.Find(typeof(Employee), nameof(Employee.IqamaNumber))!;

    [Fact]
    public async Task FirstUse_CreatesOneActiveKey_AndNothingInTheStoreIsPlaintext()
    {
        var kit = new FeKit(); var t = Guid.NewGuid();
        var ring = await kit.Keys.GetRingAsync(t, true, default);
        ring.ActiveVersion.Should().Be(1);
        var rows = kit.Store.Snapshot();
        rows.Should().ContainSingle().Which.Status.Should().Be("Active");
        rows[0].WrappedDek!.Should().NotEqual(ring.ActiveDek);
        rows[0].WrappedDek!.AsSpan().IndexOf(ring.ActiveDek).Should().Be(-1, "the DEK is only stored wrapped by the KEK");
    }

    [Fact]
    public async Task Tenants_GetIndependentKeys()
    {
        var kit = new FeKit();
        var a = await kit.Keys.GetRingAsync(Guid.NewGuid(), true, default);
        var b = await kit.Keys.GetRingAsync(Guid.NewGuid(), true, default);
        a.ActiveDek.Should().NotEqual(b.ActiveDek);
        a.BlindIndexRoot.Should().NotEqual(b.BlindIndexRoot);
    }

    [Fact]
    public async Task Ring_IsCached_SoSavesDoNotHitTheStoreEveryTime()
    {
        var kit = new FeKit(); var t = Guid.NewGuid();
        await kit.Keys.GetRingAsync(t, true, default);
        var before = kit.Store.Loads;
        for (var i = 0; i < 5; i++) await kit.Keys.GetRingAsync(t, true, default);
        kit.Store.Loads.Should().Be(before);
    }

    [Fact]
    public async Task Rotation_AddsAVersion_KeepsOldCiphertextReadable_AndKeepsTheBlindIndexStable()
    {
        var kit = new FeKit(); var t = Guid.NewGuid();
        var r1 = await kit.Keys.GetRingAsync(t, true, default);
        var oldEnv = FieldCipher.Encrypt(r1, "employees", IqamaField, "", "2123456789")!;
        var oldIdx = FieldCipher.Index(r1, "employees", IqamaField, "2123456789");

        (await kit.Keys.RotateDekAsync(t, default)).Should().Be(2);
        var r2 = await kit.Keys.GetRingAsync(t, true, default);
        r2.ActiveVersion.Should().Be(2);
        r2.ActiveDek.Should().NotEqual(r1.ActiveDek);
        FieldCipher.Decrypt(r2, "employees", IqamaField, "", oldEnv).Should().Be("2123456789");
        FieldCipher.Index(r2, "employees", IqamaField, "2123456789").Should().Equal(oldIdx, "rotating the DEK must not invalidate stored blind indexes");
        FieldEnvelope.DekVersionOf(FieldCipher.Encrypt(r2, "employees", IqamaField, "", "x")!).Should().Be(2);
        kit.Store.Snapshot().Select(r => r.Status).Should().BeEquivalentTo(["Retired", "Active"]);
    }

    [Fact]
    public async Task KekRotation_RewrapsEveryKey_AndCiphertextSurvivesDroppingTheOldKek()
    {
        var kit1 = new FeKit(kekVersions: [1]); var t = Guid.NewGuid();
        var r = await kit1.Keys.GetRingAsync(t, true, default);
        var env = FieldCipher.Encrypt(r, "employees", IqamaField, "", "2123456789")!;

        // Same store, KEK set {1 (old), 2 (new, active)}.
        var opts = new FieldEncryptionOptions { Mode = FieldEncryptionMode.DualWrite, ActiveKekVersion = 2,
            Keks = new Dictionary<int, byte[]> { [1] = kit1.Options.Keks[1], [2] = RandomNumberGenerator.GetBytes(32) } };
        var svc2 = new FieldKeyService(kit1.Store, new ConfigKeyEncryptionKeyProvider(opts));
        (await svc2.RewrapUnderActiveKekAsync(t, default)).Should().Be(1);
        kit1.Store.Snapshot().Single().KekVersion.Should().Be(2);

        var onlyNew = new FieldEncryptionOptions { Mode = FieldEncryptionMode.DualWrite, ActiveKekVersion = 2, Keks = new Dictionary<int, byte[]> { [2] = opts.Keks[2] } };
        var svc3 = new FieldKeyService(kit1.Store, new ConfigKeyEncryptionKeyProvider(onlyNew));
        FieldCipher.Decrypt(await svc3.GetRingAsync(t, false, default), "employees", IqamaField, "", env).Should().Be("2123456789");
    }

    [Fact]
    public async Task WrappedKeys_AreBoundToTheirTenant_ARowMovedToAnotherTenantDoesNotUnwrap()
    {
        var kit = new FeKit(); var a = Guid.NewGuid(); var b = Guid.NewGuid();
        await kit.Keys.GetRingAsync(a, true, default);
        var stolen = kit.Store.Snapshot().Single();
        var other = new FeKit();
        // Same KEK, different tenant id on the row.
        var store = new MemoryKeyStore();
        stolen.TenantId = b; stolen.Id = Guid.NewGuid();
        await store.TransactAsync(b, _ => [stolen], default);
        var svc = new FieldKeyService(store, kit.Kek);
        Func<Task> act = async () => await svc.GetRingAsync(b, false, default);
        await act.Should().ThrowAsync<CryptographicException>();
        _ = other;
    }

    [Fact]
    public async Task RecoveryCopy_Passphrase_RoundTrips_AndStampsTheRows()
    {
        var kit = new FeKit(); var t = Guid.NewGuid();
        var ring = await kit.Keys.GetRingAsync(t, true, default);
        var (blob, fp) = await kit.Keys.ExportRecoveryAsync(t, e => RecoveryCopy.SealWithPassphrase(t, e, Pass), default);
        fp.Should().HaveLength(64);
        blob.Should().NotContain(Convert.ToBase64String(ring.ActiveDek), "the DEK never appears in the clear");
        kit.Store.Snapshot().Should().OnlyContain(r => r.RecoveryExportedAtUtc != null && r.RecoveryFingerprint == fp);

        var opened = RecoveryCopy.OpenWithPassphrase(t, blob, Pass);
        opened.Should().ContainSingle().Which.Dek.Should().Equal(ring.ActiveDek);
        opened[0].BlindIndexRoot.Should().Equal(ring.BlindIndexRoot);
    }

    [Fact]
    public async Task RecoveryCopy_RefusesWeakPassphrase_WrongPassphrase_AndAnotherTenant()
    {
        var kit = new FeKit(); var t = Guid.NewGuid();
        await kit.Keys.GetRingAsync(t, true, default);
        Func<Task> weak = async () => await kit.Keys.ExportRecoveryAsync(t, e => RecoveryCopy.SealWithPassphrase(t, e, "short"), default);
        await weak.Should().ThrowAsync<ArgumentException>();
        var (blob, _) = await kit.Keys.ExportRecoveryAsync(t, e => RecoveryCopy.SealWithPassphrase(t, e, Pass), default);
        ((Action)(() => RecoveryCopy.OpenWithPassphrase(t, blob, Pass + "x"))).Should().Throw<CryptographicException>();
        ((Action)(() => RecoveryCopy.OpenWithPassphrase(Guid.NewGuid(), blob, Pass))).Should().Throw<CryptographicException>();
        ((Action)(() => RecoveryCopy.OpenWithPrivateKey(t, blob, "x"))).Should().Throw<CryptographicException>("sealed with a passphrase, not a key");
    }

    [Fact]
    public async Task RecoveryCopy_PublicKey_RoundTrips_AndRefusesSmallKeys()
    {
        var kit = new FeKit(); var t = Guid.NewGuid();
        var ring = await kit.Keys.GetRingAsync(t, true, default);
        using var rsa = RSA.Create(3072);
        var (blob, _) = await kit.Keys.ExportRecoveryAsync(t, e => RecoveryCopy.SealToPublicKey(t, e, rsa.ExportSubjectPublicKeyInfoPem()), default);
        RecoveryCopy.OpenWithPrivateKey(t, blob, rsa.ExportPkcs8PrivateKeyPem()).Single().Dek.Should().Equal(ring.ActiveDek);
        using var weak = RSA.Create(2048);
        Func<Task> act = async () => await kit.Keys.ExportRecoveryAsync(t, e => RecoveryCopy.SealToPublicKey(t, e, weak.ExportSubjectPublicKeyInfoPem()), default);
        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task Revoke_IsRefusedUntilARecoveryCopyExists()
    {
        var kit = new FeKit(); var t = Guid.NewGuid();
        await kit.Keys.GetRingAsync(t, true, default);
        Func<Task> act = async () => await kit.RevokeNowAsync(t);
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*recovery copy first*");
        kit.Store.Snapshot().Single().Status.Should().Be("Active");
    }

    [Fact]
    public async Task Revoke_DestroysPlatformMaterial_FailsClosed_AndRestoreBringsOldDataBack()
    {
        var kit = new FeKit(); var t = Guid.NewGuid(); var admin = Guid.NewGuid();
        var ring = await kit.Keys.GetRingAsync(t, true, default);
        var env = FieldCipher.Encrypt(ring, "employees", IqamaField, "", "2123456789")!;
        var idx = FieldCipher.Index(ring, "employees", IqamaField, "2123456789");
        var (blob, fp) = await kit.Keys.ExportRecoveryAsync(t, e => RecoveryCopy.SealWithPassphrase(t, e, Pass), default);

        await kit.RevokeNowAsync(t, admin);
        var row = kit.Store.Snapshot().Single();
        row.Status.Should().Be("Revoked");
        row.WrappedDek.Should().BeNull();
        row.WrappedBlindIndexRoot.Should().BeNull();
        row.RevokedByUserId.Should().Be(admin);
        row.CanaryEnc.Should().NotBeNull("the canary is ciphertext, not key material, and survives the revoke");
        Func<Task> read = async () => await kit.Keys.GetRingAsync(t, true, default);
        await read.Should().ThrowAsync<FieldEncryptionRevokedException>("a revoked tenant must neither decrypt nor silently create a new key");

        await kit.Keys.RestoreAsync(t, RecoveryCopy.OpenWithPassphrase(t, blob, Pass), fp, default);
        var back = await kit.Keys.GetRingAsync(t, true, default);
        FieldCipher.Decrypt(back, "employees", IqamaField, "", env).Should().Be("2123456789");
        FieldCipher.Index(back, "employees", IqamaField, "2123456789").Should().Equal(idx);
        kit.Store.Snapshot().Single().Status.Should().Be("Active");
    }

    [Fact]
    public async Task Revoke_IsTwoPhase_NothingIsDestroyedUntilTheCoolingOffElapses_AndItCanBeCancelled()
    {
        var kit = new FeKit(); var t = Guid.NewGuid(); var now = DateTime.UtcNow;
        await kit.Keys.GetRingAsync(t, true, default);
        await kit.Keys.ExportRecoveryAsync(t, e => RecoveryCopy.SealWithPassphrase(t, e, Pass), default);
        await kit.Keys.RequestRevokeAsync(t, Guid.NewGuid(), now, now.AddHours(24), default);

        (await kit.Keys.ApplyDueRevokeAsync(t, now.AddHours(23), default)).Should().BeFalse("still cooling off");
        kit.Store.Snapshot().Single().Status.Should().Be("Active");
        (await kit.Keys.GetRingAsync(t, false, default)).ActiveVersion.Should().Be(1, "keys keep working while the request is pending");
        Func<Task> again = async () => await kit.Keys.RequestRevokeAsync(t, null, now, now.AddHours(24), default);
        await again.Should().ThrowAsync<InvalidOperationException>().WithMessage("*already pending*");

        await kit.Keys.CancelRevokeAsync(t, default);
        kit.Store.Snapshot().Single().RevokeEffectiveAtUtc.Should().BeNull();
        (await kit.Keys.ApplyDueRevokeAsync(t, now.AddDays(2), default)).Should().BeFalse("a cancelled request never applies");
        Func<Task> none = async () => await kit.Keys.CancelRevokeAsync(t, default);
        await none.Should().ThrowAsync<InvalidOperationException>();

        await kit.Keys.RequestRevokeAsync(t, null, now, now.AddHours(1), default);
        (await kit.Keys.TenantsWithDueRevokeAsync(now.AddHours(2), default)).Should().Equal(t);
        (await kit.Keys.ApplyDueRevokeAsync(t, now.AddHours(2), default)).Should().BeTrue();
        kit.Store.Snapshot().Single().Status.Should().Be("Revoked");
    }

    [Fact]
    public async Task Rotation_IsRefusedWhileARevokeIsPending_AndTheRevokeIsRefusedAfterARotationUntilReExport()
    {
        var kit = new FeKit(); var t = Guid.NewGuid(); var now = DateTime.UtcNow;
        await kit.Keys.GetRingAsync(t, true, default);
        await kit.Keys.ExportRecoveryAsync(t, e => RecoveryCopy.SealWithPassphrase(t, e, Pass), default);
        await kit.Keys.RequestRevokeAsync(t, null, now, now.AddHours(24), default);
        Func<Task> rotate = async () => await kit.Keys.RotateDekAsync(t, default);
        await rotate.Should().ThrowAsync<InvalidOperationException>().WithMessage("*revoke request is pending*");
        await kit.Keys.CancelRevokeAsync(t, default);

        await kit.Keys.RotateDekAsync(t, default);
        Func<Task> stale = async () => await kit.Keys.RequestRevokeAsync(t, null, now, now, default);
        await stale.Should().ThrowAsync<InvalidOperationException>().WithMessage("*recovery copy first*", "the old copy lacks version 2");
    }

    [Fact]
    public async Task Restore_WithMultipleVersions_ReEstablishesTheOriginalActiveVersion()
    {
        var kit = new FeKit(); var t = Guid.NewGuid();
        await kit.Keys.GetRingAsync(t, true, default);
        await kit.Keys.RotateDekAsync(t, default);
        var (blob, fp) = await kit.Keys.ExportRecoveryAsync(t, e => RecoveryCopy.SealWithPassphrase(t, e, Pass), default);
        await kit.RevokeNowAsync(t);
        await kit.Keys.RestoreAsync(t, RecoveryCopy.OpenWithPassphrase(t, blob, Pass), fp, default);
        (await kit.Keys.GetRingAsync(t, true, default)).ActiveVersion.Should().Be(2);
        kit.Store.Snapshot().Select(r => r.Status).Should().BeEquivalentTo(["Retired", "Active"]);
    }

    [Fact]
    public async Task Restore_IsRefusedForALiveTenant_AStaleCopy_AndAKeyThatFailsItsCanary()
    {
        var kit = new FeKit(); var t = Guid.NewGuid();
        await kit.Keys.GetRingAsync(t, true, default);
        var (oldBlob, oldFp) = await kit.Keys.ExportRecoveryAsync(t, e => RecoveryCopy.SealWithPassphrase(t, e, Pass), default);
        var oldOpened = RecoveryCopy.OpenWithPassphrase(t, oldBlob, Pass);
        Func<Task> live = async () => await kit.Keys.RestoreAsync(t, oldOpened, oldFp, default);
        await live.Should().ThrowAsync<InvalidOperationException>().WithMessage("*only for a tenant whose keys were revoked*");

        await kit.Keys.RotateDekAsync(t, default);
        var (newBlob, newFp) = await kit.Keys.ExportRecoveryAsync(t, e => RecoveryCopy.SealWithPassphrase(t, e, Pass), default);
        await kit.RevokeNowAsync(t);

        Func<Task> stale = async () => await kit.Keys.RestoreAsync(t, RecoveryCopy.OpenWithPassphrase(t, oldBlob, Pass), oldFp, default);
        await stale.Should().ThrowAsync<InvalidOperationException>().WithMessage("*not the latest export*");
        kit.Store.Snapshot().Should().OnlyContain(r => r.Status == "Revoked", "a refused restore changes nothing");

        var tampered = RecoveryCopy.OpenWithPassphrase(t, newBlob, Pass).Select(e => e.Version == 2 ? e with { Dek = RandomNumberGenerator.GetBytes(32) } : e).ToList();
        Func<Task> badKey = async () => await kit.Keys.RestoreAsync(t, tampered, newFp, default);
        await badKey.Should().ThrowAsync<InvalidOperationException>().WithMessage("*failed verification against the stored canary*");
        var badRoot = RecoveryCopy.OpenWithPassphrase(t, newBlob, Pass).Select(e => e with { BlindIndexRoot = RandomNumberGenerator.GetBytes(32) }).ToList();
        Func<Task> badRootAct = async () => await kit.Keys.RestoreAsync(t, badRoot, newFp, default);
        await badRootAct.Should().ThrowAsync<InvalidOperationException>().WithMessage("*failed verification*");
        var partial = RecoveryCopy.OpenWithPassphrase(t, newBlob, Pass).Where(e => e.Version == 1).ToList();
        Func<Task> missing = async () => await kit.Keys.RestoreAsync(t, partial, newFp, default);
        await missing.Should().ThrowAsync<InvalidOperationException>().WithMessage("*does not cover exactly*");
        kit.Store.Snapshot().Should().OnlyContain(r => r.Status == "Revoked" && r.WrappedDek == null, "nothing is activated unless every key verified");

        await kit.Keys.RestoreAsync(t, RecoveryCopy.OpenWithPassphrase(t, newBlob, Pass), newFp, default);
        (await kit.Keys.GetRingAsync(t, false, default)).ActiveVersion.Should().Be(2);
    }

    [Fact]
    public async Task Export_IsRefusedForARevokedTenant()
    {
        var kit = new FeKit(); var t = Guid.NewGuid();
        await kit.Keys.GetRingAsync(t, true, default);
        await kit.Keys.ExportRecoveryAsync(t, e => RecoveryCopy.SealWithPassphrase(t, e, Pass), default);
        await kit.RevokeNowAsync(t);
        Func<Task> act = async () => await kit.Keys.ExportRecoveryAsync(t, e => RecoveryCopy.SealWithPassphrase(t, e, Pass), default);
        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task RevokeVersusRotate_Race_NeverLeavesAnUnrevokedKeyBehindAnApprovedRevoke()
    {
        for (var i = 0; i < 60; i++)
        {
            var kit = new FeKit(); var t = Guid.NewGuid(); var now = DateTime.UtcNow;
            await kit.Keys.GetRingAsync(t, true, default);
            await kit.Keys.ExportRecoveryAsync(t, e => RecoveryCopy.SealWithPassphrase(t, e, Pass), default);

            var revoke = Task.Run(async () => { try { await kit.Keys.RequestRevokeAsync(t, null, now, now, default); await kit.Keys.ApplyDueRevokeAsync(t, now, default); return true; } catch (InvalidOperationException) { return false; } });
            var rotate = Task.Run(async () => { try { await kit.Keys.RotateDekAsync(t, default); return true; } catch (InvalidOperationException) { return false; } });
            var results = await Task.WhenAll(revoke, rotate);

            var rows = kit.Store.Snapshot();
            if (results[0])
            {
                rows.Should().OnlyContain(r => r.Status == "Revoked" && r.WrappedDek == null, "an applied revoke covers EVERY version, including one a racing rotation tried to add");
                results[1].Should().BeFalse("the rotation could not slip a new live key in beside an approved revoke");
            }
            else
            {
                results[1].Should().BeTrue("when the revoke loses (the new version has no export) the rotation stands");
                rows.Should().HaveCount(2).And.Contain(r => r.Status == "Active" && r.WrappedDek != null);
            }
            rows.Count(r => r.Status == "Active").Should().BeLessOrEqualTo(1);
        }
    }

    [Fact]
    public async Task ConcurrentRotations_ProduceDistinctVersions_NeverADuplicateActive()
    {
        var kit = new FeKit(); var t = Guid.NewGuid();
        await kit.Keys.GetRingAsync(t, true, default);
        var tasks = Enumerable.Range(0, 5).Select(_ => Task.Run(async () => await kit.Keys.RotateDekAsync(t, default))).ToArray();
        var versions = await Task.WhenAll(tasks);
        versions.Distinct().Should().HaveCount(5, "each rotation sees the previous one's result");
        var rows = kit.Store.Snapshot();
        rows.Count(r => r.Status == "Active").Should().Be(1);
        rows.Select(r => r.KeyVersion).Should().BeEquivalentTo(Enumerable.Range(1, 6));
    }

    [Fact]
    public async Task EfStore_RejectsAStaleWrite_ViaTheRowVersionConcurrencyToken()
    {
        var name = "ef-" + Guid.NewGuid().ToString("N");
        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
        services.AddDbContext<Zayra.Api.Data.ZayraDbContext>(o => o.UseInMemoryDatabase(name));
        using var sp = services.BuildServiceProvider();
        var store = new EfFieldKeyStore(sp.GetRequiredService<Microsoft.Extensions.DependencyInjection.IServiceScopeFactory>());
        var t = Guid.NewGuid();
        await store.TransactAsync(t, _ => [new FieldEncryptionKey { TenantId = t, KeyVersion = 1, Status = "Active", WrappedDek = [1], WrappedBlindIndexRoot = [1], KekVersion = 1 }], default);

        Func<Task> staleWriter = async () => await store.TransactAsync(t, rows =>
        {
            rows[0].Status = "Retired";
            // A concurrent writer commits first (in a real database the row lock would make it wait; the token is the backstop).
            store.TransactAsync(t, inner => { inner[0].KekVersion = 7; return []; }, default).GetAwaiter().GetResult();
            return [];
        }, default);
        await staleWriter.Should().ThrowAsync<KeyStoreConflictException>();
        var row = (await store.LoadAsync(t, default)).Single();
        row.Status.Should().Be("Active", "the stale write did not land");
        row.KekVersion.Should().Be(7);
        row.RowVersion.Should().Be(1);
    }

    [Fact]
    public async Task IdleRings_AreEvictedAndWiped_ButBusyOnesAreKept()
    {
        var kit = new FeKit(); var idle = Guid.NewGuid(); var busy = Guid.NewGuid();
        var idleRing = await kit.Keys.GetRingAsync(idle, true, default);
        await kit.Keys.GetRingAsync(busy, true, default);
        var dekCopy = idleRing.ActiveDek.ToArray();
        kit.Keys.EvictIdle(TimeSpan.FromMinutes(5), DateTime.UtcNow.AddMinutes(4)).Should().Be(0);
        await Task.Delay(5);
        await kit.Keys.GetRingAsync(busy, true, default);
        kit.Keys.EvictIdle(TimeSpan.Zero, DateTime.UtcNow.AddMinutes(10)).Should().Be(2);
        idleRing.ActiveDek.Should().Equal(new byte[32], "an evicted ring's key bytes are wiped");
        dekCopy.Should().NotEqual(new byte[32]);
        kit.Keys.CachedTenants.Should().Be(0);
    }

    [Fact]
    public async Task UnavailableKek_OnSave_IsA503ShapedException_NotABareCryptoError()
    {
        var kit = new FeKit(); var t = Guid.NewGuid();
        await using (var db = kit.Db()) { db.Employees.Add(FeKit.NewEmployee(t, "E1", iqama: "2123456789")); await db.SaveChangesAsync(); }
        // Same store, but this instance holds a DIFFERENT kek (a mistyped secret): the key opens with an auth failure.
        var other = new FeKit();
        var keys = new FieldKeyService(kit.Store, other.Kek);
        var opts = new FieldEncryptionOptions { Mode = FieldEncryptionMode.DualWrite, Keks = other.Options.Keks, ActiveKekVersion = 1 };
        var b = new DbContextOptionsBuilder<Zayra.Api.Data.ZayraDbContext>().UseInMemoryDatabase(kit.DbName).AddInterceptors(new FieldEncryptionSaveInterceptor(opts, keys));
        await using var db2 = new Zayra.Api.Data.ZayraDbContext(b.Options);
        var e = await db2.Employees.SingleAsync();
        e.IqamaNumber = "2000000000";
        Func<Task> act = async () => await db2.SaveChangesAsync();
        var ex = (await act.Should().ThrowAsync<FieldKeyUnavailableException>()).Which;
        ex.StatusCode.Should().Be(503);
        ex.SafeMessage.Should().NotContain(t.ToString());
        new FieldEncryptionRevokedException(t).StatusCode.Should().Be(423);
        new FieldEncryptionRevokedException(t).SafeMessage.Should().NotContain(t.ToString());
    }

    // ---- the admin service: policy, step-up, actor, audit, notification ----------------------------------------

    [Fact]
    public async Task AdminService_Audits_EveryCustodyAction_WithActor_WithoutSecrets_AndNotifiesAdmins()
    {
        var kit = new FeKit(FieldEncryptionMode.EncryptedOnly); var t = Guid.NewGuid(); var audit = new RecordingAudit(); var notifier = new RecordingNotifier();
        var clock = new TestClock(DateTime.UtcNow);
        var admin = kit.Admin(audit, notifier: notifier, mode: FieldEncryptionMode.EncryptedOnly, coolingOff: TimeSpan.FromHours(24), clock: clock);
        var ctx = FeKit.Ctx(t); const string code = FakeStepUp.Good;

        var result = await admin.ExportRecoveryAsync(t, Pass, null, code, ActorKind.TenantAdmin, ctx, default);
        await admin.RotateDekAsync(t, code, ActorKind.PlatformOperator, ctx, default);
        // A rotation adds a key version the old recovery copy does not contain, so revoke must refuse until it is re-exported.
        Func<Task> stale = async () => await admin.RequestRevokeAsync(t, code, ActorKind.TenantAdmin, ctx, default);
        await stale.Should().ThrowAsync<InvalidOperationException>().WithMessage("*recovery copy first*");
        result = await admin.ExportRecoveryAsync(t, Pass, null, code, ActorKind.TenantAdmin, ctx, default);
        var req = await admin.RequestRevokeAsync(t, code, ActorKind.TenantAdmin, ctx, default);
        req.Applied.Should().BeFalse(); req.EffectiveAtUtc.Should().BeCloseTo(clock.GetUtcNow().UtcDateTime.AddHours(24), TimeSpan.FromSeconds(5));
        (await admin.StatusAsync(t, default)).RevokePending.Should().BeTrue();
        await admin.CancelRevokeAsync(t, code, ActorKind.TenantAdmin, ctx, default);
        await admin.RequestRevokeAsync(t, code, ActorKind.TenantAdmin, ctx, default);
        clock.Advance(TimeSpan.FromHours(25));
        (await admin.ApplyDueRevokesAsync(default)).Should().Be(1);
        await admin.RestoreAsync(t, result.RecoveryCopy, Pass, null, code, ActorKind.PlatformOperator, ctx, default);
        Func<Task> bad = async () => await admin.RestoreAsync(t, result.RecoveryCopy, "wrong passphrase wrong passphrase", null, code, ActorKind.TenantAdmin, ctx, default);
        await bad.Should().ThrowAsync<CryptographicException>();

        audit.Entries.Select(e => e.Action).Should().Equal(
            "FieldEncryption.RecoveryExported", "FieldEncryption.DekRotated", "FieldEncryption.RevokeRefused", "FieldEncryption.RecoveryExported",
            "FieldEncryption.RevokeRequested", "FieldEncryption.RevokeCancelled", "FieldEncryption.RevokeRequested", "FieldEncryption.Revoked",
            "FieldEncryption.Restored", "FieldEncryption.RestoreRejected");
        var all = string.Join("|", audit.Entries.Select(e => e.Metadata));
        all.Should().NotContain(Pass).And.NotContain(result.RecoveryCopy).And.NotContain("wrong passphrase");
        all.Should().Contain(result.Fingerprint).And.Contain("actor=tenant-admin").And.Contain("actor=platform-operator").And.Contain("actor=system");
        audit.Entries.Single(e => e.Action == "FieldEncryption.Revoked").Metadata.Should().Contain("backups retain wrapped keys");
        notifier.Sent.Select(n => n.Title).Should().Contain(new[] { "Encryption recovery copy downloaded", "Encryption key rotated", "Encryption key revoke requested",
            "Encryption key revoke cancelled", "Encryption keys revoked", "Encryption keys restored" });
        notifier.Sent.Should().OnlyContain(n => n.Tenant == t);
        (await admin.StatusAsync(t, default)).Keys.Should().HaveCount(2);
    }

    [Fact]
    public async Task AdminService_EveryCustodyAction_NeedsAFreshAuthenticatorCode_AndAFailureChangesNothing()
    {
        var kit = new FeKit(FieldEncryptionMode.EncryptedOnly); var t = Guid.NewGuid(); var audit = new RecordingAudit();
        var admin = kit.Admin(audit, mode: FieldEncryptionMode.EncryptedOnly);
        var ctx = FeKit.Ctx(t);
        await admin.ExportRecoveryAsync(t, Pass, null, FakeStepUp.Good, ActorKind.TenantAdmin, ctx, default);
        var before = kit.Store.Snapshot();

        foreach (var bad in new string?[] { null, "", "000000" })
        {
            await ((Func<Task>)(async () => await admin.ExportRecoveryAsync(t, Pass, null, bad, ActorKind.TenantAdmin, ctx, default))).Should().ThrowAsync<StepUpRequiredException>();
            await ((Func<Task>)(async () => await admin.RequestRevokeAsync(t, bad, ActorKind.TenantAdmin, ctx, default))).Should().ThrowAsync<StepUpRequiredException>();
            await ((Func<Task>)(async () => await admin.CancelRevokeAsync(t, bad, ActorKind.TenantAdmin, ctx, default))).Should().ThrowAsync<StepUpRequiredException>();
            await ((Func<Task>)(async () => await admin.RotateDekAsync(t, bad, ActorKind.PlatformOperator, ctx, default))).Should().ThrowAsync<StepUpRequiredException>();
            await ((Func<Task>)(async () => await admin.RestoreAsync(t, "{}", Pass, null, bad, ActorKind.PlatformOperator, ctx, default))).Should().ThrowAsync<StepUpRequiredException>();
        }
        kit.Store.Snapshot().Select(r => (r.RowVersion, r.Status)).Should().Equal(before.Select(r => (r.RowVersion, r.Status)), "no state changed");
        audit.Entries.Count(e => e.Action.EndsWith("StepUpFailed")).Should().Be(15);
    }

    [Fact]
    public async Task Export_IsTenantAdminOnly_ThePlatformCannotHoldAClientsKeys()
    {
        var kit = new FeKit(); var t = Guid.NewGuid(); var audit = new RecordingAudit(); var stepUp = new FakeStepUp();
        var admin = kit.Admin(audit, stepUp);
        await admin.ExportRecoveryAsync(t, Pass, null, FakeStepUp.Good, ActorKind.TenantAdmin, FeKit.Ctx(t), default);
        Func<Task> act = async () => await admin.ExportRecoveryAsync(t, Pass, null, FakeStepUp.Good, ActorKind.PlatformOperator, FeKit.Ctx(t), default);
        await act.Should().ThrowAsync<CustodyForbiddenException>();
        audit.Entries.Last().Action.Should().Be("FieldEncryption.RecoveryExportDenied");
        audit.Entries.Last().Metadata.Should().Contain("actor=platform-operator");
        Func<Task> sys = async () => await admin.ExportRecoveryAsync(t, Pass, null, FakeStepUp.Good, ActorKind.System, FeKit.Ctx(t), default);
        await sys.Should().ThrowAsync<CustodyForbiddenException>();
        stepUp.Calls.Should().HaveCount(1, "a forbidden actor is refused before any authenticator check");
    }

    [Theory]
    [InlineData(FieldEncryptionMode.Off)]
    [InlineData(FieldEncryptionMode.DualWrite)]
    public async Task Revoke_IsRefused_WhileTheDataIsStillInPlaintext(FieldEncryptionMode mode)
    {
        var kit = new FeKit(FieldEncryptionMode.DualWrite); var t = Guid.NewGuid(); var audit = new RecordingAudit();
        var admin = kit.Admin(audit, mode: mode);
        await kit.Keys.GetRingAsync(t, true, default);
        Func<Task> act = async () => await admin.RequestRevokeAsync(t, FakeStepUp.Good, ActorKind.TenantAdmin, FeKit.Ctx(t), default);
        (await act.Should().ThrowAsync<CustodyRefusedException>()).Which.Message.Should().Contain("no effect yet");
        audit.Entries.Single().Action.Should().Be("FieldEncryption.RevokeRefused");
        kit.Store.Snapshot().Single().Status.Should().Be("Active");
    }

    [Fact]
    public async Task Revoke_WithZeroCoolingOff_AppliesImmediately_ButThePlatformCanNeverRevokeForTheClient()
    {
        var kit = new FeKit(FieldEncryptionMode.EncryptedOnly); var t = Guid.NewGuid();
        var admin = kit.Admin(mode: FieldEncryptionMode.EncryptedOnly, coolingOff: TimeSpan.Zero);
        await admin.ExportRecoveryAsync(t, Pass, null, FakeStepUp.Good, ActorKind.TenantAdmin, FeKit.Ctx(t), default);
        Func<Task> platform = async () => await admin.RequestRevokeAsync(t, FakeStepUp.Good, ActorKind.PlatformOperator, FeKit.Ctx(t), default);
        await platform.Should().ThrowAsync<CustodyForbiddenException>();
        (await admin.RequestRevokeAsync(t, FakeStepUp.Good, ActorKind.TenantAdmin, FeKit.Ctx(t), default)).Applied.Should().BeTrue();
        kit.Store.Snapshot().Single().Status.Should().Be("Revoked");
    }

    [Fact]
    public async Task AdminService_Export_NeedsExactlyOneProtection_AndAnEnabledMode()
    {
        var kit = new FeKit(); var t = Guid.NewGuid();
        var admin = kit.Admin();
        Func<Task> none = async () => await admin.ExportRecoveryAsync(t, null, null, FakeStepUp.Good, ActorKind.TenantAdmin, FeKit.Ctx(t), default);
        Func<Task> both = async () => await admin.ExportRecoveryAsync(t, Pass, "pem", FakeStepUp.Good, ActorKind.TenantAdmin, FeKit.Ctx(t), default);
        await none.Should().ThrowAsync<ArgumentException>();
        await both.Should().ThrowAsync<ArgumentException>();

        var off = new FeKit(FieldEncryptionMode.Off);
        var offAdmin = off.Admin();
        Func<Task> disabled = async () => await offAdmin.ExportRecoveryAsync(t, Pass, null, FakeStepUp.Good, ActorKind.TenantAdmin, FeKit.Ctx(t), default);
        await disabled.Should().ThrowAsync<InvalidOperationException>();
    }
}
