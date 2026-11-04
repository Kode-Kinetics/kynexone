using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Zayra.Api.Infrastructure.Email;
using Zayra.Api.Models;

namespace Zayra.Api.Tests.Platform;

/// <summary>Tenant and platform SMTP relays are strictly separate: neither resolves through the other.</summary>
public class EmailRelaySeparationTests : PlatformTestBase
{
    private static SmtpEmailService Service(Zayra.Api.Data.ZayraDbContext db) =>
        new(db, new EphemeralDataProtectionProvider(), new ConfigurationBuilder().Build(),
            NullLogger<SmtpEmailService>.Instance);

    private static void Platform(Zayra.Api.Data.ZayraDbContext db)
    {
        db.PlatformConfigEntries.Add(new PlatformConfigEntry { Key = PlatformSmtpConfig.KeyHost, Value = "smtp.platform.test" });
        db.PlatformConfigEntries.Add(new PlatformConfigEntry { Key = PlatformSmtpConfig.KeyFromAddress, Value = "p@platform.test" });
        db.SaveChanges();
    }

    private static void Tenant(Zayra.Api.Data.ZayraDbContext db, Guid tenantId, string? from)
    {
        void Add(string k, string v) => db.SystemSettings.Add(new SystemSetting { TenantId = tenantId, Category = "Email", SettingKey = k, SettingValue = v });
        Add("Smtp.Host", "smtp.tenant.test");
        if (from is not null) Add("Smtp.FromAddress", from);
        db.SaveChanges();
    }

    [Fact]
    public async Task Tenant_WithoutOwnRelay_IsNotConfigured_EvenWhenPlatformRelayExists()
    {
        using var db = CreateDb();
        Platform(db);
        var svc = Service(db);

        (await svc.IsConfiguredAsync(Guid.NewGuid())).Should().BeFalse();
        (await svc.IsPlatformConfiguredAsync()).Should().BeTrue();
    }

    [Fact]
    public async Task Tenant_WithBlankFromAddress_IsNotConfigured_AndDoesNotBorrowPlatform()
    {
        using var db = CreateDb();
        Platform(db);
        var tid = Guid.NewGuid();
        Tenant(db, tid, from: null);

        (await Service(db).IsConfiguredAsync(tid)).Should().BeFalse();
    }

    [Fact]
    public async Task Tenant_WithOwnRelay_IsConfigured_AndPlatformStaysSeparate()
    {
        using var db = CreateDb();
        var tid = Guid.NewGuid();
        Tenant(db, tid, from: "hr@tenant.test");
        var svc = Service(db);

        (await svc.IsConfiguredAsync(tid)).Should().BeTrue();
        (await svc.IsPlatformConfiguredAsync()).Should().BeFalse();
    }
}
