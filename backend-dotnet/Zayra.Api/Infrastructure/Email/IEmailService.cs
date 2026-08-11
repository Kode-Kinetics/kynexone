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
}
