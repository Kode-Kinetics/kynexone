namespace Zayra.Api.Application.Auth;

public class JwtOptions
{
    public string Issuer { get; set; } = "Zayra.Api";

    /// <summary>The committed development audience; production refuses to start on it.</summary>
    public const string DevAudience = "kynexone";

    // The one audience every access token is issued for and validated against. Unique per
    // installation, so a token minted for one installation is rejected by every other.
    public string Audience { get; set; } = DevAudience;

    public string SigningKey { get; set; } = "CHANGE_ME_TO_A_64_CHARACTER_PRODUCTION_SECRET_KEY";
    public int AccessTokenMinutes { get; set; } = 30;
    public int RefreshTokenDays { get; set; } = 14;
}
