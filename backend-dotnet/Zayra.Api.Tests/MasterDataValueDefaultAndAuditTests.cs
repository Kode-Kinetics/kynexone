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
/// Employee Reference Lists (Setup → Organization and core HR). Employee forms prefill the FIRST value
/// flagged default, so saving a second default left the old one winning: an admin who switched Marital
/// Status' default from Single to Married still got Single on every new employee. Value create, edit and
/// delete also wrote no audit row, while the type endpoints beside them did.
/// </summary>
public class MasterDataValueDefaultAndAuditTests
{
    [Fact]
    public async Task Creating_a_default_value_clears_the_previous_default()
    {
        var (db, tenantId, type, single) = await Seed();
        var controller = For(db, tenantId);

        var result = await controller.CreateValue(type.Id, new MasterDataValueRequest("MARRIED", "Married", "متزوج", null, 2, IsDefault: true), CancellationToken.None);

        result.Should().BeOfType<OkObjectResult>();
        var defaults = await db.MasterDataValues.Where(v => v.TypeId == type.Id && v.IsDefault).Select(v => v.Code).ToListAsync();
        defaults.Should().Equal("MARRIED");
        (await db.MasterDataValues.SingleAsync(v => v.Id == single.Id)).UpdatedBy.Should().NotBeNull();
    }

    [Fact]
    public async Task Editing_a_value_into_the_default_clears_the_previous_default()
    {
        var (db, tenantId, type, _) = await Seed();
        var married = new MasterDataValue { TenantId = tenantId, TypeId = type.Id, Code = "MARRIED", ValueEn = "Married", SortOrder = 2 };
        db.MasterDataValues.Add(married);
        await db.SaveChangesAsync();

        await For(db, tenantId).UpdateValue(married.Id, new MasterDataValueRequest("MARRIED", "Married", null, null, 2, IsDefault: true), CancellationToken.None);

        var defaults = await db.MasterDataValues.Where(v => v.TypeId == type.Id && v.IsDefault).Select(v => v.Code).ToListAsync();
        defaults.Should().Equal("MARRIED");
    }

    [Fact]
    public async Task Another_tenants_default_is_never_touched()
    {
        var (db, tenantId, type, _) = await Seed();
        var otherTenant = Guid.NewGuid();
        var foreign = new MasterDataValue { TenantId = otherTenant, TypeId = type.Id, Code = "SINGLE", ValueEn = "Single", IsDefault = true };
        db.MasterDataValues.Add(foreign);
        await db.SaveChangesAsync();

        await For(db, tenantId).CreateValue(type.Id, new MasterDataValueRequest("MARRIED", "Married", null, null, 2, IsDefault: true), CancellationToken.None);

        (await db.MasterDataValues.SingleAsync(v => v.Id == foreign.Id)).IsDefault.Should().BeTrue();
    }

    [Fact]
    public async Task Every_value_write_leaves_an_audit_row()
    {
        var (db, tenantId, type, _) = await Seed();
        var controller = For(db, tenantId);

        var created = (MasterDataValue)((OkObjectResult)await controller.CreateValue(type.Id, new MasterDataValueRequest("SEPARATED", "Separated", null, null, 5, IsDefault: false), CancellationToken.None)).Value!;
        await controller.UpdateValue(created.Id, new MasterDataValueRequest("SEPARATED", "Separated (legal)", null, null, 5, IsDefault: false), CancellationToken.None);
        (await controller.DeleteValue(created.Id, CancellationToken.None)).Should().BeOfType<NoContentResult>();

        var audit = await db.AdminAuditLogs.Where(a => a.EntityType == "MasterDataValue" && a.EntityId == created.Id.ToString()).OrderBy(a => a.CreatedAtUtc).ToListAsync();
        audit.Select(a => a.Action).Should().Equal("Created", "Updated", "Deleted");
        audit.Should().OnlyContain(a => a.TenantId == tenantId && a.PerformedBy != null);
        audit[1].OldValuesJson.Should().Contain("\"Separated\"");
        audit[1].NewValuesJson.Should().Contain("Separated (legal)");
        (await db.MasterDataValues.IgnoreQueryFilters().SingleAsync(v => v.Id == created.Id)).UpdatedBy.Should().NotBeNull();
    }

    private static async Task<(ZayraDbContext Db, Guid TenantId, MasterDataType Type, MasterDataValue Single)> Seed()
    {
        var db = new ZayraDbContext(new DbContextOptionsBuilder<ZayraDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var tenantId = Guid.NewGuid();
        var type = new MasterDataType { TenantId = tenantId, Code = "MaritalStatus", NameEn = "Marital Status" };
        var single = new MasterDataValue { TenantId = tenantId, TypeId = type.Id, Code = "SINGLE", ValueEn = "Single", SortOrder = 1, IsDefault = true };
        db.MasterDataTypes.Add(type);
        db.MasterDataValues.Add(single);
        await db.SaveChangesAsync();
        return (db, tenantId, type, single);
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
