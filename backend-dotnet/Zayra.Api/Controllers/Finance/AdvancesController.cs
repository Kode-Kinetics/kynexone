using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using Zayra.Api.Application.Common;
using Zayra.Api.Application.Finance;
using Zayra.Api.Data;
using Zayra.Api.Models;

namespace Zayra.Api.Controllers.Finance;

[Authorize]
[ApiController]
[Route("api/finance/advances")]
public class AdvancesController : ControllerBase
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
    private string GetUserName() => User.FindFirst(System.Security.Claims.ClaimTypes.Name)?.Value ?? "Unknown";

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
    [Authorize(Roles = "Admin,HR Manager")]
    public async Task<IActionResult> UpsertPolicy([FromBody] AdvancePolicyRequest req, CancellationToken ct)
    {
        var tid = GetTenantId();
        var uid = GetUserId();
        var p = await _db.AdvancePolicies.FirstOrDefaultAsync(x => x.TenantId == tid && x.IsActive, ct);
        if (p != null)
        {
            p.PolicyName = req.PolicyName; p.MaxPercentageOfSalary = req.MaxPercentageOfSalary;
            p.MaxAdvancesPerYear = req.MaxAdvancesPerYear; p.MinServiceMonths = req.MinServiceMonths;
            p.AllowInstallments = req.AllowInstallments; p.MaxInstallments = req.MaxInstallments;
            p.CooldownMonths = req.CooldownMonths; p.RequiresApproval = req.RequiresApproval;
            await _db.SaveChangesAsync(ct);
            // SAFE-SERIALIZATION: AdvancePolicy is tenant config (percentage caps, cooldowns) — no personal PII.
            return Ok(p);
        }
        p = new AdvancePolicy
        {
            TenantId = tid, PolicyName = req.PolicyName, MaxPercentageOfSalary = req.MaxPercentageOfSalary,
            MaxAdvancesPerYear = req.MaxAdvancesPerYear, MinServiceMonths = req.MinServiceMonths,
            AllowInstallments = req.AllowInstallments, MaxInstallments = req.MaxInstallments,
            CooldownMonths = req.CooldownMonths, RequiresApproval = req.RequiresApproval, CreatedBy = uid,
        };
        _db.AdvancePolicies.Add(p);
        await _db.SaveChangesAsync(ct);
        // SAFE-SERIALIZATION: AdvancePolicy is tenant config (percentage caps, cooldowns) — no personal PII.
        return Ok(p);
    }

    // ── Salary Advances ───────────────────────────────────────────────────────

    [HttpGet]
    public async Task<IActionResult> List(
        [FromQuery] Guid? employeeId, [FromQuery] string? status,
        [FromQuery] int page = 1, [FromQuery] int pageSize = 30, CancellationToken ct = default)
    {
        var tid = GetTenantId();
        var scope = await _scopeService.ResolveAsync(User, tid, ct);
        var q = _db.SalaryAdvances.Where(x => x.TenantId == tid && !x.IsDeleted);

        if (!scope.IsUnrestricted)
        {
            var callerUserId = GetUserId();
            if (callerUserId.HasValue)
            {
                var allowedId = callerUserId.Value;
                var effectiveId = (employeeId.HasValue && employeeId.Value == allowedId) ? employeeId : allowedId;
                q = q.Where(x => x.EmployeeId == effectiveId);
            }
            else
            {
                return Ok(new { total = 0, items = Array.Empty<SalaryAdvanceDto>() });
            }
        }
        else if (employeeId.HasValue)
        {
            q = q.Where(x => x.EmployeeId == employeeId);
        }

        if (!string.IsNullOrEmpty(status)) q = q.Where(x => x.Status == status);
        var total = await q.CountAsync(ct);
        var items = await q.OrderByDescending(x => x.CreatedAtUtc)
            .Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
        return Ok(new { total, items = items.Select(SalaryAdvanceDto.Project).ToList() });
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct)
    {
        var tid = GetTenantId();
        var adv = await _db.SalaryAdvances.FirstOrDefaultAsync(x => x.Id == id && x.TenantId == tid && !x.IsDeleted, ct);
        if (adv == null) return NotFound();
        var installments = await _db.AdvanceInstallments.Where(x => x.AdvanceId == id).OrderBy(x => x.InstallmentNumber).ToListAsync(ct);
        var approvals = await _db.AdvanceApprovals.Where(x => x.AdvanceId == id).OrderBy(x => x.StepOrder).ToListAsync(ct);
        var auditLogs = await _db.AdvanceAuditLogs.Where(x => x.AdvanceId == id).OrderByDescending(x => x.CreatedAtUtc).ToListAsync(ct);
        var glEntries = await _db.FinanceGlEntries.Where(x => x.SourceEntityId == id).OrderByDescending(x => x.EntryDate).ToListAsync(ct);
        return Ok(new { advance = SalaryAdvanceDto.Project(adv), installments, approvals, auditLogs, glEntries });
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateAdvanceRequest req, CancellationToken ct)
    {
        var tid = GetTenantId();
        var uid = GetUserId();
        var policy = await _db.AdvancePolicies.FirstOrDefaultAsync(x => x.TenantId == tid && x.IsActive, ct);

        // Policy compliance checks
        // Auto-generate EmployeeId Guid when client omits it (frontend uses EmployeeIntId for payroll)
        var resolvedEmployeeId = req.EmployeeId == Guid.Empty ? Guid.NewGuid() : req.EmployeeId;

        if (policy != null && req.EmployeeIntId.HasValue)
        {
            var currentYear = DateTime.UtcNow.Year;
            var advancesThisYear = await _db.SalaryAdvances.CountAsync(
                x => x.TenantId == tid && x.EmployeeIntId == req.EmployeeIntId && !x.IsDeleted
                     && x.CreatedAtUtc.Year == currentYear, ct);
            if (advancesThisYear >= policy.MaxAdvancesPerYear)
                return BadRequest($"Employee has already used {advancesThisYear} advance(s) this year. Maximum allowed is {policy.MaxAdvancesPerYear}.");

            if (policy.CooldownMonths > 0)
            {
                var cooldownCutoff = DateTime.UtcNow.AddMonths(-policy.CooldownMonths);
                var recentlySettled = await _db.SalaryAdvances.AnyAsync(
                    x => x.TenantId == tid && x.EmployeeIntId == req.EmployeeIntId && !x.IsDeleted
                         && x.Status == "Settled" && x.UpdatedAtUtc > cooldownCutoff, ct);
                if (recentlySettled)
                    return BadRequest($"Employee must wait {policy.CooldownMonths} month(s) after settling an advance before requesting a new one.");
            }
        }

        var count = await _db.SalaryAdvances.CountAsync(x => x.TenantId == tid, ct);
        var advNumber = $"ADV-{DateTime.UtcNow.Year}-{(count + 1):D5}";

        var adv = new SalaryAdvance
        {
            TenantId = tid, EmployeeId = resolvedEmployeeId, EmployeeName = req.EmployeeName,
            EmployeeIntId = req.EmployeeIntId,
            AdvanceNumber = advNumber, RequestedAmount = req.RequestedAmount,
            RepaymentType = req.RepaymentType, Installments = req.Installments,
            Reason = req.Reason ?? string.Empty,
            Status = (policy?.RequiresApproval ?? true) ? "Pending" : "Approved",
            CreatedBy = uid,
        };

        if (!(policy?.RequiresApproval ?? true))
        {
            adv.ApprovedAmount = req.RequestedAmount;
            adv.InstallmentAmount = req.Installments > 0 ? req.RequestedAmount / req.Installments : req.RequestedAmount;
            adv.OutstandingBalance = req.RequestedAmount;
            adv.Status = "Active";
            GenerateAdvanceInstallments(tid, adv, req.RepaymentStartDate);
            await PostGlEntry(tid, uid, adv.Id, adv.AdvanceNumber, "Advance", "Disbursement",
                "1410 - Employee Salary Advances", "1000 - Cash/Bank", adv.ApprovedAmount, null, ct);
        }

        _db.SalaryAdvances.Add(adv);
        await _db.SaveChangesAsync(ct);
        await WriteAdvanceAudit(tid, uid, adv.Id, "AdvanceRequested", null,
            JsonSerializer.Serialize(new { adv.AdvanceNumber, adv.RequestedAmount, adv.Status }), ct);
        return Ok(SalaryAdvanceDto.Project(adv));
    }

    [HttpPatch("{id:guid}/approve")]
    [Authorize(Roles = "Admin,HR Manager,Finance,Manager")]
    public async Task<IActionResult> Approve(Guid id, [FromBody] AdvanceApproveRequest req, CancellationToken ct)
    {
        var tid = GetTenantId();
        var uid = GetUserId();
        var adv = await _db.SalaryAdvances.FirstOrDefaultAsync(x => x.Id == id && x.TenantId == tid, ct);
        if (adv == null) return NotFound();
        if (adv.Status != "Pending") return BadRequest("Advance is not in Pending status.");

        var oldStatus = adv.Status;
        adv.Status = "Active"; adv.ApprovedAmount = req.ApprovedAmount;
        adv.InstallmentAmount = req.Installments > 0 ? req.ApprovedAmount / req.Installments : req.ApprovedAmount;
        adv.Installments = req.Installments; adv.OutstandingBalance = req.ApprovedAmount;
        adv.RepaymentStartDate = req.RepaymentStartDate; adv.UpdatedAtUtc = DateTime.UtcNow; adv.UpdatedBy = uid;

        GenerateAdvanceInstallments(tid, adv, req.RepaymentStartDate);

        var approval = new AdvanceApproval
        {
            TenantId = tid, AdvanceId = id, StepOrder = 1, ApproverRole = "HR",
            ApprovedBy = uid, ApprovedByName = GetUserName(), Status = "Approved",
            DecidedAtUtc = DateTime.UtcNow,
        };
        _db.AdvanceApprovals.Add(approval);

        await PostGlEntry(tid, uid, adv.Id, adv.AdvanceNumber, "Advance", "Disbursement",
            "1410 - Employee Salary Advances", "1000 - Cash/Bank", req.ApprovedAmount, null, ct);

        await _db.SaveChangesAsync(ct);
        await WriteAdvanceAudit(tid, uid, id, "AdvanceApproved",
            JsonSerializer.Serialize(new { Status = oldStatus }),
            JsonSerializer.Serialize(new { Status = "Active", ApprovedAmount = req.ApprovedAmount }), ct);
        return Ok(SalaryAdvanceDto.Project(adv));
    }

    [HttpPatch("{id:guid}/reject")]
    [Authorize(Roles = "Admin,HR Manager,Finance,Manager")]
    public async Task<IActionResult> Reject(Guid id, [FromBody] RejectRequest req, CancellationToken ct)
    {
        var tid = GetTenantId();
        var uid = GetUserId();
        var adv = await _db.SalaryAdvances.FirstOrDefaultAsync(x => x.Id == id && x.TenantId == tid, ct);
        if (adv == null) return NotFound();
        var oldStatus = adv.Status;
        adv.Status = "Rejected"; adv.RejectionReason = req.Reason;
        adv.UpdatedAtUtc = DateTime.UtcNow; adv.UpdatedBy = uid;
        await _db.SaveChangesAsync(ct);
        await WriteAdvanceAudit(tid, uid, id, "AdvanceRejected",
            JsonSerializer.Serialize(new { Status = oldStatus }),
            JsonSerializer.Serialize(new { Status = "Rejected", Reason = req.Reason }), ct);
        return Ok(SalaryAdvanceDto.Project(adv));
    }

    [HttpPatch("{id:guid}/installments/{installmentId:guid}/pay")]
    [Authorize(Roles = "Admin,Finance,HR Manager")]
    public async Task<IActionResult> MarkInstallmentPaid(Guid id, Guid installmentId, [FromBody] PayAdvanceInstallmentRequest req, CancellationToken ct)
    {
        var tid = GetTenantId();
        var uid = GetUserId();
        var inst = await _db.AdvanceInstallments.FirstOrDefaultAsync(x => x.Id == installmentId && x.AdvanceId == id && x.TenantId == tid, ct);
        if (inst == null) return NotFound();
        if (inst.Status == "Paid") return BadRequest("Installment already paid.");

        inst.AmountPaid = req.AmountPaid;
        inst.PaidDate = req.PaidDate;
        inst.PayrollRunId = req.PayrollRunId;
        inst.Status = req.AmountPaid >= inst.AmountDue ? "Paid" : "Pending";

        var adv = await _db.SalaryAdvances.FirstAsync(x => x.Id == id && x.TenantId == tid, ct);
        adv.TotalRepaid += req.AmountPaid;
        adv.OutstandingBalance = Math.Max(0, adv.OutstandingBalance - req.AmountPaid);
        if (adv.OutstandingBalance == 0) adv.Status = "Settled";
        adv.UpdatedAtUtc = DateTime.UtcNow; adv.UpdatedBy = uid;

        await PostGlEntry(tid, uid, adv.Id, adv.AdvanceNumber, "Advance", "Repayment",
            "1000 - Cash/Bank", "1410 - Employee Salary Advances", req.AmountPaid, null, ct);

        await _db.SaveChangesAsync(ct);
        await WriteAdvanceAudit(tid, uid, id, "InstallmentPaid", null,
            JsonSerializer.Serialize(new { InstallmentNumber = inst.InstallmentNumber, AmountPaid = req.AmountPaid }), ct);
        return Ok(new { installment = inst, advance = adv });
    }

    // ── Audit & Reconciliation Report ────────────────────────────────────────

    [HttpGet("audit")]
    [Authorize(Roles = "Admin,Finance,HR Manager")]
    public async Task<IActionResult> AuditReport(CancellationToken ct)
    {
        var tid = GetTenantId();
        var advances = await _db.SalaryAdvances.Where(x => x.TenantId == tid && !x.IsDeleted)
            .OrderByDescending(x => x.CreatedAtUtc).ToListAsync(ct);
        var glEntries = await _db.FinanceGlEntries
            .Where(x => x.TenantId == tid && x.SourceModule == "Advance").ToListAsync(ct);

        return Ok(new
        {
            GeneratedAtUtc = DateTime.UtcNow,
            TotalAdvances = advances.Count,
            ActiveAdvances = advances.Count(x => x.Status == "Active"),
            SettledAdvances = advances.Count(x => x.Status == "Settled"),
            PendingAdvances = advances.Count(x => x.Status == "Pending"),
            TotalDisbursed = advances.Sum(x => x.ApprovedAmount),
            TotalOutstanding = advances.Sum(x => x.OutstandingBalance),
            TotalRepaid = advances.Sum(x => x.TotalRepaid),
            GlEntriesCount = glEntries.Count,
            Reconciliation = advances.Select(a => new
            {
                a.AdvanceNumber, a.EmployeeName, a.Status, a.ApprovedAmount,
                a.TotalRepaid, a.OutstandingBalance,
                BalanceCheck = Math.Round(a.ApprovedAmount - a.TotalRepaid - a.OutstandingBalance, 2),
                IsReconciled = Math.Abs(a.ApprovedAmount - a.TotalRepaid - a.OutstandingBalance) < 0.01m,
            }).ToList(),
        });
    }

    private void GenerateAdvanceInstallments(Guid tid, SalaryAdvance adv, DateOnly? startDate)
    {
        var start = startDate ?? DateOnly.FromDateTime(DateTime.UtcNow.AddMonths(1));
        for (int i = 1; i <= adv.Installments; i++)
        {
            _db.AdvanceInstallments.Add(new AdvanceInstallment
            {
                TenantId = tid, AdvanceId = adv.Id, InstallmentNumber = i,
                DueDate = start.AddMonths(i - 1), AmountDue = adv.InstallmentAmount, Status = "Pending",
            });
        }
    }

    private async Task PostGlEntry(Guid tid, Guid? uid, Guid entityId, string entityRef,
        string module, string eventType, string debitAccount, string creditAccount,
        decimal amount, string? currency, CancellationToken ct)
    {
        var resolvedCurrency = string.IsNullOrWhiteSpace(currency)
            ? await _db.ResolveTenantCurrencyAsync(tid, ct)
            : currency;
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        _db.FinanceGlEntries.Add(new FinanceGlEntry
        {
            TenantId = tid, SourceModule = module, SourceEntityId = entityId,
            SourceEntityRef = entityRef, EventType = eventType,
            DebitAccount = debitAccount, CreditAccount = creditAccount,
            Amount = amount, Currency = resolvedCurrency,
            EntryDate = today, Period = today.ToString("yyyy-MM"),
            Description = $"{module} {eventType}: {entityRef}",
            PostedBy = uid, PostedByName = GetUserName(),
        });
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
public record RejectRequest(string? Reason);
public record PayAdvanceInstallmentRequest(decimal AmountPaid, DateOnly PaidDate, Guid? PayrollRunId);
