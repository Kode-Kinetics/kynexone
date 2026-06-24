using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Controllers;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Models;

namespace Zayra.Api.Tests.Platform;

/// <summary>
/// Covers the soft-delete lifecycle (restore / purge) and the invoicing best-practice rules
/// (auto-numbering, uniqueness, issued-invoice immutability, void-not-delete).
/// </summary>
public class PlatformLifecycleInvoiceTests : PlatformTestBase
{
    private static async Task<Tenant> SeedTenant(Zayra.Api.Data.ZayraDbContext db, string slug, bool active = true, string status = "Active")
    {
        var t = new Tenant { Id = Guid.NewGuid(), Name = slug, Slug = slug, IsActive = active };
        db.Tenants.Add(t);
        db.TenantSubscriptions.Add(new TenantSubscription { TenantId = t.Id, Status = status });
        await db.SaveChangesAsync();
        return t;
    }

    private static async Task<Tenant> SeedSoftDeleted(Zayra.Api.Data.ZayraDbContext db, string baseSlug)
    {
        var t = new Tenant { Id = Guid.NewGuid(), Name = baseSlug, Slug = $"{baseSlug}__deleted_{Guid.NewGuid().ToString("N")[..8]}", IsActive = false };
        db.Tenants.Add(t);
        db.TenantSubscriptions.Add(new TenantSubscription { TenantId = t.Id, Status = "Cancelled" });
        await db.SaveChangesAsync();
        return t;
    }

    // ── Restore ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task RestoreTenant_ReactivatesAndRestoresSlug()
    {
        await using var db = CreateDb();
        var t = await SeedSoftDeleted(db, "acme");
        var controller = CreateController(db);

        var result = await controller.RestoreTenant(t.Id, CancellationToken.None);

        result.Should().BeOfType<OkObjectResult>();
        var stored = await db.Tenants.FindAsync(t.Id);
        stored!.IsActive.Should().BeTrue();
        stored.Slug.Should().Be("acme");
        (await db.TenantSubscriptions.FirstAsync(s => s.TenantId == t.Id)).Status.Should().Be("Active");
    }

    [Fact]
    public async Task RestoreTenant_OnActiveTenant_Returns400()
    {
        await using var db = CreateDb();
        var t = await SeedTenant(db, "live");
        var controller = CreateController(db);

        var result = await controller.RestoreTenant(t.Id, CancellationToken.None);

        result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task RestoreTenant_WhenOriginalSlugTaken_UsesSuffixedSlug()
    {
        await using var db = CreateDb();
        await SeedTenant(db, "acme");                 // slug now taken by a live tenant
        var deleted = await SeedSoftDeleted(db, "acme");
        var controller = CreateController(db);

        await controller.RestoreTenant(deleted.Id, CancellationToken.None);

        var stored = await db.Tenants.FindAsync(deleted.Id);
        stored!.IsActive.Should().BeTrue();
        stored.Slug.Should().StartWith("acme-restored-");
    }

    [Fact]
    public async Task BulkRestore_RestoresSelected_SkipsActive()
    {
        await using var db = CreateDb();
        var d1 = await SeedSoftDeleted(db, "alpha");
        var live = await SeedTenant(db, "beta");
        var controller = CreateController(db);

        var result = await controller.BulkRestoreTenants(
            new BulkTenantActionRequest(new List<Guid> { d1.Id, live.Id }, null), CancellationToken.None);

        var json = JsonSerializer.Serialize(((OkObjectResult)result).Value);
        json.Should().Contain("\"succeeded\":1");
        json.Should().Contain("\"skipped\":1");
    }

    // ── Purge (validation paths; ExecuteDelete itself isn't InMemory-runnable) ─

    [Fact]
    public async Task PurgeTenant_WithoutConfirm_Returns400()
    {
        await using var db = CreateDb();
        var t = await SeedSoftDeleted(db, "gone");
        var controller = CreateController(db);

        var result = await controller.PurgeTenant(t.Id, confirm: null, CancellationToken.None);

        result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task PurgeTenant_OnActiveTenant_Returns400_EvenWithConfirm()
    {
        await using var db = CreateDb();
        var t = await SeedTenant(db, "live");
        var controller = CreateController(db);

        var result = await controller.PurgeTenant(t.Id, confirm: "PURGE", CancellationToken.None);

        result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public void PurgeTenant_RequiresOwnerRole()
    {
        var method = typeof(PlatformController).GetMethod(nameof(PlatformController.PurgeTenant))!;
        var attr = (Zayra.Api.Infrastructure.Auth.RequirePlatformRoleAttribute?)Attribute.GetCustomAttribute(
            method, typeof(Zayra.Api.Infrastructure.Auth.RequirePlatformRoleAttribute));
        attr.Should().NotBeNull();
        attr!.Roles.Should().Contain(PlatformRoles.Owner);
        attr.Roles.Should().NotContain(PlatformRoles.Admin);
    }

    // ── Invoicing best practice ──────────────────────────────────────────────

    private static CreateInvoiceRequest Inv(string? number, decimal amount = 100, string status = "Draft") =>
        new(number, amount, "USD", status, null, null, null,
            DateOnly.FromDateTime(DateTime.UtcNow), DateOnly.FromDateTime(DateTime.UtcNow.AddDays(30)), null, null, null);

    [Fact]
    public async Task CreateInvoice_AutoGeneratesSequentialNumber_WhenBlank()
    {
        await using var db = CreateDb();
        var t = await SeedTenant(db, "biz");
        var controller = CreateController(db);

        var r1 = await controller.CreateInvoice(t.Id, Inv(null), CancellationToken.None);
        var r2 = await controller.CreateInvoice(t.Id, Inv(null), CancellationToken.None);

        var n1 = ((TenantInvoice)((CreatedAtActionResult)r1).Value!).InvoiceNumber;
        var n2 = ((TenantInvoice)((CreatedAtActionResult)r2).Value!).InvoiceNumber;
        var year = DateTime.UtcNow.Year;
        n1.Should().Be($"INV-{year}-0001");
        n2.Should().Be($"INV-{year}-0002");
    }

    [Fact]
    public async Task CreateInvoice_DuplicateNumber_Returns409()
    {
        await using var db = CreateDb();
        var t = await SeedTenant(db, "biz");
        var controller = CreateController(db);
        await controller.CreateInvoice(t.Id, Inv("INV-CUSTOM-1"), CancellationToken.None);

        var dup = await controller.CreateInvoice(t.Id, Inv("INV-CUSTOM-1"), CancellationToken.None);

        dup.Should().BeOfType<ConflictObjectResult>();
    }

    [Fact]
    public async Task AddInvoiceLine_OnIssuedInvoice_IsBlocked()
    {
        await using var db = CreateDb();
        var t = await SeedTenant(db, "biz");
        var controller = CreateController(db);
        var created = (CreatedAtActionResult)await controller.CreateInvoice(t.Id, Inv("INV-1", status: "Sent"), CancellationToken.None);
        var inv = (TenantInvoice)created.Value!;

        var result = await controller.AddInvoiceLine(t.Id, inv.Id,
            new AddInvoiceLineRequest("Seat", 1, 100, 0, 0, 0), CancellationToken.None);

        result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task AddInvoiceLine_OnDraft_Succeeds()
    {
        await using var db = CreateDb();
        var t = await SeedTenant(db, "biz");
        var controller = CreateController(db);
        var created = (CreatedAtActionResult)await controller.CreateInvoice(t.Id, Inv("INV-1", status: "Draft"), CancellationToken.None);
        var inv = (TenantInvoice)created.Value!;

        var result = await controller.AddInvoiceLine(t.Id, inv.Id,
            new AddInvoiceLineRequest("Seat", 2, 100, 0, 0.15m, 0), CancellationToken.None);

        result.Should().BeOfType<CreatedAtActionResult>();
    }

    [Fact]
    public async Task DeleteInvoice_IssuedInvoice_IsBlocked_DraftAllowed()
    {
        await using var db = CreateDb();
        var t = await SeedTenant(db, "biz");
        var controller = CreateController(db);
        var draft = (TenantInvoice)((CreatedAtActionResult)await controller.CreateInvoice(t.Id, Inv("INV-D", status: "Draft"), CancellationToken.None)).Value!;
        var sent  = (TenantInvoice)((CreatedAtActionResult)await controller.CreateInvoice(t.Id, Inv("INV-S", status: "Sent"), CancellationToken.None)).Value!;

        (await controller.DeleteInvoice(t.Id, sent.Id, CancellationToken.None)).Should().BeOfType<BadRequestObjectResult>();
        (await controller.DeleteInvoice(t.Id, draft.Id, CancellationToken.None)).Should().BeOfType<NoContentResult>();
    }
}
