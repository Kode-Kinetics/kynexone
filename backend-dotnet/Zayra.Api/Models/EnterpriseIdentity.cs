using Zayra.Api.Domain.Entities;

namespace Zayra.Api.Models;

public static class EnterpriseIdentityProtocols
{
    public const string Saml = "SAML";
    public const string Oidc = "OIDC";
    public const string Scim = "SCIM";
}

public static class EnterpriseIdentityEventActions
{
    public const string ScimUserCreated = "scim.user.created";
    public const string ScimUserUpdated = "scim.user.updated";
    public const string ScimUserDeactivated = "scim.user.deactivated";
    public const string SsoConfigUpdated = "identity.sso_config_updated";
    public const string ScimConfigUpdated = "identity.scim_config_updated";
}

public class TenantIdentityProviderSetting : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public bool SamlEnabled { get; set; }
    public bool OidcEnabled { get; set; }
    public bool ScimEnabled { get; set; }
    public bool EnforceSsoLogin { get; set; }
    public bool ScimDryRun { get; set; } = true;
    public string AllowedDomainsCsv { get; set; } = string.Empty;
    public string SamlEntityId { get; set; } = string.Empty;
    public string SamlSsoUrl { get; set; } = string.Empty;
    public string SamlCertificateThumbprint { get; set; } = string.Empty;
    public string OidcAuthority { get; set; } = string.Empty;
    public string OidcClientId { get; set; } = string.Empty;
    public bool OidcClientSecretConfigured { get; set; }
    public string ScimTokenHash { get; set; } = string.Empty;
    public DateTime? ScimTokenRotatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
    public Guid? UpdatedBy { get; set; }
}

public class EnterpriseIdentityProvisioningEvent : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public string Protocol { get; set; } = EnterpriseIdentityProtocols.Scim;
    public string Action { get; set; } = string.Empty;
    public string ExternalId { get; set; } = string.Empty;
    public Guid? UserId { get; set; }
    public int? EmployeeId { get; set; }
    public string Status { get; set; } = "Succeeded";
    public string DetailsJson { get; set; } = "{}";
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}
