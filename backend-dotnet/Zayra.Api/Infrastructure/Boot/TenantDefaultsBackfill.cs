using Microsoft.EntityFrameworkCore;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Documents.Letters;
using Zayra.Api.Infrastructure.Seed;

namespace Zayra.Api.Infrastructure.Boot;

/// <summary>
/// Installs the defaults that two recently-shipped modules need, on tenants that ALREADY EXIST.
///
/// <para><b>The gap this closes.</b> HR letter templates and the timesheet approval route are both
/// provisioned only on the new-tenant path (<see cref="TenantProvisioningBundle"/>, and the
/// <c>POST /api/hr-letters/templates/seed-defaults</c> admin action). Every tenant created before
/// those modules existed — which is all of them — therefore came up without them, and the modules
/// look shipped while being unusable: <c>/api/hr-letters/types</c> reports <c>isConfigured:false</c>
/// for every type so the "Issue a Letter" tab has nothing to issue from and ESS offers an empty
/// "Request a Document" dropdown, and the first timesheet anyone submits 422s with
/// <c>no_approval_route</c>. Neither is a data-quality problem the client can see coming, and
/// neither is discoverable from the screen that fails.</para>
///
/// <para><b>Why a startup backfill and not a migration.</b> A migration runs once per database and
/// cannot cover a tenant created afterwards, and it would have to restate five bilingual letter
/// templates and a parent/child workflow graph as raw SQL — a second copy of content whose source
/// of truth is <see cref="HrLetterTemplateDefaults"/> and
/// <c>TenantProvisioningBundle.ApprovalDefaults</c>, free to drift from it. This runs on every
/// boot, covers tenants created before AND after it shipped, and reads the same C# defaults every
/// other install path reads. It adds no schema, so there is no migration to roll back.</para>
///
/// <para><b>The third gap (leave day-counting).</b> A tenant with no LeavePolicy row for a leave
/// type has no recorded day-count BASIS for it, and no fallback can be right for every type at
/// once: annual leave is counted in WORKING days, while KSA Art. 117 sick leave is counted in
/// CALENDAR days ("whether such leaves are continuous or intermittent"). Counting working days
/// bands a 120-day statutory sick entitlement as roughly 86; counting calendar days charges an
/// employee for the weekend inside an annual-leave span. Every tenant created before the default
/// policy set existed has at most a country-neutral ANNUAL policy and no SICK policy at all, so
/// this pass installs the same defaults <see cref="TenantProvisioningBundle"/> gives a new tenant —
/// ordinary editable rows, keyed on (leave type, tenant-wide scope, country).</para>
///
/// <para><b>Why not simply run <see cref="TenantProvisioningBundle.ProvisionAsync"/> for everyone.</b>
/// That bundle also installs ~17 pay components, MasterData, HR request categories, compliance
/// profiles and notification templates. Long-lived tenants were never provisioned through it, so
/// running it across all of them would be a large, unreviewed data change made in order to fix
/// three named gaps. This installs only what the three broken surfaces require.</para>
///
/// <para><b>Idempotency and the no-clobber guarantee.</b> Strictly insert-if-absent, keyed on the
/// natural key, exactly as the bundle's own contract requires. It NEVER updates, reactivates or
/// resets an existing row, so a letter template a client has edited, deactivated or re-worded
/// survives every subsequent deploy. This is the property that matters most: the failure mode being
/// avoided is not "the defaults are missing" but "the client's wording reverted on Tuesday's
/// deploy".</para>
///
/// <para>Non-fatal per tenant: one tenant's failure is logged and the pass continues, so a single
/// bad row cannot deprive every other tenant of its defaults. Kill switch:
/// <c>TenantDefaults:Backfill=false</c> / <c>TenantDefaults__Backfill=false</c>.</para>
/// </summary>
public static class TenantDefaultsBackfill
{
    public readonly record struct BackfillSummary(
        int TenantsVisited,
        int LetterTemplatesAdded,
        int ApprovalWorkflowsAdded,
        int TenantsFailed,
        int LeaveTypesAdded = 0,
        int LeavePoliciesAdded = 0);

    public static async Task<BackfillSummary> RunAsync(
        ZayraDbContext db, ILogger logger, CancellationToken ct = default)
    {
        var tenants = await db.Tenants.AsNoTracking()
            .Where(t => t.IsActive)
            .Select(t => new { t.Id, t.Name })
            .ToListAsync(ct);

        var visited = 0;
        var templatesAdded = 0;
        var workflowsAdded = 0;
        var leaveTypesAdded = 0;
        var leavePoliciesAdded = 0;
        var failed = 0;

        foreach (var tenant in tenants)
        {
            try
            {
                var templates = await TenantProvisioningBundle
                    .InstallDefaultLetterTemplatesAsync(db, tenant.Id, ct);
                var workflows = await TenantProvisioningBundle
                    .InstallDefaultApprovalWorkflowsAsync(db, tenant.Id, ct);
                // Types as well as policies: a policy needs a leave type to point at, and a tenant
                // that predates the leave module has neither. Same insert-if-absent installer the
                // new-tenant path runs, so the two cannot drift.
                var (leaveTypes, leavePolicies) = await TenantProvisioningBundle
                    .InstallDefaultLeaveAsync(db, tenant.Id, ct);

                // One save per tenant. No explicit transaction: NpgsqlRetryingExecutionStrategy
                // forbids a bare BeginTransactionAsync, and SaveChangesAsync already runs inside
                // the strategy's own retryable unit. A retry re-executes this SaveChanges with the
                // same tracked entities, never the read above, so it cannot double-insert.
                if (templates > 0 || workflows > 0 || leaveTypes > 0 || leavePolicies > 0)
                {
                    await db.SaveChangesAsync(ct);
                    logger.LogInformation(
                        "TenantDefaultsBackfill: tenant {TenantId} ({Name}) — installed {Templates} "
                        + "letter template(s), {Workflows} approval workflow(s), {LeaveTypes} leave "
                        + "type(s) and {LeavePolicies} leave policy default(s).",
                        tenant.Id, tenant.Name, templates, workflows, leaveTypes, leavePolicies);
                }

                templatesAdded += templates;
                workflowsAdded += workflows;
                leaveTypesAdded += leaveTypes;
                leavePoliciesAdded += leavePolicies;
                visited++;
            }
            catch (Exception ex)
            {
                failed++;
                logger.LogError(ex,
                    "TenantDefaultsBackfill: tenant {TenantId} ({Name}) failed — continuing with the "
                    + "next tenant.", tenant.Id, tenant.Name);
                // Drop this tenant's half-built graph, or the next tenant's SaveChanges re-flushes
                // it and fails too (the cascade-poisoning the startup seed chain also guards).
                db.ChangeTracker.Clear();
            }
        }

        if (templatesAdded == 0 && workflowsAdded == 0 && leaveTypesAdded == 0 && leavePoliciesAdded == 0)
            logger.LogInformation(
                "TenantDefaultsBackfill: {Tenants} tenant(s) checked, nothing to install.", visited);
        else
            logger.LogInformation(
                "TenantDefaultsBackfill: {Tenants} tenant(s) checked — {Templates} letter template(s), "
                + "{Workflows} approval workflow(s), {LeaveTypes} leave type(s), {LeavePolicies} leave "
                + "policy default(s) installed, {Failed} tenant(s) failed.",
                visited, templatesAdded, workflowsAdded, leaveTypesAdded, leavePoliciesAdded, failed);

        return new BackfillSummary(
            visited, templatesAdded, workflowsAdded, failed, leaveTypesAdded, leavePoliciesAdded);
    }
}
