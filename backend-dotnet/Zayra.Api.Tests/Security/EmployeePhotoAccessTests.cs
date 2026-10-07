using System.Reflection;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Common;
using Zayra.Api.Controllers;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.Authorization;
using Zayra.Api.Infrastructure.Documents;
using Zayra.Api.Models;

namespace Zayra.Api.Tests.Security;

/// <summary>
/// GET /api/employees/{id}/photo serves another employee's profile photo (the mobile team view).
/// It needs both gates: the employees.read permission, and the employee inside the caller's data
/// scope. A different tenant's employee id must answer 404 without the store ever being read.
/// </summary>
public class EmployeePhotoAccessTests
{
    private static readonly byte[] Jpeg = [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10];

    [Fact]
    public void GetPhoto_IsGatedOnEmployeesRead()
    {
        var method = typeof(EmployeePhotoController).GetMethod(nameof(EmployeePhotoController.GetPhoto))!;
        var gate = method.GetCustomAttribute<HasPermissionAttribute>();

        Assert.NotNull(gate);
        Assert.Equal(new[] { "employees.read" }, gate!.Permissions);
    }

    [Fact]
    public async Task ThePermissionGate_DeniesACallerWithoutEmployeesRead_AndAdmitsOneWithIt()
    {
        var method = typeof(EmployeePhotoController).GetMethod(nameof(EmployeePhotoController.GetPhoto))!;
        var requirement = new PermissionRequirement(method.GetCustomAttribute<HasPermissionAttribute>()!.Permissions);

        Assert.False(await Evaluate(requirement, Caller(Guid.NewGuid(), "ess.read")));
        Assert.True(await Evaluate(requirement, Caller(Guid.NewGuid(), "employees.read")));
    }

    [Fact]
    public async Task InScopeEmployee_ReturnsTheStoredPhoto()
    {
        await using var db = CreateDb();
        var (tenantId, member, _) = await SeedAsync(db);
        var storage = new RecordingStorage();
        storage.Objects[member.ProfilePhotoStorageKey] = Jpeg;

        var result = await Controller(db, storage, new FixedScope(member.Id), tenantId)
            .GetPhoto(member.Id, CancellationToken.None);

        var file = Assert.IsType<FileContentResult>(result);
        Assert.Equal("image/jpeg", file.ContentType);
        Assert.Equal(Jpeg, file.FileContents);
    }

    [Fact]
    public async Task OutOfScopeEmployee_IsForbidden_AndTheStoreIsNeverRead()
    {
        await using var db = CreateDb();
        var (tenantId, member, outsider) = await SeedAsync(db);
        var storage = new RecordingStorage();
        storage.Objects[outsider.ProfilePhotoStorageKey] = Jpeg;

        var result = await Controller(db, storage, new FixedScope(member.Id), tenantId)
            .GetPhoto(outsider.Id, CancellationToken.None);

        Assert.IsType<ForbidResult>(result);
        Assert.Empty(storage.Reads);
    }

    [Fact]
    public async Task AnotherTenantsEmployee_IsNotFound_EvenForAnUnrestrictedCaller()
    {
        await using var db = CreateDb();
        var (tenantA, _, _) = await SeedAsync(db);
        var (_, foreign, _) = await SeedAsync(db);
        var storage = new RecordingStorage();
        storage.Objects[foreign.ProfilePhotoStorageKey] = Jpeg;

        var result = await Controller(db, storage, new UnrestrictedScope(), tenantA)
            .GetPhoto(foreign.Id, CancellationToken.None);

        Assert.IsType<NotFoundResult>(result);
        Assert.Empty(storage.Reads);
    }

    private static async Task<bool> Evaluate(PermissionRequirement requirement, ClaimsPrincipal user)
    {
        var context = new AuthorizationHandlerContext([requirement], user, null);
        await new PermissionAuthorizationHandler().HandleAsync(context);
        return context.HasSucceeded;
    }

    private static ClaimsPrincipal Caller(Guid tenantId, params string[] permissions) =>
        new(new ClaimsIdentity(
            new[] { new Claim("tenant_id", tenantId.ToString()), new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()) }
                .Concat(permissions.Select(p => new Claim("permission", p))),
            "test"));

    private static EmployeePhotoController Controller(ZayraDbContext db, IDocumentStorage storage, IDataScopeService scope, Guid tenantId) =>
        new(db, storage, scope)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = Caller(tenantId, "employees.read") },
            },
        };

    private static ZayraDbContext CreateDb() =>
        new(new DbContextOptionsBuilder<ZayraDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static async Task<(Guid TenantId, Employee Member, Employee Outsider)> SeedAsync(ZayraDbContext db)
    {
        var tenant = new Tenant { Name = "Photo tenant", Slug = $"photo-{Guid.NewGuid():N}" };
        db.Tenants.Add(tenant);
        Employee Make(string code) => new()
        {
            TenantId = tenant.Id, EmployeeCode = code, FullName = code, EnglishName = code, Status = "Active",
            JoiningDate = DateTime.UtcNow.AddYears(-1),
            ProfilePhotoStorageKey = $"{tenant.Id:N}/photos/{Guid.NewGuid():N}.jpg",
        };
        var member = Make("MEMBER");
        var outsider = Make("OUTSIDER");
        db.Employees.AddRange(member, outsider);
        await db.SaveChangesAsync();
        return (tenant.Id, member, outsider);
    }

    private sealed class FixedScope(params int[] allowed) : IDataScopeService
    {
        public Task<DataScope> ResolveAsync(ClaimsPrincipal caller, Guid tenantId, CancellationToken ct) =>
            Task.FromResult(new DataScope { Level = DataScopeLevel.Team, AllowedEmployeeIds = allowed });
    }

    private sealed class UnrestrictedScope : IDataScopeService
    {
        public Task<DataScope> ResolveAsync(ClaimsPrincipal caller, Guid tenantId, CancellationToken ct) =>
            Task.FromResult(new DataScope { Level = DataScopeLevel.Organization, AllowedEmployeeIds = null });
    }

    /// <summary>Tenant-prefix-enforcing in-memory store that records every read.</summary>
    private sealed class RecordingStorage : IDocumentStorage
    {
        public Dictionary<string, byte[]> Objects { get; } = new();
        public List<string> Reads { get; } = new();

        public Task<StoredDocument> SaveAsync(Guid tenantId, IFormFile file, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public string ResolvePath(string storageUrl) => storageUrl;

        public Task<byte[]> GetBytesAsync(Guid tenantId, string storageUrl, CancellationToken ct = default)
        {
            Reads.Add(storageUrl);
            if (!storageUrl.StartsWith($"{tenantId:N}/", StringComparison.Ordinal))
                throw new InvalidOperationException("Cross-tenant storage access denied.");
            return Objects.TryGetValue(storageUrl, out var bytes)
                ? Task.FromResult(bytes)
                : throw new FileNotFoundException(storageUrl);
        }
    }
}
