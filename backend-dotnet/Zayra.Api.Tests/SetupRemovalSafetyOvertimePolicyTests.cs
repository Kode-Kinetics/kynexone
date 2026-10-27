using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Common;
using Zayra.Api.Controllers;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Audit;
using Zayra.Api.Infrastructure.Organization;
using Zayra.Api.Infrastructure.Payroll;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// Overtime policies: edit, turn off/on, remove-only-if-unused, an audit entry per change, and a
/// deterministic "default" policy when several are active.
/// </summary>
public class SetupRemovalSafetyOvertimePolicyTests
{
    private static ZayraDbContext Db() => new(new DbContextOptionsBuilder<ZayraDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static OvertimeController Ctl(ZayraDbContext db, Guid tenant, bool group = true)
    {
        var c = new OvertimeController(db, new AllScope(), new HrmHierarchyService(db, new AuditService(db)));
        c.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(new[]
                {
                    new Claim("tenant_id", tenant.ToString()),
                    new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
                    new Claim(ClaimTypes.Role, "Admin"),
                    new Claim("permission", "overtime.write"),
                    group ? new Claim("is_group_scope", "true")
                          : new Claim("entity_access", System.Text.Json.JsonSerializer.Serialize(new { c = Guid.NewGuid(), r = "HR Manager" })),
                }, "Test")),
            },
        };
        return c;
    }

    private sealed class AllScope : IDataScopeService
    {
        public Task<DataScope> ResolveAsync(ClaimsPrincipal caller, Guid tenantId, CancellationToken ct)
            => Task.FromResult(new DataScope { Level = DataScopeLevel.Organization });
    }

    private static OvertimePolicy Policy(Guid t, string code, DateTime? created = null, bool active = true, Guid? branch = null) => new()
    {
        TenantId = t, Code = code, Name = code, IsActive = active, BranchId = branch,
        CreatedAtUtc = created ?? DateTime.UtcNow,
    };

    private static OvertimePolicyRequest Req(string code = "STD", string name = "Standard", decimal fixedRate = 0m, decimal reg = 1.25m) =>
        new(code, name, "BasicSalary", fixedRate, 240, 30, 240, 3600, "Nearest15", true, true, reg, 1.5m, 2m);

    private static string? Code(object? r) => r is ConflictObjectResult c
        ? c.Value!.GetType().GetProperty("code")!.GetValue(c.Value)?.ToString() : null;

    // ── default resolution ───────────────────────────────────────────────────

    [Fact]
    public async Task DefaultPolicy_IsOldestActive_AsPayrollAlwaysPicked_EvenWhenOlderIsScoped()
    {
        await using var db = Db(); var t = Guid.NewGuid();
        var oldest = Policy(t, "A-OLD", new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var newer = Policy(t, "B-NEW", new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var scoped = Policy(t, "C-BRANCH", new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc), branch: Guid.NewGuid());
        var off = Policy(t, "D-OFF", new DateTime(2023, 1, 1, 0, 0, 0, DateTimeKind.Utc), active: false);
        db.OvertimePolicies.AddRange(newer, scoped, off, oldest); await db.SaveChangesAsync();

        var picked = await OvertimePolicyDefault.Order(db.OvertimePolicies.Where(p => p.TenantId == t && p.IsActive && !p.IsDeleted)).FirstAsync();

        // The legacy payroll query: oldest active policy, scope ignored. Money must not move for existing tenants.
        var legacy = await db.OvertimePolicies.Where(p => p.TenantId == t && p.IsActive && !p.IsDeleted).OrderBy(p => p.CreatedAtUtc).FirstAsync();
        picked.Id.Should().Be(legacy.Id);
        picked.Code.Should().Be("C-BRANCH");
    }

    [Fact]
    public async Task DefaultPolicy_TiesBreakByCodeThenId()
    {
        await using var db = Db(); var t = Guid.NewGuid(); var when = DateTime.UtcNow;
        db.OvertimePolicies.AddRange(Policy(t, "ZZ", when), Policy(t, "AA", when)); await db.SaveChangesAsync();

        (await OvertimePolicyDefault.Order(db.OvertimePolicies.Where(p => p.TenantId == t)).FirstAsync()).Code.Should().Be("AA");
    }

    // ── edit ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Update_ChangesFields_AndMultipliers_AndAudits()
    {
        await using var db = Db(); var t = Guid.NewGuid();
        var p = Policy(t, "STD"); db.OvertimePolicies.Add(p);
        db.OvertimeMultipliers.Add(new OvertimeMultiplier { TenantId = t, OvertimePolicyId = p.Id, DayCategory = "RegularDay", Multiplier = 1.25m });
        await db.SaveChangesAsync();

        var r = await Ctl(db, t).UpdatePolicy(p.Id, Req(name: "Renamed", reg: 1.5m, fixedRate: 20m) with { Code = "IGNORED" }, default);

        r.Result.Should().BeOfType<OkObjectResult>();
        var saved = await db.OvertimePolicies.SingleAsync();
        saved.Name.Should().Be("Renamed"); saved.FixedHourlyRate.Should().Be(20m);
        saved.Code.Should().Be("STD", "the code is the policy's identity and does not change");
        (await db.OvertimeMultipliers.SingleAsync(m => m.DayCategory == "RegularDay")).Multiplier.Should().Be(1.5m);
        var audit = await db.OvertimeAuditLogs.SingleAsync();
        audit.Action.Should().Be("overtime.policy.updated"); audit.MetadataJson.Should().Contain("Renamed").And.Contain("STD");
    }

    [Fact]
    public async Task Update_OtherTenantsPolicy_IsNotFound()
    {
        await using var db = Db(); var t = Guid.NewGuid();
        var p = Policy(Guid.NewGuid(), "STD"); db.OvertimePolicies.Add(p); await db.SaveChangesAsync();

        (await Ctl(db, t).UpdatePolicy(p.Id, Req(), default)).Result.Should().BeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task Update_WithAMissingMultiplier_IsRejected_NothingChanges()
    {
        await using var db = Db(); var t = Guid.NewGuid();
        var p = Policy(t, "STD"); db.OvertimePolicies.Add(p); await db.SaveChangesAsync();

        var r = await Ctl(db, t).UpdatePolicy(p.Id, Req(name: "X") with { WeekendMultiplier = 0m }, default);

        r.Result.Should().BeOfType<BadRequestObjectResult>();
        (await db.OvertimePolicies.SingleAsync()).Name.Should().Be("STD");
    }

    [Fact]
    public async Task GetPolicy_ReturnsEditableFieldsAndSavedMultipliers()
    {
        await using var db = Db(); var t = Guid.NewGuid();
        var p = Policy(t, "STD"); db.OvertimePolicies.Add(p);
        db.OvertimeMultipliers.AddRange(
            new OvertimeMultiplier { TenantId = t, OvertimePolicyId = p.Id, DayCategory = "RegularDay", Multiplier = 1.3m },
            new OvertimeMultiplier { TenantId = t, OvertimePolicyId = p.Id, DayCategory = "Weekend", Multiplier = 1.6m },
            new OvertimeMultiplier { TenantId = t, OvertimePolicyId = p.Id, DayCategory = "PublicHoliday", Multiplier = 2.1m });
        await db.SaveChangesAsync();

        var ok = (OkObjectResult)(await Ctl(db, t).GetPolicy(p.Id, default));
        object V(string n) => ok.Value!.GetType().GetProperty(n)!.GetValue(ok.Value)!;
        V("regularDayMultiplier").Should().Be(1.3m); V("weekendMultiplier").Should().Be(1.6m); V("holidayMultiplier").Should().Be(2.1m);
        V("Code").Should().Be("STD"); V("MonthlyCapMinutes").Should().Be(3600);
    }

    [Fact]
    public async Task GetPolicy_OtherTenant_IsNotFound()
    {
        await using var db = Db(); var t = Guid.NewGuid();
        var p = Policy(Guid.NewGuid(), "STD"); db.OvertimePolicies.Add(p); await db.SaveChangesAsync();
        (await Ctl(db, t).GetPolicy(p.Id, default)).Should().BeOfType<NotFoundResult>();
    }

    // ── turn off / on ────────────────────────────────────────────────────────

    [Fact]
    public async Task TurnOffAndOn_Works_AndAudits()
    {
        await using var db = Db(); var t = Guid.NewGuid();
        var a = Policy(t, "A"); var b = Policy(t, "B"); db.OvertimePolicies.AddRange(a, b); await db.SaveChangesAsync();
        var c = Ctl(db, t);

        (await c.SetPolicyActive(a.Id, false, default)).Result.Should().BeOfType<OkObjectResult>();
        (await db.OvertimePolicies.SingleAsync(x => x.Id == a.Id)).IsActive.Should().BeFalse();
        (await c.SetPolicyActive(a.Id, true, default)).Result.Should().BeOfType<OkObjectResult>();
        (await db.OvertimePolicies.SingleAsync(x => x.Id == a.Id)).IsActive.Should().BeTrue();
        (await db.OvertimeAuditLogs.CountAsync()).Should().Be(2);
    }

    [Fact]
    public async Task TurnOff_LastActivePolicy_WithOpenRequests_IsRefused()
    {
        await using var db = Db(); var t = Guid.NewGuid();
        var a = Policy(t, "A"); db.OvertimePolicies.Add(a);
        await db.SaveChangesAsync();

        var r = await Ctl(db, t).SetPolicyActive(a.Id, false, default);

        Code(r.Result).Should().Be("OVERTIME_POLICY_LAST_ACTIVE", "even with no open requests, payroll would fall back to built-in defaults");
        (await db.OvertimePolicies.SingleAsync()).IsActive.Should().BeTrue();
    }

    [Fact]
    public async Task Delete_LastActivePolicy_IsRefused()
    {
        await using var db = Db(); var t = Guid.NewGuid();
        var a = Policy(t, "A"); db.OvertimePolicies.Add(a); await db.SaveChangesAsync();

        Code(await Ctl(db, t).DeletePolicy(a.Id, default)).Should().Be("OVERTIME_POLICY_LAST_ACTIVE");
        (await db.OvertimePolicies.SingleAsync()).IsDeleted.Should().BeFalse();
    }

    [Fact]
    public async Task NonGroupCaller_IsForbidden_OnEveryOvertimePolicyAndTypeWrite()
    {
        await using var db = Db(); var t = Guid.NewGuid();
        var a = Policy(t, "A"); var b = Policy(t, "B"); var ty = new OvertimeType { TenantId = t, Code = "R", Name = "R" };
        db.OvertimePolicies.AddRange(a, b); db.OvertimeTypes.Add(ty); await db.SaveChangesAsync();
        var c = Ctl(db, t, group: false);

        (await c.CreatePolicy(Req("NEW"), default)).Result.Should().BeOfType<ForbidResult>();
        (await c.UpdatePolicy(a.Id, Req(), default)).Result.Should().BeOfType<ForbidResult>();
        (await c.SetPolicyActive(a.Id, false, default)).Result.Should().BeOfType<ForbidResult>();
        (await c.DeletePolicy(b.Id, default)).Should().BeOfType<ForbidResult>();
        (await c.CreateType(new OvertimeTypeRequest("X", "X", null), default)).Result.Should().BeOfType<ForbidResult>();
        (await c.UpdateType(ty.Id, new OvertimeTypeRequest("R", "Y", null), default)).Result.Should().BeOfType<ForbidResult>();
        (await c.SetTypeActive(ty.Id, false, default)).Result.Should().BeOfType<ForbidResult>();
        (await db.OvertimePolicies.CountAsync(p => p.IsActive && !p.IsDeleted)).Should().Be(2);
    }

    [Theory]
    [InlineData("regular0")]
    [InlineData("huge")]
    [InlineData("negFixed")]
    [InlineData("hours10")]
    [InlineData("basis")]
    public async Task Update_OutOfRangeFigures_AreRejected(string which)
    {
        await using var db = Db(); var t = Guid.NewGuid();
        var p = Policy(t, "STD"); db.OvertimePolicies.Add(p); await db.SaveChangesAsync();
        var r = Req();
        r = which switch
        {
            "regular0" => r with { RegularDayMultiplier = 0m },
            "huge" => r with { WeekendMultiplier = 50m },
            "negFixed" => r with { FixedHourlyRate = -1m },
            "hours10" => r with { StandardMonthlyHours = 10 },
            _ => r with { HourlyRateBasis = "Whatever" },
        };

        (await Ctl(db, t).UpdatePolicy(p.Id, r, default)).Result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task Update_RateFigures_AfterACalculationExists_AreRefused_ButNameAndLimitsAreNot()
    {
        await using var db = Db(); var t = Guid.NewGuid();
        var p = Policy(t, "STD"); db.OvertimePolicies.Add(p);
        db.OvertimeMultipliers.AddRange(
            new OvertimeMultiplier { TenantId = t, OvertimePolicyId = p.Id, DayCategory = "RegularDay", Multiplier = 1.25m },
            new OvertimeMultiplier { TenantId = t, OvertimePolicyId = p.Id, DayCategory = "Weekend", Multiplier = 1.5m },
            new OvertimeMultiplier { TenantId = t, OvertimePolicyId = p.Id, DayCategory = "PublicHoliday", Multiplier = 2m });
        var req = new OvertimeRequest { TenantId = t, EmployeeId = 1, OvertimePolicyId = p.Id, Status = "Approved" };
        db.OvertimeRequests.Add(req);
        db.OvertimeCalculations.Add(new OvertimeCalculation { TenantId = t, OvertimeRequestId = req.Id, EmployeeId = 1 });
        await db.SaveChangesAsync();
        var c = Ctl(db, t);

        Code((await c.UpdatePolicy(p.Id, Req(reg: 1.4m), default)).Result).Should().Be("OVERTIME_POLICY_USED_BY_PAYROLL");
        (await c.UpdatePolicy(p.Id, Req(name: "Renamed"), default)).Result.Should().BeOfType<OkObjectResult>();
    }

    // ── remove ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task Delete_PolicyUsedByRequests_IsRefused_PointingToTurnOff()
    {
        await using var db = Db(); var t = Guid.NewGuid();
        var a = Policy(t, "A"); db.OvertimePolicies.AddRange(a, Policy(t, "B"));
        db.OvertimeRequests.Add(new OvertimeRequest { TenantId = t, EmployeeId = 1, OvertimePolicyId = a.Id, Status = "Approved" });
        await db.SaveChangesAsync();

        var r = await Ctl(db, t).DeletePolicy(a.Id, default);

        Code(r).Should().Be("OVERTIME_POLICY_IN_USE");
        ((ConflictObjectResult)r).Value!.ToString().Should().Contain("turn");
        (await db.OvertimePolicies.IgnoreQueryFilters().SingleAsync(x => x.Code == "A")).IsDeleted.Should().BeFalse();
    }

    [Fact]
    public async Task Delete_UnusedPolicy_Removes_AndAudits()
    {
        await using var db = Db(); var t = Guid.NewGuid();
        var a = Policy(t, "A"); db.OvertimePolicies.AddRange(a, Policy(t, "B")); await db.SaveChangesAsync();

        (await Ctl(db, t).DeletePolicy(a.Id, default)).Should().BeOfType<NoContentResult>();

        (await db.OvertimePolicies.IgnoreQueryFilters().SingleAsync(x => x.Code == "A")).IsDeleted.Should().BeTrue();
        (await db.OvertimeAuditLogs.SingleAsync()).Action.Should().Be("overtime.policy.deleted");
    }

    // ── types ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Types_EditTurnOffOn_AndListIncludingInactive()
    {
        await using var db = Db(); var t = Guid.NewGuid();
        var ty = new OvertimeType { TenantId = t, Code = "REG", Name = "Regular" }; db.OvertimeTypes.Add(ty); await db.SaveChangesAsync();
        var c = Ctl(db, t);

        (await c.UpdateType(ty.Id, new OvertimeTypeRequest("REG", "Regular day", "Regular"), default)).Result.Should().BeOfType<OkObjectResult>();
        (await c.SetTypeActive(ty.Id, false, default)).Result.Should().BeOfType<OkObjectResult>();

        ((await c.Types(false, default)).Result as OkObjectResult)!.Value.As<IEnumerable<OvertimeType>>().Should().BeEmpty();
        ((await c.Types(true, default)).Result as OkObjectResult)!.Value.As<IEnumerable<OvertimeType>>().Should().ContainSingle()
            .Which.Name.Should().Be("Regular day");
        (await c.SetTypeActive(ty.Id, true, default)).Result.Should().BeOfType<OkObjectResult>();
        (await db.OvertimeAuditLogs.CountAsync()).Should().Be(3);
    }
}
