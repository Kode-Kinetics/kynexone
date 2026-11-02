using Zayra.Api.Application.Common;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Auth;
using Zayra.Api.Controllers;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// The Finance side of the HR-makes / Finance-approves salary flow, for tests that need a staged package decided: a
/// <see cref="PayrollController"/> whose caller is a DIFFERENT login holding payroll.approve, and a seeded second approver for
/// the up-front "is there anyone who could approve this" check (<c>SecondApproverCheck</c>).
/// </summary>
internal static class SalaryApprovalKit
{
    public static PayrollController FinanceController(ZayraDbContext db, Guid tenantId, Guid? userId = null, params string[] permissions)
        => FinanceController(db, tenantId, userId, new NoopNotifications(), permissions);

    /// <summary>Records who was notified, for the tests that assert a person was told something.</summary>
    public sealed class RecordingNotifications : Zayra.Api.Infrastructure.Notifications.INotificationService
    {
        public List<(Guid? UserId, string Title, string Message)> Sent { get; } = new();
        public Task NotifyAsync(Guid tenantId, Guid? userId, string title, string message, string entityName, string? entityId, CancellationToken ct)
        { Sent.Add((userId, title, message)); return Task.CompletedTask; }
        public Task SendEmailAsync(Guid tenantId, string templateCode, string toAddress, string toName, Dictionary<string, string> variables, CancellationToken ct) => Task.CompletedTask;
    }

    public static PayrollController FinanceController(ZayraDbContext db, Guid tenantId, Guid? userId, Zayra.Api.Infrastructure.Notifications.INotificationService notifications, params string[] permissions)
    {
        var claims = new List<Claim>
        {
            new("tenant_id", tenantId.ToString()),
            new(ClaimTypes.NameIdentifier, (userId ?? Guid.NewGuid()).ToString()),
            new("permission", "payroll.approve"),
        };
        claims.AddRange(permissions.Select(p => new Claim("permission", p)));
        var httpCtx = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "test")) };
        var ctrl = new PayrollController(db, new UnrestrictedScope(), new Accessor(httpCtx), notifications, new NullPacks(),
            new StubRuleReader(), new NoopLetters(), new NullDocumentStorage(), new Zayra.Api.Infrastructure.Documents.PdfRenderGate(8));
        ctrl.ControllerContext = new ControllerContext { HttpContext = httpCtx };
        return ctrl;
    }

    /// <summary>Approves every pending salary change of the tenant as a fresh Finance login; asserts each decision succeeded.</summary>
    public static async Task ApprovePendingAsync(ZayraDbContext db, Guid tenantId, bool overrideGradeRange = false, string? reason = null)
    {
        var finance = FinanceController(db, tenantId);
        foreach (var id in await db.SalaryChangeRequests.AsNoTracking()
                     .Where(x => x.TenantId == tenantId && x.Status == SalaryChangeRequest.Pending).Select(x => x.Id).ToListAsync())
            Assert.IsType<OkObjectResult>(await finance.ApproveSalaryChange(id, overrideGradeRange ? new PayrollController.SalaryChangeDecisionRequest(reason, true) : null, CancellationToken.None));
    }

    /// <summary>Idempotent, synchronous: makes sure the tenant has a second login who could approve (builders of import/hire controllers call
    /// this so the up-front "is there anyone who could approve" check passes; tests of the check itself do not).</summary>
    public static void EnsureApprover(ZayraDbContext db, Guid tenantId, string permission = "payroll.approve")
    {
        if (db.Database.IsNpgsql() && !db.Tenants.IgnoreQueryFilters().Any(t => t.Id == tenantId)) return;
        if (db.UserPermissionOverrides.IgnoreQueryFilters().Any(o => o.TenantId == tenantId && o.PermissionKey == permission && o.IsActive)) return;
        var user = new User { TenantId = tenantId, Email = $"approver-{Guid.NewGuid():N}@test.local", NormalizedEmail = $"APPROVER-{Guid.NewGuid():N}@TEST.LOCAL", FullName = "Second Approver", IsGroupScope = true };
        db.Users.Add(user);
        db.UserPermissionOverrides.Add(new UserPermissionOverride { TenantId = tenantId, UserId = user.Id, PermissionKey = permission, Effect = "Allow" });
        // Many SQLite test worlds never seed the tenant row; the approver is scaffolding, so the tenant FK is not enforced for this one save.
        var sqlite = db.Database.IsSqlite();
        if (sqlite) db.Database.ExecuteSqlRaw("PRAGMA foreign_keys = OFF");
        try { db.SaveChanges(); }
        finally { if (sqlite) db.Database.ExecuteSqlRaw("PRAGMA foreign_keys = ON"); }
    }

    /// <summary>An active login that holds <paramref name="permission"/> through an Allow override, with group scope.</summary>
    public static async Task<User> SeedApproverAsync(ZayraDbContext db, Guid tenantId, string permission = "payroll.approve", bool groupScope = true)
    {
        var user = new User { TenantId = tenantId, Email = $"approver-{Guid.NewGuid():N}@test.local", NormalizedEmail = $"APPROVER-{Guid.NewGuid():N}@TEST.LOCAL", FullName = "Second Approver", IsGroupScope = groupScope };
        db.Users.Add(user);
        db.UserPermissionOverrides.Add(new UserPermissionOverride { TenantId = tenantId, UserId = user.Id, PermissionKey = permission, Effect = "Allow" });
        await db.SaveChangesAsync();
        return user;
    }

    private sealed class UnrestrictedScope : IDataScopeService
    {
        public Task<DataScope> ResolveAsync(ClaimsPrincipal caller, Guid tenantId, CancellationToken ct)
            => Task.FromResult(new DataScope { Level = DataScopeLevel.Organization, AllowedEmployeeIds = null });
    }

    private sealed class Accessor(HttpContext ctx) : IHttpContextAccessor { public HttpContext? HttpContext { get; set; } = ctx; }

    private sealed class NoopNotifications : Zayra.Api.Infrastructure.Notifications.INotificationService
    {
        public Task NotifyAsync(Guid tenantId, Guid? userId, string title, string message, string entityName, string? entityId, CancellationToken ct) => Task.CompletedTask;
        public Task SendEmailAsync(Guid tenantId, string templateCode, string toAddress, string toName, Dictionary<string, string> variables, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class NullPacks : Zayra.Api.Application.CountryPack.ICountryPackResolver
    {
        public Zayra.Api.Application.CountryPack.IStatutoryDeductionCalculator ResolveDeductionCalculator(string cc, string j) => new Zayra.Api.Infrastructure.CountryPack.DefaultStatutoryDeductionCalculator();
        public Zayra.Api.Application.CountryPack.IEndOfServiceCalculator ResolveEndOfServiceCalculator(string cc, string j) => new Zayra.Api.Infrastructure.CountryPack.DefaultEndOfServiceCalculator();
        public Zayra.Api.Application.CountryPack.IWageProtectionExporter ResolveWageProtectionExporter(string cc, string j) => new Zayra.Api.Infrastructure.CountryPack.DefaultWageProtectionExporter();
        public Zayra.Api.Application.CountryPack.INationalizationTracker ResolveNationalizationTracker(string cc, string j) => new Zayra.Api.Infrastructure.CountryPack.DefaultNationalizationTracker();
        public Zayra.Api.Application.CountryPack.ILocalizationProfile ResolveLocalizationProfile(string cc, string j) => new Zayra.Api.Infrastructure.CountryPack.DefaultLocalizationProfile();
        public Zayra.Api.Application.CountryPack.ICountryPackDescriptor ResolveDescriptor(string cc, string j) => new Zayra.Api.Infrastructure.CountryPack.DefaultCountryPackDescriptor();
    }

    private sealed class NoopLetters : Zayra.Api.Infrastructure.Documents.Letters.ILetterService
    {
        public Task<byte[]> GeneratePayslipPdfAsync(Zayra.Api.Infrastructure.Documents.Letters.PayslipData d, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
        public Task<byte[]> GenerateAppointmentLetterAsync(Zayra.Api.Infrastructure.Documents.Letters.LetterData d, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
        public Task<byte[]> GenerateExperienceLetterAsync(Zayra.Api.Infrastructure.Documents.Letters.LetterData d, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
        public Task<byte[]> GenerateOfferLetterAsync(Zayra.Api.Infrastructure.Documents.Letters.OfferLetterData d, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
    }
}
