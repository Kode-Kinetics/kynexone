using FluentAssertions;
using MailKit.Security;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using MimeKit;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Email;

namespace Zayra.Api.Tests;

public sealed class OfferSmtpOutcomeTests
{
    internal static IConfiguration Config(string mode = "smtp") => new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>
    { ["Email:DeliveryMode"] = mode, ["Smtp:Host"] = "relay.example.invalid", ["Smtp:FromEmail"] = "hr@example.invalid", ["Smtp:FromName"] = "People" }).Build();
    internal static ZayraDbContext Db() => new(new DbContextOptionsBuilder<ZayraDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
    internal static SmtpEmailService Service(ZayraDbContext db, IConfiguration config, FakeSmtp session) =>
        new(db, new EphemeralDataProtectionProvider(), config, NullLogger<SmtpEmailService>.Instance, () => session);

    [Theory]
    [InlineData("disconnect")]
    [InlineData("dispose")]
    public async Task SuccessfulSubmissionSurvivesDisconnectFailure(string failureStage)
    {
        await using var db = Db(); var transport = new FakeSmtp { FailureStage = failureStage };
        var service = Service(db, Config(), transport);
        var outcome = await service.DeliverAsync(Guid.NewGuid(), "candidate@example.invalid", "Candidate", "Offer", "Reviewed");
        outcome.Status.Should().Be(EmailDeliveryStatus.AcceptedByRelay); transport.Submissions.Should().Be(1);
    }

    [Theory]
    [InlineData("connect", false)]
    [InlineData("send", true)]
    public async Task SubmissionBoundarySeparatesKnownFailureFromAmbiguity(string stage, bool possible)
    {
        await using var db = Db(); var service = Service(db, Config(), new() { FailureStage = stage });
        var tenant = Guid.NewGuid(); var sender = await service.GetEffectiveSenderAsync(tenant);
        var failure = await FluentActions.Awaiting(() => service.DeliverAsync(tenant, sender.ConfigurationFingerprint, "candidate@example.invalid", "Candidate", "Offer", "Reviewed"))
            .Should().ThrowAsync<EmailTransportException>();
        failure.Which.SubmissionMayHaveStarted.Should().Be(possible);
    }

    [Fact]
    public async Task ReviewedSenderChangeStopsBeforeConnect()
    {
        await using var db = Db(); var config = Config(); var transport = new FakeSmtp(); var service = Service(db, config, transport); var tenant = Guid.NewGuid();
        var reviewed = await service.GetEffectiveSenderAsync(tenant);
        config["Smtp:FromEmail"] = "changed@example.invalid";
        var failure = await FluentActions.Awaiting(() => service.DeliverAsync(tenant, reviewed.ConfigurationFingerprint,
            "candidate@example.invalid", "Candidate", "Offer", "Reviewed")).Should().ThrowAsync<EmailTransportException>();
        failure.Which.Code.Should().Be("offer_sender_changed"); transport.Connections.Should().Be(0);
    }

    [Fact]
    public async Task ResolutionIsBoundBeforeConnectEvenIfSettingsChangeDuringConnect()
    {
        await using var db = Db(); var config = Config(); var transport = new FakeSmtp { OnConnect = () => config["Smtp:FromEmail"] = "changed@example.invalid" };
        var service = Service(db, config, transport); var tenant = Guid.NewGuid(); var reviewed = await service.GetEffectiveSenderAsync(tenant);
        (await service.DeliverAsync(tenant, reviewed.ConfigurationFingerprint, "candidate@example.invalid", "Candidate", "Offer", "Reviewed")).Status.Should().Be(EmailDeliveryStatus.AcceptedByRelay);
        transport.From.Should().Be("hr@example.invalid");
    }

    [Fact]
    public async Task MalformedReviewedSenderIsKnownPreSubmissionFailure()
    {
        await using var db=Db();var config=Config();config["Smtp:FromEmail"]="bad@@example.invalid";
        var transport=new FakeSmtp();var service=Service(db,config,transport);var tenant=Guid.NewGuid();var sender=await service.GetEffectiveSenderAsync(tenant);
        var failure=await FluentActions.Awaiting(()=>service.DeliverAsync(tenant,sender.ConfigurationFingerprint,"candidate@example.invalid","Candidate","Offer","Reviewed")).Should().ThrowAsync<EmailTransportException>();
        failure.Which.SubmissionMayHaveStarted.Should().BeFalse();transport.Submissions.Should().Be(0);
    }
}

internal sealed class FakeSmtp : ISmtpSession
{
    internal string? FailureStage; internal int Submissions; internal int Connections; internal string? From; internal Action? OnConnect;
    public Task ConnectAsync(string host, int port, SecureSocketOptions options, CancellationToken ct)
    { Connections++; OnConnect?.Invoke(); if (FailureStage == "connect") throw new IOException("synthetic connect failure"); return Task.CompletedTask; }
    public Task AuthenticateAsync(string user, string password, CancellationToken ct) => Task.CompletedTask;
    public Task SendAsync(MimeMessage message, CancellationToken ct)
    { Submissions++; From = message.From.Mailboxes.Single().Address; if (FailureStage == "send") throw new IOException("synthetic submission interruption"); return Task.CompletedTask; }
    public Task DisconnectAsync(CancellationToken ct) => FailureStage == "disconnect" ? Task.FromException(new IOException("synthetic disconnect failure")) : Task.CompletedTask;
    public void Dispose() { if(FailureStage=="dispose") throw new IOException("synthetic cleanup failure"); }
}
