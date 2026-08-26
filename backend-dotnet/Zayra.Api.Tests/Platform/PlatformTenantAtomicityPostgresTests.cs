using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Auth;
using Zayra.Api.Controllers;
using Zayra.Api.Domain.Entities;

namespace Zayra.Api.Tests.Platform;

[Collection("Integration")]
[Trait("Category", "Integration")]
public sealed class PlatformTenantAtomicityPostgresTests : PlatformTestBase
{
    private readonly PostgresFixture _fixture;

    public PlatformTenantAtomicityPostgresTests(PostgresFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task CreateTenant_WhenProvisioningFails_RollsBackTenantShell()
    {
        await using var db = _fixture.CreateDb();
        var slug = $"atomic-{Guid.NewGuid():N}";
        var controller = CreateController(db, authSeeder: new ThrowingAuthSeeder());
        var request = new CreateTenantRequest(
            Name: "Atomic Provisioning",
            Slug: slug,
            AdminEmail: $"admin-{Guid.NewGuid():N}@example.test",
            AdminFullName: "Atomic Admin",
            AdminPassword: "SecurePass123!",
            Plan: "Starter",
            MaxUsers: null,
            MaxEmployees: null,
            BillingEmail: null,
            BillingCycle: null,
            MonthlyAmount: null,
            CurrencyCode: null,
            ExpiresAtUtc: null);

        var action = () => controller.CreateTenant(request, CancellationToken.None);

        await action.Should().ThrowAsync<ProvisioningFailure>();

        await using var verification = _fixture.CreateDb();
        (await verification.Tenants.IgnoreQueryFilters().AnyAsync(x => x.Slug == slug))
            .Should().BeFalse("tenant, roles, defaults, admin and subscription are one atomic provisioning unit");
    }

    private sealed class ThrowingAuthSeeder : IAuthSeeder
    {
        public Task SeedAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<Role> EnsureTenantRolesAsync(
            Guid tenantId,
            CancellationToken cancellationToken = default)
            => throw new ProvisioningFailure();
    }

    private sealed class ProvisioningFailure : Exception { }
}
