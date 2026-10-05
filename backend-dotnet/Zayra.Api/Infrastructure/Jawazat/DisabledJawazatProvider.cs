using Zayra.Api.Application.Jawazat;

namespace Zayra.Api.Infrastructure.Jawazat;

/// <summary>No endpoint, credentials or portal automation. Availability is explicit in every result.</summary>
public sealed class DisabledJawazatProvider : IJawazatProvider
{
    public const string UnavailableMessage = "Government submission is unavailable. An authorized provider contract, sandbox and credentials have not been configured.";
    public Task<JawazatCapabilities> GetCapabilitiesAsync(CancellationToken ct) =>
        Task.FromResult(new JawazatCapabilities("Disabled", false, Array.Empty<string>(), UnavailableMessage));
    public Task<JawazatProviderResult> VerifyAsync(JawazatRequestData request, CancellationToken ct) => Task.FromResult(Unavailable());
    public Task<JawazatProviderResult> SubmitAsync(JawazatRequestData request, CancellationToken ct) => Task.FromResult(Unavailable());
    public Task<JawazatProviderResult> GetOutcomeAsync(JawazatRequestData request, CancellationToken ct) => Task.FromResult(Unavailable());

    private static JawazatProviderResult Unavailable() => new("ProviderUnavailable", UnavailableMessage, GovernmentChecks());
    public static IReadOnlyList<JawazatCheck> GovernmentChecks() =>
    [
        Unknown("government_presence", "Presence in Saudi Arabia requires government verification."),
        Unknown("government_traffic_violations", "Traffic violation clearance requires government verification."),
        Unknown("government_visa_conflicts", "Existing visas and unused-visa violations require government verification."),
        Unknown("government_status_biometrics", "Government status and biometric conditions require government verification."),
        Unknown("government_fee_payment", "Current fees, payment and route-specific payer basis require verified evidence."),
        Unknown("government_route_eligibility", "Eligibility for this beneficiary and service route has not been verified.")
    ];
    private static JawazatCheck Unknown(string code, string reason) => new(code, "Unknown", "Government provider unavailable", null, reason);
}
