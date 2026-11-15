using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Globalization;
using System.Text.Json;
using Zayra.Api.Application.Approvals;
using Zayra.Api.Application.Common;
using Zayra.Api.Application.Employees;
using Zayra.Api.Application.Finance;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Approvals;
using Zayra.Api.Infrastructure.Authorization;
using Zayra.Api.Infrastructure.Common;
using Zayra.Api.Infrastructure.Data;
using Zayra.Api.Infrastructure.Finance;
using Zayra.Api.Infrastructure.Payroll;
using Zayra.Api.Models;

namespace Zayra.Api.Controllers.Finance;

[Authorize]
[ApiController]
[Route("api/finance/advances")]
[Zayra.Api.Infrastructure.Finance.GlCostCentreRuleFilter]
public partial class AdvancesController : ControllerBase
{
    private readonly ZayraDbContext _db;
    private readonly IDataScopeService _scopeService;

    public AdvancesController(ZayraDbContext db, IDataScopeService scopeService)
    {
        _db = db;
        _scopeService = scopeService;
    }

    private Guid GetTenantId() =>
        Guid.TryParse(User.FindFirst("tenant_id")?.Value, out var id) ? id : Guid.Empty;
    private Guid? GetUserId() =>
        Guid.TryParse(User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value, out var id) ? id : null;
    // Issued tokens carry the display name as "name"; ClaimTypes.Name only exists when something maps it.
    private string GetUserName() => User.FindFirst(System.Security.Claims.ClaimTypes.Name)?.Value ?? User.FindFirst("name")?.Value ?? "Unknown";

    // ── Policy ────────────────────────────────────────────────────────────────

    [HttpGet("policy")]
    public async Task<IActionResult> GetPolicy(CancellationToken ct)
    {
        var tid = GetTenantId();
        var p = await _db.AdvancePolicies.FirstOrDefaultAsync(x => x.TenantId == tid && x.IsActive, ct);
        // SAFE-SERIALIZATION: AdvancePolicy is tenant config (percentage caps, cooldowns) — no personal PII.
        return Ok(p);
    }

    [HttpPost("policy")]
    [HasPermission("loans.write")]
    public async Task<IActionResult> UpsertPolicy([FromBody] AdvancePolicyRequest req, CancellationToken ct)
    {
        var tid = GetTenantId();
        var uid = GetUserId();
        if (req.MaxPercentageOfSalary is < 0 or > 100 || decimal.Round(req.MaxPercentageOfSalary, 2) != req.MaxPercentageOfSalary
            || req.MaxAdvancesPerYear is < 0 or > 100 || req.MinServiceMonths is < 0 or > 600
            || req.CooldownMonths is < 0 or > 600 || req.MaxInstallments is < 1 or > MaxInstallmentsWithoutPolicy
            || string.IsNullOrWhiteSpace(req.PolicyName) || req.PolicyName.Length > 200)
            return BadRequest(Refusal("advance_policy_invalid",
                $"Policy values are out of range: salary percentage 0-100, installments 1-{MaxInstallmentsWithoutPolicy}, counts and months non-negative.",
                $"قيم السياسة خارج النطاق: نسبة الراتب من 0 إلى 100، والأقساط من 1 إلى {MaxInstallmentsWithoutPolicy}، والأعداد والأشهر غير سالبة."));
        var p = await _db.AdvancePolicies.FirstOrDefaultAsync(x => x.TenantId == tid && x.IsActive, ct);
        var before = p == null ? null : JsonSerializer.Serialize(PolicyValues(p));
        if (p != null)
        {
            p.PolicyName = req.PolicyName; p.MaxPercentageOfSalary = req.MaxPercentageOfSalary;
            p.MaxAdvancesPerYear = req.MaxAdvancesPerYear; p.MinServiceMonths = req.MinServiceMonths;
            p.AllowInstallments = req.AllowInstallments; p.MaxInstallments = req.MaxInstallments;
            p.CooldownMonths = req.CooldownMonths; p.RequiresApproval = req.RequiresApproval;
        }
        else
        {
            p = new AdvancePolicy
            {
                TenantId = tid, PolicyName = req.PolicyName, MaxPercentageOfSalary = req.MaxPercentageOfSalary,
                MaxAdvancesPerYear = req.MaxAdvancesPerYear, MinServiceMonths = req.MinServiceMonths,
                AllowInstallments = req.AllowInstallments, MaxInstallments = req.MaxInstallments,
                CooldownMonths = req.CooldownMonths, RequiresApproval = req.RequiresApproval, CreatedBy = uid,
            };
            _db.AdvancePolicies.Add(p);
        }
        await _db.SaveChangesAsync(ct);
        // The audit row is keyed on the policy id (AdvanceId carries no foreign key).
        await WriteAdvanceAudit(tid, uid, p.Id, "AdvancePolicyChanged", before, JsonSerializer.Serialize(PolicyValues(p)), ct);
        // SAFE-SERIALIZATION: AdvancePolicy is tenant config (percentage caps, cooldowns) — no personal PII.
        return Ok(p);
    }

    private static object PolicyValues(AdvancePolicy p) => new
    {
        p.PolicyName, p.MaxPercentageOfSalary, p.MaxAdvancesPerYear, p.MinServiceMonths,
        p.AllowInstallments, p.MaxInstallments, p.CooldownMonths, p.RequiresApproval,
    };

    // ── Salary Advances ───────────────────────────────────────────────────────

    // F10 — gated on loans.read or loans.write, as the loan book is (see LoansController.LoansReadDenial).
    [HttpGet]
    [HasPermission("loans.read", "loans.write")]
    public async Task<IActionResult> List(
        [FromQuery] Guid? employeeId, [FromQuery] string? status,
        [FromQuery] int page = 1, [FromQuery] int pageSize = 30, CancellationToken ct = default)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 100);
        if (LoansController.LoansReadDenial(this) is { } denied) return denied;
        var tid = GetTenantId();
        var scope = await _scopeService.ResolveAsync(User, tid, ct);
        var q = _db.SalaryAdvances.Where(x => x.TenantId == tid && !x.IsDeleted);

        if (!scope.IsUnrestricted)
        {
            var allowedEmployeeIds = scope.AllowedEmployeeIds!.ToArray();
            q = q.Where(x => x.EmployeeIntId.HasValue && allowedEmployeeIds.Contains(x.EmployeeIntId.Value));
        }
        if (employeeId.HasValue)
            q = q.Where(x => x.EmployeeId == employeeId);

        if (!string.IsNullOrEmpty(status)) q = q.Where(x => x.Status == status);
        var total = await q.CountAsync(ct);
        var items = await q.OrderByDescending(x => x.CreatedAtUtc)
            .Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
        return Ok(new { total, items = items.Select(SalaryAdvanceDto.Project).ToList() });
    }

    [HttpGet("{id:guid}")]
    [HasPermission("loans.read", "loans.write")]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct)
    {
        if (LoansController.LoansReadDenial(this) is { } denied) return denied;
        var tid = GetTenantId();
        var adv = await _db.SalaryAdvances.FirstOrDefaultAsync(x => x.Id == id && x.TenantId == tid && !x.IsDeleted, ct);
        if (adv == null) return NotFound();
        // Object-level authorization (see LoansController.GetLoan): a scoped caller may only read an
        // advance for an employee in their scope; prevents IDOR via the detail route.
        var scope = await _scopeService.ResolveAsync(User, tid, ct);
        if (!scope.IsUnrestricted && !(adv.EmployeeIntId.HasValue && scope.CanAccessEmployee(adv.EmployeeIntId.Value)))
            return Forbid();
        var installments = await _db.AdvanceInstallments.Where(x => x.AdvanceId == id).OrderBy(x => x.InstallmentNumber).ToListAsync(ct);
        var approvals = await _db.AdvanceApprovals.Where(x => x.AdvanceId == id).OrderBy(x => x.StepOrder).ToListAsync(ct);
        var auditLogs = await _db.AdvanceAuditLogs.AsNoTracking().Where(x => x.TenantId == tid && x.AdvanceId == id).OrderByDescending(x => x.CreatedAtUtc).ToListAsync(ct);
        if (!IsHrActor() && !IsFinanceActor())
        {
            // As LoansController.GetLoan: salary-bearing snapshots are not a team-manager entitlement. Redact a
            // detached view (AsNoTracking), never the stored audit evidence.
            foreach (var log in auditLogs) { log.OldValuesJson = string.Empty; log.NewValuesJson = string.Empty; }
        }
        // The journal is a Finance view; other readers get the advance and its schedule only.
        var glEntries = IsFinanceActor()
            ? await _db.FinanceGlEntries.Where(x => x.TenantId == tid && x.SourceEntityId == id).OrderByDescending(x => x.EntryDate).ToListAsync(ct)
            : new List<FinanceGlEntry>();
        return Ok(new { advance = SalaryAdvanceDto.Project(adv), installments, approvals, auditLogs, glEntries });
    }

    /// <summary>The tenant-wide lock every advance request takes, as LoansController.CreateLoan takes
    /// "finance.loan-create": the per-year count, the cooldown and the advance number are all read-then-write,
    /// so two simultaneous requests for one employee could otherwise both pass the policy.</summary>
    private const string AdvanceCreateLockScope = "finance.advance-create";

    /// <summary>Hard installment ceiling for a salary advance (also the ceiling of any policy's MaxInstallments).</summary>
    private const int MaxInstallmentsWithoutPolicy = 60;

    [HttpPost]
    [HasPermission("loans.self", "loans.write")]
    public async Task<IActionResult> Create([FromBody] CreateAdvanceRequest req, CancellationToken ct)
    {
        try
        {
            return await FinanceDecisionSerializer.SerializeAsync<IActionResult>(
                _db, AdvanceCreateLockScope, GetTenantId(), GetTenantId(), () => CreateCore(req, ct), ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is Npgsql.PostgresException { SqlState: Npgsql.PostgresErrorCodes.UniqueViolation })
        {
            // The number is allocated after the highest one in use, under the tenant's create lock, so only a writer outside
            // that lock (an opening-balance import landing at the same moment) can take it first. The transaction rolled back
            // and nothing was saved; a retry allocates the next number.
            return Conflict(Refusal("advance_number_conflict",
                "Another advance took this advance number at the same moment. Nothing was saved; submit the request again.",
                "استُخدم رقم السلفة نفسه لسلفة أخرى في اللحظة ذاتها. لم يُحفظ شيء؛ أعد إرسال الطلب."));
        }
    }

    private async Task<IActionResult> CreateCore(CreateAdvanceRequest req, CancellationToken ct)
    {
        var tid = GetTenantId();
        var uid = GetUserId();
        if (uid == null) return Unauthorized();
        if (!IsMoney(req.RequestedAmount))
            return BadRequest(Refusal("advance_amount_invalid",
                "The requested amount must be a positive amount with at most two decimal places.",
                "يجب أن يكون المبلغ المطلوب موجبًا وبمنزلتين عشريتين على الأكثر."));
        // Who the caller IS comes from the login-to-employee link (CallerEmployeeResolver), the one lookup
        // self-service, mobile and the data scope share. The legacy Employee.UserAccountId column is not used:
        // a login linked only through EmployeeUserAccounts would otherwise be refused its own request.
        var ownId = await CallerEmployeeResolver.ResolveAsync(_db, User, tid, ct);
        if (req.EmployeeId == Guid.Empty && !req.EmployeeIntId.HasValue)
        {
            // Self-service: no employee named means the signed-in employee, as for a loan request.
            if (!ownId.HasValue) return BadRequest(Refusal("ess_link_missing", EssLinkGuidance.En, EssLinkGuidance.Ar));
            req = req with { EmployeeIntId = ownId };
        }
        var identity = await _db.ResolveEmployeeAsync(tid, req.EmployeeId, req.EmployeeIntId, ct);
        if (!identity.IsSuccess)
            return BadRequest(Refusal("advance_employee_not_found", identity.Error ?? "The employee could not be identified.",
                "تعذر تحديد الموظف المطلوب له السلفة."));
        var employee = identity.Employee!;
        var scope = await _scopeService.ResolveAsync(User, tid, ct);
        if (!scope.CanAccessEmployee(employee.Id)) return Forbid();
        // Only HR or Finance may request an advance on someone else's behalf (LoansController.CreateLoanCore):
        // the HR/Finance roles, or loans.write ("create loan and advance applications"). The Employee role holds
        // loans.self only, so self-service stays limited to the caller's own record.
        if (employee.Id != ownId && !IsHrActor() && !IsFinanceActor() && !User.HasPermission("loans.write"))
            return Forbid();
        if (await EmploymentRefusalAsync(tid, employee, ct) is { } notServing) return BadRequest(notServing);
        if (StartDateRefusal(req.RepaymentStartDate) is { } startRefusal) return BadRequest(startRefusal);

        var policy = await _db.AdvancePolicies.FirstOrDefaultAsync(x => x.TenantId == tid && x.IsActive, ct);
        // Audit P2-5: with no active policy every amount used to be allowed (only the 60-instalment hard cap applied). The
        // salary cap, the yearly quota, the service minimum and the cooldown all live on the policy, so there is no advance
        // without one.
        if (policy == null)
            return BadRequest(Refusal("advance_policy_required",
                "Salary advances need an active advance policy. Ask HR to set one up in Setup (Advance policy) before requesting an advance.",
                "تتطلب سلف الرواتب سياسة سلف فعّالة. اطلب من الموارد البشرية إعدادها من الإعدادات (سياسة السلف) قبل طلب سلفة."));

        // Every policy limit is checked here, before the advance is recorded. Nothing is paid until a second person approves it.
        if (InstallmentsRefusal(policy, req.RequestedAmount, req.Installments) is { } installmentsRefusal)
            return BadRequest(installmentsRefusal);
        if (policy != null)
        {
            var now = DateTime.UtcNow;
            if (policy.MinServiceMonths > 0
                && (employee.JoiningDate == default || employee.JoiningDate.Date > now.Date.AddMonths(-policy.MinServiceMonths)))
                return BadRequest(Refusal("advance_min_service",
                    $"At least {policy.MinServiceMonths} completed service month(s) are required before a salary advance.",
                    $"يلزم إكمال {policy.MinServiceMonths} شهر خدمة على الأقل قبل طلب سلفة راتب."));

            var currentYear = now.Year;
            // A rejected or cancelled request never paid anything, so it does not use up the year's allowance; a typo
            // that was refused used to lock the employee out of advances for the rest of the year.
            var advancesThisYear = await _db.SalaryAdvances.CountAsync(
                x => x.TenantId == tid && x.EmployeeIntId == employee.Id && !x.IsDeleted
                     && x.Status != AdvanceStatuses.Rejected && x.Status != AdvanceStatuses.Cancelled
                     && x.CreatedAtUtc.Year == currentYear, ct);
            if (advancesThisYear >= policy.MaxAdvancesPerYear)
                return BadRequest(Refusal("advance_yearly_limit",
                    $"Employee has already used {advancesThisYear} advance(s) this year. Maximum allowed is {policy.MaxAdvancesPerYear}.",
                    $"استخدم الموظف {advancesThisYear} سلفة هذا العام. الحد الأقصى المسموح {policy.MaxAdvancesPerYear}."));

            if (policy.CooldownMonths > 0)
            {
                var cooldownCutoff = now.AddMonths(-policy.CooldownMonths);
                // Payroll closes a fully deducted advance as "Closed"; a manual settlement used "Settled".
                var recentlySettled = await _db.SalaryAdvances.AnyAsync(
                    x => x.TenantId == tid && x.EmployeeIntId == employee.Id && !x.IsDeleted
                         && (x.Status == "Settled" || x.Status == "Closed") && x.UpdatedAtUtc > cooldownCutoff, ct);
                if (recentlySettled)
                    return BadRequest(Refusal("advance_cooldown",
                        $"Employee must wait {policy.CooldownMonths} month(s) after settling an advance before requesting a new one.",
                        $"يجب على الموظف الانتظار {policy.CooldownMonths} شهر بعد تسوية سلفة قبل طلب سلفة جديدة."));
            }

            if (await SalaryCapRefusalAsync(tid, employee, policy, req.RequestedAmount, ct) is { } capRefusal)
                return BadRequest(capRefusal);
            if (await CumulativeCapRefusalAsync(tid, employee, policy, req.RequestedAmount, null, ct) is { } cumulativeRefusal)
                return BadRequest(cumulativeRefusal);
        }

        var advNumber = await NextAdvanceNumberAsync(tid, ct);

        var adv = new SalaryAdvance
        {
            TenantId = tid, CompanyId = employee.CompanyId,
            EmployeeId = employee.PublicId, EmployeeName = employee.FullName,
            EmployeeIntId = employee.Id,
            AdvanceNumber = advNumber, RequestedAmount = req.RequestedAmount,
            RepaymentType = req.RepaymentType, Installments = req.Installments,
            Reason = req.Reason ?? string.Empty,
            // Every advance waits for a second person. A policy's RequiresApproval=false used to disburse here on
            // create, with nobody deciding; money now always moves through Approve, where the requester and the
            // employee it is for are both barred. The flag is kept on the policy but no longer skips approval.
            Status = "Pending",
            CreatedBy = uid,
        };

        _db.SalaryAdvances.Add(adv);
        await _db.SaveChangesAsync(ct);
        await WriteAdvanceAudit(tid, uid, adv.Id, "AdvanceRequested", null,
            JsonSerializer.Serialize(new { adv.AdvanceNumber, adv.RequestedAmount, adv.Status }), ct);
        return Ok(SalaryAdvanceDto.Project(adv));
    }

    /// <summary>
    /// Pending → Approved: the checker agrees the amount, the instalments and the schedule. No money moves and no journal is
    /// posted here; the advance waits for Finance to record the payout (<see cref="Payout"/>), as an approved loan waits for its
    /// payment batch. Payroll collects only Active advances, so an Approved one is never deducted.
    /// </summary>
    [HttpPatch("{id:guid}/approve")]
    [Authorize(Roles = "Admin,HR Manager,Finance,Manager")]
    public Task<IActionResult> Approve(Guid id, [FromBody] AdvanceApproveRequest req, CancellationToken ct)
    {
        var tid = GetTenantId();
        var uid = GetUserId();
        // The whole installment schedule is written here. The Pending check below is a read-then-write
        // guard and closes only a sequential replay: two simultaneous approvals both read Pending, both
        // pass, and both write a schedule. Serialized on the advance for the same reasons, and by the
        // same mechanism, as LoansController.DecideApproval.
        return FinanceDecisionSerializer.SerializeAsync<IActionResult>(
            _db, FinanceDecisionSerializer.ScopeAdvance, tid, id, async () =>
        {
        if (uid == null) return Unauthorized();
        var adv = await _db.SalaryAdvances.FirstOrDefaultAsync(x => x.Id == id && x.TenantId == tid && !x.IsDeleted, ct);
        // Object-level authorization, as Get: a scoped approver decides only for employees in their scope.
        if (adv is not null && !await CanAccessAdvanceAsync(adv, ct)) return Forbid();
        // The subject never decides on their own money: the directly linked login, and any login linked to the
        // employee through the login-link table (SubjectDecisionBar), whatever company the employee is in now.
        var employeeIsDecider = adv is not null
            && (await IsAdvanceEmployeeAsync(adv, uid.Value, ct)
                || (adv.EmployeeIntId is int subjectId
                    && await SubjectDecisionBar.CallerIsSubjectAsync(_db, tid, uid, subjectId, ct)));

        // Shared checklist — see ApprovalDecisionGuard. This endpoint IS the decision, so there is
        // no decision vocabulary to validate, and an advance carries no approval step rows.
        var verdict = ApprovalDecisionGuard.Evaluate(new ApprovalDecisionSpec
        {
            Decision = "Approved",
            AllowedDecisions = Array.Empty<string>(),
            Step = null,
            ParentLabel = "advance",
            ParentExists = adv is not null,
            ParentStatus = adv?.Status ?? string.Empty,
            ParentStatusesAllowingDecision = new[] { "Pending" },
            // DECLARED ABSENCE: SalaryAdvance carries IsLockedByPayroll and LoansController refuses
            // a decision on a locked loan, but this endpoint has never consulted it. Preserved as-is
            // — closing it changes behaviour and belongs in its own change, not in a refactor.
            Lock = ApprovalLock.None,
            MakerChecker = new MakerCheckerRule(
                adv?.CreatedBy is { } maker && uid.HasValue && maker == uid,
                null,
                "Maker-checker control: requester cannot approve their own salary advance."),
            // The employee the advance pays, whoever raised it — HR may file it for them.
            SubjectSeparation = new SubjectSeparationRule(
                employeeIsDecider,
                null,
                "Segregation of duties: you cannot approve a salary advance paid to you."),
            EarlierStepSeparation = EarlierStepRule.None,   // DECLARED ABSENCE: one synthetic step.
        });
        if (!verdict.Passed) return AdvanceApproveRefusal(verdict);
        ArgumentNullException.ThrowIfNull(adv);

        // The approved terms are bounded by the request and by the active policy; the checker may lower the
        // amount, never raise it past what was asked for or past the salary cap.
        if (!IsMoney(req.ApprovedAmount))
            return BadRequest(Refusal("advance_amount_invalid",
                "The approved amount must be a positive amount with at most two decimal places.",
                "يجب أن يكون المبلغ المعتمد موجبًا وبمنزلتين عشريتين على الأكثر."));
        if (req.ApprovedAmount > adv.RequestedAmount)
            return BadRequest(Refusal("advance_amount_above_request",
                "The approved amount cannot exceed the requested amount.",
                "لا يمكن أن يتجاوز المبلغ المعتمد المبلغ المطلوب."));
        if (StartDateRefusal(req.RepaymentStartDate) is { } startRefusal) return BadRequest(startRefusal);
        var employee = await LoadAdvanceEmployeeAsync(tid, adv, ct);
        if (await EmploymentRefusalAsync(tid, employee, ct) is { } notServing) return BadRequest(notServing);
        ArgumentNullException.ThrowIfNull(employee);
        var policy = await _db.AdvancePolicies.FirstOrDefaultAsync(x => x.TenantId == tid && x.IsActive, ct);
        if (InstallmentsRefusal(policy, req.ApprovedAmount, req.Installments) is { } installmentsRefusal)
            return BadRequest(installmentsRefusal);
        if (policy is { MaxPercentageOfSalary: > 0 })
        {
            if (await SalaryCapRefusalAsync(tid, employee, policy, req.ApprovedAmount, ct) is { } capRefusal)
                return BadRequest(capRefusal);
            if (await CumulativeCapRefusalAsync(tid, employee, policy, req.ApprovedAmount, adv.Id, ct) is { } cumulativeRefusal)
                return BadRequest(cumulativeRefusal);
        }

        var oldStatus = adv.Status;
        adv.Status = AdvanceStatuses.Approved; adv.ApprovedAmount = req.ApprovedAmount;
        // Whole cents per installment; GenerateAdvanceInstallments puts the remainder in the last one so the
        // schedule sums exactly to the principal.
        adv.InstallmentAmount = req.Installments > 0
            ? decimal.Round(req.ApprovedAmount / req.Installments, 2, MidpointRounding.ToZero) : req.ApprovedAmount;
        // Nothing is owed until the payout: the receivable, and the balance payroll recovers, start there.
        adv.Installments = req.Installments; adv.OutstandingBalance = 0m;
        // The agreed first repayment date, or null when the approver left it to the payout (one month after payment).
        adv.RepaymentStartDate = req.RepaymentStartDate; adv.UpdatedAtUtc = DateTime.UtcNow; adv.UpdatedBy = uid;

        // A provisional schedule when no date was agreed; Payout re-dates it from the paid date.
        GenerateAdvanceInstallments(tid, adv, req.RepaymentStartDate);

        var approval = new AdvanceApproval
        {
            TenantId = tid, AdvanceId = id, StepOrder = 1, ApproverRole = "HR",
            ApprovedBy = uid, ApprovedByName = GetUserName(), Status = "Approved",
            DecidedAtUtc = DateTime.UtcNow,
        };
        _db.AdvanceApprovals.Add(approval);

        await _db.SaveChangesAsync(ct);
        await WriteAdvanceAudit(tid, uid, id, "AdvanceApproved",
            JsonSerializer.Serialize(new { Status = oldStatus }),
            JsonSerializer.Serialize(new { Status = adv.Status, ApprovedAmount = req.ApprovedAmount, adv.Installments, adv.RepaymentStartDate }), ct);
        return Ok(SalaryAdvanceDto.Project(adv));
        }, ct);
    }

    [HttpPatch("{id:guid}/reject")]
    [Authorize(Roles = "Admin,HR Manager,Finance,Manager")]
    public Task<IActionResult> Reject(Guid id, [FromBody] RejectRequest req, CancellationToken ct)
    {
        var tid = GetTenantId();
        var uid = GetUserId();
        // Same lock as Approve, so an approve and a reject racing on one advance cannot both win:
        // without it the reject can land after the approve has disbursed, leaving a Rejected header
        // over a live GL entry and a live installment schedule.
        return FinanceDecisionSerializer.SerializeAsync<IActionResult>(
            _db, FinanceDecisionSerializer.ScopeAdvance, tid, id, async () =>
        {
        var adv = await _db.SalaryAdvances.FirstOrDefaultAsync(x => x.Id == id && x.TenantId == tid && !x.IsDeleted, ct);
        if (adv is not null && !await CanAccessAdvanceAsync(adv, ct)) return Forbid();

        // This is the hole the correctness batch closed, and the reason the checklist is now shared:
        // Reject wrote "Rejected" over ANY status while Approve (above) has always required Pending.
        // Rejecting an Active advance left the disbursement GL entry and the installment schedule
        // live under a Rejected header, and payroll's deduction query keys on Status == "Active",
        // so the repayments simply stopped. The guard cannot be satisfied without answering the
        // status question, which is what makes the omission unrepeatable.
        var verdict = ApprovalDecisionGuard.Evaluate(new ApprovalDecisionSpec
        {
            Decision = "Rejected",
            AllowedDecisions = Array.Empty<string>(),
            Step = null,
            ParentLabel = "advance",
            ParentExists = adv is not null,
            ParentStatus = adv?.Status ?? string.Empty,
            ParentStatusesAllowingDecision = new[] { "Pending" },
            Lock = ApprovalLock.None,                 // DECLARED ABSENCE — as in Approve, above.
            MakerChecker = MakerCheckerRule.None,     // DECLARED ABSENCE: a requester may withdraw.
            // DECLARED ABSENCE: the employee may likewise reject (withdraw) an advance paid to them;
            // a rejection grants nothing. Approve, above, carries the bar.
            SubjectSeparation = SubjectSeparationRule.None,
            EarlierStepSeparation = EarlierStepRule.None,   // DECLARED ABSENCE: one synthetic step.
        });
        if (!verdict.Passed) return AdvanceRejectRefusal(verdict, adv?.Status);
        ArgumentNullException.ThrowIfNull(adv);
        var oldStatus = adv.Status;
        adv.Status = "Rejected"; adv.RejectionReason = req.Reason;
        adv.UpdatedAtUtc = DateTime.UtcNow; adv.UpdatedBy = uid;
        await _db.SaveChangesAsync(ct);
        await WriteAdvanceAudit(tid, uid, id, "AdvanceRejected",
            JsonSerializer.Serialize(new { Status = oldStatus }),
            JsonSerializer.Serialize(new { Status = "Rejected", Reason = req.Reason }), ct);
        return Ok(SalaryAdvanceDto.Project(adv));
        }, ct);
    }

    /// <summary>
    /// Approved → Active: Finance records that the approved advance was paid, with the bank or cash reference and the paid date,
    /// and the disbursement journal posts Dr Employee Salary Advances (ADVANCE_RECEIVABLE, 1410) / Cr Cash/Bank (CASH_BANK, 1000)
    /// for the approved amount, dated and periodised on the paid date, in the advance's company and currency, once (the same
    /// post-once probe as before). This mirrors the loan payment outcome (LoansController.RecordPaymentLineOutcome): the same
    /// Admin/Finance gate with the role re-checked in the body, the same closed-period refusal, the same reference evidence.
    /// <para>Controls: the advance lock is held for the whole unit; only an Approved advance can be paid; the payer is never the
    /// person who approved it, nor the employee it pays; the employee must still be serving; a repeat with the same reference
    /// returns the paid advance and posts nothing, a different reference is refused.</para>
    /// <para>The first repayment date: the agreed date when one was agreed and it is not before the payment, otherwise one month
    /// after the paid date. The date used is stored and the schedule is re-dated from it, so payroll starts collecting exactly
    /// when the schedule says (audit P2-1: the old flow stored null and built the schedule from approval day plus a month, so
    /// payroll collected a month early and the instalment stamps drifted).</para>
    /// </summary>
    [HttpPost("{id:guid}/payout")]
    [Authorize(Roles = "Admin,Finance")]
    public Task<IActionResult> Payout(Guid id, [FromBody] AdvancePayoutRequest req, CancellationToken ct)
    {
        var tid = GetTenantId();
        var uid = GetUserId();
        return FinanceDecisionSerializer.SerializeAsync<IActionResult>(
            _db, FinanceDecisionSerializer.ScopeAdvance, tid, id, async () =>
        {
        // The role gate resolves to loans.write, which HR roles also hold: paying is the Finance tier only.
        if (!IsFinanceActor()) return Forbid();
        if (uid == null) return Unauthorized();
        var reference = req.Reference?.Trim();
        if (string.IsNullOrEmpty(reference) || reference.Length > MaxPaymentReferenceLength)
            return BadRequest(Refusal("advance_payout_reference_required",
                $"A payment reference of up to {MaxPaymentReferenceLength} characters is required.",
                $"مرجع الدفع مطلوب، بحد أقصى {MaxPaymentReferenceLength} حرفًا."));
        if (req.Notes is { Length: > 1000 })
            return BadRequest(Refusal("advance_payout_notes_too_long",
                "Payout notes can be at most 1000 characters.", "يجب ألا تتجاوز ملاحظات الصرف 1000 حرف."));

        var adv = await _db.SalaryAdvances.FirstOrDefaultAsync(x => x.Id == id && x.TenantId == tid && !x.IsDeleted, ct);
        if (adv == null) return NotFound();
        if (!await CanAccessAdvanceAsync(adv, ct)) return Forbid();

        // Idempotent replay: the same reference returns the paid advance and posts nothing. A legacy Active advance (paid by the
        // old approve-and-disburse flow) has no reference, so any payout of it is refused.
        if (adv.Status is AdvanceStatuses.Active or AdvanceStatuses.Closed or AdvanceStatuses.Settled)
            return adv.PaymentReference is { } paidReference && string.Equals(paidReference, reference, StringComparison.Ordinal)
                ? Ok(SalaryAdvanceDto.Project(adv))
                : Conflict(Refusal("advance_already_paid",
                    $"Advance {adv.AdvanceNumber} has already been paid out.",
                    $"تم صرف السلفة {adv.AdvanceNumber} بالفعل."));
        if (adv.Status != AdvanceStatuses.Approved)
            return Conflict(Refusal("invalid_advance_state",
                $"Only an approved advance can be paid out (current: {adv.Status}).",
                $"لا يمكن صرف إلا سلفة معتمدة (الحالة الحالية: {adv.Status})."));

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var paidDate = req.PaidDate ?? today;
        if (paidDate == default || paidDate > today || paidDate < DateOnly.FromDateTime(adv.CreatedAtUtc))
            return BadRequest(Refusal("advance_payout_date_invalid",
                "The paid date cannot be in the future or before the advance was requested.",
                "لا يمكن أن يكون تاريخ الصرف في المستقبل أو قبل تاريخ طلب السلفة."));

        // Maker-checker: whoever approved the advance does not also release the cash.
        var approvers = await _db.AdvanceApprovals.AsNoTracking()
            .Where(x => x.TenantId == tid && x.AdvanceId == id && x.Status == "Approved" && x.ApprovedBy != null)
            .Select(x => x.ApprovedBy!.Value).ToListAsync(ct);
        if (approvers.Contains(uid.Value))
            return BadRequest(Refusal("advance_payer_is_approver",
                "Maker-checker control: the person who approved this advance cannot also record its payout.",
                "ضابط الفصل بين المهام: لا يمكن لمن اعتمد السلفة أن يسجل صرفها أيضًا."));
        // Nor does the employee it pays record paying themselves.
        if (await IsAdvanceEmployeeAsync(adv, uid.Value, ct)
            || (adv.EmployeeIntId is int subjectId && await SubjectDecisionBar.CallerIsSubjectAsync(_db, tid, uid, subjectId, ct)))
            return BadRequest(Refusal("advance_payer_is_subject",
                "Segregation of duties: you cannot record the payout of a salary advance paid to you.",
                "الفصل بين المهام: لا يمكنك تسجيل صرف سلفة راتب مدفوعة لك."));

        // The employee must still be serving when the cash moves, not only when the terms were agreed.
        if (await EmploymentRefusalAsync(tid, await LoadAdvanceEmployeeAsync(tid, adv, ct), ct) is { } notServing)
            return BadRequest(notServing);

        var companyId = await ResolveAdvanceCompanyAsync(tid, adv, ct);
        if (await PeriodCloseGuard.IsClosedAsync(_db, tid, companyId, paidDate.ToString("yyyy-MM"), ct))
            return UnprocessableEntity(Refusal("advance_payout_period_closed",
                $"The accounting period {paidDate:yyyy-MM} is closed. Reopen it or use a paid date in an open period.",
                $"الفترة المحاسبية {paidDate:yyyy-MM} مغلقة. أعد فتحها أو استخدم تاريخ صرف في فترة مفتوحة."));
        // One bank reference evidences one payout in a company, as for loan payments.
        if (await _db.SalaryAdvances.AnyAsync(x => x.TenantId == tid && x.Id != id && x.CompanyId == adv.CompanyId
                && x.PaymentReference == reference, ct))
            return Conflict(Refusal("advance_payout_reference_used",
                "This payment reference is already recorded against another advance.",
                "مرجع الدفع هذا مسجل بالفعل لسلفة أخرى."));
        // Defence in depth: an Approved advance never has a journal; if one exists, nothing is posted twice.
        if (await DisbursementAlreadyPostedAsync(tid, id, ct))
            return Conflict(Refusal("advance_already_paid",
                $"Advance {adv.AdvanceNumber} already has a disbursement journal.",
                $"يوجد قيد صرف للسلفة {adv.AdvanceNumber} بالفعل."));

        var agreedStart = adv.RepaymentStartDate;
        var start = agreedStart is { } agreed && agreed >= paidDate ? agreed : paidDate.AddMonths(1);
        adv.Status = AdvanceStatuses.Active;
        adv.OutstandingBalance = adv.ApprovedAmount;
        adv.RepaymentStartDate = start;
        adv.PaidAtUtc = DateTime.UtcNow; adv.PaymentReference = reference; adv.PaidByUserId = uid;
        adv.UpdatedAtUtc = DateTime.UtcNow; adv.UpdatedBy = uid;
        var schedule = await _db.AdvanceInstallments
            .Where(x => x.TenantId == tid && x.AdvanceId == id).OrderBy(x => x.InstallmentNumber).ToListAsync(ct);
        foreach (var row in schedule) row.DueDate = start.AddMonths(row.InstallmentNumber - 1);

        await PostGlEntry(tid, uid, companyId, adv.Id, adv.AdvanceNumber,
            "Advance", "Disbursement", "ADVANCE_RECEIVABLE", "CASH_BANK", adv.ApprovedAmount, null, paidDate, ct);
        await _db.SaveChangesAsync(ct);
        await WriteAdvanceAudit(tid, uid, id, "AdvancePaidOut",
            JsonSerializer.Serialize(new { Status = AdvanceStatuses.Approved, RepaymentStartDate = agreedStart }),
            JsonSerializer.Serialize(new
            {
                adv.Status, adv.ApprovedAmount, PaidDate = paidDate, adv.PaymentReference, req.Notes,
                AgreedRepaymentStartDate = agreedStart, EffectiveRepaymentStartDate = start,
            }), ct);
        return Ok(SalaryAdvanceDto.Project(adv));
        }, ct);
    }

    /// <summary>
    /// Pending or Approved → Cancelled: the unpaid exit. HR or Finance cancel any unpaid advance in their data scope; the
    /// requester and the employee the advance pays may withdraw their own while it is Pending. A reason is required. Nothing was
    /// posted for an unpaid advance, so nothing is reversed; an approved advance's schedule is retired. A paid (Active) advance
    /// is recovered through payroll and cannot be cancelled here. Serialized on the advance lock, so a cancel and a payout
    /// racing on one advance cannot both win.
    /// </summary>
    [HttpPost("{id:guid}/cancel")]
    [HasPermission("loans.self", "loans.write")]
    public Task<IActionResult> Cancel(Guid id, [FromBody] AdvanceCancelRequest req, CancellationToken ct)
    {
        var tid = GetTenantId();
        var uid = GetUserId();
        return FinanceDecisionSerializer.SerializeAsync<IActionResult>(
            _db, FinanceDecisionSerializer.ScopeAdvance, tid, id, async () =>
        {
        if (uid == null) return Unauthorized();
        var reason = req.Reason?.Trim();
        if (string.IsNullOrEmpty(reason) || reason.Length > 1000)
            return BadRequest(Refusal("advance_cancel_reason_required",
                "A reason of up to 1000 characters is required to cancel an advance.",
                "يلزم ذكر سبب لا يتجاوز 1000 حرف لإلغاء السلفة."));
        var adv = await _db.SalaryAdvances.FirstOrDefaultAsync(x => x.Id == id && x.TenantId == tid && !x.IsDeleted, ct);
        if (adv == null) return NotFound();
        if (!await CanAccessAdvanceAsync(adv, ct)) return Forbid();
        // Who: HR and Finance may cancel any unpaid advance in their scope. Anyone else only their own (the one they requested,
        // or the one that pays them), and only while it is Pending: once approved, withdrawing it is an HR or Finance decision.
        var ownId = await CallerEmployeeResolver.ResolveAsync(_db, User, tid, ct);
        var isOwn = adv.CreatedBy == uid || (ownId.HasValue && adv.EmployeeIntId == ownId);
        var hrOrFinance = IsHrActor() || IsFinanceActor();
        if (!isOwn && !hrOrFinance) return Forbid();
        if (!hrOrFinance && adv.Status == AdvanceStatuses.Approved)
            return StatusCode(StatusCodes.Status403Forbidden, Refusal("advance_cancel_own_pending_only",
                "You can cancel your own advance only while it is pending. Ask HR or Finance to cancel an approved advance.",
                "يمكنك إلغاء سلفتك فقط وهي قيد الانتظار. اطلب من الموارد البشرية أو المالية إلغاء السلفة المعتمدة."));
        if (adv.Status is not (AdvanceStatuses.Pending or AdvanceStatuses.Approved))
            return Conflict(Refusal("invalid_advance_state",
                $"Only a pending or approved advance that has not been paid can be cancelled (current: {adv.Status}). A paid advance is recovered through payroll.",
                $"لا يمكن إلغاء إلا سلفة معلقة أو معتمدة لم تُصرف بعد (الحالة الحالية: {adv.Status}). السلفة المصروفة تُسترد عبر الرواتب."));
        // Defence in depth: an unpaid advance has no journal; if one exists it must be reversed, not cancelled.
        if (await DisbursementAlreadyPostedAsync(tid, id, ct))
            return Conflict(Refusal("advance_already_paid",
                $"Advance {adv.AdvanceNumber} already has a disbursement journal and cannot be cancelled.",
                $"يوجد قيد صرف للسلفة {adv.AdvanceNumber} ولا يمكن إلغاؤها."));

        var oldStatus = adv.Status;
        adv.Status = AdvanceStatuses.Cancelled;
        adv.CancelledAtUtc = DateTime.UtcNow; adv.CancelReason = reason;
        adv.UpdatedAtUtc = DateTime.UtcNow; adv.UpdatedBy = uid;
        var schedule = await _db.AdvanceInstallments
            .Where(x => x.TenantId == tid && x.AdvanceId == id && x.Status == "Pending").ToListAsync(ct);
        foreach (var row in schedule) row.Status = AdvanceStatuses.Cancelled;
        await _db.SaveChangesAsync(ct);
        await WriteAdvanceAudit(tid, uid, id, "AdvanceCancelled",
            JsonSerializer.Serialize(new { Status = oldStatus }),
            JsonSerializer.Serialize(new { adv.Status, Reason = reason, CancelledInstallments = schedule.Count }), ct);
        return Ok(SalaryAdvanceDto.Project(adv));
        }, ct);
    }

    /// <summary>
    /// Refused, exactly as <c>LoansController.MarkInstallmentPaid</c> refuses it. This route let an operator write
    /// any amount (negative included) straight onto an installment, retire the balance and post a cash receipt,
    /// with no payment reference, no bounds, no data-scope check and no bar on the borrower recording their own
    /// repayment. Salary advances are collected by payroll deduction (<c>PayrollController.Process</c> takes every
    /// Active advance and stamps the installment it funded), and a payroll-deducted loan likewise cannot take a
    /// standalone receipt. A cash receipt against an advance would need its own receipt record with a unique
    /// payment reference, which advances do not have yet.
    /// </summary>
    [HttpPatch("{id:guid}/installments/{installmentId:guid}/pay")]
    [Authorize(Roles = "Admin,Finance,HR Manager")]
    public Task<IActionResult> MarkInstallmentPaid(Guid id, Guid installmentId, [FromBody] PayAdvanceInstallmentRequest req, CancellationToken ct) =>
        Task.FromResult<IActionResult>(Conflict(Refusal("advance_installment_manual_payment_refused",
            "Salary advances are repaid through payroll deduction. An installment cannot be marked paid by hand; a cash repayment needs a receipt with a payment reference, which is not available for advances yet.",
            "تُسترد سلف الرواتب عبر الخصم من الرواتب. لا يمكن تسجيل سداد قسط يدويًا؛ السداد النقدي يحتاج إلى إيصال بمرجع دفع، وهو غير متاح للسلف بعد.")));

    // ── Audit & Reconciliation Report ────────────────────────────────────────

    // The role list resolves to loans.read, which the seeded line Manager also holds (for their own team's
    // loans), and this report read every advance in the tenant with the employee's name and amounts. It now
    // applies the caller's data scope, as List and LoansController.AuditReport do: an org-wide finance or HR
    // reader sees the book, a line Manager sees only their reporting line.
    [HttpGet("audit")]
    [Authorize(Roles = "Admin,Finance,HR Manager")]
    public async Task<IActionResult> AuditReport(CancellationToken ct)
    {
        var tid = GetTenantId();
        var scope = await _scopeService.ResolveAsync(User, tid, ct);
        var q = _db.SalaryAdvances.Where(x => x.TenantId == tid && !x.IsDeleted);
        if (!scope.IsUnrestricted)
        {
            var allowedEmployeeIds = scope.AllowedEmployeeIds!.ToArray();
            q = q.Where(x => x.EmployeeIntId.HasValue && allowedEmployeeIds.Contains(x.EmployeeIntId.Value));
        }
        var advances = await q.OrderByDescending(x => x.CreatedAtUtc).ToListAsync(ct);
        var advanceIds = advances.Select(x => x.Id).ToArray();
        var glEntries = await _db.FinanceGlEntries
            .Where(x => x.TenantId == tid && x.SourceModule == "Advance"
                && (scope.IsUnrestricted || advanceIds.Contains(x.SourceEntityId))).ToListAsync(ct);
        // Audit P1-5 — the sub-ledger against the 1410 balance, per company, for an org-wide reader only: a team-scoped
        // reader's sub-ledger is partial (the comparison would be meaningless) and the company GL balance is not theirs.
        IReadOnlyList<AdvanceReceivableTieOutRow>? tieOut = null;
        if (scope.IsUnrestricted)
        {
            var entityScope = this.GetEntityScope();
            tieOut = await AdvanceReceivableTieOut.ComputeAsync(_db, tid,
                entityScope.IsGroupLevel ? null : entityScope.AccessibleCompanyIds.ToArray(), ct);
        }

        return Ok(new
        {
            GeneratedAtUtc = DateTime.UtcNow,
            TotalAdvances = advances.Count,
            ActiveAdvances = advances.Count(x => x.Status == AdvanceStatuses.Active),
            // Payroll closes a fully deducted advance as "Closed"; a manual settlement used "Settled". Both are settled.
            SettledAdvances = advances.Count(x => x.Status is AdvanceStatuses.Settled or AdvanceStatuses.Closed),
            PendingAdvances = advances.Count(x => x.Status == AdvanceStatuses.Pending),
            // Approved and waiting for Finance to pay: agreed, not disbursed.
            ApprovedAwaitingPayout = advances.Count(x => x.Status == AdvanceStatuses.Approved),
            CancelledAdvances = advances.Count(x => x.Status == AdvanceStatuses.Cancelled),
            // Only money that was paid out: an approved advance awaiting payout has disbursed nothing.
            TotalDisbursed = advances.Where(x => AdvanceStatuses.IsPaid(x.Status)).Sum(x => x.ApprovedAmount),
            TotalOutstanding = advances.Sum(x => x.OutstandingBalance),
            TotalRepaid = advances.Sum(x => x.TotalRepaid),
            GlEntriesCount = glEntries.Count,
            ReceivableTieOut = tieOut,
            Reconciliation = advances.Select(a =>
            {
                var disbursed = AdvanceStatuses.IsPaid(a.Status) ? a.ApprovedAmount : 0m;
                return new
                {
                    a.AdvanceNumber, a.EmployeeName, a.Status, a.ApprovedAmount,
                    a.TotalRepaid, a.OutstandingBalance,
                    BalanceCheck = Math.Round(disbursed - a.TotalRepaid - a.OutstandingBalance, 2),
                    IsReconciled = Math.Abs(disbursed - a.TotalRepaid - a.OutstandingBalance) < 0.01m,
                };
            }).ToList(),
        });
    }

    private void GenerateAdvanceInstallments(Guid tid, SalaryAdvance adv, DateOnly? startDate)
    {
        var start = startDate ?? DateOnly.FromDateTime(DateTime.UtcNow.AddMonths(1));
        var scheduled = 0m;
        for (int i = 1; i <= adv.Installments; i++)
        {
            var amount = i == adv.Installments ? adv.ApprovedAmount - scheduled : adv.InstallmentAmount;
            scheduled += amount;
            _db.AdvanceInstallments.Add(new AdvanceInstallment
            {
                TenantId = tid, AdvanceId = adv.Id, InstallmentNumber = i,
                DueDate = start.AddMonths(i - 1), AmountDue = amount, Status = "Pending",
            });
        }
    }

    /// <summary>
    /// Posts one balanced salary-advance journal line.
    ///
    /// <para>POD-B1b — accounts are DRIVER KEYS resolved company-first through the same
    /// <see cref="GlAccountResolver"/> payroll uses (was: hard-coded "1410 - Employee Salary Advances" /
    /// "1000 - Cash/Bank" labels + the tenant currency, which posted to the wrong entity's account in a
    /// multi-company tenant). Driver defaults reproduce the identical labels, so a single-company tenant
    /// sees no change.</para>
    /// </summary>
    private async Task PostGlEntry(Guid tid, Guid? uid, Guid? companyId, Guid entityId, string entityRef,
        string module, string eventType, string debitDriverKey, string creditDriverKey,
        decimal amount, string? currency, DateOnly entryDate, CancellationToken ct)
    {
        var resolvedCurrency = string.IsNullOrWhiteSpace(currency)
            ? await GlAccountResolver.ResolveCurrencyAsync(_db, tid, companyId, ct)
            : currency;
        // POD-B1 (req-4) — no GL posting into a closed period. Single choke point covers every advance
        // disbursement post. POD-B1b tightens it from a group-wide close to the advance's OWN company (a
        // group-wide close still blocks — PeriodCloseGuard.cs:31). Throws BEFORE the Add so nothing is
        // persisted. Payout refuses a closed period with a coded 422 first; this stays as the backstop.
        await PeriodCloseGuard.ThrowIfClosedAsync(_db, tid, companyId, entryDate.ToString("yyyy-MM"), ct);
        var glCtx = await GlAccountResolver.LoadAsync(_db, tid, companyId, ct);
        _db.FinanceGlEntries.Add(new FinanceGlEntry
        {
            TenantId = tid, CompanyId = companyId, SourceModule = module, SourceEntityId = entityId,
            SourceEntityRef = entityRef, EventType = eventType,
            DebitAccount = GlAccountResolver.AccountLabel(debitDriverKey, glCtx),
            CreditAccount = GlAccountResolver.AccountLabel(creditDriverKey, glCtx),
            Amount = amount, Currency = resolvedCurrency,
            EntryDate = entryDate, Period = entryDate.ToString("yyyy-MM"),
            Description = $"{module} {eventType}: {entityRef}",
            PostedBy = uid, PostedByName = GetUserName(),
        });
    }

    /// <summary>POD-B1b — legal entity for an advance journal; see LoansController.ResolveLoanCompanyAsync
    /// for why the employee fallback is needed (the create path posts before CompanyId is stamped).</summary>
    private async Task<Guid?> ResolveAdvanceCompanyAsync(Guid tid, SalaryAdvance adv, CancellationToken ct)
    {
        if (adv.CompanyId is Guid cid) return cid;
        if (adv.EmployeeIntId is not int empId) return null;
        // IgnoreQueryFilters: server-side attribution must see the employee row regardless of the
        // actor's own company scope; the TenantId predicate keeps this tenant-contained.
        return await _db.Employees.IgnoreQueryFilters().AsNoTracking()
            .Where(e => e.TenantId == tid && e.Id == empId)
            .Select(e => e.CompanyId)
            .FirstOrDefaultAsync(ct);
    }

    /// <summary>POD-B1b — an advance may only be disbursed to GL once; a replayed payout (or a legacy advance
    /// disbursed at approval by the old flow) must never credit Cash/Bank twice.</summary>
    private Task<bool> DisbursementAlreadyPostedAsync(Guid tid, Guid advanceId, CancellationToken ct) =>
        // IgnoreQueryFilters is intentional: a post-once probe must see the advance's existing journal
        // whatever company it was attributed to; the TenantId + SourceEntityId predicate keeps it
        // tenant-contained and it reads no other tenant.
        _db.FinanceGlEntries.IgnoreQueryFilters().AsNoTracking()
            .AnyAsync(x => x.TenantId == tid && x.SourceModule == "Advance"
                        && x.SourceEntityId == advanceId && x.EventType == "Disbursement" && !x.IsReversed, ct);

    /// <summary>Approve's refusals, unchanged: a bare-string 400 where Reject uses a coded 409.
    /// The asymmetry is pre-existing and preserved deliberately.</summary>
    private IActionResult AdvanceApproveRefusal(ApprovalGuardVerdict verdict) => verdict.Outcome switch
    {
        ApprovalGuardOutcome.ParentNotFound => NotFound(),
        ApprovalGuardOutcome.ParentStateForbidsDecision => BadRequest(Refusal("invalid_advance_state",
            "Advance is not in Pending status.", "السلفة ليست في حالة انتظار.")),
        ApprovalGuardOutcome.MakerIsChecker => BadRequest(Refusal("advance_maker_is_checker", verdict.Message!,
            "ضابط الفصل بين المهام: لا يمكن لمقدم الطلب اعتماد سلفة الراتب التي طلبها.")),
        ApprovalGuardOutcome.SubjectIsDecider => BadRequest(Refusal("advance_subject_is_decider", verdict.Message!,
            "الفصل بين المهام: لا يمكنك اعتماد سلفة راتب مدفوعة لك.")),
        _ => throw new InvalidOperationException($"Unhandled approval guard outcome '{verdict.Outcome}'."),
    };

    /// <summary>Reject's refusals, unchanged: a coded 409 where Approve uses a bare-string 400.</summary>
    private IActionResult AdvanceRejectRefusal(ApprovalGuardVerdict verdict, string? advanceStatus) => verdict.Outcome switch
    {
        ApprovalGuardOutcome.ParentNotFound => NotFound(),
        ApprovalGuardOutcome.ParentStateForbidsDecision =>
            Conflict(Refusal("invalid_advance_state",
                $"Only a Pending advance can be rejected (current: {advanceStatus}).",
                $"لا يمكن رفض إلا سلفة في حالة انتظار (الحالة الحالية: {advanceStatus}).")),
        _ => throw new InvalidOperationException($"Unhandled approval guard outcome '{verdict.Outcome}'."),
    };

    private async Task<bool> IsAdvanceEmployeeAsync(SalaryAdvance adv, Guid userId, CancellationToken ct) =>
        // As LoansController.IsLoanBorrowerAsync: the exclusion must survive a company transfer, and it
        // returns only a boolean about the authenticated user and the advance already loaded.
        await Zayra.Api.Infrastructure.Data.ScopedBypass.NullableTenantWide(_db.Employees, adv.TenantId,
            "Exclude the authenticated employee from approving a salary advance paid to them, even after a legal-entity transfer.")
            .AnyAsync(x => x.UserAccountId == userId
                && (x.Id == adv.EmployeeIntId || x.PublicId == adv.EmployeeId), ct);

    private bool IsHrActor() => User.IsInRole("Admin") || User.IsInRole("HR Manager") || User.IsInRole("HR Director");
    private bool IsFinanceActor() => User.IsInRole("Admin") || User.IsInRole("Finance");
    /// <summary>Same money shape as LoansController.IsMoney: positive, two decimals, within the column.</summary>
    private static bool IsMoney(decimal value) => value > 0 && value <= 999999999999.99m && decimal.Round(value, 2) == value;

    /// <summary>Same limit as the payment_reference column.</summary>
    private const int MaxPaymentReferenceLength = 100;

    /// <summary>Every refusal names a machine code and carries the sentence in English and Arabic, for the reader's language.</summary>
    private static object Refusal(string code, string message, string messageAr) => new { error = code, message, messageAr };

    private async Task<bool> CanAccessAdvanceAsync(SalaryAdvance adv, CancellationToken ct)
    {
        var scope = await _scopeService.ResolveAsync(User, adv.TenantId, ct);
        return scope.IsUnrestricted || (adv.EmployeeIntId is int empId && scope.CanAccessEmployee(empId));
    }

    /// <summary>The policy's installment rule for an amount: 1..MaxInstallments, exactly 1 when installments are
    /// not allowed, and every installment at least 0.01. Null when the request complies.</summary>
    private static object? InstallmentsRefusal(AdvancePolicy? policy, decimal amount, int installments)
    {
        var max = policy == null ? MaxInstallmentsWithoutPolicy
            : !policy.AllowInstallments ? 1
            : Math.Clamp(policy.MaxInstallments, 1, MaxInstallmentsWithoutPolicy);
        if (installments < 1 || installments > max)
            return max == 1
                ? Refusal("advance_installments_invalid", "This advance policy requires repayment in a single installment.",
                    "تشترط سياسة السلف السداد على قسط واحد.")
                : Refusal("advance_installments_invalid", $"Installments must be between 1 and {max}.",
                    $"يجب أن يكون عدد الأقساط بين 1 و{max}.");
        if (amount < installments * .01m)
            return Refusal("advance_installments_invalid", "Each installment must be at least 0.01.", "يجب ألا يقل كل قسط عن 0.01.");
        return null;
    }

    /// <summary>
    /// The policy's MaxPercentageOfSalary cap, against the employee's monthly wage as the loan module computes it
    /// (<see cref="LoanEligibilityService.MonthlyWage"/>: the salary structure in force today, basic plus fixed
    /// allowances, in the company's currency). An unknown wage refuses rather than lets the request through.
    /// Null when the amount is within the cap or the policy sets no percentage.
    /// </summary>
    private async Task<object?> SalaryCapRefusalAsync(Guid tid, Employee employee, AdvancePolicy policy, decimal amount, CancellationToken ct)
    {
        if (policy.MaxPercentageOfSalary <= 0) return null;
        if (await SalaryCapAsync(tid, employee, policy, ct) is not { } cap)
            return Refusal("advance_salary_unknown",
                "An active salary structure in the company's currency is required to apply the advance policy's salary cap.",
                "يلزم هيكل راتب نشط بعملة الشركة لتطبيق حد الراتب في سياسة السلف.");
        return amount > cap.Amount
            ? Refusal("advance_salary_cap",
                $"The amount exceeds the policy limit of {policy.MaxPercentageOfSalary:0.##}% of monthly salary ({cap.Amount:0.00} {cap.Currency}).",
                $"يتجاوز المبلغ حد السياسة البالغ {policy.MaxPercentageOfSalary:0.##}% من الراتب الشهري ({cap.Amount:0.00} {cap.Currency}).")
            : null;
    }

    /// <summary>The policy's percentage of the employee's monthly wage, in the company's currency; null when the wage is unknown.</summary>
    private async Task<(decimal Amount, string Currency)?> SalaryCapAsync(Guid tid, Employee employee, AdvancePolicy policy, CancellationToken ct)
    {
        var salary = await new LoanEligibilityService(_db).SalaryAsOfAsync(tid, employee.Id, DateOnly.FromDateTime(DateTime.UtcNow), ct);
        var wage = LoanEligibilityService.MonthlyWage(salary);
        var currency = await GlAccountResolver.ResolveCurrencyAsync(_db, tid, employee.CompanyId, ct);
        if (salary == null || wage <= 0 || !string.Equals(salary.Currency, currency, StringComparison.OrdinalIgnoreCase))
            return null;
        return (decimal.Round(wage * policy.MaxPercentageOfSalary / 100m, 2, MidpointRounding.ToZero), currency);
    }

    /// <summary>
    /// Decision G2: the salary cap covers everything the employee owes or has asked for, not each request alone. A second 50%
    /// advance used to pass while the first was still fully outstanding. The open exposure is the requested amount of Pending
    /// requests, the approved amount of advances awaiting payout and the outstanding balance of Active ones (this advance
    /// excluded at approval); with the new amount it must stay within the cap. Null when within it, when nothing else is open
    /// (the per-request check has decided), or when the policy sets no percentage.
    /// </summary>
    private async Task<object?> CumulativeCapRefusalAsync(Guid tid, Employee employee, AdvancePolicy policy, decimal amount,
        Guid? excludeAdvanceId, CancellationToken ct)
    {
        if (policy.MaxPercentageOfSalary <= 0) return null;
        var open = await _db.SalaryAdvances.AsNoTracking()
            .Where(x => x.TenantId == tid && x.EmployeeIntId == employee.Id && !x.IsDeleted && x.Id != excludeAdvanceId
                && (x.Status == AdvanceStatuses.Pending || x.Status == AdvanceStatuses.Approved || x.Status == AdvanceStatuses.Active))
            .Select(x => new { x.Status, x.RequestedAmount, x.ApprovedAmount, x.OutstandingBalance })
            .ToListAsync(ct);
        var owed = open.Sum(x => x.Status == AdvanceStatuses.Pending ? x.RequestedAmount
            : x.Status == AdvanceStatuses.Approved ? x.ApprovedAmount : x.OutstandingBalance);
        if (owed <= 0m) return null;
        // An unknown wage was already refused by the per-request check.
        if (await SalaryCapAsync(tid, employee, policy, ct) is not { } cap || owed + amount <= cap.Amount) return null;
        var headroom = Math.Max(0m, cap.Amount - owed);
        return Refusal("advance_cumulative_limit",
            $"This advance and the {owed:0.00} {cap.Currency} already requested or owed on other advances exceed the policy limit of " +
            $"{policy.MaxPercentageOfSalary:0.##}% of monthly salary ({cap.Amount:0.00} {cap.Currency}). At most {headroom:0.00} {cap.Currency} more can be advanced.",
            $"هذه السلفة مع {owed:0.00} {cap.Currency} مطلوبة أو مستحقة على سلف أخرى تتجاوز حد السياسة البالغ " +
            $"{policy.MaxPercentageOfSalary:0.##}% من الراتب الشهري ({cap.Amount:0.00} {cap.Currency}). الحد الأقصى الإضافي الممكن {headroom:0.00} {cap.Currency}.");
    }

    /// <summary>
    /// Audit P3 and decision G1: an advance is for an employee who is serving, the loan module's default eligibility
    /// (LoanEligibilityService: allowed statuses Active, BlockDuringNotice). Not Active (Offboarded, Suspended, Terminated…),
    /// deleted, or on notice (an offboarding in progress) is refused, at request, approval and payout. Null when eligible.
    /// </summary>
    private async Task<object?> EmploymentRefusalAsync(Guid tid, Employee? employee, CancellationToken ct)
    {
        if (employee == null)
            return Refusal("advance_employee_missing",
                "The advance has no linked employee, so their employment cannot be checked.",
                "السلفة غير مرتبطة بموظف، فلا يمكن التحقق من حالة توظيفه.");
        var onNotice = await _db.EmployeeOffboardings.AsNoTracking()
            .AnyAsync(x => x.TenantId == tid && x.EmployeeId == employee.Id && x.Status == "InProgress", ct);
        if (employee.IsDeleted || onNotice || !string.Equals(employee.Status, EmployeeStatuses.Active, StringComparison.OrdinalIgnoreCase))
            return Refusal("advance_employee_not_eligible",
                $"A salary advance is only for an active employee who is not serving notice (status: {employee.Status}{(onNotice ? ", offboarding in progress" : string.Empty)}).",
                $"سلفة الراتب للموظف النشط غير المنتهية خدمته فقط (الحالة: {employee.Status}{(onNotice ? "، إجراءات إنهاء الخدمة جارية" : string.Empty)}).");
        return null;
    }

    /// <summary>The advance's employee, even after a legal-entity transfer (the bar and the cap follow the person).</summary>
    private async Task<Employee?> LoadAdvanceEmployeeAsync(Guid tid, SalaryAdvance adv, CancellationToken ct) =>
        adv.EmployeeIntId is int empId
            ? await ScopedBypass.NullableTenantWide(_db.Employees, tid,
                    "Read the advance's employee to check employment and apply the salary cap, even after a legal-entity transfer.")
                .AsNoTracking().FirstOrDefaultAsync(x => x.Id == empId, ct)
            : null;

    /// <summary>Audit P3: the first repayment date may not be before yesterday (a request typed late in the day in another
    /// timezone) nor more than twelve months out. Null when no date is given or it is inside the window.</summary>
    private static object? StartDateRefusal(DateOnly? start)
    {
        if (start is not { } date) return null;
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        return date < today.AddDays(-1) || date > today.AddMonths(12)
            ? Refusal("advance_repayment_start_invalid",
                "The first repayment date must be between yesterday and twelve months from today.",
                "يجب أن يكون تاريخ أول سداد بين الأمس واثني عشر شهراً من اليوم.")
            : null;
    }

    /// <summary>
    /// Audit P3: the next advance number for this year, after the highest one already used in the tenant, whatever company,
    /// deleted or imported. It was a count of the caller's visible rows plus one, which collided with an imported or deleted
    /// number (or another company's) and failed the unique index with a 500. Called under the tenant's create lock.
    /// </summary>
    private async Task<string> NextAdvanceNumberAsync(Guid tid, CancellationToken ct)
    {
        var prefix = $"ADV-{DateTime.UtcNow.Year}-";
        var used = await ScopedBypass.TenantWide(_db.SalaryAdvances, tid,
                "Allocate the next advance number: the unique index is tenant-wide, across every company and deleted rows.")
            .AsNoTracking().Where(x => x.AdvanceNumber.StartsWith(prefix)).Select(x => x.AdvanceNumber).ToListAsync(ct);
        var highest = used
            .Select(n => int.TryParse(n.AsSpan(prefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out var v) ? v : 0)
            .DefaultIfEmpty(0).Max();
        return $"{prefix}{highest + 1:D5}";
    }

    private async Task WriteAdvanceAudit(Guid tid, Guid? uid, Guid advId, string action, string? oldVal, string newVal, CancellationToken ct)
    {
        _db.AdvanceAuditLogs.Add(new AdvanceAuditLog
        {
            TenantId = tid, AdvanceId = advId, Action = action,
            OldValuesJson = oldVal ?? string.Empty, NewValuesJson = newVal,
            PerformedBy = uid, PerformedByName = GetUserName(),
        });
        await _db.SaveChangesAsync(ct);
    }
}

public record AdvancePolicyRequest(string PolicyName, decimal MaxPercentageOfSalary, int MaxAdvancesPerYear, int MinServiceMonths, bool AllowInstallments, int MaxInstallments, int CooldownMonths, bool RequiresApproval);
public record CreateAdvanceRequest(Guid EmployeeId, string EmployeeName, decimal RequestedAmount, string RepaymentType, int Installments, DateOnly? RepaymentStartDate, string? Reason, int? EmployeeIntId = null);
public record AdvanceApproveRequest(decimal ApprovedAmount, int Installments, DateOnly? RepaymentStartDate);
/// <summary>Payout of an approved advance. <paramref name="PaidDate"/> defaults to today; <paramref name="Reference"/> is the
/// bank or cash reference (required, at most 100 characters).</summary>
public record AdvancePayoutRequest(DateOnly? PaidDate, string? Reference, string? Notes);
/// <summary>Cancellation of an unpaid advance; <paramref name="Reason"/> is required (at most 1000 characters).</summary>
public record AdvanceCancelRequest(string? Reason);
public record RejectRequest(string? Reason);
public record PayAdvanceInstallmentRequest(decimal AmountPaid, DateOnly PaidDate, Guid? PayrollRunId);
