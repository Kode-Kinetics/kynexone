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
using Zayra.Api.Infrastructure.Finance;
using Zayra.Api.Models;

namespace Zayra.Api.Tests.ReleaseA;

/// <summary>
/// Creating a benefit and publishing grade values for it, racing, on real PostgreSQL 16. Both take the one per-tenant matrix lock, so a publish that
/// starts while a create is in flight waits for it and then sees the new benefit; a create waits for a publish the same way; and when the two are
/// fired together the only outcomes are "publish after create" (values stored) or "publish before create" (refused, nothing stored): never a value
/// for a benefit that has no definition. The first two tests fix the interleaving with a gate, so they fail deterministically if either path stops
/// taking the lock; the third is the unscripted version.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration")]
public class BenefitDefinitionRacePostgresTests(PostgresFixture fixture)
{
    private static readonly DateOnly Today = new(2026, 10, 6);
    private static readonly DateOnly Nov1 = new(2026, 11, 1);
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);
    private static readonly TimeSpan Blocked = TimeSpan.FromMilliseconds(1500);
    private static readonly TimeSpan Generous = TimeSpan.FromSeconds(60);

    private sealed record Seed(Guid TenantId, Guid GradeId, Guid Hr);

    private async Task<Seed> SeedAsync()
    {
        await using var db = fixture.CreateDb();
        var tid = await PostgresFixture.SeedMinimalTenant(db);
        db.Companies.Add(new Company { TenantId = tid, LegalNameEn = $"Masar {Guid.NewGuid():N}", CountryCode = "SA", DefaultCurrency = "SAR", IsActive = true });
        var grade = new Grade { TenantId = tid, Code = "G1", Name = "Grade 1", Level = 10 };
        db.Grades.Add(grade);
        db.TenantFeatureFlags.Add(new TenantFeatureFlag { TenantId = tid, FeatureKey = FeatureKeys.BenefitDefinitionV1, IsEnabled = true });
        await db.SaveChangesAsync();
        return new Seed(tid, grade.Id, Guid.NewGuid());
    }

    private static EntitlementMatrixController As(Seed seed) => new()
    {
        ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(
                [
                    new("tenant_id", seed.TenantId.ToString()), new(ClaimTypes.NameIdentifier, seed.Hr.ToString()), new(ClaimTypes.Role, "HR Director"),
                    new(EntityScopeContext.V2ClaimType, JsonSerializer.Serialize(new { v = 2, m = "group", c = Array.Empty<Guid>() })),
                ], "test")),
            },
        },
    };

    private sealed class FixedClock(DateOnly today) : ITenantClock
    {
        public Task<DateOnly> TodayAsync(Guid tenantId, CancellationToken ct) => Task.FromResult(today);
    }

    private static CreateBenefitRequest Benefit(string name) =>
        new CreateBenefitRequestBuilder(name) { Category = "Facilities", PolicyClass = "Discretionary", EffectiveFrom = Nov1 }.Build();

    /// <summary>A create on its own context and connection, as its own request. <paramref name="insideLock"/> runs right after the tenant lock is taken.</summary>
    private async Task<IActionResult> CreateAsync(Seed seed, string name, Func<Task>? insideLock = null)
    {
        await using var db = fixture.CreateDb();
        var service = new EntitlementMatrixService(db, new FixedClock(Today)) { AfterLockHook = insideLock };
        return await As(seed).AddBenefit(service, Benefit(name), default);
    }

    private async Task<IActionResult> PublishAsync(Seed seed, string code)
    {
        await using var db = fixture.CreateDb();
        var cell = new MatrixCellInput { GradeId = seed.GradeId, ComponentCode = code, Eligible = true, ValueType = "Amount", Amount = 100m };
        return await As(seed).Publish(new EntitlementMatrixService(db, new FixedClock(Today)), new PublishMatrixRequest(null, Nov1, [cell]), false, default);
    }

    private static (int? Status, string? Error) Outcome(IActionResult r)
    {
        var o = (ObjectResult)r;
        var json = JsonSerializer.SerializeToElement(o.Value, Web);
        var error = json.ValueKind == JsonValueKind.Object && json.TryGetProperty("error", out var e) ? e.GetString() : null;
        return (o.StatusCode, error);
    }

    private static string Describe(IActionResult r) => r is ObjectResult o ? $"{o.StatusCode}: {JsonSerializer.Serialize(o.Value, Web)}" : r.GetType().Name;

    private async Task<int> CellsAsync(Seed seed, string code)
    {
        await using var db = fixture.CreateDb();
        return await db.GradeEntitlements.AsNoTracking().CountAsync(g => g.TenantId == seed.TenantId && g.PayComponentCode == code);
    }

    private async Task<bool> DefinedAsync(Seed seed, string code)
    {
        await using var db = fixture.CreateDb();
        return await db.PayComponents.IgnoreQueryFilters().AsNoTracking().AnyAsync(p => p.TenantId == seed.TenantId && p.Code == code && !p.IsDeleted);
    }

    [Fact]
    public async Task APublishThatStartsWhileACreateHoldsTheLock_WaitsForIt_AndThenSeesTheNewBenefit()
    {
        var seed = await SeedAsync();
        var inside = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var create = Task.Run(() => CreateAsync(seed, "Parking", async () => { inside.SetResult(); await release.Task; }));
        await inside.Task.WaitAsync(Generous);   // the create now holds the tenant lock and has written nothing yet
        var publish = Task.Run(() => PublishAsync(seed, "BEN_PARKING"));

        (await Task.WhenAny(publish, Task.Delay(Blocked))).Should().NotBeSameAs(publish, "a publish must queue behind a create that holds the tenant matrix lock");
        (await CellsAsync(seed, "BEN_PARKING")).Should().Be(0);

        release.SetResult();
        var created = await create.WaitAsync(Generous);
        var published = await publish.WaitAsync(Generous);

        Outcome(created).Status.Should().Be(200, Describe(created));
        Outcome(published).Status.Should().Be(200, Describe(published));
        (await CellsAsync(seed, "BEN_PARKING")).Should().Be(1);
    }

    [Fact]
    public async Task ACreateThatStartsWhileAPublishHoldsTheLock_WaitsForIt()
    {
        var seed = await SeedAsync();
        var inside = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        // Stand in for a publish in flight: the same lock (scope, tenant, tenant) every matrix write takes, held until the test lets go.
        await using var holderDb = fixture.CreateDb();
        var holder = Task.Run(() => FinanceDecisionSerializer.SerializeAsync(holderDb, EntitlementMatrixService.LockScope, seed.TenantId, seed.TenantId,
            async () => { inside.SetResult(); await release.Task; return 0; }));
        await inside.Task.WaitAsync(Generous);

        var create = Task.Run(() => CreateAsync(seed, "Parking"));

        (await Task.WhenAny(create, Task.Delay(Blocked))).Should().NotBeSameAs(create, "a create must queue behind any matrix write that holds the tenant lock");
        (await DefinedAsync(seed, "BEN_PARKING")).Should().BeFalse();

        release.SetResult();
        await holder.WaitAsync(Generous);
        var created = await create.WaitAsync(Generous);

        Outcome(created).Status.Should().Be(200, Describe(created));
        (await DefinedAsync(seed, "BEN_PARKING")).Should().BeTrue();
    }

    [Fact]
    public async Task CreatesAndPublishesFiredTogether_NeverLeaveAValueForABenefitWithNoDefinition()
    {
        var seed = await SeedAsync();
        var names = Enumerable.Range(0, 6).Select(i => $"Perk {(char)('A' + i)}").ToArray();
        var codes = names.Select(n => "BEN_" + n.ToUpperInvariant().Replace(' ', '_')).ToArray();

        // Every benefit is created and published for at the same moment, the publish started a hair first so it can win the race.
        var tasks = names.Select((n, i) => (Publish: Task.Run(() => PublishAsync(seed, codes[i])), Create: Task.Run(() => CreateAsync(seed, n)))).ToArray();
        await Task.WhenAll(tasks.SelectMany(t => new[] { t.Publish, t.Create })).WaitAsync(Generous);

        for (var i = 0; i < codes.Length; i++)
        {
            var created = await tasks[i].Create;
            var published = await tasks[i].Publish;
            Outcome(created).Status.Should().Be(200, Describe(created));
            (await DefinedAsync(seed, codes[i])).Should().BeTrue();
            var cells = await CellsAsync(seed, codes[i]);
            if (Outcome(published).Status == 200) cells.Should().Be(1, $"{codes[i]}: a publish that succeeded stored its value");
            else
            {
                Outcome(published).Should().Be((400, "invalid_cells"), $"{codes[i]}: a publish that came first is refused as a whole: {Describe(published)}");
                cells.Should().Be(0, $"{codes[i]}: a refused publish stores nothing");
            }
        }
    }
}
