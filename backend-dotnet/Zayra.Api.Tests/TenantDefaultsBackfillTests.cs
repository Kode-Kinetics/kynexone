using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.Boot;
using Zayra.Api.Infrastructure.Documents.Letters;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// <c>TenantDefaultsBackfill</c> — the pass that gives an ALREADY-EXISTING tenant the defaults that
/// two recently-shipped modules only ever installed on the new-tenant path.
///
/// <para>The behaviour under test is not "the defaults arrive" — that is the easy half. It is that
/// the pass can run on every boot, for ever, against live client data without ever undoing
/// something the client did. Three of the five tests below are about that, because the incident
/// this mechanism could cause is worse than the one it fixes: a client who re-words their salary
/// certificate and finds the wording reverted after the next deploy has been given a reason to
/// distrust every other thing they have configured.</para>
///
/// <para>Runs against real Postgres (shared <see cref="PostgresFixture"/>) rather than in-memory:
/// the letter-template gap check depends on <c>ux_hr_letter_templates_scope_type</c>, a UNIQUE
/// index with a <c>WHERE is_deleted = false</c> partial filter, and an in-memory provider models
/// neither the uniqueness nor the partiality.</para>
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration")]
public class TenantDefaultsBackfillTests
{
    private readonly PostgresFixture _fx;
    public TenantDefaultsBackfillTests(PostgresFixture fx) => _fx = fx;

    private static async Task<Tenant> NewTenantAsync(Zayra.Api.Data.ZayraDbContext db, string prefix)
    {
        var tenant = new Tenant
        {
            Id = Guid.NewGuid(),
            Name = $"{prefix} Co",
            Slug = $"{prefix}-{Guid.NewGuid():N}",
            IsActive = true,
        };
        db.Tenants.Add(tenant);
        await db.SaveChangesAsync();
        return tenant;
    }

    private static Task<TenantDefaultsBackfill.BackfillSummary> RunAsync(Zayra.Api.Data.ZayraDbContext db)
        => TenantDefaultsBackfill.RunAsync(db, NullLogger.Instance, CancellationToken.None);

    // ── 1. The gap actually closes ────────────────────────────────────────────────────────────

    /// <summary>
    /// A tenant in the state every real one was in: it exists, it has a leave workflow from its
    /// original seeding, and it has neither letter templates nor a timesheet approval route. This
    /// is the exact shape IntelliFlowDemoSeeder and CleanDemoKsaSeeder leave behind, and the shape
    /// that makes /api/hr-letters/types return isConfigured:false for everything and the first
    /// POST /submit answer 422 no_approval_route.
    /// </summary>
    [Fact]
    public async Task Backfill_InstallsLetterTemplatesAndATimesheetRoute_OnATenantThatPredatesThem()
    {
        await using var db = _fx.CreateDb();
        var tenant = await NewTenantAsync(db, "predates");

        // The demo seeders' shape: a leave workflow and nothing else.
        var leave = new ApprovalWorkflow
        {
            TenantId = tenant.Id, Code = "LEAVE-APPROVAL", Name = "Leave Approval",
            EntityName = nameof(LeaveRequest), IsDefault = true, IsActive = true,
        };
        leave.Steps.Add(new ApprovalWorkflowStep
        {
            TenantId = tenant.Id, WorkflowId = leave.Id, StepOrder = 1,
            StepName = "HR Approval", ApproverRole = "HR Manager", IsFinalStep = true,
        });
        db.ApprovalWorkflows.Add(leave);
        await db.SaveChangesAsync();

        var summary = await RunAsync(db);

        summary.TenantsFailed.Should().Be(0);

        // Every letter type in the catalogue now has a tenant-wide template, and it is ACTIVE and
        // not deleted — the three conditions GET /api/hr-letters/types tests for isConfigured.
        var templates = await db.HrLetterTemplates.IgnoreQueryFilters()
            .Where(t => t.TenantId == tenant.Id).ToListAsync();
        templates.Select(t => t.LetterType).Should()
            .BeEquivalentTo(HrLetterTemplateDefaults.Build().Select(t => t.LetterType));
        templates.Should().OnlyContain(t => t.IsActive && !t.IsDeleted && t.CompanyId == null);
        // Bilingual, not an English stub: the Arabic name is what the browser gate asserts.
        templates.Should().OnlyContain(t => t.NameAr != "" && t.BodyAr != "");

        // The timesheet route exists and terminates, so the approval router can resolve it.
        var timesheet = await db.ApprovalWorkflows.IgnoreQueryFilters()
            .Include(w => w.Steps)
            .SingleAsync(w => w.TenantId == tenant.Id && w.EntityName == "Timesheet");
        timesheet.IsActive.Should().BeTrue();
        timesheet.Steps.Should().Contain(s => s.IsFinalStep);

        // The tenant's OWN leave workflow is left exactly as it was — the backfill recognised that
        // LeaveRequest was already covered and did not plant a competing LEAVE-DEFAULT beside it.
        (await db.ApprovalWorkflows.IgnoreQueryFilters()
                .CountAsync(w => w.TenantId == tenant.Id && w.EntityName == nameof(LeaveRequest)))
            .Should().Be(1);
        (await db.ApprovalWorkflows.IgnoreQueryFilters()
                .SingleAsync(w => w.TenantId == tenant.Id && w.EntityName == nameof(LeaveRequest)))
            .Code.Should().Be("LEAVE-APPROVAL");
    }

    // ── 2. Idempotent under repetition ────────────────────────────────────────────────────────

    /// <summary>
    /// The pass runs on EVERY boot, so "installs nothing the second time" is not a nicety — a pass
    /// that inserted again would violate ux_hr_letter_templates_scope_type on the second deploy and
    /// take the whole startup seed chain down with it.
    /// </summary>
    [Fact]
    public async Task Backfill_IsIdempotent_ASecondAndThirdRunInstallNothing()
    {
        await using var db = _fx.CreateDb();
        var tenant = await NewTenantAsync(db, "idem");

        var first = await RunAsync(db);
        first.LetterTemplatesAdded.Should().BeGreaterThan(0);
        first.ApprovalWorkflowsAdded.Should().BeGreaterThan(0);

        var templatesAfterFirst = await db.HrLetterTemplates.IgnoreQueryFilters()
            .CountAsync(t => t.TenantId == tenant.Id);
        var workflowsAfterFirst = await db.ApprovalWorkflows.IgnoreQueryFilters()
            .CountAsync(w => w.TenantId == tenant.Id);

        var second = await RunAsync(db);
        var third = await RunAsync(db);

        // Not merely "no error" — nothing was written at all.
        second.LetterTemplatesAdded.Should().Be(0);
        second.ApprovalWorkflowsAdded.Should().Be(0);
        second.TenantsFailed.Should().Be(0);
        third.LetterTemplatesAdded.Should().Be(0);
        third.ApprovalWorkflowsAdded.Should().Be(0);
        third.TenantsFailed.Should().Be(0);

        (await db.HrLetterTemplates.IgnoreQueryFilters().CountAsync(t => t.TenantId == tenant.Id))
            .Should().Be(templatesAfterFirst);
        (await db.ApprovalWorkflows.IgnoreQueryFilters().CountAsync(w => w.TenantId == tenant.Id))
            .Should().Be(workflowsAfterFirst);
    }

    // ── 3. THE ONE THAT PROTECTS A LIVE CLIENT ────────────────────────────────────────────────

    /// <summary>
    /// The property the whole design turns on. A client edits the wording of their salary
    /// certificate — the thing a bank reads — and a later deploy must not put the stock wording
    /// back. Also covers a template the client DEACTIVATED, which a naive "restore the defaults"
    /// pass would silently switch back on.
    /// </summary>
    [Fact]
    public async Task Backfill_NeverRevertsATenantsOwnEdits()
    {
        await using var db = _fx.CreateDb();
        var tenant = await NewTenantAsync(db, "noclobber");

        await RunAsync(db);

        var salary = await db.HrLetterTemplates.IgnoreQueryFilters()
            .SingleAsync(t => t.TenantId == tenant.Id && t.LetterType == HrLetterTypes.SalaryCertificate);

        const string clientWording =
            "We hereby confirm that {{employee_name}} has served with {{company_name}} since {{joining_date}}.";
        salary.BodyEn = clientWording;
        salary.NameEn = "Salary Confirmation (Evostel wording)";
        salary.Version = 7;
        await db.SaveChangesAsync();

        // A second template the client switched off deliberately.
        var deactivated = await db.HrLetterTemplates.IgnoreQueryFilters()
            .FirstAsync(t => t.TenantId == tenant.Id && t.LetterType != HrLetterTypes.SalaryCertificate);
        var deactivatedType = deactivated.LetterType;
        deactivated.IsActive = false;
        await db.SaveChangesAsync();

        // Deploy, deploy again.
        var again = await RunAsync(db);
        var andAgain = await RunAsync(db);
        again.LetterTemplatesAdded.Should().Be(0);
        andAgain.LetterTemplatesAdded.Should().Be(0);

        db.ChangeTracker.Clear();

        var reread = await db.HrLetterTemplates.IgnoreQueryFilters()
            .SingleAsync(t => t.TenantId == tenant.Id && t.LetterType == HrLetterTypes.SalaryCertificate);
        reread.BodyEn.Should().Be(clientWording, "a deploy must never restore the stock wording over a client's own");
        reread.NameEn.Should().Be("Salary Confirmation (Evostel wording)");
        reread.Version.Should().Be(7, "the row was not rewritten, so its version did not move");

        var rereadDeactivated = await db.HrLetterTemplates.IgnoreQueryFilters()
            .SingleAsync(t => t.TenantId == tenant.Id && t.LetterType == deactivatedType);
        rereadDeactivated.IsActive.Should().BeFalse("a template the client switched off stays off");

        // And no duplicate was planted alongside either of them.
        (await db.HrLetterTemplates.IgnoreQueryFilters()
                .CountAsync(t => t.TenantId == tenant.Id && t.LetterType == HrLetterTypes.SalaryCertificate))
            .Should().Be(1);
    }

    /// <summary>
    /// A removed template is a decision, not a gap. The unattended pass must not resurrect it;
    /// the explicit admin action (POST /api/hr-letters/templates/seed-defaults) still can.
    /// </summary>
    [Fact]
    public async Task Backfill_DoesNotResurrectATemplateTheTenantRemoved()
    {
        await using var db = _fx.CreateDb();
        var tenant = await NewTenantAsync(db, "removed");

        await RunAsync(db);

        var removed = await db.HrLetterTemplates.IgnoreQueryFilters()
            .SingleAsync(t => t.TenantId == tenant.Id && t.LetterType == HrLetterTypes.SalaryCertificate);
        removed.IsDeleted = true;
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var again = await RunAsync(db);

        again.LetterTemplatesAdded.Should().Be(0);
        (await db.HrLetterTemplates.IgnoreQueryFilters()
                .CountAsync(t => t.TenantId == tenant.Id
                                 && t.LetterType == HrLetterTypes.SalaryCertificate
                                 && !t.IsDeleted))
            .Should().Be(0, "the removal stands until an administrator explicitly restores the defaults");
    }

    // ── 5. Scope ──────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The pass walks every active tenant, so the one thing it must never do is let one tenant's
    /// state decide another's — or write into a tenant that has been deactivated.
    /// </summary>
    [Fact]
    public async Task Backfill_CoversEveryActiveTenant_AndSkipsInactiveOnes()
    {
        await using var db = _fx.CreateDb();
        var active = await NewTenantAsync(db, "active");
        var alsoActive = await NewTenantAsync(db, "active2");
        var inactive = await NewTenantAsync(db, "inactive");
        inactive.IsActive = false;
        await db.SaveChangesAsync();

        await RunAsync(db);

        foreach (var id in new[] { active.Id, alsoActive.Id })
        {
            (await db.HrLetterTemplates.IgnoreQueryFilters().CountAsync(t => t.TenantId == id))
                .Should().Be(HrLetterTemplateDefaults.Build().Count);
            (await db.ApprovalWorkflows.IgnoreQueryFilters()
                    .AnyAsync(w => w.TenantId == id && w.EntityName == "Timesheet"))
                .Should().BeTrue();
        }

        (await db.HrLetterTemplates.IgnoreQueryFilters().CountAsync(t => t.TenantId == inactive.Id))
            .Should().Be(0, "a deactivated tenant is not provisioned into");
        (await db.ApprovalWorkflows.IgnoreQueryFilters().CountAsync(w => w.TenantId == inactive.Id))
            .Should().Be(0);
    }
}
