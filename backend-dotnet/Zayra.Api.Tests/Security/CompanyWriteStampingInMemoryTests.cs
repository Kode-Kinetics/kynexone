using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Common;
using Zayra.Api.Data;
using Zayra.Api.Models;

namespace Zayra.Api.Tests.Security;

/// <summary>
/// Closes the Phase 2 test blind spot: most InMemory harnesses construct ZayraDbContext
/// WITHOUT an IHttpContextAccessor, which the write-stamping guard correctly treats as a
/// trusted system context — so those tests never exercise the guard. This suite proves
/// the guard is provider-agnostic: wire an accessor into an InMemory context (pattern
/// below) and enforcement activates exactly as it does on Postgres.
///
/// CONTRIBUTOR NOTE: if your test creates operational rows through a controller with an
/// HttpContext, pass the SAME principal to the DbContext via an accessor (as done here),
/// or your test is running in system context and asserts nothing about company scope.
/// </summary>
public class CompanyWriteStampingInMemoryTests
{
    [Fact]
    public async Task InMemoryContext_WithAccessor_EnforcesCompanyScopeOnWrites()
    {
        var tenantId = Guid.NewGuid();
        var dbName = Guid.NewGuid().ToString();

        // Seed in system context (no accessor) — trusted, like seeders/backfill.
        await using (var seed = NewDb(dbName))
        {
            seed.Tenants.Add(new Zayra.Api.Domain.Entities.Tenant { Id = tenantId, Name = "IM", Slug = $"im-{Guid.NewGuid():N}"[..20] });
            seed.Companies.AddRange(
                new Company { Id = CompanyA, TenantId = tenantId, LegalNameEn = "IM A", IsActive = true },
                new Company { Id = CompanyB, TenantId = tenantId, LegalNameEn = "IM B", IsActive = true });
            await seed.SaveChangesAsync();
        }

        var accessor = new FixedAccessor
        {
            HttpContext = new DefaultHttpContext { User = ScopedPrincipal(tenantId, CompanyA) },
        };
        await using var db = NewDb(dbName, accessor);

        // 1. Cross-company write is blocked — the guard runs on InMemory too.
        db.LeaveRequests.Add(new LeaveRequest
        {
            TenantId = tenantId, CompanyId = CompanyB, EmployeeId = 42,
            StartDate = new DateOnly(2026, 10, 1), EndDate = new DateOnly(2026, 10, 2), Status = "Draft",
        });
        var forged = () => db.SaveChangesAsync();
        await forged.Should().ThrowAsync<UnauthorizedAccessException>().WithMessage("*company_scope_denied*");
        db.ChangeTracker.Clear();

        // 2. In-scope write with server-side resolution stamps the actor's single company.
        var batch = new WPSFileBatch { TenantId = tenantId };
        db.WPSFileBatches.Add(batch);
        await db.SaveChangesAsync();
        batch.CompanyId.Should().Be(CompanyA);
    }

    [Fact]
    public async Task InMemoryContext_WithoutAccessor_RemainsTrustedSystemContext()
    {
        var tenantId = Guid.NewGuid();
        await using var db = NewDb(Guid.NewGuid().ToString());
        db.Tenants.Add(new Zayra.Api.Domain.Entities.Tenant { Id = tenantId, Name = "IM2", Slug = $"im2-{Guid.NewGuid():N}"[..20] });
        db.LeaveRequests.Add(new LeaveRequest
        {
            TenantId = tenantId, EmployeeId = 42,
            StartDate = new DateOnly(2026, 10, 1), EndDate = new DateOnly(2026, 10, 2), Status = "Draft",
        });

        var act = () => db.SaveChangesAsync();
        await act.Should().NotThrowAsync("no HTTP user = seeder/worker/backfill context, trusted by design");
    }

    private static readonly Guid CompanyA = Guid.NewGuid();
    private static readonly Guid CompanyB = Guid.NewGuid();

    private static ZayraDbContext NewDb(string dbName, IHttpContextAccessor? accessor = null) => new(
        new DbContextOptionsBuilder<ZayraDbContext>().UseInMemoryDatabase(dbName).Options, accessor);

    private static ClaimsPrincipal ScopedPrincipal(Guid tenantId, Guid companyId) =>
        new(new ClaimsIdentity(new[]
        {
            new Claim("tenant_id", tenantId.ToString()),
            new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
            new Claim(EntityScopeContext.V2ClaimType,
                JsonSerializer.Serialize(new { v = 2, m = "companies", c = new[] { companyId } })),
        }, "Test"));

    private sealed class FixedAccessor : IHttpContextAccessor
    {
        public HttpContext? HttpContext { get; set; }
    }
}
