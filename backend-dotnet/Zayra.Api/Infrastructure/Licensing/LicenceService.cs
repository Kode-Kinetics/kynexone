using Microsoft.EntityFrameworkCore;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Modules;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Licensing;

public enum LicenceState
{
    /// <summary>No valid licence is installed (none imported, signature broken, wrong installation, not yet valid).</summary>
    Missing,
    Valid,
    /// <summary>Past the expiry date but inside the grace days: everything works, the UI shows a banner.</summary>
    Grace,
    /// <summary>Past the grace days: the product refuses work until a renewed licence is imported.</summary>
    Expired,
}

/// <summary>
/// The verified licence as last loaded, with the installation it was checked against. Immutable: a refresh replaces
/// the whole snapshot, so a reader never sees half of an old licence and half of a new one.
/// </summary>
public sealed record LicenceSnapshot(VerifiedLicence? Licence, Guid? InstallationId, DateTime LoadedAtUtc)
{
    /// <summary>What an installation without a valid licence may do: nothing optional, and at most one of each seat.</summary>
    public static LicenceSnapshot None(Guid? installationId) => new(null, installationId, DateTime.UtcNow);

    public LicencePayload? Payload => Licence?.Payload;

    public LicenceState StateAt(DateTime nowUtc)
    {
        if (Payload is not { } p) return LicenceState.Missing;
        if (p.NotBeforeUtc > nowUtc) return LicenceState.Missing;
        if (p.ExpiresAtUtc is not { } expires || nowUtc <= expires) return LicenceState.Valid;
        return nowUtc <= expires.AddDays(p.GraceDays) ? LicenceState.Grace : LicenceState.Expired;
    }

    /// <summary>0 = unlimited. Without a licence, one of each — never 0, which would read as unlimited.</summary>
    public int MaxEmployees => Payload?.MaxEmployees ?? 1;
    public int MaxUsers => Payload?.MaxUsers ?? 1;
    public int MaxCompanies => Payload?.MaxCompanies ?? 1;
    public int MaxAdminUsers => Payload?.MaxAdminUsers ?? 1;
    /// <summary>0 = AI disabled.</summary>
    public int AiMonthlyTokenLimit => Payload?.AiMonthlyTokenLimit ?? 0;
    public string AccountType => Payload?.AccountType ?? "SingleCompany";
    public DateTime? SupportExpiresAtUtc => Payload?.SupportExpiresAtUtc;
    public IReadOnlySet<string> Modules { get; } = Licence?.Payload.Modules.ToHashSet(StringComparer.Ordinal) ?? [];
    public IReadOnlySet<string> OptInFeatures { get; } = Licence?.Payload.OptInFeatures.ToHashSet(StringComparer.Ordinal) ?? [];
}

/// <summary>
/// The installation's commercial entitlement, from the vendor-signed licence only. Every limit, optional module and
/// opt-in feature in the product reads it here. Singleton: refreshed at boot, after every import and every
/// 15 minutes (<see cref="LicenceRefreshWorker"/>). Each refresh re-verifies the stored signature, so editing the
/// stored licence — or any table — changes nothing.
/// </summary>
public interface ILicenceService
{
    LicenceSnapshot Current { get; }
    LicenceState State { get; }
    bool IsModuleLicensed(string moduleKey);
    bool IsOptInLicensed(string featureKey);
    Task RefreshAsync(CancellationToken ct);
}

public sealed class LicenceService : ILicenceService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly LicenceVerifier _verifier;
    private readonly ILogger<LicenceService> _log;
    private readonly Func<DateTime> _clock;
    private volatile LicenceSnapshot _current = LicenceSnapshot.None(null);

    public LicenceService(IServiceScopeFactory scopes, LicenceVerifier verifier, ILogger<LicenceService> log)
        : this(scopes, verifier, log, () => DateTime.UtcNow) { }

    internal LicenceService(IServiceScopeFactory scopes, LicenceVerifier verifier, ILogger<LicenceService> log, Func<DateTime> clock)
    {
        _scopes = scopes;
        _verifier = verifier;
        _log = log;
        _clock = clock;
    }

    /// <summary>The keys a licence may name: every catalogued module and every opt-in feature.</summary>
    public static IReadOnlySet<string> KnownModules { get; } = ModuleCatalog.All.Select(m => m.Key).ToHashSet(StringComparer.Ordinal);
    public static IReadOnlySet<string> KnownOptIns { get; } = Zayra.Api.Infrastructure.Modules.OptInFeatures.Keys.ToHashSet(StringComparer.Ordinal);

    public LicenceSnapshot Current => _current;
    public LicenceState State => _current.StateAt(_clock());

    public bool IsModuleLicensed(string moduleKey)
    {
        var module = ModuleCatalog.TryGet(moduleKey);
        // A core or statutory module is never the licence's to switch off: that would switch off a legal obligation.
        if (module is not null && module.Lock is ModuleLock.Core or ModuleLock.Statutory) return true;
        return Usable && _current.Modules.Contains(moduleKey);
    }

    public bool IsOptInLicensed(string featureKey) => Usable && _current.OptInFeatures.Contains(featureKey);

    private bool Usable => State is LicenceState.Valid or LicenceState.Grace;

    public async Task RefreshAsync(CancellationToken ct)
    {
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ZayraDbContext>();
        _current = await LoadAsync(db, _verifier, _clock(), _log, ct);
    }

    /// <summary>Reads and re-verifies the Active licence row. Anything wrong with it reads as no licence.</summary>
    internal static async Task<LicenceSnapshot> LoadAsync(ZayraDbContext db, LicenceVerifier verifier, DateTime nowUtc,
        ILogger log, CancellationToken ct)
    {
        var installationId = await InstallationIdentity.GetAsync(db, ct);
        var row = await db.InstallationLicences.AsNoTracking()
            .Where(l => l.Status == InstallationLicenceStatuses.Active)
            .FirstOrDefaultAsync(ct);
        if (row is null) return LicenceSnapshot.None(installationId);

        // Re-verify with the clock at the licence's own start, so "not before" was checked at import and an
        // expired licence still loads (its expiry is a state, not a verification failure).
        var verification = verifier.Verify(new LicenceFile(row.Payload, row.Signature, row.Kid), installationId,
            LicenceService.KnownModules, LicenceService.KnownOptIns, DateTime.MaxValue.AddYears(-1));
        if (!verification.IsValid || verification.Licence!.Fingerprint != row.Fingerprint)
        {
            log.LogError("[Licence] The stored licence {LicenceId} no longer verifies ({Problems}). Treating the installation as unlicensed.",
                row.LicenceId, string.Join(" ", verification.Problems));
            return LicenceSnapshot.None(installationId);
        }
        return new LicenceSnapshot(verification.Licence, installationId, nowUtc);
    }
}

/// <summary>This installation's identity, created once (at organisation setup, or on first licence use).</summary>
public static class InstallationIdentity
{
    public static async Task<Guid?> GetAsync(ZayraDbContext db, CancellationToken ct)
    {
        var value = await db.PlatformConfigEntries.AsNoTracking()
            .Where(e => e.Key == PlatformConfigKeys.InstallationId)
            .Select(e => e.Value)
            .FirstOrDefaultAsync(ct);
        return Guid.TryParse(value, out var id) ? id : null;
    }

    /// <summary>Returns the installation id, creating it if this installation has none yet. Saves.</summary>
    public static async Task<Guid> EnsureAsync(ZayraDbContext db, CancellationToken ct)
    {
        if (await GetAsync(db, ct) is { } existing) return existing;
        var id = Guid.NewGuid();
        db.PlatformConfigEntries.Add(new PlatformConfigEntry { Key = PlatformConfigKeys.InstallationId, Value = id.ToString() });
        await db.SaveChangesAsync(ct);
        return id;
    }
}

/// <summary>
/// The licence's limits as the limit checks read them: 0 = unlimited. A null service — only ever a test that built a
/// service by hand without one; DI always supplies it — imposes no limit, so such a test keeps measuring what it was
/// written for.
/// </summary>
public static class LicenceLimits
{
    public static int MaxEmployees(this ILicenceService? licence) => licence?.Current.MaxEmployees ?? 0;
    public static int MaxUsers(this ILicenceService? licence) => licence?.Current.MaxUsers ?? 0;
    public static int MaxCompanies(this ILicenceService? licence) => licence?.Current.MaxCompanies ?? 0;
    public static int MaxAdminUsers(this ILicenceService? licence) => licence?.Current.MaxAdminUsers ?? 0;
    public static string AccountType(this ILicenceService? licence, string fallback) => licence?.Current.AccountType ?? fallback;
}

public static class LicenceControllerExtensions
{
    /// <summary>The request's licence service, or null when a test constructed the controller without services.</summary>
    public static ILicenceService? Licence(this Microsoft.AspNetCore.Mvc.ControllerBase controller) =>
        controller.HttpContext?.RequestServices?.GetService(typeof(ILicenceService)) as ILicenceService;
}

/// <summary>The licence's monthly AI allowance. A limit of 0 means AI is not licensed at all.</summary>
public readonly record struct AiAllowance(bool Licensed, long MonthlyTokenLimit)
{
    /// <summary>A null service (a test that built its caller by hand) imposes no limit.</summary>
    public static AiAllowance For(ILicenceService? licence) => licence is null
        ? new AiAllowance(true, long.MaxValue)
        : new AiAllowance(licence.Current.AiMonthlyTokenLimit > 0, licence.Current.AiMonthlyTokenLimit);

    public const string NotLicensedCode = "ai_not_licensed";
    public const string NotLicensedMessage = "AI features are not included in this installation's licence.";
}
