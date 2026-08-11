using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Data;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Notifications;

public interface INotificationService
{
    Task NotifyAsync(Guid tenantId, Guid? userId, string title, string message, string entityName, string? entityId, CancellationToken cancellationToken);

    /// <summary>Render and dispatch a named notification template to a specific email address.</summary>
    Task SendEmailAsync(Guid tenantId, string templateCode, string toAddress, string toName, Dictionary<string, string> variables, CancellationToken cancellationToken);

    /// <summary>
    /// POD-D5 multi-channel entry point. ENQUEUE-ONLY: writes the in-app rows and the per-channel
    /// delivery ledger, then returns. It performs ZERO network I/O, so it can never fail — or HANG —
    /// a payroll operation. Returns the delivery rows it created (empty when nothing was enqueued).
    ///
    /// The default implementation degrades to the legacy single-channel notify, so the ~40 existing
    /// test doubles that implement this interface keep compiling untouched.
    /// </summary>
    async Task<IReadOnlyList<NotificationDelivery>> EnqueueAsync(NotificationRequest request, CancellationToken cancellationToken)
    {
        await NotifyAsync(request.TenantId, request.UserId, request.Title, request.Message,
            request.EntityName, request.EntityId, cancellationToken);
        return [];
    }
}

/// <summary>
/// POD-D5 — EMPLOYEE REACH.
///
/// WHAT CHANGED AND WHY
///
///  1. ENQUEUE-ONLY. The old NotifyAsync awaited SMTP inline, per recipient, on the request thread
///     with no timeout (MailKit defaults to 120 s). A 500-employee tenant against a black-holed
///     relay could stall a payroll Lock for hours while holding a pooled DbContext. All provider
///     I/O — the FIRST attempt included — now happens in NotificationDeliveryWorker.
///
///  2. ITS OWN DbContext. The old code called SaveChangesAsync on the CALLER's request-scoped
///     pooled context, flushing whatever the caller had staged and leaving failed entities tracked
///     on a shared context. Every write here happens on a context from a child scope.
///
///  3. IN-APP IS AUDIENCE-ROUTED. Writing only a Notification row was a black hole for employees:
///     ESS (EmployeeSelfServiceController) and mobile (MobileController) read EmployeeNotifications,
///     while Notification is read only by the admin bell. An employee-audience message now lands in
///     BOTH, so the "never lost" fallback is actually reachable by the person it is for.
///
///  4. INTERPOLATION ACTUALLY WORKS. The old Interpolate built "{{Key}}" while every seeded template
///     uses "{Key}" — so it never fired once, and a real phone would have received "{Period}"
///     literally. Fixed, plus a fail-closed guard: an outbound body with a surviving placeholder is
///     never sent.
///
///  5. NO AI. Every body is template-driven and deterministic; every channel decision comes from
///     tenant config plus employee preference. AI is opt-in advisory only and is not in this path.
/// </summary>
public class NotificationService : INotificationService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly INotificationRecipientResolver _recipients;
    private readonly ILogger<NotificationService> _log;

    public NotificationService(IServiceScopeFactory scopeFactory, INotificationRecipientResolver recipients,
        ILogger<NotificationService> log)
    {
        _scopeFactory = scopeFactory;
        _recipients = recipients;
        _log = log;
    }

    // ── Legacy surface (signature-compatible; ~25 call sites unchanged) ───────

    public Task NotifyAsync(Guid tenantId, Guid? userId, string title, string message, string entityName,
        string? entityId, CancellationToken cancellationToken)
        => EnqueueAsync(new NotificationRequest
        {
            TenantId = tenantId,
            UserId = userId,
            // No business event code at these call sites: derive a stable one from the entity so the
            // dedupe key still contains real business identity and never a GUID.
            EventCode = string.IsNullOrWhiteSpace(entityName) ? "GENERIC_NOTICE" : $"{entityName}.Notice",
            EntityName = entityName,
            EntityId = entityId,
            Title = title,
            Message = message,
        }, cancellationToken);

    public async Task SendEmailAsync(Guid tenantId, string templateCode, string toAddress, string toName,
        Dictionary<string, string> variables, CancellationToken cancellationToken)
    {
        NotificationRecipient? recipient = null;
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<ZayraDbContext>();
            recipient = await _recipients.ResolveByEmailAsync(db, tenantId, toAddress, cancellationToken);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Notification: recipient lookup for template {Code} failed.", templateCode);
        }

        await EnqueueAsync(new NotificationRequest
        {
            TenantId = tenantId,
            UserId = recipient?.UserId,
            EmployeeId = recipient?.EmployeeId,
            EventCode = templateCode,
            EntityName = variables.TryGetValue("EntityName", out var en) ? en : templateCode,
            EntityId = variables.TryGetValue("EntityId", out var eid) ? eid : null,
            Title = variables.TryGetValue("Subject", out var s) ? s : string.Empty,
            Message = variables.TryGetValue("Body", out var b) ? b : string.Empty,
            Variables = variables,
            // Only used when the address belongs to nobody in the tenant directory.
            ExternalEmail = recipient is null ? toAddress : null,
            ExternalName = recipient is null ? toName : null,
        }, cancellationToken);
    }

    // ── The pipeline ──────────────────────────────────────────────────────────

    public async Task<IReadOnlyList<NotificationDelivery>> EnqueueAsync(NotificationRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            return await EnqueueCoreAsync(request, cancellationToken);
        }
        catch (Exception ex)
        {
            // A notification must NEVER break the business operation that raised it. This is
            // stricter than the old best-effort catch: the failure is logged with the event code
            // so it is diagnosable, and nothing propagates to a payroll/leave/approval caller.
            _log.LogError(ex, "Notification enqueue failed for {EventCode} in tenant {TenantId}.",
                request.EventCode, request.TenantId);
            return [];
        }
    }

    private async Task<IReadOnlyList<NotificationDelivery>> EnqueueCoreAsync(NotificationRequest request,
        CancellationToken ct)
    {
        if (request.TenantId == Guid.Empty) return [];

        // A dedicated scope → a dedicated DbContext. Never the caller's, never inside its transaction.
        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ZayraDbContext>();

        var recipient = await _recipients.ResolveAsync(db, request.TenantId, request.UserId, request.EmployeeId, ct);
        if (recipient is null && !string.IsNullOrWhiteSpace(request.ExternalEmail))
            recipient = new NotificationRecipient
            {
                TenantId = request.TenantId,
                DisplayName = request.ExternalName ?? request.ExternalEmail!,
                Email = request.ExternalEmail,
            };

        // BROADCAST (no user, no employee, no external address): NotificationsController.Recent
        // shows UserId-null rows to EVERY user in the tenant. Fanning that to short channels would
        // resolve "all tenant contacts" and SMS the whole company. In-app only, enforced here.
        var isBroadcast = recipient is null;

        var templates = await LoadTemplatesAsync(db, request.TenantId, request.EventCode, ct);
        var inApp = RenderInApp(request, templates);

        var now = DateTime.UtcNow;
        var deliveries = new List<NotificationDelivery>();

        // 1. IN-APP FIRST, ALWAYS. This is the terminal fallback and it is written unconditionally,
        //    so a message can never be lost because a provider is missing.
        var notification = new Notification
        {
            TenantId = request.TenantId,
            UserId = recipient?.UserId,
            Channel = NotificationChannels.InApp,
            Title = inApp.Subject,
            Message = inApp.Body,
            EntityName = request.EntityName,
            EntityId = request.EntityId,
        };
        db.Notifications.Add(notification);

        EmployeeNotification? employeeNotification = null;
        if (recipient?.EmployeeId is { } empId)
        {
            // ESS web and mobile read THIS table, not Notifications.
            employeeNotification = new EmployeeNotification
            {
                TenantId = request.TenantId,
                EmployeeId = empId,
                Title = inApp.Subject,
                Body = inApp.Body,
                NotificationType = "Info",
            };
            db.EmployeeNotifications.Add(employeeNotification);
        }

        var inAppDelivery = NewDelivery(request, recipient, NotificationChannels.InApp, inApp.Subject, inApp.Body, now);
        inAppDelivery.Outcome = DeliveryOutcomes.Sent;
        inAppDelivery.CompletedAtUtc = now;
        inAppDelivery.NextAttemptAtUtc = null;
        inAppDelivery.ProviderName = "in-app";
        inAppDelivery.DestinationMasked = employeeNotification is not null ? "ESS + bell" : "bell";
        if (inApp.HadUnresolved)
        {
            inAppDelivery.ErrorCode = "unresolved_placeholder";
            inAppDelivery.ErrorMessage = "Template variables were missing; placeholders were stripped from the in-app body.";
        }
        deliveries.Add(inAppDelivery);

        if (!isBroadcast)
        {
            var preference = await LoadPreferenceAsync(db, request.TenantId, recipient!.EmployeeId, ct);

            foreach (var channel in new[] { NotificationChannels.Email, NotificationChannels.Sms,
                         NotificationChannels.WhatsApp, NotificationChannels.Push })
            {
                var row = SelectChannel(channel, recipient, templates, preference, request, now);
                if (row is not null) deliveries.Add(row);
            }
        }

        foreach (var row in deliveries)
        {
            row.NotificationId = notification.Id;
            row.EmployeeNotificationId = employeeNotification?.Id;
        }

        // 2. IDEMPOTENCY. Business-identity dedupe key, checked here and DB-enforced by the unique
        //    index on (TenantId, DedupeKey). No GUID takes part in the key, so a re-Lock / retried
        //    POST / double-click computes the SAME key and is refused instead of sending twice.
        var keys = deliveries.Select(d => d.DedupeKey).ToList();
        // IgnoreQueryFilters is intentional: enqueue runs on a child scope with no ambient tenant filter;
        // the WHERE pins the tenant explicitly.
        var alreadyQueued = await db.NotificationDeliveries.IgnoreQueryFilters().AsNoTracking()
            .Where(d => d.TenantId == request.TenantId && keys.Contains(d.DedupeKey))
            .Select(d => d.DedupeKey)
            .ToListAsync(ct);

        if (alreadyQueued.Count > 0)
        {
            var seen = alreadyQueued.ToHashSet(StringComparer.Ordinal);
            deliveries.RemoveAll(d => seen.Contains(d.DedupeKey));
            if (deliveries.Count == 0)
            {
                // Everything about this notification has already been enqueued — do not duplicate
                // the in-app rows either.
                db.Entry(notification).State = EntityState.Detached;
                if (employeeNotification is not null) db.Entry(employeeNotification).State = EntityState.Detached;
                return [];
            }
        }

        db.NotificationDeliveries.AddRange(deliveries);

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            // Lost a race against a concurrent enqueue. The other writer's rows stand; ours are
            // discarded. Never a duplicate send, never an exception to the business caller.
            _log.LogInformation(ex, "Notification {EventCode} for tenant {TenantId} was already enqueued concurrently.",
                request.EventCode, request.TenantId);
            return [];
        }

        return deliveries;
    }

    // ── Channel selection ─────────────────────────────────────────────────────

    /// <summary>
    /// Returns the delivery row to enqueue for this channel, or null when the channel is not
    /// selected at all (no opt-in). "Selected but unusable" is NOT null — it becomes a visible
    /// no_contact / suppressed row, because losing the reason is exactly the silence this pod removes.
    /// </summary>
    private static NotificationDelivery? SelectChannel(string channel, NotificationRecipient recipient,
        IReadOnlyList<NotificationTemplate> templates, EmployeeNotificationPreference? preference,
        NotificationRequest request, DateTime now)
    {
        var template = templates.FirstOrDefault(t =>
            t.Channel.Equals(channel, StringComparison.OrdinalIgnoreCase) && t.IsActive);

        if (channel == NotificationChannels.Email)
        {
            // Email is ON by default — that is exactly what the code did before this pod, and
            // turning it off silently would be a regression. An employee can opt out.
            if (recipient.EmployeeId.HasValue && preference is { EmailEnabled: false }) return null;

            var destination = recipient.Email;
            var (subject, body, unresolved) = RenderForChannel(channel, request, template, templates);
            var row = NewDelivery(request, recipient, channel, subject, body, now);

            if (string.IsNullOrWhiteSpace(destination))
                return Terminal(row, DeliveryOutcomes.NoContact, now, "no_contact",
                    "No email address on file for this recipient.");

            if (unresolved)
                return Terminal(row, DeliveryOutcomes.Failed, now, "unresolved_placeholder",
                    "Template variables were missing; refusing to email a body containing raw placeholders.");

            row.DestinationMasked = NotificationBodyPolicy.MaskEmail(destination);
            // Stored ONLY for an address with no directory subject to re-resolve from; cleared
            // by the worker the moment the delivery is terminal.
            row.DestinationRaw = recipient.UserId is null && recipient.EmployeeId is null ? destination! : string.Empty;
            row.NextAttemptAtUtc = now;
            return row;
        }

        // SHORT CHANNELS. Opt-in is explicit and dual-gated:
        //   (a) the tenant must have an ACTIVE NotificationTemplate row for (Code, Channel) — the
        //       existing admin template UI is the switch; and
        //   (b) the employee's preference must allow it. A MISSING preference row means OFF —
        //       EmployeeNotificationPreference has zero writers repo-wide, so the table is empty in
        //       every live tenant and its CLR defaults (PushEnabled = true, EmailEnabled = true)
        //       must never be mistaken for consent.
        if (template is null) return null;
        if (recipient.EmployeeId is null) return null;             // no employee ⇒ no phone/token of ours
        if (preference is null) return null;                       // no explicit opt-in on record
        if (!recipient.IsActiveEmployee) return null;              // terminated: stop the pushes

        var allowed = channel switch
        {
            NotificationChannels.Sms => preference.SmsEnabled,
            NotificationChannels.WhatsApp => preference.SmsEnabled,   // same contact + same consent surface
            NotificationChannels.Push => preference.PushEnabled,
            _ => false,
        };
        if (!allowed) return null;

        var (shortSubject, shortBody, shortUnresolved) = RenderForChannel(channel, request, template, templates);
        var shortRow = NewDelivery(request, recipient, channel, shortSubject, shortBody, now);

        if (shortUnresolved)
            return Terminal(shortRow, DeliveryOutcomes.Failed, now, "unresolved_placeholder",
                "Template variables were missing; refusing to send a body containing raw placeholders.");

        // FINAL PRIVACY BACKSTOP. An SMS/WhatsApp/push body must say a payslip is READY — never what
        // it is worth. This catches a hand-edited tenant template that interpolated an amount.
        if (NotificationBodyPolicy.ContainsMonetaryToken(shortBody) || NotificationBodyPolicy.ContainsMonetaryToken(shortSubject))
            return Terminal(shortRow, DeliveryOutcomes.Suppressed, now, "pii_guard_blocked",
                $"Body contained a monetary figure, which is not permitted on {channel}. Delivered in-app instead.");

        if (channel == NotificationChannels.Push)
        {
            if (recipient.PushTargets.Count == 0)
                return Terminal(shortRow, DeliveryOutcomes.NoContact, now, "no_contact",
                    "No registered mobile device for this employee.");
            shortRow.DestinationMasked = NotificationBodyPolicy.MaskDevices(recipient.PushTargets.Count);
            shortRow.NextAttemptAtUtc = ApplyQuietHours(now, preference, channel);
            return shortRow;
        }

        if (string.IsNullOrWhiteSpace(recipient.Phone))
            return Terminal(shortRow, DeliveryOutcomes.NoContact, now, "no_contact",
                "No mobile number on file for this employee.");

        shortRow.DestinationMasked = NotificationBodyPolicy.MaskPhone(recipient.Phone);
        shortRow.NextAttemptAtUtc = ApplyQuietHours(now, preference, channel);
        return shortRow;
    }

    /// <summary>
    /// Stamps a row that will never leave the queue. Kept as an explicit helper so every
    /// "we did not send it, and here is exactly why" path is uniform and durable.
    /// </summary>
    private static NotificationDelivery Terminal(NotificationDelivery row, string outcome, DateTime now,
        string errorCode, string errorMessage)
    {
        row.Outcome = outcome;
        row.CompletedAtUtc = now;
        row.NextAttemptAtUtc = null;
        row.ErrorCode = errorCode;
        row.ErrorMessage = errorMessage;
        return row;
    }

    /// <summary>
    /// Quiet hours are HONOURED, not ignored: a short-channel message inside the window is DEFERRED
    /// to the end of the window rather than dropped. QuietHoursJson shape:
    /// {"start":"22:00","end":"07:00","utcOffsetMinutes":180}. Malformed ⇒ no deferral.
    /// </summary>
    internal static DateTime ApplyQuietHours(DateTime nowUtc, EmployeeNotificationPreference? preference, string channel)
    {
        if (preference is null || !NotificationChannels.IsShortChannel(channel)) return nowUtc;
        if (string.IsNullOrWhiteSpace(preference.QuietHoursJson) || preference.QuietHoursJson == "{}") return nowUtc;

        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(preference.QuietHoursJson);
            var root = doc.RootElement;
            if (root.ValueKind != System.Text.Json.JsonValueKind.Object) return nowUtc;
            if (!root.TryGetProperty("start", out var startEl) || !root.TryGetProperty("end", out var endEl))
                return nowUtc;
            if (!TimeOnly.TryParse(startEl.GetString(), out var start) || !TimeOnly.TryParse(endEl.GetString(), out var end))
                return nowUtc;

            var offsetMinutes = root.TryGetProperty("utcOffsetMinutes", out var offEl) && offEl.TryGetInt32(out var off)
                ? off : 0;
            var local = nowUtc.AddMinutes(offsetMinutes);
            var localTime = TimeOnly.FromDateTime(local);

            var inQuiet = start <= end
                ? localTime >= start && localTime < end
                : localTime >= start || localTime < end;      // window wraps midnight
            if (!inQuiet) return nowUtc;

            var releaseLocal = local.Date.Add(end.ToTimeSpan());
            if (releaseLocal <= local) releaseLocal = releaseLocal.AddDays(1);
            return DateTime.SpecifyKind(releaseLocal.AddMinutes(-offsetMinutes), DateTimeKind.Utc);
        }
        catch
        {
            return nowUtc;   // a malformed preference must never block a notification
        }
    }

    // ── Rendering ─────────────────────────────────────────────────────────────

    private static (string Subject, string Body, bool HadUnresolved) RenderInApp(NotificationRequest request,
        IReadOnlyList<NotificationTemplate> templates)
    {
        var template = templates.FirstOrDefault(t =>
            t.Channel.Equals(NotificationChannels.InApp, StringComparison.OrdinalIgnoreCase) && t.IsActive);

        var subject = !string.IsNullOrWhiteSpace(request.Title) ? request.Title
            : !string.IsNullOrWhiteSpace(template?.SubjectEn) ? template!.SubjectEn : request.EventCode;
        var body = !string.IsNullOrWhiteSpace(request.Message) ? request.Message
            : template?.BodyEn ?? string.Empty;

        subject = NotificationBodyPolicy.Interpolate(subject, request.Variables, htmlEncodeValues: false);
        body = NotificationBodyPolicy.Interpolate(body, request.Variables, htmlEncodeValues: false);

        var unresolved = NotificationBodyPolicy.HasUnresolvedPlaceholder(subject)
            || NotificationBodyPolicy.HasUnresolvedPlaceholder(body);
        if (unresolved)
        {
            // In-app is the guaranteed-visible fallback, so it is stripped rather than failed —
            // but the delivery row records unresolved_placeholder so an admin sees the bad template.
            subject = NotificationBodyPolicy.StripPlaceholders(subject);
            body = NotificationBodyPolicy.StripPlaceholders(body);
        }
        return (Truncate(subject, 300), body, unresolved);
    }

    private static (string Subject, string Body, bool Unresolved) RenderForChannel(string channel,
        NotificationRequest request, NotificationTemplate? template, IReadOnlyList<NotificationTemplate> allTemplates)
    {
        var isShort = NotificationChannels.IsShortChannel(channel);
        var allowList = isShort ? NotificationBodyPolicy.ShortChannelVariables : null;

        string subjectTemplate, bodyTemplate;
        if (template is not null && !string.IsNullOrWhiteSpace(template.BodyEn))
        {
            subjectTemplate = template.SubjectEn;
            bodyTemplate = template.BodyEn;
        }
        else if (isShort && NotificationBodyPolicy.ShortChannelDefault(request.EventCode) is { } shortDefault)
        {
            (subjectTemplate, bodyTemplate) = shortDefault;
        }
        else
        {
            var fallback = allTemplates.FirstOrDefault(t =>
                t.Channel.Equals(NotificationChannels.InApp, StringComparison.OrdinalIgnoreCase) && t.IsActive);
            subjectTemplate = !string.IsNullOrWhiteSpace(request.Title) ? request.Title
                : !string.IsNullOrWhiteSpace(fallback?.SubjectEn) ? fallback!.SubjectEn : request.EventCode;
            bodyTemplate = !string.IsNullOrWhiteSpace(request.Message) ? request.Message : fallback?.BodyEn ?? string.Empty;
        }

        // HTML-encode the VALUES (not the template) on email — the old SendEmailAsync spliced the
        // rendered body straight into an <html> wrapper, and template variables come from
        // employee-controlled fields.
        var encodeValues = channel == NotificationChannels.Email;
        var subject = NotificationBodyPolicy.Interpolate(subjectTemplate, request.Variables, false, allowList);
        var body = NotificationBodyPolicy.Interpolate(bodyTemplate, request.Variables, encodeValues, allowList);

        var unresolved = NotificationBodyPolicy.HasUnresolvedPlaceholder(subject)
            || NotificationBodyPolicy.HasUnresolvedPlaceholder(body);
        return (Truncate(subject, 300), body, unresolved);
    }

    // ── Row construction + dedupe key ─────────────────────────────────────────

    private static NotificationDelivery NewDelivery(NotificationRequest request, NotificationRecipient? recipient,
        string channel, string subject, string body, DateTime now)
    {
        var recipientKey = recipient?.RecipientKey ?? "broadcast";
        var dedupeKey = ComputeDedupeKey(request.TenantId, request.EventCode, request.EntityName, request.EntityId,
            recipientKey, channel, subject, body);

        return new NotificationDelivery
        {
            TenantId = request.TenantId,
            EventCode = request.EventCode,
            EntityName = request.EntityName,
            EntityId = request.EntityId,
            Channel = channel,
            Outcome = DeliveryOutcomes.Queued,
            AudienceType = recipient?.AudienceType ?? NotificationAudiences.User,
            UserId = recipient?.UserId,
            EmployeeId = recipient?.EmployeeId,
            Subject = subject,
            Body = body,
            DedupeKey = dedupeKey,
            // Forwarded to the vendor (Meta message_id / FCM message id / Twilio Idempotency-Key) so
            // a vendor-side retry cannot duplicate either.
            IdempotencyKey = dedupeKey[..Math.Min(32, dedupeKey.Length)],
            NextAttemptAtUtc = now,
            CreatedAtUtc = now,
        };
    }

    /// <summary>
    /// Pure BUSINESS identity — tenant, event, entity, recipient, channel and rendered content.
    /// Deliberately contains no Guid.NewGuid() value: Notification.Id is minted fresh on every call,
    /// so hashing it (as a naive design would) makes every re-entry a new key and the unique index
    /// never fires. Content is included so a genuinely different message still goes out while an
    /// identical repeat for the same entity is refused.
    /// </summary>
    internal static string ComputeDedupeKey(Guid tenantId, string eventCode, string entityName, string? entityId,
        string recipientKey, string channel, string subject, string body)
    {
        var material = string.Join('|', "v1", tenantId.ToString("N"), eventCode ?? string.Empty,
            entityName ?? string.Empty, entityId ?? string.Empty, recipientKey, channel,
            Sha256Hex($"{subject}\n{body}"));
        return Sha256Hex(material);
    }

    private static string Sha256Hex(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    // ── Lookups ───────────────────────────────────────────────────────────────

    private static async Task<IReadOnlyList<NotificationTemplate>> LoadTemplatesAsync(ZayraDbContext db,
        Guid tenantId, string eventCode, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(eventCode)) return [];
        // The unique index is (TenantId, Code, Channel) — the OLD lookup filtered on Code only and
        // took FirstOrDefault, which silently picks an arbitrary channel's template the moment
        // SMS/WhatsApp rows exist. Load them all and pick per channel.
        // IgnoreQueryFilters is intentional: as above — tenant pinned in the WHERE.
        return await db.NotificationTemplates.IgnoreQueryFilters().AsNoTracking()
            .Where(t => t.TenantId == tenantId && t.Code == eventCode && !t.IsDeleted)
            .ToListAsync(ct);
    }

    private static async Task<EmployeeNotificationPreference?> LoadPreferenceAsync(ZayraDbContext db,
        Guid tenantId, int? employeeId, CancellationToken ct)
    {
        if (employeeId is null) return null;
        // IgnoreQueryFilters is intentional: as above — tenant pinned in the WHERE.
        return await db.EmployeeNotificationPreferences.IgnoreQueryFilters().AsNoTracking()
            .FirstOrDefaultAsync(p => p.TenantId == tenantId && p.EmployeeId == employeeId.Value, ct);
    }

    internal static bool IsUniqueViolation(DbUpdateException ex)
    {
        for (var inner = ex.InnerException; inner is not null; inner = inner.InnerException)
        {
            if (inner.Data["SqlState"] as string == "23505") return true;
            if (inner.Message.Contains("23505", StringComparison.Ordinal)) return true;
            if (inner.Message.Contains("duplicate key", StringComparison.OrdinalIgnoreCase)) return true;
            if (inner.Message.Contains("UNIQUE constraint failed", StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    private static string Truncate(string value, int max) =>
        string.IsNullOrEmpty(value) ? string.Empty : value.Length <= max ? value : value[..max];
}
