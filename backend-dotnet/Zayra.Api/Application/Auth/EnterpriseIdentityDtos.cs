using System.ComponentModel.DataAnnotations;

namespace Zayra.Api.Application.Auth;

public record EnterpriseIdentitySettingsDto(
    Guid TenantId,
    bool SamlEnabled,
    bool OidcEnabled,
    bool ScimEnabled,
    bool EnforceSsoLogin,
    bool ScimDryRun,
    IReadOnlyCollection<string> AllowedDomains,
    string SamlEntityId,
    string SamlSsoUrl,
    string SamlCertificateThumbprint,
    string OidcAuthority,
    string OidcClientId,
    bool OidcClientSecretConfigured,
    bool ScimTokenConfigured,
    DateTime? ScimTokenRotatedAtUtc,
    DateTime UpdatedAtUtc);

public record UpdateEnterpriseIdentitySettingsRequest(
    bool? SamlEnabled,
    bool? OidcEnabled,
    bool? ScimEnabled,
    bool? EnforceSsoLogin,
    bool? ScimDryRun,
    IReadOnlyCollection<string>? AllowedDomains,
    string? SamlEntityId,
    string? SamlSsoUrl,
    string? SamlCertificateThumbprint,
    string? OidcAuthority,
    string? OidcClientId,
    bool? OidcClientSecretConfigured);

public record RotateScimTokenResponse(string Token, DateTime RotatedAtUtc);

public record SamlServiceProviderMetadataDto(string EntityId, string AssertionConsumerServiceUrl, string SingleLogoutServiceUrl);

public record OidcTenantMetadataDto(
    string Issuer,
    string AuthorizationEndpoint,
    string TokenEndpoint,
    string UserInfoEndpoint,
    string JwksUri,
    IReadOnlyCollection<string> ResponseTypesSupported,
    IReadOnlyCollection<string> ScopesSupported,
    string ClientId);

public record EnterpriseIdentityValidationResult(bool IsValid, IReadOnlyCollection<string> Errors);

public record ScimListResponse(int TotalResults, int StartIndex, int ItemsPerPage, IReadOnlyCollection<ScimUserResource> Resources);

public record ScimUserResource(
    string Id,
    string? ExternalId,
    string UserName,
    bool Active,
    ScimName Name,
    IReadOnlyCollection<ScimEmail> Emails,
    string? DisplayName);

public record ScimName(string? GivenName, string? FamilyName, string? Formatted);
public record ScimEmail(string Value, bool Primary = true, string Type = "work");

public record ScimUserUpsertRequest(
    string? ExternalId,
    [Required] string UserName,
    bool? Active,
    ScimName? Name,
    IReadOnlyCollection<ScimEmail>? Emails,
    string? DisplayName);

public record ScimPatchRequest(IReadOnlyCollection<ScimPatchOperation> Operations);
public record ScimPatchOperation(string Op, string? Path, object? Value);
