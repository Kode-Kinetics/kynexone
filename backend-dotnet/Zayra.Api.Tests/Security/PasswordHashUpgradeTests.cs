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
    /// <summary>The production work factor, explicitly: the test assembly makes default hashers cheap.</summary>
    private static Pbkdf2PasswordHasher Current() => new(Pbkdf2PasswordHasher.CurrentIterations);

    [Fact]
    public void NewHashes_CarryTheCurrentWorkFactor()
    {
        var hash = Current().Hash(Password);

        hash.Should().StartWith($"PBKDF2${Pbkdf2PasswordHasher.CurrentIterations}$");
        Pbkdf2PasswordHasher.CurrentIterations.Should().BeGreaterThanOrEqualTo(600_000);
        Current().Verify(Password, hash).Should().BeTrue();
        Current().NeedsRehash(hash).Should().BeFalse();
    }

    [Fact]
    public void LegacyHashes_StillVerify_AndAreFlaggedForRehash()
    {
        var legacy = Legacy.Hash(Password);
        legacy.Should().StartWith("PBKDF2$100000$");

        var current = Current();
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
        var hasher = Current();
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
            var result = await kit.Auth(db, Current()).LoginAsync(
                new LoginRequest("legacy@hardening.local", Password, AuthHardeningTestKit.TenantSlug),
                AuthHardeningTestKit.Ctx, CancellationToken.None);
            result.Tokens.Should().NotBeNull("the legacy hash must still sign the user in");
        }

        await using (var db = kit.NewDb())
        {
            var user = await db.Users.AsNoTracking().SingleAsync(u => u.Id == userId);
            user.PasswordHash.Should().StartWith($"PBKDF2${Pbkdf2PasswordHasher.CurrentIterations}$");
            Current().Verify(Password, user.PasswordHash).Should().BeTrue();
            user.UpdatedAtUtc.Should().Be(stampBefore,
                "re-encoding the same password is not a credential change; rotating User.UpdatedAtUtc "
                + "would sign the user out of every other device");
            (await db.AuditLogs.IgnoreQueryFilters().AnyAsync(a => a.Action == "auth.password_rehashed"
                && a.EntityId == userId.ToString())).Should().BeTrue();
        }

        // Second login uses the upgraded hash and does not rehash again.
        await using (var db = kit.NewDb())
        {
            var again = await kit.Auth(db, Current()).LoginAsync(
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
            var act = () => kit.Auth(db, Current()).LoginAsync(
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
        var changedElsewhere = Current().Hash("A-new-password-42!");

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

    [Fact]
    public async Task TwoSimultaneousFirstLogins_BothSucceed_WhenTheOtherAlreadyUpgradedTheHash()
    {
        await using var kit = await AuthHardeningTestKit.CreateAsync();
        var userId = await kit.SeedUserAsync("twin@hardening.local", Legacy.Hash(Password), roleName: null);
        // The "other" login upgrades the SAME password first, between this login's verify and its
        // guarded UPDATE, so this login's compare-and-set misses.
        var otherUpgrade = Current().Hash(Password);
        var racing = new RacingHasher(async () =>
        {
            await using var other = kit.NewDb();
            await other.Users.Where(u => u.Id == userId)
                .ExecuteUpdateAsync(s => s.SetProperty(u => u.PasswordHash, otherUpgrade));
        });

        await using (var db = kit.NewDb())
        {
            var result = await kit.Auth(db, racing).LoginAsync(
                new LoginRequest("twin@hardening.local", Password, AuthHardeningTestKit.TenantSlug),
                AuthHardeningTestKit.Ctx, CancellationToken.None);
            result.Tokens.Should().NotBeNull("the stored hash still verifies this password, so the login must pass");
        }

        await using (var db = kit.NewDb())
            (await db.Users.AsNoTracking().SingleAsync(u => u.Id == userId)).PasswordHash
                .Should().Be(otherUpgrade, "the winner's upgrade is kept, not overwritten");
    }

    private sealed class IterationCountingHasher(int iterations) : Pbkdf2PasswordHasher(iterations)
    {
        public long Iterations { get; private set; }
        protected override byte[] Derive(string password, byte[] salt, int iterations, int length)
        {
            Iterations += iterations;
            return base.Derive(password, salt, iterations, length);
        }
    }

    [Fact]
    public void AFailedCheckAgainstALegacyHash_CostsTheSameWorkAsTheDummyPath()
    {
        var legacy = Legacy.Hash(Password);

        var failedLegacy = new IterationCountingHasher(Pbkdf2PasswordHasher.CurrentIterations);
        failedLegacy.Verify("wrong-password", legacy).Should().BeFalse();

        var dummy = new IterationCountingHasher(Pbkdf2PasswordHasher.CurrentIterations);
        dummy.Verify("wrong-password", Current().Hash("never-disclosed")).Should().BeFalse();

        failedLegacy.Iterations.Should().Be(Pbkdf2PasswordHasher.CurrentIterations)
            .And.Be(dummy.Iterations, "a miss on an old hash must not answer faster than a miss on a current one");

        var succeeded = new IterationCountingHasher(Pbkdf2PasswordHasher.CurrentIterations);
        succeeded.Verify(Password, legacy).Should().BeTrue();
        succeeded.Iterations.Should().Be(100_000, "a success is followed by the re-hash, so it needs no padding");
    }

    [Fact]
    public void TheTestOverride_IsIgnoredWithoutTheExplicitSwitch()
    {
        Pbkdf2PasswordHasher.ResolveDefaultIterations(false, 1_000).Should().Be(Pbkdf2PasswordHasher.CurrentIterations,
            "an override set without the test-only AppContext switch must be ignored");
        Pbkdf2PasswordHasher.ResolveDefaultIterations(true, null).Should().Be(Pbkdf2PasswordHasher.CurrentIterations);
        Pbkdf2PasswordHasher.ResolveDefaultIterations(true, 0).Should().Be(Pbkdf2PasswordHasher.CurrentIterations);
        Pbkdf2PasswordHasher.ResolveDefaultIterations(true, 1_000).Should().Be(1_000);
    }

    [Fact]
    public void ProductionNeverLowersTheWorkFactor()
    {
        Pbkdf2PasswordHasher.DefaultIterationsOverride.Should().Be(1_000, "the test module initializer ran");
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        string? api = null;
        for (var i = 0; i < 8 && dir is not null; i++, dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "backend-dotnet", "Zayra.Api");
            if (Directory.Exists(candidate)) { api = candidate; break; }
        }
        if (api is null)
        {
            if (Environment.GetEnvironmentVariable("CI") is "true" or "1")
                throw new Xunit.Sdk.XunitException("Zayra.Api source not found under CI.");
            return;
        }
        var assignments = Directory.EnumerateFiles(api, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                        && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .SelectMany(f => File.ReadAllLines(f).Select(l => (File: f, Line: l)))
            .Where(x => x.Line.Contains("DefaultIterationsOverride", StringComparison.Ordinal)
                        && System.Text.RegularExpressions.Regex.IsMatch(x.Line, @"DefaultIterationsOverride\s*=[^=>]"))
            .ToList();
        assignments.Should().BeEmpty("only the test assembly may lower the default work factor");
        Directory.EnumerateFiles(api, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
            .Where(f => File.ReadAllText(f).Contains("SetSwitch(Pbkdf2PasswordHasher.WeakHashingSwitch", StringComparison.Ordinal)
                        || File.ReadAllText(f).Contains("SetSwitch(\"Zayra.Tests.AllowWeakPasswordHashing\"", StringComparison.Ordinal))
            .Should().BeEmpty("production must never turn on the weak-hashing switch");
    }

    private sealed class RacingHasher(Func<Task> onHash) : IPasswordHasher
    {
        private readonly Pbkdf2PasswordHasher _inner = new(Pbkdf2PasswordHasher.CurrentIterations);

        public string Hash(string password)
        {
            onHash().GetAwaiter().GetResult();
            return _inner.Hash(password);
        }

        public bool Verify(string password, string passwordHash) => _inner.Verify(password, passwordHash);
        public bool NeedsRehash(string passwordHash) => _inner.NeedsRehash(passwordHash);
    }
}
