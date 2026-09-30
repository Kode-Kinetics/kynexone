namespace Zayra.Api.Infrastructure.Email;

public record EmailAttachment(string FileName, byte[] Data, string ContentType);

public interface IEmailService
{
    Task SendAsync(string toAddress, string toName, string subject, string htmlBody, IReadOnlyList<EmailAttachment>? attachments = null, CancellationToken cancellationToken = default);
    /// <summary>Returns true when SMTP is configured and email delivery will be attempted.</summary>
    Task<bool> IsConfiguredAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// POD-D5 — TENANT-EXPLICIT overloads.
    ///
    /// The ambient-scope overloads above are only safe inside an HTTP request, where the global
    /// tenant query filter is active. In a BackgroundService there is no HttpContext, so
    /// ZayraDbContext._isSystemScope is TRUE and the filter is bypassed entirely — an SMTP config
    /// read with no explicit predicate would then return EVERY tenant's rows and relay one tenant's
    /// payroll mail through another tenant's server, from another tenant's address.
    ///
    /// NotificationDeliveryWorker always uses these. Default implementations delegate to the
    /// ambient overloads so existing fakes and callers keep compiling unchanged.
    /// </summary>
    Task SendAsync(Guid tenantId, string toAddress, string toName, string subject, string htmlBody,
        IReadOnlyList<EmailAttachment>? attachments = null, CancellationToken cancellationToken = default)
        => SendAsync(toAddress, toName, subject, htmlBody, attachments, cancellationToken);

    /// <inheritdoc cref="SendAsync(Guid,string,string,string,string,IReadOnlyList{EmailAttachment},CancellationToken)"/>
    Task<bool> IsConfiguredAsync(Guid tenantId, CancellationToken cancellationToken = default)
        => IsConfiguredAsync(cancellationToken);

    /// <summary>
    /// PLATFORM-EXPLICIT overloads — the control-plane relay configured in Platform Settings.
    ///
    /// Platform mail (subscription invoices, platform-admin password resets, the SMTP test itself)
    /// belongs to no tenant, so it must never resolve through a tenant's SystemSettings rows. The
    /// ambient overload cannot express that: for a platform-admin request there is no tenant_id
    /// claim, the query filter is bypassed, and a tenant-scoped read returns rows for every tenant.
    ///
    /// Default implementations delegate to the ambient overloads so existing fakes keep compiling.
    /// </summary>
    Task SendPlatformAsync(string toAddress, string toName, string subject, string htmlBody,
        IReadOnlyList<EmailAttachment>? attachments = null, CancellationToken cancellationToken = default)
        => SendAsync(toAddress, toName, subject, htmlBody, attachments, cancellationToken);

    /// <inheritdoc cref="SendPlatformAsync"/>
    Task<bool> IsPlatformConfiguredAsync(CancellationToken cancellationToken = default)
        => IsConfiguredAsync(cancellationToken);

    /// <summary>
    /// F09 — send and SAY WHAT HAPPENED. Any caller that tells a person "sent" or records a status
    /// uses this, not <c>SendAsync</c>: it separates "accepted by the relay" from "captured by test
    /// delivery mode" from "no relay at all". A relay failure still throws, with its reason.
    ///
    /// <para>The default implementation is the honest reading of the older two-call contract, so
    /// existing fakes keep compiling and keep meaning what they meant.</para>
    /// </summary>
    async Task<EmailDeliveryResult> DeliverAsync(Guid tenantId, string toAddress, string toName, string subject,
        string htmlBody, IReadOnlyList<EmailAttachment>? attachments = null, CancellationToken cancellationToken = default)
    {
        if (!await IsConfiguredAsync(tenantId, cancellationToken)) return EmailDeliveryResult.NoRelay;
        await SendAsync(tenantId, toAddress, toName, subject, htmlBody, attachments, cancellationToken);
        return EmailDeliveryResult.Accepted("for this workspace");
    }

    /// <inheritdoc cref="DeliverAsync"/>
    async Task<EmailDeliveryResult> DeliverPlatformAsync(string toAddress, string toName, string subject,
        string htmlBody, IReadOnlyList<EmailAttachment>? attachments = null, CancellationToken cancellationToken = default)
    {
        if (!await IsPlatformConfiguredAsync(cancellationToken)) return EmailDeliveryResult.NoRelay;
        await SendPlatformAsync(toAddress, toName, subject, htmlBody, attachments, cancellationToken);
        return EmailDeliveryResult.Accepted("for the platform");
    }
}
