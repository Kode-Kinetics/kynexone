using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Auth;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.Auth;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Seed;

public class AuthSeeder : IAuthSeeder
{
    private readonly ZayraDbContext _db;

    public AuthSeeder(ZayraDbContext db)
    {
        _db = db;
    }

    /// <summary>
    /// Boot-time seeding of the GLOBAL permission catalogue only. It creates no tenant, company,
    /// user or business data: every tenant and user is created by the platform admin
    /// (see docs/DATA_ENTRY_PATHS.md). Per-tenant roles are installed by
    /// <see cref="EnsureTenantRolesAsync"/> when the platform admin provisions a tenant.
    /// The schema is owned by EF migrations; this never calls EnsureCreated.
    /// </summary>
    public async Task SeedAsync(CancellationToken cancellationToken = default)
    {
        await EnsurePermissions(cancellationToken);

        // Backfill EVERY tenant's Admin role with all permissions in a SINGLE set-based statement.
        // The previous implementation looped per-tenant-then-per-role with one query each — on a
        // database with many tenants that became thousands of sequential round-trips and made startup
        // take many minutes, so the app never finished booting (health check timed out → the whole
        // service stayed offline). This achieves the identical result in ONE round-trip regardless of
        // tenant count. Raw SQL intentionally bypasses the global tenant query filter.
        try
        {
            await _db.Database.ExecuteSqlRawAsync(
                @"INSERT INTO role_permissions (role_id, permission_id)
                  SELECT r.id, p.id
                  FROM roles r
                  CROSS JOIN permissions p
                  WHERE r.name = 'Admin'
                    AND NOT EXISTS (
                        SELECT 1 FROM role_permissions rp
                        WHERE rp.role_id = r.id AND rp.permission_id = p.id)
                  ON CONFLICT DO NOTHING;", cancellationToken);
        }
        catch (Exception ex) { Console.WriteLine($"[Seed] Admin permission backfill skipped: {ex.Message}"); }

        // PRIVILEGE-ESCALATION-BY-RESTART (removed). This block used to run, tenant-wide on every
        // boot:
        //
        //     UPDATE users SET is_group_scope = TRUE
        //     WHERE is_group_scope = FALSE AND <has Admin role> AND <has no active entity grant>
        //
        // The stated intent was a ONE-TIME repair: tenant admins created before the entity-scope
        // rollout had the Admin role but neither group scope nor company grants, which resolves to
        // entity_scope=none (EntityScopeContext.cs:183) and blocks even creating an employee.
        // Repairing that once is reasonable. Re-asserting it forever is not, because "Admin role,
        // no active grant" is ALSO the exact shape of a deliberately de-scoped administrator:
        //
        //   * AccessController.SetGroupScope(false) writes a `GroupScopeRevoked` audit row and
        //     revokes the user's refresh tokens — an explicit, recorded narrowing.
        //   * AccessController's grant delete sets UserEntityAccess.IsActive = false; revoking an
        //     admin's LAST company grant leaves them with no active grant.
        //
        // Either way the next restart silently widened them back to full group scope — on Render
        // that is every deploy, plus three OOM restarts in four days. Revocation became escalation.
        // The repair has also already run everywhere it could: against production on 2026-09-21 the
        // predicate above matched 0 rows. So it is deleted rather than narrowed, and what remains
        // is the read-only detection of the state it existed to find.
        //
        // Widening a live user's scope is an authorisation decision and belongs to an authenticated
        // administrator behind AccessController's audit trail, never to an unattended boot path.
        try
        {
            var strandedAdmins = await CountStrandedAdminsAsync(cancellationToken);
            if (strandedAdmins > 0)
            {
                Console.WriteLine(
                    $"[Seed] WARNING: {strandedAdmins} Admin-role user(s) resolve to entity_scope=none " +
                    "(no group scope and no active company grant). They cannot administer their tenant. " +
                    "Grant scope deliberately via PATCH /access/users/{id}/group-scope or an entity grant — " +
                    "the seeder no longer does this automatically, because it could not tell a " +
                    "never-configured admin from a deliberately de-scoped one.");
            }
        }
        catch (Exception ex) { Console.WriteLine($"[Seed] Admin entity-scope check skipped: {ex.Message}"); }
    }

    /// <summary>
    /// Read-only diagnostic: how many non-deleted Admin-role users resolve to entity_scope=none.
    /// </summary>
    /// <remarks>
    /// Raw SQL, deliberately: this must see every tenant, and the LINQ equivalent would need
    /// <c>IgnoreQueryFilters()</c> — a new raw bypass that
    /// <c>QueryFilterBypassRatchetTests</c> exists to prevent. It only ever SELECTs, so it cannot
    /// widen anyone's scope; <c>RawSqlExecutionRatchetTests</c> pins that property.
    /// </remarks>
    private async Task<int> CountStrandedAdminsAsync(CancellationToken cancellationToken)
    {
        var counts = await _db.Database
            .SqlQueryRaw<int>(
                @"SELECT COUNT(*)::int AS ""Value""
                  FROM users u
                  WHERE COALESCE(u.is_group_scope, FALSE) = FALSE
                    AND COALESCE(u.is_deleted, FALSE) = FALSE
                    AND EXISTS (
                        SELECT 1
                        FROM user_roles ur
                        JOIN roles r ON r.id = ur.role_id
                        WHERE ur.user_id = u.id
                          AND r.tenant_id = u.tenant_id
                          AND r.normalized_name = 'ADMIN')
                    AND NOT EXISTS (
                        SELECT 1
                        FROM user_entity_accesses uea
                        WHERE uea.user_id = u.id
                          AND uea.tenant_id = u.tenant_id
                          AND COALESCE(uea.is_active, TRUE) = TRUE);")
            .ToListAsync(cancellationToken);
        return counts.Count == 0 ? 0 : counts[0];
    }

    public async Task<Role> EnsureTenantRolesAsync(Guid tenantId, CancellationToken cancellationToken = default)
    {
        var permissions = await EnsurePermissions(cancellationToken);
        var Ps = (string[] keys) => permissions.Where(x => keys.Contains(x.Key)).ToList();

        // Level 1 — Admin: all permissions
        var adminRole = await EnsureRole(tenantId, "Admin", "Tenant system administrator with full access", permissions, 1, false, cancellationToken);

        // Level 2 — HR Director: full HR + payroll visibility + reports + compliance
        await EnsureRole(tenantId, "HR Director", "Senior HR leader with strategic visibility", permissions.Where(x =>
            x.Key.StartsWith("employees.") || x.Key.StartsWith("attendance.") || x.Key.StartsWith("leave.") ||
            x.Key.StartsWith("overtime.") || x.Key.StartsWith("dashboard.") || x.Key.StartsWith("organization.") ||
            x.Key.StartsWith("approvals.") || x.Key.StartsWith("notifications.") || x.Key.StartsWith("localization.") ||
            x.Key.StartsWith("performance.") || x.Key.StartsWith("compliance.") || x.Key.StartsWith("reports.") ||
            x.Key.StartsWith("recruitment.") || x.Key is "payroll.read" or "loans.read" or "audit.read" or
            "roles.manage" or "users.manage" or "manager.read" or "manager.approve" or
            "qiwa.read" or "qiwa.sync"
        ).ToList(), 2, true, cancellationToken);

        // Level 3 — HR Manager: operational HR management
        // NOTE: the explicit payroll.* / loans.write grants below are RECONCILIATION for the
        // [HasPermission] conversion, not new reach. HR Manager already reaches all PayrollController /
        // GosiController operator+approve endpoints and Bonuses/Loans-type creation today via the
        // role-name [Authorize(Roles="...HR Manager...")] gates; the bundle simply had no payroll/loan
        // WRITE keys. Higher-tier run lifecycle (payroll.lock / payroll.run_delete) is deliberately
        // NOT granted here, preserving HR Manager's current exclusion from lock/void/send-back/delete.
        await EnsureRole(tenantId, "HR Manager", "HR operations manager", permissions.Where(x =>
            x.Key.StartsWith("employees.") || x.Key.StartsWith("attendance.") || x.Key.StartsWith("leave.") ||
            x.Key.StartsWith("overtime.") || x.Key.StartsWith("dashboard.") || x.Key.StartsWith("organization.") ||
            x.Key.StartsWith("approvals.") || x.Key.StartsWith("notifications.") || x.Key.StartsWith("localization.") ||
            x.Key is "audit.read" or "manager.read" or "manager.approve" or "reports.read" or "qiwa.read" or
            "payroll.read" or "payroll.write" or "payroll.approve" or "loans.write"
        ).ToList(), 3, true, cancellationToken);

        // Level 4 — Payroll Manager: payroll + finance + employees
        await EnsureRole(tenantId, "Payroll Manager", "Manages payroll processing and WPS submissions", Ps(new[] {
            "dashboard.read", "employees.read", "employees.sensitive", "attendance.read", "leave.read",
            "overtime.read", "payroll.read", "payroll.write", "payroll.approve", "loans.read", "loans.approve",
            // payroll.run_delete reconciles the method-level [Authorize(Roles="Admin,Payroll Manager")] on
            // DELETE /payroll/runs/{id} into the effective-permission model (its current effective reach).
            "payroll.run_delete",
            "approvals.read", "approvals.decide", "reports.read", "notifications.read",
            // Phase 2 GL + non-statutory rates (NOT statutory_override / author_predicates — higher trust).
            "finance.gl.read", "finance.gl.manage", "finance.gl.drivers.manage",
            "payroll.rates.read", "payroll.rates.manage"
        }), 4, true, cancellationToken);

        // Level 5 — HR Officer: HR operations specialist
        await EnsureRole(tenantId, "HR Officer", "HR operations specialist", Ps(new[] {
            "dashboard.read", "employees.read", "employees.write", "employees.documents", "employees.templates",
            // employees.bulk_import reconciles HR Officer's existing role-name reach to POST /employees/import(-preview).
            "employees.bulk_import",
            "organization.read", "approvals.read", "approvals.write", "notifications.read", "localization.read",
            "leave.read", "leave.write", "attendance.read", "overtime.read", "profile.read"
        }), 5, true, cancellationToken);

        // Level 6 — Payroll Officer: payroll processing
        await EnsureRole(tenantId, "Payroll Officer", "Payroll and WPS specialist", Ps(new[] {
            "dashboard.read", "employees.read", "employees.sensitive", "attendance.read",
            "payroll.read", "payroll.write", "loans.read", "approvals.read", "notifications.read", "reports.read"
        }), 6, true, cancellationToken);

        // Level 7 — Finance Approver: finance approvals
        // payroll.lock reconciles the method-level [Authorize(Roles="...Finance Approver")] intent on the
        // run lock/void/send-back endpoints (financial-controller tier) into the effective-permission model.
        await EnsureRole(tenantId, "Finance Approver", "Finance approver for loans, advances and payroll", Ps(new[] {
            "dashboard.read", "employees.read", "payroll.read", "payroll.approve", "payroll.lock",
            "loans.read", "loans.approve", "approvals.read", "approvals.decide",
            "finance.gl.read", "payroll.rates.read"
        }), 7, true, cancellationToken);

        // Level 8 — Compliance Officer: compliance and contracts
        await EnsureRole(tenantId, "Compliance Officer", "Manages compliance, contracts and regulatory records", Ps(new[] {
            "dashboard.read", "employees.read", "employees.documents", "organization.read",
            "compliance.read", "compliance.write", "approvals.read", "audit.read", "reports.read", "notifications.read"
        }), 8, true, cancellationToken);

        // Level 9 — Manager: team management and approvals
        // approvals.write reconciles Manager's existing role-name reach to POST /approval-requests and
        // POST /approval-workflows/requests (starting an approval request) into the permission model.
        await EnsureRole(tenantId, "Manager", "People manager with team oversight and approval authority", Ps(new[] {
            "dashboard.read", "employees.read", "approvals.read", "approvals.write", "approvals.decide", "notifications.read",
            "manager.read", "manager.approve", "ess.read", "ess.write", "leave.read", "leave.approve",
            "attendance.read", "overtime.read", "overtime.approve", "profile.read"
        }), 9, true, cancellationToken);

        // Level 10 — Supervisor: front-line supervision
        await EnsureRole(tenantId, "Supervisor", "Front-line supervisor for operational staff", Ps(new[] {
            "dashboard.read", "employees.read", "attendance.read", "attendance.write",
            "manager.read", "manager.approve", "leave.read", "overtime.read", "ess.read", "ess.write", "profile.read"
        }), 10, true, cancellationToken);

        // Level 11 — Recruiter: talent acquisition
        await EnsureRole(tenantId, "Recruiter", "Recruitment and hiring specialist", Ps(new[] {
            "dashboard.read", "employees.read", "recruitment.read", "recruitment.write",
            "notifications.read", "organization.read", "profile.read"
        }), 11, true, cancellationToken);

        // Level 12 — HR Assistant: limited HR support
        await EnsureRole(tenantId, "HR Assistant", "Junior HR support with limited write access", Ps(new[] {
            "dashboard.read", "employees.read", "organization.read", "notifications.read",
            "attendance.read", "leave.read", "ess.read", "profile.read", "localization.read"
        }), 12, true, cancellationToken);

        // Level 13 — Auditor: read-only audit
        await EnsureRole(tenantId, "Auditor", "Read-only audit and compliance reviewer", Ps(new[] {
            "dashboard.read", "employees.read", "organization.read", "approvals.read",
            "audit.read", "payroll.read", "attendance.read", "leave.read", "compliance.read", "reports.read",
            "qiwa.read"
        }), 13, true, cancellationToken);

        // Level 14 — Kiosk Operator: attendance kiosk only
        await EnsureRole(tenantId, "Kiosk Operator", "Restricted to kiosk attendance capture only", Ps(new[] {
            "attendance.kiosk"
        }), 14, true, cancellationToken);

        // Level 15 — Employee: self-service only
        await EnsureRole(tenantId, "Employee", "Employee self-service user", Ps(new[] {
            "dashboard.read", "profile.read", "ess.read", "ess.write"
        }), 15, true, cancellationToken);

        // Establishment matrix: seed the default staffing-level catalog here so EVERY tenant
        // provisioning path (platform create/repair, all demo seeders, future ones) gets the
        // editable defaults without each caller remembering to. Idempotent; respects deliberate
        // deletion. GET /api/establishment/levels lazy-seeds pre-existing tenants as the backstop.
        await new EstablishmentSeeder(_db).EnsureStaffingLevelsAsync(tenantId, cancellationToken);

        return adminRole;
    }

    // NOTE: the former private EnsureGlobalCountryRules country-pack table moved into the single
    // idempotent TenantProvisioningBundle (FIX 1 / C1), which every tenant-create path invokes so
    // the packs are installed uniformly (per-rule idempotent, UAE weekend corrected, tier-tagged).

    private async Task<List<Permission>> EnsurePermissions(CancellationToken cancellationToken)
    {
        var definitions = new (string Key, string Module, string Description)[]
        {
            // Dashboard
            ("dashboard.read", "Dashboard", "Read workforce dashboard metrics"),
            ("dashboard.export", "Dashboard", "Export dashboard data"),
            // Employees
            ("employees.read", "Employees", "Read employee records"),
            ("employees.write", "Employees", "Create and update employee records"),
            ("employees.delete", "Employees", "Delete/archive employee records"),
            ("employees.sensitive", "Employees", "View sensitive employee fields (salary, NID, passport)"),
            ("employees.approve", "Employees", "Approve employee drafts, changes, and transfers"),
            ("employees.documents", "Employees", "Upload and download employee documents"),
            ("employees.templates", "Employees", "Generate localized employee document templates"),
            ("employees.bulk_import", "Employees", "Bulk import employee records"),
            // Profile
            ("profile.read", "Profile", "Read own profile"),
            ("profile.write", "Profile", "Update own profile"),
            // Organization
            ("organization.read", "Organization", "Read companies, branches, departments, and designations"),
            ("organization.write", "Organization", "Create and update organization master data"),
            ("organization.delete", "Organization", "Delete organization master data"),
            ("organization.establishment.write", "Organization", "Edit staffing budgets and establishment envelopes (departmental headcount by level)"),
            // Attendance
            ("attendance.read", "Attendance", "Read attendance records"),
            ("attendance.write", "Attendance", "Create and update attendance records"),
            ("attendance.delete", "Attendance", "Delete or cancel attendance records"),
            ("attendance.kiosk", "Attendance", "Use kiosk-only attendance capture"),
            ("attendance.bulk_import", "Attendance", "Bulk import attendance data"),
            ("attendance.lock", "Attendance", "Lock/unlock attendance periods"),
            // Leave
            ("leave.read", "Leave", "Read leave requests and balances"),
            ("leave.write", "Leave", "Submit and manage leave requests"),
            ("leave.approve", "Leave", "Approve or reject leave requests"),
            ("leave.cancel", "Leave", "Cancel approved leave"),
            ("leave.policy_manage", "Leave", "Manage leave types and policies"),
            // Overtime
            ("overtime.read", "Overtime", "Read overtime requests"),
            ("overtime.write", "Overtime", "Submit overtime requests"),
            ("overtime.approve", "Overtime", "Approve or reject overtime requests"),
            ("overtime.policy_manage", "Overtime", "Manage overtime types and policies"),
            // Payroll
            ("payroll.read", "Payroll", "Read payroll runs and slips"),
            ("payroll.write", "Payroll", "Create and process payroll runs"),
            ("payroll.approve", "Payroll", "Approve payroll runs"),
            ("payroll.export", "Payroll", "Export payroll and WPS files"),
            ("payroll.structure_manage", "Payroll", "Manage salary structures and components"),
            // Payroll run state-machine (financial-controller tier) — added for the [HasPermission]
            // conversion of the run lifecycle. Seeded into bundles below so today's role-name reach
            // is preserved (Admin via backfill; payroll.lock→Finance Approver; run_delete→Payroll Manager).
            ("payroll.lock", "Payroll", "Lock, void, or send back a payroll run (financial-controller tier)"),
            ("payroll.run_delete", "Payroll", "Hard-delete a payroll run"),
            // Loans & Advances
            ("loans.read", "Loans", "Read loan and advance records"),
            ("loans.write", "Loans", "Create loan and advance applications"),
            ("loans.approve", "Loans", "Approve or reject loans and advances"),
            ("loans.policy_manage", "Loans", "Manage loan types and policies"),
            // Recruitment
            ("recruitment.read", "Recruitment", "Read job openings and applications"),
            ("recruitment.write", "Recruitment", "Manage recruitment pipeline"),
            ("recruitment.approve", "Recruitment", "Approve requisitions and offers"),
            ("recruitment.delete", "Recruitment", "Delete recruitment records"),
            // Performance
            ("performance.read", "Performance", "Read appraisal and performance data"),
            ("performance.write", "Performance", "Create and update performance reviews"),
            ("performance.approve", "Performance", "Approve performance ratings and recommendations"),
            ("performance.cycle_manage", "Performance", "Manage performance cycles and templates"),
            // Compliance
            ("compliance.read", "Compliance", "Read compliance and contract records"),
            ("compliance.write", "Compliance", "Manage compliance documents"),
            ("compliance.approve", "Compliance", "Approve compliance items"),
            // Manager
            ("manager.read", "Manager", "Read direct and indirect team records"),
            ("manager.approve", "Manager", "Approve assigned team requests"),
            // ESS
            ("ess.read", "ESS", "Read own employee self-service records"),
            ("ess.write", "ESS", "Create employee self-service requests"),
            // Approvals
            ("approvals.read", "Approvals", "Read approval workflows and approval requests"),
            ("approvals.write", "Approvals", "Create approval workflows and start approval requests"),
            ("approvals.decide", "Approvals", "Approve or reject approval requests"),
            ("approvals.override", "Approvals", "Override configured approver role controls"),
            ("approvals.manage", "Approvals", "Manage approval workflow definitions"),
            // Enterprise setup
            ("organization.setup.apply", "Setup", "Apply AI-generated organization setup changes"),
            // Reports
            ("reports.read", "Reports", "Run and view reports"),
            ("reports.schedule", "Reports", "Create scheduled reports"),
            ("reports.export", "Reports", "Export reports to Excel/PDF"),
            // Notifications
            ("notifications.read", "Notifications", "Read workflow notifications"),
            ("notifications.manage", "Notifications", "Manage notification templates"),
            // Localization
            ("localization.read", "Localization", "Read localized calendar conversions"),
            ("localization.manage", "Localization", "Manage localization and calendar settings"),
            // Access Control
            ("roles.manage", "Access", "Manage user roles and permissions"),
            ("users.manage", "Access", "Manage users and user accounts"),
            ("security.manage", "Security", "Manage security settings and access policies"),
            // Audit
            ("audit.read", "Audit", "Read audit logs"),
            ("audit.export", "Audit", "Export audit logs"),
            // AI & Intelligence
            ("ai.query", "AI", "Query the AI HR assistant"),
            ("ai.insights_view", "AI", "View AI-generated workforce insights"),
            // Shifts
            ("shifts.read", "Shifts", "Read shift schedules and rosters"),
            ("shifts.write", "Shifts", "Create and update shift schedules"),
            ("shifts.manage", "Shifts", "Manage shift definitions and policies"),
            // QIWA (Saudi workforce platform)
            ("qiwa.configure", "QIWA", "Configure QIWA establishment credentials and connection"),
            ("qiwa.sync", "QIWA", "Trigger and manage QIWA employee sync operations"),
            ("qiwa.read", "QIWA", "View QIWA connection status and sync logs"),
            // Finance — GL configuration (Phase 2)
            ("finance.gl.read", "Finance", "View chart of accounts and payroll GL mappings"),
            ("finance.gl.manage", "Finance", "Manage GL accounts, mappings and per-company overrides"),
            ("finance.gl.drivers.manage", "Finance", "Manage custom GL posting drivers"),
            ("finance.gl.drivers.author_predicates", "Finance", "Author non-Exact GL driver predicates and employer-expense pairs (Admin/vendor)"),
            // Payroll — client rate configuration (Phase 2)
            ("payroll.rates.read", "Payroll", "View company and statutory rate configuration"),
            ("payroll.rates.manage", "Payroll", "Manage non-statutory company rate policies"),
            ("payroll.rates.statutory_override", "Payroll", "Create bounded per-company statutory rate overrides (reason + effective-dated + audited)"),
        };

        foreach (var definition in definitions)
        {
            if (!await _db.Permissions.AnyAsync(x => x.Key == definition.Key, cancellationToken))
            {
                _db.Permissions.Add(new Permission { Key = definition.Key, Module = definition.Module, Description = definition.Description });
            }
        }
        await _db.SaveChangesAsync(cancellationToken);

        // Self-healing prune of FOREIGN permissions. This global `permissions` catalog is shared and the
        // upsert above is additive-only, so permission keys seeded by a DIFFERENT product that once ran
        // against this database (the unmerged OpsTrax fleet/TMS/logistics branch) linger forever and show
        // up in the RBAC / Overrides UI. Remove them — plus their role links and per-user overrides — on
        // every boot. Targeted strictly by foreign namespace prefix, so HR and platform permissions can
        // never be affected. Idempotent: a no-op once the catalog is clean.
        var foreignPrefixes = new[] { "fleet_tms.", "fleet.", "logistics." };
        var foreignPermissions = (await _db.Permissions.ToListAsync(cancellationToken))
            .Where(p => foreignPrefixes.Any(prefix => p.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        if (foreignPermissions.Count > 0)
        {
            var foreignIds = foreignPermissions.Select(p => p.Id).ToHashSet();
            var foreignKeys = foreignPermissions.Select(p => p.Key).ToHashSet();
            var roleLinks = await _db.RolePermissions.Where(rp => foreignIds.Contains(rp.PermissionId)).ToListAsync(cancellationToken);
            var overrides = await _db.UserPermissionOverrides.Where(o => foreignKeys.Contains(o.PermissionKey)).ToListAsync(cancellationToken);
            _db.RolePermissions.RemoveRange(roleLinks);
            _db.UserPermissionOverrides.RemoveRange(overrides);
            _db.Permissions.RemoveRange(foreignPermissions);
            await _db.SaveChangesAsync(cancellationToken);
            Console.WriteLine(
                $"[Seed] Pruned {foreignPermissions.Count} foreign (fleet/TMS/logistics) permission(s), " +
                $"{roleLinks.Count} role link(s) and {overrides.Count} user override(s) from the RBAC catalog.");
        }

        return await _db.Permissions.ToListAsync(cancellationToken);
    }

    private async Task<Role> EnsureRole(Guid tenantId, string name, string description, IReadOnlyCollection<Permission> permissions, int authorityLevel, bool isEditable, CancellationToken cancellationToken)
    {
        var normalized = AuthService.Normalize(name);
        var role = await _db.Roles
            .Include(x => x.RolePermissions)
            .FirstOrDefaultAsync(x => x.TenantId == tenantId && x.NormalizedName == normalized, cancellationToken);
        if (role is null)
        {
            role = new Role
            {
                TenantId = tenantId, Name = name, NormalizedName = normalized, Description = description,
                IsSystem = true, AuthorityLevel = authorityLevel, IsActive = true, IsEditable = isEditable
            };
            _db.Roles.Add(role);
            await _db.SaveChangesAsync(cancellationToken);
        }
        else
        {
            role.AuthorityLevel = authorityLevel;
            role.IsEditable = isEditable;
            role.Description = description;
        }

        foreach (var permission in permissions)
        {
            if (!role.RolePermissions.Any(x => x.PermissionId == permission.Id))
            {
                role.RolePermissions.Add(new RolePermission { RoleId = role.Id, PermissionId = permission.Id });
            }
        }
        await _db.SaveChangesAsync(cancellationToken);
        return role;
    }
}
