using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Auth;
using Zayra.Api.Application.Common;
using Zayra.Api.Controllers;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.Audit;
using Zayra.Api.Infrastructure.Auth;
using Zayra.Api.Models;

namespace Zayra.Api.Tests.Platform;

public sealed class WriterMutationContainmentTests : PlatformTestBase
{
    [Fact]
    public async Task RevokeSessions_InvalidatesRefreshChallengeAndAccessStamp_AtomicallyWithAudit()
    {
        await using var db = CreateDb();
        var tenant = new Tenant { Id = Guid.NewGuid(), Name = "Credential tenant", Slug = "credential-tenant" };
        var user = new User
        {
            Id = Guid.NewGuid(),
            TenantId = tenant.Id,
            Email = "person@example.com",
            NormalizedEmail = "PERSON@EXAMPLE.COM",
            FullName = "Person",
            PasswordHash = "unreachable"
        };
        var refresh = new RefreshToken
        {
            UserId = user.Id,
            TokenHash = "refresh-hash",
            ExpiresAtUtc = DateTime.UtcNow.AddDays(1)
        };
        var challenge = new MfaChallengeToken
        {
            TenantId = tenant.Id,
            UserId = user.Id,
            TokenHash = "challenge-hash",
            ExpiresAtUtc = DateTime.UtcNow.AddMinutes(5)
        };
        db.AddRange(tenant, user, refresh, challenge);
        await db.SaveChangesAsync();
        var priorStamp = TenantSessionSecurity.StampValue(user);

        var result = await CreateController(db).RevokeSessions(user.Id, CancellationToken.None);

        result.Should().BeOfType<OkObjectResult>();
        db.ChangeTracker.Clear();
        var committedUser = await db.Users.IgnoreQueryFilters().SingleAsync(x => x.Id == user.Id);
        TenantSessionSecurity.StampValue(committedUser).Should().NotBe(priorStamp);
        (await db.RefreshTokens.SingleAsync(x => x.Id == refresh.Id)).RevokedAtUtc.Should().NotBeNull();
        (await db.MfaChallengeTokens.IgnoreQueryFilters().SingleAsync(x => x.Id == challenge.Id)).UsedAtUtc.Should().NotBeNull();
        (await db.AdminAuditLogs.IgnoreQueryFilters().CountAsync(x => x.Action == "SessionsRevoked")).Should().Be(1);
        (await db.LoginActivities.IgnoreQueryFilters().CountAsync(x => x.EventType == LoginEventTypes.SessionRevoked)).Should().Be(1);
    }

    [Fact]
    public async Task SuspendAndReactivate_InvalidateCredentialsOnBothTransitions()
    {
        await using var db = CreateDb();
        var tenant = new Tenant { Id = Guid.NewGuid(), Name = "Lifecycle tenant", Slug = "lifecycle-tenant" };
        var subscription = new TenantSubscription { TenantId = tenant.Id, Status = "Active" };
        var user = new User
        {
            Id = Guid.NewGuid(),
            TenantId = tenant.Id,
            Email = "person@example.com",
            NormalizedEmail = "PERSON@EXAMPLE.COM",
            FullName = "Person",
            PasswordHash = "unreachable"
        };
        db.AddRange(tenant, subscription, user);
        await db.SaveChangesAsync();
        var controller = CreateController(db);

        var firstRefresh = new RefreshToken { UserId = user.Id, TokenHash = "r1", ExpiresAtUtc = DateTime.UtcNow.AddDays(1) };
        var firstChallenge = new MfaChallengeToken
        {
            TenantId = tenant.Id, UserId = user.Id, TokenHash = "c1", ExpiresAtUtc = DateTime.UtcNow.AddMinutes(5)
        };
        db.AddRange(firstRefresh, firstChallenge);
        await db.SaveChangesAsync();

        (await controller.SuspendTenant(tenant.Id, new TenantActionRequest("test"), CancellationToken.None))
            .Should().BeOfType<OkObjectResult>();
        db.ChangeTracker.Clear();
        (await db.Tenants.SingleAsync(x => x.Id == tenant.Id)).IsActive.Should().BeFalse();
        (await db.RefreshTokens.SingleAsync(x => x.Id == firstRefresh.Id)).RevokedAtUtc.Should().NotBeNull();
        (await db.MfaChallengeTokens.IgnoreQueryFilters().SingleAsync(x => x.Id == firstChallenge.Id)).UsedAtUtc.Should().NotBeNull();

        var secondRefresh = new RefreshToken { UserId = user.Id, TokenHash = "r2", ExpiresAtUtc = DateTime.UtcNow.AddDays(1) };
        var secondChallenge = new MfaChallengeToken
        {
            TenantId = tenant.Id, UserId = user.Id, TokenHash = "c2", ExpiresAtUtc = DateTime.UtcNow.AddMinutes(5)
        };
        db.AddRange(secondRefresh, secondChallenge);
        await db.SaveChangesAsync();

        (await controller.ReactivateTenant(tenant.Id, new TenantActionRequest("test"), CancellationToken.None))
            .Should().BeOfType<OkObjectResult>();
        db.ChangeTracker.Clear();
        (await db.Tenants.SingleAsync(x => x.Id == tenant.Id)).IsActive.Should().BeTrue();
        (await db.RefreshTokens.SingleAsync(x => x.Id == secondRefresh.Id)).RevokedAtUtc.Should().NotBeNull();
        (await db.MfaChallengeTokens.IgnoreQueryFilters().SingleAsync(x => x.Id == secondChallenge.Id)).UsedAtUtc.Should().NotBeNull();
    }

    [Fact]
    public async Task UnsafeLifecycleWriters_AreFailClosedWithoutMutationOrSuccessAudit()
    {
        await using var db = CreateDb();
        var tenant = new Tenant
        {
            Id = Guid.NewGuid(),
            Name = "Deleted tenant",
            Slug = "deleted-tenant__deleted_12345678",
            IsActive = false
        };
        var company = new Company
        {
            Id = Guid.NewGuid(),
            TenantId = tenant.Id,
            LegalNameEn = "Draft company",
            RegistrationNumber = "DRAFT-1",
            ApprovalStatus = CompanyApprovalStatuses.Draft,
            IsActive = false
        };
        db.AddRange(tenant, company);
        await db.SaveChangesAsync();
        var controller = CreateController(db);

        (await controller.DeleteTenant(tenant.Id, "DELETE", CancellationToken.None))
            .Should().BeOfType<ConflictObjectResult>();
        (await controller.RestoreTenant(tenant.Id, CancellationToken.None)).Should().BeOfType<ConflictObjectResult>();
        (await controller.ReactivateTenant(tenant.Id, new TenantActionRequest("bypass attempt"), CancellationToken.None))
            .Should().BeOfType<ConflictObjectResult>();
        (await controller.BulkRestoreTenants(
            new BulkTenantActionRequest(new List<Guid> { tenant.Id }, "test"), CancellationToken.None))
            .Should().BeOfType<ConflictObjectResult>();
        (await controller.BulkDeleteTenants(
            new BulkDeleteTenantsRequest(new List<Guid> { tenant.Id }, "DELETE"), CancellationToken.None))
            .Should().BeOfType<ConflictObjectResult>();
        (await controller.ApproveCompany(tenant.Id, company.Id, CancellationToken.None))
            .Should().BeOfType<ConflictObjectResult>();

        db.ChangeTracker.Clear();
        var committedTenant = await db.Tenants.SingleAsync(x => x.Id == tenant.Id);
        committedTenant.IsActive.Should().BeFalse();
        committedTenant.Slug.Should().Be("deleted-tenant__deleted_12345678");
        var committedCompany = await db.Companies.IgnoreQueryFilters().SingleAsync(x => x.Id == company.Id);
        committedCompany.IsActive.Should().BeFalse();
        committedCompany.ApprovalStatus.Should().Be(CompanyApprovalStatuses.Draft);
        (await db.AdminAuditLogs.IgnoreQueryFilters().CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task ScimDryRun_WritesEvidenceWithoutFlushingProposedIdentityMutation()
    {
        await using var db = CreateDb();
        var tenant = new Tenant { Id = Guid.NewGuid(), Name = "SCIM tenant", Slug = "scim-tenant" };
        var user = new User
        {
            Id = Guid.NewGuid(),
            TenantId = tenant.Id,
            Email = "person@example.com",
            NormalizedEmail = "PERSON@EXAMPLE.COM",
            FullName = "Original Name",
            PasswordHash = "unreachable",
            ExternalId = "ext-1",
            IdentityProvider = EnterpriseIdentityProtocols.Scim,
            ProvisioningSource = EnterpriseIdentityProtocols.Scim,
            AccessMode = AccessModes.EssOnly,
            IsActive = true,
            Status = "Active"
        };
        db.AddRange(
            tenant,
            user,
            new TenantIdentityProviderSetting
            {
                TenantId = tenant.Id,
                ScimEnabled = true,
                ScimDryRun = true,
                ScimTokenHash = "configured",
                AllowedDomainsCsv = "example.com"
            });
        await db.SaveChangesAsync();
        var service = new EnterpriseIdentityService(
            db,
            new DeterministicTokenService(),
            new Pbkdf2PasswordHasher(),
            new AuditService(db));

        var proposed = await service.UpsertScimUserAsync(
            tenant.Id,
            new ScimUserUpsertRequest(
                "ext-1",
                "person@example.com",
                false,
                new ScimName("Proposed", "Name", "Proposed Name"),
                new[] { new ScimEmail("person@example.com") },
                "Proposed Name"),
            new RequestContext("127.0.0.1", "test", null, tenant.Id),
            CancellationToken.None);

        proposed.Active.Should().BeFalse("the response describes the dry-run proposal");
        db.ChangeTracker.Clear();
        var committed = await db.Users.IgnoreQueryFilters().SingleAsync(x => x.Id == user.Id);
        committed.FullName.Should().Be("Original Name");
        committed.IsActive.Should().BeTrue();
        committed.AccessMode.Should().Be(AccessModes.EssOnly);
        var evidence = await db.EnterpriseIdentityProvisioningEvents.IgnoreQueryFilters().SingleAsync();
        evidence.Status.Should().Be("DryRun");
    }

    [Fact]
    public async Task OrganizationImport_CannotChangeExistingCompanyLifecycle()
    {
        await using var db = CreateDb();
        var tenant = new Tenant { Id = Guid.NewGuid(), Name = "Import tenant", Slug = "import-tenant" };
        var company = new Company
        {
            Id = Guid.NewGuid(),
            TenantId = tenant.Id,
            LegalNameEn = "Existing Company",
            CountryCode = "SA",
            RegistrationNumber = "REG-1",
            DefaultCurrency = "SAR",
            IsActive = false
        };
        db.AddRange(tenant, company);
        await db.SaveChangesAsync();
        var controller = new OrganizationStructureImportController(db, new AuditService(db))
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(new[]
                    {
                        new Claim("tenant_id", tenant.Id.ToString()),
                        new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
                        new Claim(ClaimTypes.Role, "Admin"),
                        new Claim(
                            EntityScopeContext.V2ClaimType,
                            JsonSerializer.Serialize(new { v = 2, m = "group", c = Array.Empty<Guid>() }))
                    }, "Test"))
                }
            }
        };
        var metadataOnlyRequest = new OrganizationStructureImportRequest(
            CompaniesCsv: "LegalNameEn,TradeName,CountryCode,DefaultCurrency\nExisting Company,Updated Trade Name,SA,SAR\n",
            BranchesCsv: null,
            CostCentersCsv: null,
            DepartmentsCsv: null,
            GradesCsv: null,
            GradePayComponentsCsv: null,
            DesignationsCsv: null,
            PositionsCsv: null);

        var metadataOnlyResponse = await controller.Commit(metadataOnlyRequest, CancellationToken.None);

        metadataOnlyResponse.Result.Should().BeOfType<OkObjectResult>();
        db.ChangeTracker.Clear();
        var metadataUpdated = await db.Companies.IgnoreQueryFilters().SingleAsync(x => x.Id == company.Id);
        metadataUpdated.TradeName.Should().Be("Updated Trade Name");
        metadataUpdated.IsActive.Should().BeFalse();
        var successAuditCount = await db.AuditLogs.IgnoreQueryFilters()
            .CountAsync(x => x.Action == "setup.organization_structure_import_committed");

        var request = new OrganizationStructureImportRequest(
            CompaniesCsv: "LegalNameEn,CountryCode,DefaultCurrency,IsActive\nExisting Company,SA,SAR,true\n",
            BranchesCsv: null,
            CostCentersCsv: null,
            DepartmentsCsv: null,
            GradesCsv: null,
            GradePayComponentsCsv: null,
            DesignationsCsv: null,
            PositionsCsv: null);

        var response = await controller.Commit(request, CancellationToken.None);

        var blocked = response.Result.Should().BeOfType<UnprocessableEntityObjectResult>().Subject;
        blocked.Value.Should().BeOfType<OrganizationStructureImportResult>()
            .Which.HasBlockingErrors.Should().BeTrue();
        db.ChangeTracker.Clear();
        (await db.Companies.IgnoreQueryFilters().SingleAsync(x => x.Id == company.Id)).IsActive.Should().BeFalse();
        (await db.AuditLogs.IgnoreQueryFilters().CountAsync(x => x.Action == "setup.organization_structure_import_committed"))
            .Should().Be(successAuditCount, "the rejected lifecycle change must not write a success audit");
    }

    private sealed class DeterministicTokenService : ITokenService
    {
        public string CreateAccessToken(
            User user,
            IReadOnlyCollection<string> roles,
            IReadOnlyCollection<string> permissions,
            Tenant tenant,
            IReadOnlyCollection<EntityAccessGrant> entityAccess,
            EntityScopeDescriptor entityScope,
            out DateTime expiresAtUtc)
        {
            expiresAtUtc = DateTime.UtcNow.AddMinutes(5);
            return "unused";
        }

        public string CreateSecureToken() => "deterministic-secure-token";

        public string HashToken(string token) => $"hash:{token}";
    }
}
