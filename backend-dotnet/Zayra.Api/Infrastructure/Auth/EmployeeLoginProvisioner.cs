using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Auth;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.Data;
using Zayra.Api.Infrastructure.Jobs;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Auth;

/// <summary>
/// THE ONE place an employee's login is created (contract §3). The login belongs to the employee profile from
/// creation onwards, so there is never a link step: every creation path (single create, draft approval, bulk import,
/// a work email set later, the IT backfill) calls <see cref="EnsureStagedLoginAsync"/>.
///
/// <para>A STAGED login cannot sign in: NoLogin, inactive, an unusable password, the Employee role and an INACTIVE
/// grant to the employee's company. It is switched on only when the employee redeems a welcome code HR handed them
/// (<see cref="WelcomeCodeRedeemer"/>). Staging consumes no seat.</para>
///
/// <para>It never adopts an existing login: a work email that already belongs to another login (live, deleted, or a
/// former employee's) leaves the employee <c>blocked</c> for an administrator to resolve through the two-person Link.
/// It never invents a domain: a company without <c>EmailDomain</c> leaves the employee blocked.</para>
///
/// <para>Idempotent, and saves nothing: the caller's unit of work commits the login with the employee change that
/// caused it. Callers inside a transaction hold the employee row; the unique (tenant, normalized email) index on users
/// is the backstop for two concurrent runs.</para>
/// </summary>
public sealed class EmployeeLoginProvisioner
{
    public const string StagedAction = "access.employee_login_staged";

    public static class Results
    {
        public const string Staged = "staged";
        public const string Existing = "existing";
        public const string NoWorkEmail = "no_work_email";
        public const string Blocked = "blocked";
    }

    public static class BlockedCodes
    {
        public const string CompanyEmailDomainMissing = "company_email_domain_missing";
        public const string WrongDomain = "work_email_wrong_domain";
        public const string PlusAddress = WorkEmailPlusAddressException.Code;
        public const string InvalidCharacters = WorkEmailInvalidCharactersException.Code;
        public const string EmailBelongsToExistingLogin = "email_belongs_to_existing_login";
        public const string EmailBelongsToFormerEmployee = "email_belongs_to_former_employee";
        public const string LegacyUnlinkedLogin = "legacy_unlinked_login";
        public const string EmployeeRoleMissing = "employee_role_missing";
    }

    public sealed record Outcome(int EmployeeId, string Result, string? BlockedCode = null, Guid? UserId = null);

    private const string Why =
        "Employee login provisioning: the employee's own login, any login already using the work email, and the employee's company domain are read across legal entities; the tenant is re-applied and the employee was already authorised for the caller.";

    private readonly ZayraDbContext _db;

    public EmployeeLoginProvisioner(ZayraDbContext db) => _db = db;

    /// <summary>A password hash no password verifies: the PBKDF2 parser refuses the scheme, so Verify is false.</summary>
    public static string UnusableHash() => $"STAGED${Convert.ToBase64String(RandomNumberGenerator.GetBytes(24))}";

    public async Task<Outcome> EnsureStagedLoginAsync(Guid tenantId, Employee employee, RequestContext actor, CancellationToken ct) =>
        (await EnsureStagedLoginsAsync(tenantId, new[] { employee }, actor, ct))[0];

    /// <summary>Batch form (bulk import, backfill): one read per kind, not per row. Employees must already have ids.</summary>
    public async Task<IReadOnlyList<Outcome>> EnsureStagedLoginsAsync(
        Guid tenantId, IReadOnlyList<Employee> employees, RequestContext actor, CancellationToken ct)
    {
        var outcomes = new Outcome[employees.Count];
        if (employees.Count == 0) return outcomes;
        var nowUtc = DateTime.UtcNow;

        var ids = employees.Select(e => e.Id).Distinct().ToList();
        // Inside the caller's transaction the employee rows are held while their links are read and written, so two
        // concurrent runs for one employee serialise and the second sees the first's login (idempotent).
        if (_db.Database.IsRelational())
            await ScopedBypass.NullableTenantWide(_db.Employees, tenantId, Why).TagWith(RowLockingInterceptor.ForUpdateTag)
                .Where(e => ids.Contains(e.Id)).OrderBy(e => e.Id).Select(e => e.Id).ToListAsync(ct);
        var linkedEmployeeIds = (await ScopedBypass.TenantWide(_db.EmployeeUserAccounts, tenantId, Why)
                .Where(x => ids.Contains(x.EmployeeId) && !x.IsDeleted)
                .Select(x => x.EmployeeId)
                .ToListAsync(ct))
            .Concat(_db.ChangeTracker.Entries<EmployeeUserAccount>()
                .Where(x => x.State == EntityState.Added && x.Entity.TenantId == tenantId && !x.Entity.IsDeleted)
                .Select(x => x.Entity.EmployeeId))
            .ToHashSet();

        var companyIds = employees.Where(e => e.CompanyId.HasValue).Select(e => e.CompanyId!.Value).Distinct().ToList();
        var domains = await ScopedBypass.TenantWide(_db.Companies, tenantId, Why).AsNoTracking()
            .Where(c => companyIds.Contains(c.Id) && !c.IsDeleted)
            .Select(c => new { c.Id, c.EmailDomain })
            .ToDictionaryAsync(c => c.Id, c => (c.EmailDomain ?? string.Empty).Trim().ToLowerInvariant(), ct);

        var wanted = employees
            .Select(e => (e.WorkEmail ?? string.Empty).Trim())
            .Where(e => e.Length > 0)
            .Select(AuthService.Normalize)
            .Distinct()
            .ToList();
        var owners = await ScopedBypass.TenantWide(_db.Users, tenantId, Why).AsNoTracking()
            .Where(u => wanted.Contains(u.NormalizedEmail))
            .Select(u => new { u.Id, u.NormalizedEmail })
            .ToListAsync(ct);
        var ownerByEmail = owners.GroupBy(o => o.NormalizedEmail).ToDictionary(g => g.Key, g => g.Select(x => x.Id).ToList());
        var formerOwnerIds = await FormerEmployeeLoginIdsAsync(tenantId, owners.Select(o => o.Id).ToList(), ct);
        // Logins staged earlier in this same unit of work (bulk rows sharing an address).
        var claimed = _db.ChangeTracker.Entries<User>()
            .Where(x => x.State == EntityState.Added && x.Entity.TenantId == tenantId)
            .Select(x => x.Entity.NormalizedEmail)
            .ToHashSet(StringComparer.Ordinal);

        Role? employeeRole = null;
        var roleLoaded = false;

        for (var i = 0; i < employees.Count; i++)
        {
            var employee = employees[i];
            var email = (employee.WorkEmail ?? string.Empty).Trim();
            if (email.Length == 0) { outcomes[i] = new Outcome(employee.Id, Results.NoWorkEmail); continue; }
            if (linkedEmployeeIds.Contains(employee.Id)) { outcomes[i] = new Outcome(employee.Id, Results.Existing); continue; }

            string? blocked = null;
            var domain = employee.CompanyId is Guid cid && domains.TryGetValue(cid, out var d) ? d : string.Empty;
            var normalized = AuthService.Normalize(email);
            if (employee.UserAccountId.HasValue) blocked = BlockedCodes.LegacyUnlinkedLogin;
            else if (WorkEmailPlusAddressException.IsPlusAddressed(email)) blocked = BlockedCodes.PlusAddress;
            else if (WorkEmailInvalidCharactersException.IsInvalid(email)) blocked = BlockedCodes.InvalidCharacters;
            else if (domain.Length == 0) blocked = BlockedCodes.CompanyEmailDomainMissing;
            else if (!IsOnDomain(email, domain)) blocked = BlockedCodes.WrongDomain;
            else if (ownerByEmail.TryGetValue(normalized, out var ownerIds))
                blocked = ownerIds.Any(formerOwnerIds.Contains)
                    ? BlockedCodes.EmailBelongsToFormerEmployee
                    : BlockedCodes.EmailBelongsToExistingLogin;
            else if (claimed.Contains(normalized)) blocked = BlockedCodes.EmailBelongsToExistingLogin;

            if (blocked is null && !roleLoaded)
            {
                employeeRole = await _db.Roles.AsNoTracking()
                    .Where(x => (x.TenantId == tenantId || x.TenantId == null)
                        && x.NormalizedName == "EMPLOYEE" && x.IsActive && !x.IsDeleted)
                    .OrderByDescending(x => x.TenantId == tenantId)
                    .FirstOrDefaultAsync(ct);
                roleLoaded = true;
            }
            if (blocked is null && employeeRole is null) blocked = BlockedCodes.EmployeeRoleMissing;
            if (blocked is not null) { outcomes[i] = new Outcome(employee.Id, Results.Blocked, blocked); continue; }

            var user = new User
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                Email = email.ToLowerInvariant(),
                NormalizedEmail = normalized,
                FullName = employee.FullName,
                PasswordHash = UnusableHash(),
                Status = "PendingPasswordSetup",
                AccessMode = AccessModes.NoLogin,
                IsActive = false,
                IsEmailConfirmed = false,
                MustChangePassword = false,
                IdentityProvider = "Local",
                ProvisioningSource = "Local",
                CreatedAtUtc = nowUtc,
                UpdatedAtUtc = nowUtc,
            };
            _db.Users.Add(user);
            _db.UserRoles.Add(new UserRole { UserId = user.Id, RoleId = employeeRole!.Id });
            if (employee.CompanyId is Guid companyId)
                _db.UserEntityAccesses.Add(new UserEntityAccess
                {
                    TenantId = tenantId,
                    UserId = user.Id,
                    CompanyId = companyId,
                    GrantMode = EntityGrantModes.SelectedCompanies,
                    Role = "Employee",
                    // Staged like the login: switched on only when the employee redeems their welcome code.
                    IsActive = false,
                    CreatedAtUtc = nowUtc,
                    CreatedBy = actor.UserId,
                    GrantedBy = actor.UserId,
                    GrantedAt = nowUtc,
                });
            var link = new EmployeeUserAccount
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                EmployeeId = employee.Id,
                UserId = user.Id,
                AccessMode = AccessModes.NoLogin,
                Status = "PendingPasswordSetup",
                IsPrimary = true,
                RequiresPasswordSetup = true,
                InvitationTokenHash = string.Empty,
                CreatedAtUtc = nowUtc,
                CreatedBy = actor.UserId,
            };
            _db.EmployeeUserAccounts.Add(link);
            employee.UserAccountId = user.Id;
            _db.AuditLogs.Add(AuthAuditEntry.Create(
                Guid.NewGuid(), nowUtc, StagedAction, "User", user.Id.ToString(), actor with { TenantId = tenantId },
                JsonSerializer.Serialize(new { employeeId = employee.Id, linkId = link.Id, note = "Staged login: cannot sign in until the employee redeems a welcome code." })));
            claimed.Add(normalized);
            linkedEmployeeIds.Add(employee.Id);
            outcomes[i] = new Outcome(employee.Id, Results.Staged, null, user.Id);
        }
        return outcomes;
    }

    public static bool IsOnDomain(string workEmail, string domain)
    {
        var (matches, _) = Employees.WorkEmailDeriver.ValidateAgainstDomain(workEmail, domain);
        return matches && Employees.WorkEmailDeriver.ExtractLocalPart(workEmail).Trim().Length > 0;
    }

    /// <summary>Logins (of <paramref name="userIds"/>) that belong to a former employee: terminated/exited/archived, deleted or merged.</summary>
    public async Task<HashSet<Guid>> FormerEmployeeLoginIdsAsync(Guid tenantId, IReadOnlyCollection<Guid> userIds, CancellationToken ct)
    {
        if (userIds.Count == 0) return new HashSet<Guid>();
        var ids = userIds.ToList();
        var linkedEmployeeIds = await ScopedBypass.TenantWide(_db.EmployeeUserAccounts, tenantId, Why).AsNoTracking()
            .Where(x => x.UserId != null && ids.Contains(x.UserId.Value))
            .Select(x => new { UserId = x.UserId!.Value, x.EmployeeId })
            .ToListAsync(ct);
        var employeeIds = linkedEmployeeIds.Select(x => x.EmployeeId).Distinct().ToList();
        var employees = await ScopedBypass.NullableTenantWide(_db.Employees, tenantId, Why).AsNoTracking()
            .Where(e => employeeIds.Contains(e.Id) || (e.UserAccountId != null && ids.Contains(e.UserAccountId.Value)))
            .Select(e => new { e.Id, e.UserAccountId, e.Status, e.IsDeleted, e.DuplicateOfEmployeeId })
            .ToListAsync(ct);
        var former = employees.Where(e => e.IsDeleted || e.DuplicateOfEmployeeId != null || EmployeeAccessStates.IsFormerStatus(e.Status)).ToList();
        var result = new HashSet<Guid>();
        foreach (var f in former)
        {
            if (f.UserAccountId is Guid pointer && ids.Contains(pointer)) result.Add(pointer);
            foreach (var l in linkedEmployeeIds.Where(l => l.EmployeeId == f.Id)) result.Add(l.UserId);
        }
        return result;
    }
}
