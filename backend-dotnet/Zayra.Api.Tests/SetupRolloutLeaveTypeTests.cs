using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Controllers.Leave;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Approvals;
using Zayra.Api.Infrastructure.Leave;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>Leave types follow Setup's one removal rule: unused = deleted (code freed), used = turned off and restorable,
/// a statutory type needs an explicit acknowledgement before it is turned off.</summary>
public class SetupRolloutLeaveTypeTests
{
    private static ZayraDbContext Db() => new(new DbContextOptionsBuilder<ZayraDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static LeaveTypesController Ctl(ZayraDbContext db, Guid tenant)
    {
        var c = new LeaveTypesController(db, new LeaveService(db, new ApprovalRouter(db)));
        c.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(new[]
                {
                    new Claim("tenant_id", tenant.ToString()),
                    new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
                    new Claim(ClaimTypes.Role, "Admin"),
                    new Claim("is_group_scope", "true"),
                }, "Test")),
            },
        };
        return c;
    }

    private static LeaveType Type(Guid t, string code, string name = "Training leave", string category = "General") =>
        new() { TenantId = t, Code = code, NameEn = name, Category = category, IsActive = true };

    private static JsonElement Json(IActionResult r) => JsonSerializer.SerializeToElement(((ObjectResult)r).Value);

    [Fact]
    public async Task Unused_type_is_deleted_and_its_code_is_free()
    {
        var db = Db(); var tenant = Guid.NewGuid();
        var lt = Type(tenant, "TRN"); db.LeaveTypes.Add(lt); await db.SaveChangesAsync();

        var r = await Ctl(db, tenant).Delete(lt.Id, null, default);

        Json(r).GetProperty("outcome").GetString().Should().Be("deleted");
        (await db.LeaveTypes.AnyAsync(x => x.Code == "TRN")).Should().BeFalse();
    }

    [Fact]
    public async Task Type_with_a_request_is_turned_off_with_a_reason_and_can_be_restored()
    {
        var db = Db(); var tenant = Guid.NewGuid();
        var lt = Type(tenant, "TRN"); db.LeaveTypes.Add(lt);
        db.LeaveRequests.Add(new LeaveRequest { TenantId = tenant, LeaveTypeId = lt.Id });
        await db.SaveChangesAsync();
        var ctl = Ctl(db, tenant);

        var r = await ctl.Delete(lt.Id, null, default);

        var body = Json(r);
        body.GetProperty("outcome").GetString().Should().Be("turnedOff");
        body.GetProperty("usage").GetProperty("summary").GetString().Should().Contain("1 leave requests");
        (await db.LeaveTypes.SingleAsync(x => x.Id == lt.Id)).IsActive.Should().BeFalse();

        var restored = await ctl.Restore(lt.Id, default);
        Json(restored).GetProperty("outcome").GetString().Should().Be("restored");
        (await db.LeaveTypes.SingleAsync(x => x.Id == lt.Id)).IsActive.Should().BeTrue();
    }

    [Fact]
    public async Task Statutory_type_is_refused_until_the_floor_warning_is_acknowledged()
    {
        var db = Db(); var tenant = Guid.NewGuid();
        var lt = Type(tenant, "MATERNITY", "Maternity leave", "Maternity"); db.LeaveTypes.Add(lt); await db.SaveChangesAsync();
        var ctl = Ctl(db, tenant);

        var refused = await ctl.Delete(lt.Id, null, default);
        refused.Should().BeOfType<ConflictObjectResult>();
        Json(refused).GetProperty("code").GetString().Should().Be("LEAVE_TYPE_STATUTORY");
        (await db.LeaveTypes.AnyAsync(x => x.Id == lt.Id)).Should().BeTrue();

        var ok = await ctl.Delete(lt.Id, "LEAVE_TYPE_STATUTORY", default);
        ok.Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task Edit_cannot_flip_on_or_off()
    {
        var db = Db(); var tenant = Guid.NewGuid();
        var lt = Type(tenant, "TRN"); db.LeaveTypes.Add(lt); await db.SaveChangesAsync();
        await Ctl(db, tenant).Update(lt.Id, new UpdateLeaveTypeRequest(null, null, null, null, null, null, null, null, null, null, null, false), default);
        (await db.LeaveTypes.SingleAsync(x => x.Id == lt.Id)).IsActive.Should().BeTrue();
    }
}
