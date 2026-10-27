using System.Security.Cryptography;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Zayra.Api.Controllers;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.Auth;
using Zayra.Api.Infrastructure.FieldEncryption;
using Zayra.Api.Infrastructure.Notifications;
using Zayra.Api.Models;

namespace Zayra.Api.Tests.FieldEncryption;

public class FieldEncryptionHardeningTests
{
    private static readonly EncryptedField Passport = EncryptedFieldCatalog.Find(typeof(EmployeeDraft), nameof(EmployeeDraft.PassportNumber))!;

    private static async Task SeedDraftsAsync(FeKit kit, Guid t)
    {
        await using var db = kit.Db();
        db.EmployeeDrafts.AddRange(new EmployeeDraft { TenantId = t, PassportNumber = "P1111111" }, new EmployeeDraft { TenantId = t, PassportNumber = "P2222222" });
        await db.SaveChangesAsync();
    }

    // ---- AAD: a ciphertext cannot be moved to another row of the same tenant ------------------------------------

    [Fact]
    public async Task SwappingTwoRowsCiphertext_IsDetected_ByVerify()
    {
        var kit = new FeKit(); var t = Guid.NewGuid();
        await SeedDraftsAsync(kit, t);
        await using (var db = kit.Db(interceptors: false))
        {
            var rows = await db.EmployeeDrafts.OrderBy(d => d.PassportNumber).ToListAsync();
            var a = (byte[])db.Entry(rows[0]).Property("PassportNumberEnc").CurrentValue!;
            var b = (byte[])db.Entry(rows[1]).Property("PassportNumberEnc").CurrentValue!;
            db.Entry(rows[0]).Property("PassportNumberEnc").CurrentValue = b;
            db.Entry(rows[1]).Property("PassportNumberEnc").CurrentValue = a;
            await db.SaveChangesAsync();
        }
        await using var v = kit.Db();
        var report = await kit.Backfill(v).VerifyAsync(t, 1000, null, default);
        report.Mismatches.Where(m => m.Entity == nameof(EmployeeDraft)).Select(m => m.Kind).Should().OnlyContain(k => k == MismatchKind.UndecryptableCiphertext);
        report.MismatchTotal.Should().Be(2, "both swapped values fail authentication against the row they now sit in");
    }

    [Fact]
    public async Task Reencrypt_UpgradesFormat1Values_ToFormat2()
    {
        var kit = new FeKit(); var t = Guid.NewGuid();
        await SeedDraftsAsync(kit, t);
        var ring = await kit.Keys.GetRingAsync(t, false, default);
        await using (var db = kit.Db(interceptors: false))
        {
            foreach (var d in await db.EmployeeDrafts.ToListAsync())
                db.Entry(d).Property("PassportNumberEnc").CurrentValue =
                    FieldEnvelope.Seal(ring.ActiveDek, 1, d.PassportNumber, FieldCipher.LegacyAad("employee_drafts", Passport), FieldEnvelope.FormatV1);
            await db.SaveChangesAsync();
        }
        await using (var v = kit.Db()) (await kit.Backfill(v).VerifyAsync(t, 1000, null, default)).MismatchTotal.Should().Be(0, "format 1 still opens");
        await using (var r = kit.Db()) (await kit.Backfill(r).ReencryptToActiveAsync(t, 1000, default)).Encrypted.Should().Be(2);
        await using var check = kit.Db(interceptors: false);
        foreach (var d in await check.EmployeeDrafts.ToListAsync())
            ((byte[])check.Entry(d).Property("PassportNumberEnc").CurrentValue!)[0].Should().Be(2);
    }

    // ---- repair --------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Repair_RebuildsStaleCiphertext_ThatVerifyReports_AndLeavesGoodRowsAlone()
    {
        var kit = new FeKit(); var t = Guid.NewGuid();
        await SeedDraftsAsync(kit, t);
        await using (var raw = kit.Db(interceptors: false))   // a bypassing write: plaintext changes, ciphertext does not
        {
            var d = await raw.EmployeeDrafts.OrderBy(x => x.PassportNumber).FirstAsync();
            d.PassportNumber = "P9999999"; await raw.SaveChangesAsync();
        }
        await using (var v = kit.Db()) (await kit.Backfill(v).VerifyAsync(t, 1000, null, default)).Mismatches.Should().ContainSingle(m => m.Kind == MismatchKind.PlaintextDiffers);
        await using (var r = kit.Db())
        {
            var fixedUp = await kit.Backfill(r).RepairAsync(t, 1000, null, default);
            fixedUp.Repaired.Should().Be(1);
        }
        await using var again = kit.Db();
        (await kit.Backfill(again).VerifyAsync(t, 1000, null, default)).MismatchTotal.Should().Be(0);
    }

    [Fact]
    public async Task Repair_NeedsDualWrite()
    {
        var kit = new FeKit(FieldEncryptionMode.Off); var t = Guid.NewGuid();
        await using var db = kit.Db(FieldEncryptionMode.Off);
        Func<Task> act = async () => await kit.Backfill(db, FieldEncryptionMode.Off).RepairAsync(t, 10, null, default);
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*DualWrite*");
    }

    // ---- reprotect: compare-and-set --------------------------------------------------------------------------

    private sealed class MutatingProvider(IDataProtectionProvider inner, Action onProtect) : IDataProtectionProvider
    {
        public IDataProtector CreateProtector(string purpose) => new P(inner.CreateProtector(purpose), onProtect);
        private sealed class P(IDataProtector inner, Action onProtect) : IDataProtector
        {
            public IDataProtector CreateProtector(string purpose) => new P(inner.CreateProtector(purpose), onProtect);
            public byte[] Protect(byte[] plaintext) { onProtect(); return inner.Protect(plaintext); }
            public byte[] Unprotect(byte[] protectedData) => inner.Unprotect(protectedData);
        }
    }

    [Fact]
    public async Task Reprotect_DoesNotOverwriteASecretReEnrolledWhileItRuns()
    {
        var dir = Path.Combine(Path.GetTempPath(), "fe-rp-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(dir);
        try
        {
            var s = new ServiceCollection();
            s.AddDataProtection().SetApplicationName("Zayra.Api").PersistKeysToFileSystem(new DirectoryInfo(dir));
            using var sp = s.BuildServiceProvider();
            var dp = sp.GetRequiredService<IDataProtectionProvider>();
            var manager = sp.GetRequiredService<Microsoft.AspNetCore.DataProtection.KeyManagement.IKeyManager>();
            var mfa = dp.CreateProtector("Zayra.Mfa.TotpSecret.v1");
            var oldSecret = mfa.Protect("OLDSECRETOLDSECRET");

            var name = "rp-" + Guid.NewGuid().ToString("N");
            DbContextOptions<Zayra.Api.Data.ZayraDbContext> Opts() => new DbContextOptionsBuilder<Zayra.Api.Data.ZayraDbContext>().UseInMemoryDatabase(name).Options;
            var tenant = Guid.NewGuid();
            Guid userId;
            await using (var seed = new Zayra.Api.Data.ZayraDbContext(Opts()))
            {
                var u = new User { TenantId = tenant, Email = "a@x.test", FullName = "A", PasswordHash = "x", MfaSecretEncrypted = oldSecret };
                seed.Users.Add(u); await seed.SaveChangesAsync(); userId = u.Id;
            }
            var reenrolled = mfa.Protect("NEWSECRETNEWSECRET");
            await using var db = new Zayra.Api.Data.ZayraDbContext(Opts());
            // The moment the job protects the re-protected value, the user finishes a re-enrolment in another request.
            var calls = 0;   // the first Protect call is the job's own key probe; the second is the row being re-protected
            var provider = new MutatingProvider(dp, () =>
            {
                if (++calls != 2) return;
                using var other = new Zayra.Api.Data.ZayraDbContext(Opts());
                var u = other.Users.Single(x => x.Id == userId); u.MfaSecretEncrypted = reenrolled; other.SaveChanges();
            });
            var job = new DataProtectionSecretReprotector(db, provider, manager);
            job.CreateNewDefaultKey();
            var report = await job.RunAsync(default);

            report.Conflicts.Should().Be(1);
            report.Rewritten.Should().Be(0);
            report.SafeToRevokeOldKeys.Should().BeFalse();
            await using var read = new Zayra.Api.Data.ZayraDbContext(Opts());
            (await read.Users.SingleAsync()).MfaSecretEncrypted.Should().Be(reenrolled, "the user's new secret survives");
        }
        finally { try { Directory.Delete(dir, true); } catch { /* best effort */ } }
    }

    [Fact]
    public void Reprotect_ReportsUnreadable_AsNotSafeToRevokeOldKeys()
    {
        new ReprotectReport(5, 0, 1, 0).SafeToRevokeOldKeys.Should().BeFalse();
        new ReprotectReport(5, 0, 0, 0).SafeToRevokeOldKeys.Should().BeTrue();
    }

    // ---- step-up --------------------------------------------------------------------------------------------

    private static string Base32Secret(TotpService totp) => totp.GenerateBase32Secret();

    private static string Code(string base32, long? step = null)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var bits = 0; var acc = 0; var bytes = new List<byte>();
        foreach (var c in base32) { acc = (acc << 5) | alphabet.IndexOf(c); bits += 5; if (bits >= 8) { bits -= 8; bytes.Add((byte)((acc >> bits) & 0xFF)); } }
        var counter = BitConverter.GetBytes(step ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 30);
        if (BitConverter.IsLittleEndian) Array.Reverse(counter);
        var hash = HMACSHA1.HashData(bytes.ToArray(), counter);
        var o = hash[^1] & 0x0F;
        var bin = ((hash[o] & 0x7F) << 24) | (hash[o + 1] << 16) | (hash[o + 2] << 8) | hash[o + 3];
        return (bin % 1_000_000).ToString("D6");
    }

    [Fact]
    public async Task StepUp_AcceptsAFreshCodeOnce_RefusesReplayWrongCodeOtherTenantAndNoMfa()
    {
        var totp = new TotpService(new EphemeralDataProtectionProvider());
        var secret = Base32Secret(totp);
        var name = "su-" + Guid.NewGuid().ToString("N");
        var db = new Zayra.Api.Data.ZayraDbContext(new DbContextOptionsBuilder<Zayra.Api.Data.ZayraDbContext>().UseInMemoryDatabase(name).Options);
        var tenant = Guid.NewGuid();
        var admin = new User { TenantId = tenant, Email = "a@x.test", FullName = "A", PasswordHash = "x", MFAEnabled = true, MfaSecretEncrypted = totp.EncryptSecret(secret) };
        var noMfa = new User { TenantId = tenant, Email = "b@x.test", FullName = "B", PasswordHash = "x" };
        db.Users.AddRange(admin, noMfa); await db.SaveChangesAsync();
        var verifier = new TotpStepUpVerifier(db, totp);

        var code = Code(secret);
        (await verifier.VerifyAsync(ActorKind.TenantAdmin, admin.Id, tenant, "000000" == code ? "000001" : "000000", default)).Should().BeFalse("wrong code");
        (await verifier.VerifyAsync(ActorKind.TenantAdmin, admin.Id, Guid.NewGuid(), code, default)).Should().BeFalse("not a user of that tenant");
        (await verifier.VerifyAsync(ActorKind.TenantAdmin, admin.Id, tenant, null, default)).Should().BeFalse();
        (await verifier.VerifyAsync(ActorKind.TenantAdmin, noMfa.Id, tenant, code, default)).Should().BeFalse("no authenticator enrolled");
        (await verifier.VerifyAsync(ActorKind.TenantAdmin, admin.Id, tenant, code, default)).Should().BeTrue();
        (await verifier.VerifyAsync(ActorKind.TenantAdmin, admin.Id, tenant, code, default)).Should().BeFalse("a code is single use");

        var pu = new PlatformUser { Email = "p@x.test", FullName = "P", PasswordHash = "x", MfaEnabled = true, MfaSecretEncrypted = totp.EncryptSecret(secret) };
        db.PlatformUsers.Add(pu); await db.SaveChangesAsync();
        (await verifier.VerifyAsync(ActorKind.PlatformOperator, pu.Id, tenant, Code(secret), default)).Should().BeTrue();
        (await verifier.VerifyAsync(ActorKind.PlatformOperator, admin.Id, tenant, Code(secret), default)).Should().BeFalse("a tenant user id is not a platform operator");
    }

    private sealed class CapturingNotifications : INotificationService
    {
        public List<(Guid? User, string Title)> Sent { get; } = [];
        public Task NotifyAsync(Guid tenantId, Guid? userId, string title, string message, string entityName, string? entityId, CancellationToken ct) { Sent.Add((userId, title)); return Task.CompletedTask; }
        public Task SendEmailAsync(Guid tenantId, string templateCode, string toAddress, string toName, Dictionary<string, string> variables, CancellationToken ct) => Task.CompletedTask;
    }

    [Fact]
    public async Task Notifier_ReachesEveryActiveTenantAdmin_AndOnlyThem()
    {
        var db = new Zayra.Api.Data.ZayraDbContext(new DbContextOptionsBuilder<Zayra.Api.Data.ZayraDbContext>().UseInMemoryDatabase("nt-" + Guid.NewGuid().ToString("N")).Options);
        var t = Guid.NewGuid(); var other = Guid.NewGuid();
        var role = new Role { TenantId = t, Name = "Admin", NormalizedName = "ADMIN" };
        var viewer = new Role { TenantId = t, Name = "Viewer", NormalizedName = "VIEWER" };
        User U(Guid tenant, string e, bool active = true) => new() { TenantId = tenant, Email = e, FullName = e, PasswordHash = "x", IsActive = active };
        var a1 = U(t, "a1@x.test"); var a2 = U(t, "a2@x.test"); var gone = U(t, "gone@x.test", active: false); var v = U(t, "v@x.test"); var foreign = U(other, "f@x.test");
        db.Roles.AddRange(role, viewer); db.Users.AddRange(a1, a2, gone, v, foreign);
        db.UserRoles.AddRange(new UserRole { UserId = a1.Id, RoleId = role.Id }, new UserRole { UserId = a2.Id, RoleId = role.Id }, new UserRole { UserId = gone.Id, RoleId = role.Id },
            new UserRole { UserId = v.Id, RoleId = viewer.Id }, new UserRole { UserId = foreign.Id, RoleId = role.Id });
        await db.SaveChangesAsync();
        var sink = new CapturingNotifications();
        await new TenantAdminCustodyNotifier(db, sink, NullLogger<TenantAdminCustodyNotifier>.Instance).NotifyTenantAdminsAsync(t, "T", "M", default);
        sink.Sent.Select(s => s.User).Should().BeEquivalentTo([(Guid?)a1.Id, a2.Id]);
    }

    // ---- surface -----------------------------------------------------------------------------------------------

    [Fact]
    public void ThePlatformSurface_HasNoExportAndNoRevoke_AndEveryMutatingActionNeedsAPlatformRole()
    {
        var methods = typeof(PlatformFieldEncryptionController).GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.DeclaredOnly);
        methods.Select(m => m.Name).Should().NotContain(["Export", "Revoke", "RequestRevoke"]);
        foreach (var m in methods.Where(m => m.GetCustomAttributes(typeof(Microsoft.AspNetCore.Mvc.HttpPostAttribute), false).Length > 0))
            m.GetCustomAttributes(typeof(RequirePlatformRoleAttribute), false).Should().NotBeEmpty($"{m.Name} must name the platform roles allowed");
        typeof(PlatformDataProtectionRingController).GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.DeclaredOnly)
            .Where(m => m.GetCustomAttributes(typeof(Microsoft.AspNetCore.Mvc.HttpPostAttribute), false).Length > 0)
            .Should().OnlyContain(m => m.GetCustomAttributes(typeof(RequirePlatformRoleAttribute), false).Length > 0);
    }

    [Fact]
    public void FieldEncryptionUnavailable_MapsToLockedOrUnavailable_NeverA500_AndNeverLeaksTheTenant()
    {
        var t = Guid.NewGuid();
        FieldEncryptionUnavailableException[] all = [new FieldEncryptionRevokedException(t), new FieldKeyUnavailableException("detail " + t), new KekUnavailableException("kek 3 missing")];
        all.Select(e => e.StatusCode).Should().Equal(423, 503, 503);
        all.Should().OnlyContain(e => !e.SafeMessage.Contains(t.ToString()) && !e.SafeMessage.Contains("kek"));
        all.Should().OnlyContain(e => e is InvalidOperationException);
    }
}
