using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Zayra.Api.Application.Auth;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.FieldEncryption;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Government.Credentials;

/// <summary>What anyone outside a transport may know about a credential. Never the secret (ratchet: GovernmentRatchetTests C).</summary>
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
    Guid? RotatedBy,
    string? ClientIdentifier = null,
    string? BaseUrlHost = null,
    string? AuthorizationHost = null);

/// <summary>The non-secret things a credential is bound to: its client id and the hosts it was issued for. All go into the AAD.</summary>
public sealed record GovernmentCredentialBinding(string? ClientIdentifier = null, string? BaseUrlHost = null, string? TokenUrlHost = null);

/// <summary>
/// An opened secret, for a transport only. Not a DTO: no controller may reference it (GovernmentRatchetTests C), and its
/// <see cref="ToString"/> never prints the secret or the client id.
/// </summary>
public sealed class UnsealedGovernmentCredential(string body, string environment, string kind, Guid? companyId, int version, string secret,
    string? clientIdentifier = null, string? baseUrlHost = null, string? tokenUrlHost = null)
{
    public string Body { get; } = body;
    public string Environment { get; } = environment;
    public string Kind { get; } = kind;
    public Guid? CompanyId { get; } = companyId;
    public int Version { get; } = version;
    public string Secret { get; } = secret;
    public string? ClientIdentifier { get; } = clientIdentifier;
    public string? BaseUrlHost { get; } = baseUrlHost;
    public string? TokenUrlHost { get; } = tokenUrlHost;
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

/// <summary>A credential request the store refuses for a reason the caller can act on: a stable code, an HTTP status, EN and AR text.</summary>
public sealed class GovernmentCredentialRefusedException(string code, int statusCode, string message, string messageAr) : InvalidOperationException(message)
{
    public string Code { get; } = code;
    public int StatusCode { get; } = statusCode;
    public string MessageAr { get; } = messageAr;
}

/// <summary>
/// The store for customer (establishment) secrets of government and bank bodies (architecture 3.4, security R1), built on the
/// existing field encryption: the tenant's DEK from <see cref="FieldKeyService"/>, wrapped by <see cref="IKeyEncryptionKeyProvider"/>.
/// <list type="bullet">
/// <item><b>Fails closed.</b> Unless field encryption is on (mode other than Off) and a KEK is configured, it refuses to save,
/// open or describe a credential (<see cref="GovernmentCredentialsUnavailableException"/>, 503). Production-environment credentials
/// are refused as well until the operator sets <see cref="AcceptLiveCredentialsKey"/> (default false; it stays false until the KEK
/// escrow holder is named, CTO gate C8).</item>
/// <item><b>AAD</b> = tenant + company + body + environment + version + client id + hosts, so a ciphertext moved to another row,
/// tenant, company, environment or host binding, or replayed from an older version, does not open.</item>
/// <item><b>Company first, no silent fallback.</b> A company with its own credential uses it or nothing: an expired company
/// credential is refused, it never falls back to the tenant-wide one. A company with none uses the tenant-wide one.</item>
/// <item><b>Metadata only</b> leaves this class except through <see cref="OpenAsync"/>, which only a transport calls; opening records
/// <c>last_used_at_utc</c> with a single-row update that never flushes the caller's pending changes. A save over an existing
/// credential is a rotation guarded by the version (a concurrency token): two racing rotations give one winner and a 409.</item>
/// </list>
/// </summary>
public sealed class GovernmentCredentialStore(
    ZayraDbContext db, FieldKeyService keys, IKeyEncryptionKeyProvider kek, FieldEncryptionOptions fieldEncryption, IAuditService audit,
    IConfiguration? configuration = null)
{
    public const string UnavailableCode = "government_credentials_unavailable";
    public const string LiveNotAcceptedCode = "government_live_credentials_not_accepted";
    public const string ConflictCode = "government_credential_conflict";
    public const string CompanyRequiredCode = "company_scope_required";
    public const string CompanyUnknownCode = "government_credential_company_unknown";
    public const string InvalidCode = "government_credential_invalid";
    public const string HostRequiredCode = "government_credential_host_required";
    /// <summary>Operator flag, default false: production credentials are refused until it is true (after the KEK escrow holder is named).</summary>
    public const string AcceptLiveCredentialsKey = "Government:Credentials:AcceptLiveCredentials";
    public const int MaxSecretLength = 16_384;
    private const string FingerprintTable = "government_credentials";
    private const string FingerprintColumn = "secret";

    /// <summary>Field encryption is switched on (a mode other than Off) AND a KEK is configured to wrap the tenant keys.</summary>
    public bool IsAvailable => fieldEncryption.Enabled && kek.IsConfigured;

    public bool AcceptsLiveCredentials => bool.TryParse(configuration?[AcceptLiveCredentialsKey], out var on) && on;

    public static string Aad(Guid tenantId, Guid? companyId, string body, string environment, int version,
        string? clientIdentifier = null, string? baseUrlHost = null, string? tokenUrlHost = null) =>
        $"gov2|{tenantId:N}|{(companyId is { } c ? c.ToString("N") : "-")}|{body}|{environment}|{version}|{clientIdentifier}|{baseUrlHost}|{tokenUrlHost}";

    public async Task<GovernmentCredentialMetadata> SaveAsync(Guid tenantId, Guid? companyId, string body, string environment, string kind,
        string secret, DateTime? expiresAtUtc, RequestContext context, CancellationToken ct, GovernmentCredentialBinding? binding = null)
    {
        RequireAvailable(environment);
        if (tenantId == Guid.Empty) throw new ArgumentException("A tenant is required.", nameof(tenantId));
        if (!GovernmentBodies.IsKnown(body)) throw new ArgumentException($"Unknown body '{body}'.", nameof(body));
        if (!GovernmentCredentialKinds.IsKnown(kind)) throw new ArgumentException($"Unknown credential kind '{kind}'.", nameof(kind));
        if (string.IsNullOrWhiteSpace(secret) || secret.Length > MaxSecretLength)
            throw new ArgumentException($"The secret must be between 1 and {MaxSecretLength} characters.", nameof(secret));
        var expires = NormaliseUtc(expiresAtUtc);
        if (expires is { } expiry && expiry <= DateTime.UtcNow) throw new ArgumentException("The expiry must be in the future.", nameof(expiresAtUtc));
        binding = Normalise(binding);
        // A bank credential, and every production credential, names the hosts it was issued for (the API host; and the token
        // host for an OAuth client), so the transport can refuse any other. Only a sandbox credential of a government body may omit it.
        if (GovernmentBodies.IsBank(body) || environment == GovernmentEnvironments.Production)
        {
            if (binding.BaseUrlHost is null)
                throw new GovernmentCredentialRefusedException(HostRequiredCode, 400,
                    "Give the API host this credential was issued for (for example api.bank.example).",
                    "أدخل اسم خادم الواجهة البرمجية الذي صدرت له بيانات الاعتماد هذه.");
            if (kind == GovernmentCredentialKinds.OAuthClient && binding.TokenUrlHost is null)
                throw new GovernmentCredentialRefusedException(HostRequiredCode, 400,
                    "Give the token (sign-in) host this OAuth client was issued for.",
                    "أدخل اسم خادم الرموز (تسجيل الدخول) الذي صدر له عميل OAuth هذا.");
        }

        // The company is settled BEFORE anything is sealed, so the AAD names the company the row is stored under and the audit row
        // tells the truth: a caller narrowed to one company who names none saves (or rotates) that company's own credential.
        var effectiveCompany = companyId ?? db.SingleScopedCompanyId;
        if (effectiveCompany is { } named && !await db.Companies.AsNoTracking().AnyAsync(c => c.Id == named && c.TenantId == tenantId, ct))
            throw new GovernmentCredentialRefusedException(CompanyUnknownCode, 400, "The company is not part of this workspace.", "الشركة ليست ضمن مساحة العمل هذه.");

        var ring = await keys.GetRingAsync(tenantId, createIfMissing: true, ct);
        var row = await Find(tenantId, effectiveCompany, body, environment).SingleOrDefaultAsync(ct);
        var now = DateTime.UtcNow;
        var rotation = row is not null;
        if (row is null)
        {
            row = new GovernmentCredential
            {
                TenantId = tenantId, CompanyId = effectiveCompany, Body = body, Environment = environment,
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
        row.ExpiresAtUtc = expires;
        row.ClientIdentifier = binding.ClientIdentifier;
        row.BaseUrlHost = binding.BaseUrlHost;
        row.TokenUrlHost = binding.TokenUrlHost;
        row.DekVersion = ring.ActiveVersion;
        row.Ciphertext = FieldEnvelope.Seal(ring.ActiveDek, checked((byte)ring.ActiveVersion), secret,
            Aad(tenantId, effectiveCompany, body, environment, row.CredentialVersion, row.ClientIdentifier, row.BaseUrlHost, row.TokenUrlHost));
        row.Fingerprint = Fingerprint(ring, secret);

        audit.Stage(rotation ? "government.credential.rotated" : "government.credential.saved", nameof(GovernmentCredential), row.Id.ToString(),
            context with { TenantId = tenantId }, AuditJson(row), effectiveCompany);
        await SaveOrForgetAsync(row, ct);
        if (row.CompanyId != effectiveCompany)
        {
            // Unreachable while the company is settled above; kept as a fail-closed check (the row would not open under its AAD).
            throw new GovernmentCredentialRefusedException(CompanyRequiredCode, 400, "Choose the company this credential belongs to.", "اختر الشركة التي تخص بيانات الاعتماد هذه.");
        }
        return ToMetadata(row);
    }

    public async Task<GovernmentCredentialMetadata?> GetMetadataAsync(Guid tenantId, Guid? companyId, string body, string environment, CancellationToken ct)
    {
        RequireAvailable(null);
        var row = await Find(tenantId, companyId ?? db.SingleScopedCompanyId, body, environment).AsNoTracking().SingleOrDefaultAsync(ct);
        return row is null ? null : ToMetadata(row);
    }

    /// <summary>Removes the credential (the ciphertext is destroyed with the row). Allowed without a KEK: removing a secret is always safe.</summary>
    public async Task<bool> DeleteAsync(Guid tenantId, Guid? companyId, string body, string environment, RequestContext context, CancellationToken ct)
    {
        var effectiveCompany = companyId ?? db.SingleScopedCompanyId;
        var row = await Find(tenantId, effectiveCompany, body, environment).SingleOrDefaultAsync(ct);
        if (row is null) return false;
        db.GovernmentCredentials.Remove(row);
        audit.Stage("government.credential.removed", nameof(GovernmentCredential), row.Id.ToString(),
            context with { TenantId = tenantId }, AuditJson(row), row.CompanyId);
        await SaveOrForgetAsync(row, ct);
        return true;
    }

    /// <summary>
    /// Opens the secret for a transport: the company's own credential if it has one (expired or unopenable means null, never the
    /// tenant-wide one), else the tenant-wide one. Records last-used unless <paramref name="probeOnly"/>.
    /// </summary>
    public async Task<UnsealedGovernmentCredential?> OpenAsync(Guid tenantId, Guid? companyId, string body, string environment,
        CancellationToken ct, bool probeOnly = false)
    {
        RequireAvailable(environment);
        var now = DateTime.UtcNow;
        var row = companyId is not null ? await Find(tenantId, companyId, body, environment).AsNoTracking().SingleOrDefaultAsync(ct) : null;
        row ??= await Find(tenantId, null, body, environment).AsNoTracking().SingleOrDefaultAsync(ct);
        if (row is null || row.TenantId != tenantId || IsExpired(row, now)) return null;

        string secret;
        try
        {
            if (row.Ciphertext.Length < FieldEnvelope.MinLength) return null;
            var ring = await keys.GetRingAsync(tenantId, createIfMissing: false, ct);
            if (!ring.Deks.TryGetValue(FieldEnvelope.DekVersionOf(row.Ciphertext), out var dek)) return null;
            secret = FieldEnvelope.Open(dek, row.Ciphertext,
                Aad(row.TenantId, row.CompanyId, row.Body, row.Environment, row.CredentialVersion, row.ClientIdentifier, row.BaseUrlHost, row.TokenUrlHost),
                MaxEnvelopeLength);
        }
        catch (CryptographicException) { return null; }
        catch (InvalidOperationException ex) when (ex is not FieldEncryptionUnavailableException) { return null; }

        if (!probeOnly) await RecordLastUseAsync(row, now, ct);
        return new UnsealedGovernmentCredential(row.Body, row.Environment, row.Kind, row.CompanyId, row.CredentialVersion, secret,
            row.ClientIdentifier, row.BaseUrlHost, row.TokenUrlHost);
    }

    /// <summary>G3 of the live gate for one environment: a credential is present, unexpired and opens. False (never a throw) when storage is off.</summary>
    public async Task<bool> IsUsableAsync(Guid tenantId, Guid? companyId, string body, string environment, CancellationToken ct)
    {
        if (!IsAvailable || (environment == GovernmentEnvironments.Production && !AcceptsLiveCredentials)) return false;
        try { return await OpenAsync(tenantId, companyId, body, environment, ct, probeOnly: true) is not null; }
        catch (FieldEncryptionUnavailableException) { return false; }
    }

    /// <summary>The largest envelope a <see cref="MaxSecretLength"/> secret can produce (UTF-8, up to 4 bytes a character).</summary>
    private const int MaxEnvelopeLength = FieldEnvelope.MinLength + MaxSecretLength * 4;

    /// <summary>
    /// One-row update of last-used (and only of that, only while the version is unchanged). On a relational database it never
    /// touches the caller's tracked changes. A company-scoped caller may use the tenant-wide credential, so this bookkeeping write
    /// is allowed on it (ZayraDbContext's per-type bookkeeping list).
    /// </summary>
    private async Task RecordLastUseAsync(GovernmentCredential read, DateTime now, CancellationToken ct)
    {
        if (db.Database.IsRelational())
        {
            await db.GovernmentCredentials
                .Where(x => x.Id == read.Id && x.TenantId == read.TenantId && x.CredentialVersion == read.CredentialVersion)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.LastUsedAtUtc, now), ct);
            return;
        }
        var tracked = await db.GovernmentCredentials.SingleAsync(x => x.Id == read.Id && x.TenantId == read.TenantId, ct);
        tracked.LastUsedAtUtc = now;
        await db.SaveChangesAsync(ct);
    }

    private IQueryable<GovernmentCredential> Find(Guid tenantId, Guid? companyId, string body, string environment) =>
        db.GovernmentCredentials.Where(x => x.TenantId == tenantId && x.CompanyId == companyId && x.Body == body && x.Environment == environment);

    /// <summary>
    /// Saves; on any refusal puts the row back as it was and drops the staged audit, so nothing of the refused change lingers in the
    /// request's context. A lost rotation race (version changed, or the unique index) is a 409.
    /// </summary>
    private async Task SaveOrForgetAsync(GovernmentCredential row, CancellationToken ct)
    {
        try { await db.SaveChangesAsync(ct); }
        catch (Exception ex)
        {
            audit.DiscardStaged();
            var entry = db.Entry(row);
            if (entry.State == EntityState.Added) entry.State = EntityState.Detached;
            else
            {
                entry.CurrentValues.SetValues(entry.OriginalValues);
                entry.State = EntityState.Unchanged;
            }
            if (ex is DbUpdateException)
                throw new GovernmentCredentialRefusedException(ConflictCode, 409,
                    "Someone else changed this credential at the same time. Nothing was saved; read it again and retry.",
                    "قام شخص آخر بتغيير بيانات الاعتماد هذه في الوقت نفسه. لم يُحفظ شيء؛ أعد القراءة وحاول مرة أخرى.");
            throw;
        }
    }

    private static bool IsExpired(GovernmentCredential row, DateTime now) => row.ExpiresAtUtc is { } expiry && expiry <= now;

    private void RequireAvailable(string? environment)
    {
        if (!IsAvailable) throw new GovernmentCredentialsUnavailableException();
        if (environment == GovernmentEnvironments.Production && !AcceptsLiveCredentials)
            throw new GovernmentCredentialRefusedException(LiveNotAcceptedCode, 409,
                "Production credentials are not accepted on this server yet. Use a sandbox credential; the platform operator switches production on.",
                "لا تُقبل بيانات اعتماد بيئة الإنتاج على هذا الخادم بعد. استخدم بيانات اعتماد بيئة الاختبار؛ يفعّل مشغّل المنصة بيئة الإنتاج.");
        if (environment is not null && !GovernmentEnvironments.IsKnown(environment)) throw new ArgumentException($"Unknown environment '{environment}'.", nameof(environment));
    }

    private static DateTime? NormaliseUtc(DateTime? value) => value switch
    {
        null => null,
        { Kind: DateTimeKind.Utc } v => v,
        { Kind: DateTimeKind.Local } v => v.ToUniversalTime(),
        { } v => DateTime.SpecifyKind(v, DateTimeKind.Utc),
    };

    private static GovernmentCredentialBinding Normalise(GovernmentCredentialBinding? binding)
    {
        static string? Host(string? value, string name)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;
            var host = value.Trim().TrimEnd('.').ToLowerInvariant();
            if (host.Length > 255 || Uri.CheckHostName(host) != UriHostNameType.Dns || !host.Contains('.'))
                throw new ArgumentException($"{name} must be a host name (no scheme, port, path or address).", name);
            return host;
        }
        var clientId = string.IsNullOrWhiteSpace(binding?.ClientIdentifier) ? null : binding!.ClientIdentifier!.Trim();
        if (clientId is { Length: > 200 } || clientId?.Any(char.IsControl) == true) throw new ArgumentException("The client id is at most 200 printable characters.", nameof(binding));
        return new GovernmentCredentialBinding(clientId, Host(binding?.BaseUrlHost, "baseUrlHost"), Host(binding?.TokenUrlHost, "tokenUrlHost"));
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
        baseUrlHost = row.BaseUrlHost, authorizationHost = row.TokenUrlHost, clientIdentifierSet = row.ClientIdentifier is not null,
    });

    private static GovernmentCredentialMetadata ToMetadata(GovernmentCredential row) => new(
        row.Body, row.Environment, row.Kind, row.CompanyId, row.Fingerprint, row.CredentialVersion, row.ExpiresAtUtc,
        row.LastUsedAtUtc, row.CreatedAtUtc, row.CreatedBy, row.RotatedAtUtc, row.RotatedBy,
        row.ClientIdentifier, row.BaseUrlHost, row.TokenUrlHost);
}
