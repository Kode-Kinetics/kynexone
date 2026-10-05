using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using System.Text.Json.Serialization;
using Zayra.Api.Application.Common;

namespace Zayra.Api.Application.Jawazat;

public static class JawazatConstants
{
    public const string ApprovalEntityName = "JawazatRequest";
    public const string EmployerAssisted = "EmployerAssisted";
    public const string WorkerNotification = "WorkerSelfServiceNotification";
    public const string ExitReentryIssue = "ExitReentryIssue";
    public static bool IsHrRole(string role) => role is "Admin" or "HR Director" or "HR Manager" or "HR Officer";
}

public sealed record JawazatActor(Guid TenantId, Guid UserId, bool CanManage, DataScope EmployeeScope, EntityScopeContext CompanyScope);

public sealed record JawazatPolicy(
    int SchemaVersion = 1,
    bool EmployerAssistedEnabled = false,
    bool WorkerNotificationEnabled = false,
    int MaximumTripDays = 90,
    int MinimumPassportValidityDays = 90,
    string RuleVersion = "",
    string ReviewedBy = "",
    DateTime? ReviewedAtUtc = null);

public sealed record JawazatPolicySnapshot(Guid ProfileId, Guid CompanyId, DateOnly EffectiveFrom, DateOnly? EffectiveTo, DateTime CapturedAtUtc, JawazatPolicy Policy);
public sealed record JawazatPolicyDto(Guid ProfileId, Guid CompanyId, DateOnly EffectiveFrom, DateOnly? EffectiveTo, JawazatPolicy Policy, bool CanCreateEmployerAssisted, bool CanRecordWorkerNotification);
public sealed record JawazatCheck(string Code, string Result, string EvidenceSource, DateTime? EvidenceAtUtc, string Reason);
public sealed record JawazatEvaluation(JawazatPolicySnapshot PolicySnapshot, IReadOnlyList<JawazatCheck> Checks, bool CanSubmitToGovernment);

public sealed record JawazatCreateRequest(
    int? EmployeeId,
    [Required] string Route,
    [Required] string Service,
    DateOnly DepartureDate,
    DateOnly ReturnDate,
    [Required, MaxLength(1000)] string Reason,
    Guid IdempotencyKey);

/// <summary>Versioned payload in the existing HR ticket. No client can write this document directly.</summary>
public sealed record JawazatRequestData(
    int SchemaVersion,
    string Route,
    string Service,
    DateOnly DepartureDate,
    DateOnly ReturnDate,
    string Reason,
    Guid IdempotencyKey,
    string InternalState,
    string ProviderState,
    JawazatPolicySnapshot PolicySnapshot,
    IReadOnlyList<JawazatCheck> Checks,
    DateTime? LastProviderAttemptAtUtc = null,
    string? ProviderMessage = null,
    string? DecisionNote = null);

public sealed record JawazatRequestDto(Guid Id, int EmployeeId, Guid CompanyId, string Subject, string Status, Guid? ApprovalRequestId, int WorkflowVersion, DateTime CreatedAtUtc, JawazatRequestData Data);
public sealed record JawazatCapabilities(string Provider, bool IsAvailable, IReadOnlyList<string> SupportedOperations, string Message);
public sealed record JawazatProviderResult(string State, string Message, IReadOnlyList<JawazatCheck> Checks);

public interface IJawazatProvider
{
    Task<JawazatCapabilities> GetCapabilitiesAsync(CancellationToken ct);
    Task<JawazatProviderResult> VerifyAsync(JawazatRequestData request, CancellationToken ct);
    Task<JawazatProviderResult> SubmitAsync(JawazatRequestData request, CancellationToken ct);
    Task<JawazatProviderResult> GetOutcomeAsync(JawazatRequestData request, CancellationToken ct);
}

public interface IJawazatWorkflowService
{
    Task<JawazatCapabilities> GetCapabilitiesAsync(CancellationToken ct);
    Task<JawazatPolicyDto> GetPolicyAsync(JawazatActor actor, int? employeeId, CancellationToken ct);
    Task<JawazatEvaluation> EvaluateAsync(JawazatActor actor, JawazatCreateRequest request, CancellationToken ct);
    Task<JawazatRequestDto> CreateAsync(JawazatActor actor, JawazatCreateRequest request, CancellationToken ct);
    Task<JawazatRequestDto> GetAsync(JawazatActor actor, Guid id, CancellationToken ct);
    Task<IReadOnlyList<JawazatRequestDto>> ListAsync(JawazatActor actor, CancellationToken ct);
    Task<JawazatRequestDto> SubmitAsync(JawazatActor actor, Guid id, CancellationToken ct);
    Task<JawazatRequestDto> ReconcileAsync(JawazatActor actor, Guid id, CancellationToken ct);
}

public sealed class JawazatException(string code, string message, int statusCode = 409) : InvalidOperationException(message)
{
    public string Code { get; } = code;
    public int StatusCode { get; } = statusCode;
}

public static class JawazatJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };
    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);
    public static JawazatRequestData Read(string json)
    {
        try
        {
            var data = JsonSerializer.Deserialize<JawazatRequestData>(json, Options);
            if (data is null || data.SchemaVersion != 1 || data.PolicySnapshot is null || data.Checks is null)
                throw new JsonException();
            return data;
        }
        catch (JsonException) { throw new JawazatException("invalid_request_data", "The Jawazat request has an unsupported or invalid data version."); }
    }
}

public static class JawazatPolicyRules
{
    public static string? ValidateJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        if (json.Length > 16000) return "JawazatPolicyJson is too large.";
        JawazatPolicy? policy;
        try { policy = JsonSerializer.Deserialize<JawazatPolicy>(json, JawazatJson.Options); }
        catch (JsonException) { return "JawazatPolicyJson must be a valid policy object with known fields."; }
        if (policy is null || policy.SchemaVersion != 1) return "Jawazat policy schemaVersion must be 1.";
        if (policy.MinimumPassportValidityDays is < 90 or > 3650) return "Passport validity must be between 90 and 3650 days.";
        if (policy.MaximumTripDays is < 1 or > 365) return "Company maximumTripDays must be between 1 and 365.";
        if (policy.RuleVersion?.Length > 120 || policy.ReviewedBy?.Length > 240) return "Jawazat policy review fields are too long.";
        if (policy.EmployerAssistedEnabled || policy.WorkerNotificationEnabled)
        {
            if (string.IsNullOrWhiteSpace(policy.RuleVersion) || string.IsNullOrWhiteSpace(policy.ReviewedBy) || policy.ReviewedAtUtc is null)
                return "An enabled Jawazat policy requires ruleVersion, reviewedBy and reviewedAtUtc.";
            if (policy.ReviewedAtUtc.Value > DateTime.UtcNow.AddMinutes(5)) return "Policy review time cannot be in the future.";
        }
        return null;
    }

    public static JawazatPolicy Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) throw new JawazatException("policy_not_configured", "This company has no Jawazat policy.", 422);
        var error = ValidateJson(json);
        if (error is not null) throw new JawazatException("invalid_policy", error, 422);
        return JsonSerializer.Deserialize<JawazatPolicy>(json, JawazatJson.Options)!;
    }
}
