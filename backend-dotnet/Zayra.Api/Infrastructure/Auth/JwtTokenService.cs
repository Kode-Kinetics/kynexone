using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Zayra.Api.Application.Auth;
using Zayra.Api.Application.Common;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Auth;

public class JwtTokenService : ITokenService
{
    private readonly JwtOptions _options;
    private static readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);

    public JwtTokenService(IOptions<JwtOptions> options)
    {
        _options = options.Value;
    }

    public string CreateAccessToken(User user, IReadOnlyCollection<string> roles, IReadOnlyCollection<string> permissions, Tenant tenant, IReadOnlyCollection<EntityAccessGrant> entityAccess, EntityScopeDescriptor entityScope, out DateTime expiresAtUtc)
    {
        expiresAtUtc = DateTime.UtcNow.AddMinutes(_options.AccessTokenMinutes);
        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
            new(JwtRegisteredClaimNames.Email, user.Email),
            new(JwtRegisteredClaimNames.Name, user.FullName),
            new("tenant_id", tenant.Id.ToString()),
            new("tenant", tenant.Slug)
        };
        var link = user.EmployeeUserAccounts.Where(x => !x.IsDeleted).OrderByDescending(x => x.IsPrimary).ThenByDescending(x => x.CreatedAtUtc).FirstOrDefault();
        if (link is not null)
        {
            claims.Add(new Claim("employee_id", link.EmployeeId.ToString()));
            claims.Add(new Claim("access_mode", link.AccessMode));
            claims.Add(new Claim("requires_password_setup", link.RequiresPasswordSetup ? "true" : "false"));
            if (link.AccessMode == AccessModes.KioskOnly) claims.Add(new Claim("kiosk_only", "true"));
        }
        claims.AddRange(roles.Select(role => new Claim(ClaimTypes.Role, role)));
        claims.AddRange(permissions.Select(permission => new Claim("permission", permission)));
        // Company scope, claim format v2 (+ legacy v1 during the rollout window).
        // The scope decision is made at issuance by EntityScopeClaims.Resolve — never
        // inferred from claim absence, which is what makes the StrictMode flip safe.
        claims.AddRange(EntityScopeClaims.Build(entityScope, entityAccess));

        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_options.SigningKey));
        var credentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);
        var token = new JwtSecurityToken(_options.Issuer, _options.TenantAudience, claims, expires: expiresAtUtc, signingCredentials: credentials);
        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    public string CreateSecureToken()
    {
        return Convert.ToBase64String(RandomNumberGenerator.GetBytes(64));
    }

    public string HashToken(string token)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(token));
        return Convert.ToHexString(hash);
    }
}
