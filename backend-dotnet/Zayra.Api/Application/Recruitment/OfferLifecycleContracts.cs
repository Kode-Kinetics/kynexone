using System.Text.Json.Serialization;

namespace Zayra.Api.Application.Recruitment;

// Unknown fields are refused: required flags, currency and payroll mappings are server-owned.
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record OfferComponentInput(string Code, string LabelEn, string? LabelAr, string Kind,
    decimal? Amount, string? Description, string? Frequency, bool Included, string? ExclusionReason,
    Guid? PayComponentId, int Order);

public sealed record OfferComponentSnapshot(string Code, string LabelEn, string? LabelAr, string Kind,
    decimal? Amount, string? Description, string? Frequency, bool Included, string? ExclusionReason,
    Guid? PayComponentId, int Order, bool Required, string? StructureField = null,
    DateOnly? EffectiveFrom = null, DateOnly? EffectiveTo = null, string? Classification = null,
    string MappingSource = "Custom", string? DefinitionCode = null, string? DefinitionLabelEn = null,
    string? DefinitionLabelAr = null, bool IsTaxable = false, bool GosiSubject = false,
    bool WpsIncluded = false, bool EosbIncluded = false);

public sealed record OfferCompensationSnapshot(int SchemaVersion, string Currency, int Precision,
    IReadOnlyList<OfferComponentSnapshot> Components, decimal MonthlyCash, decimal AnnualRecurringCash,
    decimal OneTimeCash, decimal BasicSalary, decimal HousingAllowance, decimal TransportAllowance,
    decimal OtherAllowances, long PolicyVersion = 0, string PolicySource = "CompanyDefault");

public sealed record OfferActor(Guid TenantId, Guid? CompanyId, Guid UserId, string Name);
public sealed record OfferEmailRequest(long ExpectedVersion, string IdempotencyKey, string Subject, string Message,
    string ReviewedSenderFingerprint, string? ResendReason = null, bool ConfirmResend = false);
public sealed record OfferMailReadiness(bool Configured, string Mode, string Source, string? FromAddress,
    string? FromName, bool CanManageSettings, string? BlockedReason, string ConfigurationFingerprint, bool RecipientRestricted);
[System.Text.Json.Serialization.JsonConverter(typeof(System.Text.Json.Serialization.JsonStringEnumConverter<OfferReconciliationAction>))]
public enum OfferReconciliationAction { ApplyRetainedRelayEvidence, RecordConfirmedExternalSending, AuthorizeRiskAcknowledgedResend }
public sealed record OfferDispatchCancelRequest(long ExpectedVersion, string Reason);
public sealed record OfferDispatchReconcileRequest(long ExpectedVersion, long ExpectedReservationVersion,
    OfferReconciliationAction Action, string Reason, string? EvidenceReference = null, DateTime? OccurredAtUtc = null,
    bool AcknowledgeDuplicateRisk = false, OfferEmailRequest? Replacement = null);
public sealed record OfferDispatchDto(Guid Id, Guid OfferId, long Version, string Outcome, long ReservationVersion,
    string? ReservationState, string Subject, string Message, string RecipientEmail, string RecipientName,
    Guid ArtifactId, Guid InitiatorUserId, string InitiatorName, string? FromAddress, string? FromName,
    string SenderSource, string SenderFingerprint, DateTime CreatedAtUtc, DateTime? FinalizedAtUtc,
    IReadOnlyList<string> AllowedActions, IReadOnlyList<OfferAttemptDto> Attempts, string? TransportOutcome = null);
public sealed record OfferAttemptDto(Guid Id, string Stage, string Outcome, string Code, DateTime OccurredAtUtc);
public sealed record OfferMutationRequest(long ExpectedVersion);
public sealed record OfferArtifactDto(Guid Id, string Sha256, long SizeBytes, string Provenance, DateTime? FrozenAtUtc, long OfferVersion);
public sealed record OfferExternalSendRequest(long ExpectedVersion, string IdempotencyKey, string Channel,
    DateTime OccurredAtUtc, string? Reference = null, string? Reason = null);
public sealed record OfferReservationDto(Guid ApplicationId, Guid OfferId, Guid OperationId, string State,
    long ReservationVersion, long OfferVersion);
public sealed record OfferResponseRequest(long ExpectedVersion, string Decision, string Source, DateTime OccurredAtUtc,
    string Reason, string? EvidenceReference = null);
public sealed record OfferResponseDto(Guid OfferId, long Version, string Status, Guid? OnboardingDraftId, bool AlreadyRecorded);
public sealed record OfferReasonRequest(long ExpectedVersion, string Reason);
public sealed record OfferCreateInput(DateOnly StartDate, int ProbationMonths, string? OfferedJobTitle = null, string? OfferedDepartment = null,
    Guid? DepartmentId = null, Guid? DesignationId = null, DateTime? ResponseDeadline = null, Guid? TemplateId = null,
    IReadOnlyList<OfferComponentInput>? Components = null, decimal? BasicSalary = null, decimal? HousingAllowance = null,
    decimal? TransportAllowance = null, decimal? OtherAllowances = null, string? ContentHtml = null, long? ExpectedApplicationVersion = null);
public sealed record OfferHistoryQuery(string? Search = null, Guid? ApplicationId = null, Guid? CompanyId = null,
    string? Position = null, string? Status = null, string? CommunicationOutcome = null,
    DateTime? IssuedFrom = null, DateTime? IssuedTo = null, int Page = 1, int PageSize = 20);
public sealed record OfferPage<T>(IReadOnlyList<T> Items, int Total, int Page, int PageSize);
public sealed record OfferHistoryItem(Guid Id, Guid ApplicationId, Guid? CompanyId, Guid? SeriesId, int? RevisionNumber,
    long Version, string CandidateName, string Position, string Status, string CommunicationOutcome,
    string OriginalProvenance, DateTime GeneratedAtUtc, DateTime? SentAtUtc, DateTime? ResponseDeadline, DateTime? SupersededAtUtc);
public sealed record OfferActionCapability(bool Allowed, string? Reason = null);
public sealed record OfferDetailDto(Zayra.Api.Models.OfferLetter Offer, OfferArtifactDto? Artifact,
    string OriginalProvenance, IReadOnlyList<Zayra.Api.Models.OfferApproval> Approvals,
    IReadOnlyDictionary<string, OfferActionCapability> Capabilities, IReadOnlyList<OfferHistoryItem> Revisions,
    IReadOnlyList<string> ChangedFields, string CommunicationOutcome,
    System.Text.Json.JsonElement? CompensationSnapshot, System.Text.Json.JsonElement? DesignSnapshot);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record OfferDraftInput(long ExpectedVersion, long? ReviewedPolicyVersion,
    DateOnly StartDate, DateTime? ResponseDeadline, int ProbationMonths, string RecipientEmail,
    string OfferedJobTitle, string OfferedDepartment, IReadOnlyList<OfferComponentInput>? Components = null,
    decimal? BasicSalary = null, decimal? HousingAllowance = null, decimal? TransportAllowance = null,
    decimal? OtherAllowances = null, Guid? DepartmentId = null, Guid? DesignationId = null,
    Guid? TemplateId = null, string? DesignJson = null, bool AdoptLegacy = false, string? RecipientChangeReason = null);
public sealed record OfferPolicyDto(Guid CompanyId, string Currency, int Precision,
    IReadOnlyList<string> RequiredComponentCodes, long ExpectedVersion);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record OfferPolicyUpdate(int Precision, IReadOnlyList<string> RequiredComponentCodes, long ExpectedVersion);
public sealed record OfferComponentOption(Guid Id, string Code, string LabelEn, string LabelAr,
    string? StructureField, DateOnly? EffectiveFrom, DateOnly? EffectiveTo, string Classification);
public sealed record OfferCanonicalBasicOption(string Code = "BASIC", string LabelEn = "Basic salary",
    string LabelAr = "الراتب الأساسي", string Kind = "Cash", string Frequency = "Monthly",
    string StructureField = "BasicSalary", bool Required = true);
public sealed record OfferComponentOptionsDto(OfferPolicyDto Policy, IReadOnlyList<OfferComponentOption> Options,
    OfferCanonicalBasicOption CanonicalBasic);
