using System.Security.Claims;
using Zayra.Api.Models;

namespace Zayra.Api.Controllers.Recruitment;

/// <summary>Who may write feedback on an interview. HR coordinators run panels and may record for any interviewer; anyone else (a line manager
/// on the panel) may only write on an interview they are named on. One rule, used by both feedback endpoints.</summary>
public static class InterviewPanel
{
    private static readonly string[] CoordinatorRoles = ["Admin", "HR Manager", "HR Officer", "Recruiter"];

    public static bool IsCoordinator(ClaimsPrincipal user) => CoordinatorRoles.Any(user.IsInRole);

    public static bool IsAssigned(InterviewSchedule interview, string? name) =>
        !string.IsNullOrWhiteSpace(name) && interview.InterviewerNames.Contains(name, StringComparison.OrdinalIgnoreCase);

    public static bool MayWriteFeedback(ClaimsPrincipal user, InterviewSchedule interview, string? name) =>
        IsCoordinator(user) || IsAssigned(interview, name);
}
