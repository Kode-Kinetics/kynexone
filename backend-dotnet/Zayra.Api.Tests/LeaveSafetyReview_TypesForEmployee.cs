using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Auth;
using Zayra.Api.Controllers.Leave;
using Zayra.Api.Infrastructure.Audit;
using Zayra.Api.Infrastructure.Documents;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>Review follow-up: TypesForEmployee. See <see cref="LeaveSafetyReviewTests"/>.</summary>
public sealed partial class LeaveSafetyReviewTests
{

    private static ClaimsPrincipal Caller(LeaveBatch1World w, string role, int? employeeId, params string[] permissions)
    {
        var claims = new List<Claim>
        {
            new("tenant_id", w.TenantId.ToString()), new(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
            new(ClaimTypes.Name, "Test User"), new(ClaimTypes.Role, role),
        };
        if (employeeId is int id) claims.Add(new Claim("employee_id", id.ToString()));
        claims.AddRange(permissions.Select(p => new Claim("permission", p)));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "Test"));
    }

    // ── 1. Leave types for a named employee ───────────────────────────────────────────────────────

    private static LeaveTypesController Types(LeaveBatch1World w, ClaimsPrincipal user, Zayra.Api.Application.Common.DataScope scope) =>
        new(w.Db, w.Service, new LeaveBatch1World.FixedScope(scope))
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = user } },
        };

    [Fact]
    public async Task A_plain_employee_asking_for_a_colleagues_leave_types_is_refused()
    {
        var w = await LeaveBatch1World.CreateAsync();
        var c = Types(w, Caller(w, "Employee", w.Other.Id, "ess.read"), w.OwnScope(w.Other));

        (await c.List(null, null, null, w.Emp.Id, CancellationToken.None)).Should().BeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task An_employee_asking_for_their_own_leave_types_and_hr_asking_for_anyones_are_answered()
    {
        var w = await LeaveBatch1World.CreateAsync();

        (await Types(w, Caller(w, "Employee", w.Emp.Id, "ess.read"), w.OwnScope(w.Emp)).List(null, null, null, w.Emp.Id, CancellationToken.None))
            .Should().BeOfType<OkObjectResult>();
        (await Types(w, Caller(w, "HR Manager", null, "leave.read", "leave.write"), LeaveBatch1World.OrgScope()).List(null, null, null, w.Emp.Id, CancellationToken.None))
            .Should().BeOfType<OkObjectResult>();
    }
}
