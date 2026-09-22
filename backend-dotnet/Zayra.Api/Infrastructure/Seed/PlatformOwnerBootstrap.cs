using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Auth;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Seed;

/// <summary>
/// One-time bootstrap of the FIRST platform operator (PlatformRoles.Owner). Every tenant, tenant
/// user, test and demo account is then created by that operator through the platform-admin API.
///
/// <para>Safe by construction: it does nothing unless an operator explicitly supplies
/// <c>PLATFORM_ADMIN_PASSWORD</c>, and nothing at all once any platform user exists — so it can only
/// ever create the first one. The PLATFORM_ADMIN_BOOTSTRAP gate and strong-password guard for
/// production/dedicated deployments are enforced in Program.cs before this is called.</para>
/// </summary>
public static class PlatformOwnerBootstrap
{
    public static async Task RunAsync(
        ZayraDbContext db,
        IPasswordHasher hasher,
        ILogger logger,
        CancellationToken ct = default)
    {
        if (await db.PlatformUsers.AnyAsync(ct))
            return;

        var email    = Environment.GetEnvironmentVariable("PLATFORM_ADMIN_EMAIL") ?? "platform@kynex.one";
        var password = Environment.GetEnvironmentVariable("PLATFORM_ADMIN_PASSWORD");

        if (string.IsNullOrWhiteSpace(password))
        {
            logger.LogWarning("PlatformOwnerBootstrap: PLATFORM_ADMIN_PASSWORD not set — skipping platform owner seed.");
            return;
        }

        db.PlatformUsers.Add(new PlatformUser
        {
            Email        = email.Trim().ToLowerInvariant(),
            FullName     = "Platform Owner",
            PasswordHash = hasher.Hash(password),
            Role         = PlatformRoles.Owner,
            IsActive     = true,
            CreatedAtUtc = DateTime.UtcNow
        });
        await db.SaveChangesAsync(ct);
        logger.LogInformation("PlatformOwnerBootstrap: seeded platform owner {Email}", email);
    }
}
