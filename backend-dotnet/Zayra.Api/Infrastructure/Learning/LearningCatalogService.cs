using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Auth;
using Zayra.Api.Data;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Learning;

// ── Contracts ───────────────────────────────────────────────────────────────────────────────────────────────────────

public sealed record CourseInput(
    string? Code, string? Title, string? TitleAr, string? Description, string? DescriptionAr, int? EstimatedMinutes,
    string? ContentKind, string? LessonHtml, string? LinkUrl, string? Cmi5AuUrl, string? Cmi5ActivityId, string? Cmi5MoveOn,
    string? CompletionRule, IReadOnlyList<AssessmentQuestionInput>? Questions, decimal? PassMarkPercent, int? MaxAttempts,
    bool? ShuffleQuestions, int? ValidityMonths, bool? IssuesCertificate, int? Version, bool? RequiresHrVerification = null);

public sealed record CourseSummaryDto(Guid Id, string Code, int VersionNumber, string Status, string Title, string? TitleAr, string ContentKind,
    string CompletionRule, int? ValidityMonths, bool IssuesCertificate, int QuestionCount, DateTime? PublishedAtUtc, DateTime UpdatedAtUtc, int Version);

public sealed record CourseDetailDto(Guid Id, string Code, int VersionNumber, string Status, string Title, string? TitleAr, string? Description,
    string? DescriptionAr, int? EstimatedMinutes, string ContentKind, string? LessonHtml, string? LinkUrl, string? Cmi5AuUrl, string? Cmi5ActivityId,
    string? Cmi5MoveOn, string CompletionRule, decimal? PassMarkPercent, int? MaxAttempts, bool ShuffleQuestions, int? ValidityMonths,
    bool IssuesCertificate, int QuestionCount, IReadOnlyList<AssessmentQuestion>? Questions, IReadOnlyList<CourseSummaryDto> Versions,
    DateTime? PublishedAtUtc, DateTime UpdatedAtUtc, int Version, bool RequiresHrVerification);

public sealed record PathItemInput(Guid CourseId, bool? IsRequired, int? PrerequisiteSequence);

public sealed record PathInput(string? Code, string? Name, string? NameAr, string? Description, LearningEligibilityCriteria? Eligibility,
    DateOnly? EffectiveFrom, DateOnly? EffectiveTo, int? RenewalWindowDays, IReadOnlyList<PathItemInput>? Items, int? Version);

public sealed record PathItemDto(Guid Id, int Sequence, Guid CourseId, string CourseCode, string CourseTitle, string? CourseTitleAr,
    string CourseStatus, bool HasPublishedVersion, bool IsRequired, int? PrerequisiteSequence);

public sealed record PathDto(Guid Id, string Code, string Name, string? NameAr, string? Description, string Status, LearningEligibilityCriteria Eligibility,
    DateOnly? EffectiveFrom, DateOnly? EffectiveTo, int RenewalWindowDays, IReadOnlyList<PathItemDto> Items, int LiveAssignments,
    int CompletedAssignments, DateTime UpdatedAtUtc, int Version);

public sealed record RuleInput(Guid PathId, string? Name, string? TargetKind, Guid TargetId, int? DueInDays, bool? IsMandatory);

public sealed record RuleDto(Guid Id, Guid PathId, string PathName, string Name, string TargetKind, Guid TargetId, string TargetName,
    int DueInDays, bool IsMandatory, bool IsActive, DateTime? LastAppliedAtUtc, DateTime CreatedAtUtc);

/// <summary>
/// The catalogue: course versions (LRN-01), learning paths (LRN-02) and saved assignment rules (LRN-03). Every write is
/// audited after it commits (LRN-15) and carries the row's Version as its optimistic concurrency token.
/// </summary>
public sealed class LearningCatalogService(ZayraDbContext db, IAuditService audit, TimeProvider clock, LearningAssignmentService assignments)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    // ── Courses ─────────────────────────────────────────────────────────────────────────────────────────────────────

    public async Task<IReadOnlyList<CourseSummaryDto>> ListCoursesAsync(Guid tenantId, string? status, CancellationToken ct)
    {
        var query = db.LearningCourses.AsNoTracking().Where(c => c.TenantId == tenantId);
        if (!string.IsNullOrWhiteSpace(status)) query = query.Where(c => c.Status == status);
        var rows = await query.OrderBy(c => c.Code).ThenByDescending(c => c.VersionNumber).ToListAsync(ct);
        return rows.Select(Summary).ToList();
    }

    public async Task<CourseDetailDto> GetCourseAsync(Guid tenantId, Guid id, bool includeAnswerKey, CancellationToken ct)
    {
        var course = await db.LearningCourses.AsNoTracking().FirstOrDefaultAsync(c => c.TenantId == tenantId && c.Id == id, ct)
            ?? throw LearningException.NotFound("Course");
        var versions = await db.LearningCourses.AsNoTracking().Where(c => c.TenantId == tenantId && c.Code == course.Code)
            .OrderByDescending(c => c.VersionNumber).ToListAsync(ct);
        return Detail(course, versions, includeAnswerKey);
    }

    public async Task<CourseDetailDto> CreateCourseAsync(LearningActor actor, CourseInput input, CancellationToken ct)
    {
        var code = NormaliseCode(input.Code) ?? throw LearningException.Invalid("Give the course a code of 2–40 letters, digits or dashes.");
        if (await db.LearningCourses.AnyAsync(c => c.TenantId == actor.TenantId && c.Code == code, ct))
            throw LearningException.Conflict("course_code_taken", "A course with this code already exists. Open it and create a new version instead.");
        var course = new LearningCourse { TenantId = actor.TenantId, Code = code, VersionNumber = 1, Status = LearningCourseStatuses.Draft };
        Apply(course, input);
        db.LearningCourses.Add(course);
        await SaveUniqueAsync("course_code_taken", "A course with this code already exists.", ct);
        await audit.WriteAsync("learning.course.created", nameof(LearningCourse), course.Id.ToString(), actor.Request,
            JsonSerializer.Serialize(new { course.Code, course.VersionNumber }), ct);
        return await GetCourseAsync(actor.TenantId, course.Id, true, ct);
    }

    public async Task<CourseDetailDto> UpdateCourseAsync(LearningActor actor, Guid id, CourseInput input, CancellationToken ct)
    {
        var course = await db.LearningCourses.FirstOrDefaultAsync(c => c.TenantId == actor.TenantId && c.Id == id, ct)
            ?? throw LearningException.NotFound("Course");
        if (course.Status != LearningCourseStatuses.Draft)
            throw LearningException.Conflict("course_immutable",
                "A published course version cannot be changed. Create a new version, edit it, and publish that.");
        ExpectVersion(course.Version, input.Version);
        Apply(course, input);
        course.Version++;
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync("learning.course.updated", nameof(LearningCourse), course.Id.ToString(), actor.Request,
            JsonSerializer.Serialize(new { course.Code, course.VersionNumber }), ct);
        return await GetCourseAsync(actor.TenantId, course.Id, true, ct);
    }

    public async Task<CourseDetailDto> PublishCourseAsync(LearningActor actor, Guid id, int? expectedVersion, CancellationToken ct)
    {
        var course = await db.LearningCourses.FirstOrDefaultAsync(c => c.TenantId == actor.TenantId && c.Id == id, ct)
            ?? throw LearningException.NotFound("Course");
        if (course.Status != LearningCourseStatuses.Draft)
            throw LearningException.Conflict("course_not_draft", "Only a draft version can be published.");
        ExpectVersion(course.Version, expectedVersion);
        var problems = PublishProblems(course);
        if (problems.Count > 0)
            throw new LearningException(422, "course_incomplete", "This version is not ready to publish.", problems);

        var now = clock.GetUtcNow().UtcDateTime;
        var retired = new List<int>();
        // One transaction, two statements: the version it replaces stops being assignable (enrollments pinned to it
        // carry on, LRN-01), then this one is published. Retire first, or the one-Published-per-code index refuses it.
        await LearningDb.InTransactionAsync(db, async () =>
        {
            retired.Clear();
            var previous = await db.LearningCourses.Where(c => c.TenantId == actor.TenantId && c.Code == course.Code
                && c.Status == LearningCourseStatuses.Published).ToListAsync(ct);
            foreach (var old in previous)
            {
                old.Status = LearningCourseStatuses.Retired;
                old.RetiredAtUtc = now;
                old.Version++;
                retired.Add(old.VersionNumber);
            }
            if (previous.Count > 0) await db.SaveChangesAsync(ct);
            course.Status = LearningCourseStatuses.Published;
            course.PublishedAtUtc = now;
            course.PublishedByUserId = actor.UserId;
            course.Version++;
            await db.SaveChangesAsync(ct);
        }, ct);
        await audit.WriteAsync("learning.course.published", nameof(LearningCourse), course.Id.ToString(), actor.Request,
            JsonSerializer.Serialize(new { course.Code, course.VersionNumber, retired }), ct);
        return await GetCourseAsync(actor.TenantId, course.Id, true, ct);
    }

    public async Task<CourseDetailDto> NewVersionAsync(LearningActor actor, Guid id, CancellationToken ct)
    {
        var source = await db.LearningCourses.AsNoTracking().FirstOrDefaultAsync(c => c.TenantId == actor.TenantId && c.Id == id, ct)
            ?? throw LearningException.NotFound("Course");
        var draft = await db.LearningCourses.AsNoTracking().FirstOrDefaultAsync(c => c.TenantId == actor.TenantId && c.Code == source.Code
            && c.Status == LearningCourseStatuses.Draft, ct);
        if (draft is not null)
            throw LearningException.Conflict("draft_exists", "This course already has a draft version. Edit that one.", new { draftId = draft.Id });
        var next = await db.LearningCourses.Where(c => c.TenantId == actor.TenantId && c.Code == source.Code).MaxAsync(c => c.VersionNumber, ct) + 1;
        var copy = new LearningCourse
        {
            TenantId = actor.TenantId, Code = source.Code, VersionNumber = next, Status = LearningCourseStatuses.Draft,
            Title = source.Title, TitleAr = source.TitleAr, Description = source.Description, DescriptionAr = source.DescriptionAr,
            EstimatedMinutes = source.EstimatedMinutes, ContentKind = source.ContentKind, LessonHtml = source.LessonHtml, LinkUrl = source.LinkUrl,
            Cmi5AuUrl = source.Cmi5AuUrl, Cmi5ActivityId = source.Cmi5ActivityId, Cmi5MoveOn = source.Cmi5MoveOn, Cmi5AuOrigin = source.Cmi5AuOrigin,
            CompletionRule = source.CompletionRule, AssessmentJson = source.AssessmentJson, PassMarkPercent = source.PassMarkPercent,
            MaxAttempts = source.MaxAttempts, ShuffleQuestions = source.ShuffleQuestions, ValidityMonths = source.ValidityMonths,
            IssuesCertificate = source.IssuesCertificate, RequiresHrVerification = source.RequiresHrVerification,
        };
        db.LearningCourses.Add(copy);
        await SaveUniqueAsync("draft_exists", "This course already has a draft version. Edit that one.", ct);
        await audit.WriteAsync("learning.course.version_created", nameof(LearningCourse), copy.Id.ToString(), actor.Request,
            JsonSerializer.Serialize(new { copy.Code, copy.VersionNumber, fromVersion = source.VersionNumber }), ct);
        return await GetCourseAsync(actor.TenantId, copy.Id, true, ct);
    }

    public async Task<CourseDetailDto> RetireCourseAsync(LearningActor actor, Guid id, CancellationToken ct)
    {
        var course = await db.LearningCourses.FirstOrDefaultAsync(c => c.TenantId == actor.TenantId && c.Id == id, ct)
            ?? throw LearningException.NotFound("Course");
        if (course.Status != LearningCourseStatuses.Published)
            throw LearningException.Conflict("course_not_published", "Only the published version can be retired.");
        course.Status = LearningCourseStatuses.Retired;
        course.RetiredAtUtc = clock.GetUtcNow().UtcDateTime;
        course.Version++;
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync("learning.course.retired", nameof(LearningCourse), course.Id.ToString(), actor.Request,
            JsonSerializer.Serialize(new { course.Code, course.VersionNumber }), ct);
        return await GetCourseAsync(actor.TenantId, course.Id, true, ct);
    }

    private static void Apply(LearningCourse course, CourseInput input)
    {
        course.Title = (input.Title ?? string.Empty).Trim();
        if (course.Title.Length is 0 or > 200) throw LearningException.Invalid("Give the course a title (up to 200 characters).");
        course.TitleAr = Clean(input.TitleAr, 200);
        course.Description = Clean(input.Description, 2000);
        course.DescriptionAr = Clean(input.DescriptionAr, 2000);
        course.EstimatedMinutes = input.EstimatedMinutes is > 0 and <= 10_000 ? input.EstimatedMinutes : null;

        var kind = LearningContentKinds.All.FirstOrDefault(k => string.Equals(k, input.ContentKind, StringComparison.OrdinalIgnoreCase))
            ?? throw LearningException.Invalid("Choose the content type: lesson, link or cmi5 unit.");
        course.ContentKind = kind;
        course.LessonHtml = kind == LearningContentKinds.Lesson ? NullIfEmpty(LearningHtmlSanitizer.Sanitize(input.LessonHtml)) : null;
        course.LinkUrl = kind == LearningContentKinds.Link ? HttpUrl(input.LinkUrl, "The link must be a full http(s) address.") : null;
        if (kind == LearningContentKinds.Cmi5)
        {
            var au = HttpUrl(input.Cmi5AuUrl, "The cmi5 unit address must be a full http(s) address.");
            course.Cmi5AuUrl = au;
            course.Cmi5AuOrigin = au is null ? null : OriginOf(au);
            course.Cmi5ActivityId = Clean(input.Cmi5ActivityId, 500);
            if (course.Cmi5ActivityId is not null && !Uri.TryCreate(course.Cmi5ActivityId, UriKind.Absolute, out _))
                throw LearningException.Invalid("The cmi5 activity id must be an absolute IRI, e.g. https://example.com/au/fire-safety.");
            course.Cmi5MoveOn = Cmi5MoveOnValues.All.FirstOrDefault(m => string.Equals(m, input.Cmi5MoveOn, StringComparison.OrdinalIgnoreCase))
                ?? Cmi5MoveOnValues.Completed;
        }
        else
        {
            course.Cmi5AuUrl = course.Cmi5AuOrigin = course.Cmi5ActivityId = course.Cmi5MoveOn = null;
        }

        var rule = LearningCompletionRules.All.FirstOrDefault(r => string.Equals(r, input.CompletionRule, StringComparison.OrdinalIgnoreCase))
            ?? (kind == LearningContentKinds.Cmi5 ? LearningCompletionRules.Cmi5MoveOn : LearningCompletionRules.Acknowledgement);
        // §3.1: cmi5 completes by moveOn only; acknowledgement only for Lesson/Link with no assessment.
        if (kind == LearningContentKinds.Cmi5 && rule != LearningCompletionRules.Cmi5MoveOn)
            throw LearningException.Invalid("A cmi5 unit is completed by its moveOn rule; it cannot be acknowledged or carry a native assessment.");
        if (kind != LearningContentKinds.Cmi5 && rule == LearningCompletionRules.Cmi5MoveOn)
            throw LearningException.Invalid("Only a cmi5 unit can be completed by moveOn.");
        course.CompletionRule = rule;

        if (rule == LearningCompletionRules.Assessment)
        {
            var (bank, problems) = LearningAssessment.Build(input.Questions);
            if (bank is null) throw LearningException.Invalid("The assessment has problems.", problems);
            course.AssessmentJson = LearningAssessment.Serialize(bank);
            // A 0% pass mark would let an empty submission pass: the mark must be above 0.
            course.PassMarkPercent = input.PassMarkPercent is > 0 and <= 100
                ? Math.Round(input.PassMarkPercent.Value, 2) : throw LearningException.Invalid("Set a pass mark above 0% and up to 100%.");
            course.MaxAttempts = input.MaxAttempts is >= 1 and <= 20 ? input.MaxAttempts : throw LearningException.Invalid("Allow between 1 and 20 attempts.");
            course.ShuffleQuestions = input.ShuffleQuestions ?? false;
        }
        else
        {
            course.AssessmentJson = null;
            course.MaxAttempts = null;
            course.ShuffleQuestions = false;
            // Cmi5 with a moveOn that needs "passed": the pass mark is the unit's mastery score (LMS.LaunchData).
            course.PassMarkPercent = kind == LearningContentKinds.Cmi5 && UsesPassed(course.Cmi5MoveOn) && input.PassMarkPercent is decimal mastery
                ? (mastery is > 0 and <= 100 ? Math.Round(mastery, 2) : throw LearningException.Invalid("Set the unit's pass mark above 0% and up to 100%."))
                : null;
        }
        course.ValidityMonths = input.ValidityMonths is null or 0 ? null
            : input.ValidityMonths is >= 1 and <= 120 ? input.ValidityMonths : throw LearningException.Invalid("Validity must be between 1 and 120 months, or empty for no expiry.");
        course.IssuesCertificate = input.IssuesCertificate ?? true;
        // Content-reported completion (cmi5) that grants a certificate or a qualification waits for HR by default.
        course.RequiresHrVerification = kind == LearningContentKinds.Cmi5
            && (input.RequiresHrVerification ?? (course.IssuesCertificate || course.ValidityMonths is not null));
    }

    internal static bool UsesPassed(string? moveOn) =>
        moveOn is Cmi5MoveOnValues.Passed or Cmi5MoveOnValues.CompletedAndPassed or Cmi5MoveOnValues.CompletedOrPassed;

    internal static List<string> PublishProblems(LearningCourse c)
    {
        var problems = new List<string>();
        if (string.IsNullOrWhiteSpace(c.Title)) problems.Add("title_missing");
        if (c.ContentKind == LearningContentKinds.Lesson && string.IsNullOrWhiteSpace(c.LessonHtml)) problems.Add("lesson_content_missing");
        if (c.ContentKind == LearningContentKinds.Link && string.IsNullOrWhiteSpace(c.LinkUrl)) problems.Add("link_missing");
        if (c.ContentKind == LearningContentKinds.Cmi5 && (c.Cmi5AuUrl is null || c.Cmi5ActivityId is null || c.Cmi5MoveOn is null)) problems.Add("cmi5_unit_incomplete");
        if (c.CompletionRule == LearningCompletionRules.Assessment && LearningAssessment.Parse(c.AssessmentJson).Questions.Count == 0) problems.Add("assessment_empty");
        if (c.CompletionRule == LearningCompletionRules.Assessment && c.PassMarkPercent is not > 0) problems.Add("pass_mark_missing");
        // "Satisfied on launch" proves nothing, so it cannot grant a certificate or a qualification.
        if (c.ContentKind == LearningContentKinds.Cmi5 && c.Cmi5MoveOn == Cmi5MoveOnValues.NotApplicable && (c.IssuesCertificate || c.ValidityMonths is not null))
            problems.Add("move_on_not_applicable_with_certificate");
        if (c.ContentKind == LearningContentKinds.Cmi5 && UsesPassed(c.Cmi5MoveOn) && c.PassMarkPercent is not > 0) problems.Add("mastery_score_missing");
        return problems;
    }

    // ── Paths ───────────────────────────────────────────────────────────────────────────────────────────────────────

    public async Task<IReadOnlyList<PathDto>> ListPathsAsync(Guid tenantId, CancellationToken ct)
    {
        var paths = await db.LearningPaths.AsNoTracking().Where(p => p.TenantId == tenantId).OrderBy(p => p.Name).ToListAsync(ct);
        var dtos = new List<PathDto>();
        foreach (var p in paths) dtos.Add(await PathDtoAsync(p, ct));
        return dtos;
    }

    public async Task<PathDto> GetPathAsync(Guid tenantId, Guid id, CancellationToken ct)
    {
        var path = await db.LearningPaths.AsNoTracking().FirstOrDefaultAsync(p => p.TenantId == tenantId && p.Id == id, ct)
            ?? throw LearningException.NotFound("Learning path");
        return await PathDtoAsync(path, ct);
    }

    public async Task<PathDto> CreatePathAsync(LearningActor actor, PathInput input, CancellationToken ct)
    {
        var code = NormaliseCode(input.Code) ?? throw LearningException.Invalid("Give the path a code of 2–40 letters, digits or dashes.");
        if (await db.LearningPaths.AnyAsync(p => p.TenantId == actor.TenantId && p.Code == code, ct))
            throw LearningException.Conflict("path_code_taken", "A learning path with this code already exists.");
        var path = new LearningPath { TenantId = actor.TenantId, Code = code, Status = LearningPathStatuses.Draft };
        ApplyPathHeader(path, input);
        db.LearningPaths.Add(path);
        await ReplaceItemsAsync(path, input.Items ?? [], ct);
        await SaveUniqueAsync("path_code_taken", "A learning path with this code already exists.", ct);
        await audit.WriteAsync("learning.path.created", nameof(LearningPath), path.Id.ToString(), actor.Request,
            JsonSerializer.Serialize(new { path.Code, items = input.Items?.Count ?? 0 }), ct);
        return await GetPathAsync(actor.TenantId, path.Id, ct);
    }

    public async Task<PathDto> UpdatePathAsync(LearningActor actor, Guid id, PathInput input, CancellationToken ct)
    {
        var path = await db.LearningPaths.FirstOrDefaultAsync(p => p.TenantId == actor.TenantId && p.Id == id, ct)
            ?? throw LearningException.NotFound("Learning path");
        if (path.Status == LearningPathStatuses.Archived) throw LearningException.Conflict("path_archived", "An archived path cannot be changed.");
        ExpectVersion(path.Version, input.Version);
        ApplyPathHeader(path, input);
        if (input.Items is not null)
        {
            // Assignments are pinned to the items they were created from: an active path's structure is fixed.
            if (path.Status != LearningPathStatuses.Draft)
                throw LearningException.Conflict("path_items_locked",
                    "The courses in an active path cannot change, because people are already assigned to them. Create a new path instead.");
            await ReplaceItemsAsync(path, input.Items, ct);
        }
        path.Version++;
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync("learning.path.updated", nameof(LearningPath), path.Id.ToString(), actor.Request,
            JsonSerializer.Serialize(new { path.Code }), ct);
        return await GetPathAsync(actor.TenantId, path.Id, ct);
    }

    public async Task<PathDto> ActivatePathAsync(LearningActor actor, Guid id, CancellationToken ct)
    {
        var path = await db.LearningPaths.FirstOrDefaultAsync(p => p.TenantId == actor.TenantId && p.Id == id, ct)
            ?? throw LearningException.NotFound("Learning path");
        if (path.Status != LearningPathStatuses.Draft) throw LearningException.Conflict("path_not_draft", "Only a draft path can be activated.");
        var items = await db.LearningPathItems.AsNoTracking().Where(i => i.PathId == path.Id).ToListAsync(ct);
        if (!items.Any(i => i.IsRequired))
            throw new LearningException(422, "path_incomplete", "A path needs at least one required course before it can be activated.");
        var codes = await CourseCodesAsync(actor.TenantId, items.Select(i => i.CourseId), ct);
        var unpublished = new List<string>();
        foreach (var code in codes.Values.Distinct())
            if (!await db.LearningCourses.AnyAsync(c => c.TenantId == actor.TenantId && c.Code == code && c.Status == LearningCourseStatuses.Published, ct))
                unpublished.Add(code);
        if (unpublished.Count > 0)
            throw new LearningException(422, "path_course_unpublished", "Every course in the path must have a published version.", unpublished);
        path.Status = LearningPathStatuses.Active;
        path.ActivatedAtUtc = clock.GetUtcNow().UtcDateTime;
        path.Version++;
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync("learning.path.activated", nameof(LearningPath), path.Id.ToString(), actor.Request,
            JsonSerializer.Serialize(new { path.Code }), ct);
        return await GetPathAsync(actor.TenantId, path.Id, ct);
    }

    public async Task<PathDto> ArchivePathAsync(LearningActor actor, Guid id, CancellationToken ct)
    {
        var path = await db.LearningPaths.FirstOrDefaultAsync(p => p.TenantId == actor.TenantId && p.Id == id, ct)
            ?? throw LearningException.NotFound("Learning path");
        if (path.Status == LearningPathStatuses.Archived) return await GetPathAsync(actor.TenantId, id, ct);
        path.Status = LearningPathStatuses.Archived;
        path.Version++;
        // A standing rule for an archived path would keep assigning it.
        foreach (var rule in await db.LearningAssignmentRules.Where(r => r.TenantId == actor.TenantId && r.PathId == id && r.IsActive).ToListAsync(ct))
        {
            rule.IsActive = false;
            rule.Version++;
        }
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync("learning.path.archived", nameof(LearningPath), path.Id.ToString(), actor.Request,
            JsonSerializer.Serialize(new { path.Code }), ct);
        return await GetPathAsync(actor.TenantId, path.Id, ct);
    }

    private static void ApplyPathHeader(LearningPath path, PathInput input)
    {
        path.Name = (input.Name ?? string.Empty).Trim();
        if (path.Name.Length is 0 or > 200) throw LearningException.Invalid("Give the path a name (up to 200 characters).");
        path.NameAr = Clean(input.NameAr, 200);
        path.Description = Clean(input.Description, 2000);
        path.EligibilityJson = (input.Eligibility ?? new LearningEligibilityCriteria()).Serialize();
        if (input.EffectiveFrom is { } from && input.EffectiveTo is { } to && to < from)
            throw LearningException.Invalid("The path cannot end before it starts.");
        path.EffectiveFrom = input.EffectiveFrom;
        path.EffectiveTo = input.EffectiveTo;
        path.RenewalWindowDays = input.RenewalWindowDays is >= 0 and <= 365 ? input.RenewalWindowDays.Value : 30;
    }

    private async Task ReplaceItemsAsync(LearningPath path, IReadOnlyList<PathItemInput> items, CancellationToken ct)
    {
        if (items.Count > 50) throw LearningException.Invalid("A path can hold at most 50 courses.");
        var courseIds = items.Select(i => i.CourseId).ToList();
        var codes = await CourseCodesAsync(path.TenantId, courseIds, ct);
        if (codes.Count != courseIds.Distinct().Count()) throw LearningException.Invalid("One of the courses does not exist.");
        if (codes.Values.Count() != codes.Values.Distinct().Count() || courseIds.Count != courseIds.Distinct().Count())
            throw LearningException.Invalid("A course can appear in a path only once.");

        db.LearningPathItems.RemoveRange(await db.LearningPathItems.Where(i => i.PathId == path.Id).ToListAsync(ct));
        var created = new List<LearningPathItem>();
        for (var index = 0; index < items.Count; index++)
        {
            var input = items[index];
            var item = new LearningPathItem
            {
                TenantId = path.TenantId, PathId = path.Id, CourseId = input.CourseId, Sequence = index + 1, IsRequired = input.IsRequired ?? true,
            };
            if (input.PrerequisiteSequence is int pre)
            {
                // A prerequisite is an EARLIER, REQUIRED item: an optional or later one could never unblock this item.
                if (pre < 1 || pre > index) throw LearningException.Invalid($"Course {index + 1}: a prerequisite must be an earlier course in the path.");
                var prerequisite = created[pre - 1];
                if (!prerequisite.IsRequired) throw LearningException.Invalid($"Course {index + 1}: a prerequisite must be a required course.");
                item.PrerequisiteItemId = prerequisite.Id;
            }
            created.Add(item);
        }
        db.LearningPathItems.AddRange(created);
    }

    private async Task<PathDto> PathDtoAsync(LearningPath p, CancellationToken ct)
    {
        var items = await db.LearningPathItems.AsNoTracking().Where(i => i.PathId == p.Id).OrderBy(i => i.Sequence).ToListAsync(ct);
        var courseIds = items.Select(i => i.CourseId).ToList();
        var courses = await db.LearningCourses.AsNoTracking().Where(c => c.TenantId == p.TenantId && courseIds.Contains(c.Id)).ToDictionaryAsync(c => c.Id, ct);
        var codes = courses.Values.Select(c => c.Code).Distinct().ToList();
        var published = await db.LearningCourses.AsNoTracking()
            .Where(c => c.TenantId == p.TenantId && codes.Contains(c.Code) && c.Status == LearningCourseStatuses.Published)
            .ToDictionaryAsync(c => c.Code, ct);
        var sequenceById = items.ToDictionary(i => i.Id, i => i.Sequence);
        var dtoItems = items.Select(i =>
        {
            var course = courses[i.CourseId];
            var current = published.GetValueOrDefault(course.Code) ?? course;
            return new PathItemDto(i.Id, i.Sequence, i.CourseId, course.Code, current.Title, current.TitleAr, current.Status,
                published.ContainsKey(course.Code), i.IsRequired,
                i.PrerequisiteItemId is Guid pre && sequenceById.TryGetValue(pre, out var s) ? s : null);
        }).ToList();
        var live = await db.LearningAssignments.AsNoTracking().CountAsync(a => a.TenantId == p.TenantId && a.PathId == p.Id
            && LearningAssignmentStatuses.Active.Contains(a.Status), ct);
        var completed = await db.LearningAssignments.AsNoTracking().CountAsync(a => a.TenantId == p.TenantId && a.PathId == p.Id
            && a.Status == LearningAssignmentStatuses.Completed, ct);
        return new PathDto(p.Id, p.Code, p.Name, p.NameAr, p.Description, p.Status, LearningEligibilityCriteria.Parse(p.EligibilityJson),
            p.EffectiveFrom, p.EffectiveTo, p.RenewalWindowDays, dtoItems, live, completed, p.UpdatedAtUtc, p.Version);
    }

    // ── Rules ───────────────────────────────────────────────────────────────────────────────────────────────────────

    public async Task<IReadOnlyList<RuleDto>> ListRulesAsync(Guid tenantId, CancellationToken ct)
    {
        var rules = await db.LearningAssignmentRules.AsNoTracking().Where(r => r.TenantId == tenantId)
            .OrderByDescending(r => r.IsActive).ThenBy(r => r.Name).ToListAsync(ct);
        var pathIds = rules.Select(r => r.PathId).Distinct().ToList();
        var paths = await db.LearningPaths.AsNoTracking().Where(p => p.TenantId == tenantId && pathIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id, p => p.Name, ct);
        var names = await TargetNamesAsync(tenantId, rules.Select(r => (r.TargetKind, r.TargetId)), ct);
        return rules.Select(r => new RuleDto(r.Id, r.PathId, paths.GetValueOrDefault(r.PathId, ""), r.Name, r.TargetKind, r.TargetId,
            names.GetValueOrDefault((r.TargetKind, r.TargetId), ""), r.DueInDays, r.IsMandatory, r.IsActive, r.LastAppliedAtUtc, r.CreatedAtUtc)).ToList();
    }

    /// <summary>Saves a rule and applies it at once to everyone it matches today; the sweep applies it to later matches.</summary>
    public async Task<(RuleDto Rule, AssignmentBulkResult Applied)> CreateRuleAsync(LearningActor actor, RuleInput input, CancellationToken ct)
    {
        var path = await db.LearningPaths.AsNoTracking().FirstOrDefaultAsync(p => p.TenantId == actor.TenantId && p.Id == input.PathId, ct)
            ?? throw LearningException.NotFound("Learning path");
        if (path.Status != LearningPathStatuses.Active) throw LearningException.Conflict("path_not_active", "Activate the path before adding a rule for it.");
        var kind = LearningRuleTargets.All.FirstOrDefault(k => string.Equals(k, input.TargetKind, StringComparison.OrdinalIgnoreCase))
            ?? throw LearningException.Invalid("Choose who the rule targets: company, department, designation or grade.");
        var names = await TargetNamesAsync(actor.TenantId, [(kind, input.TargetId)], ct);
        if (!names.ContainsKey((kind, input.TargetId))) throw LearningException.Invalid("The target of the rule does not exist.");
        var rule = new LearningAssignmentRule
        {
            TenantId = actor.TenantId, PathId = path.Id, TargetKind = kind, TargetId = input.TargetId,
            Name = Clean(input.Name, 200) ?? $"{path.Name} — {names[(kind, input.TargetId)]}",
            DueInDays = input.DueInDays is >= 0 and <= 3650 ? input.DueInDays.Value : 30,
            IsMandatory = input.IsMandatory ?? true,
        };
        db.LearningAssignmentRules.Add(rule);
        await SaveUniqueAsync("rule_exists", "An active rule already assigns this path to this target.", ct);
        await audit.WriteAsync("learning.rule.created", nameof(LearningAssignmentRule), rule.Id.ToString(), actor.Request,
            JsonSerializer.Serialize(new { rule.PathId, rule.TargetKind, rule.TargetId, rule.DueInDays }), ct);
        var applied = await assignments.ApplyRuleAsync(actor, rule.Id, ct);
        var dto = (await ListRulesAsync(actor.TenantId, ct)).First(r => r.Id == rule.Id);
        return (dto, applied);
    }

    public async Task<RuleDto> DeactivateRuleAsync(LearningActor actor, Guid id, CancellationToken ct)
    {
        var rule = await db.LearningAssignmentRules.FirstOrDefaultAsync(r => r.TenantId == actor.TenantId && r.Id == id, ct)
            ?? throw LearningException.NotFound("Rule");
        if (rule.IsActive)
        {
            rule.IsActive = false;
            rule.Version++;
            await db.SaveChangesAsync(ct);
            await audit.WriteAsync("learning.rule.deactivated", nameof(LearningAssignmentRule), rule.Id.ToString(), actor.Request, null, ct);
        }
        return (await ListRulesAsync(actor.TenantId, ct)).First(r => r.Id == rule.Id);
    }

    internal async Task<Dictionary<(string, Guid), string>> TargetNamesAsync(Guid tenantId, IEnumerable<(string Kind, Guid Id)> targets, CancellationToken ct)
    {
        var list = targets.ToList();
        var result = new Dictionary<(string, Guid), string>();
        var ids = list.Select(t => t.Id).Distinct().ToList();
        if (list.Any(t => t.Kind == LearningRuleTargets.Company))
            foreach (var c in await db.Companies.AsNoTracking().Where(c => c.TenantId == tenantId && ids.Contains(c.Id)).Select(c => new { c.Id, c.LegalNameEn }).ToListAsync(ct))
                result[(LearningRuleTargets.Company, c.Id)] = c.LegalNameEn;
        if (list.Any(t => t.Kind == LearningRuleTargets.Department))
            foreach (var d in await db.Departments.AsNoTracking().Where(d => d.TenantId == tenantId && ids.Contains(d.Id)).Select(d => new { d.Id, d.NameEn }).ToListAsync(ct))
                result[(LearningRuleTargets.Department, d.Id)] = d.NameEn;
        if (list.Any(t => t.Kind == LearningRuleTargets.Designation))
            foreach (var d in await db.Designations.AsNoTracking().Where(d => d.TenantId == tenantId && ids.Contains(d.Id)).Select(d => new { d.Id, d.TitleEn }).ToListAsync(ct))
                result[(LearningRuleTargets.Designation, d.Id)] = d.TitleEn;
        if (list.Any(t => t.Kind == LearningRuleTargets.Grade))
            foreach (var g in await db.Grades.AsNoTracking().Where(g => g.TenantId == tenantId && ids.Contains(g.Id)).Select(g => new { g.Id, g.Name }).ToListAsync(ct))
                result[(LearningRuleTargets.Grade, g.Id)] = g.Name;
        return result;
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────────────────────────────────────────

    private async Task<Dictionary<Guid, string>> CourseCodesAsync(Guid tenantId, IEnumerable<Guid> courseIds, CancellationToken ct)
    {
        var ids = courseIds.Distinct().ToList();
        return await db.LearningCourses.AsNoTracking().Where(c => c.TenantId == tenantId && ids.Contains(c.Id)).ToDictionaryAsync(c => c.Id, c => c.Code, ct);
    }

    private async Task SaveUniqueAsync(string code, string message, CancellationToken ct)
    {
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException ex) when (LearningDb.IsUniqueViolation(ex))
        {
            LearningDb.DetachAdded(db);
            throw LearningException.Conflict(code, message);
        }
    }

    private static void ExpectVersion(int actual, int? expected)
    {
        if (expected is int e && e != actual)
            throw LearningException.Conflict("concurrent_change", "Someone else changed this record at the same time. Reload it and try again.");
    }

    internal static string? NormaliseCode(string? code)
    {
        var value = (code ?? string.Empty).Trim().ToUpperInvariant();
        return value.Length is >= 2 and <= 40 && value.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_') ? value : null;
    }

    private static string? Clean(string? value, int max)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var trimmed = value.Trim();
        return trimmed.Length <= max ? trimmed : throw LearningException.Invalid($"A text field is longer than {max} characters.");
    }

    private static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    private static string? HttpUrl(string? value, string error)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        return Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp)
            && uri.AbsoluteUri.Length <= 2000
            ? uri.AbsoluteUri
            : throw LearningException.Invalid(error);
    }

    internal static string OriginOf(string url)
    {
        var uri = new Uri(url);
        return uri.IsDefaultPort ? $"{uri.Scheme}://{uri.Host}" : $"{uri.Scheme}://{uri.Host}:{uri.Port}";
    }

    internal static CourseSummaryDto Summary(LearningCourse c) => new(c.Id, c.Code, c.VersionNumber, c.Status, c.Title, c.TitleAr, c.ContentKind,
        c.CompletionRule, c.ValidityMonths, c.IssuesCertificate, LearningAssessment.Parse(c.AssessmentJson).Questions.Count, c.PublishedAtUtc,
        c.UpdatedAtUtc, c.Version);

    private static CourseDetailDto Detail(LearningCourse c, IReadOnlyList<LearningCourse> versions, bool includeAnswerKey)
    {
        var bank = LearningAssessment.Parse(c.AssessmentJson);
        return new CourseDetailDto(c.Id, c.Code, c.VersionNumber, c.Status, c.Title, c.TitleAr, c.Description, c.DescriptionAr, c.EstimatedMinutes,
            c.ContentKind, c.LessonHtml, c.LinkUrl, c.Cmi5AuUrl, c.Cmi5ActivityId, c.Cmi5MoveOn, c.CompletionRule, c.PassMarkPercent, c.MaxAttempts,
            c.ShuffleQuestions, c.ValidityMonths, c.IssuesCertificate, bank.Questions.Count,
            includeAnswerKey ? LearningAssessment.ForAuthor(bank) : null,
            versions.Select(Summary).ToList(), c.PublishedAtUtc, c.UpdatedAtUtc, c.Version, c.RequiresHrVerification);
    }
}
