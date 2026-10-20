using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Recruitment;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Email;

namespace Zayra.Api.Infrastructure.Recruitment;

public sealed class OfferMailReadinessService(ZayraDbContext db, IEmailService email, OfferLifecycleService lifecycle)
{
    public async Task<OfferMailReadiness> GetReadinessAsync(OfferActor actor, CancellationToken ct = default)
    {
        var sender = await email.GetEffectiveSenderAsync(actor.TenantId, ct);
        var active = await db.Tenants.AsNoTracking().AnyAsync(t => t.Id == actor.TenantId && t.IsActive, ct);
        return new(sender.Configured, sender.Mode, sender.Source, sender.FromAddress, sender.FromName, false,
            !active ? "Workspace is inactive." : !lifecycle.Enabled ? "Managed offer lifecycle is disabled."
            : sender.Mode == "Capture" ? "Testing: messages are captured and do not issue offers."
            : !sender.Configured ? "No effective SMTP relay is configured." : null,
            sender.ConfigurationFingerprint, sender.RecipientRestricted);
    }
}
