using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Common;
using Zayra.Api.Controllers.Admin;
using Zayra.Api.Data;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// Concurrent "make this the default" writes on one reference list: each cleared the other defaults it could see and
/// committed its own flag, so two simultaneous writers left two defaults (and employee forms prefill the first one).
/// Writes that set IsDefault are now serialized per (tenant, type) with the transaction-scoped advisory lock.
/// Real Postgres: the race is only real there.
/// </summary>
[Collection("Integration")]
[Trait("Category", "Integration")]
public sealed class MasterDataDefaultConcurrencyPostgresTests(PostgresFixture fx)
{
    [Fact]
    public async Task ConcurrentDefaultWrites_LeaveExactlyOneDefault()
    {
        const int rounds = 12, writers = 4;
        for (var round = 0; round < rounds; round++)
        {
            Guid tenantId, typeId;
            var valueIds = new List<Guid>();
            await using (var seed = fx.CreateDb())
            {
                tenantId = await PostgresFixture.SeedMinimalTenant(seed);
                var type = new MasterDataType { TenantId = tenantId, Code = "MaritalStatus", NameEn = "Marital Status" };
                seed.MasterDataTypes.Add(type);
                for (var i = 0; i < writers; i++)
                {
                    var value = new MasterDataValue { TenantId = tenantId, TypeId = type.Id, Code = $"V{i}", ValueEn = $"Value {i}", SortOrder = i, IsDefault = i == 0 };
                    seed.MasterDataValues.Add(value);
                    valueIds.Add(value.Id);
                }
                await seed.SaveChangesAsync();
                typeId = type.Id;
            }

            // Every writer makes a DIFFERENT value the default at the same moment: half by edit, half by create.
            using var start = new ManualResetEventSlim(false);
            var tasks = Enumerable.Range(0, writers).Select(i => Task.Run(async () =>
            {
                await using var db = fx.CreateDb();
                var controller = For(db, tenantId);
                start.Wait();
                return i % 2 == 0
                    ? await controller.UpdateValue(valueIds[i], new MasterDataValueRequest($"V{i}", $"Value {i}", null, null, i, IsDefault: true), CancellationToken.None)
                    : await controller.CreateValue(typeId, new MasterDataValueRequest($"N{i}", $"New {i}", null, null, 10 + i, IsDefault: true), CancellationToken.None);
            })).ToList();
            start.Set();
            var results = await Task.WhenAll(tasks);

            results.Should().AllBeOfType<OkObjectResult>();
            await using var verify = fx.CreateDb();
            (await verify.MasterDataValues.CountAsync(v => v.TenantId == tenantId && v.TypeId == typeId && v.IsDefault && !v.IsDeleted))
                .Should().Be(1, $"round {round}: concurrent default writes must leave exactly one default");
        }
    }

    private static MasterDataController For(ZayraDbContext db, Guid tenantId) => new(db)
    {
        ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(new[]
                {
                    new Claim("tenant_id", tenantId.ToString()),
                    new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
                    new Claim(ClaimTypes.Role, "Admin"),
                    new Claim(EntityScopeContext.V2ClaimType, JsonSerializer.Serialize(new { v = 2, m = "group", c = Array.Empty<Guid>() })),
                }, "Test")),
            },
        },
    };
}
