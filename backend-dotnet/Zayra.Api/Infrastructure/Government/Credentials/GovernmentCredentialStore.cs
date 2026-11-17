using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Auth;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.FieldEncryption;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Government.Credentials;

/// <summary>What anyone outside a transport may know about a credential. Never the secret (ratchet: GovernmentSecretDtoRatchetTests).</summary>
public sealed record GovernmentCredentialMetadata(
    string Body,
    string Environment,
    string Kind,
    Guid? CompanyId,
    string Fingerprint,
    int Version,
    DateTime? ExpiresAtUtc,
    DateTime? LastUsedAtUtc,
    DateTime CreatedAtUtc,
    Guid? CreatedBy,
    DateTime? RotatedAtUtc,
    Guid? RotatedBy);

/// <summary>
/// An opened secret, for a transport only. Not a DTO: no controller may reference it (GovernmentSecretDtoRatchetTests), and its
/// <see cref="ToString"/> never prints the secret.
/// </summary>
public sealed class UnsealedGovernmentCredential(string body, string environment, string kind, Guid? companyId, int version, string secret)
{
    public string Body { get; } = body;
    public string Environment { get; } = environment;
    public string Kind { get; } = kind;
    public Guid? CompanyId { get; } = companyId;
    public int Version { get; } = version;
    public string Secret { get; } = secret;
    public override string ToString() => $"GovernmentCredential({Body}/{Environment} v{Version}, secret withheld)";
}

/// <summary>Credential storage is switched off on this server because field-encryption key wrapping is not configured (security R1).</summary>
public sealed class GovernmentCredentialsUnavailableException()
    : FieldEncryptionUnavailableException("Government credential storage needs field encryption switched on with a configured KEK.",
        503, GovernmentCredentialStore.UnavailableCode)
{
    public override string SafeMessage =>
        "Credentials for government and bank services cannot be stored or used on this server yet: its encryption keys are not set up. "
        + "Nothing was saved. Contact your platform operator.";
}

/// <summary>
/// The store for customer (establishment) secrets of government and bank bodies (architecture 3.4, security R1), built on the
/// existing field encryption: the tenant's DEK from <see cref="FieldKeyService"/>, wrapped by <see cref="IKeyEncryptionKeyProvider"/>.
/// <list type="bullet">
/// <item><b>Fails closed.</b> Unless field encryption is on (mode other than Off) and a KEK is configured, it refuses to save,
/// open or describe a credential (<see cref="GovernmentCredentialsUnavailableException"/>, 503): no customer secret is accepted
/// until key wrapping is on (security R1, CTO gate C8). The KEK escrow holder is still to be named, so no live credential
/// belongs on a real deployment yet.</item>
/// <item><b>AAD</b> = tenant + company + body + environment + version, so a ciphertext moved to another row, tenant, company or
/// environment, or replayed from an older version, does not open.</item>
/// <item><b>Metadata only</b> leaves this class except through <see cref="OpenAsync"/>, which only a transport calls; opening
/// records <c>last_used_at_utc</c>. A save over an existing credential is a rotation: the version goes up and who rotated it
/// and when are recorded. Every save, rotation and removal stages an audit row (never the secret, only its fingerprint).</item>
/// </list>
/// </summary>
public sealed class GovernmentCredentialStore(
    ZayraDbContext db, FieldKeyService keys, IKeyEncryptionKeyProvider kek, FieldEncryptionOptions fieldEncryption, IAuditService audit)
{
    public const string UnavailableCode = "government_credentials_unavailable";
    public const int MaxSecretLength = 16_384;
    private const string FingerprintTable = "government_credentials";
    private const string FingerprintColumn = "secret";

    /// <summary>Field encryption is switched on (a mode other than Off) AND a KEK is configured to wrap the tenant keys.</summary>
    public bool IsAvailable => fieldEncryption.Enabled && kek.IsConfigured;

    public static string Aad(Guid tenantId, Guid? companyId, string body, string environment, int version) =>
        $"gov1|{tenantId:N}|{(companyId is { } c ? c.ToString("N") : "-")}|{body}|{environment}|{version}";

    public async Task<GovernmentCredentialMetadata> SaveAsync(Guid tenantId, Guid? companyId, string body, string environment, string kind,
        string secret, DateTime? expiresAtUtc, RequestContext context, CancellationToken ct)
    {
        RequireAvailable();
        if (tenantId == Guid.Empty) throw new ArgumentException("A tenant is required.", nameof(tenantId));
        if (!GovernmentBodies.IsKnown(body)) throw new ArgumentException($"Unknown body '{body}'.", nameof(body));
        if (!GovernmentEnvironments.IsKnown(environment)) throw new ArgumentException($"Unknown environment '{environment}'.", nameof(environment));
        if (!GovernmentCredentialKinds.IsKnown(kind)) throw new ArgumentException($"Unknown credential kind '{kind}'.", nameof(kind));
        if (string.IsNullOrWhiteSpace(secret) || secret.Length > MaxSecretLength)
            throw new ArgumentException($"The secret must be between 1 and {MaxSecretLength} characters.", nameof(secret));
        if (expiresAtUtc is { } expiry && expiry <= DateTime.UtcNow) throw new ArgumentException("The expiry must be in the future.", nameof(expiresAtUtc));

        var ring = await keys.GetRingAsync(tenantId, createIfMissing: true, ct);
        var row = await Find(tenantId, companyId, body, environment).SingleOrDefaultAsync(ct);
        var now = DateTime.UtcNow;
        var rotation = row is not null;
        if (row is null)
        {
            row = new GovernmentCredential
            {
                TenantId = tenantId, CompanyId = companyId, Body = body, Environment = environment,
                CredentialVersion = 1, CreatedAtUtc = now, CreatedBy = context.UserId,
            };
            db.GovernmentCredentials.Add(row);
        }
        else
        {
            row.CredentialVersion++;
            row.RotatedAtUtc = now;
            row.RotatedBy = context.UserId;
            row.LastUsedAtUtc = null;
        }
        row.Kind = kind;
        row.ExpiresAtUtc = expiresAtUtc;
        row.DekVersion = ring.ActiveVersion;
        row.Ciphertext = FieldEnvelope.Seal(ring.ActiveDek, checked((byte)ring.ActiveVersion), secret,
            Aad(tenantId, companyId, body, environment, row.CredentialVersion));
        row.Fingerprint = Fingerprint(ring, secret);

        audit.Stage(rotation ? "government.credential.rotated" : "government.credential.saved", nameof(GovernmentCredential), row.Id.ToString(),
            context with { TenantId = tenantId }, AuditJson(row), companyId);
        await SaveOrForgetAsync(row, ct);
        if (row.CompanyId != companyId)
        {
            // The company write guard stamped a company-scoped caller's own company onto a tenant-wide request (its rule for every
            // company-scoped configuration row). Reseal under the company the row now belongs to, so it opens where it is stored.
            row.Ciphertext = FieldEnvelope.Seal(ring.ActiveDek, checked((byte)ring.ActiveVersion), secret,
                Aad(tenantId, row.CompanyId, body, environment, row.CredentialVersion));
            await SaveOrForgetAsync(row, ct);
        }
        return ToMetadata(row);
    }

    public async Task<GovernmentCredentialMetadata?> GetMetadataAsync(Guid tenantId, Guid? companyId, string body, string environment, CancellationToken ct)
    {
        RequireAvailable();
        var row = await Find(tenantId, companyId, body, environment).AsNoTracking().SingleOrDefaultAsync(ct);
        return row is null ? null : ToMetadata(row);
    }

    /// <summary>Removes the credential (the ciphertext is destroyed with the row). Allowed without a KEK: removing a secret is always safe.</summary>
    public async Task<bool> DeleteAsync(Guid tenantId, Guid? companyId, string body, string environment, RequestContext context, CancellationToken ct)
    {
        var row = await Find(tenantId, companyId, body, environment).SingleOrDefaultAsync(ct);
        if (row is null) return false;
        db.GovernmentCredentials.Remove(row);
        audit.Stage("government.credential.removed", nameof(GovernmentCredential), row.Id.ToString(),
            context with { TenantId = tenantId }, AuditJson(row), companyId);
        await SaveOrForgetAsync(row, ct);
        return true;
    }

    /// <summary>
    /// Opens the secret for a transport: the company's own credential first, then the tenant-wide one. Null when there is none,
    /// it has expired, or it does not open (wrong AAD, moved row, unknown key version). Records last-used unless <paramref name="probeOnly"/>.
    /// </summary>
    public async Task<UnsealedGovernmentCredential?> OpenAsync(Guid tenantId, Guid? companyId, string body, string environment,
        CancellationToken ct, bool probeOnly = false)
    {
        RequireAvailable();
        var now = DateTime.UtcNow;
        var row = companyId is not null ? await Find(tenantId, companyId, body, environment).SingleOrDefaultAsync(ct) : null;
        if (row is null || IsExpired(row, now)) row = await Find(tenantId, null, body, environment).SingleOrDefaultAsync(ct);
        if (row is null || row.TenantId != tenantId || IsExpired(row, now)) return null;

        string secret;
        try
        {
            var ring = await keys.GetRingAsync(tenantId, createIfMissing: false, ct);
            if (!ring.Deks.TryGetValue(FieldEnvelope.DekVersionOf(row.Ciphertext), out var dek)) return null;
            secret = FieldEnvelope.Open(dek, row.Ciphertext, Aad(row.TenantId, row.CompanyId, row.Body, row.Environment, row.CredentialVersion),
                MaxEnvelopeLength);
        }
        catch (CryptographicException) { return null; }
        catch (InvalidOperationException ex) when (ex is not FieldEncryptionUnavailableException) { return null; }

        if (!probeOnly)
        {
            row.LastUsedAtUtc = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
        }
        return new UnsealedGovernmentCredential(row.Body, row.Environment, row.Kind, row.CompanyId, row.CredentialVersion, secret);
    }

    /// <summary>G3 of the live gate: a credential is present, unexpired and opens. False (never a throw) when storage is off.</summary>
    public async Task<bool> IsUsableAsync(Guid tenantId, Guid? companyId, string body, string environment, CancellationToken ct)
    {
        if (!IsAvailable) return false;
        try { return await OpenAsync(tenantId, companyId, body, environment, ct, probeOnly: true) is not null; }
        catch (FieldEncryptionUnavailableException) { return false; }
    }

    /// <summary>The largest envelope a <see cref="MaxSecretLength"/> secret can produce (UTF-8, up to 4 bytes a character).</summary>
    private const int MaxEnvelopeLength = FieldEnvelope.MinLength + MaxSecretLength * 4;

    private IQueryable<GovernmentCredential> Find(Guid tenantId, Guid? companyId, string body, string environment) =>
        db.GovernmentCredentials.Where(x => x.TenantId == tenantId && x.CompanyId == companyId && x.Body == body && x.Environment == environment);

    /// <summary>
    /// Saves; on any refusal (a company or tenant write guard, a constraint) puts the row back as it was and drops the staged audit,
    /// so nothing of the refused change lingers in the request's context.
    /// </summary>
    private async Task SaveOrForgetAsync(GovernmentCredential row, CancellationToken ct)
    {
        try { await db.SaveChangesAsync(ct); }
        catch
        {
            audit.DiscardStaged();
            var entry = db.Entry(row);
            if (entry.State == EntityState.Added) entry.State = EntityState.Detached;
            else
            {
                entry.CurrentValues.SetValues(entry.OriginalValues);
                entry.State = EntityState.Unchanged;
            }
            throw;
        }
    }

    private static bool IsExpired(GovernmentCredential row, DateTime now) => row.ExpiresAtUtc is { } expiry && expiry <= now;

    private void RequireAvailable()
    {
        if (!IsAvailable) throw new GovernmentCredentialsUnavailableException();
    }

    private static string Fingerprint(TenantKeyRing ring, string secret)
    {
        // Keyed like a blind index (HKDF from the tenant's blind-index root, its own label), so a database reader cannot test guesses.
        var key = HKDF.DeriveKey(HashAlgorithmName.SHA256, ring.BlindIndexRoot, 32, salt: null,
            info: System.Text.Encoding.UTF8.GetBytes($"gov-fp:{FingerprintTable}.{FingerprintColumn}"));
        try
        {
            var mac = HMACSHA256.HashData(key, System.Text.Encoding.UTF8.GetBytes(secret));
            return Convert.ToHexString(mac, 0, 8).ToLowerInvariant();
        }
        finally { CryptographicOperations.ZeroMemory(key); }
    }

    private static string AuditJson(GovernmentCredential row) => JsonSerializer.Serialize(new
    {
        body = row.Body, environment = row.Environment, kind = row.Kind, companyId = row.CompanyId,
        version = row.CredentialVersion, fingerprint = row.Fingerprint, expiresAtUtc = row.ExpiresAtUtc,
    });

    private static GovernmentCredentialMetadata ToMetadata(GovernmentCredential row) => new(
        row.Body, row.Environment, row.Kind, row.CompanyId, row.Fingerprint, row.CredentialVersion, row.ExpiresAtUtc,
        row.LastUsedAtUtc, row.CreatedAtUtc, row.CreatedBy, row.RotatedAtUtc, row.RotatedBy);
}
