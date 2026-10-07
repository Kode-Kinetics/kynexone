using System.Data;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Zayra.Api.Application.Auth;
using Zayra.Api.Application.Common;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.Data;
using Zayra.Api.Infrastructure.Jobs;
using Zayra.Api.Infrastructure.Email;
using Zayra.Api.Infrastructure.Employees;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Auth;

// ── Contract §4 shapes (frontend builders mock exactly these) ──────────────────────────────────────────────

public sealed record EmployeeAccessDto(
    int EmployeeId, string EmployeeName, string EmployeeCode, string WorkEmail, string State,
    DateTime? CodeExpiresAtUtc, string? CodeIssuedByName, DateTime? LastCodeExpiredAtUtc, DateTime? LastSignInAtUtc,
    string? StoppedReason, string? BlockedCode, string? BlockedReason, bool CanIssue,
    /// <summary>The workspace has a working email transport (codes are emailed instead of printed).</summary>
    bool EmailDelivery);

/// <summary><c>Delivery</c>: "print" never emails — the codes come back (and are recorded as disclosed) even when the
/// workspace can send email. Absent: emailed when a relay exists, otherwise returned.</summary>
public sealed record IssueCodesRequest(IReadOnlyList<int>? EmployeeIds, string? Delivery = null)
{
    public const string Print = "print";
    public bool PrintOnly => string.Equals(Delivery?.Trim(), Print, StringComparison.OrdinalIgnoreCase);
}

public sealed record IssuedCodeDto(
    int EmployeeId, string EmployeeName, string ArabicName, string EmployeeCode,
    /// <summary>The login's username (User.Email) — what the slip prints as "Your username" (F7).</summary>
    string Username,
    string Department, string Site,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Code,
    DateTime ExpiresAtUtc, string TenantSlug);

public sealed record SkippedCodeDto(int EmployeeId, string ReasonCode, string Reason);

public sealed record IssueCodesResponse(IReadOnlyList<IssuedCodeDto> Issued, IReadOnlyList<SkippedCodeDto> Skipped, bool Emailed, string DeliveryMessage);

public sealed record WorkEmailBackfillRow(string? EmployeeCode, string? WorkEmail);
public sealed record WorkEmailBackfillRequest(IReadOnlyList<WorkEmailBackfillRow>? Rows, bool DryRun);
public sealed record BackfillMatchDto(int EmployeeId, string EmployeeCode, string EmployeeName, string OldEmail, string NewEmail);
public sealed record BackfillWrongDomainDto(string EmployeeCode, string WorkEmail, string ExpectedDomain);
public sealed record BackfillConflictDto(string EmployeeCode, string WorkEmail, string Reason);
public sealed record WorkEmailBackfillResponse(
    IReadOnlyList<BackfillMatchDto> Matched, IReadOnlyList<string> NotFound, IReadOnlyList<BackfillWrongDomainDto> WrongDomain,
    IReadOnlyList<BackfillConflictDto> Conflicts, int Saved);

/// <summary>A whole-request refusal (400 with <c>code</c>).</summary>
public sealed class EmployeeAccessRequestException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

/// <summary>
/// HR's side of employee access (contract §4, Amendments 1–3): the state card, issuing welcome codes (single, bulk,
/// and "Reset sign-in" on an active login) and the IT work-email backfill.
/// </summary>
public sealed class EmployeeAccessService
{
    public const string IssuedAction = "access.welcome_code_issued";
    public const string DisclosedAction = "access.welcome_code_disclosed";
    public const int MaxBulk = 500;
    public const int MaxBackfillRows = 2000;
    public const int BackfillChunk = 200;

    /// <summary>
    /// SEATS (F9) — OWNER DECISION PENDING: whether an ESS-only login uses a paid seat. True keeps today's behaviour
    /// (every active login counts). Flip here; issue and redeem both read it.
    /// </summary>
    public const bool EssOnlyLoginsUseASeat = true;

    public static class Skip
    {
        public const string NotFound = "not_found";
        public const string NotActive = "employee_not_active";
        public const string Stopped = "stopped";
        public const string SelfIssue = "cannot_issue_for_self";
        public const string AboveCeiling = "above_ceiling";
        public const string Privileged = "privileged_login";
        public const string ResetNeedsPermission = "reset_requires_permission";
        public const string Blocked = "blocked";
        public const string SeatLimit = "seat_limit";
    }

    public static string SkipReason(string code) => code switch
    {
        Skip.NotFound => "Employee not found.",
        Skip.NotActive => "Activate the employee first.",
        Skip.Stopped => "This employee's access is stopped.",
        Skip.SelfIssue => "You can't give yourself a sign-in code.",
        Skip.AboveCeiling => "This person has access you don't hold, so someone with more access must do this.",
        Skip.Privileged => "This sign-in has extra access. Someone who can reset sign-ins must do it, one person at a time.",
        Skip.ResetNeedsPermission => "This employee already uses KynexOne. Resetting their sign-in needs the reset permission.",
        Skip.SeatLimit => "Your company's KynexOne plan is full.",
        EmployeeAccessStates.WaitingForWorkEmail => "No work email yet.",
        WorkEmailSetterRule.SetByCallerCode => "You set this person's work email, so another HR colleague must give access.",
        _ => EmployeeAccessStates.BlockedReason(code),
    };

    private const string Why =
        "Employee access: an in-scope employee's own login, links and reset links, and the tenant's seat count are read across legal entities; the tenant is re-applied and the employee was scope-checked against the caller.";

    private readonly ZayraDbContext _db;
    private readonly IEmailService _email;
    private readonly byte[] _codeKey;
    private readonly string _appUrl;

    public EmployeeAccessService(ZayraDbContext db, IEmailService email, IOptions<JwtOptions> jwt, IConfiguration? configuration = null)
    {
        _db = db;
        _email = email;
        _codeKey = WelcomeCodes.DeriveKey(jwt.Value.SigningKey);
        _appUrl = (AuthLinkBuilder.ResolvePublicAppUrl(configuration?["APP_URL"] ?? Environment.GetEnvironmentVariable("APP_URL")) ?? string.Empty).TrimEnd('/');
    }

    // ── GET ────────────────────────────────────────────────────────────────────────────────────────────

    public async Task<EmployeeAccessDto?> GetAsync(Guid tenantId, int employeeId, EntityScopeContext scope, RequestContext caller,
        bool canIssue, bool canReset, CancellationToken ct)
    {
        var employee = await ScopedBypass.NullableTenantWide(_db.Employees, tenantId, Why).AsNoTracking()
            .Where(e => e.Id == employeeId && !e.IsDeleted)
            .Select(e => new { e.Id, e.CompanyId, e.Status })
            .FirstOrDefaultAsync(ct);
        if (employee is null || !scope.CanAccessCompany(employee.CompanyId)) return null;
        var now = DateTime.UtcNow;
        var (facts, state) = (await EmployeeAccessStates.EvaluateAsync(_db, tenantId, new[] { employeeId }, now, ct))[employeeId];
        string? issuerName = null;
        if (state.CodeIssuedBy is Guid issuer)
            issuerName = await ScopedBypass.TenantWide(_db.Users, tenantId, Why).AsNoTracking()
                .Where(u => u.Id == issuer).Select(u => u.FullName).FirstOrDefaultAsync(ct);

        var allowed = state.State switch
        {
            EmployeeAccessStates.NotStarted or EmployeeAccessStates.CodeGiven when facts.Login is not { IsActive: true } => canIssue,
            EmployeeAccessStates.Active or EmployeeAccessStates.CodeGiven => canReset,
            _ => false,
        };
        if (allowed && (!AuthCurrentEligibility.IsEmployeeLifecycleEligible(facts.Status)
                        || (caller.UserId is Guid me && (facts.Login?.UserId == me
                            || await WorkEmailSetterRule.IsCallerSetterAsync(_db, tenantId, employeeId, me, ct)))))
            allowed = false;

        return new EmployeeAccessDto(facts.EmployeeId, facts.EmployeeName, facts.EmployeeCode, facts.WorkEmail, state.State,
            state.CodeExpiresAtUtc, issuerName, state.LastCodeExpiredAtUtc, state.LastSignInAtUtc,
            state.StoppedReason, state.BlockedCode, state.BlockedReason, allowed,
            await _email.IsConfiguredAsync(tenantId, ct));
    }

    // ── POST codes ─────────────────────────────────────────────────────────────────────────────────────

    private sealed record Pending(IssuedCodeDto Item, Guid UserId, string Email, bool ResetOfActive);

    public async Task<IssueCodesResponse> IssueCodesAsync(Guid tenantId, IssueCodesRequest request, EntityScopeContext scope,
        RequestContext caller, bool canIssue, bool canReset, CancellationToken ct)
    {
        var ids = (request.EmployeeIds ?? Array.Empty<int>()).Where(x => x > 0).Distinct().ToList();
        if (ids.Count == 0) throw new EmployeeAccessRequestException("employee_ids_required", "Choose at least one employee.");
        if (ids.Count > MaxBulk) throw new EmployeeAccessRequestException("too_many", $"Choose at most {MaxBulk} employees at a time.");
        if (caller.UserId is not Guid callerId) throw new EmployeeAccessRequestException("caller_unknown", "Sign in again.");

        var nowUtc = DateTime.UtcNow;
        // F1: an active login is reset only one at a time.
        if (ids.Count > 1)
        {
            var states = await EmployeeAccessStates.EvaluateAsync(_db, tenantId, ids, nowUtc, ct);
            if (states.Values.Any(s => s.State.State is EmployeeAccessStates.Active or EmployeeAccessStates.CodeGiven && s.Facts.Login is { IsActive: true }))
                throw new EmployeeAccessRequestException("reset_is_single",
                    "Someone who already uses KynexOne is reset one person at a time. Remove them from the selection.");
        }
        var single = ids.Count == 1;
        var callerCeiling = await PrivilegeCeilingGraph.TryLoadCallerAsync(_db, tenantId, callerId, ct)
            ?? throw new EmployeeAccessRequestException("caller_unknown", "Sign in again.");
        var tenantSlug = await _db.Tenants.AsNoTracking().Where(t => t.Id == tenantId).Select(t => t.Slug).FirstAsync(ct);

        var issued = new List<Pending>();
        var skipped = new List<SkippedCodeDto>();
        foreach (var employeeId in ids)
        {
            var outcome = await IssueOneAsync(tenantId, tenantSlug, employeeId, scope, caller, callerId, callerCeiling, single,
                canIssue, canReset, ct);
            if (outcome.Skip is { } code) skipped.Add(new SkippedCodeDto(employeeId, code, outcome.Reason ?? SkipReason(code)));
            else issued.Add(outcome.Issued!);
        }

        // Delivery: by email to the login's username when a relay exists; otherwise the code comes back to the issuer,
        // and that disclosure makes them a credential handler for the two-person rule.
        var configured = issued.Count > 0 && !request.PrintOnly && await _email.IsConfiguredAsync(tenantId, ct);
        var items = new List<IssuedCodeDto>();
        var anyEmailed = false;
        var allEmailed = issued.Count > 0;
        foreach (var p in issued)
        {
            var sent = configured && await TryEmailAsync(tenantId, p, ct);
            if (sent) { items.Add(p.Item with { Code = null }); anyEmailed = true; continue; }
            allEmailed = false;
            _db.AuditLogs.Add(AuthAuditEntry.Create(Guid.NewGuid(), DateTime.UtcNow, DisclosedAction, "User", p.UserId.ToString(),
                caller with { TenantId = tenantId },
                JsonSerializer.Serialize(new { employeeId = p.Item.EmployeeId, expiresAtUtc = p.Item.ExpiresAtUtc, emailDeliveryConfigured = configured, resetOfActiveLogin = p.ResetOfActive })));
            items.Add(p.Item);
        }
        // Saved BEFORE any code leaves the server: no disclosure without its record.
        if (issued.Count > 0) await _db.SaveChangesAsync(ct);

        var message = issued.Count == 0
            ? "No codes were issued."
            : allEmailed
                ? "Each employee was emailed their sign-in code at their work email."
                : anyEmailed
                    ? "Some codes could not be emailed. Print the sign-in slips for those employees."
                    : request.PrintOnly
                        ? "Print the sign-in slips."
                    : configured
                        ? "The codes could not be emailed, so print the sign-in slips."
                        : "No email delivery is configured, so print the sign-in slips.";
        return new IssueCodesResponse(items, skipped, allEmailed, message);
    }

    private sealed record OneOutcome(string? Skip, Pending? Issued, string? Reason = null);

    private async Task<OneOutcome> IssueOneAsync(Guid tenantId, string tenantSlug, int employeeId, EntityScopeContext scope,
        RequestContext caller, Guid callerId, PrivilegeCeiling.Caller callerCeiling, bool single, bool canIssue, bool canReset,
        CancellationToken ct)
    {
        OneOutcome? result = null;

        async Task RunAsync(CancellationToken token)
        {
            _db.ChangeTracker.Clear();
            result = null;
            var nowUtc = DateTime.UtcNow;
            // The tenant row serialises issue, redeem and the seat count.
            var tenant = await _db.Tenants.TagWith(RowLockingInterceptor.ForUpdateTag)
                .SingleOrDefaultAsync(t => t.Id == tenantId && t.IsActive, token);
            var employee = await ScopedBypass.NullableTenantWide(_db.Employees, tenantId, Why).TagWith(RowLockingInterceptor.ForUpdateTag)
                .SingleOrDefaultAsync(e => e.Id == employeeId && !e.IsDeleted, token);
            if (tenant is null || employee is null || !scope.CanAccessCompany(employee.CompanyId))
            { result = new(Skip.NotFound, null); return; }

            var (facts, state) = (await EmployeeAccessStates.EvaluateAsync(_db, tenantId, new[] { employeeId }, nowUtc, token))[employeeId];
            switch (state.State)
            {
                case EmployeeAccessStates.WaitingForWorkEmail: result = new(EmployeeAccessStates.WaitingForWorkEmail, null); return;
                case EmployeeAccessStates.Stopped: result = new(Skip.Stopped, null); return;
                case EmployeeAccessStates.Blocked: result = new(Skip.Blocked, null, state.BlockedReason); return;
            }
            if (!AuthCurrentEligibility.IsEmployeeLifecycleEligible(employee.Status)) { result = new(Skip.Blocked, null, SkipReason(Skip.NotActive)); return; }
            // A login already in use (active, or active with a live reset code) is a RESET (F1), whatever the state shows.
            var resetOfActive = facts.Login is { IsActive: true };
            if (resetOfActive ? !canReset : !canIssue) { result = new(Skip.ResetNeedsPermission, null); return; }
            if (await WorkEmailSetterRule.IsCallerSetterAsync(_db, tenantId, employeeId, callerId, token))
            { result = new(WorkEmailSetterRule.SetByCallerCode, null); return; }

            // A pre-existing employee with no login yet: stage it now (the provisioner is idempotent).
            if (facts.Link is null)
            {
                var staged = await new EmployeeLoginProvisioner(_db).EnsureStagedLoginAsync(tenantId, employee, caller with { TenantId = tenantId }, token);
                if (staged.Result == EmployeeLoginProvisioner.Results.Blocked)
                { result = new(Skip.Blocked, null, EmployeeAccessStates.BlockedReason(staged.BlockedCode!, facts.CompanyDomain)); return; }
                await _db.SaveChangesAsync(token);
            }

            var link = await ScopedBypass.TenantWide(_db.EmployeeUserAccounts, tenantId, Why).TagWith(RowLockingInterceptor.ForUpdateTag)
                .Where(x => x.EmployeeId == employeeId && !x.IsDeleted)
                .OrderByDescending(x => x.IsPrimary).ThenByDescending(x => x.CreatedAtUtc)
                .FirstAsync(token);
            var userId = link.UserId!.Value;
            await ScopedBypass.TenantWide(_db.Users, tenantId, Why).TagWith(RowLockingInterceptor.ForUpdateTag)
                .Where(u => u.Id == userId).Select(u => u.Id).ToListAsync(token);
            var user = (await PrivilegeCeilingGraph.LoadUsersAsync(_db, tenantId, new Guid[] { userId }, token)).Single();

            if (user.Id == callerId) { result = new(Skip.SelfIssue, null); return; }
            if (PrivilegeCeiling.AboveCallerRefusal(callerCeiling, PrivilegeCeilingGraph.HoldsAdmin(user, tenantId), AuthService.GetPermissions(user)) is not null)
            { result = new(Skip.AboveCeiling, null); return; }
            var privileged = await EmployeeLoginPrivilege.IsPrivilegedAsync(_db, tenantId, user, link, employeeId, nowUtc, token);
            if (privileged && !(single && canReset)) { result = new(Skip.Privileged, null); return; }
            if (!resetOfActive && await SeatsFullAsync(tenantId, userId, nowUtc, token)) { result = new(Skip.SeatLimit, null); return; }

            // ONE LIVE CREDENTIAL (F3): the code supersedes any invitation, earlier code and unused reset link.
            var code = WelcomeCodes.Generate();
            var expires = WelcomeCodes.ExpiresAt(nowUtc, employee.JoiningDate);
            link.WelcomeCodeHash = WelcomeCodes.Hash(_codeKey, link.Id, user.NormalizedEmail, code);
            link.WelcomeCodeIssuedAtUtc = nowUtc;
            link.WelcomeCodeExpiresAtUtc = expires;
            link.WelcomeCodeIssuedBy = callerId;
            link.WelcomeCodeFailedAttempts = 0;
            link.WelcomeCodeRedeemedAtUtc = null;
            link.InvitationTokenHash = string.Empty;
            link.InvitationExpiresAtUtc = null;
            foreach (var reset in await _db.PasswordResetTokens.TagWith(RowLockingInterceptor.ForUpdateTag)
                         .Where(x => x.UserId == userId && x.UsedAtUtc == null).OrderBy(x => x.Id).ToListAsync(token))
                reset.UsedAtUtc = nowUtc;
            _db.AuditLogs.Add(AuthAuditEntry.Create(Guid.NewGuid(), nowUtc, IssuedAction, "EmployeeUserAccount", link.Id.ToString(),
                caller with { TenantId = tenantId },
                JsonSerializer.Serialize(new { employeeId, userId, expiresAtUtc = expires, resetOfActiveLogin = resetOfActive, privilegedLogin = privileged, superseded = state.State == EmployeeAccessStates.CodeGiven })));
            await _db.SaveChangesAsync(token);

            result = new(null, new Pending(new IssuedCodeDto(employeeId, facts.EmployeeName, facts.ArabicName, facts.EmployeeCode,
                user.Email, facts.Department, facts.Site, code, expires, tenantSlug), userId, user.Email, resetOfActive));
        }

        if (_db.Database.IsRelational())
        {
            var strategy = _db.Database.CreateExecutionStrategy();
            await strategy.ExecuteAsync(async () =>
            {
                await using var tx = await _db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
                await RunAsync(ct);
                await tx.CommitAsync(ct);
            });
        }
        else
        {
            await RunAsync(ct);
        }
        _db.ChangeTracker.Clear();
        return result!;
    }

    /// <summary>F9: active logins + live codes on logins not yet active ≥ MaxUsers. <paramref name="targetUserId"/> is excluded (a reissue takes no extra seat).</summary>
    private async Task<bool> SeatsFullAsync(Guid tenantId, Guid targetUserId, DateTime nowUtc, CancellationToken ct)
    {
        var max = await _db.TenantSubscriptions.AsNoTracking().Where(s => s.TenantId == tenantId).Select(s => (int?)s.MaxUsers).FirstOrDefaultAsync(ct);
        if (max is not > 0) return false;
        var used = await CountSeatsAsync(_db, tenantId, nowUtc, excludeUserId: targetUserId, ct);
        return used >= max.Value;
    }

    /// <summary>Seats in use: active logins, plus logins holding a live welcome code (they will be active).</summary>
    public static async Task<int> CountSeatsAsync(ZayraDbContext db, Guid tenantId, DateTime nowUtc, Guid? excludeUserId, CancellationToken ct)
    {
        var active = ScopedBypass.TenantWide(db.Users, tenantId, Why).AsNoTracking()
            .Where(u => u.IsActive && !u.IsDeleted && u.Id != excludeUserId);
        if (!EssOnlyLoginsUseASeat) active = active.Where(u => u.AccessMode != AccessModes.EssOnly);
        var activeCount = await active.CountAsync(ct);
        var pending = await ScopedBypass.TenantWide(db.EmployeeUserAccounts, tenantId, Why).AsNoTracking()
            .Where(l => !l.IsDeleted && l.WelcomeCodeHash != null && l.WelcomeCodeRedeemedAtUtc == null
                && l.WelcomeCodeExpiresAtUtc > nowUtc && l.WelcomeCodeFailedAttempts < WelcomeCodes.BurnAt
                && l.UserId != null && l.UserId != excludeUserId
                && l.User != null && !l.User.IsActive && !l.User.IsDeleted)
            .Select(l => l.UserId).Distinct().CountAsync(ct);
        return activeCount + (EssOnlyLoginsUseASeat ? pending : 0);
    }

    private async Task<bool> TryEmailAsync(Guid tenantId, Pending p, CancellationToken ct)
    {
        var grouped = $"{p.Item.Code![..4]} {p.Item.Code[4..]}";
        var welcome = string.IsNullOrEmpty(_appUrl) ? "/welcome" : $"{_appUrl}/welcome";
        static string enc(string? v) => System.Web.HttpUtility.HtmlEncode(v ?? string.Empty);
        var html = $"""
            <p>Hello {enc(p.Item.EmployeeName)},</p>
            <p>HR has given you access to KynexOne, your company's HR self-service.</p>
            <p>Your username: <strong>{enc(p.Item.Username)}</strong><br/>
            Your welcome code: <strong style="font-size:18px;letter-spacing:2px">{grouped}</strong><br/>
            Use it by: {p.Item.ExpiresAtUtc:dd MMMM yyyy}</p>
            <p>Open <a href="{enc(welcome)}">{enc(welcome)}</a>, enter your email and the code, then choose your own password.</p>
            <p>Use once. Don't share it. HR will never ask for your password.</p>
            <hr/><p style="font-size:12px;color:#666">KynexOne Workforce</p>
            """;
        try
        {
            var delivery = await _email.DeliverAsync(tenantId, p.Email, p.Item.EmployeeName, "Your KynexOne welcome code", html, cancellationToken: ct);
            return delivery.ReachedARelay;
        }
        catch (Exception)
        {
            return false; // the code is committed; it falls back to the printed slip
        }
    }

    // ── POST work-emails (backfill from IT) ─────────────────────────────────────────────────────────────

    public async Task<WorkEmailBackfillResponse> BackfillWorkEmailsAsync(Guid tenantId, WorkEmailBackfillRequest request,
        EntityScopeContext scope, RequestContext caller, CancellationToken ct)
    {
        var rows = request.Rows ?? Array.Empty<WorkEmailBackfillRow>();
        if (rows.Count == 0) throw new EmployeeAccessRequestException("rows_required", "Add at least one row.");
        if (rows.Count > MaxBackfillRows) throw new EmployeeAccessRequestException("too_many", $"Send at most {MaxBackfillRows} rows at a time.");

        var plan = await PlanBackfillAsync(tenantId, rows, scope, ct);
        if (request.DryRun || plan.Matched.Count == 0)
            return new WorkEmailBackfillResponse(plan.Matched, plan.NotFound, plan.WrongDomain, plan.Conflicts, 0);

        // Apply in chunks of 200, one transaction each, idempotent per row; each chunk re-validates under lock.
        var saved = 0;
        var lateConflicts = new List<BackfillConflictDto>();
        foreach (var chunk in plan.Matched.Chunk(BackfillChunk))
        {
            var (chunkSaved, chunkConflicts) = await ApplyBackfillChunkAsync(tenantId, chunk, scope, caller, ct);
            saved += chunkSaved;
            lateConflicts.AddRange(chunkConflicts);
        }
        var failed = lateConflicts.Select(c => c.EmployeeCode).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return new WorkEmailBackfillResponse(
            plan.Matched.Where(m => !failed.Contains(m.EmployeeCode)).ToList(),
            plan.NotFound, plan.WrongDomain, plan.Conflicts.Concat(lateConflicts).ToList(), saved);
    }

    private sealed record BackfillPlan(List<BackfillMatchDto> Matched, List<string> NotFound, List<BackfillWrongDomainDto> WrongDomain, List<BackfillConflictDto> Conflicts);

    private async Task<BackfillPlan> PlanBackfillAsync(Guid tenantId, IReadOnlyList<WorkEmailBackfillRow> rows, EntityScopeContext scope, CancellationToken ct)
    {
        var matched = new List<BackfillMatchDto>();
        var notFound = new List<string>();
        var wrongDomain = new List<BackfillWrongDomainDto>();
        var conflicts = new List<BackfillConflictDto>();

        var cleaned = rows.Select(r => (Code: (r.EmployeeCode ?? string.Empty).Trim(), Email: (r.WorkEmail ?? string.Empty).Trim())).ToList();
        // In-file duplicates (same employee number or same email) are refused outright (F7).
        var dupCodes = cleaned.GroupBy(r => r.Code, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var dupEmails = cleaned.Where(r => r.Email.Length > 0).GroupBy(r => AuthService.Normalize(r.Email)).Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet();

        var codes = cleaned.Select(r => r.Code).Where(c => c.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var employees = await ScopedBypass.NullableTenantWide(_db.Employees, tenantId, Why).AsNoTracking()
            .Where(e => codes.Contains(e.EmployeeCode) && !e.IsDeleted)
            .Select(e => new { e.Id, e.EmployeeCode, e.FullName, e.WorkEmail, e.CompanyId })
            .ToListAsync(ct);
        var byCode = employees.Where(e => scope.CanAccessCompany(e.CompanyId))
            .GroupBy(e => e.EmployeeCode, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var companyIds = employees.Where(e => e.CompanyId.HasValue).Select(e => e.CompanyId!.Value).Distinct().ToList();
        var domains = await ScopedBypass.TenantWide(_db.Companies, tenantId, Why).AsNoTracking()
            .Where(c => companyIds.Contains(c.Id)).ToDictionaryAsync(c => c.Id, c => (c.EmailDomain ?? string.Empty).Trim().ToLowerInvariant(), ct);
        var states = await EmployeeAccessStates.EvaluateAsync(_db, tenantId, byCode.Values.Select(e => e.Id).ToList(), DateTime.UtcNow, ct);

        // Addresses taken by OTHER employees or by logins.
        var wanted = cleaned.Where(r => r.Email.Length > 0).Select(r => AuthService.Normalize(r.Email)).Distinct().ToList();
        var takenByEmployees = (await ScopedBypass.NullableTenantWide(_db.Employees, tenantId, Why).AsNoTracking()
                .Where(e => !e.IsDeleted && e.WorkEmail != "")
                .Select(e => new { e.Id, e.WorkEmail }).ToListAsync(ct))
            .Where(e => wanted.Contains(AuthService.Normalize(e.WorkEmail)))
            .GroupBy(e => AuthService.Normalize(e.WorkEmail)).ToDictionary(g => g.Key, g => g.Select(x => x.Id).ToList());
        var takenByLogins = await ScopedBypass.TenantWide(_db.Users, tenantId, Why).AsNoTracking()
            .Where(u => wanted.Contains(u.NormalizedEmail)).Select(u => new { u.Id, u.NormalizedEmail }).ToListAsync(ct);

        foreach (var (code, email) in cleaned)
        {
            if (code.Length == 0 || !byCode.TryGetValue(code, out var e)) { notFound.Add(code); continue; }
            if (dupCodes.Contains(code)) { conflicts.Add(new(code, email, "duplicate_in_file")); continue; }
            if (email.Length == 0) { conflicts.Add(new(code, email, "work_email_missing")); continue; }
            var norm = AuthService.Normalize(email);
            if (dupEmails.Contains(norm)) { conflicts.Add(new(code, email, "duplicate_in_file")); continue; }
            if (WorkEmailPlusAddressException.IsPlusAddressed(email)) { conflicts.Add(new(code, email, WorkEmailPlusAddressException.Code)); continue; }
            var domain = e.CompanyId is Guid cid && domains.TryGetValue(cid, out var d) ? d : string.Empty;
            if (domain.Length == 0) { conflicts.Add(new(code, email, EmployeeLoginProvisioner.BlockedCodes.CompanyEmailDomainMissing)); continue; }
            if (!EmployeeLoginProvisioner.IsOnDomain(email, domain)) { wrongDomain.Add(new(code, email, domain)); continue; }
            if (string.Equals(AuthService.Normalize(e.WorkEmail ?? string.Empty), norm, StringComparison.Ordinal))
            { matched.Add(new(e.Id, e.EmployeeCode, e.FullName, e.WorkEmail ?? string.Empty, email.ToLowerInvariant())); continue; } // idempotent re-run
            if (takenByEmployees.TryGetValue(norm, out var others) && others.Any(id => id != e.Id))
            { conflicts.Add(new(code, email, "email_used_by_another_employee")); continue; }
            var st = states.TryGetValue(e.Id, out var s) ? s.State.State : EmployeeAccessStates.WaitingForWorkEmail;
            var ownLogin = states.TryGetValue(e.Id, out var s2) ? s2.Facts.Login?.UserId : null;
            if (takenByLogins.Any(u => u.NormalizedEmail == norm && u.Id != ownLogin))
            { conflicts.Add(new(code, email, EmployeeLoginProvisioner.BlockedCodes.EmailBelongsToExistingLogin)); continue; }
            if (st == EmployeeAccessStates.Active) { conflicts.Add(new(code, email, "username_differs")); continue; }
            matched.Add(new(e.Id, e.EmployeeCode, e.FullName, e.WorkEmail ?? string.Empty, email.ToLowerInvariant()));
        }
        return new BackfillPlan(matched, notFound, wrongDomain, conflicts);
    }

    private async Task<(int Saved, List<BackfillConflictDto> Conflicts)> ApplyBackfillChunkAsync(
        Guid tenantId, IReadOnlyList<BackfillMatchDto> chunk, EntityScopeContext scope, RequestContext caller, CancellationToken ct)
    {
        var saved = 0;
        var conflicts = new List<BackfillConflictDto>();

        async Task RunAsync(CancellationToken token)
        {
            _db.ChangeTracker.Clear();
            saved = 0;
            conflicts.Clear();
            var nowUtc = DateTime.UtcNow;
            var ids = chunk.Select(m => m.EmployeeId).ToList();
            var employees = await ScopedBypass.NullableTenantWide(_db.Employees, tenantId, Why).TagWith(RowLockingInterceptor.ForUpdateTag)
                .Where(e => ids.Contains(e.Id) && !e.IsDeleted).OrderBy(e => e.Id).ToListAsync(token);
            // Re-validate under the lock: the plan was read without one.
            var replan = await PlanBackfillAsync(tenantId, chunk.Select(m => new WorkEmailBackfillRow(m.EmployeeCode, m.NewEmail)).ToList(), scope, token);
            conflicts.AddRange(replan.Conflicts);
            conflicts.AddRange(replan.WrongDomain.Select(w => new BackfillConflictDto(w.EmployeeCode, w.WorkEmail, EmployeeLoginProvisioner.BlockedCodes.WrongDomain)));
            var ctx = caller with { TenantId = tenantId };
            var changed = new List<Employee>();
            foreach (var m in replan.Matched)
            {
                var employee = employees.First(e => e.Id == m.EmployeeId);
                var prior = employee.WorkEmail ?? string.Empty;
                if (string.Equals(AuthService.Normalize(prior), AuthService.Normalize(m.NewEmail), StringComparison.Ordinal))
                { changed.Add(employee); continue; }
                employee.WorkEmail = m.NewEmail;
                employee.UpdatedAtUtc = nowUtc;
                employee.UpdatedBy = caller.UserId;
                // Active logins keep their username; staged ones follow (and lose any code). Audited as a work-email change.
                var login = await WorkEmailLoginGuard.ApplyAsync(_db, employee, tenantId, prior, ctx, nowUtc, token);
                if (login.LoginUsernameDiffers)
                {
                    // Re-validation found the login active after all: do not change this row.
                    employee.WorkEmail = prior;
                    conflicts.Add(new(m.EmployeeCode, m.NewEmail, "username_differs"));
                    continue;
                }
                _db.AuditLogs.Add(AuthAuditEntry.Create(Guid.NewGuid(), nowUtc, "employee.work_email_backfilled", "Employee",
                    employee.Id.ToString(System.Globalization.CultureInfo.InvariantCulture), ctx,
                    JsonSerializer.Serialize(new { oldWorkEmail = prior, newWorkEmail = m.NewEmail, source = "backfill" })));
                changed.Add(employee);
                saved++;
            }
            await _db.SaveChangesAsync(token);
            await new EmployeeLoginProvisioner(_db).EnsureStagedLoginsAsync(tenantId, changed, ctx, token);
            await _db.SaveChangesAsync(token);
        }

        if (_db.Database.IsRelational())
        {
            var strategy = _db.Database.CreateExecutionStrategy();
            await strategy.ExecuteAsync(async () =>
            {
                await using var tx = await _db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
                await RunAsync(ct);
                await tx.CommitAsync(ct);
            });
        }
        else
        {
            await RunAsync(ct);
        }
        _db.ChangeTracker.Clear();
        return (saved, conflicts);
    }
}
