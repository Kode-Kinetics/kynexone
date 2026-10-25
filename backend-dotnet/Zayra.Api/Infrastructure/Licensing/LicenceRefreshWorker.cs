using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Data;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Licensing;

/// <summary>
/// Keeps <see cref="ILicenceService"/> current: re-reads and re-verifies the active licence every
/// <see cref="Interval"/>, so a licence that expires, enters grace or is replaced by another process (the CLI import)
/// takes effect without a restart. On start it also imports the file at <c>Licence:Path</c> when that file is not the
/// licence already installed.
/// </summary>
public sealed class LicenceRefreshWorker : BackgroundService
{
    // One indexed read of the active licence a minute: a licence imported from the command line (install, the e2e
    // setup) is in force within a minute, without a restart.
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);
    public const string PathConfigKey = "Licence:Path";

    private readonly ILicenceService _licence;
    private readonly IServiceScopeFactory _scopes;
    private readonly IConfiguration _config;
    private readonly ILogger<LicenceRefreshWorker> _log;

    public LicenceRefreshWorker(ILicenceService licence, IServiceScopeFactory scopes, IConfiguration config, ILogger<LicenceRefreshWorker> log)
    {
        _licence = licence;
        _scopes = scopes;
        _config = config;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await ImportFromPathAsync(stoppingToken); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogError(ex, "[Licence] Importing the licence at {Path} failed.", _config[PathConfigKey]);
        }

        using var timer = new PeriodicTimer(Interval);
        do
        {
            try { await _licence.RefreshAsync(stoppingToken); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.LogWarning(ex, "[Licence] Refresh failed; keeping the last verified licence.");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task ImportFromPathAsync(CancellationToken ct)
    {
        var path = _config[PathConfigKey];
        if (string.IsNullOrWhiteSpace(path)) return;
        if (!File.Exists(path))
        {
            _log.LogWarning("[Licence] Licence:Path is set to {Path}, but there is no file there.", path);
            return;
        }

        var json = await File.ReadAllTextAsync(path, ct);
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ZayraDbContext>();

        // Only import a file that differs from the licence already active: comparing the payload fingerprint avoids
        // an audit row on every boot.
        var file = LicenceFile.Parse(json);
        var payload = Base64Url.Decode(file?.Payload);
        if (payload is not null)
        {
            var fingerprint = Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();
            if (await db.InstallationLicences.AnyAsync(l => l.Status == InstallationLicenceStatuses.Active && l.Fingerprint == fingerprint, ct))
                return;
        }

        var result = await scope.ServiceProvider.GetRequiredService<LicenceImporter>()
            .ImportAsync(json, actorUserId: null, actorName: $"licence_path:{path}", ct);
        if (result.Succeeded)
            _log.LogInformation("[Licence] Imported the licence at {Path} ({Outcome}).", path, result.Outcome);
        else
            _log.LogError("[Licence] The licence at {Path} was rejected: {Problems}", path, string.Join(" ", result.Problems));
    }
}

/// <summary>
/// <c>--import-licence &lt;file&gt;</c> and <c>--licence-status</c>: the operator's command-line view of the licence.
/// Both run after the boot sequence and exit without serving traffic, like <c>--setup-organisation</c>.
/// </summary>
public static class LicenceCommands
{
    public const string ImportArgument = "--import-licence";
    public const string StatusArgument = "--licence-status";

    public static bool ShouldHandle(string[] args) =>
        args.Contains(ImportArgument, StringComparer.Ordinal) || args.Contains(StatusArgument, StringComparer.Ordinal);

    public static async Task<int> RunAsync(IServiceProvider services, string[] args, TextWriter output, TextWriter error, CancellationToken ct = default)
    {
        using var scope = services.CreateScope();
        var licence = scope.ServiceProvider.GetRequiredService<ILicenceService>();

        if (args.Contains(ImportArgument, StringComparer.Ordinal))
        {
            var at = Array.IndexOf(args, ImportArgument);
            var path = at + 1 < args.Length ? args[at + 1] : null;
            if (string.IsNullOrWhiteSpace(path) || path.StartsWith("--", StringComparison.Ordinal) || !File.Exists(path))
            {
                await error.WriteLineAsync($"[{ImportArgument}] REFUSED: give the path of a licence file (got '{path}').");
                return 1;
            }

            var result = await scope.ServiceProvider.GetRequiredService<LicenceImporter>()
                .ImportAsync(await File.ReadAllTextAsync(path, ct), actorUserId: null,
                    actorName: $"licence_cli:{Environment.UserName}", ct);
            if (!result.Succeeded)
            {
                await error.WriteLineAsync($"[{ImportArgument}] REJECTED: {string.Join(" ", result.Problems)}");
                return 1;
            }
            output.WriteLine($"[{ImportArgument}] {(result.Outcome == LicenceImportOutcome.Imported ? "Imported" : "Already active")}: "
                             + $"{result.Payload!.LicenceId} for {result.Payload.Licensee}.");
        }

        await licence.RefreshAsync(ct);
        var db = scope.ServiceProvider.GetRequiredService<ZayraDbContext>();
        await WriteStatusAsync(licence, db, output, ct);
        return 0;
    }

    private static async Task WriteStatusAsync(ILicenceService licence, ZayraDbContext db, TextWriter output, CancellationToken ct)
    {
        var snapshot = licence.Current;
        var installationId = await InstallationIdentity.EnsureAsync(db, ct);
        var usage = await LicenceUsage.MeasureAsync(db, ct);
        var p = snapshot.Payload;
        var text = new StringBuilder()
            .AppendLine($"Installation id : {installationId}")
            .AppendLine($"Licence state   : {licence.State}");
        if (p is not null)
        {
            text.AppendLine($"Licence id      : {p.LicenceId}")
                .AppendLine($"Licensee        : {p.Licensee}")
                .AppendLine($"Edition         : {p.Edition}")
                .AppendLine($"Account type    : {p.AccountType}")
                .AppendLine($"Expires         : {(p.ExpiresAtUtc is { } e ? $"{e:yyyy-MM-dd} (+{p.GraceDays} grace days)" : "never (perpetual)")}")
                .AppendLine($"Support until   : {(p.SupportExpiresAtUtc is { } s ? s.ToString("yyyy-MM-dd") : "no limit")}")
                .AppendLine($"Modules         : {(p.Modules.Count == 0 ? "(core and statutory only)" : string.Join(", ", p.Modules))}")
                .AppendLine($"Opt-in features : {(p.OptInFeatures.Count == 0 ? "(none)" : string.Join(", ", p.OptInFeatures))}");
        }
        text.AppendLine($"Employees       : {usage.ActiveEmployees} / {Limit(snapshot.MaxEmployees)}")
            .AppendLine($"Users           : {usage.ActiveUsers} / {Limit(snapshot.MaxUsers)}")
            .AppendLine($"Companies       : {usage.Companies} / {Limit(snapshot.MaxCompanies)}")
            .AppendLine($"AI tokens/month : {(snapshot.AiMonthlyTokenLimit == 0 ? "disabled" : snapshot.AiMonthlyTokenLimit.ToString())}");
        output.Write(text.ToString());
    }

    private static string Limit(int max) => max == 0 ? "unlimited" : max.ToString();
}

/// <summary>What the organisation uses against its licence, across every company.</summary>
public sealed record LicenceUsage(int ActiveEmployees, int ActiveUsers, int Companies)
{
    private const string Why = "Licence usage counts the whole organisation against installation-wide limits, whatever companies the caller can see.";

    public static async Task<LicenceUsage> MeasureAsync(ZayraDbContext db, CancellationToken ct)
    {
        var organisationId = await db.Tenants.AsNoTracking().Select(t => (Guid?)t.Id).FirstOrDefaultAsync(ct);
        if (organisationId is not { } org) return new(0, 0, 0);
        return new(
            await Zayra.Api.Infrastructure.Data.ScopedBypass.NullableTenantWide(db.Employees, org, Why).AsNoTracking()
                .CountAsync(e => e.Status == "Active" && !e.IsDeleted, ct),
            await Zayra.Api.Infrastructure.Data.ScopedBypass.TenantWide(db.Users, org, Why).AsNoTracking()
                .CountAsync(u => u.IsActive && !u.IsDeleted, ct),
            await Zayra.Api.Infrastructure.Data.ScopedBypass.TenantWide(db.Companies, org, Why).AsNoTracking()
                .CountAsync(c => c.IsActive && !c.IsDeleted, ct));
    }
}
