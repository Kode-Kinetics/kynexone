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
        var (reason, reasonAr) = !active ? ("Workspace is inactive.", "مساحة العمل غير نشطة.")
            : !lifecycle.Enabled ? ("Managed offer lifecycle is disabled.", "دورة العروض المُدارة معطّلة.")
            : sender.Mode == "Capture" ? ("Testing: messages are captured and do not issue offers.", "وضع الاختبار: تُلتقط الرسائل ولا يصدر أي عرض.")
            : !sender.Configured ? ("No effective SMTP relay is configured.", "لا يوجد خادم بريد (SMTP) مُعدّ لمساحة العمل، لذلك لا يُرسل أي عرض ويبقى محجوزًا حتى يُضبط.")
            : ((string?)null, (string?)null);
        return new(sender.Configured, sender.Mode, sender.Source, sender.FromAddress, sender.FromName, false, reason,
            sender.ConfigurationFingerprint, sender.RecipientRestricted, reasonAr);
    }
}
