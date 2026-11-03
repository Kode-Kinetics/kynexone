using System.Text;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Controllers;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Models;

namespace Zayra.Api.Tests.Platform;

/// <summary>The account export hands a leaving customer their account, never their credentials.</summary>
public class TenantAccountExportTests : PlatformTestBase
{
    [Fact]
    public async Task Export_ContainsTheAccount_AndNeverACredential()
    {
        await using var db = CreateDb();
        var tenant = new Tenant { Id = Guid.NewGuid(), Name = "Alpha", Slug = "alpha", IsActive = true };
        db.Tenants.Add(tenant);
        db.TenantSubscriptions.Add(new TenantSubscription { TenantId = tenant.Id, Status = "Active" });
        db.Users.Add(new User { Id = Guid.NewGuid(), TenantId = tenant.Id, Email = "boss@alpha.com", FullName = "Boss", IsActive = true, Status = "Active", PasswordHash = "SECRET-HASH-VALUE" });
        db.TenantInvoices.Add(new TenantInvoice { TenantId = tenant.Id, InvoiceNumber = "INV-1", Amount = 10 });
        await db.SaveChangesAsync();

        var result = await CreateController(db).ExportTenantAccount(tenant.Id, default);

        var file = result.Should().BeOfType<FileContentResult>().Subject;
        file.ContentType.Should().Be("application/json");
        file.FileDownloadName.Should().StartWith("alpha-account-export-");
        var json = Encoding.UTF8.GetString(file.FileContents);
        json.Should().Contain("boss@alpha.com").And.Contain("INV-1").And.Contain("NOT included");
        json.Should().NotContain("SECRET-HASH-VALUE").And.NotContainEquivalentOf("passwordHash");
        (await db.AdminAuditLogs.CountAsync(l => l.TenantId == tenant.Id && l.Action == "TenantAccountExported")).Should().Be(1);
    }

    [Fact]
    public async Task Export_OfAnotherTenantsData_IsNeverMixedIn()
    {
        await using var db = CreateDb();
        var a = new Tenant { Id = Guid.NewGuid(), Name = "A", Slug = "a", IsActive = true };
        var b = new Tenant { Id = Guid.NewGuid(), Name = "B", Slug = "b", IsActive = true };
        db.Tenants.AddRange(a, b);
        db.Users.Add(new User { Id = Guid.NewGuid(), TenantId = b.Id, Email = "other@b.com", FullName = "Other", IsActive = true, Status = "Active" });
        await db.SaveChangesAsync();

        var file = (FileContentResult)await CreateController(db).ExportTenantAccount(a.Id, default);

        Encoding.UTF8.GetString(file.FileContents).Should().NotContain("other@b.com");
    }

    [Fact]
    public async Task Export_UnknownTenant_Is404_AndPurgedTenant_Is409()
    {
        await using var db = CreateDb();
        var purged = new Tenant { Id = Guid.NewGuid(), Name = "P", Slug = "p__deleted_12345678", IsActive = false, PurgedAtUtc = DateTime.UtcNow };
        db.Tenants.Add(purged);
        await db.SaveChangesAsync();
        var controller = CreateController(db);

        (await controller.ExportTenantAccount(Guid.NewGuid(), default)).Should().BeOfType<NotFoundObjectResult>();
        (await controller.ExportTenantAccount(purged.Id, default)).Should().BeOfType<ConflictObjectResult>();
    }
}
