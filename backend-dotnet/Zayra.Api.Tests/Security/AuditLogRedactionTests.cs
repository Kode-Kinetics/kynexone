using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Zayra.Api.Controllers;
using Zayra.Api.Domain.Entities;

namespace Zayra.Api.Tests.Security;

/// <summary>
/// Audit readers see who did what and when. Raw metadata, IP and user agent — which carry attempted
/// sign-in emails and client addresses — are returned only to callers holding security.manage.
/// </summary>
public sealed class AuditLogRedactionTests
{
    private static async Task<JsonElement> RecentAs(AuthHardeningTestKit kit, params string[] permissions)
    {
        await using var db = kit.NewDb();
        var claims = new List<Claim> { new("tenant_id", kit.TenantId.ToString()), new(ClaimTypes.Role, "Admin") };
        claims.AddRange(permissions.Select(p => new Claim("permission", p)));
        var controller = new AuditLogsController(db)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "test")) },
            },
        };
        var result = await controller.Recent(10, CancellationToken.None);
        return JsonSerializer.SerializeToElement(((OkObjectResult)result).Value, new JsonSerializerOptions(JsonSerializerDefaults.Web));
    }

    [Fact]
    public async Task RawMetadataAndAddresses_AreOnlyForSecurityManagers()
    {
        await using var kit = await AuthHardeningTestKit.CreateAsync();
        await using (var db = kit.NewDb())
        {
            db.AuditLogs.Add(new AuditLog
            {
                TenantId = kit.TenantId, Action = "auth.login_failed", EntityName = "User",
                Metadata = "{\"email\":\"attacker-guess@victim.test\",\"reason\":\"password_mismatch\"}",
                IpAddress = "198.51.100.23", UserAgent = "curl/8",
            });
            await db.SaveChangesAsync();
        }

        var reader = (await RecentAs(kit, "audit.read"))[0];
        reader.GetProperty("action").GetString().Should().Be("auth.login_failed");
        reader.GetProperty("metadata").ValueKind.Should().Be(JsonValueKind.Null);
        reader.GetProperty("ipAddress").ValueKind.Should().Be(JsonValueKind.Null);
        reader.GetProperty("userAgent").ValueKind.Should().Be(JsonValueKind.Null);
        reader.GetProperty("rawDetailRedacted").GetBoolean().Should().BeTrue();
        reader.ToString().Should().NotContain("attacker-guess").And.NotContain("198.51.100.23");

        var security = (await RecentAs(kit, "audit.read", "security.manage"))[0];
        security.GetProperty("metadata").GetString().Should().Contain("attacker-guess@victim.test");
        security.GetProperty("ipAddress").GetString().Should().Be("198.51.100.23");
        security.GetProperty("rawDetailRedacted").GetBoolean().Should().BeFalse();
    }
}
