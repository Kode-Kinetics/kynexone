using Zayra.Api.Models;

namespace Zayra.Api.Application.Onboarding;

/// <summary>
/// The rules every writer of an onboarding task row applies, in one place: a fixed list of statuses, a written reason before a task that holds
/// back activation is skipped, and evidence on file before a task that needs it is completed. The worklist and the legacy Recruitment >
/// Onboarding tab both call these, so neither can be a way round the other.
/// </summary>
public static class OnboardingTaskRules
{
    public static readonly string[] Statuses = { "Pending", "InProgress", "Completed", "Skipped", "Blocked" };
    public const int MinSkipReasonLength = 5;

    public static bool IsKnownStatus(string? status) => status is not null && Statuses.Contains(status, StringComparer.Ordinal);

    /// <summary>True when this change skips a task that blocks activation and no real reason was given.</summary>
    public static bool SkipReasonMissing(OnboardingTask task, string? newStatus, string? note) =>
        newStatus == "Skipped" && task.Status != "Skipped" && task.BlocksActivation && (note?.Trim().Length ?? 0) < MinSkipReasonLength;

    /// <summary>True when this change completes a task that needs evidence and none is attached yet.</summary>
    public static bool EvidenceMissing(OnboardingTask task, string? newStatus) =>
        newStatus == "Completed" && task.Status != "Completed" && task.EvidenceRequired && task.EvidenceDocumentId is null;
}
