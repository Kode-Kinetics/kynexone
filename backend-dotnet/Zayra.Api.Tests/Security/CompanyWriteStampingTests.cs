using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Data;
using Zayra.Api.Models;

namespace Zayra.Api.Tests.Security;

/// <summary>
/// Phase 2 write-side enforcement (spec 4): the read filter cannot stop a request from
/// WRITING into another company, and a forgotten CompanyId creates rows invisible to
/// scoped users. These tests pin the SaveChanges stamping matrix against real Postgres.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration")]
public class CompanyWriteStampingTests
{
    private readonly PostgresFixture _fx;
    public CompanyWriteStampingTests(PostgresFixture fx) => _fx = fx;

    // ── Unauthorized explicit CompanyId is rejected ────────────────────────────

    [Fact]
    public async Task Write_ExplicitCompanyOutsideActorScope_IsBlocked()
    {
        var (tenantId, companyA, companyB) = await SeedTwoCompanyTenant();

        var accessor = Accessor(ScopedPrincipal(tenantId, companyA));
        await using var db = _fx.CreateDbWithAccessor(accessor);

        db.LeaveRequests.Add(new LeaveRequest
        {
            TenantId = tenantId, CompanyId = companyB, EmployeeId = 999999,
            StartDate = new DateOnly(2026, 8, 1), EndDate = new DateOnly(2026, 8, 2), Status = "Draft",
        });

        var act = () => db.SaveChangesAsync();
        await act.Should().ThrowAsync<UnauthorizedAccessException>()
            .WithMessage("*company_scope_denied*",
                "a Company A actor must not create operational rows inside Company B (spec 7)");
    }

    // ── Missing CompanyId resolves server-side, never trusted from the client ──

    [Fact]
    public async Task Write_MissingCompany_SingleCompanyTenant_StampsTheDefault()
    {
        await using var seed = _fx.CreateDb();
        var tenantId = await PostgresFixture.SeedMinimalTenant(seed);
        var only = NewCompany(tenantId, "Stamp Single");
        seed.Companies.Add(only);
        await seed.SaveChangesAsync();

        var accessor = Accessor(GroupPrincipal(tenantId));
        await using var db = _fx.CreateDbWithAccessor(accessor);
        var loan = new EmployeeLoan { TenantId = tenantId, EmployeeId = Guid.NewGuid(), LoanTypeId = Guid.NewGuid(), LoanNumber = $"LN-{Guid.NewGuid():N}" };
        db.EmployeeLoans.Add(loan);
        await db.SaveChangesAsync();

        loan.CompanyId.Should().Be(only.Id, "SingleCompany tenants resolve unambiguously (spec 4)");
    }

    [Fact]
    public async Task Write_MissingCompany_EmployeeLinked_FollowsOwningEmployee()
    {
        var (tenantId, companyA, companyB) = await SeedTwoCompanyTenant();
        await using var seed = _fx.CreateDb();
        var employee = new Employee { TenantId = tenantId, CompanyId = companyB, EmployeeCode = $"ST-{Guid.NewGuid():N}"[..12], FullName = "Owner B", Status = "Active", JoiningDate = DateTime.UtcNow };
        seed.Employees.Add(employee);
        await seed.SaveChangesAsync();

        // Group-scope actor: record follows the employee's legal entity, not a guess.
        var accessor = Accessor(GroupPrincipal(tenantId));
        await using var db = _fx.CreateDbWithAccessor(accessor);
        var request = new OvertimeRequest { TenantId = tenantId, EmployeeId = employee.Id, WorkDate = new DateOnly(2026, 8, 3) };
        db.OvertimeRequests.Add(request);
        await db.SaveChangesAsync();

        request.CompanyId.Should().Be(companyB, "the owning employee's company is the safe route (spec 4)");
    }

    [Fact]
    public async Task Write_EmployeeLinked_OutsideActorScope_IsBlocked()
    {
        var (tenantId, companyA, companyB) = await SeedTwoCompanyTenant();
        await using var seed = _fx.CreateDb();
        var employee = new Employee { TenantId = tenantId, CompanyId = companyB, EmployeeCode = $"ST-{Guid.NewGuid():N}"[..12], FullName = "Owner B", Status = "Active", JoiningDate = DateTime.UtcNow };
        seed.Employees.Add(employee);
        await seed.SaveChangesAsync();

        var accessor = Accessor(ScopedPrincipal(tenantId, companyA));
        await using var db = _fx.CreateDbWithAccessor(accessor);
        db.OvertimeRequests.Add(new OvertimeRequest { TenantId = tenantId, EmployeeId = employee.Id, WorkDate = new DateOnly(2026, 8, 3) });

        var act = () => db.SaveChangesAsync();
        await act.Should().ThrowAsync<UnauthorizedAccessException>().WithMessage("*company_scope_denied*",
            "Company A HR must not write records for a Company B employee (spec 7)");
    }

    [Fact]
    public async Task Write_MissingCompany_GroupTenant_NoDerivableContext_FailsClosed()
    {
        var (tenantId, _, _) = await SeedTwoCompanyTenant();

        var accessor = Accessor(GroupPrincipal(tenantId));
        await using var db = _fx.CreateDbWithAccessor(accessor);
        // No employee linkage resolvable (EmployeeId=0), no explicit company, 2 companies.
        db.WPSFileBatches.Add(new WPSFileBatch { TenantId = tenantId });

        var act = () => db.SaveChangesAsync();
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*company_scope_required*",
            "Group tenants require explicit company context (spec 4)");
    }

    [Fact]
    public async Task Write_ScopedActorWithSingleAccessibleCompany_ResolvesToIt()
    {
        var (tenantId, companyA, _) = await SeedTwoCompanyTenant();

        var accessor = Accessor(ScopedPrincipal(tenantId, companyA));
        await using var db = _fx.CreateDbWithAccessor(accessor);
        var batch = new WPSFileBatch { TenantId = tenantId };
        db.WPSFileBatches.Add(batch);
        await db.SaveChangesAsync();

        batch.CompanyId.Should().Be(companyA, "an actor scoped to exactly one company is unambiguous context");
    }

    // ── Reassignment / null-out is blocked ─────────────────────────────────────

    [Fact]
    public async Task Update_CompanyReassignmentOrNullOut_IsBlocked()
    {
        var (tenantId, companyA, companyB) = await SeedTwoCompanyTenant();
        await using var seed = _fx.CreateDb();
        var loan = new EmployeeLoan { TenantId = tenantId, CompanyId = companyA, EmployeeId = Guid.NewGuid(), LoanTypeId = Guid.NewGuid(), LoanNumber = $"LN-{Guid.NewGuid():N}" };
        seed.EmployeeLoans.Add(loan);
        await seed.SaveChangesAsync();

        var accessor = Accessor(GroupPrincipal(tenantId));
        await using var db = _fx.CreateDbWithAccessor(accessor);

        var tracked = await db.EmployeeLoans.FirstAsync(l => l.Id == loan.Id);
        tracked.CompanyId = companyB;
        var reassign = () => db.SaveChangesAsync();
        await reassign.Should().ThrowAsync<InvalidOperationException>().WithMessage("*company_reassignment_blocked*");

        db.ChangeTracker.Clear();
        var tracked2 = await db.EmployeeLoans.FirstAsync(l => l.Id == loan.Id);
        tracked2.CompanyId = null;
        var nullOut = () => db.SaveChangesAsync();
        await nullOut.Should().ThrowAsync<InvalidOperationException>().WithMessage("*company_reassignment_blocked*",
            "nulling-out would make the row invisible to scoped users — only an explicit transfer workflow may move rows");
    }

    // ── System contexts stay trusted ───────────────────────────────────────────

    [Fact]
    public async Task Write_SystemContext_NoHttpUser_IsNotStamped_OrBlocked()
    {
        var (tenantId, _, _) = await SeedTwoCompanyTenant();

        await using var db = _fx.CreateDb(); // no accessor — seeder/worker/backfill context
        var batch = new WPSFileBatch { TenantId = tenantId };
        db.WPSFileBatches.Add(batch);
        await db.SaveChangesAsync();

        batch.CompanyId.Should().BeNull("system contexts are trusted group scope; the backfill repairs nulls");
    }

    // ── Harness ────────────────────────────────────────────────────────────────

    private async Task<(Guid TenantId, Guid CompanyA, Guid CompanyB)> SeedTwoCompanyTenant()
    {
        await using var db = _fx.CreateDb();
        var tenantId = await PostgresFixture.SeedMinimalTenant(db);
        var a = NewCompany(tenantId, "Stamp A");
        var b = NewCompany(tenantId, "Stamp B");
        db.Companies.AddRange(a, b);
        await db.SaveChangesAsync();
        return (tenantId, a.Id, b.Id);
    }

    private static Company NewCompany(Guid tenantId, string name) => new()
    {
        TenantId = tenantId, LegalNameEn = name, RegistrationNumber = $"REG-{Guid.NewGuid():N}", IsActive = true,
    };

    private static ClaimsPrincipal ScopedPrincipal(Guid tenantId, Guid companyId) =>
        new(new ClaimsIdentity(new[]
        {
            new Claim("tenant_id", tenantId.ToString()),
            new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
            new Claim(Zayra.Api.Application.Common.EntityScopeContext.V2ClaimType,
                JsonSerializer.Serialize(new { v = 2, m = "companies", c = new[] { companyId } })),
        }, "Test"));

    private static ClaimsPrincipal GroupPrincipal(Guid tenantId) =>
        new(new ClaimsIdentity(new[]
        {
            new Claim("tenant_id", tenantId.ToString()),
            new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
            new Claim(Zayra.Api.Application.Common.EntityScopeContext.V2ClaimType,
                JsonSerializer.Serialize(new { v = 2, m = "group", c = Array.Empty<Guid>() })),
        }, "Test"));

    private static IHttpContextAccessor Accessor(ClaimsPrincipal principal) =>
        new FixedAccessor { HttpContext = new DefaultHttpContext { User = principal } };

    private sealed class FixedAccessor : IHttpContextAccessor
    {
        public HttpContext? HttpContext { get; set; }
    }
}
