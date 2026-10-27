using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Auth;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.FieldEncryption;
using Zayra.Api.Models;

namespace Zayra.Api.Tests.FieldEncryption;

/// <summary>Ephemeral keys only: every test generates its own KEK, nothing here is a real secret.</summary>
internal sealed class FeKit
{
    public FieldEncryptionOptions Options { get; }
    public ConfigKeyEncryptionKeyProvider Kek { get; }
    public MemoryKeyStore Store { get; } = new();
    public FieldKeyService Keys { get; }
    public string DbName { get; } = $"fe-{Guid.NewGuid():N}";

    public FeKit(FieldEncryptionMode mode = FieldEncryptionMode.DualWrite, int[]? kekVersions = null, int? active = null)
    {
        var versions = kekVersions ?? [1];
        Options = new FieldEncryptionOptions
        {
            Mode = mode,
            Keks = versions.ToDictionary(v => v, _ => RandomNumberGenerator.GetBytes(32)),
            ActiveKekVersion = active ?? versions.Max(),
        };
        Kek = new ConfigKeyEncryptionKeyProvider(Options);
        Keys = new FieldKeyService(Store, Kek);
    }

    /// <summary>A context over the SAME in-memory database; mode and interceptors vary per call.</summary>
    public ZayraDbContext Db(FieldEncryptionMode? mode = null, bool interceptors = true)
    {
        var opts = new FieldEncryptionOptions { Mode = mode ?? Options.Mode, Keks = Options.Keks, ActiveKekVersion = Options.ActiveKekVersion };
        var b = new DbContextOptionsBuilder<ZayraDbContext>().UseInMemoryDatabase(DbName);
        if (interceptors) b.AddInterceptors(new FieldEncryptionSaveInterceptor(opts, Keys), new FieldEncryptionQueryGuard(opts));
        return new ZayraDbContext(b.Options);
    }

    public FieldEncryptionBackfill Backfill(ZayraDbContext db, FieldEncryptionMode? mode = null) =>
        new(db, Keys, new FieldEncryptionOptions { Mode = mode ?? Options.Mode, Keks = Options.Keks, ActiveKekVersion = Options.ActiveKekVersion });

    /// <summary>Test shortcut: revoke immediately (the real flow is request -> cooling-off -> apply).</summary>
    public async Task RevokeNowAsync(Guid tenant, Guid? by = null)
    {
        var now = DateTime.UtcNow;
        await Keys.RequestRevokeAsync(tenant, by, now, now, default);
        await Keys.ApplyDueRevokeAsync(tenant, now, default);
    }

    public FieldEncryptionAdminService Admin(RecordingAudit? audit = null, FakeStepUp? stepUp = null, RecordingNotifier? notifier = null,
        FieldEncryptionMode? mode = null, TimeSpan? coolingOff = null, TimeProvider? clock = null) =>
        new(Store, Keys, new FieldEncryptionOptions { Mode = mode ?? Options.Mode, Keks = Options.Keks, ActiveKekVersion = Options.ActiveKekVersion, RevokeCoolingOff = coolingOff ?? TimeSpan.FromHours(24) },
            audit ?? new RecordingAudit(), stepUp ?? new FakeStepUp(), notifier ?? new RecordingNotifier(), clock);

    public static Employee NewEmployee(Guid tenant, string code, string iqama = "", string iban = "", string passport = "") => new()
    {
        TenantId = tenant, EmployeeCode = code, FullName = $"Person {code}", Status = "Active", JoiningDate = DateTime.UtcNow.Date,
        IqamaNumber = iqama, BankIban = iban, PassportNumber = passport,
    };

    public static RequestContext Ctx(Guid tenant) => new("127.0.0.1", "test", Guid.NewGuid(), tenant);
}

internal sealed class MemoryKeyStore : IFieldKeyStore
{
    private readonly object _gate = new();
    private readonly List<FieldEncryptionKey> _rows = [];
    public int Loads;

    private static FieldEncryptionKey Clone(FieldEncryptionKey r) => new()
    {
        Id = r.Id, TenantId = r.TenantId, KeyVersion = r.KeyVersion, Status = r.Status, WrappedDek = r.WrappedDek?.ToArray(),
        WrappedBlindIndexRoot = r.WrappedBlindIndexRoot?.ToArray(), KekVersion = r.KekVersion, CreatedAtUtc = r.CreatedAtUtc,
        RetiredAtUtc = r.RetiredAtUtc, RevokedAtUtc = r.RevokedAtUtc, RevokedByUserId = r.RevokedByUserId,
        RecoveryExportedAtUtc = r.RecoveryExportedAtUtc, RecoveryFingerprint = r.RecoveryFingerprint,
        RowVersion = r.RowVersion, CanaryEnc = r.CanaryEnc?.ToArray(), RootCheck = r.RootCheck?.ToArray(), ActiveAtRevoke = r.ActiveAtRevoke,
        RevokeRequestedAtUtc = r.RevokeRequestedAtUtc, RevokeEffectiveAtUtc = r.RevokeEffectiveAtUtc, RevokeRequestedByUserId = r.RevokeRequestedByUserId,
    };

    public Task<List<FieldEncryptionKey>> LoadAsync(Guid tenantId, CancellationToken ct)
    {
        lock (_gate) { Loads++; return Task.FromResult(_rows.Where(r => r.TenantId == tenantId).OrderBy(r => r.KeyVersion).Select(Clone).ToList()); }
    }

    /// <summary>Serialises like the row lock: the work and its commit are atomic per store, and a throw applies nothing.</summary>
    public Task TransactAsync(Guid tenantId, Func<List<FieldEncryptionKey>, IReadOnlyCollection<FieldEncryptionKey>> work, CancellationToken ct)
    {
        lock (_gate)
        {
            var rows = _rows.Where(r => r.TenantId == tenantId).OrderBy(r => r.KeyVersion).Select(Clone).ToList();
            var add = work(rows);
            if (add.Any(a => _rows.Any(r => r.TenantId == a.TenantId && r.KeyVersion == a.KeyVersion))) throw new KeyStoreConflictException("unique (tenant, version)");
            if (add.Count(a => a.Status == FieldEncryptionKey.Active) + rows.Count(r => r.Status == FieldEncryptionKey.Active) > 1) throw new KeyStoreConflictException("one active key per tenant");
            foreach (var r in rows) { var i = _rows.FindIndex(x => x.Id == r.Id); r.RowVersion = _rows[i].RowVersion + 1; _rows[i] = Clone(r); }
            _rows.AddRange(add.Select(Clone));
            return Task.CompletedTask;
        }
    }

    public Task<bool> AnyAsync(CancellationToken ct) { lock (_gate) return Task.FromResult(_rows.Count > 0); }
    public Task<List<Guid>> TenantsWithDueRevokeAsync(DateTime nowUtc, CancellationToken ct)
    {
        lock (_gate) return Task.FromResult(_rows.Where(r => r.Status != FieldEncryptionKey.Revoked && r.RevokeEffectiveAtUtc <= nowUtc).Select(r => r.TenantId).Distinct().ToList());
    }
    public List<FieldEncryptionKey> Snapshot() { lock (_gate) return _rows.Select(Clone).ToList(); }
}

internal sealed class FakeStepUp : IStepUpVerifier
{
    public const string Good = "123456";
    public List<(ActorKind Kind, Guid? User, Guid Tenant)> Calls { get; } = [];
    public Task<bool> VerifyAsync(ActorKind kind, Guid? userId, Guid tenantId, string? totpCode, CancellationToken ct)
    {
        Calls.Add((kind, userId, tenantId));
        return Task.FromResult(totpCode == Good);
    }
}

internal sealed class RecordingNotifier : ICustodyNotifier
{
    public List<(Guid Tenant, string Title, string Message)> Sent { get; } = [];
    public Task NotifyTenantAdminsAsync(Guid tenantId, string title, string message, CancellationToken ct) { Sent.Add((tenantId, title, message)); return Task.CompletedTask; }
}

internal sealed class RecordingAudit : IAuditService
{
    public List<(string Action, string Entity, string? Id, string? Metadata)> Entries { get; } = [];
    public Task WriteAsync(string action, string entityName, string? entityId, RequestContext context, string? metadata, CancellationToken cancellationToken)
    {
        Entries.Add((action, entityName, entityId, metadata));
        return Task.CompletedTask;
    }
}

internal sealed class TestClock(DateTime startUtc) : TimeProvider
{
    private DateTime _now = DateTime.SpecifyKind(startUtc, DateTimeKind.Utc);
    public void Advance(TimeSpan by) => _now += by;
    public override DateTimeOffset GetUtcNow() => new(_now);
}
