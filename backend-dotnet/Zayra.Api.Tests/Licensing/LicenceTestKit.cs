using System.Security.Cryptography;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Zayra.Api.Infrastructure.Licensing;

namespace Zayra.Api.Tests.Licensing;

/// <summary>
/// Signs licences with a key pair generated inside the test run — never committed, never the vendor's — and builds
/// verifiers and licence services that trust only that key.
/// </summary>
public sealed class LicenceTestKit : IDisposable
{
    public const string Kid = "test-kid";
    public ECDsa Key { get; } = ECDsa.Create(ECCurve.NamedCurves.nistP256);

    public LicenceVerifier Verifier() => new(new Dictionary<string, ECDsa> { [Kid] = Key });

    public static LicencePayload Payload(Action<LicencePayloadBuilder>? configure = null)
    {
        var builder = new LicencePayloadBuilder();
        configure?.Invoke(builder);
        return builder.Build();
    }

    public LicenceFile Sign(LicencePayload payload, string? kid = null)
    {
        var bytes = payload.ToUtf8Bytes();
        return new LicenceFile(Base64Url.Encode(bytes), Base64Url.Encode(Key.SignData(bytes, HashAlgorithmName.SHA256)), kid ?? Kid);
    }

    public string SignJson(LicencePayload payload) => Sign(payload).ToJson();

    public void Dispose() => Key.Dispose();
}

/// <summary>A licence payload with sensible defaults: valid now, perpetual, generous limits.</summary>
public sealed class LicencePayloadBuilder
{
    private LicencePayload _payload = new()
    {
        LicenceId = $"TEST-{Guid.NewGuid():N}"[..20],
        Licensee = "Test Organisation LLC",
        Edition = "Standard",
        IssuedAtUtc = DateTime.UtcNow.AddDays(-1),
        NotBeforeUtc = DateTime.UtcNow.AddDays(-1),
        AccountType = "Group",
        MaxEmployees = 100,
        MaxUsers = 50,
        MaxCompanies = 5,
        MaxAdminUsers = 5,
        AiMonthlyTokenLimit = 100_000,
    };

    public LicencePayloadBuilder With(Func<LicencePayload, LicencePayload> change)
    {
        _payload = change(_payload);
        return this;
    }

    public LicencePayload Build() => _payload;
}

/// <summary>An <see cref="ILicenceService"/> answering from a fixed snapshot, for tests of the readers.</summary>
public sealed class FixedLicenceService : ILicenceService
{
    private readonly DateTime _now;

    public FixedLicenceService(LicencePayload? payload, DateTime? nowUtc = null)
    {
        _now = nowUtc ?? DateTime.UtcNow;
        Current = payload is null
            ? LicenceSnapshot.None(null)
            : new LicenceSnapshot(new VerifiedLicence(payload, LicenceTestKit.Kid, "", "", "fingerprint"), null, _now);
    }

    /// <summary>Everything licensed, nothing limited: what an HTTP test host uses when it is not testing the licence.</summary>
    public static FixedLicenceService Unlimited() => new(LicenceTestKit.Payload(b => b.With(p => p with
    {
        MaxEmployees = 0, MaxUsers = 0, MaxCompanies = 0, MaxAdminUsers = 0, AiMonthlyTokenLimit = int.MaxValue,
        Modules = LicenceService.KnownModules.ToList(),
        OptInFeatures = LicenceService.KnownOptIns.ToList(),
    })));

    /// <summary>
    /// Everything licensed, with the given limits (0 = unlimited): what a test of a seat or company cap uses where it
    /// used to seed a <c>TenantSubscriptions</c> row.
    /// </summary>
    public static FixedLicenceService WithLimits(int maxUsers = 0, int maxAdminUsers = 0, int maxCompanies = 0, int maxEmployees = 0,
        int aiMonthlyTokenLimit = int.MaxValue) =>
        new(LicenceTestKit.Payload(b => b.With(p => p with
        {
            MaxEmployees = maxEmployees, MaxUsers = maxUsers, MaxCompanies = maxCompanies, MaxAdminUsers = maxAdminUsers,
            AiMonthlyTokenLimit = aiMonthlyTokenLimit,
            Modules = LicenceService.KnownModules.ToList(),
            OptInFeatures = LicenceService.KnownOptIns.ToList(),
        })));

    public LicenceSnapshot Current { get; }
    public LicenceState State => Current.StateAt(_now);
    public bool IsModuleLicensed(string moduleKey)
    {
        var module = Zayra.Api.Infrastructure.Modules.ModuleCatalog.TryGet(moduleKey);
        if (module is not null && module.Lock is Zayra.Api.Infrastructure.Modules.ModuleLock.Core or Zayra.Api.Infrastructure.Modules.ModuleLock.Statutory)
            return true;
        return State is LicenceState.Valid or LicenceState.Grace && Current.Modules.Contains(moduleKey);
    }
    public bool IsOptInLicensed(string featureKey) => State is LicenceState.Valid or LicenceState.Grace && Current.OptInFeatures.Contains(featureKey);
    public Task RefreshAsync(CancellationToken ct) => Task.CompletedTask;
}

public static class LicenceTestServices
{
    /// <summary>Replaces the application's licence with an unlimited one, so an HTTP test host is not refused 402.</summary>
    public static IServiceCollection UseUnlimitedLicence(this IServiceCollection services)
    {
        foreach (var d in services.Where(d => d.ServiceType == typeof(ILicenceService)).ToList()) services.Remove(d);
        services.AddSingleton<ILicenceService>(FixedLicenceService.Unlimited());
        return services;
    }

    /// <summary>A real <see cref="LicenceService"/> over <paramref name="scopes"/>, trusting only the kit's key.</summary>
    public static LicenceService RealService(IServiceScopeFactory scopes, LicenceTestKit kit, Func<DateTime>? clock = null) =>
        new(scopes, kit.Verifier(), NullLogger<LicenceService>.Instance, clock ?? (() => DateTime.UtcNow));
}
