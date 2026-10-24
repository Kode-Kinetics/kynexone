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
