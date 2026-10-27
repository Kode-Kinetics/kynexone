using System.Security.Claims;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Zayra.Api.Application.Employees.Ess;
using Zayra.Api.Infrastructure.Auth.StepUp;

namespace Zayra.Api.Tests.Ess;

/// <summary>C-2 step-up: the stateless token. Credential checking is covered end to end in EssCompletionHttpTests.</summary>
public class StepUpServiceTests
{
    private static readonly Guid User = Guid.NewGuid(), Tenant = Guid.NewGuid();
    private static readonly DateTime T0 = new(2026, 10, 27, 12, 0, 0, DateTimeKind.Utc);
    private const string Key = "unit-test-signing-key-unit-test-signing-key-unit-test-signing-key-0123456789";

    private static StepUpService Service(Func<DateTime> clock, string key = Key) => new(null!, null!, null!,
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Jwt:SigningKey"] = key }).Build(), null, clock);

    [Fact]
    public void StepUp_TokenExpired_Tampered_WrongPurpose_WrongUser_Rejected()
    {
        var now = T0;
        var svc = Service(() => now);
        var token = svc.Issue(User, Tenant, StepUpPurposes.EssSensitive, T0);

        svc.IsFresh(token, User, Tenant, StepUpPurposes.EssSensitive, StepUpPurposes.EssSensitiveMaxAge).Should().BeTrue();
        now = T0.AddMinutes(9);
        svc.IsFresh(token, User, Tenant, StepUpPurposes.EssSensitive, StepUpPurposes.EssSensitiveMaxAge).Should().BeTrue();

        // expired: older than the caller's max age
        now = T0.AddMinutes(11);
        svc.IsFresh(token, User, Tenant, StepUpPurposes.EssSensitive, StepUpPurposes.EssSensitiveMaxAge).Should().BeFalse();
        // a stricter caller (esign: 5 min) refuses what ess.sensitive would still accept
        now = T0.AddMinutes(6);
        var esign = svc.Issue(User, Tenant, StepUpPurposes.Esign, T0);
        svc.IsFresh(esign, User, Tenant, StepUpPurposes.Esign, StepUpPurposes.EsignMaxAge).Should().BeFalse();
        // a token from the future is not fresh either (clock-skew abuse)
        now = T0.AddMinutes(-5);
        svc.IsFresh(token, User, Tenant, StepUpPurposes.EssSensitive, StepUpPurposes.EssSensitiveMaxAge).Should().BeFalse();

        now = T0.AddMinutes(1);
        // tampered: flip one character of the payload and of the signature
        var parts = token.Split('.');
        parts.Length.Should().Be(2);
        var payloadFlip = (parts[0][0] == 'A' ? "B" : "A") + parts[0][1..] + "." + parts[1];
        var sigFlip = parts[0] + "." + (parts[1][0] == 'A' ? "B" : "A") + parts[1][1..];
        foreach (var bad in new[] { payloadFlip, sigFlip, "", "garbage", token + "x", parts[0], "a.b.c", null })
            svc.IsFresh(bad, User, Tenant, StepUpPurposes.EssSensitive, StepUpPurposes.EssSensitiveMaxAge).Should().BeFalse(because: bad ?? "null");

        // wrong purpose / wrong user
        svc.IsFresh(token, User, Tenant, StepUpPurposes.Esign, StepUpPurposes.EsignMaxAge).Should().BeFalse();
        svc.IsFresh(token, Guid.NewGuid(), Tenant, StepUpPurposes.EssSensitive, StepUpPurposes.EssSensitiveMaxAge).Should().BeFalse();
        // signed with another key
        Service(() => now, Key + "-other").IsFresh(token, User, Tenant, StepUpPurposes.EssSensitive, StepUpPurposes.EssSensitiveMaxAge).Should().BeFalse();
    }

    [Fact]
    public void StepUp_BindsTenantAndUser()
    {
        var svc = Service(() => T0.AddSeconds(30));
        var token = svc.Issue(User, Tenant, StepUpPurposes.EssSensitive, T0);
        svc.IsFresh(token, User, Guid.NewGuid(), StepUpPurposes.EssSensitive, TimeSpan.FromMinutes(10)).Should().BeFalse("another tenant");
        svc.IsFresh(token, Guid.NewGuid(), Tenant, StepUpPurposes.EssSensitive, TimeSpan.FromMinutes(10)).Should().BeFalse("another user");

        // The principal overload reads sub + tenant_id the way the rest of the API does.
        var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, User.ToString()), new Claim("tenant_id", Tenant.ToString())], "t"));
        var other = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()), new Claim("tenant_id", Tenant.ToString())], "t"));
        svc.IsFresh(token, principal, StepUpPurposes.EssSensitive, TimeSpan.FromMinutes(10)).Should().BeTrue();
        svc.IsFresh(token, other, StepUpPurposes.EssSensitive, TimeSpan.FromMinutes(10)).Should().BeFalse();
        svc.IsFresh(token, new ClaimsPrincipal(new ClaimsIdentity()), StepUpPurposes.EssSensitive, TimeSpan.FromMinutes(10)).Should().BeFalse();

        // Each token has its own jti (P4 uses it as a single-use ledger key), readable only for the right binding.
        var second = svc.Issue(User, Tenant, StepUpPurposes.EssSensitive, T0);
        var j1 = svc.ReadJti(token, User, Tenant, StepUpPurposes.EssSensitive);
        var j2 = svc.ReadJti(second, User, Tenant, StepUpPurposes.EssSensitive);
        j1.Should().NotBeNullOrEmpty(); j2.Should().NotBeNullOrEmpty(); j1.Should().NotBe(j2);
        svc.ReadJti(token, Guid.NewGuid(), Tenant, StepUpPurposes.EssSensitive).Should().BeNull();
        svc.ReadJti(token, User, Tenant, StepUpPurposes.Esign).Should().BeNull();
    }

    [Fact]
    public void StepUp_Purposes_AreTheFrozenSet()
    {
        StepUpPurposes.EssSensitive.Should().Be("ess.sensitive");
        StepUpPurposes.Esign.Should().Be("esign");
        StepUpPurposes.EssSensitiveMaxAge.Should().Be(TimeSpan.FromMinutes(10));
        StepUpPurposes.EsignMaxAge.Should().Be(TimeSpan.FromMinutes(5));
        StepUpPurposes.IsKnown("anything").Should().BeFalse();
    }
}
