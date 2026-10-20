using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;

namespace Zayra.Api.Infrastructure.Email;

public interface ISmtpSession : IDisposable
{
    Task ConnectAsync(string host, int port, SecureSocketOptions options, CancellationToken ct);
    Task AuthenticateAsync(string user, string password, CancellationToken ct);
    Task SendAsync(MimeMessage message, CancellationToken ct);
    Task DisconnectAsync(CancellationToken ct);
}

internal sealed class SmtpSession : ISmtpSession
{
    private readonly SmtpClient client = new() { Timeout = 20_000 };
    public Task ConnectAsync(string host, int port, SecureSocketOptions options, CancellationToken ct) => client.ConnectAsync(host, port, options, ct);
    public Task AuthenticateAsync(string user, string password, CancellationToken ct) => client.AuthenticateAsync(user, password, ct);
    public async Task SendAsync(MimeMessage message, CancellationToken ct) => await client.SendAsync(message, ct);
    public Task DisconnectAsync(CancellationToken ct) => client.DisconnectAsync(true, ct);
    public void Dispose() => client.Dispose();
}
