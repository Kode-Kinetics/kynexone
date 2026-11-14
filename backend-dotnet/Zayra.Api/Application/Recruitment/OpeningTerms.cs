using Zayra.Api.Controllers.Recruitment;
using Zayra.Api.Models;

namespace Zayra.Api.Application.Recruitment;

/// <summary>
/// What an opening is created with. An opening raised from an approved requisition inherits the requisition's placement
/// (department, designation, grade), budget range (as its salary range) and required skills (as its requirements) for
/// everything the request leaves blank; what the request does state wins. Pure, so the rule is tested without a database.
/// </summary>
public static class OpeningTerms
{
    public sealed record Terms(Guid? DepartmentId, string DepartmentName, Guid? DesignationId, string DesignationTitle, Guid? GradeId,
        decimal? SalaryFrom, decimal? SalaryTo, string Requirements);

    public static Terms Inherit(CreateOpeningRequest req, ManpowerRequisition? requisition)
    {
        static string Pick(string? stated, string? inherited) => !string.IsNullOrWhiteSpace(stated) ? stated : inherited ?? string.Empty;
        return new Terms(
            req.DepartmentId ?? requisition?.DepartmentId,
            Pick(req.DepartmentName, requisition?.DepartmentName),
            req.DesignationId ?? requisition?.DesignationId,
            Pick(req.DesignationTitle, requisition?.DesignationTitle),
            req.GradeId ?? requisition?.GradeId,
            req.SalaryFrom ?? requisition?.BudgetFrom,
            req.SalaryTo ?? requisition?.BudgetTo,
            Pick(req.Requirements, requisition?.RequiredSkills));
    }
}

public enum OfferStageGateOutcome
{
    /// <summary>The move is not into Offer: nothing to check.</summary>
    NotApplicable,
    /// <summary>An interview or assessment is on record.</summary>
    Allowed,
    /// <summary>No interview or assessment, and no acceptable reason: the move is refused.</summary>
    NeedsEvaluation,
    /// <summary>No interview or assessment, moved on with a recorded reason.</summary>
    OverriddenWithReason,
}

/// <summary>An application cannot reach Offer without an interview or an assessment on record, unless the person moving it says why.</summary>
public static class OfferStageGate
{
    public const string ErrorCode = "offer_stage_needs_evaluation";
    public const int MinReasonLength = 5;
    public const int MaxReasonLength = 1000;

    public static OfferStageGateOutcome Evaluate(string toStage, bool hasInterview, bool hasAssessment, string? overrideReason)
    {
        if (toStage != "Offer") return OfferStageGateOutcome.NotApplicable;
        if (hasInterview || hasAssessment) return OfferStageGateOutcome.Allowed;
        var reason = overrideReason?.Trim() ?? string.Empty;
        return reason.Length is >= MinReasonLength and <= MaxReasonLength ? OfferStageGateOutcome.OverriddenWithReason : OfferStageGateOutcome.NeedsEvaluation;
    }
}
