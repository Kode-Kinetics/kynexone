namespace Zayra.Api.Infrastructure.Auth;

internal static class AuthChallengeTokenCodec
{
    public const string TenantLoginPurpose = "tm1";
    public const string TenantEnrollmentPurpose = "te1";
    public const string PlatformLoginPurpose = "pm1";

    public static string CreateTenant(
        string purpose,
        Guid challengeId,
        Guid userId,
        Guid tenantId,
        string sessionStamp,
        string nonce)
    {
        if (purpose is not (TenantLoginPurpose or TenantEnrollmentPurpose))
            throw new ArgumentOutOfRangeException(nameof(purpose));
        return string.Join('.', purpose, challengeId.ToString("N"), userId.ToString("N"),
            tenantId.ToString("N"), sessionStamp, nonce);
    }

    public static string CreatePlatform(
        Guid challengeId,
        Guid platformUserId,
        string sessionStamp,
        string nonce) =>
        string.Join('.', PlatformLoginPurpose, challengeId.ToString("N"),
            platformUserId.ToString("N"), "platform", sessionStamp, nonce);

    public static bool TryParse(string rawToken, string expectedPurpose, out AuthChallengeEnvelope envelope)
    {
        envelope = default;
        if (string.IsNullOrWhiteSpace(rawToken) || rawToken.Length > 1024) return false;
        var parts = rawToken.Split('.', 6, StringSplitOptions.None);
        if (parts.Length != 6 || !string.Equals(parts[0], expectedPurpose, StringComparison.Ordinal))
            return false;
        if (!Guid.TryParseExact(parts[1], "N", out var challengeId)
            || !Guid.TryParseExact(parts[2], "N", out var principalId)
            || string.IsNullOrWhiteSpace(parts[4])
            || string.IsNullOrWhiteSpace(parts[5]))
            return false;

        Guid? tenantId = null;
        if (expectedPurpose == PlatformLoginPurpose)
        {
            if (!string.Equals(parts[3], "platform", StringComparison.Ordinal)) return false;
        }
        else if (!Guid.TryParseExact(parts[3], "N", out var parsedTenantId))
        {
            return false;
        }
        else
        {
            tenantId = parsedTenantId;
        }

        envelope = new AuthChallengeEnvelope(
            expectedPurpose,
            challengeId,
            principalId,
            tenantId,
            parts[4],
            parts[5]);
        return true;
    }
}

internal readonly record struct AuthChallengeEnvelope(
    string Purpose,
    Guid ChallengeId,
    Guid PrincipalId,
    Guid? TenantId,
    string SessionStamp,
    string Nonce);
