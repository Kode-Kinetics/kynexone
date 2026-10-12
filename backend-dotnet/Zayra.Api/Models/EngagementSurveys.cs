using Zayra.Api.Domain.Entities;

namespace Zayra.Api.Models;

// ─────────────────────────────────────────────────────────────────────────────────────────────────────────────────────
// ENG — Engagement surveys. Six tables; the privacy model is docs/acceptance/capability-closure/REGISTER.md §3.2 and
// ENG_DESIGN.md. Two of them (survey_responses, survey_answers) are the anonymous side of the design and are held to
// rules the rest of the schema is not:
//   * NO identity column — no CreatedBy/UpdatedBy/UserId/EmployeeId/InvitationId, and no CreatedAtUtc/UpdatedAtUtc
//     either: ZayraDbContext.SaveChangesAsync stamps those BY PROPERTY NAME with the caller's id and the exact time.
//     EngagementPrivacyRatchetTests fails the build if one appears.
//   * random GUID ids, NO date or time (the survey cycle is the only "when"), and only coarse cohort attributes captured at
//     submission (company, department, designation, grade band, tenure band, and the line manager as a TEAM
//     attribute used only to scope a manager's own view).
// The participation ledger (survey_invitations) knows who was invited and whether they submitted; it holds no answer.
// ─────────────────────────────────────────────────────────────────────────────────────────────────────────────────────

/// <summary>Response modes, shown to respondents verbatim through <c>EngagementPrivacyNotices</c>.</summary>
public static class EngagementSurveyModes
{
    /// <summary>Answers are linked to the employee; reading them needs engagement.identified.read.</summary>
    public const string Identified = "Identified";
    /// <summary>The ledger knows who responded (targeted reminders); answers carry no identity; aggregate only.</summary>
    public const string Confidential = "Confidential";
    /// <summary>As Confidential, but HR sees non-respondents only as a count; reminders go to everyone not yet opened.</summary>
    public const string Anonymous = "Anonymous";

    public static readonly string[] All = [Identified, Confidential, Anonymous];
    public static bool IsValid(string? mode) => mode is not null && All.Contains(mode, StringComparer.Ordinal);
}

public static class EngagementTemplateStatuses
{
    public const string Draft = "Draft";
    public const string Published = "Published";
    public const string Retired = "Retired";
    public static readonly string[] All = [Draft, Published, Retired];
}

public static class EngagementSurveyStatuses
{
    public const string Draft = "Draft";
    /// <summary>Launched with an open date in the future; the worker opens it at <c>OpensAtUtc</c>.</summary>
    public const string Scheduled = "Scheduled";
    public const string Open = "Open";
    public const string Closed = "Closed";
    public const string Cancelled = "Cancelled";
    public static readonly string[] All = [Draft, Scheduled, Open, Closed, Cancelled];
}

public static class EngagementInvitationStatuses
{
    public const string Invited = "Invited";
    /// <summary>The employee opened the survey at least once (a single-use session token was issued).</summary>
    public const string Opened = "Opened";
    public const string Submitted = "Submitted";
    public const string Cancelled = "Cancelled";
    public static readonly string[] All = [Invited, Opened, Submitted, Cancelled];
}

public static class EngagementActionStatuses
{
    public const string Open = "Open";
    public const string InProgress = "InProgress";
    public const string Done = "Done";
    public const string Cancelled = "Cancelled";
    public static readonly string[] All = [Open, InProgress, Done, Cancelled];
}

public static class EngagementRecurrences
{
    public const string None = "None";
    public const string Monthly = "Monthly";
    public const string Quarterly = "Quarterly";
    public const string SemiAnnual = "SemiAnnual";
    public const string Annual = "Annual";
    public static readonly string[] All = [None, Monthly, Quarterly, SemiAnnual, Annual];

    /// <summary>Months between cycles; 0 for <see cref="None"/>.</summary>
    public static int Months(string recurrence) => recurrence switch
    {
        Monthly => 1,
        Quarterly => 3,
        SemiAnnual => 6,
        Annual => 12,
        _ => 0,
    };
}

/// <summary>
/// A survey template version. Questions are an immutable JSON snapshot once published.
///
/// <para>WHY A TABLE. No existing table holds a versioned questionnaire. <c>employee_sentiment_pulses</c> (an unused,
/// identified orphan left in place by this release — no DROP ships with it) is a single per-employee score with no
/// questions, versions or audience; the 360 feedback model is per-reviewer and identified by design. HR owns it;
/// kept while any survey references it (FK RESTRICT), so a published version can always explain old results.</para>
/// </summary>
public class EngagementSurveyTemplate : ITenantOwned, ICompanyScoped
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    /// <summary>Null = a tenant-wide template every company may use.</summary>
    public Guid? CompanyId { get; set; }
    /// <summary>Groups the versions of one template. Version 1's id.</summary>
    public Guid FamilyId { get; set; }
    public int VersionNumber { get; set; } = 1;
    public string Status { get; set; } = EngagementTemplateStatuses.Draft;
    public string NameEn { get; set; } = string.Empty;
    public string NameAr { get; set; } = string.Empty;
    public string DescriptionEn { get; set; } = string.Empty;
    public string DescriptionAr { get; set; } = string.Empty;
    /// <summary>{ drivers: [...], questions: [...] } — see <c>EngagementTemplateContent</c>. Frozen once published
    /// (a database trigger refuses any change to a non-draft row's content).</summary>
    public string ContentJson { get; set; } = "{}";
    public DateTime? PublishedAtUtc { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public Guid? CreatedBy { get; set; }
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
    public Guid? UpdatedBy { get; set; }
    public int Version { get; set; }
}

/// <summary>
/// One survey cycle: the template version it asks, its mode, audience, dates, recurrence and minimum cohort.
///
/// <para>WHY A TABLE. A survey has its own lifecycle (Draft → Scheduled/Open → Closed, or Cancelled), audience and
/// privacy mode, independent of the template it asks; recurring cycles share a <see cref="SeriesId"/> so results
/// trend across them. HR owns it; deleting a closed survey deletes its invitations, responses and answers (CASCADE)
/// and is audited at survey level.</para>
///
/// <para>SCOPING. <see cref="ICompanyScoped"/>, not Operational: a null company is a group-wide survey that every
/// company's HR and every invited employee must be able to see. Results are cut to the caller's companies by the
/// response rows, which ARE operational; managing a group-wide survey needs group scope (EngagementController).</para>
/// </summary>
public class EngagementSurvey : ITenantOwned, ICompanyScoped
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid? CompanyId { get; set; }
    public Guid TemplateId { get; set; }
    public string TitleEn { get; set; } = string.Empty;
    public string TitleAr { get; set; } = string.Empty;
    public string Mode { get; set; } = EngagementSurveyModes.Anonymous;
    public string Status { get; set; } = EngagementSurveyStatuses.Draft;
    /// <summary>{ departmentIds, designationIds, gradeIds, companyIds, excludeJoinedWithinDays } — see EngagementAudience.</summary>
    public string AudienceJson { get; set; } = "{}";
    public DateTime OpensAtUtc { get; set; }
    public DateTime ClosesAtUtc { get; set; }
    public string Recurrence { get; set; } = EngagementRecurrences.None;
    /// <summary>All cycles of one recurring survey share this id (the first cycle's id).</summary>
    public Guid SeriesId { get; set; }
    public int CycleNumber { get; set; } = 1;
    /// <summary>The smallest group whose results may be shown (k). Never below the floor of 3.</summary>
    public int MinCohortSize { get; set; } = 5;
    /// <summary>
    /// The k this survey's results were decided at, frozen when it closes (or, for a survey closed before this column
    /// existed, on its first results computation). A later tenant or survey increase can only hide more on top of the
    /// views visible at this k; nothing ever lowers it. Null while open.
    /// </summary>
    public int? FrozenMinCohortSize { get; set; }
    /// <summary>
    /// The ONE attribute comments may be read by (besides the whole survey): company, department, designation, grade,
    /// tenure, or none. Never combined with another filter, so comment groups cannot be intersected.
    /// </summary>
    public string CommentDimension { get; set; } = "department";
    /// <summary>Comma-separated days after opening on which non-respondents are reminded, e.g. "3,7".</summary>
    public string ReminderOffsetsDays { get; set; } = string.Empty;
    public int InvitationCount { get; set; }
    public DateTime? LaunchedAtUtc { get; set; }
    public Guid? LaunchedBy { get; set; }
    /// <summary>Set once every invitation's SURVEY_INVITE was enqueued. Null on an open survey = the worker retries
    /// (the outbox dedupe key makes the retry send nothing twice).</summary>
    public DateTime? InvitesSentAtUtc { get; set; }
    public DateTime? ClosedAtUtc { get; set; }
    /// <summary>Set when the worker has created the next cycle (the unique series/cycle index is the real guard).</summary>
    public DateTime? NextCycleCreatedAtUtc { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public Guid? CreatedBy { get; set; }
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
    public Guid? UpdatedBy { get; set; }
    public int Version { get; set; }
}

/// <summary>
/// The participation ledger: one row per invited employee per survey. It never holds an answer.
///
/// <para>WHY A TABLE. Dedupe (one invitation per survey × employee, DB-unique), the single-use session token, the
/// draft, reminders and the submitted flag all belong to the INVITEE, while answers must not. Splitting them is the
/// privacy design: there is no column from here to <see cref="EngagementSurveyResponse"/> except
/// <see cref="IdentifiedResponseId"/>, which a CHECK allows only in Identified mode.</para>
///
/// <para>WHAT IS DELIBERATELY ABSENT. No UpdatedAtUtc/UpdatedBy (the context would stamp the exact submission time);
/// <see cref="SubmittedOn"/> and <see cref="FirstOpenedOn"/> are dates, not times, and in ANONYMOUS mode both stay null
/// and the status never becomes Opened (CHECK): the ledger of an anonymous survey keeps only invited / submitted. The draft is encrypted with
/// DataProtection and discarded on submit and at close. HR owns it; deleted with its survey (CASCADE) or its
/// employee (CASCADE).</para>
/// </summary>
public class EngagementSurveyInvitation : ITenantOwned, ICompanyScopedOperational
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid? CompanyId { get; set; }
    public Guid SurveyId { get; set; }
    public int EmployeeId { get; set; }
    /// <summary>Copied from the survey at invitation; immutable. Lets a CHECK tie the identified link to the mode.</summary>
    public string Mode { get; set; } = EngagementSurveyModes.Anonymous;
    public string Status { get; set; } = EngagementInvitationStatuses.Invited;
    public DateTime InvitedAtUtc { get; set; } = DateTime.UtcNow;
    public DateOnly? FirstOpenedOn { get; set; }
    public DateOnly? SubmittedOn { get; set; }
    /// <summary>SHA-256 (hex) of the current single-use session token; cleared on submit.</summary>
    public string? SessionTokenHash { get; set; }
    /// <summary>DataProtection-protected draft answers. Not readable by any HR endpoint; never reported.</summary>
    public string? DraftCipher { get; set; }
    public DateOnly? DraftSavedOn { get; set; }
    /// <summary>Identified mode only (CHECK): the response these answers became.</summary>
    public Guid? IdentifiedResponseId { get; set; }
    public int RemindersSent { get; set; }
    public int Version { get; set; }
}

/// <summary>
/// One submitted response. NO IDENTITY: see the file header and EngagementPrivacyRatchetTests.
///
/// <para>WHY A TABLE. The anonymous unit of analysis: cohort attributes captured at submission so results do not
/// move when people change department later, and so nothing needs to join back to an employee. HR owns it; deleted
/// with its survey (CASCADE); free text in its answers is purged after the tenant's retention period while the
/// numeric answers are kept (EngagementFreeTextRetentionRule).</para>
/// </summary>
public class EngagementSurveyResponse : ITenantOwned, ICompanyScopedOperational
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid SurveyId { get; set; }
    /// <summary>Cohort: the respondent's company at submission.</summary>
    public Guid? CompanyId { get; set; }
    public Guid? DepartmentId { get; set; }
    public Guid? DesignationId { get; set; }
    /// <summary>Grade band (<c>Grade.Band</c>, else the grade code) at submission; empty when ungraded.</summary>
    public string GradeBand { get; set; } = string.Empty;
    /// <summary>One of <c>EngagementTenureBands</c>.</summary>
    public string TenureBand { get; set; } = string.Empty;
    /// <summary>
    /// TEAM attribute: the respondent's line manager's employee id at submission — never the respondent. No FK. Used
    /// only to cut a manager's own view to their team; every slice it forms is suppressed like any other.
    /// </summary>
    public int? CohortManagerEmployeeId { get; set; }
    // No submission date or time at all: the survey (one cycle) is the only "when". A date would let anyone who can
    // read the ledger's submitted flags match a response to the few people who submitted that day.
}

/// <summary>
/// One answer to one question of one response. NO IDENTITY.
///
/// <para>WHY A TABLE. Per-item distributions, means, favourable %, eNPS and comment retention all work on answers;
/// JSON on the response would make every metric a parse and the free-text purge a rewrite. HR owns it; deleted with
/// its response (CASCADE). <see cref="CompanyId"/> repeats the response's so the company filter applies here too.</para>
/// </summary>
public class EngagementSurveyAnswer : ITenantOwned, ICompanyScopedOperational
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid? CompanyId { get; set; }
    public Guid SurveyId { get; set; }
    public Guid ResponseId { get; set; }
    public string QuestionKey { get; set; } = string.Empty;
    /// <summary>Likert 1–5 or eNPS 0–10.</summary>
    public int? NumericValue { get; set; }
    /// <summary>Selected option keys, comma-separated (single or multi choice).</summary>
    public string? ChoiceKeys { get; set; }
    public string? TextValue { get; set; }
    /// <summary>True once the retention rule removed the text; the row stays so counts reconcile.</summary>
    public bool TextPurged { get; set; }
}

/// <summary>
/// A follow-up action raised from a result (a driver, an item, a cohort or the whole survey).
///
/// <para>WHY A TABLE. Owners, due dates, status and progress belong to the action, not the survey; one survey has
/// many. Not an approval request (nothing is decided) nor an HR request (it is not employee-raised). HR owns it;
/// kept when its survey is deleted (SurveyId SET NULL, title snapshot kept), audited at survey level.</para>
/// </summary>
public class EngagementAction : ITenantOwned, ICompanyScopedOperational
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid? CompanyId { get; set; }
    public Guid? SurveyId { get; set; }
    public string SurveyTitle { get; set; } = string.Empty;
    /// <summary>Overall | Driver | Item | Cohort.</summary>
    public string SourceKind { get; set; } = "Overall";
    /// <summary>The driver key, question key or cohort label the action answers.</summary>
    public string SourceRef { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public int OwnerEmployeeId { get; set; }
    public DateOnly DueOn { get; set; }
    public string Status { get; set; } = EngagementActionStatuses.Open;
    /// <summary>[{ atUtc, byName, status, note }] — progress history, newest last. Never queried or reported on.</summary>
    public string ProgressJson { get; set; } = "[]";
    public DateTime? CompletedAtUtc { get; set; }
    public DateOnly? OverdueRemindedOn { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public Guid? CreatedBy { get; set; }
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
    public Guid? UpdatedBy { get; set; }
    public int Version { get; set; }
}

public static class EngagementTenureBands
{
    public const string Under1Year = "lt1y";
    public const string OneToThree = "1to3y";
    public const string ThreeToFive = "3to5y";
    public const string FivePlus = "5yplus";
    public const string Unknown = "unknown";
    public static readonly string[] All = [Under1Year, OneToThree, ThreeToFive, FivePlus, Unknown];

    public static string For(DateTime joiningDate, DateOnly asOf)
    {
        if (joiningDate == default || joiningDate.Year < 1950) return Unknown;
        var joined = DateOnly.FromDateTime(joiningDate);
        if (joined > asOf) return Under1Year;
        var months = (asOf.Year - joined.Year) * 12 + asOf.Month - joined.Month - (asOf.Day < joined.Day ? 1 : 0);
        return months switch
        {
            < 12 => Under1Year,
            < 36 => OneToThree,
            < 60 => ThreeToFive,
            _ => FivePlus,
        };
    }
}
