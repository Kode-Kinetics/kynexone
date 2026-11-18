using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace Zayra.Api.Tests.Government.AnbPayrollApi;

/// <summary>
/// A real HTTPS server on 127.0.0.1 with a throwaway self-signed certificate, so the REAL
/// <see cref="SocketsHttpHandler"/> (not a stub) is exercised: redirects, TLS, framing. The handler under
/// test is pointed at it with a ConnectCallback, so every host name (anb-sandbox.test, anb-auth.test,
/// anb-redirect.test) lands here and is recorded by its Host header. No external network.
/// </summary>
internal sealed class LoopbackTlsBank : IAsyncDisposable
{
    public sealed record Seen(string Method, string Host, string Path, byte[] Body)
    {
        public string BodyText => Encoding.UTF8.GetString(Body);
    }

    /// <param name="After">Wait this long (after reading the whole request) before answering.</param>
    /// <param name="Never">Read the whole request, then never answer (the connection stays open until the test ends).</param>
    public sealed record Reply(int Status, string Body, string? Location = null, TimeSpan? After = null, bool Never = false);

    /// <summary>A reply that reads everything and then says nothing: the bank took the bytes and went silent.</summary>
    public static Reply Silence() => new(0, string.Empty, Never: true);

    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly X509Certificate2 _cert;
    private readonly CancellationTokenSource _stop = new();
    private readonly Func<Seen, Reply> _route;
    public readonly ConcurrentQueue<Seen> Requests = new();

    public LoopbackTlsBank(Func<Seen, Reply> route)
    {
        _route = route;
        _cert = MakeCertificate();
        _listener.Start();
        _ = AcceptLoopAsync();
    }

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    /// <summary>Points a real handler at this server and trusts its throwaway certificate. Nothing else on the
    /// handler is changed, so its redirect, cookie and decompression settings are the ones under test.</summary>
    public SocketsHttpHandler Wire(SocketsHttpHandler handler)
    {
        handler.ConnectCallback = async (_, ct) =>
        {
            var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            await socket.ConnectAsync(IPAddress.Loopback, Port, ct);
            return new NetworkStream(socket, ownsSocket: true);
        };
        handler.SslOptions.RemoteCertificateValidationCallback = (_, _, _, _) => true;
        return handler;
    }

    private async Task AcceptLoopAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _listener.AcceptTcpClientAsync(_stop.Token); }
            catch { return; }
            _ = ServeAsync(client);
        }
    }

    private async Task ServeAsync(TcpClient client)
    {
        using (client)
        {
            try
            {
                await using var ssl = new SslStream(client.GetStream(), leaveInnerStreamOpen: false);
                await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                {
                    ServerCertificate = _cert,
                    EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                }, _stop.Token);

                var head = await ReadHeadAsync(ssl);
                if (head is null) return;
                var lines = head.Split("\r\n");
                var requestLine = lines[0].Split(' ');
                var headers = lines.Skip(1).Where(l => l.Contains(':'))
                    .Select(l => l.Split(':', 2))
                    .GroupBy(p => p[0].Trim(), StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(g => g.Key, g => g.First()[1].Trim(), StringComparer.OrdinalIgnoreCase);
                var length = headers.TryGetValue("Content-Length", out var cl) ? int.Parse(cl, CultureInfo.InvariantCulture) : 0;
                var body = new byte[length];
                var read = 0;
                while (read < length)
                {
                    var n = await ssl.ReadAsync(body.AsMemory(read, length - read), _stop.Token);
                    if (n == 0) break;
                    read += n;
                }

                var seen = new Seen(requestLine[0], headers.GetValueOrDefault("Host", string.Empty).Split(':')[0], requestLine[1], body);
                Requests.Enqueue(seen);
                var reply = _route(seen);
                if (reply.Never)
                {
                    await Task.Delay(Timeout.Infinite, _stop.Token);
                    return;
                }
                if (reply.After is { } after) await Task.Delay(after, _stop.Token);
                var payload = Encoding.UTF8.GetBytes(reply.Body);
                var response = new StringBuilder()
                    .Append(CultureInfo.InvariantCulture, $"HTTP/1.1 {reply.Status} X\r\n")
                    .Append("Content-Type: application/json\r\n")
                    .Append(CultureInfo.InvariantCulture, $"Content-Length: {payload.Length}\r\n")
                    .Append("Connection: close\r\n");
                if (reply.Location is not null) response.Append("Location: ").Append(reply.Location).Append("\r\n");
                response.Append("\r\n");
                await ssl.WriteAsync(Encoding.ASCII.GetBytes(response.ToString()), _stop.Token);
                await ssl.WriteAsync(payload, _stop.Token);
                await ssl.FlushAsync(_stop.Token);
            }
            catch
            {
                // a client that hangs up is not a test failure here; the assertions read Requests
            }
        }
    }

    private static async Task<string?> ReadHeadAsync(Stream stream)
    {
        var buffer = new List<byte>(1024);
        var one = new byte[1];
        while (buffer.Count < 64 * 1024)
        {
            var n = await stream.ReadAsync(one);
            if (n == 0) return null;
            buffer.Add(one[0]);
            var c = buffer.Count;
            if (c >= 4 && buffer[c - 4] == '\r' && buffer[c - 3] == '\n' && buffer[c - 2] == '\r' && buffer[c - 1] == '\n')
                return Encoding.ASCII.GetString(buffer.ToArray(), 0, c - 4);
        }
        return null;
    }

    private static X509Certificate2 MakeCertificate()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=anb-sandbox.test", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName("anb-sandbox.test");
        san.AddDnsName("anb-auth.test");
        san.AddDnsName("anb-redirect.test");
        request.CertificateExtensions.Add(san.Build());
        using var ephemeral = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        // Re-imported from PKCS#12 so the private key is usable by SslStream on every platform.
        return new X509Certificate2(ephemeral.Export(X509ContentType.Pkcs12));
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        _listener.Stop();
        await Task.Yield();
        _cert.Dispose();
        _stop.Dispose();
    }
}

/// <summary>Hands the transport's named ANB client a handler wired to a <see cref="LoopbackTlsBank"/>. Test-only: production gets the
/// guarded handler from DI, which refuses loopback (AnbTransportGuardTests).</summary>
internal sealed class LoopbackClientFactory(HttpMessageHandler handler) : IHttpClientFactory
{
    public HttpClient CreateClient(string name)
    {
        if (name != Zayra.Api.Infrastructure.Government.GovernmentHttp.ClientName(Zayra.Api.Infrastructure.Government.GovernmentBodies.BankAnb))
            throw new InvalidOperationException($"unexpected client {name}");
        return new HttpClient(handler, disposeHandler: false) { Timeout = Timeout.InfiniteTimeSpan };
    }
}
