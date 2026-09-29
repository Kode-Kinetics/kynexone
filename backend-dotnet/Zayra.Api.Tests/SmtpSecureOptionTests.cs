using MailKit.Security;
using Zayra.Api.Infrastructure.Email;

namespace Zayra.Api.Tests;

/// <summary>
/// The port decides how TLS starts. 465 is implicit TLS; 587 is STARTTLS. Asking for STARTTLS on
/// 465 leaves the client waiting for a plaintext greeting that never comes, and it surfaces as a
/// bare 20-second timeout naming no cause.
///
/// <para>The old rule was <c>UseTls ? StartTls : Auto</c> — the port was ignored entirely, so a
/// checkbox labelled "Use STARTTLS (recommended)" made a valid 465 + SSL setup impossible to
/// express through the UI. On 2026-09-23 an operator configured GoDaddy's
/// <c>smtpout.secureserver.net:465</c> with that box ticked and got exactly that timeout.</para>
/// </summary>
public class SmtpSecureOptionTests
{
    /// <summary>THE INCIDENT: 465 with the STARTTLS box ticked must NOT ask for STARTTLS.</summary>
    [Fact]
    public void Port465_UsesImplicitTls_EvenWhenTheStartTlsBoxIsTicked()
    {
        Assert.Equal(SecureSocketOptions.SslOnConnect,
            SmtpEmailService.ResolveSecureOption(useTls: true, port: 465));
    }

    [Fact]
    public void Port465_UsesImplicitTls_WhenTheBoxIsClear_Too()
    {
        // 465 is SMTPS by definition. There is no plaintext mode on it to fall back to.
        Assert.Equal(SecureSocketOptions.SslOnConnect,
            SmtpEmailService.ResolveSecureOption(useTls: false, port: 465));
    }

    /// <summary>587 is submission: the checkbox is meaningful there and must be honoured.</summary>
    [Theory]
    [InlineData(587)]
    [InlineData(2525)]
    public void SubmissionPorts_HonourTheCheckbox(int port)
    {
        Assert.Equal(SecureSocketOptions.StartTls,
            SmtpEmailService.ResolveSecureOption(useTls: true, port: port));
        Assert.Equal(SecureSocketOptions.Auto,
            SmtpEmailService.ResolveSecureOption(useTls: false, port: port));
    }

    /// <summary>An internal relay on 25 with TLS unticked must still be reachable.</summary>
    [Fact]
    public void Port25_WithoutTls_NegotiatesRatherThanForcingAnything()
    {
        Assert.Equal(SecureSocketOptions.Auto,
            SmtpEmailService.ResolveSecureOption(useTls: false, port: 25));
    }
}
