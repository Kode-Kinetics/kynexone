using System.Security.Claims;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Data;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Policy;

/// <summary>
/// Reads a tenant's policy settings. Every method resolves the tenant from the signed-in request
/// unless a tenant id is passed (background jobs). An unregistered key throws
/// <see cref="UnknownPolicyKeyException"/>; asking for the wrong type throws <see cref="InvalidOperationException"/>.
/// A missing (or unreadable) stored value yields the registry default, which is the strict choice.
/// </summary>
public interface IPolicySettings
{
    Task<bool> GetBoolAsync(string key, CancellationToken ct = default);
    Task<string> GetStringAsync(string key, CancellationToken ct = default);
    Task<string> GetEnumAsync(string key, CancellationToken ct = default);
    Task<TEnum> GetEnumAsync<TEnum>(string key, CancellationToken ct = default) where TEnum : struct, Enum;
    Task<IReadOnlyList<string>> GetCsvAsync(string key, CancellationToken ct = default);

    Task<bool> GetBoolAsync(Guid tenantId, string key, CancellationToken ct = default);
    Task<string> GetStringAsync(Guid tenantId, string key, CancellationToken ct = default);
    Task<string> GetEnumAsync(Guid tenantId, string key, CancellationToken ct = default);
    Task<TEnum> GetEnumAsync<TEnum>(Guid tenantId, string key, CancellationToken ct = default) where TEnum : struct, Enum;
    Task<IReadOnlyList<string>> GetCsvAsync(Guid tenantId, string key, CancellationToken ct = default);
}

public sealed record PolicySettingView(PolicyDefinition Definition, string Value, bool IsDefault);

public enum PolicySetStatus { Changed, Unchanged, UnknownKey, Invalid }

public sealed record PolicySetResult(PolicySetStatus Status, string? Error, PolicySettingView? Setting);

public sealed record PolicyActor(Guid? UserId, string Name, string IpAddress);

public interface IPolicySettingsAdmin
{
    Task<IReadOnlyList<PolicySettingView>> ListAsync(Guid tenantId, CancellationToken ct = default);
    Task<PolicySetResult> SetAsync(Guid tenantId, string key, string? value, PolicyActor actor, CancellationToken ct = default);
}

public sealed class PolicySettingsService : IPolicySettings, IPolicySettingsAdmin
{
    public const string AuditEntityType = "PolicySetting";

    private readonly ZayraDbContext _db;
    private readonly IHttpContextAccessor? _http;

    public PolicySettingsService(ZayraDbContext db, IHttpContextAccessor? http = null) { _db = db; _http = http; }

    private Guid RequestTenant() =>
        Guid.TryParse(_http?.HttpContext?.User.FindFirst("tenant_id")?.Value, out var id) && id != Guid.Empty
            ? id
            : throw new InvalidOperationException("No tenant on the current request; pass a tenant id explicitly.");

    /// <summary>Stored value for the tenant, or null when the tenant has no override row.</summary>
    private async Task<string?> StoredAsync(Guid tenantId, string key, CancellationToken ct)
    {
        return await ScopedBypass.TenantWide(_db.SystemSettings, tenantId, "Policy settings are read outside an HTTP request (jobs) where the ambient tenant filter hides rows; the tenant is pinned by ScopedBypass.").AsNoTracking()
            .Where(s => s.Category == PolicyRegistry.StorageCategory && s.SettingKey == key)
            .Select(s => s.SettingValue)
            .FirstOrDefaultAsync(ct);
    }

    private async Task<string> EffectiveAsync(Guid tenantId, PolicyDefinition def, CancellationToken ct)
    {
        var stored = await StoredAsync(tenantId, def.Key, ct);
        if (stored is null) return def.Default;
        if (def.Type == PolicyValueType.Csv) return string.Join(',', PolicyRegistry.ParseCsv(stored));
        return PolicyRegistry.TryNormalise(def, stored, out var n, out _) ? n : def.Default;
    }

    private async Task<string> ReadAsync(Guid tenantId, string key, PolicyValueType expected, CancellationToken ct)
    {
        var def = PolicyRegistry.Get(key);
        if (def.Type != expected)
            throw new InvalidOperationException($"Policy '{key}' is {def.Type}, not {expected}.");
        return await EffectiveAsync(tenantId, def, ct);
    }

    public Task<bool> GetBoolAsync(string key, CancellationToken ct = default) => GetBoolAsync(RequestTenant(), key, ct);
    public Task<string> GetStringAsync(string key, CancellationToken ct = default) => GetStringAsync(RequestTenant(), key, ct);
    public Task<string> GetEnumAsync(string key, CancellationToken ct = default) => GetEnumAsync(RequestTenant(), key, ct);
    public Task<TEnum> GetEnumAsync<TEnum>(string key, CancellationToken ct = default) where TEnum : struct, Enum => GetEnumAsync<TEnum>(RequestTenant(), key, ct);
    public Task<IReadOnlyList<string>> GetCsvAsync(string key, CancellationToken ct = default) => GetCsvAsync(RequestTenant(), key, ct);

    public async Task<bool> GetBoolAsync(Guid tenantId, string key, CancellationToken ct = default) =>
        await ReadAsync(tenantId, key, PolicyValueType.Bool, ct) == "true";

    public async Task<string> GetStringAsync(Guid tenantId, string key, CancellationToken ct = default)
    {
        var def = PolicyRegistry.Get(key); // unknown key throws before anything else
        return await EffectiveAsync(tenantId, def, ct);
    }

    public Task<string> GetEnumAsync(Guid tenantId, string key, CancellationToken ct = default) =>
        ReadAsync(tenantId, key, PolicyValueType.Enum, ct);

    public async Task<TEnum> GetEnumAsync<TEnum>(Guid tenantId, string key, CancellationToken ct = default) where TEnum : struct, Enum
    {
        var v = await ReadAsync(tenantId, key, PolicyValueType.Enum, ct);
        return Enum.TryParse<TEnum>(v, true, out var e)
            ? e
            : throw new InvalidOperationException($"Policy '{key}' value '{v}' is not a member of {typeof(TEnum).Name}.");
    }

    public async Task<IReadOnlyList<string>> GetCsvAsync(Guid tenantId, string key, CancellationToken ct = default) =>
        PolicyRegistry.ParseCsv(await ReadAsync(tenantId, key, PolicyValueType.Csv, ct));

    // ── admin ────────────────────────────────────────────────────────────────

    public async Task<IReadOnlyList<PolicySettingView>> ListAsync(Guid tenantId, CancellationToken ct = default)
    {
        var rows = await ScopedBypass.TenantWide(_db.SystemSettings, tenantId, "Policy settings are read outside an HTTP request (jobs) where the ambient tenant filter hides rows; the tenant is pinned by ScopedBypass.").AsNoTracking()
            .Where(s => s.Category == PolicyRegistry.StorageCategory)
            .ToDictionaryAsync(s => s.SettingKey, s => s.SettingValue, ct);
        return PolicyRegistry.All.Select(def =>
        {
            var value = def.Default;
            if (rows.TryGetValue(def.Key, out var stored))
                value = def.Type == PolicyValueType.Csv ? string.Join(',', PolicyRegistry.ParseCsv(stored))
                    : PolicyRegistry.TryNormalise(def, stored, out var n, out _) ? n : def.Default;
            return new PolicySettingView(def, value, value == def.Default);
        }).ToList();
    }

    public async Task<PolicySetResult> SetAsync(Guid tenantId, string key, string? value, PolicyActor actor, CancellationToken ct = default)
    {
        if (!PolicyRegistry.TryGet(key, out var def))
            return new PolicySetResult(PolicySetStatus.UnknownKey, $"'{key}' is not a known policy setting.", null);
        if (!PolicyRegistry.TryNormalise(def, value, out var normalised, out var error))
            return new PolicySetResult(PolicySetStatus.Invalid, error, null);

        var row = await ScopedBypass.TenantWide(_db.SystemSettings, tenantId, "Policy settings are read outside an HTTP request (jobs) where the ambient tenant filter hides rows; the tenant is pinned by ScopedBypass.")
            .FirstOrDefaultAsync(s => s.Category == PolicyRegistry.StorageCategory && s.SettingKey == key, ct);
        var old = row is null ? def.Default
            : def.Type == PolicyValueType.Csv ? string.Join(',', PolicyRegistry.ParseCsv(row.SettingValue))
            : PolicyRegistry.TryNormalise(def, row.SettingValue, out var o, out _) ? o : def.Default;

        if (old == normalised)
            return new PolicySetResult(PolicySetStatus.Unchanged, null, new PolicySettingView(def, old, old == def.Default));

        if (row is null)
        {
            _db.SystemSettings.Add(new SystemSetting
            {
                TenantId = tenantId, Category = PolicyRegistry.StorageCategory, SettingKey = key, SettingValue = normalised,
                DataType = def.Type == PolicyValueType.Bool ? "bool" : "string", Description = def.Description, UpdatedBy = actor.UserId,
            });
        }
        else
        {
            row.SettingValue = normalised; row.UpdatedAtUtc = DateTime.UtcNow; row.UpdatedBy = actor.UserId;
        }

        _db.AdminAuditLogs.Add(new AdminAuditLog
        {
            TenantId = tenantId, CompanyId = null, EntityType = AuditEntityType, EntityId = key, Action = "Updated",
            OldValuesJson = JsonSerializer.Serialize(new { key, value = old }),
            NewValuesJson = JsonSerializer.Serialize(new { key, value = normalised }),
            PerformedBy = actor.UserId, PerformedByName = actor.Name, IpAddress = actor.IpAddress,
        });
        await _db.SaveChangesAsync(ct);
        return new PolicySetResult(PolicySetStatus.Changed, null, new PolicySettingView(def, normalised, normalised == def.Default));
    }
}
