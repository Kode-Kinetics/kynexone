using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Auth;
using Zayra.Api.Application.Common;
using Zayra.Api.Infrastructure.Audit;
using Zayra.Api.Infrastructure.Government;
using Zayra.Api.Infrastructure.Government.Credentials;
using Zayra.Api.Models;
using Zayra.Api.Tests.FieldEncryption;

namespace Zayra.Api.Tests.Government;

/// <summary>
/// The credential table on a real database: a company-scoped user who names no company rotates their own company's row (one row,
/// version 2, it opens); the partial unique indexes hold; last-used is a single-row update that never flushes the caller's work;
/// two rotations racing give one winner.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration")]
public sealed class GovernmentCredentialPostgresTests(PostgresFixture fixture)
{
    private const string SecretA = "synthetic-secret-pg-0001";
    private const string SecretB = "synthetic-secret-pg-0002";

    private static IHttpContextAccessor CompanyUser(Guid tenantId, Guid company) => new HttpContextAccessor
    {
        HttpContext = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
            [
                new Claim("tenant_id", tenantId.ToString()), new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
                new Claim(EntityScopeContext.V2ClaimType, JsonSerializer.Serialize(new { v = 2, m = "companies", c = new[] { company.ToString() } })),
            ], "test")),
        },
    };

    private static GovernmentCredentialStore Store(FeKit kit, Zayra.Api.Data.ZayraDbContext db) =>
        new(db, kit.Keys, kit.Kek, kit.Options, new AuditService(db));

    private async Task<(Guid TenantId, Guid A, Guid B)> SeedAsync()
    {
        await using var seed = fixture.CreateDb();
        var tenantId = await PostgresFixture.SeedMinimalTenant(seed);
        var a = new Company { Id = Guid.NewGuid(), TenantId = tenantId, LegalNameEn = "Synthetic A", IsActive = true };
        var b = new Company { Id = Guid.NewGuid(), TenantId = tenantId, LegalNameEn = "Synthetic B", IsActive = true };
        seed.Companies.AddRange(a, b);
        await seed.SaveChangesAsync();
        return (tenantId, a.Id, b.Id);
    }

    [Fact]
    public async Task ACompanyScopedUser_SavingTwiceWithoutACompany_RotatesOneRowThatOpens()
    {
        var (tenantId, a, _) = await SeedAsync();
        var kit = new FeKit();
        foreach (var secret in new[] { SecretA, SecretB })
        {
            await using var db = fixture.CreateDbWithAccessor(CompanyUser(tenantId, a));
            await Store(kit, db).SaveAsync(tenantId, null, GovernmentBodies.Gosi, "sandbox", GovernmentCredentialKinds.OAuthClient, secret, null,
                new RequestContext("127.0.0.1", "test", Guid.NewGuid(), tenantId), CancellationToken.None);
        }

        await using var verify = fixture.CreateDb();
        var row = await verify.GovernmentCredentials.IgnoreQueryFilters().SingleAsync(x => x.TenantId == tenantId);
        Assert.Equal(a, row.CompanyId);
        Assert.Equal(2, row.CredentialVersion);
        Assert.Equal(SecretB, (await Store(kit, verify).OpenAsync(tenantId, a, GovernmentBodies.Gosi, "sandbox", CancellationToken.None))!.Secret);
    }

    [Fact]
    public async Task OpeningTheTenantWideCredential_AsACompanyUser_RecordsLastUse_WithoutFlushingTheCallersWork()
    {
        var (tenantId, a, _) = await SeedAsync();
        var kit = new FeKit();
        await using (var system = fixture.CreateDb())
            await Store(kit, system).SaveAsync(tenantId, null, GovernmentBodies.Gosi, "sandbox", GovernmentCredentialKinds.ApiKey, SecretA, null,
                new RequestContext("127.0.0.1", "test", Guid.NewGuid(), tenantId), CancellationToken.None);

        await using var db = fixture.CreateDbWithAccessor(CompanyUser(tenantId, a));
        IAuditService callerAudit = new AuditService(db);
        callerAudit.Stage("payroll.batch_prepared", "PaymentBatch", "42", new RequestContext("127.0.0.1", "test", null, tenantId), "{}", a);
        var opened = await Store(kit, db).OpenAsync(tenantId, a, GovernmentBodies.Gosi, "sandbox", CancellationToken.None);

        Assert.Equal(SecretA, opened!.Secret);
        Assert.Single(db.ChangeTracker.Entries().Where(e => e.State == EntityState.Added));
        await using var verify = fixture.CreateDb();
        Assert.NotNull((await verify.GovernmentCredentials.IgnoreQueryFilters().SingleAsync(x => x.TenantId == tenantId)).LastUsedAtUtc);
        Assert.Equal(0, await verify.AuditLogs.IgnoreQueryFilters().CountAsync(x => x.TenantId == tenantId && x.Action == "payroll.batch_prepared"));
    }

    [Fact]
    public async Task ThePartialUniqueIndexes_AllowOneTenantWideAndOnePerCompany()
    {
        var (tenantId, a, _) = await SeedAsync();
        GovernmentCredential Row(Guid? company) => new()
        {
            TenantId = tenantId, CompanyId = company, Body = GovernmentBodies.Gosi, Environment = "sandbox", Kind = GovernmentCredentialKinds.ApiKey,
            Ciphertext = new byte[40], DekVersion = 1, Fingerprint = "0011223344556677",
        };
        foreach (var company in new Guid?[] { null, a })
        {
            await using (var first = fixture.CreateDb())
            {
                var original = Row(company);
                original.Ciphertext[0] = 2;
                first.GovernmentCredentials.Add(original);
                await first.SaveChangesAsync();
            }
            await using var second = fixture.CreateDb();
            var duplicate = Row(company);
            duplicate.Ciphertext[0] = 2;
            second.GovernmentCredentials.Add(duplicate);
            await Assert.ThrowsAsync<DbUpdateException>(() => second.SaveChangesAsync());
        }
    }

    [Fact]
    public async Task TwoRotationsRacing_OneWins_TheOtherIsA409()
    {
        var (tenantId, _, _) = await SeedAsync();
        var kit = new FeKit();
        await using (var system = fixture.CreateDb())
            await Store(kit, system).SaveAsync(tenantId, null, GovernmentBodies.Gosi, "sandbox", GovernmentCredentialKinds.ApiKey, SecretA, null,
                new RequestContext("127.0.0.1", "test", Guid.NewGuid(), tenantId), CancellationToken.None);

        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<object> RotateAsync(string secret)
        {
            await using var db = fixture.CreateDb();
            await gate.Task;
            try
            {
                return await Store(kit, db).SaveAsync(tenantId, null, GovernmentBodies.Gosi, "sandbox", GovernmentCredentialKinds.ApiKey, secret, null,
                    new RequestContext("127.0.0.1", "test", Guid.NewGuid(), tenantId), CancellationToken.None);
            }
            catch (GovernmentCredentialRefusedException refused) { return refused; }
        }
        var runs = Enumerable.Range(0, 6).Select(i => Task.Run(() => RotateAsync($"synthetic-rotation-{i}"))).ToArray();
        gate.SetResult();
        var results = await Task.WhenAll(runs);

        var winners = results.OfType<GovernmentCredentialMetadata>().ToList();
        Assert.All(results.OfType<GovernmentCredentialRefusedException>(), r => Assert.Equal(409, r.StatusCode));
        await using var verify = fixture.CreateDb();
        var row = await verify.GovernmentCredentials.IgnoreQueryFilters().SingleAsync(x => x.TenantId == tenantId);
        Assert.Equal(1 + winners.Count, row.CredentialVersion);
        Assert.NotNull(await Store(kit, verify).OpenAsync(tenantId, null, GovernmentBodies.Gosi, "sandbox", CancellationToken.None));
    }
}
