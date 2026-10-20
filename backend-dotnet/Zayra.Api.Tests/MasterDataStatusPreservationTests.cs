using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Controllers.Admin;
using Zayra.Api.Data;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// Master Data status is an explicit decision, never a side-effect of an unrelated edit.
///
/// <para>DEFECT: the Setup › Master Data value modal saved <c>{ code, valueEn, valueAr, sortOrder, isDefault }</c>
/// with no <c>isActive</c>. <c>MasterDataValueRequest.IsActive</c> defaulted to <c>true</c> and UpdateValue assigned it,
/// so renaming a value someone had retired silently put it back in every employee dropdown. The same PUT also
/// cleared <c>ExtraJson</c>. Types had the opposite gap: <c>MasterDataTypeRequest</c> had no IsActive at all, so a
/// type could never be retired.</para>
///
/// <para>Requests are deserialised from JSON with the web defaults the API uses, so these tests exercise the wire
/// contract — an absent property — and not just a C# default.</para>
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration")]
public sealed class MasterDataStatusPreservationTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);
    private readonly PostgresFixture _fx;
    public MasterDataStatusPreservationTests(PostgresFixture fx) => _fx = fx;

    [Fact]
    public async Task EditingAnInactiveValueWithoutAStatus_KeepsItInactive_AndKeepsItsMetadata()
    {
        var (tenantId, type) = await SeedTypeAsync(isSystem: false);
        await using var db = _fx.CreateDb();
        var value = new MasterDataValue
        {
            TenantId = tenantId, TypeId = type.Id, Code = "RETIRED", ValueEn = "Retired", ValueAr = "",
            ExtraJson = "{\"legacy\":true}", SortOrder = 3, IsActive = false,
        };
        db.MasterDataValues.Add(value);
        await db.SaveChangesAsync();

        // Exactly what the value modal sends: no isActive, no extraJson.
        var request = Json<MasterDataValueRequest>("""{"code":"RETIRED","valueEn":"Retired (renamed)","valueAr":"","sortOrder":4,"isDefault":false}""");
        Assert.IsType<OkObjectResult>(await Controller(db, tenantId).UpdateValue(value.Id, request, CancellationToken.None));

        await using var verify = _fx.CreateDb();
        var stored = await verify.MasterDataValues.AsNoTracking().SingleAsync(x => x.Id == value.Id);
        Assert.Equal("Retired (renamed)", stored.ValueEn);
        Assert.Equal(4, stored.SortOrder);
        Assert.False(stored.IsActive, "an edit that did not mention status must not reactivate a retired value");
        Assert.Equal("{\"legacy\":true}", stored.ExtraJson);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task AnExplicitValueStatus_IsApplied_AndAudited(bool before, bool after)
    {
        var (tenantId, type) = await SeedTypeAsync(isSystem: false);
        await using var db = _fx.CreateDb();
        var value = new MasterDataValue { TenantId = tenantId, TypeId = type.Id, Code = "V", ValueEn = "V", IsActive = before };
        db.MasterDataValues.Add(value);
        await db.SaveChangesAsync();

        var request = Json<MasterDataValueRequest>($$"""{"code":"V","valueEn":"V","sortOrder":0,"isDefault":false,"isActive":{{(after ? "true" : "false")}}}""");
        Assert.IsType<OkObjectResult>(await Controller(db, tenantId).UpdateValue(value.Id, request, CancellationToken.None));

        await using var verify = _fx.CreateDb();
        Assert.Equal(after, (await verify.MasterDataValues.AsNoTracking().SingleAsync(x => x.Id == value.Id)).IsActive);
        var audit = await verify.AdminAuditLogs.AsNoTracking()
            .SingleAsync(x => x.TenantId == tenantId && x.EntityType == "MasterDataValue" && x.EntityId == value.Id.ToString());
        Assert.Equal("Updated", audit.Action);
        Assert.Contains($"\"IsActive\":{(before ? "true" : "false")}", audit.OldValuesJson);
        Assert.Contains($"\"IsActive\":{(after ? "true" : "false")}", audit.NewValuesJson);
    }

    [Fact]
    public async Task ATypeCanBeDeactivated_AndTheDecisionPersistsAndIsAudited()
    {
        var (tenantId, type) = await SeedTypeAsync(isSystem: false);
        await using var db = _fx.CreateDb();
        var request = Json<MasterDataTypeRequest>("""{"code":"CUSTOM","nameEn":"Custom list","nameAr":"","description":"","allowCustomValues":true,"isActive":false}""");
        Assert.IsType<OkObjectResult>(await Controller(db, tenantId).UpdateType(type.Id, request, CancellationToken.None));

        await using var verify = _fx.CreateDb();
        Assert.False((await verify.MasterDataTypes.AsNoTracking().SingleAsync(x => x.Id == type.Id)).IsActive);
        var audit = await verify.AdminAuditLogs.AsNoTracking()
            .SingleAsync(x => x.TenantId == tenantId && x.EntityType == "MasterDataType" && x.EntityId == type.Id.ToString());
        Assert.Contains("\"IsActive\":true", audit.OldValuesJson);
        Assert.Contains("\"IsActive\":false", audit.NewValuesJson);

        // Retired types leave the active list and stay in the full one, where they can be reactivated.
        var ctrl = Controller(verify, tenantId);
        var active = Assert.IsType<OkObjectResult>(await ctrl.ListTypes(true, CancellationToken.None)).Value as IEnumerable<MasterDataType>;
        Assert.DoesNotContain(active!, x => x.Id == type.Id);
        var all = Assert.IsType<OkObjectResult>(await ctrl.ListTypes(false, CancellationToken.None)).Value as IEnumerable<MasterDataType>;
        Assert.Contains(all!, x => x.Id == type.Id);

        // A later rename without a status keeps it retired.
        await using var again = _fx.CreateDb();
        var rename = Json<MasterDataTypeRequest>("""{"code":"CUSTOM","nameEn":"Custom list (renamed)","allowCustomValues":true}""");
        Assert.IsType<OkObjectResult>(await Controller(again, tenantId).UpdateType(type.Id, rename, CancellationToken.None));
        await using var final = _fx.CreateDb();
        var stored = await final.MasterDataTypes.AsNoTracking().SingleAsync(x => x.Id == type.Id);
        Assert.Equal("Custom list (renamed)", stored.NameEn);
        Assert.False(stored.IsActive);
    }

    [Fact]
    public async Task ASystemDefinedType_CannotBeDeactivated()
    {
        var (tenantId, type) = await SeedTypeAsync(isSystem: true);
        await using var db = _fx.CreateDb();
        var request = Json<MasterDataTypeRequest>("""{"code":"SYS","nameEn":"System list","allowCustomValues":true,"isActive":false}""");
        Assert.IsType<BadRequestObjectResult>(await Controller(db, tenantId).UpdateType(type.Id, request, CancellationToken.None));

        await using var verify = _fx.CreateDb();
        Assert.True((await verify.MasterDataTypes.AsNoTracking().SingleAsync(x => x.Id == type.Id)).IsActive);
    }

    private async Task<(Guid TenantId, MasterDataType Type)> SeedTypeAsync(bool isSystem)
    {
        await using var db = _fx.CreateDb();
        var tenantId = await PostgresFixture.SeedMinimalTenant(db);
        var type = new MasterDataType
        {
            TenantId = tenantId, Code = isSystem ? "SYS" : "CUSTOM", NameEn = isSystem ? "System list" : "Custom list",
            IsSystemDefined = isSystem, AllowCustomValues = true, IsActive = true,
        };
        db.MasterDataTypes.Add(type);
        await db.SaveChangesAsync();
        return (tenantId, type);
    }

    private static T Json<T>(string json) => JsonSerializer.Deserialize<T>(json, Web)!;

    private static MasterDataController Controller(ZayraDbContext db, Guid tenantId) => new(db)
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
                    new Claim("permission", "organization.write"),
                    new Claim("entity_scope", "{\"v\":2,\"m\":\"group\",\"c\":[]}"),
                }, "Test")),
            },
        },
    };
}
