using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Auth;
using Zayra.Api.Application.Common;
using Zayra.Api.Application.CountryPack;
using Zayra.Api.Controllers;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Authorization;
using Zayra.Api.Infrastructure.CountryPack;
using Zayra.Api.Infrastructure.Documents.Letters;
using Zayra.Api.Infrastructure.Notifications;
using Zayra.Api.Infrastructure.Seed;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>PAY-10 — payroll.slips.read reads the register and payslips; it never exports. payroll.export keeps working.</summary>
public class PayrollSlipsReadPermissionTests
{
    // L3: a single payslip PDF is a read of one person's slip, so slips.read keeps opening it. The run-wide ZIP of every
    // payslip is a bulk export of the whole run's pay and needs payroll.export (see the ratchet below).
    [Theory]
    [InlineData("DownloadSlipPdf")]
    [InlineData("Slips")]
    [InlineData("ListPayslips")]
    [InlineData("Register")]
    public void The_register_and_payslip_reads_accept_slips_read(string action) =>
        LegacyRolePermissionResolver.ResolveAllFor("Payroll", action, new[] { "GET" }).Should().Contain("payroll.slips.read");

    [Theory]
    [InlineData("DownloadSlipPdf")]
    [InlineData("DownloadRunPdfBundle")]
    public void The_pdf_reads_still_accept_payroll_export(string action) =>
        LegacyRolePermissionResolver.ResolveAllFor("Payroll", action, new[] { "GET" }).Should().Contain("payroll.export");

    [Theory]
    [InlineData("Slips")]
    [InlineData("Register")]
    public void The_json_reads_still_accept_payroll_read(string action) =>
        LegacyRolePermissionResolver.ResolveAllFor("Payroll", action, new[] { "GET" }).Should().Contain("payroll.read");

    [Fact]
    public void Slips_read_is_not_accepted_for_any_export_or_write_action()
    {
        foreach (var (action, verb) in new[] { ("GenerateWpsFile", "POST"), ("Approve", "POST"), ("ExportStructures", "GET"), ("Process", "POST"), ("DownloadRunPdfBundle", "GET") })
            LegacyRolePermissionResolver.ResolveAllFor("Payroll", action, new[] { verb }).Should().NotContain("payroll.slips.read", action);
    }

    [Fact]
    public void The_bulk_payslip_bundle_is_gated_on_payroll_export_alone()
        => LegacyRolePermissionResolver.ResolveAllFor("Payroll", "DownloadRunPdfBundle", new[] { "GET" }).Should().Equal("payroll.export");

    [Fact]
    public async Task A_role_holding_only_slips_read_cannot_export_the_register()
    {
        var (db, tenant) = await SeedTenantAsync();
        var c = Ctrl(db, tenant, "payroll.slips.read");
        (await c.ExportRegister(Guid.NewGuid(), CancellationToken.None)).Should().BeOfType<ForbidResult>();
    }

    [Fact]
    public async Task The_seeded_payroll_roles_hold_slips_read_and_admin_holds_it_too()
    {
        var (db, tenant) = await SeedTenantAsync();
        foreach (var role in new[] { "Admin", "HR Manager", "Payroll Manager", "Payroll Officer" })
            (await RolePermissionsAsync(db, tenant, role)).Should().Contain("payroll.slips.read", role);
        // slips.read widens nothing for roles that could not reach payslips before.
        (await RolePermissionsAsync(db, tenant, "Employee")).Should().NotContain("payroll.slips.read");
    }

    private static async Task<string[]> RolePermissionsAsync(ZayraDbContext db, Guid tenantId, string role) =>
        (await db.Roles.Where(r => r.TenantId == tenantId && r.Name == role)
            .SelectMany(r => r.RolePermissions.Select(rp => rp.Permission!.Key)).ToListAsync()).ToArray();

    private static async Task<(ZayraDbContext Db, Guid TenantId)> SeedTenantAsync()
    {
        var db = new ZayraDbContext(new DbContextOptionsBuilder<ZayraDbContext>()
            .UseInMemoryDatabase("slips-read-" + Guid.NewGuid().ToString("N")).Options);
        var tenantId = Guid.NewGuid();
        db.Tenants.Add(new Zayra.Api.Domain.Entities.Tenant { Id = tenantId, Name = "Slips", Slug = $"sr-{Guid.NewGuid():N}"[..20] });
        await db.SaveChangesAsync();
        await new AuthSeeder(db).EnsureTenantRolesAsync(tenantId, CancellationToken.None);
        return (db, tenantId);
    }

    private static PayrollController Ctrl(ZayraDbContext db, Guid tenantId, params string[] permissions)
    {
        var claims = new List<Claim> { new("tenant_id", tenantId.ToString()), new(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()) };
        claims.AddRange(permissions.Select(p => new Claim("permission", p)));
        var httpCtx = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "test")) };
        var ctrl = new PayrollController(db, new _SlpUnrestrictedScope(), new _SlpHttpAccessor(httpCtx), new _SlpNullNotifications(),
            new _SlpNullPackResolver(), new StubRuleReader(), new _SlpNullLetterService(), new _SlpNullDocStorage(),
            new Zayra.Api.Infrastructure.Documents.PdfRenderGate(4));
        ctrl.ControllerContext = new ControllerContext { HttpContext = httpCtx };
        return ctrl;
    }
}

file sealed class _SlpUnrestrictedScope : IDataScopeService
{
    public Task<DataScope> ResolveAsync(ClaimsPrincipal caller, Guid tenantId, CancellationToken ct)
        => Task.FromResult(new DataScope { Level = DataScopeLevel.Organization });
}

file sealed class _SlpHttpAccessor : IHttpContextAccessor
{
    public _SlpHttpAccessor(HttpContext ctx) => HttpContext = ctx;
    public HttpContext? HttpContext { get; set; }
}

file sealed class _SlpNullNotifications : INotificationService
{
    public Task NotifyAsync(Guid t, Guid? u, string title, string msg, string en, string? eid, CancellationToken ct) => Task.CompletedTask;
    public Task SendEmailAsync(Guid t, string code, string to, string name, Dictionary<string, string> vars, CancellationToken ct) => Task.CompletedTask;
}

file sealed class _SlpNullPackResolver : ICountryPackResolver
{
    public IStatutoryDeductionCalculator ResolveDeductionCalculator(string cc, string j)
        => new DefaultStatutoryDeductionCalculator();
    public IEndOfServiceCalculator ResolveEndOfServiceCalculator(string cc, string j)
        => new DefaultEndOfServiceCalculator();
    public IWageProtectionExporter ResolveWageProtectionExporter(string cc, string j)
        => new DefaultWageProtectionExporter();
    public INationalizationTracker ResolveNationalizationTracker(string cc, string j)
        => new DefaultNationalizationTracker();
    public ILocalizationProfile ResolveLocalizationProfile(string cc, string j)
        => new DefaultLocalizationProfile();
    public ICountryPackDescriptor ResolveDescriptor(string cc, string j)
        => new DefaultCountryPackDescriptor();
}

file sealed class _SlpNullLetterService : ILetterService
{
    public Task<byte[]> GeneratePayslipPdfAsync(PayslipData d, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
    public Task<byte[]> GenerateAppointmentLetterAsync(LetterData d, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
    public Task<byte[]> GenerateExperienceLetterAsync(LetterData d, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
    public Task<byte[]> GenerateOfferLetterAsync(OfferLetterData d, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
}

file sealed class _SlpNullDocStorage : Zayra.Api.Infrastructure.Documents.IDocumentStorage
{
    public Task<Zayra.Api.Infrastructure.Documents.StoredDocument> SaveAsync(Guid tenantId, Microsoft.AspNetCore.Http.IFormFile file, CancellationToken ct)
        => Task.FromResult(new Zayra.Api.Infrastructure.Documents.StoredDocument(file.FileName, file.ContentType, "storage/test", "/tmp/test"));
    public Task<byte[]> GetBytesAsync(Guid tenantId, string storageUrl, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
    public string ResolvePath(string storageUrl) => "/tmp/test";
}
