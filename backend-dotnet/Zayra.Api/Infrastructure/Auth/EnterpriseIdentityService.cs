using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Auth;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Auth;

public class EnterpriseIdentityService : IEnterpriseIdentityService
{
    private readonly ZayraDbContext _db;
    private readonly ITokenService _tokens;
    private readonly IPasswordHasher _passwordHasher;
    private readonly IAuditService _audit;

    public EnterpriseIdentityService(ZayraDbContext db, ITokenService tokens, IPasswordHasher passwordHasher, IAuditService audit)
    {
        _db = db;
        _tokens = tokens;
        _passwordHasher = passwordHasher;
        _audit = audit;
    }

    public async Task<EnterpriseIdentitySettingsDto> GetSettingsAsync(Guid tenantId, CancellationToken ct)
    {
        var setting = await EnsureSettingsAsync(tenantId, ct);
        return ToDto(setting);
    }

    public async Task<EnterpriseIdentitySettingsDto> UpdateSettingsAsync(Guid tenantId, UpdateEnterpriseIdentitySettingsRequest request, RequestContext context, CancellationToken ct)
    {
        var setting = await EnsureSettingsAsync(tenantId, ct);
        if (request.SamlEnabled.HasValue) setting.SamlEnabled = request.SamlEnabled.Value;
        if (request.OidcEnabled.HasValue) setting.OidcEnabled = request.OidcEnabled.Value;
        if (request.ScimEnabled.HasValue) setting.ScimEnabled = request.ScimEnabled.Value;
        if (request.ScimDryRun.HasValue) setting.ScimDryRun = request.ScimDryRun.Value;
        if (request.AllowedDomains is not null) setting.AllowedDomainsCsv = string.Join(",", NormalizeDomains(request.AllowedDomains));
        if (request.SamlEntityId is not null) setting.SamlEntityId = Clean(request.SamlEntityId, 512);
        if (request.SamlSsoUrl is not null) setting.SamlSsoUrl = Clean(request.SamlSsoUrl, 1024);
        if (request.SamlCertificateThumbprint is not null) setting.SamlCertificateThumbprint = Clean(request.SamlCertificateThumbprint, 160);
        if (request.OidcAuthority is not null) setting.OidcAuthority = Clean(request.OidcAuthority.TrimEnd('/'), 1024);
        if (request.OidcClientId is not null) setting.OidcClientId = Clean(request.OidcClientId, 256);
        if (request.OidcClientSecretConfigured.HasValue) setting.OidcClientSecretConfigured = request.OidcClientSecretConfigured.Value;

        if (request.EnforceSsoLogin.HasValue)
        {
            setting.EnforceSsoLogin = request.EnforceSsoLogin.Value;
            var validation = Validate(setting);
            if (setting.EnforceSsoLogin && !validation.IsValid)
                throw new InvalidOperationException("SSO enforcement requires valid SAML or OIDC settings and at least one allowed email domain.");
        }

        setting.UpdatedAtUtc = DateTime.UtcNow;
        setting.UpdatedBy = context.UserId;
        await _db.SaveChangesAsync(ct);
        await _audit.WriteAsync(EnterpriseIdentityEventActions.SsoConfigUpdated, "TenantIdentityProviderSetting", setting.Id.ToString(), context, null, ct);
        return ToDto(setting);
    }

    public async Task<RotateScimTokenResponse> RotateScimTokenAsync(Guid tenantId, RequestContext context, CancellationToken ct)
    {
        var setting = await EnsureSettingsAsync(tenantId, ct);
        var raw = _tokens.CreateSecureToken();
        var now = DateTime.UtcNow;
        setting.ScimTokenHash = _tokens.HashToken(raw);
        setting.ScimTokenRotatedAtUtc = now;
        setting.UpdatedAtUtc = now;
        setting.UpdatedBy = context.UserId;
        await _db.SaveChangesAsync(ct);
        await _audit.WriteAsync(EnterpriseIdentityEventActions.ScimConfigUpdated, "TenantIdentityProviderSetting", setting.Id.ToString(), context, "{\"rotated\":true}", ct);
        return new RotateScimTokenResponse(raw, now);
    }

    public async Task<SamlServiceProviderMetadataDto?> GetSamlMetadataAsync(string tenantSlug, string baseUrl, CancellationToken ct)
    {
        var tenant = await LoadTenantBySlugAsync(tenantSlug, ct);
        if (tenant is null) return null;
        var setting = await EnsureSettingsAsync(tenant.Id, ct);
        if (!setting.SamlEnabled) return null;
        var root = baseUrl.TrimEnd('/');
        return new SamlServiceProviderMetadataDto(
            $"{root}/saml/{tenant.Slug}",
            $"{root}/api/enterprise-identity/saml/{tenant.Slug}/acs",
            $"{root}/api/enterprise-identity/saml/{tenant.Slug}/logout");
    }

    public async Task<OidcTenantMetadataDto?> GetOidcMetadataAsync(string tenantSlug, string baseUrl, CancellationToken ct)
    {
        var tenant = await LoadTenantBySlugAsync(tenantSlug, ct);
        if (tenant is null) return null;
        var setting = await EnsureSettingsAsync(tenant.Id, ct);
        if (!setting.OidcEnabled) return null;
        var root = baseUrl.TrimEnd('/');
        var issuer = $"{root}/oidc/{tenant.Slug}";
        return new OidcTenantMetadataDto(
            issuer,
            $"{root}/api/enterprise-identity/oidc/{tenant.Slug}/authorize",
            $"{root}/api/enterprise-identity/oidc/{tenant.Slug}/token",
            $"{root}/api/enterprise-identity/oidc/{tenant.Slug}/userinfo",
            $"{root}/api/enterprise-identity/oidc/{tenant.Slug}/jwks",
            new[] { "code" },
            new[] { "openid", "profile", "email" },
            setting.OidcClientId);
    }

    public async Task<EnterpriseIdentityValidationResult> ValidateSettingsAsync(Guid tenantId, CancellationToken ct)
    {
        var setting = await EnsureSettingsAsync(tenantId, ct);
        return Validate(setting);
    }

    public async Task<Guid?> ValidateScimTokenAsync(string rawToken, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(rawToken)) return null;
        var hash = _tokens.HashToken(rawToken);
        var setting = await _db.TenantIdentityProviderSettings.AsNoTracking()
            .FirstOrDefaultAsync(x => x.ScimEnabled && x.ScimTokenHash == hash, ct);
        return setting?.TenantId;
    }

    public async Task<ScimListResponse> ListScimUsersAsync(Guid tenantId, int startIndex, int count, CancellationToken ct)
    {
        var skip = Math.Max(0, startIndex - 1);
        var take = Math.Clamp(count, 1, 100);
        var query = _db.Users.AsNoTracking().Where(x => x.TenantId == tenantId && !x.IsDeleted);
        var total = await query.CountAsync(ct);
        var users = await query.OrderBy(x => x.Email).Skip(skip).Take(take).ToListAsync(ct);
        return new ScimListResponse(total, startIndex, take, users.Select(ToScim).ToList());
    }

    public async Task<ScimUserResource> UpsertScimUserAsync(Guid tenantId, ScimUserUpsertRequest request, RequestContext context, CancellationToken ct)
    {
        var setting = await EnsureSettingsAsync(tenantId, ct);
        EnsureScimReady(setting);
        var email = ResolveEmail(request);
        ValidateDomain(setting, email);
        var normalized = AuthService.Normalize(email);
        var externalId = Clean(request.ExternalId ?? string.Empty, 256);
        var user = await _db.Users.FirstOrDefaultAsync(x =>
            x.TenantId == tenantId
            && !x.IsDeleted
            && ((externalId != "" && x.ExternalId == externalId) || x.NormalizedEmail == normalized), ct);

        var active = request.Active ?? true;
        var action = user is null ? EnterpriseIdentityEventActions.ScimUserCreated : EnterpriseIdentityEventActions.ScimUserUpdated;
        if (user is null)
        {
            user = new User
            {
                TenantId = tenantId,
                Email = email,
                NormalizedEmail = normalized,
                FullName = ResolveName(request),
                PasswordHash = _passwordHasher.Hash(_tokens.CreateSecureToken() + "!Aa1"),
                IdentityProvider = EnterpriseIdentityProtocols.Scim,
                ProvisioningSource = EnterpriseIdentityProtocols.Scim,
                ExternalId = externalId,
                AccessMode = active ? AccessModes.EssOnly : AccessModes.NoLogin,
                Status = active ? "Active" : "Deactivated",
                IsActive = active,
                IsEmailConfirmed = true,
                LastProvisionedAtUtc = DateTime.UtcNow,
            };
            if (!setting.ScimDryRun) _db.Users.Add(user);
        }
        else
        {
            user.Email = email;
            user.NormalizedEmail = normalized;
            user.FullName = ResolveName(request);
            user.ExternalId = externalId == "" ? user.ExternalId : externalId;
            user.IdentityProvider = EnterpriseIdentityProtocols.Scim;
            user.ProvisioningSource = EnterpriseIdentityProtocols.Scim;
            user.LastProvisionedAtUtc = DateTime.UtcNow;
            user.IsActive = active;
            user.Status = active ? "Active" : "Deactivated";
            user.AccessMode = active ? user.AccessMode : AccessModes.NoLogin;
            user.UpdatedAtUtc = DateTime.UtcNow;
            if (!active) await RevokeTokensAsync(user.Id, context.IpAddress, ct);
        }

        await RecordEventAsync(tenantId, action, externalId, user.Id, null, setting.ScimDryRun ? "DryRun" : "Succeeded", context, ct);
        if (!setting.ScimDryRun) await _db.SaveChangesAsync(ct);
        return ToScim(user);
    }

    public async Task<ScimUserResource?> PatchScimUserAsync(Guid tenantId, Guid userId, ScimPatchRequest request, RequestContext context, CancellationToken ct)
    {
        var setting = await EnsureSettingsAsync(tenantId, ct);
        EnsureScimReady(setting);
        var user = await _db.Users.FirstOrDefaultAsync(x => x.TenantId == tenantId && x.Id == userId && !x.IsDeleted, ct);
        if (user is null) return null;

        foreach (var op in request.Operations)
        {
            if (!op.Op.Equals("replace", StringComparison.OrdinalIgnoreCase)) continue;
            var path = op.Path ?? string.Empty;
            if (path.Equals("active", StringComparison.OrdinalIgnoreCase) && TryBoolean(op.Value, out var active))
            {
                user.IsActive = active;
                user.Status = active ? "Active" : "Deactivated";
                if (!active)
                {
                    user.AccessMode = AccessModes.NoLogin;
                    await RevokeTokensAsync(user.Id, context.IpAddress, ct);
                }
            }
            if (path.Equals("displayName", StringComparison.OrdinalIgnoreCase) && op.Value is not null)
                user.FullName = Clean(op.Value.ToString() ?? user.FullName, 180);
        }

        user.IdentityProvider = EnterpriseIdentityProtocols.Scim;
        user.ProvisioningSource = EnterpriseIdentityProtocols.Scim;
        user.LastProvisionedAtUtc = DateTime.UtcNow;
        user.UpdatedAtUtc = DateTime.UtcNow;
        await RecordEventAsync(tenantId, EnterpriseIdentityEventActions.ScimUserUpdated, user.ExternalId, user.Id, null, setting.ScimDryRun ? "DryRun" : "Succeeded", context, ct);
        if (!setting.ScimDryRun) await _db.SaveChangesAsync(ct);
        return ToScim(user);
    }

    public async Task<bool> DeactivateScimUserAsync(Guid tenantId, Guid userId, RequestContext context, CancellationToken ct)
    {
        var setting = await EnsureSettingsAsync(tenantId, ct);
        EnsureScimReady(setting);
        var user = await _db.Users.FirstOrDefaultAsync(x => x.TenantId == tenantId && x.Id == userId && !x.IsDeleted, ct);
        if (user is null) return false;
        user.IsActive = false;
        user.Status = "Deactivated";
        user.AccessMode = AccessModes.NoLogin;
        user.ProvisioningSource = EnterpriseIdentityProtocols.Scim;
        user.LastProvisionedAtUtc = DateTime.UtcNow;
        user.UpdatedAtUtc = DateTime.UtcNow;
        await RevokeTokensAsync(user.Id, context.IpAddress, ct);
        await RecordEventAsync(tenantId, EnterpriseIdentityEventActions.ScimUserDeactivated, user.ExternalId, user.Id, null, setting.ScimDryRun ? "DryRun" : "Succeeded", context, ct);
        if (!setting.ScimDryRun) await _db.SaveChangesAsync(ct);
        return true;
    }

    private async Task<TenantIdentityProviderSetting> EnsureSettingsAsync(Guid tenantId, CancellationToken ct)
    {
        var setting = await _db.TenantIdentityProviderSettings.FirstOrDefaultAsync(x => x.TenantId == tenantId, ct);
        if (setting is not null) return setting;
        setting = new TenantIdentityProviderSetting { TenantId = tenantId };
        _db.TenantIdentityProviderSettings.Add(setting);
        await _db.SaveChangesAsync(ct);
        return setting;
    }

    private async Task<Tenant?> LoadTenantBySlugAsync(string slug, CancellationToken ct) =>
        await _db.Tenants.AsNoTracking().FirstOrDefaultAsync(x => x.Slug == slug.Trim().ToLowerInvariant() && x.IsActive, ct);

    private async Task RevokeTokensAsync(Guid userId, string? ip, CancellationToken ct)
    {
        var tokens = await _db.RefreshTokens.Where(x => x.UserId == userId && x.RevokedAtUtc == null).ToListAsync(ct);
        foreach (var token in tokens)
        {
            token.RevokedAtUtc = DateTime.UtcNow;
            token.RevokedByIp = ip;
        }
    }

    private async Task RecordEventAsync(Guid tenantId, string action, string externalId, Guid? userId, int? employeeId, string status, RequestContext context, CancellationToken ct)
    {
        _db.EnterpriseIdentityProvisioningEvents.Add(new EnterpriseIdentityProvisioningEvent
        {
            TenantId = tenantId,
            Action = action,
            ExternalId = externalId,
            UserId = userId,
            EmployeeId = employeeId,
            Status = status,
            DetailsJson = JsonSerializer.Serialize(new { actor = context.UserId, source = EnterpriseIdentityProtocols.Scim })
        });
        await _audit.WriteAsync(action, "EnterpriseIdentityProvisioningEvent", userId?.ToString(), context, null, ct);
    }

    private static EnterpriseIdentityValidationResult Validate(TenantIdentityProviderSetting setting)
    {
        var errors = new List<string>();
        if (setting.EnforceSsoLogin && !setting.SamlEnabled && !setting.OidcEnabled)
            errors.Add("sso_enforcement_requires_saml_or_oidc");
        if ((setting.SamlEnabled || setting.OidcEnabled || setting.EnforceSsoLogin) && ParseDomains(setting.AllowedDomainsCsv).Count == 0)
            errors.Add("allowed_domains_required");
        if (setting.SamlEnabled)
        {
            if (!IsHttps(setting.SamlSsoUrl)) errors.Add("saml_sso_url_must_be_https");
            if (string.IsNullOrWhiteSpace(setting.SamlEntityId)) errors.Add("saml_entity_id_required");
            if (string.IsNullOrWhiteSpace(setting.SamlCertificateThumbprint)) errors.Add("saml_certificate_thumbprint_required");
        }
        if (setting.OidcEnabled)
        {
            if (!IsHttps(setting.OidcAuthority)) errors.Add("oidc_authority_must_be_https");
            if (string.IsNullOrWhiteSpace(setting.OidcClientId)) errors.Add("oidc_client_id_required");
        }
        if (setting.ScimEnabled && string.IsNullOrWhiteSpace(setting.ScimTokenHash))
            errors.Add("scim_token_required");
        return new EnterpriseIdentityValidationResult(errors.Count == 0, errors);
    }

    private static EnterpriseIdentitySettingsDto ToDto(TenantIdentityProviderSetting s) =>
        new(s.TenantId, s.SamlEnabled, s.OidcEnabled, s.ScimEnabled, s.EnforceSsoLogin, s.ScimDryRun,
            ParseDomains(s.AllowedDomainsCsv), s.SamlEntityId, s.SamlSsoUrl, s.SamlCertificateThumbprint,
            s.OidcAuthority, s.OidcClientId, s.OidcClientSecretConfigured, !string.IsNullOrWhiteSpace(s.ScimTokenHash),
            s.ScimTokenRotatedAtUtc, s.UpdatedAtUtc);

    private static ScimUserResource ToScim(User user) =>
        new(user.Id.ToString(), string.IsNullOrWhiteSpace(user.ExternalId) ? null : user.ExternalId, user.Email, user.IsActive,
            SplitName(user.FullName), new[] { new ScimEmail(user.Email) }, user.FullName);

    private static ScimName SplitName(string fullName)
    {
        var parts = fullName.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return new ScimName(parts.FirstOrDefault(), parts.Length > 1 ? parts[1] : null, fullName);
    }

    private static string ResolveEmail(ScimUserUpsertRequest request) =>
        (request.Emails?.FirstOrDefault(x => x.Primary)?.Value ?? request.Emails?.FirstOrDefault()?.Value ?? request.UserName).Trim().ToLowerInvariant();

    private static string ResolveName(ScimUserUpsertRequest request) =>
        Clean(request.DisplayName ?? request.Name?.Formatted ?? $"{request.Name?.GivenName} {request.Name?.FamilyName}".Trim(), 180);

    private static void ValidateDomain(TenantIdentityProviderSetting setting, string email)
    {
        var domain = email.Split('@').LastOrDefault() ?? string.Empty;
        var allowed = ParseDomains(setting.AllowedDomainsCsv);
        if (allowed.Count > 0 && !allowed.Contains(domain, StringComparer.OrdinalIgnoreCase))
            throw new InvalidOperationException("SCIM user email domain is not allowed for this tenant.");
    }

    private static void EnsureScimReady(TenantIdentityProviderSetting setting)
    {
        if (!setting.ScimEnabled || string.IsNullOrWhiteSpace(setting.ScimTokenHash))
            throw new UnauthorizedAccessException("SCIM is not enabled for this tenant.");
    }

    private static IReadOnlyCollection<string> NormalizeDomains(IEnumerable<string> domains) =>
        domains.Select(d => d.Trim().TrimStart('@').ToLowerInvariant())
            .Where(d => d.Contains('.') && !d.Contains('/') && !d.Contains(' '))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(50)
            .ToList();

    private static IReadOnlyCollection<string> ParseDomains(string csv) =>
        NormalizeDomains(csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    private static bool IsHttps(string value) => Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps;
    private static string Clean(string value, int max) => value.Trim().Length <= max ? value.Trim() : value.Trim()[..max];

    private static bool TryBoolean(object? value, out bool result)
    {
        if (value is bool b) { result = b; return true; }
        if (value is JsonElement { ValueKind: JsonValueKind.True }) { result = true; return true; }
        if (value is JsonElement { ValueKind: JsonValueKind.False }) { result = false; return true; }
        return bool.TryParse(value?.ToString(), out result);
    }
}
