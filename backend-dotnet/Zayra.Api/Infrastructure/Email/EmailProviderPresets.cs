namespace Zayra.Api.Infrastructure.Email;

/// <summary>
/// One known mail provider's connection settings.
/// </summary>
/// <param name="Key">Stable identifier persisted alongside the SMTP config.</param>
/// <param name="Label">Name shown to the admin.</param>
/// <param name="Host">SMTP relay hostname. Empty for <c>custom</c>.</param>
/// <param name="Port">Submission port. 587 = STARTTLS, 465 = implicit TLS.</param>
/// <param name="UseTls">Whether the transport is encrypted (always true for real providers).</param>
/// <param name="UsernamePattern">What the username field expects — drives the UI hint.</param>
/// <param name="Guidance">The one thing that most often goes wrong with this provider.</param>
/// <param name="AlternatePorts">Other ports the provider accepts, offered in the UI.</param>
/// <param name="DocsUrl">Provider's own SMTP documentation, so the admin can verify us.</param>
/// <param name="IsHostAlias">
/// True when this preset shares its host with a more general one and so cannot be identified from
/// the host alone. GoDaddy's Microsoft 365 resale is smtp.office365.com, indistinguishable on the
/// wire from a direct Microsoft 365 tenant — detection must resolve that host to the general entry
/// rather than to whichever alias happens to come first in the list.
/// </param>
public sealed record EmailProviderPreset(
    string Key,
    string Label,
    string Host,
    int Port,
    bool UseTls,
    string UsernamePattern,
    string Guidance,
    IReadOnlyList<int> AlternatePorts,
    string? DocsUrl,
    string Category,
    bool IsHostAlias = false);

/// <summary>
/// Auto-configuration catalog for the platform SMTP form.
///
/// Every value here is the provider's documented submission endpoint. Picking a preset only
/// pre-fills the form — the admin still supplies credentials and saves, and nothing is trusted
/// until <c>POST settings/smtp/test</c> delivers a real message.
///
/// Ordering matters: the UI renders the list as given, and GoDaddy leads because that is what
/// this platform currently sends through.
/// </summary>
public static class EmailProviderPresets
{
    public const string CustomKey = "custom";

    public static readonly IReadOnlyList<EmailProviderPreset> All =
    [
        new("godaddy", "GoDaddy — Professional Email", "smtpout.secureserver.net", 587, true,
            "full-email",
            "Use the full mailbox address as the username (not just the part before the @). GoDaddy relays reject SMTP AUTH from IPs it does not recognise for the first few sends.",
            [465, 80, 3535], "https://www.godaddy.com/help/set-up-email-clients-3941", "Business"),

        new("godaddy-microsoft365", "GoDaddy — Microsoft 365 mailbox", "smtp.office365.com", 587, true,
            "full-email",
            "GoDaddy resells Microsoft 365. If your mailbox opens in Outlook Web, use this preset, not the Professional Email one, and enable Authenticated SMTP on the mailbox.",
            [], "https://learn.microsoft.com/exchange/mail-flow-best-practices/how-to-set-up-a-multifunction-device-or-application-to-send-email-using-microsoft-365-or-office-365", "Business",
            IsHostAlias: true),

        new("microsoft365", "Microsoft 365 / Outlook", "smtp.office365.com", 587, true,
            "full-email",
            "Authenticated SMTP (SMTP AUTH) is disabled by default on new tenants. Enable it per-mailbox in the Microsoft 365 admin centre, and use an app password if MFA is on.",
            [], "https://learn.microsoft.com/exchange/clients-and-mobile-in-exchange-online/authenticated-client-smtp-submission", "Business"),

        new("google-workspace", "Gmail / Google Workspace", "smtp.gmail.com", 587, true,
            "full-email",
            "Your normal account password will not work. Turn on 2-Step Verification and generate a 16-character App Password, then paste that as the password.",
            [465], "https://support.google.com/a/answer/176600", "Business"),

        new("zoho", "Zoho Mail", "smtp.zoho.com", 587, true,
            "full-email",
            "Data-centre specific: use smtp.zoho.eu (Europe), smtp.zoho.in (India) or smtp.zoho.com.au if your account was created there. An app-specific password is required when 2FA is on.",
            [465], "https://www.zoho.com/mail/help/zoho-smtp.html", "Business"),

        new("sendgrid", "SendGrid", "smtp.sendgrid.net", 587, true,
            "literal-apikey",
            "The username is the literal word \"apikey\" — not your account email. The password is the full SG. API key, which is shown only once when created.",
            [465, 2525], "https://www.twilio.com/docs/sendgrid/for-developers/sending-email/getting-started-smtp", "Transactional"),

        new("amazon-ses", "Amazon SES", "email-smtp.us-east-1.amazonaws.com", 587, true,
            "ses-credentials",
            "Change the region in the host to match your verified SES identity. SMTP credentials are generated in the SES console and are NOT your AWS access keys. A new account is sandboxed until you request production access.",
            [465, 2587, 25], "https://docs.aws.amazon.com/ses/latest/dg/smtp-connect.html", "Transactional"),

        new("mailgun", "Mailgun", "smtp.mailgun.org", 587, true,
            "domain-postmaster",
            "The username looks like postmaster@mg.yourdomain.com — copy it from the domain's SMTP credentials page. EU-region domains use smtp.eu.mailgun.org.",
            [465, 2525], "https://documentation.mailgun.com/docs/mailgun/user-manual/sending-messages/#smtp-relay", "Transactional"),

        new("postmark", "Postmark", "smtp.postmarkapp.com", 587, true,
            "token-both",
            "Paste the Server API Token into BOTH the username and password fields. Postmark also requires the From address to be a verified Sender Signature.",
            [2525, 25], "https://postmarkapp.com/developer/user-guide/send-email-with-smtp", "Transactional"),

        new("brevo", "Brevo (ex-Sendinblue)", "smtp-relay.brevo.com", 587, true,
            "full-email",
            "The password is the SMTP key from Brevo's SMTP & API page, not your login password.",
            [465, 2525], "https://help.brevo.com/hc/en-us/articles/7924908994450", "Transactional"),

        new("mailjet", "Mailjet", "in-v3.mailjet.com", 587, true,
            "api-key-pair",
            "Username is the API Key, password is the Secret Key — both from Mailjet's account settings.",
            [465, 25, 2525], "https://dev.mailjet.com/smtp-relay/configuration/", "Transactional"),

        new("resend", "Resend", "smtp.resend.com", 587, true,
            "literal-resend",
            "The username is the literal word \"resend\"; the password is your re_ API key.",
            [465, 2465, 2587], "https://resend.com/docs/send-with-smtp", "Transactional"),

        new("namecheap", "Namecheap Private Email", "mail.privateemail.com", 587, true,
            "full-email",
            "Use the full mailbox address as the username. Port 465 with implicit TLS is also supported.",
            [465], "https://www.namecheap.com/support/knowledgebase/article.aspx/1179/", "Business"),

        new("hostinger", "Hostinger Email", "smtp.hostinger.com", 587, true,
            "full-email",
            "Use the full mailbox address as the username and the mailbox password.",
            [465], "https://support.hostinger.com/en/articles/4305847", "Business"),

        new("icloud", "iCloud Mail", "smtp.mail.me.com", 587, true,
            "full-email",
            "Requires an app-specific password generated at appleid.apple.com. The From address must be one of your verified iCloud aliases.",
            [], "https://support.apple.com/en-us/102525", "Consumer"),

        new("yahoo", "Yahoo Mail", "smtp.mail.yahoo.com", 587, true,
            "full-email",
            "Requires an app password from Yahoo account security. Not suitable for transactional volume.",
            [465], "https://help.yahoo.com/kb/SLN4724.html", "Consumer"),

        new("cpanel", "cPanel / shared hosting", "mail.yourdomain.com", 587, true,
            "full-email",
            "Replace yourdomain.com with your own domain. cPanel hosts commonly use 465 with implicit TLS instead of 587.",
            [465, 25], null, "Business"),

        new(CustomKey, "Other / manual configuration", "", 587, true,
            "full-email",
            "Enter the host, port and credentials your mail provider documented. Use port 465 only if the provider says the connection is TLS from the first byte.",
            [465, 25, 2525], null, "Other"),
    ];

    public static EmailProviderPreset? ByKey(string? key) =>
        string.IsNullOrWhiteSpace(key)
            ? null
            : All.FirstOrDefault(p => string.Equals(p.Key, key, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Best-effort provider identification from a saved host, so SMTP configured before this
    /// catalog existed still shows the right provider (and its guidance) in the UI.
    /// Returns null rather than guessing when the host matches nothing — the UI then shows "Other".
    /// </summary>
    public static EmailProviderPreset? DetectByHost(string? host)
    {
        if (string.IsNullOrWhiteSpace(host)) return null;
        var h = host.Trim().ToLowerInvariant();

        // Exact host match first — cheapest. Aliases are skipped so a host shared by several
        // presets resolves to the general one, not to whichever alias is listed first.
        var exact = All.FirstOrDefault(p =>
            !p.IsHostAlias && !string.IsNullOrEmpty(p.Host) && string.Equals(p.Host, h, StringComparison.OrdinalIgnoreCase));
        if (exact is not null) return exact;

        // Then registrable-domain / prefix matches for the providers that vary by region.
        if (h.EndsWith("secureserver.net")) return ByKey("godaddy");
        if (h.EndsWith("office365.com") || h.EndsWith("outlook.com")) return ByKey("microsoft365");
        if (h.EndsWith("gmail.com") || h.EndsWith("googlemail.com")) return ByKey("google-workspace");
        if (h.Contains("zoho.")) return ByKey("zoho");
        if (h.EndsWith("sendgrid.net")) return ByKey("sendgrid");
        if (h.StartsWith("email-smtp.") && h.EndsWith("amazonaws.com")) return ByKey("amazon-ses");
        if (h.EndsWith("mailgun.org")) return ByKey("mailgun");
        if (h.EndsWith("postmarkapp.com")) return ByKey("postmark");
        if (h.EndsWith("brevo.com") || h.EndsWith("sendinblue.com")) return ByKey("brevo");
        if (h.EndsWith("mailjet.com")) return ByKey("mailjet");
        if (h.EndsWith("resend.com")) return ByKey("resend");
        if (h.EndsWith("privateemail.com")) return ByKey("namecheap");
        if (h.EndsWith("hostinger.com")) return ByKey("hostinger");
        if (h.EndsWith("mail.me.com") || h.EndsWith("icloud.com")) return ByKey("icloud");
        if (h.EndsWith("mail.yahoo.com")) return ByKey("yahoo");

        return null;
    }
}
