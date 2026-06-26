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

public interface IRecruitmentAiService
{
    Task<JobDescriptionResult> GenerateJobDescriptionAsync(JobDescriptionRequest req, CancellationToken ct);
    /// <summary>Scores/ranks candidates against a role. Advisory only — never auto-rejects.</summary>
    Task<ScreeningResult> ScreenAsync(ScreeningInput input, CancellationToken ct);
    Task<InterviewQuestionsResult> GenerateInterviewQuestionsAsync(InterviewQuestionsRequest req, CancellationToken ct);
}
