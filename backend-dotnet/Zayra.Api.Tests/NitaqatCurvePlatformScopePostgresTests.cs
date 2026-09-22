using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Xunit;
using Zayra.Api.Application.CountryPack;
using Zayra.Api.Infrastructure.Compliance;
using Zayra.Api.Infrastructure.CountryPack;
using Zayra.Api.Infrastructure.Seed;

namespace Zayra.Api.Tests;

/// <summary>
/// ─────────────────────────────────────────────────────────────────────────────
///  THE PLATFORM-SCOPE TRAP, ON REAL POSTGRES, FOR THE CURVE PATH.
/// ─────────────────────────────────────────────────────────────────────────────
///
/// <para>This product has already been bitten once by exactly this: Nitaqat platform rows
/// (<c>TenantId = null</c>) were invisible in production while every unit test passed, because
/// the in-memory provider treats <c>x.TenantId == null</c> as ordinary LINQ where null equals
/// null, and PostgreSQL does not — <c>col = NULL</c> is never true.</para>
///
/// <para><see cref="NitaqatPlatformScopeSqlTests"/> guards that for the Nitaqat REFERENCE tables,
/// which are read through <c>ScopedBypass.NullableTenantWide</c>. The curve constants are not:
/// they live in <c>statutory_rules</c> and are read through <see cref="StatutoryRuleReader"/>,
/// a completely different query with its own null handling. Every curve loaded by this change
/// reaches a customer through THAT path, so it gets its own proof — and against a real database,
/// not a compiled query string.</para>
///
/// <para>If this test ever fails, the symptom in production is not an error. It is every
/// establishment being told "no Nitaqat band floors are configured for your activity", for ever,
/// with a green test suite.</para>
/// </summary>
[Collection("Integration")]
[Trait("Category", "Integration")]
public sealed class NitaqatCurvePlatformScopePostgresTests
{
    private readonly PostgresFixture _fixture;

    public NitaqatCurvePlatformScopePostgresTests(PostgresFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task SeededPlatformCurves_AreVisibleToATenant_OnRealPostgres()
    {
        await using var db = _fixture.CreateDb();

        // Write the platform defaults exactly as boot does — TenantId = null throughout.
        await StatutoryRuleSeeder.SeedAsync(db, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);

        var seeded = await db.StatutoryRules
            .IgnoreQueryFilters()
            .CountAsync(r => r.TenantId == null && r.RuleKey.StartsWith("nitaqat.curve."));
        seeded.Should().BeGreaterThan(600, "the 2026 annex is 42 activities of 16 rows each");

        // Now read them back as a TENANT would — a non-null tenant id, which is the branch that
        // has to fall back to the platform scope. This is the exact reader production uses.
        await using var readDb = _fixture.CreateDb();
        var reader = new StatutoryRuleReader(readDb);
        var someTenant = Guid.NewGuid();

        var floors = await NitaqatCurve.ResolveAsync(
            reader, "MANUFACTURING", 400m, new DateOnly(2026, 6, 1), someTenant);

        floors.Should().NotBeNull(
            "a tenant with no curve of its own must fall back to the platform default. If this is "
            + "null, every establishment in production is refused a band while the unit suite is green.");

        // The Ministry's C-2026 Manufacturing column, computed end to end through Postgres.
        floors!.LowGreen.Should().Be(25.15m);
        floors.MediumGreen.Should().Be(33.07m);
        floors.HighGreen.Should().Be(36.43m);
        floors.Platinum.Should().Be(42.33m);
    }

    /// <summary>
    /// The same read, with the platform rows present, must ALSO work for a null tenant id (the
    /// platform-only branch) — and must not leak another tenant's override.
    /// </summary>
    [Fact]
    public async Task PlatformScopeRead_AndTenantOverride_BothResolveCorrectly_OnRealPostgres()
    {
        await using var db = _fixture.CreateDb();
        await StatutoryRuleSeeder.SeedAsync(db, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);

        var tenantWithOverride = Guid.NewGuid();
        var otherTenant = Guid.NewGuid();

        // One tenant overrides a single gradient. Nothing else changes.
        db.StatutoryRules.Add(new Zayra.Api.Models.StatutoryRule
        {
            TenantId = tenantWithOverride,
            CountryCode = CountryCodes.Saudi,
            Jurisdiction = Jurisdictions.KsaMainland,
            RuleKey = NitaqatCurve.GradientKey("MANUFACTURING", Zayra.Api.Models.NitaqatBands.LowGreen),
            RuleValue = "9.99",
            DataType = "decimal",
            Description = "Test override.",
            EffectiveFrom = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            CreatedAtUtc = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();

        var asOf = new DateOnly(2026, 6, 1);

        // Platform scope (null tenant) sees the seeded gradient, never the override.
        await using var db1 = _fixture.CreateDb();
        (await new StatutoryRuleReader(db1).GetDecimalAsync(
                CountryCodes.Saudi, Jurisdictions.KsaMainland,
                NitaqatCurve.GradientKey("MANUFACTURING", Zayra.Api.Models.NitaqatBands.LowGreen), asOf, null))
            .Should().Be(1.68m, "the platform default is the annex gradient");

        // The overriding tenant sees its own value.
        await using var db2 = _fixture.CreateDb();
        (await new StatutoryRuleReader(db2).GetDecimalAsync(
                CountryCodes.Saudi, Jurisdictions.KsaMainland,
                NitaqatCurve.GradientKey("MANUFACTURING", Zayra.Api.Models.NitaqatBands.LowGreen), asOf, tenantWithOverride))
            .Should().Be(9.99m, "a tenant override outranks the platform default");

        // An unrelated tenant falls back to the platform default and never sees the override.
        await using var db3 = _fixture.CreateDb();
        (await new StatutoryRuleReader(db3).GetDecimalAsync(
                CountryCodes.Saudi, Jurisdictions.KsaMainland,
                NitaqatCurve.GradientKey("MANUFACTURING", Zayra.Api.Models.NitaqatBands.LowGreen), asOf, otherTenant))
            .Should().Be(1.68m, "one tenant's reading of the annex must not become every tenant's");
    }

    /// <summary>
    /// The seeder must stay idempotent on real Postgres. The unique index on
    /// <c>(tenant_id, country_code, jurisdiction, rule_key, effective_from)</c> does NOT protect
    /// platform rows from each other, because Postgres treats NULL as distinct for uniqueness —
    /// so a second boot that re-inserted would silently double every constant, and the reader's
    /// "newest effective date wins" tie-break would then pick arbitrarily between duplicates.
    /// </summary>
    [Fact]
    public async Task SeedingTwice_AddsNothing_OnRealPostgres()
    {
        await using var db = _fixture.CreateDb();
        var log = Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;

        await StatutoryRuleSeeder.SeedAsync(db, log);
        var first = await db.StatutoryRules.IgnoreQueryFilters().CountAsync(r => r.TenantId == null);

        await using var db2 = _fixture.CreateDb();
        await StatutoryRuleSeeder.SeedAsync(db2, log);
        var second = await db2.StatutoryRules.IgnoreQueryFilters().CountAsync(r => r.TenantId == null);

        second.Should().Be(first, "re-running the seeder must add nothing");
    }
}
