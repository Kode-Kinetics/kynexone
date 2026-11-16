using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Zayra.Api.Application.Auth;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.Auth;
using Zayra.Api.Models;

namespace Zayra.Api.Tests.Security;

/// <summary>
/// Two-step sign-in failed-attempt lockout through the real pipeline. Each challenge allows a handful of wrong codes, but a person who
/// knows the password can ask for a new challenge as often as they like, so the per-account count of wrong codes has to stop them:
/// after ten the account is locked for the tenant's lockout period, a new sign-in is refused, and a correct code or an administrator's
/// unlock clears it.
/// </summary>
public sealed class MfaFailedAttemptLockoutHttpTests(LaunchSecurityFixture f) : IClassFixture<LaunchSecurityFixture>
{
    private const string Password = "Correct-Horse-9!";

    private async Task<(string Email, Guid UserId)> SeedMfaUserAsync()
    {
        var email = $"mfa-{Guid.NewGuid():N}@launch.local";
        var id = Guid.NewGuid();
        await f.SeedAsync(async db =>
        {
            using var scope = f.Host.Services.CreateScope();
            var hasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher>();
            var totp = scope.ServiceProvider.GetRequiredService<TotpService>();
            var role = await db.Roles.SingleAsync(r => r.TenantId == f.TenantId && r.Name == "Employee");
            db.Users.Add(new User
            {
                Id = id, TenantId = f.TenantId, Email = email, NormalizedEmail = email.ToUpperInvariant(), FullName = "Mfa Person",
                PasswordHash = hasher.Hash(Password), Status = "Active", AccessMode = AccessModes.FullPortal, IsActive = true, IsEmailConfirmed = true,
                MFAEnabled = true, MfaSecretEncrypted = totp.EncryptSecret(totp.GenerateBase32Secret()),
            });
            db.UserRoles.Add(new UserRole { UserId = id, RoleId = role.Id });
        });
        return (email, id);
    }

    private async Task<HttpResponseMessage> LoginAsync(string email) =>
        await f.Client.PostAsJsonAsync("/api/auth/login", new { email, password = Password, tenantSlug = $"launch-{f.TenantId:N}" });

    /// <summary>Signs in, then sends <paramref name="wrongCodes"/> wrong codes against the challenge it was given.</summary>
    private async Task BurnChallengeAsync(string email, int wrongCodes)
    {
        using var login = await LoginAsync(email);
        var body = await login.Content.ReadAsStringAsync();
        login.StatusCode.Should().Be(HttpStatusCode.OK, body);
        var challenge = JsonDocument.Parse(body).RootElement.GetProperty("challengeToken").GetString();
        for (var i = 0; i < wrongCodes; i++)
        {
            using var attempt = await f.Client.PostAsJsonAsync("/api/auth/mfa/challenge/verify", new { challengeToken = challenge, totpCode = "000000" });
            ((int)attempt.StatusCode).Should().BeInRange(400, 401, await attempt.Content.ReadAsStringAsync());
        }
    }

    [Fact]
    public async Task Ten_wrong_codes_across_challenges_lock_the_account_and_refuse_a_new_sign_in()
    {
        var (email, userId) = await SeedMfaUserAsync();
        await BurnChallengeAsync(email, 5);
        await BurnChallengeAsync(email, 5);

        var locked = await f.ReadAsync(db => db.Users.IgnoreQueryFilters().AsNoTracking().Where(u => u.Id == userId)
            .Select(u => new { u.MfaFailedCount, u.LockoutEnd, u.IsLocked }).SingleAsync());
        locked.MfaFailedCount.Should().BeGreaterThanOrEqualTo(10);
        locked.LockoutEnd.Should().NotBeNull("ten wrong codes must lock the account, not just be counted");
        locked.LockoutEnd!.Value.Should().BeAfter(DateTime.UtcNow);

        using var third = await LoginAsync(email);
        var text = await third.Content.ReadAsStringAsync();
        third.StatusCode.Should().NotBe(HttpStatusCode.OK, "a locked account is not given another challenge: " + text);
        text.Should().NotContain("challengeToken");
    }

    [Fact]
    public async Task Nine_wrong_codes_do_not_lock_the_account()
    {
        var (email, userId) = await SeedMfaUserAsync();
        await BurnChallengeAsync(email, 5);
        await BurnChallengeAsync(email, 4);

        var state = await f.ReadAsync(db => db.Users.IgnoreQueryFilters().AsNoTracking().Where(u => u.Id == userId)
            .Select(u => new { u.LockoutEnd, u.IsLocked }).SingleAsync());
        state.LockoutEnd.Should().BeNull();
        state.IsLocked.Should().BeFalse();
        using var next = await LoginAsync(email);
        next.StatusCode.Should().Be(HttpStatusCode.OK, "below the limit a new challenge is still offered");
    }
}
