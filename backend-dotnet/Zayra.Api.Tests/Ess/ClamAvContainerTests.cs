using System.Text;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using FluentAssertions;
using Zayra.Api.Application.Employees.Ess;

namespace Zayra.Api.Tests.Ess;

/// <summary>
/// CD66 against a REAL clamd (image clamav/clamav, started with Testcontainers). Skipped quietly, with a console line, when Docker or the image is not
/// available, so the suite stays runnable offline. Opt out explicitly with KYNEX_SKIP_CLAMAV=1. The EICAR string is the standard antivirus test file:
/// harmless, and every engine must flag it.
/// </summary>
[Trait("Category", "ClamAv")]
public sealed class ClamAvContainerTests : IAsyncLifetime
{
    private const string Eicar = @"X5O!P%@AP[4\PZX54(P^)7CC)7}$EICAR-STANDARD-ANTIVIRUS-TEST-FILE!$H+H*";
    private IContainer? _clamd;
    private string? _unavailable;

    public async Task InitializeAsync()
    {
        if (Environment.GetEnvironmentVariable("KYNEX_SKIP_CLAMAV") == "1") { _unavailable = "KYNEX_SKIP_CLAMAV=1"; return; }
        try
        {
            _clamd = new ContainerBuilder()
                .WithImage("clamav/clamav:stable")
                .WithPortBinding(3310, true)
                .WithEnvironment("CLAMAV_NO_FRESHCLAMD", "true")
                .WithWaitStrategy(Wait.ForUnixContainer().UntilMessageIsLogged("Self checking every", o => o.WithTimeout(TimeSpan.FromMinutes(8))))
                .Build();
            await _clamd.StartAsync();
        }
        catch (Exception ex)
        {
            _unavailable = ex.GetType().Name + ": " + ex.Message.Split('\n')[0];
            Console.WriteLine("[ClamAvContainerTests] skipped, clamd container unavailable: " + _unavailable);
            if (_clamd is not null) { try { await _clamd.DisposeAsync(); } catch { } _clamd = null; }
        }
    }

    public async Task DisposeAsync() { if (_clamd is not null) await _clamd.DisposeAsync(); }

    private ClamAvMalwareScanner Scanner() => new(new ClamAvOptions { Host = _clamd!.Hostname, Port = _clamd.GetMappedPublicPort(3310), MaxBytes = 5 * 1024 * 1024, Timeout = TimeSpan.FromSeconds(30) });

    [Fact]
    public async Task Eicar_IsQuarantinedWithItsSignature_AndACleanFileIsClean()
    {
        if (_unavailable is not null) return;
        var scanner = Scanner();
        var bad = await scanner.ScanAsync(Encoding.ASCII.GetBytes(Eicar), "eicar.pdf", default);
        bad.Verdict.Should().Be(ScanVerdict.Quarantined);
        bad.Signature.Should().Contain("Eicar");
        var good = await scanner.ScanAsync(Encoding.ASCII.GetBytes("%PDF-1.4\n1 0 obj<<>>endobj\ntrailer<<>>\n%%EOF"), "ok.pdf", default);
        good.Verdict.Should().Be(ScanVerdict.Clean);
    }

    [Fact]
    public async Task ADeadScanner_FailsClosed()
    {
        if (_unavailable is not null) return;
        var dead = new ClamAvMalwareScanner(new ClamAvOptions { Host = _clamd!.Hostname, Port = 1, MaxBytes = 1024, Timeout = TimeSpan.FromSeconds(2) });
        (await dead.ScanAsync(new byte[] { 1 }, "x.pdf", default)).Verdict.Should().Be(ScanVerdict.QuarantinedPending);
    }
}
