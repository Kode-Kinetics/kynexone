using Microsoft.EntityFrameworkCore;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.Data;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Auth;

/// <summary>
/// Where one employee stands on the way to signing in (contract §1, precedence from Amendment 3 F3):
/// stopped &gt; blocked &gt; code_given (a live code OR a live invitation) &gt; active &gt; not_started &gt; waiting_for_work_email.
/// One evaluator for the profile card, the list column, the list filter and the issuing rules, so they never disagree.
/// </summary>
public static class EmployeeAccessStates
{
    public const string WaitingForWorkEmail = "waiting_for_work_email";
    public const string NotStarted = "not_started";
    public const string CodeGiven = "code_given";
    public const string Active = "active";
    public const string Stopped = "stopped";
    public const string Blocked = "blocked";

    public static readonly IReadOnlyList<string> All = [WaitingForWorkEmail, NotStarted, CodeGiven, Active, Stopped, Blocked];

    /// <summary>Employment statuses that stop access outright.</summary>
    private static readonly string[] StoppedStatuses =
        [EmployeeStatuses.Suspended, "Inactive", "Terminated", EmployeeStatuses.Archived, "Exited"];

    /// <summary>A former employee: their login must never be handed to someone new (F8).</summary>
    public static bool IsFormerStatus(string? status) =>
        status is "Terminated" or "Exited" or EmployeeStatuses.Archived;

    public static string BlockedReason(string code, string? domain = null) => code switch
    {
        EmployeeLoginProvisioner.BlockedCodes.CompanyEmailDomainMissing =>
            "The company has no official email domain yet. An administrator adds it in company settings.",
        EmployeeLoginProvisioner.BlockedCodes.WrongDomain =>
            string.IsNullOrEmpty(domain) ? "The work email isn't on the company's email domain." : $"Work email must end in @{domain}.",
        EmployeeLoginProvisioner.BlockedCodes.PlusAddress => WorkEmailPlusAddressException.Text,
        EmployeeLoginProvisioner.BlockedCodes.InvalidCharacters => WorkEmailInvalidCharactersException.Text,
        EmployeeLoginProvisioner.BlockedCodes.EmailBelongsToExistingLogin =>
            "This work email is already someone's sign-in. An administrator must check it is the same person and connect it.",
        EmployeeLoginProvisioner.BlockedCodes.EmailBelongsToFormerEmployee =>
            "This work email belongs to a former employee's sign-in. An administrator must resolve it before access is given.",
        EmployeeLoginProvisioner.BlockedCodes.LegacyUnlinkedLogin =>
            "This employee has an older sign-in that isn't connected properly. An administrator must resolve it.",
        EmployeeLoginProvisioner.BlockedCodes.EmployeeRoleMissing =>
            "The Employee role is missing in this workspace. An administrator must restore it.",
        _ => "An administrator must resolve this employee's sign-in.",
    };

    /// <summary>Everything the evaluator reads about one employee.</summary>
    public sealed record Facts(
        int EmployeeId,
        string EmployeeName,
        string ArabicName,
        string EmployeeCode,
        string Department,
        string Site,
        string Status,
        bool IsDeleted,
        bool IsMerged,
        string WorkEmail,
        Guid? CompanyId,
        string CompanyDomain,
        Guid? PointerUserId,
        DateTime JoiningDate,
        EmployeeUserAccount? Link,
        LoginFacts? Login,
        string? EmailOwnerBlockedCode);

    public sealed record LoginFacts(Guid UserId, string Email, string Status, bool IsActive, bool IsDeleted, DateTime? LastLoginAtUtc);

    public sealed record Evaluation(
        string State,
        DateTime? CodeExpiresAtUtc = null,
        Guid? CodeIssuedBy = null,
        DateTime? LastCodeExpiredAtUtc = null,
        DateTime? LastSignInAtUtc = null,
        string? StoppedReason = null,
        string? BlockedCode = null,
        string? BlockedReason = null);

    public static Evaluation Evaluate(Facts f, DateTime nowUtc)
    {
        var link = f.Link;
        var login = f.Login;

        // ── stopped ──
        if (f.IsDeleted || f.IsMerged)
            return new Evaluation(Stopped, StoppedReason: "The employee record was removed.");
        if (StoppedStatuses.Contains(f.Status, StringComparer.OrdinalIgnoreCase))
            return new Evaluation(Stopped, StoppedReason: $"The employee's status is {f.Status}.");
        if (link is not null && !string.IsNullOrEmpty(link.LoginDisabledReason) && link.AccessMode == AccessModes.NoLogin)
            return new Evaluation(Stopped, StoppedReason: link.LoginDisabledReason);
        if (login is not null && login.Status is "Deactivated" or "Suspended" or "Locked")
            return new Evaluation(Stopped, StoppedReason: login.Status == "Locked"
                ? "The sign-in was locked by an administrator."
                : "The sign-in was switched off by an administrator.");

        // ── blocked ──
        if (link is not null && (login is null || login.IsDeleted))
            return Block(EmployeeLoginProvisioner.BlockedCodes.LegacyUnlinkedLogin, f.CompanyDomain);
        if (link is null)
        {
            if (f.PointerUserId.HasValue) return Block(EmployeeLoginProvisioner.BlockedCodes.LegacyUnlinkedLogin, f.CompanyDomain);
            var email = f.WorkEmail.Trim();
            if (email.Length == 0) return new Evaluation(WaitingForWorkEmail);
            if (WorkEmailPlusAddressException.IsPlusAddressed(email)) return Block(EmployeeLoginProvisioner.BlockedCodes.PlusAddress, f.CompanyDomain);
            if (WorkEmailInvalidCharactersException.IsInvalid(email)) return Block(EmployeeLoginProvisioner.BlockedCodes.InvalidCharacters, f.CompanyDomain);
            if (f.CompanyDomain.Length == 0) return Block(EmployeeLoginProvisioner.BlockedCodes.CompanyEmailDomainMissing, null);
            if (!EmployeeLoginProvisioner.IsOnDomain(email, f.CompanyDomain))
                return Block(EmployeeLoginProvisioner.BlockedCodes.WrongDomain, f.CompanyDomain);
            if (f.EmailOwnerBlockedCode is { } owner) return Block(owner, f.CompanyDomain);
            return new Evaluation(NotStarted);
        }

        // ── code_given (a live code or a live invitation) ──
        if (WelcomeCodes.IsLive(link, nowUtc))
            return new Evaluation(CodeGiven, link.WelcomeCodeExpiresAtUtc, link.WelcomeCodeIssuedBy,
                LastSignInAtUtc: login!.LastLoginAtUtc);
        if (WelcomeCodes.HasLiveInvitation(link, nowUtc))
            return new Evaluation(CodeGiven, link.InvitationExpiresAtUtc, null, LastSignInAtUtc: login!.LastLoginAtUtc);

        // ── active ──
        if (login!.IsActive && !link.RequiresPasswordSetup && link.AccessMode != AccessModes.NoLogin)
            return new Evaluation(Active, LastSignInAtUtc: login.LastLoginAtUtc);

        // ── not_started ──
        DateTime? lastExpired = !string.IsNullOrEmpty(link.WelcomeCodeHash) && link.WelcomeCodeRedeemedAtUtc is null
            ? link.WelcomeCodeExpiresAtUtc
            : null;
        return new Evaluation(NotStarted, LastCodeExpiredAtUtc: lastExpired, LastSignInAtUtc: login.LastLoginAtUtc);

        static Evaluation Block(string code, string? domain) => new(Blocked, BlockedCode: code, BlockedReason: BlockedReason(code, domain));
    }

    private const string Why =
        "Employee access state: each listed employee's own login, any login already using their work email, and the employing company's domain are read across legal entities; the tenant is re-applied and the employee ids were already scope-filtered by the caller.";

    /// <summary>Loads the facts for <paramref name="employeeIds"/> (already tenant- and scope-filtered by the caller).</summary>
    public static async Task<Dictionary<int, Facts>> LoadAsync(ZayraDbContext db, Guid tenantId, IReadOnlyCollection<int> employeeIds, CancellationToken ct)
    {
        var result = new Dictionary<int, Facts>();
        if (employeeIds.Count == 0) return result;
        var ids = employeeIds.Distinct().ToList();

        var employees = await ScopedBypass.NullableTenantWide(db.Employees, tenantId, Why).AsNoTracking()
            .Where(e => ids.Contains(e.Id))
            .Select(e => new
            {
                e.Id, e.FullName, e.ArabicName, e.EmployeeCode, e.Department, e.Branch, e.BranchId, e.Status, e.IsDeleted,
                e.DuplicateOfEmployeeId, e.WorkEmail, e.CompanyId, e.UserAccountId, e.JoiningDate,
            })
            .ToListAsync(ct);

        var companyIds = employees.Where(e => e.CompanyId.HasValue).Select(e => e.CompanyId!.Value).Distinct().ToList();
        var domains = await ScopedBypass.TenantWide(db.Companies, tenantId, Why).AsNoTracking()
            .Where(c => companyIds.Contains(c.Id) && !c.IsDeleted)
            .Select(c => new { c.Id, c.EmailDomain })
            .ToDictionaryAsync(c => c.Id, c => (c.EmailDomain ?? string.Empty).Trim().ToLowerInvariant(), ct);
        var branchIds = employees.Where(e => string.IsNullOrEmpty(e.Branch) && e.BranchId.HasValue).Select(e => e.BranchId!.Value).Distinct().ToList();
        var branches = branchIds.Count == 0
            ? new Dictionary<Guid, string>()
            : await ScopedBypass.TenantWide(db.Branches, tenantId, Why).AsNoTracking()
                .Where(b => branchIds.Contains(b.Id))
                .ToDictionaryAsync(b => b.Id, b => b.NameEn, ct);

        var links = await ScopedBypass.TenantWide(db.EmployeeUserAccounts, tenantId, Why).AsNoTracking()
            .Where(x => ids.Contains(x.EmployeeId) && !x.IsDeleted)
            .ToListAsync(ct);
        var primaryLinks = links.GroupBy(x => x.EmployeeId)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(x => x.IsPrimary).ThenByDescending(x => x.CreatedAtUtc).First());
        var linkedUserIds = primaryLinks.Values.Where(l => l.UserId.HasValue).Select(l => l.UserId!.Value).Distinct().ToList();
        var logins = await ScopedBypass.TenantWide(db.Users, tenantId, Why).AsNoTracking()
            .Where(u => linkedUserIds.Contains(u.Id))
            .Select(u => new LoginFacts(u.Id, u.Email, u.Status, u.IsActive, u.IsDeleted, u.LastLoginAtUtc))
            .ToDictionaryAsync(u => u.UserId, ct);

        // Unlinked employees whose work email is already some login's username.
        var unlinkedEmails = employees
            .Where(e => !primaryLinks.ContainsKey(e.Id) && !string.IsNullOrWhiteSpace(e.WorkEmail))
            .Select(e => AuthService.Normalize(e.WorkEmail))
            .Distinct()
            .ToList();
        var owners = unlinkedEmails.Count == 0
            ? []
            : await ScopedBypass.TenantWide(db.Users, tenantId, Why).AsNoTracking()
                .Where(u => unlinkedEmails.Contains(u.NormalizedEmail))
                .Select(u => new { u.Id, u.NormalizedEmail })
                .ToListAsync(ct);
        var former = await new EmployeeLoginProvisioner(db).FormerEmployeeLoginIdsAsync(tenantId, owners.Select(o => o.Id).ToList(), ct);
        var ownerCode = owners.GroupBy(o => o.NormalizedEmail).ToDictionary(g => g.Key, g => g.Any(o => former.Contains(o.Id))
            ? EmployeeLoginProvisioner.BlockedCodes.EmailBelongsToFormerEmployee
            : EmployeeLoginProvisioner.BlockedCodes.EmailBelongsToExistingLogin);

        foreach (var e in employees)
        {
            primaryLinks.TryGetValue(e.Id, out var link);
            LoginFacts? login = link?.UserId is Guid uid && logins.TryGetValue(uid, out var lf) ? lf : null;
            var normalized = string.IsNullOrWhiteSpace(e.WorkEmail) ? string.Empty : AuthService.Normalize(e.WorkEmail);
            result[e.Id] = new Facts(
                e.Id, e.FullName ?? string.Empty, e.ArabicName ?? string.Empty, e.EmployeeCode ?? string.Empty,
                e.Department ?? string.Empty,
                !string.IsNullOrEmpty(e.Branch) ? e.Branch : e.BranchId is Guid b && branches.TryGetValue(b, out var bn) ? bn : string.Empty,
                e.Status ?? string.Empty, e.IsDeleted, e.DuplicateOfEmployeeId.HasValue, e.WorkEmail ?? string.Empty,
                e.CompanyId, e.CompanyId is Guid c && domains.TryGetValue(c, out var dom) ? dom : string.Empty,
                e.UserAccountId, e.JoiningDate, link, login,
                link is null && ownerCode.TryGetValue(normalized, out var oc) ? oc : null);
        }
        return result;
    }

    /// <summary>Evaluates states for <paramref name="employeeIds"/>.</summary>
    public static async Task<Dictionary<int, (Facts Facts, Evaluation State)>> EvaluateAsync(
        ZayraDbContext db, Guid tenantId, IReadOnlyCollection<int> employeeIds, DateTime nowUtc, CancellationToken ct)
    {
        var facts = await LoadAsync(db, tenantId, employeeIds, ct);
        return facts.ToDictionary(kv => kv.Key, kv => (kv.Value, Evaluate(kv.Value, nowUtc)));
    }

    /// <summary>Parses the list's <c>access=</c> parameter (comma-separated). Unknown values are ignored; empty = no filter.</summary>
    public static IReadOnlySet<string>? ParseFilter(string? access)
    {
        if (string.IsNullOrWhiteSpace(access)) return null;
        var set = access.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(x => x.ToLowerInvariant())
            .Where(All.Contains)
            .ToHashSet(StringComparer.Ordinal);
        return set;
    }

    /// <summary>
    /// Restricts <paramref name="query"/> to employees in one of <paramref name="states"/>. The state is computed by the
    /// same evaluator the column shows, over every employee the query matches, so the filter works past page one.
    /// </summary>
    public static async Task<IQueryable<Employee>> ApplyFilterAsync(
        IQueryable<Employee> query, ZayraDbContext db, Guid tenantId, IReadOnlySet<string>? states, CancellationToken ct)
    {
        if (states is null) return query;
        if (states.Count == 0) return query.Where(_ => false);
        var ids = await query.Select(e => e.Id).ToListAsync(ct);
        var evaluated = await EvaluateAsync(db, tenantId, ids, DateTime.UtcNow, ct);
        var keep = evaluated.Where(kv => states.Contains(kv.Value.State.State)).Select(kv => kv.Key).ToList();
        return query.Where(e => keep.Contains(e.Id));
    }
    /// <summary>Fills <c>AccessState</c> and <c>WorkEmail</c> on a page of list items.</summary>
    public static async Task<List<Zayra.Api.Controllers.EmployeeListItemDto>> DecorateAsync(
        ZayraDbContext db, Guid tenantId, List<Zayra.Api.Controllers.EmployeeListItemDto> items, CancellationToken ct)
    {
        if (items.Count == 0) return items;
        var evaluated = await EvaluateAsync(db, tenantId, items.Select(i => i.Id).ToList(), DateTime.UtcNow, ct);
        return items.Select(i => evaluated.TryGetValue(i.Id, out var e)
            ? i with { AccessState = e.State.State, WorkEmail = e.Facts.WorkEmail }
            : i).ToList();
    }
}
