using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Approvals;
using Zayra.Api.Application.Leave;
using Zayra.Api.Data;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Leave;

public class LeaveService : ILeaveService
{
    private readonly ZayraDbContext _db;
    private readonly IApprovalPolicyService _policyService;

    public LeaveService(ZayraDbContext db, IApprovalPolicyService policyService)
    {
        _db = db;
        _policyService = policyService;
    }

    public async Task<EmployeeLeaveBalance> GetOrCreateBalanceAsync(Guid tenantId, int employeeId, Guid leaveTypeId, int year, CancellationToken ct = default)
    {
        var balance = await _db.EmployeeLeaveBalances
            .FirstOrDefaultAsync(b => b.TenantId == tenantId && b.EmployeeId == employeeId && b.LeaveTypeId == leaveTypeId && b.Year == year, ct);

        if (balance is null)
        {
            var leaveType = await _db.LeaveTypes.FirstOrDefaultAsync(t => t.Id == leaveTypeId && t.TenantId == tenantId, ct);
            balance = new EmployeeLeaveBalance
            {
                TenantId = tenantId,
                EmployeeId = employeeId,
                LeaveTypeId = leaveTypeId,
                LeaveTypeName = leaveType?.NameEn ?? string.Empty,
                Year = year
            };
            _db.EmployeeLeaveBalances.Add(balance);
            await _db.SaveChangesAsync(ct);
        }

        return balance;
    }

    public async Task AccrueMonthlyAsync(Guid tenantId, CancellationToken ct = default)
    {
        var activePolicies = await _db.LeavePolicies
            .Where(p => p.TenantId == tenantId && p.Status == "Active" && p.AccrualMethod == "Monthly")
            .ToListAsync(ct);

        var currentYear = DateTime.UtcNow.Year;
        var employees = await _db.Employees
            .Where(e => e.TenantId == tenantId && e.Status == "Active")
            .Select(e => new { e.Id, e.FullName })
            .ToListAsync(ct);

        foreach (var policy in activePolicies)
        {
            var monthlyAccrual = Math.Round(policy.AnnualEntitlementDays / 12, 4);
            foreach (var emp in employees)
            {
                var balance = await GetOrCreateBalanceAsync(tenantId, emp.Id, policy.LeaveTypeId, currentYear, ct);
                balance.EmployeeName = emp.FullName;
                balance.Accrued += monthlyAccrual;
                balance.UpdatedAtUtc = DateTime.UtcNow;

                var txn = new LeaveBalanceTransaction
                {
                    TenantId = tenantId,
                    EmployeeId = emp.Id,
                    LeaveTypeId = policy.LeaveTypeId,
                    Year = currentYear,
                    TransactionType = "Accrual",
                    Amount = monthlyAccrual,
                    BalanceBefore = balance.Accrued - monthlyAccrual,
                    BalanceAfter = balance.Accrued,
                    Reference = $"MONTHLY-ACCRUAL-{currentYear}-{DateTime.UtcNow.Month:00}",
                    Reason = "Monthly accrual",
                    PerformedByName = "System"
                };
                _db.LeaveBalanceTransactions.Add(txn);
            }
        }

        await _db.SaveChangesAsync(ct);
    }

    public async Task<decimal> CalculateWorkingDaysAsync(Guid tenantId, DateOnly start, DateOnly end, Guid? policyId, CancellationToken ct = default)
    {
        if (end < start) return 0;

        LeavePolicy? policy = null;
        if (policyId.HasValue)
        {
            policy = await _db.LeavePolicies.FirstOrDefaultAsync(p => p.Id == policyId.Value && p.TenantId == tenantId, ct);
        }

        var totalDays = end.DayNumber - start.DayNumber + 1;
        var workingDays = (decimal)totalDays;

        if (policy is null)
        {
            return workingDays;
        }

        if (!policy.WeekendsIncluded)
        {
            var weekendDays = 0;
            var current = start;
            while (current <= end)
            {
                var dow = current.DayOfWeek;
                if (dow == DayOfWeek.Saturday || dow == DayOfWeek.Sunday)
                    weekendDays++;
                current = current.AddDays(1);
            }
            workingDays -= weekendDays;
        }

        if (!policy.PublicHolidaysIncluded)
        {
            var publicHolidayCount = await _db.PublicHolidays
                .Where(h => h.TenantId == tenantId && h.Date >= start && h.Date <= end && !h.IsOptional)
                .CountAsync(ct);
            workingDays -= publicHolidayCount;
        }

        return Math.Max(0, workingDays);
    }

    public async Task<bool> HasSufficientBalanceAsync(Guid tenantId, int employeeId, Guid leaveTypeId, decimal requestedDays, int year, CancellationToken ct = default)
    {
        var balance = await _db.EmployeeLeaveBalances
            .FirstOrDefaultAsync(b => b.TenantId == tenantId && b.EmployeeId == employeeId && b.LeaveTypeId == leaveTypeId && b.Year == year, ct);

        if (balance is null) return false;
        if (balance.NegativeAllowed) return true;

        return balance.Available >= requestedDays;
    }

    public async Task<bool> HasOverlappingLeaveAsync(Guid tenantId, int employeeId, DateOnly start, DateOnly end, Guid? excludeRequestId, CancellationToken ct = default)
    {
        var query = _db.LeaveRequests
            .Where(r => r.TenantId == tenantId
                && r.EmployeeId == employeeId
                && r.Status != "Rejected"
                && r.Status != "Cancelled"
                && r.Status != "Withdrawn"
                && r.StartDate <= end
                && r.EndDate >= start);

        if (excludeRequestId.HasValue)
        {
            query = query.Where(r => r.Id != excludeRequestId.Value);
        }

        return await query.AnyAsync(ct);
    }

    public async Task ApplyLeaveBalanceAsync(Guid tenantId, int employeeId, Guid leaveTypeId, decimal days, int year, string action, string reference, string performedBy, CancellationToken ct = default)
    {
        var balance = await GetOrCreateBalanceAsync(tenantId, employeeId, leaveTypeId, year, ct);
        var balanceBefore = balance.Available;

        switch (action)
        {
            case "Pending":
                balance.Pending += days;
                break;
            case "Used":
                balance.Pending = Math.Max(0, balance.Pending - days);
                balance.Used += days;
                break;
            case "Adjustment":
                balance.ManualAdjustment += days;
                break;
            case "Allocation":
                balance.Entitled += days;
                break;
            case "Accrual":
                balance.Accrued += days;
                break;
            case "Encashed":
                balance.Encashed += days;
                break;
            case "Expired":
                balance.Expired += days;
                break;
            case "CarryForward":
                balance.CarriedForward += days;
                break;
            default:
                balance.ManualAdjustment += days;
                break;
        }

        balance.UpdatedAtUtc = DateTime.UtcNow;

        var txn = new LeaveBalanceTransaction
        {
            TenantId = tenantId,
            EmployeeId = employeeId,
            LeaveTypeId = leaveTypeId,
            Year = year,
            TransactionType = action,
            Amount = days,
            BalanceBefore = balanceBefore,
            BalanceAfter = balance.Available,
            Reference = reference,
            Reason = action,
            PerformedByName = performedBy
        };
        _db.LeaveBalanceTransactions.Add(txn);

        await _db.SaveChangesAsync(ct);
    }

    public async Task ReverseLeaveBalanceAsync(Guid tenantId, int employeeId, Guid leaveTypeId, decimal days, int year, string reference, string performedBy, CancellationToken ct = default)
    {
        var balance = await GetOrCreateBalanceAsync(tenantId, employeeId, leaveTypeId, year, ct);
        var balanceBefore = balance.Available;

        balance.Used = Math.Max(0, balance.Used - days);
        balance.UpdatedAtUtc = DateTime.UtcNow;

        var txn = new LeaveBalanceTransaction
        {
            TenantId = tenantId,
            EmployeeId = employeeId,
            LeaveTypeId = leaveTypeId,
            Year = year,
            TransactionType = "Reversed",
            Amount = days,
            BalanceBefore = balanceBefore,
            BalanceAfter = balance.Available,
            Reference = reference,
            Reason = "Balance reversal",
            PerformedByName = performedBy
        };
        _db.LeaveBalanceTransactions.Add(txn);

        await _db.SaveChangesAsync(ct);
    }

    public async Task<LeaveRequest> SubmitRequestAsync(Guid tenantId, LeaveRequest request, CancellationToken ct = default)
    {
        if (request.EndDate < request.StartDate)
            throw new InvalidOperationException("End date must be after start date.");

        var year = request.StartDate.Year;

        var hasOverlap = await HasOverlappingLeaveAsync(tenantId, request.EmployeeId, request.StartDate, request.EndDate, null, ct);
        if (hasOverlap)
            throw new InvalidOperationException("Employee already has an approved or pending leave for the requested dates.");

        var workingDays = await CalculateWorkingDaysAsync(tenantId, request.StartDate, request.EndDate, request.PolicyId, ct);
        request.TotalDays = workingDays;

        var leaveType = await _db.LeaveTypes.FirstOrDefaultAsync(t => t.Id == request.LeaveTypeId && t.TenantId == tenantId, ct);
        if (leaveType is null)
            throw new InvalidOperationException("Invalid leave type.");

        if (leaveType.RequiresAttachment && string.IsNullOrWhiteSpace(request.AttachmentPath))
            throw new InvalidOperationException("An attachment is required for this leave type.");

        if (leaveType.RequiresReason && string.IsNullOrWhiteSpace(request.Reason))
            throw new InvalidOperationException("A reason is required for this leave type.");

        if (leaveType.MaxConsecutiveDays > 0 && workingDays > leaveType.MaxConsecutiveDays)
            throw new InvalidOperationException($"This leave type allows a maximum of {leaveType.MaxConsecutiveDays} consecutive day(s). Requested: {workingDays}.");

        var sufficient = await HasSufficientBalanceAsync(tenantId, request.EmployeeId, request.LeaveTypeId, workingDays, year, ct);
        if (!sufficient)
            throw new InvalidOperationException("Insufficient leave balance.");

        request.TenantId = tenantId;
        request.LeaveTypeName = leaveType.NameEn;
        request.SubmittedAtUtc = DateTime.UtcNow;

        // Resolve approver from hierarchy policy; fall back to "Submitted" (role-based routing)
        var resolvedPolicy = await _policyService.ResolveAsync(tenantId, request.EmployeeId, "Leave", ct);
        if (resolvedPolicy is not null && resolvedPolicy.Steps.Count > 0)
        {
            var firstStep = resolvedPolicy.Steps[0];
            request.Status = "PendingManagerApproval";
            // Create the first pending LeaveApproval record so the right person's inbox populates
            _db.LeaveApprovals.Add(new LeaveApproval
            {
                TenantId = tenantId,
                LeaveRequestId = request.Id,
                StepNumber = firstStep.StepOrder,
                ApproverRole = firstStep.ApproverType,
                ApproverId = firstStep.ApproverEmployeeId.HasValue
                    ? await ResolveUserIdAsync(tenantId, firstStep.ApproverEmployeeId.Value, ct)
                    : null,
                ApproverName = firstStep.ApproverEmployeeName ?? string.Empty,
                Decision = "Pending",
            });
        }
        else
        {
            request.Status = "Submitted";
        }

        _db.LeaveRequests.Add(request);

        await ApplyLeaveBalanceAsync(tenantId, request.EmployeeId, request.LeaveTypeId, workingDays, year,
            "Pending", request.Id.ToString(), request.EmployeeName, ct);

        await LogAuditAsync(tenantId, "LeaveRequest", request.Id.ToString(), "Submitted",
            string.Empty, "Submitted", "Leave request submitted", request.EmployeeName, ct);

        return request;
    }

    public async Task<LeaveRequest> ApproveRequestAsync(Guid tenantId, Guid requestId, Guid approverId, string approverName, string? notes, CancellationToken ct = default)
    {
        var request = await _db.LeaveRequests
            .FirstOrDefaultAsync(r => r.Id == requestId && r.TenantId == tenantId, ct)
            ?? throw new InvalidOperationException("Leave request not found.");

        if (request.Status != "Submitted" && request.Status != "PendingManagerApproval" && request.Status != "PendingHRApproval")
            throw new InvalidOperationException($"Cannot approve a request with status '{request.Status}'.");

        // An employee cannot approve their own leave request.
        var requesterEmployee = await _db.Employees.AsNoTracking()
            .FirstOrDefaultAsync(e => e.TenantId == tenantId && e.Id == request.EmployeeId, ct);
        if (requesterEmployee?.UserAccountId is not null && requesterEmployee.UserAccountId == approverId)
            throw new InvalidOperationException("An employee cannot approve their own leave request.");

        var previousStatus = request.Status;
        request.Status = "Approved";
        request.DecidedAtUtc = DateTime.UtcNow;

        var approval = new LeaveApproval
        {
            TenantId = tenantId,
            LeaveRequestId = requestId,
            StepNumber = 1,
            ApproverRole = "Approver",
            ApproverId = approverId,
            ApproverName = approverName,
            Decision = "Approved",
            Notes = notes ?? string.Empty,
            ActedAtUtc = DateTime.UtcNow
        };
        _db.LeaveApprovals.Add(approval);

        await ApplyLeaveBalanceAsync(tenantId, request.EmployeeId, request.LeaveTypeId, request.TotalDays,
            request.StartDate.Year, "Used", request.Id.ToString(), approverName, ct);

        // Unpaid leave (LeaveType.IsPaid == false) must reduce salary in the pay period the
        // leave falls in. Payroll's Process() reads LeavePayrollImpact rows (ImpactType
        // containing "Deduction") and subtracts the Amount — but nothing created those rows,
        // so unpaid leave silently produced zero deduction. Produce the impact here on approval.
        var leaveType = await _db.LeaveTypes.AsNoTracking()
            .FirstOrDefaultAsync(t => t.Id == request.LeaveTypeId && t.TenantId == tenantId, ct);
        if (leaveType is not null && !leaveType.IsPaid && request.TotalDays > 0
            && !await _db.LeavePayrollImpacts.AnyAsync(x => x.TenantId == tenantId && x.LeaveRequestId == requestId, ct))
        {
            var salary = await _db.EmployeeSalaryStructures.AsNoTracking()
                .Where(s => s.TenantId == tenantId && s.EmployeeId == request.EmployeeId && s.IsActive)
                .OrderByDescending(s => s.EffectiveDate)
                .FirstOrDefaultAsync(ct);
            // basic ÷ 30 per unpaid day — standard GCC day-rate convention, matches payroll's
            // default LOP divisor. [FLAG-COMPLIANCE: confirm divisor per jurisdiction before filing.]
            var basic = salary?.BasicSalary ?? 0m;
            var amount = Math.Round(basic / 30m * request.TotalDays, 2);
            _db.LeavePayrollImpacts.Add(new LeavePayrollImpact
            {
                TenantId = tenantId,
                LeaveRequestId = requestId,
                EmployeeId = request.EmployeeId,
                PayPeriod = $"{request.StartDate.Year}-{request.StartDate.Month:00}",
                ImpactType = "Leave Deduction (Unpaid)",
                Days = request.TotalDays,
                Amount = amount,
                Status = "Pending",
            });
        }

        await LogAuditAsync(tenantId, "LeaveRequest", requestId.ToString(), "Approved",
            previousStatus, "Approved", notes ?? string.Empty, approverName, ct);

        await _db.SaveChangesAsync(ct);
        return request;
    }

    public async Task<LeaveRequest> RejectRequestAsync(Guid tenantId, Guid requestId, Guid approverId, string approverName, string reason, CancellationToken ct = default)
    {
        var request = await _db.LeaveRequests
            .FirstOrDefaultAsync(r => r.Id == requestId && r.TenantId == tenantId, ct)
            ?? throw new InvalidOperationException("Leave request not found.");

        if (request.Status != "Submitted" && request.Status != "PendingManagerApproval" && request.Status != "PendingHRApproval")
            throw new InvalidOperationException($"Cannot reject a request with status '{request.Status}'.");

        var previousStatus = request.Status;
        request.Status = "Rejected";
        request.RejectionReason = reason;
        request.DecidedAtUtc = DateTime.UtcNow;

        var approval = new LeaveApproval
        {
            TenantId = tenantId,
            LeaveRequestId = requestId,
            StepNumber = 1,
            ApproverRole = "Approver",
            ApproverId = approverId,
            ApproverName = approverName,
            Decision = "Rejected",
            Notes = reason,
            ActedAtUtc = DateTime.UtcNow
        };
        _db.LeaveApprovals.Add(approval);

        var balance = await _db.EmployeeLeaveBalances
            .FirstOrDefaultAsync(b => b.TenantId == tenantId && b.EmployeeId == request.EmployeeId
                && b.LeaveTypeId == request.LeaveTypeId && b.Year == request.StartDate.Year, ct);

        if (balance is not null)
        {
            balance.Pending = Math.Max(0, balance.Pending - request.TotalDays);
            balance.UpdatedAtUtc = DateTime.UtcNow;

            var txn = new LeaveBalanceTransaction
            {
                TenantId = tenantId,
                EmployeeId = request.EmployeeId,
                LeaveTypeId = request.LeaveTypeId,
                Year = request.StartDate.Year,
                TransactionType = "Reversed",
                Amount = request.TotalDays,
                BalanceBefore = balance.Available - request.TotalDays,
                BalanceAfter = balance.Available,
                Reference = request.Id.ToString(),
                Reason = $"Rejected: {reason}",
                PerformedByName = approverName
            };
            _db.LeaveBalanceTransactions.Add(txn);
        }

        await LogAuditAsync(tenantId, "LeaveRequest", requestId.ToString(), "Rejected",
            previousStatus, "Rejected", reason, approverName, ct);

        await _db.SaveChangesAsync(ct);
        return request;
    }

    public async Task<LeaveRequest> CancelRequestAsync(Guid tenantId, Guid requestId, string cancelledByName, string reason, CancellationToken ct = default)
    {
        var request = await _db.LeaveRequests
            .FirstOrDefaultAsync(r => r.Id == requestId && r.TenantId == tenantId, ct)
            ?? throw new InvalidOperationException("Leave request not found.");

        if (request.Status == "Cancelled" || request.Status == "Withdrawn")
            throw new InvalidOperationException("Request is already cancelled or withdrawn.");

        var wasApproved = request.Status == "Approved";
        var previousStatus = request.Status;
        request.Status = "Cancelled";
        request.CancellationReason = reason;
        request.CancelledAtUtc = DateTime.UtcNow;

        var balance = await _db.EmployeeLeaveBalances
            .FirstOrDefaultAsync(b => b.TenantId == tenantId && b.EmployeeId == request.EmployeeId
                && b.LeaveTypeId == request.LeaveTypeId && b.Year == request.StartDate.Year, ct);

        if (balance is not null)
        {
            if (wasApproved)
            {
                balance.Used = Math.Max(0, balance.Used - request.TotalDays);
            }
            else
            {
                balance.Pending = Math.Max(0, balance.Pending - request.TotalDays);
            }
            balance.UpdatedAtUtc = DateTime.UtcNow;

            var txn = new LeaveBalanceTransaction
            {
                TenantId = tenantId,
                EmployeeId = request.EmployeeId,
                LeaveTypeId = request.LeaveTypeId,
                Year = request.StartDate.Year,
                TransactionType = "Reversed",
                Amount = request.TotalDays,
                BalanceBefore = wasApproved ? balance.Available - request.TotalDays : balance.Available - request.TotalDays,
                BalanceAfter = balance.Available,
                Reference = request.Id.ToString(),
                Reason = $"Cancelled: {reason}",
                PerformedByName = cancelledByName
            };
            _db.LeaveBalanceTransactions.Add(txn);
        }

        // Remove any unpaid-leave payroll deduction that has not yet been picked up by a run,
        // so cancelling an approved unpaid leave does not still dock the employee's salary.
        // Already-processed impacts (Status == "Processed") are left for audit integrity.
        var pendingImpacts = await _db.LeavePayrollImpacts
            .Where(x => x.TenantId == tenantId && x.LeaveRequestId == requestId && x.Status != "Processed")
            .ToListAsync(ct);
        if (pendingImpacts.Count > 0)
            _db.LeavePayrollImpacts.RemoveRange(pendingImpacts);

        await LogAuditAsync(tenantId, "LeaveRequest", requestId.ToString(), "Cancelled",
            previousStatus, "Cancelled", reason, cancelledByName, ct);

        await _db.SaveChangesAsync(ct);
        return request;
    }

    public async Task LogAuditAsync(Guid tenantId, string entityType, string entityId, string action, string oldValue, string newValue, string reason, string performedByName, CancellationToken ct = default)
    {
        var log = new LeaveAuditLog
        {
            TenantId = tenantId,
            EntityType = entityType,
            EntityId = entityId,
            Action = action,
            OldValue = oldValue,
            NewValue = newValue,
            Reason = reason,
            PerformedByName = performedByName
        };
        _db.LeaveAuditLogs.Add(log);
        await _db.SaveChangesAsync(ct);
    }

    public async Task GenerateInsightsAsync(Guid tenantId, CancellationToken ct = default)
    {
        var cutoff = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-90));
        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        var sickLeaveTypes = await _db.LeaveTypes
            .Where(t => t.TenantId == tenantId && t.IsActive && (t.Category == "Sick" || t.NameEn.Contains("Sick")))
            .Select(t => t.Id)
            .ToListAsync(ct);

        if (!sickLeaveTypes.Any()) return;

        var recentSickLeave = await _db.LeaveRequests
            .Where(r => r.TenantId == tenantId
                && sickLeaveTypes.Contains(r.LeaveTypeId)
                && r.Status == "Approved"
                && r.StartDate >= cutoff
                && r.StartDate <= today)
            .ToListAsync(ct);

        var suspiciousEmployees = recentSickLeave
            .GroupBy(r => r.EmployeeId)
            .Where(g =>
            {
                var mondayFridayCount = g.Count(r =>
                    r.StartDate.DayOfWeek == DayOfWeek.Monday ||
                    r.StartDate.DayOfWeek == DayOfWeek.Friday);
                return mondayFridayCount > 3;
            })
            .ToList();

        foreach (var group in suspiciousEmployees)
        {
            var existing = await _db.LeaveAIInsights
                .AnyAsync(i => i.TenantId == tenantId
                    && i.InsightType == "AbsencePattern"
                    && i.AffectedEmployeeId == group.Key
                    && !i.IsAcknowledged, ct);

            if (!existing)
            {
                var firstRecord = group.First();
                var insight = new LeaveAIInsight
                {
                    TenantId = tenantId,
                    InsightType = "AbsencePattern",
                    Severity = "Warning",
                    Title = $"Monday/Friday absence pattern detected",
                    Summary = $"Employee {firstRecord.EmployeeName} (ID: {group.Key}) has taken sick leave on Monday or Friday more than 3 times in the last 90 days.",
                    AffectedEmployeeId = group.Key,
                    AffectedDepartment = firstRecord.DepartmentName,
                    Data = System.Text.Json.JsonSerializer.Serialize(new
                    {
                        EmployeeId = group.Key,
                        EmployeeName = firstRecord.EmployeeName,
                        MondayFridayCount = group.Count(r =>
                            r.StartDate.DayOfWeek == DayOfWeek.Monday ||
                            r.StartDate.DayOfWeek == DayOfWeek.Friday),
                        TotalSickLeaveDays = group.Sum(r => r.TotalDays),
                        Period = "Last 90 days"
                    }),
                    IsAcknowledged = false
                };
                _db.LeaveAIInsights.Add(insight);
            }
        }

        await _db.SaveChangesAsync(ct);
    }

    // Resolves the UserAccountId for an employee (used to route the LeaveApproval record to the right user inbox)
    private async Task<Guid?> ResolveUserIdAsync(Guid tenantId, int employeeId, CancellationToken ct)
        => await _db.Employees
            .AsNoTracking()
            .Where(e => e.TenantId == tenantId && e.Id == employeeId)
            .Select(e => e.UserAccountId)
            .FirstOrDefaultAsync(ct);
}
