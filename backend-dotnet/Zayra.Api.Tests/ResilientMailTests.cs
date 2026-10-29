using System.Net.Sockets;
using FluentAssertions;
using Zayra.Api.Infrastructure.Email;
using Xunit;

namespace Zayra.Api.Tests;

/// <summary>F7 - secret-bearing inline mail: retry transient relay failures, never repeat an accepted message.</summary>
[Collection("ResilientMail")]   // the idempotency cache is process-wide
public sealed class ResilientMailTests
{
    private static readonly Func<TimeSpan, CancellationToken, Task> NoDelay = (_, _) => Task.CompletedTask;
    private static string Key(string suffix = "a") => ResilientMail.Key(Guid.Parse("11111111-1111-1111-1111-111111111111"), "welcome-code", $"{suffix}@x.example", "Subject", "<p>body</p>");

    public ResilientMailTests() => ResilientMail.Reset();

    [Fact]
    public async Task TransientFailures_AreRetriedWithBackoff_ThenSucceed()
    {
        var calls = 0; var delays = new List<TimeSpan>();
        var result = await ResilientMail.RunAsync(Key(), _ =>
        {
            calls++;
            if (calls < 3) throw new SocketException((int)SocketError.ConnectionRefused);
            return Task.FromResult(EmailDeliveryResult.Accepted("relay"));
        }, default, (d, _) => { delays.Add(d); return Task.CompletedTask; }, () => 1.0);

        result.ReachedARelay.Should().BeTrue();
        calls.Should().Be(3);
        delays.Should().Equal(TimeSpan.FromMilliseconds(500), TimeSpan.FromMilliseconds(2000));
    }

    [Fact]
    public async Task APersistentTransientFailure_GivesUpAfterTheAttemptBudget_AndRethrows()
    {
        var calls = 0;
        var act = () => ResilientMail.RunAsync(Key(), _ => { calls++; throw new SocketException((int)SocketError.ConnectionRefused); }, default, NoDelay);
        await act.Should().ThrowAsync<SocketException>();
        calls.Should().Be(ResilientMail.MaxAttempts);
    }

    [Theory]
    [MemberData(nameof(Permanent))]
    public async Task PermanentFailures_AreNotRetried(Exception failure)
    {
        var calls = 0;
        var act = () => ResilientMail.RunAsync(Key("p" + failure.GetType().Name), _ => { calls++; throw failure; }, default, NoDelay);
        await act.Should().ThrowAsync<Exception>();
        calls.Should().Be(1);
    }

    public static IEnumerable<object[]> Permanent() => new[]
    {
        new object[] { new EmailNotConfiguredException("no relay") },
        new object[] { new EmailTransportException("smtp_submission_unknown", submissionMayHaveStarted: true) },   // might have been sent: never repeat
        new object[] { new InvalidOperationException("bad address") },
        new object[] { new System.IO.IOException("connection dropped after DATA") },   // may have been accepted: never repeat
        new object[] { new TimeoutException() },
        new object[] { new MailKit.Net.Smtp.SmtpProtocolException("bad reply") },
        new object[] { new MailKit.Net.Smtp.SmtpCommandException(MailKit.Net.Smtp.SmtpErrorCode.RecipientNotAccepted, MailKit.Net.Smtp.SmtpStatusCode.MailboxUnavailable, "550 no such user") },
    };

    [Fact]
    public async Task ConclusivePreSubmissionFailure_IsRetried()
    {
        var calls = 0;
        var r = await ResilientMail.RunAsync(Key("pre"), _ =>
        {
            if (++calls == 1) throw new EmailTransportException("smtp_connection_failed", submissionMayHaveStarted: false);
            return Task.FromResult(EmailDeliveryResult.Accepted("relay"));
        }, default, NoDelay);
        r.ReachedARelay.Should().BeTrue(); calls.Should().Be(2);
    }

    [Fact]
    public async Task SmtpFourXx_IsTransient_FiveXx_IsNot()
    {
        ResilientMail.IsTransient(new MailKit.Net.Smtp.SmtpCommandException(MailKit.Net.Smtp.SmtpErrorCode.UnexpectedStatusCode,
            MailKit.Net.Smtp.SmtpStatusCode.MailboxBusy, "450 try later")).Should().BeTrue();
        ResilientMail.IsTransient(new MailKit.Net.Smtp.SmtpCommandException(MailKit.Net.Smtp.SmtpErrorCode.UnexpectedStatusCode,
            MailKit.Net.Smtp.SmtpStatusCode.MailboxUnavailable, "550")).Should().BeFalse();
        ResilientMail.IsTransient(new OperationCanceledException()).Should().BeFalse("the caller gave up");
        await Task.CompletedTask;
    }

    [Fact]
    public async Task TheSameMessageAcceptedTwice_IsSentOnce_WithinTheWindow()
    {
        var calls = 0;
        Task<EmailDeliveryResult> Send(CancellationToken _) { calls++; return Task.FromResult(EmailDeliveryResult.Accepted("relay")); }
        var now = new DateTime(2026, 10, 26, 12, 0, 0, DateTimeKind.Utc);

        await ResilientMail.RunAsync(Key(), Send, default, NoDelay, null, () => now);
        var again = await ResilientMail.RunAsync(Key(), Send, default, NoDelay, null, () => now.AddSeconds(30));
        again.ReachedARelay.Should().BeTrue();
        calls.Should().Be(1, "a double click / retried POST must not mail the person twice");

        await ResilientMail.RunAsync(Key(), Send, default, NoDelay, null, () => now.Add(ResilientMail.Window).AddSeconds(1));
        calls.Should().Be(2, "after the window it is a new request");

        await ResilientMail.RunAsync(Key("someone-else"), Send, default, NoDelay, null, () => now);
        calls.Should().Be(3, "a different recipient is a different message");
    }

    [Fact]
    public async Task OnlyARealRelayAcceptance_IsRemembered()
    {
        var calls = 0;
        Task<EmailDeliveryResult> Send(CancellationToken _) { calls++; return Task.FromResult(calls == 1 ? EmailDeliveryResult.NoRelay : EmailDeliveryResult.Accepted("relay")); }
        (await ResilientMail.RunAsync(Key("nr"), Send, default, NoDelay)).Status.Should().Be(EmailDeliveryStatus.NotConfigured);
        (await ResilientMail.RunAsync(Key("nr"), Send, default, NoDelay)).ReachedARelay.Should().BeTrue("a not-configured result must not suppress a later real send");
        calls.Should().Be(2);
    }

    [Fact]
    public void Key_IsDeterministic_CaseInsensitiveOnTheAddress_AndSeparatesKindAndContent()
    {
        var t = Guid.NewGuid();
        ResilientMail.Key(t, "k", "A@X.com", "s", "b").Should().Be(ResilientMail.Key(t, "k", " a@x.com ", "s", "b"));
        ResilientMail.Key(t, "k", "a@x.com", "s", "b").Should().NotBe(ResilientMail.Key(t, "other", "a@x.com", "s", "b"));
        ResilientMail.Key(t, "k", "a@x.com", "s", "b").Should().NotBe(ResilientMail.Key(t, "k", "a@x.com", "s", "b2"));
        ResilientMail.Key(t, "k", "a@x.com", "s", "b").Should().NotBe(ResilientMail.Key(Guid.NewGuid(), "k", "a@x.com", "s", "b"));
    }

    [Fact]
    public void Backoff_GrowsFourfold_WithJitterBetweenHalfAndFull()
    {
        ResilientMail.Backoff(0, 0).Should().Be(TimeSpan.FromMilliseconds(250));
        ResilientMail.Backoff(0, 1).Should().Be(TimeSpan.FromMilliseconds(500));
        ResilientMail.Backoff(1, 1).Should().Be(TimeSpan.FromMilliseconds(2000));
    }

    [Fact]
    public async Task ExtensionMethod_DeduplicatesOnlyOnAnExplicitActionKey_SoAResendIsAlwaysSent()
    {
        var fake = new FakeEmail();
        var tenant = Guid.NewGuid();
        // identical content, no key: a deliberate resend goes out both times
        await fake.DeliverResilientAsync(tenant, "a@x.com", "A", "S", "<p>b</p>", kind: "welcome-code");
        await fake.DeliverResilientAsync(tenant, "a@x.com", "A", "S", "<p>b</p>", kind: "welcome-code");
        fake.TenantCalls.Should().Be(2);
        // the same user action twice (a double-click / retried POST): once
        await fake.DeliverResilientAsync(tenant, "a@x.com", "A", "S", "<p>b</p>", kind: "invite", idempotencyKey: "req-1");
        await fake.DeliverResilientAsync(tenant, "a@x.com", "A", "S", "<p>b</p>", kind: "invite", idempotencyKey: "req-1");
        fake.TenantCalls.Should().Be(3);
        await fake.DeliverResilientAsync(tenant, "a@x.com", "A", "S", "<p>b</p>", kind: "invite", idempotencyKey: "req-2");
        fake.TenantCalls.Should().Be(4);
        await fake.DeliverPlatformResilientAsync("p@x.com", "P", "S", "<p>b</p>", kind: "mfa");
        fake.PlatformCalls.Should().Be(1);
    }

    [Fact]
    public async Task MaxAttemptsOne_NeverRetries()
    {
        var calls = 0;
        var act = () => ResilientMail.RunAsync(null, _ => { calls++; throw new SocketException((int)SocketError.ConnectionRefused); }, default, NoDelay, maxAttempts: 1);
        await act.Should().ThrowAsync<SocketException>();
        calls.Should().Be(1);
    }

    private sealed class FakeEmail : IEmailService
    {
        public int TenantCalls, PlatformCalls;
        public Task SendAsync(string toAddress, string toName, string subject, string htmlBody, IReadOnlyList<EmailAttachment>? attachments = null, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<bool> IsConfiguredAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<EmailDeliveryResult> DeliverAsync(Guid tenantId, string toAddress, string toName, string subject, string htmlBody,
            IReadOnlyList<EmailAttachment>? attachments = null, CancellationToken cancellationToken = default)
        { TenantCalls++; return Task.FromResult(EmailDeliveryResult.Accepted("t")); }
        public Task<EmailDeliveryResult> DeliverPlatformAsync(string toAddress, string toName, string subject, string htmlBody,
            IReadOnlyList<EmailAttachment>? attachments = null, CancellationToken cancellationToken = default)
        { PlatformCalls++; return Task.FromResult(EmailDeliveryResult.Accepted("p")); }
    }
}
