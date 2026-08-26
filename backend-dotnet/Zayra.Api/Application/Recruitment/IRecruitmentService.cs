using Zayra.Api.Models;

namespace Zayra.Api.Application.Recruitment;

public interface IRecruitmentService
{
    Task<string> GenerateRequisitionNumberAsync(Guid tenantId, CancellationToken ct = default);
    Task<string> GenerateJobCodeAsync(Guid tenantId, CancellationToken ct = default);
    Task<Guid?> CreateApprovalRequestAsync(Guid tenantId, string entityName, Guid entityId, string title, Guid? requestedByUserId, CancellationToken ct = default);
    string GenerateOfferLetterHtml(OfferLetterTemplateData data);
    Task<OfferAcceptanceResult> AcceptOfferAsync(
        Guid tenantId,
        Guid offerId,
        Guid requestedByUserId,
        string performedByName,
        CancellationToken ct = default);
}

public enum OfferAcceptanceOutcome
{
    Accepted,
    AlreadyAccepted,
    NotFound,
    InvalidOfferState,
    InvalidApplicationState,
    IncompleteRecruitmentData,
}

public sealed record OfferAcceptanceResult(
    OfferAcceptanceOutcome Outcome,
    Guid OfferId,
    Guid? ApplicationId,
    Guid? OnboardingDraftId,
    string Message)
{
    public bool IsSuccess => Outcome is OfferAcceptanceOutcome.Accepted or OfferAcceptanceOutcome.AlreadyAccepted;
    public bool WasAcceptedNow => Outcome == OfferAcceptanceOutcome.Accepted;
}

public record OfferLetterTemplateData(
    string CandidateName,
    string JobTitle,
    string Department,
    DateOnly StartDate,
    decimal BasicSalary,
    decimal HousingAllowance,
    decimal TransportAllowance,
    decimal OtherAllowances,
    decimal GrossSalary,
    int ProbationMonths);
