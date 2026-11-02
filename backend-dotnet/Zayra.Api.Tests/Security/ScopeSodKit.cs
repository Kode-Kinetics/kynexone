using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Common;
using Zayra.Api.Data;
using Zayra.Api.Models;

namespace Zayra.Api.Tests.Security;

/// <summary>
/// Shared in-memory harness for the company-scope and separation-of-duties tests: one tenant with two
/// companies, an employee in each, and principals scoped to company A, company B, or the whole group.
/// The controller and its DbContext share ONE principal, so the central read filter and write guard
/// see exactly what the controller sees.
/// </summary>
internal sealed class ScopeSodWorld
{
    public Guid Tenant { get; } = Guid.NewGuid();
    public Guid CompanyA { get; } = Guid.NewGuid();
    public Guid CompanyB { get; } = Guid.NewGuid();
    public string Store { get; } = Guid.NewGuid().ToString();
    public int EmpA { get; private set; }
    public int EmpB { get; private set; }
    public Guid UserA { get; } = Guid.NewGuid();
    public Guid UserB { get; } = Guid.NewGuid();

    /// <summary>Two group-scope Finance logins (payroll.approve) that never sign in during a test: they are who the up-front
    /// "is there anyone who could approve this" check finds. A test of that check creates the world without them.</summary>
    public Guid FinanceOne { get; } = Guid.NewGuid();
    public Guid FinanceTwo { get; } = Guid.NewGuid();

    public static async Task<ScopeSodWorld> CreateAsync(bool withFinanceApprovers = true)
    {
        var w = new ScopeSodWorld();
        await using var db = w.Plain();
        db.Companies.AddRange(
            new Company { Id = w.CompanyA, TenantId = w.Tenant, LegalNameEn = "A Co", RegistrationNumber = "RA-" + w.CompanyA.ToString("N")[..8], IsActive = true },
            new Company { Id = w.CompanyB, TenantId = w.Tenant, LegalNameEn = "B Co", RegistrationNumber = "RB-" + w.CompanyB.ToString("N")[..8], IsActive = true });
        var a = new Employee { TenantId = w.Tenant, CompanyId = w.CompanyA, EmployeeCode = "A-001", FullName = "Alpha Emp", Status = "Active", JoiningDate = DateTime.UtcNow.AddYears(-2), UserAccountId = w.UserA, Salary = 5000m };
        var b = new Employee { TenantId = w.Tenant, CompanyId = w.CompanyB, EmployeeCode = "B-001", FullName = "Beta Emp", Status = "Active", JoiningDate = DateTime.UtcNow.AddYears(-2), UserAccountId = w.UserB, Salary = 9000m };
        db.Employees.AddRange(a, b);
        await db.SaveChangesAsync();
        w.EmpA = a.Id; w.EmpB = b.Id;
        if (withFinanceApprovers)
            foreach (var id in new[] { w.FinanceOne, w.FinanceTwo })
            {
                db.Users.Add(new Zayra.Api.Domain.Entities.User { Id = id, TenantId = w.Tenant, Email = $"fin-{id:N}@test.local", NormalizedEmail = $"FIN-{id:N}@TEST.LOCAL", FullName = "Finance", IsGroupScope = true });
                foreach (var permission in new[] { "payroll.approve", "leave.approve", "leave.write" })
                    db.UserPermissionOverrides.Add(new UserPermissionOverride { TenantId = w.Tenant, UserId = id, PermissionKey = permission, Effect = "Allow" });
            }
        await db.SaveChangesAsync();
        return w;
    }

    /// <summary>Unscoped context (system-trusted) used for seeding and for reading back.</summary>
    public ZayraDbContext Plain() =>
        new(new DbContextOptionsBuilder<ZayraDbContext>().UseInMemoryDatabase(Store).Options);

    public ClaimsPrincipal CompanyAdmin(Guid companyId, Guid? userId = null, params string[] permissions) =>
        Principal(userId, "companies", new[] { companyId }, permissions);

    /// <summary>Scoped to BOTH companies without group scope: a new config row with no company is ambiguous for this caller.</summary>
    public ClaimsPrincipal TwoCompanyAdmin(Guid? userId = null, params string[] permissions) =>
        Principal(userId, "companies", new[] { CompanyA, CompanyB }, permissions);

    public ClaimsPrincipal GroupAdmin(Guid? userId = null, params string[] permissions) =>
        Principal(userId, "group", Array.Empty<Guid>(), permissions);

    public ClaimsPrincipal ForeignTenantAdmin(params string[] permissions) =>
        new(new ClaimsIdentity(Claims(Guid.NewGuid(), Guid.NewGuid(), "group", Array.Empty<Guid>(), permissions), "Test"));

    private ClaimsPrincipal Principal(Guid? userId, string mode, Guid[] companies, string[] permissions) =>
        new(new ClaimsIdentity(Claims(Tenant, userId ?? Guid.NewGuid(), mode, companies, permissions), "Test"));

    private static List<Claim> Claims(Guid tenant, Guid user, string mode, Guid[] companies, string[] permissions)
    {
        var c = new List<Claim>
        {
            new("tenant_id", tenant.ToString()),
            new(ClaimTypes.NameIdentifier, user.ToString()),
            new(ClaimTypes.Name, "Tester"),
            new(ClaimTypes.Role, "Admin"),
            new(EntityScopeContext.V2ClaimType, JsonSerializer.Serialize(new { v = 2, m = mode, c = companies })),
        };
        // An org-wide HR administrator by default; tests that need other permissions add them.
        c.AddRange(permissions.Append("employees.write").Distinct().Select(p => new Claim("permission", p)));
        return c;
    }

    public ZayraDbContext DbFor(ClaimsPrincipal principal) =>
        new(new DbContextOptionsBuilder<ZayraDbContext>().UseInMemoryDatabase(Store).Options,
            new FixedAccessor(new DefaultHttpContext { User = principal }));

    public static T Wire<T>(T controller, ClaimsPrincipal principal) where T : ControllerBase
    {
        controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = principal } };
        return controller;
    }

    private sealed class FixedAccessor(HttpContext ctx) : IHttpContextAccessor
    {
        public HttpContext? HttpContext { get; set; } = ctx;
    }
}
