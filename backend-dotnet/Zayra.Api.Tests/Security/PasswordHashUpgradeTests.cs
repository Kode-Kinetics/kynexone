using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Auth;
using Zayra.Api.Infrastructure.Auth;

namespace Zayra.Api.Tests.Security;

/// <summary>
/// PBKDF2-SHA256 moved from 100,000 to 600,000 iterations (OWASP). The iteration count is written into
/// every hash, so old hashes must keep verifying and must be upgraded the next time their owner signs
/// in — without signing them out of other devices and without letting a concurrent password change lose.
/// </summary>
public sealed class PasswordHashUpgradeTests
{
    private const string Password = "CorrectHorse-Battery-9!";
    private static readonly Pbkdf2PasswordHasher Legacy = new(100_000);

    [Fact]
    public void NewHashes_CarryTheCurrentWorkFactor()
    {
        var hash = new Pbkdf2PasswordHasher().Hash(Password);

        hash.Should().StartWith($"PBKDF2${Pbkdf2PasswordHasher.CurrentIterations}$");
        Pbkdf2PasswordHasher.CurrentIterations.Should().BeGreaterThanOrEqualTo(600_000);
        new Pbkdf2PasswordHasher().Verify(Password, hash).Should().BeTrue();
        new Pbkdf2PasswordHasher().NeedsRehash(hash).Should().BeFalse();
    }

    [Fact]
    public void LegacyHashes_StillVerify_AndAreFlaggedForRehash()
    {
        var legacy = Legacy.Hash(Password);
        legacy.Should().StartWith("PBKDF2$100000$");

        var current = new Pbkdf2PasswordHasher();
        current.Verify(Password, legacy).Should().BeTrue("raising the work factor must never invalidate a credential");
        current.Verify("wrong", legacy).Should().BeFalse();
        current.NeedsRehash(legacy).Should().BeTrue();
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-hash")]
    [InlineData("BCRYPT$600000$c2FsdA==$a2V5")]
    [InlineData("PBKDF2$abc$c2FsdA==$a2V5")]
    [InlineData("PBKDF2$600000$%%%$a2V5")]
    [InlineData("PBKDF2$2000000000$c2FsdA==$a2V5")]
    public void MalformedOrAbusiveHashes_NeitherVerifyNorRequestRehash(string stored)
    {
        var hasher = new Pbkdf2PasswordHasher();
        hasher.Verify(Password, stored).Should().BeFalse();
        hasher.NeedsRehash(stored).Should().BeFalse();
    }

    [Fact]
    public async Task SuccessfulLogin_UpgradesALegacyHash_WithoutRotatingTheSessionStamp()
    {
        await using var kit = await AuthHardeningTestKit.CreateAsync();
        var userId = await kit.SeedUserAsync("legacy@hardening.local", Legacy.Hash(Password), roleName: null);

        DateTime? stampBefore;
        await using (var db = kit.NewDb())
            stampBefore = (await db.Users.AsNoTracking().SingleAsync(u => u.Id == userId)).UpdatedAtUtc;

        await using (var db = kit.NewDb())
        {
            var result = await kit.Auth(db).LoginAsync(
                new LoginRequest("legacy@hardening.local", Password, AuthHardeningTestKit.TenantSlug),
                AuthHardeningTestKit.Ctx, CancellationToken.None);
            result.Tokens.Should().NotBeNull("the legacy hash must still sign the user in");
        }

        await using (var db = kit.NewDb())
        {
            var user = await db.Users.AsNoTracking().SingleAsync(u => u.Id == userId);
            user.PasswordHash.Should().StartWith($"PBKDF2${Pbkdf2PasswordHasher.CurrentIterations}$");
            new Pbkdf2PasswordHasher().Verify(Password, user.PasswordHash).Should().BeTrue();
            user.UpdatedAtUtc.Should().Be(stampBefore,
                "re-encoding the same password is not a credential change; rotating User.UpdatedAtUtc "
                + "would sign the user out of every other device");
            (await db.AuditLogs.IgnoreQueryFilters().AnyAsync(a => a.Action == "auth.password_rehashed"
                && a.EntityId == userId.ToString())).Should().BeTrue();
        }

        // Second login uses the upgraded hash and does not rehash again.
        await using (var db = kit.NewDb())
        {
            var again = await kit.Auth(db).LoginAsync(
                new LoginRequest("legacy@hardening.local", Password, AuthHardeningTestKit.TenantSlug),
                AuthHardeningTestKit.Ctx, CancellationToken.None);
            again.Tokens.Should().NotBeNull();
            (await db.AuditLogs.IgnoreQueryFilters().CountAsync(a => a.Action == "auth.password_rehashed"))
                .Should().Be(1);
        }
    }

    [Fact]
    public async Task FailedLogin_NeverRehashes()
    {
        await using var kit = await AuthHardeningTestKit.CreateAsync();
        var legacy = Legacy.Hash(Password);
        var userId = await kit.SeedUserAsync("legacy2@hardening.local", legacy, roleName: null);

        await using (var db = kit.NewDb())
        {
            var act = () => kit.Auth(db).LoginAsync(
                new LoginRequest("legacy2@hardening.local", "wrong-password", AuthHardeningTestKit.TenantSlug),
                AuthHardeningTestKit.Ctx, CancellationToken.None);
            await act.Should().ThrowAsync<UnauthorizedAccessException>();
        }

        await using (var db = kit.NewDb())
            (await db.Users.AsNoTracking().SingleAsync(u => u.Id == userId)).PasswordHash.Should().Be(legacy);
    }

    [Fact]
    public async Task Rehash_LosesToAConcurrentPasswordChange()
    {
        await using var kit = await AuthHardeningTestKit.CreateAsync();
        var userId = await kit.SeedUserAsync("race@hardening.local", Legacy.Hash(Password), roleName: null);
        var changedElsewhere = new Pbkdf2PasswordHasher().Hash("A-new-password-42!");

        // A hasher that changes the stored password the moment the rehash computes its new value —
        // i.e. between the verify and the guarded UPDATE.
        var racing = new RacingHasher(async () =>
        {
            await using var other = kit.NewDb();
            await other.Users.Where(u => u.Id == userId)
                .ExecuteUpdateAsync(s => s.SetProperty(u => u.PasswordHash, changedElsewhere));
        });

        await using (var db = kit.NewDb())
        {
            var act = () => kit.Auth(db, racing).LoginAsync(
                new LoginRequest("race@hardening.local", Password, AuthHardeningTestKit.TenantSlug),
                AuthHardeningTestKit.Ctx, CancellationToken.None);
            await act.Should().ThrowAsync<UnauthorizedAccessException>(
                "the stored credential changed after this password was verified, so no session may be issued");
        }

        await using (var db = kit.NewDb())
            (await db.Users.AsNoTracking().SingleAsync(u => u.Id == userId)).PasswordHash
                .Should().Be(changedElsewhere, "the guarded UPDATE must not overwrite a newer password");
    }

    private sealed class RacingHasher(Func<Task> onHash) : IPasswordHasher
    {
        private readonly Pbkdf2PasswordHasher _inner = new();

        public string Hash(string password)
        {
            onHash().GetAwaiter().GetResult();
            return _inner.Hash(password);
        }

        public bool Verify(string password, string passwordHash) => _inner.Verify(password, passwordHash);
        public bool NeedsRehash(string passwordHash) => _inner.NeedsRehash(passwordHash);
    }
}
