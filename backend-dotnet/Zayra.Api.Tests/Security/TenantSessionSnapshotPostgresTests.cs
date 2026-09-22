using System.Data.Common;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql.EntityFrameworkCore.PostgreSQL.Infrastructure;
using Zayra.Api.Application.Common;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.Auth;
using Zayra.Api.Models;

namespace Zayra.Api.Tests.Security;

/// <summary>
/// Proves that one tenant-session decision is derived from one PostgreSQL snapshot. The old
/// READ COMMITTED implementation read the user graph first and active companies later, so a
/// transaction committed between those statements could manufacture a state that never existed.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration")]
public sealed class TenantSessionSnapshotPostgresTests
{
    private readonly PostgresFixture _fixture;

    public TenantSessionSnapshotPostgresTests(PostgresFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task ConcurrentScopeAndStampChange_CannotAuthorizeImpossibleMixedSnapshot()
    {
        var createdAtUtc = new DateTime(2026, 9, 22, 1, 0, 0, DateTimeKind.Utc);
        var email = $"snapshot-{Guid.NewGuid():N}@example.test";
        var tenant = new Tenant
        {
            Name = $"Session snapshot {Guid.NewGuid():N}",
            Slug = $"session-snapshot-{Guid.NewGuid():N}",
            IsActive = true
        };
        var oldCompany = new Company
        {
            TenantId = tenant.Id,
            LegalNameEn = "Old active company",
            IsActive = true
        };
        var newCompany = new Company
        {
            TenantId = tenant.Id,
            LegalNameEn = "New active company",
            IsActive = false
        };
        var user = new User
        {
            TenantId = tenant.Id,
            Email = email,
            NormalizedEmail = email.ToUpperInvariant(),
            FullName = "Session Snapshot User",
            PasswordHash = "not-used-by-this-test",
            Status = "Active",
            AccessMode = AccessModes.FullPortal,
            IdentityProvider = "Local",
            IsActive = true,
            IsEmailConfirmed = true,
            IsGroupScope = false,
            CreatedAtUtc = createdAtUtc
        };
        var allCurrentCompaniesGrant = new UserEntityAccess
        {
            TenantId = tenant.Id,
            UserId = user.Id,
            User = user,
            CompanyId = null,
            GrantMode = EntityGrantModes.AllCurrentCompanies,
            Role = "Viewer",
            IsActive = true,
            CreatedAtUtc = createdAtUtc
        };

        await using (var seed = _fixture.CreateRetryingDb())
        {
            seed.AddRange(tenant, oldCompany, newCompany, user, allCurrentCompaniesGrant);
            await seed.SaveChangesAsync();
        }

        var oldStamp = TenantSessionSecurity.StampValue(user);
        var oldStatePrincipal = PrincipalFor(user.Id, tenant.Id, oldStamp, oldCompany.Id);
        var impossiblePrincipal = PrincipalFor(user.Id, tenant.Id, oldStamp, newCompany.Id);

        // Positive and negative controls: before the writer, only the old-company token is valid.
        await using (var before = _fixture.CreateRetryingDb())
        {
            Assert.True(await TenantSessionSecurity.IsCurrentAsync(
                oldStatePrincipal, before, CancellationToken.None));
        }
        await using (var before = _fixture.CreateRetryingDb())
        {
            Assert.False(await TenantSessionSecurity.IsCurrentAsync(
                impossiblePrincipal, before, CancellationToken.None));
        }

        var pause = new PauseAfterUserGraphReadInterceptor();
        var options = new DbContextOptionsBuilder<ZayraDbContext>()
            .UseNpgsql(_fixture.ConnectionString, provider => provider.EnableRetryOnFailure(
                maxRetryCount: 5,
                maxRetryDelay: TimeSpan.FromSeconds(5),
                errorCodesToAdd: null))
            .AddInterceptors(
                Zayra.Api.Infrastructure.Jobs.RowLockingInterceptor.Instance,
                pause)
            .Options;

        await using var reader = new ZayraDbContext(options);
        var validation = TenantSessionSecurity.IsCurrentAsync(
            impossiblePrincipal, reader, CancellationToken.None);

        await pause.WaitUntilUserGraphWasReadAsync();
        try
        {
            // This one commit changes both halves of the authorization decision. A stale user
            // graph plus these new active companies would match impossiblePrincipal; neither the
            // complete pre-state nor the complete post-state does.
            await using var writer = _fixture.CreateRetryingDb();
            var persistedUser = await writer.Users.SingleAsync(x => x.Id == user.Id);
            var persistedOldCompany = await writer.Companies.SingleAsync(x => x.Id == oldCompany.Id);
            var persistedNewCompany = await writer.Companies.SingleAsync(x => x.Id == newCompany.Id);
            persistedOldCompany.IsActive = false;
            persistedNewCompany.IsActive = true;
            TenantSessionSecurity.RotateStamp(persistedUser, createdAtUtc.AddMinutes(1));
            await writer.SaveChangesAsync();
        }
        finally
        {
            pause.ReleaseAfterWriterCommit();
        }

        Assert.False(await validation);
        Assert.Equal(1, pause.PauseCount);

        // Post-state control: the old stamp is revoked, so the same token remains invalid after
        // the switch. The concurrent result above therefore has to match a real committed state.
        await using var after = _fixture.CreateRetryingDb();
        Assert.False(await TenantSessionSecurity.IsCurrentAsync(
            impossiblePrincipal, after, CancellationToken.None));
    }

    private static ClaimsPrincipal PrincipalFor(
        Guid userId,
        Guid tenantId,
        string sessionStamp,
        Guid companyId)
    {
        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, userId.ToString()),
            new Claim("tenant_id", tenantId.ToString()),
            new Claim(TenantSessionSecurity.SessionStampClaim, sessionStamp),
            new Claim(EntityScopeContext.V2ClaimType, JsonSerializer.Serialize(new
            {
                v = 2,
                m = EntityScopeModes.Companies,
                c = new[] { companyId }
            }))
        };
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "SnapshotTest"));
    }

    private sealed class PauseAfterUserGraphReadInterceptor : DbCommandInterceptor
    {
        private readonly TaskCompletionSource _userGraphWasRead =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _writerCommitted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _pauseCount;

        public int PauseCount => Volatile.Read(ref _pauseCount);

        public Task WaitUntilUserGraphWasReadAsync() =>
            _userGraphWasRead.Task.WaitAsync(TimeSpan.FromSeconds(20));

        public void ReleaseAfterWriterCommit() => _writerCommitted.TrySetResult();

        public override async ValueTask<DbDataReader> ReaderExecutedAsync(
            DbCommand command,
            CommandExecutedEventData eventData,
            DbDataReader result,
            CancellationToken cancellationToken = default)
        {
            if (IsInitialUserGraphRead(command.CommandText)
                && Interlocked.CompareExchange(ref _pauseCount, 1, 0) == 0)
            {
                _userGraphWasRead.TrySetResult();
                await _writerCommitted.Task.WaitAsync(cancellationToken);
            }

            return result;
        }

        private static bool IsInitialUserGraphRead(string sql) =>
            sql.Contains("users", StringComparison.OrdinalIgnoreCase)
            && sql.Contains("user_roles", StringComparison.OrdinalIgnoreCase)
            && sql.Contains("user_entity_access", StringComparison.OrdinalIgnoreCase);
    }
}
