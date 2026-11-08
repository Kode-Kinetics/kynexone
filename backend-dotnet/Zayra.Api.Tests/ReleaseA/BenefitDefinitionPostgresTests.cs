using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Common;
using Zayra.Api.Controllers.Entitlements;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Entitlements;
using Zayra.Api.Models;

namespace Zayra.Api.Tests.ReleaseA;

/// <summary>
/// The benefit definition on real PostgreSQL 16: the tenant lock serialises concurrent creates and edits (one winner, no duplicate row, no lost
/// update), a replay returns the original, a subset scope writes its skip markers and the definition columns through every CHECK and the unique key,
/// and a removed benefit comes back with its old markers reused.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration")]
public class BenefitDefinitionPostgresTests(PostgresFixture fixture)
{
    private static readonly DateOnly Today = new(2026, 10, 6);
    private static readonly DateOnly Nov1 = new(2026, 11, 1);
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private sealed record Seed(Guid TenantId, Guid Alpha, Guid Beta, Guid Hr, Guid Hr2);

    private async Task<Seed> SeedAsync()
    {
        await using var db = fixture.CreateDb();
        var tid = await PostgresFixture.SeedMinimalTenant(db);
        var alpha = new Company { TenantId = tid, LegalNameEn = $"Masar A {Guid.NewGuid():N}", CountryCode = "SA", DefaultCurrency = "SAR", IsActive = true };
        var beta = new Company { TenantId = tid, LegalNameEn = $"Masar B {Guid.NewGuid():N}", CountryCode = "SA", DefaultCurrency = "SAR", IsActive = true };
        db.AddRange(alpha, beta);
        db.TenantFeatureFlags.Add(new TenantFeatureFlag { TenantId = tid, FeatureKey = FeatureKeys.BenefitDefinitionV1, IsEnabled = true });
        await db.SaveChangesAsync();
        return new Seed(tid, alpha.Id, beta.Id, Guid.NewGuid(), Guid.NewGuid());
    }

    private static EntitlementMatrixController As(Seed seed, Guid user) => new()
    {
        ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(
                [
                    new("tenant_id", seed.TenantId.ToString()), new(ClaimTypes.NameIdentifier, user.ToString()), new(ClaimTypes.Role, "HR Director"),
                    new(EntityScopeContext.V2ClaimType, JsonSerializer.Serialize(new { v = 2, m = "group", c = Array.Empty<Guid>() })),
                ], "test")),
            },
        },
    };

    private sealed class FixedClock(DateOnly today) : ITenantClock
    {
        public Task<DateOnly> TodayAsync(Guid tenantId, CancellationToken ct) => Task.FromResult(today);
    }

    /// <summary>Every call gets its own context and connection, as separate requests do.</summary>
    private async Task<IActionResult> AddAsync(Seed seed, Guid user, CreateBenefitRequest req)
    {
        await using var db = fixture.CreateDb();
        return await As(seed, user).AddBenefit(new EntitlementMatrixService(db, new FixedClock(Today)), req, default);
    }

    private async Task<IActionResult> PutAsync(Seed seed, Guid user, string code, UpdateBenefitRequest req)
    {
        await using var db = fixture.CreateDb();
        return await As(seed, user).UpdateBenefit(new EntitlementMatrixService(db, new FixedClock(Today)), code, req, default);
    }

    private static CreateBenefitRequest Parking(Action<CreateBenefitRequestBuilder>? more = null)
    {
        var b = new CreateBenefitRequestBuilder("Parking") { Code = "PARK", Category = "Facilities", PolicyClass = "Discretionary", EffectiveFrom = Nov1 };
        more?.Invoke(b);
        return b.Build();
    }

    private static (int? Status, string? Error) Outcome(IActionResult r)
    {
        var o = (ObjectResult)r;
        var json = JsonSerializer.SerializeToElement(o.Value, Web);
        return (o.StatusCode, json.ValueKind == JsonValueKind.Object && json.TryGetProperty("error", out var e) ? e.GetString() : null);
    }

    private static string VersionOf(IActionResult r) => ((MatrixComponentDto)((ObjectResult)r).Value!).Definition!.Version;

    [Fact]
    public async Task ParallelCreatesOfTheSameName_LeaveExactlyOneBenefit_TheOthersAreBenefitExists()
    {
        var seed = await SeedAsync();

        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(i =>
            Task.Run(() => AddAsync(seed, Guid.NewGuid(), Parking(b => b.RequestId = Guid.NewGuid())))));

        results.Select(Outcome).Count(o => o.Status == 200).Should().Be(1);
        results.Select(Outcome).Where(o => o.Status != 200).Should().OnlyContain(o => o.Status == 409 && o.Error == "benefit_exists");
        await using var db = fixture.CreateDb();
        (await db.PayComponents.IgnoreQueryFilters().CountAsync(p => p.TenantId == seed.TenantId && p.Code == "BEN_PARK" && p.CompanyId == null)).Should().Be(1);
        (await db.AuditLogs.CountAsync(a => a.TenantId == seed.TenantId && a.Action == EntitlementMatrixService.AuditBenefitAdded)).Should().Be(1);
    }

    [Fact]
    public async Task ParallelReplaysOfOneRequest_AllReturnTheSameBenefit_AndWriteOnce()
    {
        var seed = await SeedAsync();
        var requestId = Guid.NewGuid();

        var results = await Task.WhenAll(Enumerable.Range(0, 6).Select(i =>
            Task.Run(() => AddAsync(seed, seed.Hr, Parking(b => b.RequestId = requestId)))));

        results.Select(Outcome).Should().OnlyContain(o => o.Status == 200);
        results.Select(VersionOf).Distinct().Should().HaveCount(1);
        await using var db = fixture.CreateDb();
        (await db.PayComponents.IgnoreQueryFilters().CountAsync(p => p.TenantId == seed.TenantId && p.Code == "BEN_PARK")).Should().Be(1);
        (await db.AuditLogs.CountAsync(a => a.TenantId == seed.TenantId && a.Action == EntitlementMatrixService.AuditBenefitAdded)).Should().Be(1);
    }

    [Fact]
    public async Task ParallelEditsWithTheSameVersion_OneWins_TheRestAreStale_NoLostUpdate()
    {
        var seed = await SeedAsync();
        var version = VersionOf(await AddAsync(seed, seed.Hr, Parking()));

        UpdateBenefitRequest Edit(string category) => new(version, "Parking", null) { Category = category, PolicyClass = "Discretionary", EffectiveFrom = Nov1 };
        var results = await Task.WhenAll(Enumerable.Range(0, 6).Select(i =>
            Task.Run(() => PutAsync(seed, Guid.NewGuid(), "BEN_PARK", Edit($"Category {i}")))));

        results.Select(Outcome).Count(o => o.Status == 200).Should().Be(1);
        results.Select(Outcome).Where(o => o.Status != 200).Should().OnlyContain(o => o.Status == 409 && o.Error == "stale_definition");
        await using var db = fixture.CreateDb();
        var row = await db.PayComponents.AsNoTracking().SingleAsync(p => p.TenantId == seed.TenantId && p.Code == "BEN_PARK" && p.CompanyId == null);
        row.BenefitCategory.Should().StartWith("Category ");
        (await db.AuditLogs.CountAsync(a => a.TenantId == seed.TenantId && a.Action == EntitlementMatrixService.AuditBenefitUpdated)).Should().Be(1);
    }

    [Fact]
    public async Task ASubsetScope_WritesItsMarkersAndTheDefinitionColumns_ThroughEveryCheck_AndComesBackWithItsMarkersReused()
    {
        var seed = await SeedAsync();

        Outcome(await AddAsync(seed, seed.Hr, Parking(b => b.Scope = new BenefitScopeInput([seed.Alpha])))).Status.Should().Be(200);

        await using (var db = fixture.CreateDb())
        {
            var rows = await db.PayComponents.IgnoreQueryFilters().AsNoTracking().Where(p => p.TenantId == seed.TenantId && p.Code == "BEN_PARK").ToListAsync();
            rows.Should().HaveCount(2);
            var tenantRow = rows.Single(r => r.CompanyId == null);
            (tenantRow.BenefitCategory, tenantRow.PolicyClass, tenantRow.RequiresEnrolment, tenantRow.EffectiveFrom).Should().Be(("Facilities", "Discretionary", (bool?)false, (DateOnly?)Nov1));
            var marker = rows.Single(r => r.CompanyId == seed.Beta);
            (marker.IsOffered, marker.EffectiveFrom, marker.BenefitCategory).Should().Be((false, (DateOnly?)Nov1, (string?)null));
        }

        // Remove (nothing uses it), then add it again with the same scope: the removed marker is reused, not a second row at the same key.
        await using (var db = fixture.CreateDb())
            Outcome(await As(seed, seed.Hr).RemoveBenefit(new EntitlementMatrixService(db, new FixedClock(Today)), "BEN_PARK", default)).Status.Should().Be(200);
        Outcome(await AddAsync(seed, seed.Hr, Parking(b => b.Scope = new BenefitScopeInput([seed.Alpha])))).Status.Should().Be(200);
        await using (var db = fixture.CreateDb())
        {
            var rows = await db.PayComponents.IgnoreQueryFilters().AsNoTracking().Where(p => p.TenantId == seed.TenantId && p.Code == "BEN_PARK").ToListAsync();
            rows.Should().HaveCount(2);
            rows.Should().OnlyContain(r => !r.IsDeleted);
            rows.Single(r => r.CompanyId == seed.Beta).IsOffered.Should().BeFalse();
        }
    }

    [Fact]
    public async Task ARefusedCreate_AfterTheLock_WritesNothing_NotEvenTheCatalogueSeed()
    {
        var seed = await SeedAsync();

        var refused = await AddAsync(seed, seed.Hr, Parking(b => b.PolicyClass = "Mandatory"));
        var refusedScope = await AddAsync(seed, seed.Hr, Parking(b => b.Scope = new BenefitScopeInput([Guid.NewGuid()])));

        Outcome(refused).Error.Should().Be("policy_class_invalid");
        Outcome(refusedScope).Error.Should().Be("company_not_found");
        await using var db = fixture.CreateDb();
        (await db.PayComponents.IgnoreQueryFilters().CountAsync(p => p.TenantId == seed.TenantId)).Should().Be(0);
        (await db.AuditLogs.CountAsync(a => a.TenantId == seed.TenantId)).Should().Be(0);
    }

    [Fact]
    public async Task ACashAllowance_WithDefinitionFields_StoresThemOnPostgres_AndRependsWhenHrChangesTheDates()
    {
        var seed = await SeedAsync();
        await using (var db = fixture.CreateDb())
        {
            db.GlDrivers.Add(new GlDriver
            {
                TenantId = seed.TenantId, CompanyId = null, Key = "EARN:CAR", Label = "Car", Category = GlDriverCategories.Earning, PostingSide = "DR",
                DefaultCode = "5210", DefaultName = "Car", MatchMode = "Exact", MatchComponentCode = "CAR", SortOrder = 70,
            });
            await db.SaveChangesAsync();
        }
        var created = await AddAsync(seed, seed.Hr, new CreateBenefitRequestBuilder("Car")
            { Delivery = "SalaryAllowance", Frequency = "Monthly", GlDriverKey = "EARN:CAR", Category = "Vehicle", PolicyClass = "Contractual" }.Build());
        Outcome(created).Status.Should().Be(200, JsonSerializer.Serialize(((ObjectResult)created).Value));

        var edited = await PutAsync(seed, seed.Hr2, "ALW_CAR", new UpdateBenefitRequest(VersionOf(created), "Car", null)
            { Category = "Vehicle", PolicyClass = "Contractual", EffectiveFrom = Nov1 });

        Outcome(edited).Status.Should().Be(200);
        await using var verify = fixture.CreateDb();
        var row = await verify.PayComponents.IgnoreQueryFilters().AsNoTracking().SingleAsync(p => p.TenantId == seed.TenantId && p.Code == "ALW_CAR");
        (row.BenefitCategory, row.PolicyClass, row.EffectiveFrom, row.FinanceConfirmedAtUtc, row.CreatedBy).Should().Be(("Vehicle", "Contractual", (DateOnly?)Nov1, (DateTime?)null, (Guid?)seed.Hr2));
    }
}
