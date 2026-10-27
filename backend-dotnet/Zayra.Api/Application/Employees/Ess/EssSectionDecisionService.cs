using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Approvals;
using Zayra.Api.Application.Auth;
using Zayra.Api.Application.Employees.Onboarding;
using Zayra.Api.Controllers;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.Auth;
using Zayra.Api.Infrastructure.Authorization;
using Zayra.Api.Infrastructure.Common;
using Zayra.Api.Infrastructure.Employees;
using Zayra.Api.Infrastructure.Employees.Onboarding;
using Zayra.Api.Models;

namespace Zayra.Api.Application.Employees.Ess;

/// <summary>
/// HR's decision on one submitted profile section (the section branch of the existing approve/reject routes).
///
/// <list type="bullet">
///   <item>Accept applies the section through <see cref="ProfileSectionRegistry"/> in ONE SaveChanges with its audit rows.</item>
///   <item><b>The bank section is never applied by this acceptance.</b> An Active employee gets an <see cref="EmployeeChangeRequest"/>
///     effective the next calendar day, decided through the approval workflow; the reviewing HR user is the submitter of that
///     request, so the requester bar keeps them from approving it, and the subject bar keeps the employee out. A hire not yet
///     in post has the figure written and the Finance pay gap re-opened (<see cref="CompensationApprovalGate"/>).</item>
///   <item>An identity number that fails its checksum can be accepted only with employees.override_validation and a reason.</item>
///   <item>Audit rows carry field names and a hash of the last four characters, never the values.</item>
/// </list>
/// </summary>
public sealed class EssSectionDecisionService(ZayraDbContext db, IApprovalWorkflowService approvals, IHttpContextAccessor http)
{
    public const string AcceptedAction = "ess.section.accepted";
    public const string WaiverAction = "employee.id_checksum_waived";
    public const string AwaitingFinance = "AwaitingFinance";

    private HttpContext Http => http.HttpContext!;

    public static Dictionary<string, string> ReadValues(string json)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (k, v) in JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json) ?? [])
        {
            if (k.StartsWith('_')) continue;
            result[k] = v.ValueKind switch { JsonValueKind.Null => string.Empty, JsonValueKind.String => v.GetString() ?? string.Empty, _ => v.ToString() };
        }
        return result;
    }

    public static IReadOnlyList<Guid> ReadDocumentIds(string json)
    {
        var root = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json) ?? [];
        if (!root.TryGetValue("_documents", out var arr) || arr.ValueKind != JsonValueKind.Array) return [];
        return arr.EnumerateArray().Select(x => Guid.TryParse(x.GetString(), out var g) ? g : Guid.Empty).Where(g => g != Guid.Empty).ToList();
    }

    public static string Last4Hash(string value)
    {
        var clean = value.Replace(" ", "");
        var last4 = clean.Length <= 4 ? clean : clean[^4..];
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(last4)))[..16].ToLowerInvariant();
    }

    public static NationalityClass? ClassOf(Employee e) =>
        !string.IsNullOrWhiteSpace(e.NationalityCode) ? NationalityCatalog.ClassOf(e.NationalityCode)
        : string.IsNullOrWhiteSpace(e.Nationality) ? null : NationalityCatalog.LegacyClassOf(e.Nationality);

    public async Task<string?> CompanyCountryAsync(Employee e, CancellationToken ct) =>
        e.CompanyId is null ? null : await db.Companies.AsNoTracking().Where(c => c.Id == e.CompanyId).Select(c => c.CountryCode).FirstOrDefaultAsync(ct);

    public async Task<IActionResult> ApproveAsync(EmployeeProfileChangeRequest change, Employee employee, ProfileChangeDecisionDto request,
        ClaimsPrincipal user, Guid? deciderId, CancellationToken ct)
    {
        var section = change.Section!;
        if (!EssSectionKeys.IsKnown(section)) return EssProblem.Create(Http, 400, EssProblemCodes.UnknownField);
        var tenantId = change.TenantId;
        var now = DateTime.UtcNow;
        var cls = ClassOf(employee);
        var country = await CompanyCountryAsync(employee, ct);
        var values = ReadValues(change.RequestedChangesJson);

        // Re-validate with today's rules: the value may predate a rule, or have arrived by another path.
        var errors = section == EssSectionKeys.Documents ? [] : SectionValidation.Validate(section, cls, country, values, requireComplete: true);
        var onlyChecksum = errors.Count > 0 && errors.All(e => e.Value.All(c => c == EmployeeFieldErrors.IdChecksumInvalid));
        var waived = false;
        var reason = request.OverrideReason?.Trim();
        var canOverride = user.EffectivePermissions().Contains(EmployeeInputGuard.OverridePermission, StringComparer.OrdinalIgnoreCase);
        if (errors.Count > 0)
        {
            if (!onlyChecksum) return EssProblem.Validation(Http, 422, EssProblemCodes.ValidationFailed, errors);
            if (string.IsNullOrEmpty(reason))
                return canOverride
                    ? EssProblem.Validation(Http, 422, EssProblemCodes.OverrideReasonRequired, errors)
                    : EssProblem.Validation(Http, 422, EssProblemCodes.ValidationFailed, errors);
            if (!canOverride) return EssProblem.Validation(Http, 422, EssProblemCodes.OverrideNotPermitted, errors);
            if (reason.Length is < 10 or > 500) return EssProblem.Validation(Http, 422, EssProblemCodes.OverrideReasonRequired, errors);
            waived = true;
        }

        var notes = request.Notes?.Trim();
        if (notes is { Length: > 500 }) return EssProblem.Create(Http, 400, EssProblemCodes.NoteRequired);
        var context = new RequestContext(Http.Connection.RemoteIpAddress?.ToString(), Http.Request.Headers.UserAgent.ToString(), deciderId, tenantId, Permissions: user.EffectivePermissions());
        var actionId = EmployeeCompletionController.ActionId(tenantId, employee.Id);
        var action = await db.EmployeeActionItems.FirstOrDefaultAsync(x => x.TenantId == tenantId && x.Id == actionId, ct);

        change.DecidedAtUtc = now;
        change.DecidedBy = deciderId;
        change.ReviewNote = notes;
        if (waived)
            db.AuditLogs.Add(AuthAuditEntry.Create(Guid.NewGuid(), now, WaiverAction, "Employee", employee.Id.ToString(System.Globalization.CultureInfo.InvariantCulture), context,
                JsonSerializer.Serialize(new { code = EmployeeFieldErrors.IdChecksumInvalid, reason, section, requestId = change.Id })));

        if (section == EssSectionKeys.Bank)
            return employee.Status == "Active"
                ? await SendBankToFinanceAsync(change, employee, values, context, action, ct)
                : await WriteBankForPendingHireAsync(change, employee, values, context, action, ct);

        // ── the ordinary sections: one SaveChanges for the decision, the effect and the audit ──────────────────
        ProfileSectionRegistry.Apply(section, employee, values);
        employee.UpdatedAtUtc = now;
        change.Status = "Approved";
        if (section == EssSectionKeys.Documents)
        {
            var ids = ReadDocumentIds(change.RequestedChangesJson);
            foreach (var doc in await db.EmployeeDocuments.Where(d => d.TenantId == tenantId && d.EmployeeId == employee.Id && ids.Contains(d.Id)).ToListAsync(ct))
            { doc.ApprovalStatus = "Approved"; doc.VerifiedAtUtc = now; doc.VerifiedBy = deciderId; }
        }
        ClearRequested(action, section);
        StageAudit(change, employee, context, values, section);
        await db.SaveChangesAsync(ct);
        return new OkObjectResult(change);
    }

    public async Task<IActionResult> RejectAsync(EmployeeProfileChangeRequest change, ProfileChangeDecisionDto request, Guid? deciderId, CancellationToken ct)
    {
        var notes = request.Notes?.Trim();
        if (string.IsNullOrEmpty(notes) || notes.Length > 500) return EssProblem.Create(Http, 400, EssProblemCodes.NoteRequired);
        change.Status = "Rejected";
        change.DecidedAtUtc = DateTime.UtcNow;
        change.DecidedBy = deciderId;
        change.ReviewNote = notes;
        var actionId = EmployeeCompletionController.ActionId(change.TenantId, change.EmployeeId);
        var action = await db.EmployeeActionItems.FirstOrDefaultAsync(x => x.TenantId == change.TenantId && x.Id == actionId, ct);
        if (action is not null) action.Status = "Open";
        db.EmployeeSelfServiceAuditLogs.Add(new EmployeeSelfServiceAuditLog
        {
            TenantId = change.TenantId, EmployeeId = change.EmployeeId, UserId = deciderId, Action = "ess.profile_change.rejected",
            EntityName = nameof(EmployeeProfileChangeRequest), EntityId = change.Id.ToString(),
        });
        await db.SaveChangesAsync(ct);
        return new OkObjectResult(change);
    }

    // ── bank ────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Atomically moves a section request from PendingHR to AwaitingFinance; false when another accept already did.</summary>
    public static async Task<bool> TryClaimForFinanceAsync(ZayraDbContext db, Guid tenantId, Guid requestId, CancellationToken ct) =>
        await db.EmployeeProfileChangeRequests.Where(x => x.TenantId == tenantId && x.Id == requestId && x.Status == "PendingHR")
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, AwaitingFinance), ct) == 1;

    private async Task<IActionResult> SendBankToFinanceAsync(EmployeeProfileChangeRequest change, Employee employee, Dictionary<string, string> values,
        RequestContext context, EmployeeActionItem? action, CancellationToken ct)
    {
        var tenantId = change.TenantId;
        var proposed = new Dictionary<string, string> { ["bankIban"] = values.GetValueOrDefault("iban") ?? string.Empty };
        if (!string.IsNullOrWhiteSpace(values.GetValueOrDefault("bankName"))) proposed["bankName"] = values["bankName"];
        if (await EmployeeChangeOverlap.FindPendingOverlapAsync(db, tenantId, employee.Id, proposed.Keys, null, ct) is not null)
            return EssProblem.Create(Http, 409, EssProblemCodes.FinancePending);

        var workflow = await EnsureEmployeeChangeWorkflowAsync(tenantId, ct);
        if (db.Entry(workflow).State != EntityState.Unchanged || db.ChangeTracker.Entries<ApprovalWorkflowStep>().Any(e => e.State != EntityState.Unchanged))
            await db.SaveChangesAsync(ct);
        var title = $"Employee change approval - {employee.EmployeeCode} {employee.FullName}";
        var create = new CreateApprovalRequest(workflow.Id, nameof(EmployeeChangeRequest), Guid.Empty.ToString(), title, employee.Id, employee.CompanyId, "High");
        try { await approvals.EnsureRoutableAsync(tenantId, create, context, ct); }
        catch (ApprovalRoutingException ex) { return new UnprocessableEntityObjectResult(new { error = ex.Code, code = ex.Code, message = ex.Message }); }

        var today = await TenantClock.TodayForAsync(db, tenantId, ct);
        var changeRequest = new EmployeeChangeRequest
        {
            TenantId = tenantId, EmployeeId = employee.Id,
            // The reviewing HR user submits the change to Finance: the existing "requester cannot approve" rule then bars them,
            // and the subject bar bars the employee. The employee's own submission stays on the profile request.
            RequestedByUserId = context.UserId,
            EffectiveDate = today.AddDays(1),   // date-level cooling-off: takes effect from the next calendar day
            SensitiveFields = string.Join(',', proposed.Keys),
            ProposedChangesJson = JsonSerializer.Serialize(proposed),
        };
        // One accept wins: a concurrent accept of the same request finds it already claimed and creates nothing.
        if (!await TryClaimForFinanceAsync(db, tenantId, change.Id, ct))
            return new ConflictObjectResult(new { message = "Profile change request is already decided." });
        db.EmployeeChangeRequests.Add(changeRequest);
        change.Status = AwaitingFinance;
        var json = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(change.RequestedChangesJson) ?? [];
        var rewritten = json.ToDictionary(kv => kv.Key, kv => (object?)kv.Value);
        rewritten["_changeRequestId"] = changeRequest.Id.ToString();
        change.RequestedChangesJson = JsonSerializer.Serialize(rewritten);
        StageAudit(change, employee, context, values, EssSectionKeys.Bank);
        db.EmployeeNotifications.Add(new EmployeeNotification
        {
            TenantId = tenantId, EmployeeId = employee.Id, NotificationType = "EssBankChange",
            Title = "Your bank details were sent for approval", Body = "HR checked your new bank details. They now need approval from your manager, an HR manager and a Finance approver (a payroll approver, never the HR reviewer) before they apply, from the next day.",
        });
        await db.SaveChangesAsync(ct);
        try
        {
            var approval = await approvals.CreateRequestAsync(tenantId, create with { EntityId = changeRequest.Id.ToString() }, context, ct);
            changeRequest.ApprovalRequestId = approval.Id;
            await db.SaveChangesAsync(ct);
        }
        catch (ApprovalRoutingException ex)
        {
            // Nobody could decide it: close what was just opened and give the section back, with nothing applied.
            changeRequest.Status = EmployeeChangeStatuses.Cancelled;
            changeRequest.RejectionReason = ex.Message.Length <= 1000 ? ex.Message : ex.Message[..1000];
            change.Status = "PendingHR";
            change.DecidedAtUtc = null; change.DecidedBy = null;
            await db.SaveChangesAsync(ct);
            return new UnprocessableEntityObjectResult(new { error = ex.Code, code = ex.Code, message = ex.Message });
        }
        return new ObjectResult(new { outcome = AwaitingFinance, changeRequestId = changeRequest.Id }) { StatusCode = StatusCodes.Status202Accepted };
    }

    private async Task<IActionResult> WriteBankForPendingHireAsync(EmployeeProfileChangeRequest change, Employee employee, Dictionary<string, string> values,
        RequestContext context, EmployeeActionItem? action, CancellationToken ct)
    {
        var tenantId = change.TenantId;
        var prior = await CompensationApprovalGate.LiveFingerprintAsync(db, tenantId, employee.Id, ct);
        var keys = new List<string> { "bankIban" };
        employee.BankIban = values.GetValueOrDefault("iban") ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(values.GetValueOrDefault("bankName"))) { employee.BankName = values["bankName"]; keys.Add("bankName"); }
        employee.UpdatedAtUtc = DateTime.UtcNow;
        await EmployeeBankProfileSync.SyncAsync(db, employee, keys, ct);
        change.Status = "Approved";
        ClearRequested(action, EssSectionKeys.Bank);
        StageAudit(change, employee, context, values, EssSectionKeys.Bank);
        await db.SaveChangesAsync(ct);
        // The figure now stored is not the one Finance approved: the pay gap re-opens and the HR reviewer is recorded as its writer,
        // so they cannot be the approver. Reads the saved figure, hence after the save above.
        await CompensationApprovalGate.ReconcileAsync(db, employee, prior, context, ct, forceReopen: true);
        await db.SaveChangesAsync(ct);
        return new OkObjectResult(change);
    }

    private void ClearRequested(EmployeeActionItem? action, string section)
    {
        if (action is null) return;
        var left = (action.RequestedSections ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Where(s => s != section).ToList();
        action.RequestedSections = action.RequestedSections is null ? null : string.Join(',', left);
        if (action.RequestedSections is { Length: 0 }) { action.RequestedSections = null; action.Status = "Completed"; }
    }

    /// <summary>
    /// Once HR has decided, the request keeps only the last four characters and a hash of them for identity and bank values
    /// (the applied value lives on the employee, or on the Finance change request until it takes effect).
    /// </summary>
    private static void RedactSensitiveValues(EmployeeProfileChangeRequest change, Employee employee)
    {
        var section = change.Section;
        if (section is null) return;
        var json = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(change.RequestedChangesJson) ?? [];
        var rewritten = json.ToDictionary(kv => kv.Key, kv => (object?)kv.Value);
        var hashes = new Dictionary<string, string>();
        foreach (var field in ProfileSectionRegistry.FieldsFor(section, ClassOf(employee)).Where(f => f.Sensitive))
        {
            if (!json.TryGetValue(field.Key, out var v) || v.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(v.GetString())) continue;
            rewritten[field.Key] = ProfileSectionRegistry.Mask(field, v.GetString());
            hashes[field.Key] = Last4Hash(v.GetString()!);
        }
        if (hashes.Count == 0) return;
        rewritten["_last4Hash"] = hashes;
        change.RequestedChangesJson = JsonSerializer.Serialize(rewritten);
    }

    private void StageAudit(EmployeeProfileChangeRequest change, Employee employee, RequestContext context, Dictionary<string, string> values, string section)
    {
        RedactSensitiveValues(change, employee);
        var last4 = values.Where(kv => !string.IsNullOrEmpty(kv.Value)).ToDictionary(kv => kv.Key, kv => Last4Hash(kv.Value));
        db.AuditLogs.Add(AuthAuditEntry.Create(Guid.NewGuid(), DateTime.UtcNow, AcceptedAction, nameof(EmployeeProfileChangeRequest), change.Id.ToString(), context,
            JsonSerializer.Serialize(new { section, employeeId = employee.Id, fields = last4.Keys, last4 })));
        db.EmployeeSelfServiceAuditLogs.Add(new EmployeeSelfServiceAuditLog
        {
            TenantId = change.TenantId, EmployeeId = employee.Id, UserId = context.UserId, Action = "ess.profile_change.approved",
            EntityName = nameof(EmployeeProfileChangeRequest), EntityId = change.Id.ToString(),
        });
    }

    public const string BankWorkflowCode = "EMPLOYEE-BANK-CHANGE";
    public const string FinanceApproverRole = "Finance Approver";

    private async Task<ApprovalWorkflow> EnsureEmployeeChangeWorkflowAsync(Guid tenantId, CancellationToken ct)
    {
        // A salary destination is a payment instruction, so it has its own three-step chain ending with a Finance approver
        // (payroll.approve is enforced for that step by ApprovalWorkflowService); the generic employee-change chain has none.
        var workflow = await db.ApprovalWorkflows.Include(w => w.Steps).FirstOrDefaultAsync(w => w.TenantId == tenantId && w.Code == BankWorkflowCode && w.IsActive, ct);
        if (workflow is null)
        {
            workflow = new ApprovalWorkflow { TenantId = tenantId, Code = BankWorkflowCode, Name = "Employee bank details change approval", EntityName = nameof(EmployeeChangeRequest), IsActive = true };
            db.ApprovalWorkflows.Add(workflow);
        }
        var last = workflow.Steps.OrderBy(x => x.StepOrder).LastOrDefault();
        if (workflow.Steps.Count != 3 || last is null || !last.IsFinalStep || !string.Equals(last.ApproverRole, FinanceApproverRole, StringComparison.OrdinalIgnoreCase))
        {
            db.ApprovalWorkflowSteps.RemoveRange(workflow.Steps);
            workflow.Steps.Clear();
            workflow.Steps.Add(new ApprovalWorkflowStep { TenantId = tenantId, WorkflowId = workflow.Id, StepOrder = 1, StepName = "Direct Manager Review", ApproverRole = "Manager", ApproverType = "Manager", EscalationAfterHours = 24, IsFinalStep = false });
            workflow.Steps.Add(new ApprovalWorkflowStep { TenantId = tenantId, WorkflowId = workflow.Id, StepOrder = 2, StepName = "HR Final Approval", ApproverRole = "HR Manager", ApproverType = "Role", EscalationAfterHours = 48, IsFinalStep = false });
            workflow.Steps.Add(new ApprovalWorkflowStep { TenantId = tenantId, WorkflowId = workflow.Id, StepOrder = 3, StepName = "Finance Approval", ApproverRole = FinanceApproverRole, ApproverType = "Role", EscalationAfterHours = 48, IsFinalStep = true });
        }
        return workflow;
    }
}
