namespace Zayra.Api.Application.Recruitment;

// ── Job description generation ──────────────────────────────────────────────

public sealed record JobDescriptionRequest(
    string Title,
    string? DepartmentName,
    string? DesignationTitle,
    string EmploymentType,
    string? SeniorityLevel,
    string? CountryCode,
    string? Notes);

public sealed record JobDescriptionResult(
    string Summary,
    List<string> Responsibilities,
    List<string> Requirements,
    string Engine);

// ── Candidate screening / ranking (advisory only) ───────────────────────────

public sealed record CandidateForScreening(
    Guid CandidateId,
    string Name,
    string CurrentJobTitle,
    decimal ExperienceYears,
    string EducationLevel,
    string Tags);

public sealed record ScreeningInput(
    string JobTitle,
    string Description,
    string Requirements,
    IReadOnlyList<CandidateForScreening> Candidates);

public sealed record CandidateScore(
    Guid CandidateId,
    string Name,
    int Score,                 // 0–100
    string Recommendation,     // Shortlist | Maybe | Reject
    string Rationale);

public sealed record ScreeningResult(
    List<CandidateScore> Ranked,
    string Engine,
    List<string> Notes);

// ── Interview question generation ───────────────────────────────────────────

public sealed record InterviewQuestionsRequest(string Title, string? SeniorityLevel, string? Notes);
public sealed record QuestionCategory(string Category, List<string> Questions);
public sealed record InterviewQuestionsResult(List<QuestionCategory> Categories, string Engine);

// ── Who asked ───────────────────────────────────────────────────────────────

/// <summary>
/// The caller behind a recruitment AI request.
///
/// <para>Every model call must produce a usage/cost record carrying tenant, user and role. This
/// service is not a controller and there is no ambient tenant accessor in DI, so the identity has
/// to be threaded in explicitly — the same shape <c>AiUserContext</c> already uses for the
/// advisory path. Passing it in rather than reading it from an ambient HttpContext also keeps the
/// service usable from a background job, and keeps the tenant visible at every call site.</para>
/// </summary>
/// <param name="UserRole">Comma-joined role claims, as <c>AiAdvisoryService</c> records them.</param>
public sealed record RecruitmentAiCaller(Guid TenantId, Guid? UserId, string UserRole);

public interface IRecruitmentAiService
{
    Task<JobDescriptionResult> GenerateJobDescriptionAsync(RecruitmentAiCaller caller, JobDescriptionRequest req, CancellationToken ct);
    /// <summary>Scores/ranks candidates against a role. Advisory only — never auto-rejects.</summary>
    Task<ScreeningResult> ScreenAsync(RecruitmentAiCaller caller, ScreeningInput input, CancellationToken ct);
    Task<InterviewQuestionsResult> GenerateInterviewQuestionsAsync(RecruitmentAiCaller caller, InterviewQuestionsRequest req, CancellationToken ct);
}
