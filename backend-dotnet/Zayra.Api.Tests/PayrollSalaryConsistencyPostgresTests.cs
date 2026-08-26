using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Zayra.Api.Application.Auth;
using Zayra.Api.Application.Common;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.Auth;
using Zayra.Api.Infrastructure.Compliance;
using Zayra.Api.Infrastructure.Qiwa;
using Zayra.Api.Infrastructure.Seed;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// Cross-module pilot contract on real PostgreSQL: the locked payroll, salary coverage, payslip YTD,
/// company scope, and GOSI readiness must all resolve the same effective-dated salary assignments.
/// </summary>
[Collection("Integration")]
[Trait("Category", "Integration")]
public sealed class PayrollSalaryConsistencyPostgresTests
{
    private readonly PostgresFixture _fixture;

    public PayrollSalaryConsistencyPostgresTests(PostgresFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task IntelliFlowSeed_LockedRunCoverageYtdAndGosiAgree()
    {
        await using var db = _fixture.CreateDb();
        var hasher = new Pbkdf2PasswordHasher();
        await GosiRuleSeeder.SeedDefaultsAsync(db, NullLogger.Instance);
        await IntelliFlowDemoSeeder.SeedAsync(
            db, hasher, new PilotAuthSeeder(db), NullLogger.Instance, CancellationToken.None);

        var tenant = await db.Tenants.SingleAsync(t => t.Slug == IntelliFlowDemoSeeder.Slug);
        var company = await db.Companies.SingleAsync(c => c.TenantId == tenant.Id);
        var pilotUsers = await db.Users
            .Where(u => u.TenantId == tenant.Id)
            .Include(u => u.EntityAccesses)
            .ToListAsync();
        pilotUsers.Where(u => !u.IsGroupScope).Should().OnlyContain(u =>
            u.EntityAccesses.Count == 1
            && u.EntityAccesses.Single().CompanyId == company.Id
            && u.EntityAccesses.Single().GrantMode == EntityGrantModes.SelectedCompanies
            && u.EntityAccesses.Single().IsActive,
            "every company-scoped pilot persona must receive an explicit legal-entity grant");
        var hrManager = pilotUsers.Single(u => u.Email == "hrmanager@intelliflow.com");
        var hrScope = EntityScopeClaims.Resolve(
            hrManager.IsGroupScope,
            hrManager.EntityAccesses
                .Where(a => a.IsActive)
                .Select(a => new EntityAccessGrant(a.CompanyId, a.Role, a.GrantMode))
                .ToList(),
            new[] { company.Id });
        hrScope.Mode.Should().Be(EntityScopeModes.Companies);
        hrScope.CompanyIds.Should().ContainSingle().Which.Should().Be(company.Id,
            "the HR Manager JWT must carry the company that owns its role approval queue");
        var run = await db.PayrollRuns.SingleAsync(r => r.TenantId == tenant.Id && r.Status == "Locked");
        var periodEnd = new DateOnly(run.Year, run.Month, 1).AddMonths(1).AddDays(-1);
        var employees = await db.Employees
            .Where(e => e.TenantId == tenant.Id && e.CompanyId == company.Id && e.Status == "Active" && !e.IsDeleted)
            .ToListAsync();
        var assignments = await db.EmployeeSalaryStructures
            .Where(s => s.TenantId == tenant.Id && s.IsActive && s.EffectiveDate <= periodEnd)
            .ToListAsync();
        var slips = await db.PayrollSlips
            .Where(s => s.TenantId == tenant.Id && s.RunId == run.Id)
            .ToListAsync();

        employees.Should().HaveCount(12);
        assignments.Select(s => s.EmployeeId).Distinct().Should().HaveCount(12);
        slips.Should().HaveCount(12);
        foreach (var slip in slips)
        {
            var salary = assignments
                .Where(s => s.EmployeeId == slip.EmployeeId)
                .OrderByDescending(s => s.EffectiveDate)
                .First();
            slip.CompanyId.Should().Be(company.Id);
            slip.BasicSalary.Should().Be(salary.BasicSalary);
            slip.YtdGross.Should().Be(slip.GrossSalary);
            slip.YtdDeductions.Should().Be(slip.Deductions);
            slip.YtdNet.Should().Be(slip.NetSalary);
        }

        var coverage = assignments.Select(s => s.EmployeeId).Distinct().Count() * 100m / employees.Count;
        coverage.Should().Be(100m);

        var gosi = await new GosiReadinessReportService(db).BuildAsync(tenant.Id, CancellationToken.None);
        gosi.Employees.Should().HaveCount(12);
        gosi.Employees.Should().NotContain(e =>
            e.BlockingIssues.Any(i => i.Code == "MISSING_BASIC_SALARY"));

        var deductions = await db.PayrollDeductions
            .Where(d => d.TenantId == tenant.Id && d.PayrollRunId == run.Id && d.Source == "Statutory")
            .ToListAsync();
        deductions.Should().NotBeEmpty();
        deductions.Where(d => !d.IsEmployerContribution).Sum(d => d.Amount)
            .Should().Be(slips.Sum(s => s.EmployeeStatutoryTotal));
        deductions.Where(d => d.IsEmployerContribution).Sum(d => d.Amount)
            .Should().Be(slips.Sum(s => s.EmployerStatutoryTotal));

        var statutoryGl = await db.FinanceGlEntries
            .Where(e => e.TenantId == tenant.Id && e.SourceEntityId == run.Id
                     && e.EventType == GlEventTypes.Accrual
                     && e.Description.StartsWith(PayrollGlDescriptions.DeductionPrefix))
            .ToListAsync();
        statutoryGl.Sum(e => e.Amount).Should().Be(deductions.Sum(d => d.Amount));

        company.GosiEmployerId.Should().Be("3000112233");
        company.WpsEmployerId.Should().NotBeNullOrWhiteSpace();
        company.QiwaEstablishmentId.Should().NotBeNullOrWhiteSpace();
        employees.Should().OnlyContain(e => QiwaIntegrationService.MissingQiwaFields(e).Count == 0);
        (await db.QiwaTenantConnections.SingleAsync(c => c.TenantId == tenant.Id))
            .Environment.Should().Be("sandbox");
    }

    [Fact]
    public async Task DemoPilotPipeline_TwoSuccessiveRuns_PreserveTenantSessionAndOperationalData()
    {
        await using var db = _fixture.CreateDb();
        var hasher = new Pbkdf2PasswordHasher();
        var authSeeder = new PilotAuthSeeder(db);

        async Task RunPilotPipelineAsync()
        {
            // Mirror Program.cs ordering, including the legacy seeder entry point. Clean-pilot mode
            // must call it as owner-only; otherwise it recreates Evostel/Al-Nakheel before cleanup
            // deactivates them, adding two inactive tenant rows per backend restart.
            await DemoDataSeeder.SeedAsync(
                db, hasher, authSeeder, NullLogger.Instance, seedLegacyTenants: false);
            await GosiRuleSeeder.SeedDefaultsAsync(db, NullLogger.Instance);
            await StatutoryRuleSeeder.SeedAsync(db, NullLogger.Instance);
            await CleanDemoKsaSeeder.DeactivateGarbageDemoTenantsAsync(db, NullLogger.Instance);
            await IntelliFlowFragmentCleanup.RunAsync(db, NullLogger.Instance);
            await CleanDemoKsaSeeder.SeedAsync(db, hasher, authSeeder, NullLogger.Instance);
            await IntelliFlowDemoSeeder.SeedAsync(db, hasher, authSeeder, NullLogger.Instance);
            db.ChangeTracker.Clear();
        }

        // Establish the same canonical state the startup pipeline produces on a clean database.
        await RunPilotPipelineAsync();

        var tenant = await db.Tenants.SingleAsync(t => t.Slug == IntelliFlowDemoSeeder.Slug && t.IsActive);
        var admin = await db.Users.SingleAsync(u =>
            u.TenantId == tenant.Id && u.NormalizedEmail == AuthService.Normalize(IntelliFlowDemoSeeder.AdminEmail));
        var lockedRunId = await db.PayrollRuns
            .Where(r => r.TenantId == tenant.Id && r.Status == "Locked")
            .Select(r => r.Id)
            .SingleAsync();

        var session = new RefreshToken
        {
            UserId = admin.Id,
            FamilyId = Guid.NewGuid(),
            TokenHash = $"restart-witness-{Guid.NewGuid():N}",
            CreatedAtUtc = DateTime.UtcNow,
            ExpiresAtUtc = DateTime.UtcNow.AddDays(7),
        };
        db.RefreshTokens.Add(session);
        await db.SaveChangesAsync();

        var tenantId = tenant.Id;
        var adminId = admin.Id;
        var adminPasswordHash = admin.PasswordHash;
        var tenantCount = await db.Tenants.CountAsync();
        var intelliFlowHistoryCount = await db.Tenants.CountAsync(t =>
            t.Slug == IntelliFlowDemoSeeder.Slug || t.Slug.StartsWith("intelliflow__deleted_"));

        // Model two backend restarts with SEED_DEMO_DATA=true.
        await RunPilotPipelineAsync();
        await RunPilotPipelineAsync();

        var preservedTenant = await db.Tenants.SingleAsync(t => t.Slug == IntelliFlowDemoSeeder.Slug && t.IsActive);
        var preservedAdmin = await db.Users.SingleAsync(u =>
            u.TenantId == preservedTenant.Id && u.NormalizedEmail == AuthService.Normalize(IntelliFlowDemoSeeder.AdminEmail));
        var preservedSession = await db.RefreshTokens.SingleAsync(r => r.Id == session.Id);

        preservedTenant.Id.Should().Be(tenantId);
        preservedAdmin.Id.Should().Be(adminId);
        preservedAdmin.PasswordHash.Should().Be(adminPasswordHash,
            "a restart must not silently replace the login identity or its credentials");
        preservedAdmin.IsActive.Should().BeTrue();
        preservedSession.RevokedAtUtc.Should().BeNull("valid login sessions must survive a backend restart");
        preservedSession.UserId.Should().Be(adminId);
        (await db.PayrollRuns.AnyAsync(r => r.Id == lockedRunId && r.TenantId == tenantId)).Should().BeTrue(
            "operational payroll data must remain attached to the same tenant");
        (await db.Tenants.CountAsync()).Should().Be(tenantCount);
        (await db.Tenants.CountAsync(t =>
            t.Slug == IntelliFlowDemoSeeder.Slug || t.Slug.StartsWith("intelliflow__deleted_")))
            .Should().Be(intelliFlowHistoryCount,
                "restarts must not accumulate soft-deleted IntelliFlow tenant copies");
    }

    private sealed class PilotAuthSeeder : IAuthSeeder
    {
        private readonly ZayraDbContext _db;

        public PilotAuthSeeder(ZayraDbContext db) => _db = db;

        public Task SeedAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public async Task<Role> EnsureTenantRolesAsync(
            Guid tenantId, CancellationToken cancellationToken = default)
        {
            var names = new[]
            {
                "Admin", "HR Director", "HR Manager", "Finance Approver",
                "Manager", "Supervisor", "Employee", "Auditor",
            };
            var roles = names.Select(name => new Role
            {
                TenantId = tenantId,
                Name = name,
                NormalizedName = name.ToUpperInvariant(),
                Description = name,
            }).ToList();
            _db.Roles.AddRange(roles);
            await _db.SaveChangesAsync(cancellationToken);
            return roles[0];
        }
    }
}
