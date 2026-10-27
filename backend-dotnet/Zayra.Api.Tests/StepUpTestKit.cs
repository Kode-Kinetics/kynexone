using Microsoft.AspNetCore.DataProtection;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Audit;
using Zayra.Api.Infrastructure.Auth;

namespace Zayra.Api.Tests;

/// <summary>
/// "Confirm it's you" for tests that are about something else: an AccessController built with
/// <see cref="AlwaysConfirmed"/> behaves as if the caller just confirmed. StepUpTests covers the real check.
/// </summary>
internal static class StepUpTestKit
{
    public static readonly IDataProtectionProvider Protection = DataProtectionProvider.Create("ZayraStepUpTests");

    public static StepUpService Real(ZayraDbContext db) =>
        new(db, new Pbkdf2PasswordHasher(), new TotpService(Protection), new AuditService(db), Protection);

    public static StepUpService AlwaysConfirmed(ZayraDbContext db) => new Confirmed(db);

    private sealed class Confirmed(ZayraDbContext db)
        : StepUpService(db, new Pbkdf2PasswordHasher(), new TotpService(Protection), new AuditService(db), Protection)
    {
        public override Task<bool> IsConfirmedAsync(Guid userId, Guid tenantId, string? token, CancellationToken cancellationToken) =>
            Task.FromResult(true);
    }
}
