using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Approvals;
using Zayra.Api.Application.Jawazat;
using Zayra.Api.Data;
using Zayra.Api.Models;
using Zayra.Api.Infrastructure.Data;

namespace Zayra.Api.Infrastructure.Jawazat;

public sealed class JawazatWorkflowService(ZayraDbContext db, IApprovalRouter router, IJawazatProvider provider) : IJawazatWorkflowService
{
    public Task<JawazatCapabilities> GetCapabilitiesAsync(CancellationToken ct) => provider.GetCapabilitiesAsync(ct);

    public async Task<JawazatPolicyDto> GetPolicyAsync(JawazatActor actor, int? employeeId, CancellationToken ct)
    {
        var employee = await ResolveEmployeeAsync(actor, employeeId, ct);
        var snapshot = await ResolvePolicyAsync(actor.TenantId, employee.CompanyId!.Value, ct, allowNotificationFallback: true);
        return new(snapshot.ProfileId, snapshot.CompanyId, snapshot.EffectiveFrom, snapshot.EffectiveTo, snapshot.Policy,
            snapshot.ProfileId != Guid.Empty && snapshot.Policy.EmployerAssistedEnabled, employee.UserAccountId == actor.UserId);
    }

    public async Task<JawazatEvaluation> EvaluateAsync(JawazatActor actor, JawazatCreateRequest request, CancellationToken ct)
    {
        var employee = await ResolveEmployeeAsync(actor, request.EmployeeId, ct);
        var snapshot = await ResolvePolicyAsync(actor.TenantId, employee.CompanyId!.Value, ct, request.Route == JawazatConstants.WorkerNotification);
        ValidateRequest(request, snapshot.Policy);
        RequireRoute(actor, employee, request, snapshot.Policy);
        return new(snapshot, EvaluateChecks(employee, request, snapshot.Policy), false);
    }

    public async Task<JawazatRequestDto> CreateAsync(JawazatActor actor, JawazatCreateRequest request, CancellationToken ct)
    {
        if (!db.Database.IsRelational() || db.Database.CurrentTransaction is not null)
            return await CreateCoreAsync(actor, request, ct);
        var attempt = 0;
        return await db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            if (attempt++ > 0) db.ChangeTracker.Clear();
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            var result = await CreateCoreAsync(actor, request, ct);
            await tx.CommitAsync(ct);
            return result;
        });
    }

    private async Task<JawazatRequestDto> CreateCoreAsync(JawazatActor actor, JawazatCreateRequest request, CancellationToken ct)
    {
        if (request.IdempotencyKey == Guid.Empty) throw new JawazatException("idempotency_key_required", "A non-empty idempotency key is required.", 400);
        var employee = await ResolveEmployeeAsync(actor, request.EmployeeId, ct);
        // Serialize duplicate creates across pods using the existing employee row. The lock is
        // inside the retrying transaction; no new lock/queue table or in-process correctness claim.
        if (db.Database.IsNpgsql())
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({$"jawazat:{actor.TenantId}:{employee.Id}"}, 0))", ct);
        var prior = await db.HRRequests.Where(x => x.TenantId == actor.TenantId && x.EmployeeId == employee.Id
            && x.CreatedBy == actor.UserId && x.JawazatDataJson != null).ToListAsync(ct);
        foreach (var row in prior)
        {
            var data = JawazatJson.Read(row.JawazatDataJson!);
            if (data.IdempotencyKey != request.IdempotencyKey) continue;
            if (data.Route != request.Route || data.Service != request.Service || data.DepartureDate != request.DepartureDate
                || data.ReturnDate != request.ReturnDate || data.Reason != request.Reason?.Trim())
                throw new JawazatException("idempotency_key_reused", "This key already identifies a different Jawazat request.");
            await RequireAccessAsync(actor, row, ct);
            return ToDto(row);
        }

        var snapshot = await ResolvePolicyAsync(actor.TenantId, employee.CompanyId!.Value, ct, request.Route == JawazatConstants.WorkerNotification);
        ValidateRequest(request, snapshot.Policy);
        RequireRoute(actor, employee, request, snapshot.Policy);
        var checks = EvaluateChecks(employee, request, snapshot.Policy);
        if (request.Route == JawazatConstants.EmployerAssisted && checks.Any(x => x.Result == "Failed"))
            throw new JawazatException("local_policy_failed", "Resolve the failed local checks before requesting employer-assisted issuance.", 422);
        ApprovalRoute? route = null;
        if (request.Route == JawazatConstants.EmployerAssisted)
        {
            route = await router.ResolveAsync(actor.TenantId, employee.Id, JawazatConstants.ApprovalEntityName, ct);
            if (route.Steps.Any(s => s.ApproverType != "Role" || !JawazatConstants.IsHrRole(s.ApproverRole)))
                throw new JawazatException("hr_workflow_required", "Jawazat employer-assisted requests require a workflow containing only HR role steps.", 422);
        }

        var notification = request.Route == JawazatConstants.WorkerNotification;
        var ticket = new HRRequest
        {
            TenantId = actor.TenantId, CompanyId = employee.CompanyId, EmployeeId = employee.Id,
            CategoryName = "Jawazat", Subject = notification ? "Exit/re-entry: employee notification" : "Exit/re-entry: HR review",
            Description = request.Reason.Trim(), CreatedBy = actor.UserId,
            Status = notification ? "Closed" : "InProgress", DueAtUtc = DateTime.UtcNow.AddHours(48), WorkflowVersion = 1,
            JawazatDataJson = JawazatJson.Serialize(new JawazatRequestData(1, request.Route, request.Service,
                request.DepartureDate, request.ReturnDate, request.Reason.Trim(), request.IdempotencyKey,
                notification ? "NotificationRecorded" : "PendingApproval", "NotSubmitted", snapshot, checks))
        };
        if (route is not null)
        {
            var first = route.FirstStep;
            var approval = new ApprovalRequest
            {
                TenantId = actor.TenantId, CompanyId = employee.CompanyId, WorkflowId = route.WorkflowId,
                EntityName = JawazatConstants.ApprovalEntityName, EntityId = ticket.Id.ToString(), Title = ticket.Subject,
                RequestedByUserId = actor.UserId, RequestedForEmployeeId = employee.Id, CurrentStepOrder = first.StepOrder,
                CurrentApproverRole = first.ApproverRole, CurrentApproverType = "Role", CurrentQueue = $"Role:{first.ApproverRole}",
                SlaHours = Math.Clamp(first.EscalationAfterHours ?? 48, 1, 720), LastRoutedAtUtc = DateTime.UtcNow
            };
            approval.DueAtUtc = DateTime.UtcNow.AddHours(approval.SlaHours);
            ticket.ApprovalRequestId = approval.Id;
            db.ApprovalRequests.Add(approval);
        }
        db.HRRequests.Add(ticket);
        db.HRRequestComments.Add(new HRRequestComment
        {
            TenantId = actor.TenantId, HRRequestId = ticket.Id, EmployeeId = employee.Id, UserId = actor.UserId,
            AuthorType = "System", AuthorName = "Jawazat workflow",
            Comment = notification ? "Employee notification recorded. This is not employer consent or proof of government eligibility or issuance."
                : "Submitted for internal HR approval. Government verification and issuance are separate."
        });
        db.EmployeeNotifications.Add(new EmployeeNotification
        {
            TenantId = actor.TenantId, EmployeeId = employee.Id, NotificationType = "Info",
            Title = notification ? "Travel notification recorded" : "Exit/re-entry request sent to HR",
            Body = notification ? "Your notification is recorded. This does not issue a visa or confirm eligibility." : "Your request is awaiting the configured HR approval workflow."
        });
        await db.SaveChangesAsync(ct);
        return ToDto(ticket);
    }

    public async Task<JawazatRequestDto> GetAsync(JawazatActor actor, Guid id, CancellationToken ct) => ToDto(await LoadAsync(actor, id, ct));

    public async Task<IReadOnlyList<JawazatRequestDto>> ListAsync(JawazatActor actor, CancellationToken ct)
    {
        var ownId = await ResolveOwnIdAsync(actor, ct);
        var query = db.HRRequests.AsNoTracking().Where(x => x.TenantId == actor.TenantId && x.JawazatDataJson != null);
        if (!actor.CanManage)
        {
            if (ownId is null) return Array.Empty<JawazatRequestDto>();
            query = query.Where(x => x.EmployeeId == ownId);
        }
        else
        {
            var companyIds = actor.CompanyScope.AccessibleCompanyIds;
            var employeeIds = actor.EmployeeScope.AllowedEmployeeIds;
            query = query.Where(x => x.EmployeeId == ownId || (x.CompanyId != null
                && (actor.CompanyScope.IsGroupLevel || companyIds.Contains(x.CompanyId.Value))
                && (actor.EmployeeScope.IsUnrestricted || employeeIds!.Contains(x.EmployeeId))));
        }
        var rows = await query.OrderByDescending(x => x.CreatedAtUtc).Take(200).ToListAsync(ct);
        return rows.Select(ToDto).ToList();
    }

    public async Task<JawazatRequestDto> SubmitAsync(JawazatActor actor, Guid id, CancellationToken ct)
    {
        var row = await LoadAsync(actor, id, ct);
        RequireHrCompany(actor, row);
        var data = JawazatJson.Read(row.JawazatDataJson!);
        if (data.Route != JawazatConstants.EmployerAssisted || data.InternalState != "Approved")
            throw new JawazatException("not_approved", "Only an HR-approved employer-assisted request can attempt provider submission.");
        var employee = await ScopedBypass.NullableTenantWide(db.Employees, actor.TenantId,
            "Jawazat transfer integrity: tenant and saved employee ID bound the read; frozen company authorization was checked before this read.")
            .AsNoTracking().SingleOrDefaultAsync(e => e.Id == row.EmployeeId && !e.IsDeleted, ct);
        if (employee is null || employee.CompanyId != row.CompanyId)
            throw new JawazatException("company_changed", "The employee company changed. Create a request under the current company policy.");
        // The provider is deliberately disabled. No configured adapter is allowed to turn this
        // foundation into a live gateway without a separately reviewed submission implementation.
        var capabilities = await provider.GetCapabilitiesAsync(ct);
        if (capabilities.IsAvailable)
            throw new JawazatException("live_submission_not_implemented", "Live provider submission is not enabled in this release.", 422);
        if (data.ProviderState == "ProviderUnavailable") return ToDto(row);
        var result = await provider.VerifyAsync(data, ct);
        row.JawazatDataJson = JawazatJson.Serialize(data with
        {
            ProviderState = "ProviderUnavailable", LastProviderAttemptAtUtc = DateTime.UtcNow,
            ProviderMessage = capabilities.Message,
            Checks = data.Checks.Where(c => !c.Code.StartsWith("government_", StringComparison.Ordinal)).Concat(result.Checks).ToList()
        });
        row.WorkflowVersion++;
        await db.SaveChangesAsync(ct);
        return ToDto(row);
    }

    public async Task<JawazatRequestDto> ReconcileAsync(JawazatActor actor, Guid id, CancellationToken ct)
    {
        var row = await LoadAsync(actor, id, ct);
        RequireHrCompany(actor, row);
        // No government operation has been sent by this release, therefore there is no outcome to
        // reconcile. Reusing submit records honest unavailability without inventing a reference.
        return await SubmitAsync(actor, id, ct);
    }

    private async Task<HRRequest> LoadAsync(JawazatActor actor, Guid id, CancellationToken ct)
    {
        var row = await db.HRRequests.FirstOrDefaultAsync(x => x.TenantId == actor.TenantId && x.Id == id && x.JawazatDataJson != null, ct)
            ?? throw new JawazatException("not_found", "Jawazat request not found.", 404);
        await RequireAccessAsync(actor, row, ct);
        return row;
    }

    private async Task RequireAccessAsync(JawazatActor actor, HRRequest row, CancellationToken ct)
    {
        if (row.TenantId != actor.TenantId || row.CompanyId is null) throw new JawazatException("forbidden", "Request access denied.", 403);
        var ownId = await ResolveOwnIdAsync(actor, ct);
        if (ownId == row.EmployeeId) return; // Employees retain their own historical requests after a transfer.
        RequireHrCompany(actor, row);
    }

    private static void RequireHrCompany(JawazatActor actor, HRRequest row)
    {
        if (!actor.CanManage || row.CompanyId is null || !actor.CompanyScope.CanAccessCompany(row.CompanyId)
            || !actor.EmployeeScope.CanAccessEmployee(row.EmployeeId))
            throw new JawazatException("forbidden", "HR access to the request's original company and employee is required.", 403);
    }

    private async Task<int?> ResolveOwnIdAsync(JawazatActor actor, CancellationToken ct)
    {
        var ids = await ScopedBypass.NullableTenantWide(db.Employees, actor.TenantId,
            "Jawazat own identity: account and tenant bounded identity-only lookup detects duplicate links across companies and preserves own transfer history.")
            .AsNoTracking().Where(e => e.UserAccountId == actor.UserId && !e.IsDeleted).Select(e => e.Id).Take(2).ToListAsync(ct);
        if (ids.Count > 1) throw new JawazatException("ambiguous_identity", "More than one employee is linked to this account. HR must correct the account linkage.", 403);
        return ids.Count == 1 ? ids[0] : null;
    }

    private async Task<Employee> ResolveEmployeeAsync(JawazatActor actor, int? employeeId, CancellationToken ct)
    {
        if (actor.TenantId == Guid.Empty || actor.UserId == Guid.Empty) throw new JawazatException("unauthorized", "An authenticated tenant account is required.", 401);
        var ownId = await ResolveOwnIdAsync(actor, ct);
        var id = employeeId ?? ownId ?? throw new JawazatException("employee_link_required", "A unique employee account link is required.", 422);
        var employee = await ScopedBypass.NullableTenantWide(db.Employees, actor.TenantId,
            "Jawazat subject lookup: tenant and exact employee ID are bounded; unique owner or HR company and employee authorization is required before use.")
            .AsNoTracking().SingleOrDefaultAsync(e => e.Id == id && !e.IsDeleted, ct)
            ?? throw new JawazatException("employee_not_found", "Employee not found.", 404);
        if (employee.CompanyId is null) throw new JawazatException("company_required", "The employee must belong to a company.", 422);
        if (id != ownId && (!actor.CanManage || !actor.EmployeeScope.CanAccessEmployee(id) || !actor.CompanyScope.CanAccessCompany(employee.CompanyId)))
            throw new JawazatException("forbidden", "Employee access denied.", 403);
        return employee;
    }

    private async Task<JawazatPolicySnapshot> ResolvePolicyAsync(Guid tenantId, Guid companyId, CancellationToken ct, bool allowNotificationFallback = false)
    {
        var country = await db.Companies.AsNoTracking().Where(c => c.TenantId == tenantId && c.Id == companyId && !c.IsDeleted && c.IsActive)
            .Select(c => c.CountryCode).SingleOrDefaultAsync(ct);
        if (country is not ("SA" or "SAU")) throw new JawazatException("saudi_company_required", "Jawazat requests require an active Saudi company.", 422);
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var profiles = await db.CompanyComplianceProfiles.AsNoTracking().Where(p => p.TenantId == tenantId && !p.IsDeleted
            && p.Status == CompanyPolicyStatuses.Active && (p.CompanyId == companyId || p.CompanyId == null)
            && (p.CountryCode == "SA" || p.CountryCode == "SAU") && p.EffectiveFrom <= today
            && (p.EffectiveTo == null || p.EffectiveTo >= today)).ToListAsync(ct);
        var candidates = profiles.Any(p => p.CompanyId == companyId) ? profiles.Where(p => p.CompanyId == companyId).ToList() : profiles;
        // A company may gate its assisted-processing service. It cannot refuse receipt of a worker's
        // notification by withholding a policy. The empty profile reference records that distinction.
        if (allowNotificationFallback && (candidates.Count != 1 || JawazatPolicyRules.ValidateJson(candidates[0].JawazatPolicyJson) is not null
            || string.IsNullOrWhiteSpace(candidates[0].JawazatPolicyJson)))
            return new(Guid.Empty, companyId, today, null, DateTime.UtcNow, new JawazatPolicy(RuleVersion: "unconfigured-notification-only"));
        if (candidates.Count == 0) throw new JawazatException("policy_not_configured", "No active Jawazat compliance profile covers today for this company.", 422);
        // Overlapping policies require correction; choosing by insertion order would change authorization.
        if (candidates.Count != 1) throw new JawazatException("ambiguous_policy", "Multiple active compliance profiles cover today. Resolve the overlap before creating a request.", 422);
        var profile = candidates[0];
        return new(profile.Id, companyId, profile.EffectiveFrom, profile.EffectiveTo, DateTime.UtcNow, JawazatPolicyRules.Parse(profile.JawazatPolicyJson));
    }

    private static void ValidateRequest(JawazatCreateRequest request, JawazatPolicy policy)
    {
        if (request.Service != JawazatConstants.ExitReentryIssue) throw new JawazatException("unsupported_service", "Only exit/re-entry issuance requests are supported. Cancellation, extension and final exit are unavailable.", 400);
        if (request.Route is not (JawazatConstants.EmployerAssisted or JawazatConstants.WorkerNotification)) throw new JawazatException("unsupported_route", "Select employer-assisted or worker self-service notification.", 400);
        if (request.DepartureDate < DateOnly.FromDateTime(DateTime.UtcNow) || request.ReturnDate < request.DepartureDate)
            throw new JawazatException("invalid_dates", "Departure cannot be in the past and return cannot precede departure.", 400);
        if (string.IsNullOrWhiteSpace(request.Reason) || request.Reason.Length > 1000) throw new JawazatException("reason_required", "Provide a reason of 1 to 1000 characters.", 400);
    }

    private static void RequireRoute(JawazatActor actor, Employee employee, JawazatCreateRequest request, JawazatPolicy policy)
    {
        if (request.Route == JawazatConstants.EmployerAssisted && !policy.EmployerAssistedEnabled)
            throw new JawazatException("route_disabled", "Employer-assisted requests are disabled by company policy.", 422);
        if (request.Route == JawazatConstants.WorkerNotification && employee.UserAccountId != actor.UserId)
            throw new JawazatException("notification_not_allowed", "Only the employee may record their own worker self-service notification.", 403);
    }

    private static IReadOnlyList<JawazatCheck> EvaluateChecks(Employee employee, JawazatCreateRequest request, JawazatPolicy policy)
    {
        var now = DateTime.UtcNow;
        var today = DateOnly.FromDateTime(now);
        var checks = new List<JawazatCheck>
        {
            new("company_trip_duration", request.ReturnDate.DayNumber - request.DepartureDate.DayNumber + 1 <= policy.MaximumTripDays ? "Passed" : "Failed",
                "Company policy", now, $"Company travel review threshold: {policy.MaximumTripDays} days. This does not prevent recording a worker notification or decide government eligibility."),
            new("local_employment", employee.Status == EmployeeStatuses.Active ? "Passed" : "Failed", "Employee record", now, "Internal processing requires an active employee record; this is not a government status check."),
            new("local_passport_validity", employee.PassportExpiryDate is null ? "Unknown" : employee.PassportExpiryDate >= today.AddDays(policy.MinimumPassportValidityDays) ? "Passed" : "Failed",
                "Employee record", now, $"Local passport expiry must be at least {policy.MinimumPassportValidityDays} days from evaluation. Government acceptance remains unverified."),
            new("local_residence_trip_coverage", request.Route == JawazatConstants.EmployerAssisted || employee.IqamaExpiryDate is null
                    ? "Unknown" : employee.IqamaExpiryDate >= request.ReturnDate ? "Passed" : "Failed",
                "Employee record", now, request.Route == JawazatConstants.EmployerAssisted
                    ? "Employer-assisted residence requirements need route-specific government verification; trip coverage is not assumed to be a universal prohibition."
                    : "Worker-route local residence expiry is checked against the proposed return date as an advisory flag. It does not block recording this notification; government eligibility remains unverified.")
        };
        checks.AddRange(DisabledJawazatProvider.GovernmentChecks());
        return checks;
    }

    private static JawazatRequestDto ToDto(HRRequest row) => new(row.Id, row.EmployeeId,
        row.CompanyId ?? throw new JawazatException("company_required", "The request has no company scope."), row.Subject, row.Status,
        row.ApprovalRequestId, row.WorkflowVersion, row.CreatedAtUtc, JawazatJson.Read(row.JawazatDataJson!));
}
