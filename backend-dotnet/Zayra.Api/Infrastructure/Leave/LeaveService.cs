using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Approvals;
using Zayra.Api.Application.Leave;
using Zayra.Api.Application.WorkWeek;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.WorkWeek;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Leave;

public class LeaveService : ILeaveService
{
    private readonly ZayraDbContext _db;
    private readonly IApprovalPolicyService _policyService;
    private readonly IWorkWeekService _workWeek;

    public LeaveService(ZayraDbContext db, IApprovalPolicyService policyService, IWorkWeekService? workWeek = null)
    {
        _db = db;
        _policyService = policyService;
        // Optional so existing callers/tests keep working; DI always supplies the real one.
        _workWeek = workWeek ?? new WorkWeekService(db);
    }

    /// <summary>
    /// POD-C3 (MF-6c) — resolves <c>lop.monthly_day_divisor</c> for the employee's legal entity, the SAME
    /// key and the SAME precedence PayrollController.Process reads. Before this, unpaid leave was
    /// snapshotted at a hard-coded <c>basic / 30</c> at approval time while payroll charged unpaid
    /// ABSENCE at the configured divisor — two day-rates for the same economic fact on one payslip.
    /// Falls back to 30 (the shipped default), so a tenant that never configured a divisor is unaffected.
    /// </summary>
    private async Task<decimal> ResolveLopDayDivisorAsync(Guid tenantId, int employeeId, DateOnly onDate, CancellationToken ct)
    {
        const decimal fallback = 30m;
        // IgnoreQueryFilters is intentional: resolving the employee's legal entity is a SYSTEM/config
        // read that must succeed regardless of the approver's own company claims; the explicit TenantId
        // predicate on BOTH sides re-applies exact tenant scope and never reads another tenant.
        var cc = await _db.Employees.AsNoTracking()
            .Where(e => e.TenantId == tenantId && e.Id == employeeId)
            .Join(_db.Companies.IgnoreQueryFilters().AsNoTracking().Where(c => c.TenantId == tenantId),
                  e => e.CompanyId, c => (Guid?)c.Id, (e, c) => c.CountryCode)
            .FirstOrDefaultAsync(ct);
        if (string.IsNullOrWhiteSpace(cc)) return fallback;
        var eff = onDate.ToDateTime(TimeOnly.MinValue);
        // Tenant override wins over the seeded platform default (TenantId == null) — the same rule
        // StatutoryRuleReader applies for the payroll run.
        // IgnoreQueryFilters is intentional: StatutoryRule platform defaults are stored with TenantId ==
        // null, which the per-tenant global filter excludes; this reads the tenant's own rows PLUS those
        // platform defaults and nothing else.
        var raw = await _db.StatutoryRules.IgnoreQueryFilters().AsNoTracking()
            .Where(r => (r.TenantId == tenantId || r.TenantId == null)
                     && r.CountryCode == cc && r.RuleKey == "lop.monthly_day_divisor"
                     && r.EffectiveFrom <= eff && (r.EffectiveTo == null || r.EffectiveTo >= eff))
            .OrderByDescending(r => r.TenantId != null).ThenByDescending(r => r.EffectiveFrom)
            .Select(r => r.RuleValue)
            .FirstOrDefaultAsync(ct);
        return decimal.TryParse(raw, System.Globalization.NumberStyles.Any,
                                System.Globalization.CultureInfo.InvariantCulture, out var v) && v > 0m
            ? v : fallback;
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
        var accrualMonth = DateTime.UtcNow;
        if (!_db.Database.IsRelational())
        {
            await AccrueMonthlyCoreAsync(tenantId, accrualMonth, ct);
            return;
        }

        // The scheduler can overlap during deploys or manual replay. Serialize one tenant/month
        // on PostgreSQL, inside the execution strategy required by EnableRetryOnFailure. The
        // filtered unique ledger index remains the permanent invariant if a non-cooperating writer
        // bypasses this service.
        var strategy = _db.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            var isPostgres = (_db.Database.ProviderName ?? string.Empty)
                .Contains("Npgsql", StringComparison.OrdinalIgnoreCase);
            // PostgreSQL's advisory lock is the serializer. ReadCommitted is intentional: a second
            // waiter must take its SELECT snapshots after the first writer commits. Beginning a
            // Serializable snapshot before waiting produces a legitimate 40001 on the later write.
            await using var transaction = await _db.Database.BeginTransactionAsync(
                isPostgres
                    ? System.Data.IsolationLevel.ReadCommitted
                    : System.Data.IsolationLevel.Serializable,
                ct);
            if (isPostgres)
            {
                var lockIdentity = $"leave-accrual:{tenantId:N}:{accrualMonth:yyyy-MM}";
                await _db.Database.ExecuteSqlInterpolatedAsync(
                    $"SELECT pg_advisory_xact_lock(hashtextextended({lockIdentity}, 0))", ct);
            }

            await AccrueMonthlyCoreAsync(tenantId, accrualMonth, ct);
            await transaction.CommitAsync(ct);
        });
    }

    private async Task AccrueMonthlyCoreAsync(Guid tenantId, DateTime accrualMonth, CancellationToken ct)
    {
        var activePolicies = await _db.LeavePolicies
            .Where(p => p.TenantId == tenantId && p.Status == "Active" && p.AccrualMethod == "Monthly")
            .ToListAsync(ct);
        var eligibilityRows = await _db.LeavePolicyEligibilities
            .Where(e => e.TenantId == tenantId && e.IsActive)
            .ToListAsync(ct);

        var currentYear = accrualMonth.Year;
        var employees = await _db.Employees
            .Where(e => e.TenantId == tenantId && e.Status == "Active")
            .Select(e => new { e.Id, e.FullName, e.CompanyId, e.BranchId, e.DepartmentId, e.GradeId, e.Department, e.Grade, e.EmploymentType, e.ContractType, e.Gender })
            .ToListAsync(ct);
        var companyCountries = await _db.Companies
            .AsNoTracking()
            .Where(c => c.TenantId == tenantId)
            .Select(c => new { c.Id, c.CountryCode })
            .ToDictionaryAsync(c => c.Id, c => c.CountryCode, ct);

        foreach (var policy in activePolicies)
        {
            var monthlyAccrual = Math.Round(policy.AnnualEntitlementDays / 12, 4);
            var accrualReference = $"MONTHLY-ACCRUAL-{currentYear}-{accrualMonth.Month:00}";
            foreach (var emp in employees)
            {
                var employeeCountryCode = emp.CompanyId.HasValue && companyCountries.TryGetValue(emp.CompanyId.Value, out var companyCountry)
                    ? companyCountry
                    : string.Empty;
                if (!IsPolicyEligible(policy, eligibilityRows, employeeCountryCode, emp.CompanyId, emp.BranchId, emp.DepartmentId, emp.GradeId, emp.Department, emp.Grade, emp.EmploymentType, emp.ContractType, emp.Gender))
                    continue;
                // Several active policies can overlap (for example a tenant default and a
                // company-specific override). Accrue only the same most-specific policy that
                // request submission would resolve, otherwise the employee is credited twice.
                var resolvedPolicy = activePolicies
                    .Where(p => p.LeaveTypeId == policy.LeaveTypeId
                        && IsPolicyEligible(p, eligibilityRows, employeeCountryCode, emp.CompanyId, emp.BranchId,
                            emp.DepartmentId, emp.GradeId, emp.Department, emp.Grade, emp.EmploymentType,
                            emp.ContractType, emp.Gender))
                    .OrderByDescending(p => PolicySpecificity(p, eligibilityRows, emp.DepartmentId, emp.GradeId))
                    .ThenByDescending(p => p.UpdatedAtUtc)
                    .FirstOrDefault();
                if (resolvedPolicy?.Id != policy.Id) continue;
                if (await _db.LeaveBalanceTransactions.AsNoTracking().AnyAsync(t =>
                        t.TenantId == tenantId && t.EmployeeId == emp.Id && t.LeaveTypeId == policy.LeaveTypeId
                        && t.Year == currentYear && t.TransactionType == "Accrual" && t.Reference == accrualReference, ct))
                    continue;

                var balance = await GetOrCreateBalanceAsync(tenantId, emp.Id, policy.LeaveTypeId, currentYear, ct);
                balance.EmployeeName = emp.FullName;
                balance.Accrued += monthlyAccrual;
                balance.UpdatedAtUtc = DateTime.UtcNow;

                var txn = new LeaveBalanceTransaction
                {
                    TenantId = tenantId,
                    CompanyId = emp.CompanyId,
                    EmployeeId = emp.Id,
                    LeaveTypeId = policy.LeaveTypeId,
                    Year = currentYear,
                    TransactionType = "Accrual",
                    Amount = monthlyAccrual,
                    BalanceBefore = balance.Accrued - monthlyAccrual,
                    BalanceAfter = balance.Accrued,
                    Reference = accrualReference,
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
            // Weekend (rest) days come from configuration via WorkWeekService — company override
            // → tenant default → country pack → GCC default. Hard-coding Sat/Sun here was the
            // legally-wrong leave deduction for GCC tenants (over-deducts Fri, under-deducts Sun).
            var workWeek = await _workWeek.ResolveAsync(tenantId, policy.CompanyId, policy.CountryCode, ct);
            workingDays -= workWeek.CountWeekendDays(start, end);
        }

        if (!policy.PublicHolidaysIncluded)
        {
            var calendars = _db.PublicHolidayCalendars
                .Where(c => c.TenantId == tenantId && c.IsActive && c.CalendarYear == start.Year);
            if (end.Year != start.Year)
            {
                calendars = _db.PublicHolidayCalendars
                    .Where(c => c.TenantId == tenantId && c.IsActive && c.CalendarYear >= start.Year && c.CalendarYear <= end.Year);
            }
            if (!string.IsNullOrWhiteSpace(policy.CountryCode))
                calendars = calendars.Where(c => c.CountryCode == policy.CountryCode);
            if (policy.CompanyId.HasValue)
                calendars = calendars.Where(c => c.CompanyId == policy.CompanyId || c.CompanyId == null);
            if (policy.BranchId.HasValue)
                calendars = calendars.Where(c => c.BranchId == policy.BranchId || c.BranchId == null);

            var publicHolidayCount = await _db.PublicHolidays
                .Where(h => h.TenantId == tenantId && h.Date >= start && h.Date <= end && !h.IsOptional)
                .Join(calendars, h => h.CalendarId, c => c.Id, (h, _) => h.Id)
                .Distinct()
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
            CompanyId = await _db.Employees.AsNoTracking()
                .Where(e => e.TenantId == tenantId && e.Id == employeeId && !e.IsDeleted)
                .Select(e => e.CompanyId)
                .FirstOrDefaultAsync(ct),
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
            CompanyId = await _db.Employees.AsNoTracking()
                .Where(e => e.TenantId == tenantId && e.Id == employeeId && !e.IsDeleted)
                .Select(e => e.CompanyId)
                .FirstOrDefaultAsync(ct),
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

    public Task<LeaveRequest> SubmitRequestAsync(Guid tenantId, LeaveRequest request, CancellationToken ct = default)
        => SubmitRequestAsync(tenantId, request, null, ct);

    public async Task<LeaveRequest> SubmitRequestAsync(Guid tenantId, LeaveRequest request, Guid? requestedByUserId, CancellationToken ct = default)
    {
        if (!_db.Database.IsRelational() || _db.Database.CurrentTransaction is not null)
            return await SubmitRequestCoreAsync(tenantId, request, requestedByUserId, ct);

        // Submission spans several SaveChanges calls (balance reservation, routing projection and
        // audit). Keep the entire aggregate change atomic and put the transaction inside the EF
        // execution strategy: Npgsql's EnableRetryOnFailure explicitly rejects a bare transaction.
        // A retry reuses this scoped DbContext, so detach state left by the rolled-back attempt
        // before rebuilding the aggregate from the database. The request has a client-generated
        // Guid, which also gives verifySucceeded a durable commit marker for commit-ack failures.
        var strategy = _db.Database.CreateExecutionStrategy();
        var firstAttempt = true;
        return await strategy.ExecuteInTransactionAsync(
            async retryCt =>
            {
                if (!firstAttempt)
                    _db.ChangeTracker.Clear();
                firstAttempt = false;
                return await SubmitRequestCoreAsync(tenantId, request, requestedByUserId, retryCt);
            },
            retryCt => IsSubmissionPersistedAsync(tenantId, request.Id, retryCt),
            ct);
    }

    private async Task<LeaveRequest> SubmitRequestCoreAsync(Guid tenantId, LeaveRequest request, Guid? requestedByUserId, CancellationToken ct)
    {
        if (request.EndDate < request.StartDate)
            throw new InvalidOperationException("End date must be after start date.");

        var hasOverlap = await HasOverlappingLeaveAsync(tenantId, request.EmployeeId, request.StartDate, request.EndDate, null, ct);
        if (hasOverlap)
            throw new InvalidOperationException("Employee already has an approved or pending leave for the requested dates.");

        var workingDays = await CalculateWorkingDaysAsync(tenantId, request.StartDate, request.EndDate, request.PolicyId, ct);
        request.TotalDays = workingDays;

        var leaveType = await _db.LeaveTypes.FirstOrDefaultAsync(t => t.Id == request.LeaveTypeId && t.TenantId == tenantId, ct);
        if (leaveType is null)
            throw new InvalidOperationException("Invalid leave type.");

        var employee = await _db.Employees.AsNoTracking()
            .FirstOrDefaultAsync(e => e.TenantId == tenantId && e.Id == request.EmployeeId && !e.IsDeleted, ct)
            ?? throw new InvalidOperationException("Employee not found.");

        var effectivePolicy = await ResolveLeavePolicyAsync(tenantId, employee, request.LeaveTypeId, request.PolicyId, ct);
        request.PolicyId = effectivePolicy?.Id;
        request.CompanyId = employee.CompanyId;
        request.EmployeeName = string.IsNullOrWhiteSpace(request.EmployeeName) ? employee.FullName : request.EmployeeName;
        request.DepartmentName = string.IsNullOrWhiteSpace(request.DepartmentName) ? employee.Department : request.DepartmentName;
        request.DesignationTitle = string.IsNullOrWhiteSpace(request.DesignationTitle) ? employee.Designation : request.DesignationTitle;
        if (effectivePolicy is not null)
        {
            request.PayrollImpact = effectivePolicy.PayrollImpact;
            workingDays = await CalculateWorkingDaysAsync(tenantId, request.StartDate, request.EndDate, effectivePolicy.Id, ct);
        }

        if (request.DayType.StartsWith("Half", StringComparison.OrdinalIgnoreCase))
        {
            if (request.StartDate != request.EndDate)
                throw new InvalidOperationException("Half-day leave must be for a single date.");
            workingDays = 0.5m;
        }
        else if (request.DayType.Equals("Hourly", StringComparison.OrdinalIgnoreCase))
        {
            workingDays = Math.Round(request.HoursRequested / 8m, 4);
        }
        request.TotalDays = workingDays;

        if (leaveType.RequiresAttachment && string.IsNullOrWhiteSpace(request.AttachmentPath))
            throw new InvalidOperationException("An attachment is required for this leave type.");

        if (leaveType.RequiresReason && string.IsNullOrWhiteSpace(request.Reason))
            throw new InvalidOperationException("A reason is required for this leave type.");

        if (leaveType.MaxConsecutiveDays > 0 && workingDays > leaveType.MaxConsecutiveDays)
            throw new InvalidOperationException($"This leave type allows a maximum of {leaveType.MaxConsecutiveDays} consecutive day(s). Requested: {workingDays}.");

        if (request.DayType.StartsWith("Half", StringComparison.OrdinalIgnoreCase) && !leaveType.IsHalfDayAllowed)
            throw new InvalidOperationException("Half-day leave is not allowed for this leave type.");
        if (request.DayType.Equals("Hourly", StringComparison.OrdinalIgnoreCase))
        {
            if (!leaveType.IsHourlyAllowed) throw new InvalidOperationException("Hourly leave is not allowed for this leave type.");
            if (request.StartDate != request.EndDate || request.HoursRequested <= 0 || request.HoursRequested > 8)
                throw new InvalidOperationException("Hourly leave must be for one day and between 0 and 8 hours.");
        }
        if (effectivePolicy is not null)
        {
            if (workingDays < effectivePolicy.MinimumDaysPerRequest)
                throw new InvalidOperationException($"This policy requires at least {effectivePolicy.MinimumDaysPerRequest} day(s) per request.");
            if (effectivePolicy.MaximumDaysPerRequest > 0 && workingDays > effectivePolicy.MaximumDaysPerRequest)
                throw new InvalidOperationException($"This policy allows at most {effectivePolicy.MaximumDaysPerRequest} day(s) per request.");
            var noticeDays = request.StartDate.DayNumber - DateOnly.FromDateTime(DateTime.UtcNow).DayNumber;
            if (!request.IsEmergency && effectivePolicy.NoticeRequiredDays > noticeDays)
                throw new InvalidOperationException($"This policy requires {effectivePolicy.NoticeRequiredDays} day(s) advance notice.");
            if (!effectivePolicy.AppliesOnProbation && employee.ProbationEndDate.HasValue
                && request.StartDate <= employee.ProbationEndDate.Value)
                throw new InvalidOperationException("This leave policy does not apply during probation.");
        }

        var yearSegments = await CalculateRequestYearSegmentsAsync(tenantId, request, effectivePolicy?.Id, ct);
        foreach (var segment in yearSegments)
            if (!await HasSufficientBalanceAsync(tenantId, request.EmployeeId, request.LeaveTypeId, segment.Days, segment.Year, ct))
                throw new InvalidOperationException($"Insufficient leave balance for {segment.Year}.");

        request.TenantId = tenantId;
        request.LeaveTypeName = leaveType.NameEn;
        request.SubmittedAtUtc = DateTime.UtcNow;

        // Resolve approver from hierarchy policy. A missing tenant policy must still produce an
        // actionable queue item: leaving the request in bare "Submitted" created no LeaveApproval,
        // while the UI promised manager approval and both operational queues showed zero work.
        // Fall back to the employee's direct manager; if no usable manager account exists, route
        // visibly to the HR Manager role instead of silently orphaning the request.
        var resolvedPolicy = await _policyService.ResolveAsync(tenantId, request.EmployeeId, "Leave", ct);
        LeaveApproval firstApproval;
        int? firstApproverEmployeeId;
        if (resolvedPolicy is not null && resolvedPolicy.Steps.Count > 0)
        {
            var firstStep = resolvedPolicy.Steps[0];
            request.Status = "PendingManagerApproval";
            firstApproverEmployeeId = firstStep.ApproverEmployeeId;
            firstApproval = new LeaveApproval
            {
                TenantId = tenantId,
                LeaveRequestId = request.Id,
                StepNumber = firstStep.StepOrder,
                ApproverRole = ResolvedApproverRole(firstStep),
                ApproverId = firstStep.ApproverEmployeeId.HasValue
                    ? await ResolveUserIdAsync(tenantId, firstStep.ApproverEmployeeId.Value, ct)
                    : null,
                ApproverName = firstStep.ApproverEmployeeName ?? string.Empty,
                Decision = "Pending",
            };
        }
        else
        {
            var fallbackManager = employee.ManagerEmployeeId.HasValue
                ? await _db.Employees.AsNoTracking()
                    .Where(e => e.TenantId == tenantId && e.Id == employee.ManagerEmployeeId.Value && !e.IsDeleted)
                    .Select(e => new { e.UserAccountId, e.FullName })
                    .FirstOrDefaultAsync(ct)
                : null;

            request.Status = "PendingManagerApproval";
            firstApproverEmployeeId = employee.ManagerEmployeeId;
            firstApproval = new LeaveApproval
            {
                TenantId = tenantId,
                LeaveRequestId = request.Id,
                StepNumber = 1,
                ApproverRole = fallbackManager?.UserAccountId is not null ? "Manager" : "HR Manager",
                ApproverId = fallbackManager?.UserAccountId,
                ApproverName = fallbackManager?.FullName ?? string.Empty,
                Decision = "Pending",
            };
        }

        _db.LeaveApprovals.Add(firstApproval);
        _db.LeaveRequests.Add(request);
        if (request.DelegateEmployeeId.HasValue)
        {
            _db.LeaveDelegations.Add(new LeaveDelegation
            {
                TenantId = tenantId,
                EmployeeId = request.EmployeeId,
                EmployeeName = request.EmployeeName,
                DelegateEmployeeId = request.DelegateEmployeeId.Value,
                DelegateEmployeeName = request.DelegateEmployeeName,
                LeaveRequestId = request.Id,
                StartDate = request.StartDate,
                EndDate = request.EndDate,
                DelegationType = "ApprovalOnly",
                Status = "Active"
            });
        }
        _db.ApprovalRequests.Add(BuildApprovalProjection(
            request,
            firstApproval,
            firstApproverEmployeeId,
            resolvedPolicy?.PolicyId ?? Guid.Empty,
            requestedByUserId ?? employee.UserAccountId));

        foreach (var segment in yearSegments)
            await ApplyLeaveBalanceAsync(tenantId, request.EmployeeId, request.LeaveTypeId, segment.Days, segment.Year,
                "Pending", request.Id.ToString(), request.EmployeeName, ct);

        await LogAuditAsync(tenantId, "LeaveRequest", request.Id.ToString(), "Submitted",
            string.Empty, "Submitted", "Leave request submitted", request.EmployeeName, ct);

        return request;
    }

    public async Task<LeaveRequest> ApproveRequestAsync(Guid tenantId, Guid requestId, Guid approverId, string approverName, string? notes, CancellationToken ct = default)
    {
        if (!_db.Database.IsRelational() || _db.Database.CurrentTransaction is not null)
            return await ApproveRequestCoreAsync(tenantId, requestId, approverId, approverName, notes, ct);

        return await ExecuteDecisionTransactionAsync(
            tenantId, requestId, approverId, "Approved",
            retryCt => ApproveRequestCoreAsync(tenantId, requestId, approverId, approverName, notes, retryCt), ct);
    }

    private async Task<LeaveRequest> ApproveRequestCoreAsync(Guid tenantId, Guid requestId, Guid approverId, string approverName, string? notes, CancellationToken ct)
    {
        var request = await _db.LeaveRequests
            .FirstOrDefaultAsync(r => r.Id == requestId && r.TenantId == tenantId, ct)
            ?? throw new InvalidOperationException("Leave request not found.");

        if (request.Status != "Submitted" && request.Status != "PendingManagerApproval" && request.Status != "PendingHRApproval")
            throw new InvalidOperationException($"Cannot approve a request with status '{request.Status}'.");

        await EnsureMakerCheckerAsync(request, approverId, ct);

        var previousStatus = request.Status;
        var resolvedPolicy = await _policyService.ResolveAsync(tenantId, request.EmployeeId, "Leave", ct);
        var pendingApproval = await _db.LeaveApprovals
            .Where(a => a.TenantId == tenantId && a.LeaveRequestId == requestId && a.Decision == "Pending")
            .OrderBy(a => a.StepNumber)
            .FirstOrDefaultAsync(ct);

        var currentApproval = await EnsureCanonicalApprovalAsync(request, pendingApproval, ct);
        await ConsumePendingApprovalAsync(currentApproval, "Approved", approverId, approverName, notes ?? string.Empty, ct);

        var currentStepNumber = currentApproval.StepNumber;
        var nextStep = resolvedPolicy?.Steps
            .Where(s => s.StepOrder > currentStepNumber)
            .OrderBy(s => s.StepOrder)
            .FirstOrDefault();

        if (nextStep is not null)
        {
            request.Status = StatusForPendingStep(nextStep);
            var nextApproval = new LeaveApproval
            {
                TenantId = tenantId,
                LeaveRequestId = requestId,
                StepNumber = nextStep.StepOrder,
                ApproverRole = ResolvedApproverRole(nextStep),
                ApproverId = nextStep.ApproverEmployeeId.HasValue
                    ? await ResolveUserIdAsync(tenantId, nextStep.ApproverEmployeeId.Value, ct)
                    : null,
                ApproverName = nextStep.ApproverEmployeeName ?? string.Empty,
                Decision = "Pending",
            };
            _db.LeaveApprovals.Add(nextApproval);
            await SyncApprovalProjectionAsync(request, currentApproval, "Approved", approverId,
                notes ?? string.Empty, nextApproval, nextStep.ApproverEmployeeId, ct);

            await LogAuditAsync(tenantId, "LeaveRequest", requestId.ToString(), "ApprovalStepApproved",
                previousStatus, request.Status, notes ?? string.Empty, approverName, ct);
            await _db.SaveChangesAsync(ct);
            return request;
        }

        request.Status = "Approved";
        request.DecidedAtUtc = DateTime.UtcNow;

        await SyncApprovalProjectionAsync(request, currentApproval, "Approved", approverId,
            notes ?? string.Empty, null, null, ct);

        foreach (var segment in await CalculateRequestYearSegmentsAsync(tenantId, request, request.PolicyId, ct))
            await ApplyLeaveBalanceAsync(tenantId, request.EmployeeId, request.LeaveTypeId, segment.Days,
                segment.Year, "Used", request.Id.ToString(), approverName, ct);

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
                .Where(s => s.TenantId == tenantId && s.EmployeeId == request.EmployeeId && s.IsActive && s.EffectiveDate <= request.StartDate)
                .OrderByDescending(s => s.EffectiveDate)
                .FirstOrDefaultAsync(ct);
            // POD-C3 (MF-6c) — the divisor is READ, not hard-coded. This literal 30 was a THIRD day-rate
            // in the product (alongside lop.monthly_day_divisor and the proration basis): a tenant on a
            // 26-day divisor charged unpaid leave at basic/30 here and unpaid absence at basic/26 in
            // payroll, on the same payslip. With proration in play the mismatch can drive net negative.
            // Resolution order mirrors the payroll run exactly: the tenant/platform StatutoryRule for the
            // employee's company country, falling back to 30 — so nothing changes for a tenant that never
            // configured one. [FLAG-COMPLIANCE: confirm divisor per jurisdiction before filing.]
            var basic = salary?.BasicSalary ?? 0m;
            var lopDivisor = await ResolveLopDayDivisorAsync(tenantId, request.EmployeeId, request.StartDate, ct);
            var amount = Math.Round(basic / lopDivisor * request.TotalDays, 2);
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

        // Notify the employee in their self-service feed (EmployeeNotification was a dead
        // table — read by ESS but never written, so employees never saw any updates).
        _db.EmployeeNotifications.Add(new EmployeeNotification
        {
            TenantId = tenantId, EmployeeId = request.EmployeeId, NotificationType = "Success",
            Title = "Leave approved",
            Body = $"Your {request.LeaveTypeName} request ({request.StartDate:dd MMM} – {request.EndDate:dd MMM}) was approved by {approverName}.",
        });

        await _db.SaveChangesAsync(ct);
        return request;
    }

    public async Task<LeaveRequest> RejectRequestAsync(Guid tenantId, Guid requestId, Guid approverId, string approverName, string reason, CancellationToken ct = default)
    {
        if (!_db.Database.IsRelational() || _db.Database.CurrentTransaction is not null)
            return await RejectRequestCoreAsync(tenantId, requestId, approverId, approverName, reason, ct);

        return await ExecuteDecisionTransactionAsync(
            tenantId, requestId, approverId, "Rejected",
            retryCt => RejectRequestCoreAsync(tenantId, requestId, approverId, approverName, reason, retryCt), ct);
    }

    private async Task<LeaveRequest> ExecuteDecisionTransactionAsync(
        Guid tenantId,
        Guid requestId,
        Guid approverId,
        string decision,
        Func<CancellationToken, Task<LeaveRequest>> operation,
        CancellationToken ct)
    {
        // The CAS update, routing projection, balance mutation, payroll impact, audit and employee
        // notification are one unit. ExecuteInTransactionAsync both satisfies Npgsql's retrying
        // strategy contract and verifies an ambiguous commit using the canonical decision rows.
        // ChangeTracker.Clear is only used between attempts; without it a rolled-back attempt can
        // leave LeaveRequest.Status == Approved and Added audit/balance rows in memory, making the
        // retry fail early or duplicate side effects.
        var strategy = _db.Database.CreateExecutionStrategy();
        var firstAttempt = true;
        int? attemptedStep = null;
        return await strategy.ExecuteInTransactionAsync(
            async retryCt =>
            {
                if (!firstAttempt)
                    _db.ChangeTracker.Clear();
                firstAttempt = false;

                attemptedStep = await _db.LeaveApprovals.AsNoTracking()
                    .Where(a => a.TenantId == tenantId && a.LeaveRequestId == requestId && a.Decision == "Pending")
                    .OrderBy(a => a.StepNumber)
                    .Select(a => (int?)a.StepNumber)
                    .FirstOrDefaultAsync(retryCt);

                return await operation(retryCt);
            },
            retryCt => IsDecisionPersistedAsync(
                tenantId, requestId, attemptedStep, approverId, decision, retryCt),
            ct);
    }

    private async Task<bool> IsSubmissionPersistedAsync(Guid tenantId, Guid requestId, CancellationToken ct)
    {
        // All four records are part of the same transaction. Checking each guards against ever
        // treating a historically partial (pre-fix) submission as a successful retry outcome.
        return await _db.LeaveRequests.AsNoTracking()
                   .AnyAsync(r => r.TenantId == tenantId && r.Id == requestId, ct)
            && await _db.LeaveApprovals.AsNoTracking()
                   .AnyAsync(a => a.TenantId == tenantId && a.LeaveRequestId == requestId, ct)
            && await _db.ApprovalRequests.AsNoTracking()
                   .AnyAsync(a => a.TenantId == tenantId && a.Id == requestId, ct)
            && await _db.LeaveBalanceTransactions.AsNoTracking()
                   .AnyAsync(t => t.TenantId == tenantId && t.Reference == requestId.ToString()
                              && t.TransactionType == "Pending", ct);
    }

    private async Task<bool> IsDecisionPersistedAsync(
        Guid tenantId,
        Guid requestId,
        int? attemptedStep,
        Guid approverId,
        string decision,
        CancellationToken ct)
    {
        if (!attemptedStep.HasValue)
            return false;

        return await _db.LeaveApprovals.AsNoTracking()
                   .AnyAsync(a => a.TenantId == tenantId && a.LeaveRequestId == requestId
                              && a.StepNumber == attemptedStep.Value && a.Decision == decision
                              && a.ApproverId == approverId, ct)
            && await _db.ApprovalDecisions.AsNoTracking()
                   .AnyAsync(d => d.TenantId == tenantId && d.ApprovalRequestId == requestId
                              && d.StepOrder == attemptedStep.Value && d.Decision == decision
                              && d.DecidedByUserId == approverId, ct);
    }

    private async Task<LeaveRequest> RejectRequestCoreAsync(Guid tenantId, Guid requestId, Guid approverId, string approverName, string reason, CancellationToken ct)
    {
        var request = await _db.LeaveRequests
            .FirstOrDefaultAsync(r => r.Id == requestId && r.TenantId == tenantId, ct)
            ?? throw new InvalidOperationException("Leave request not found.");

        if (request.Status != "Submitted" && request.Status != "PendingManagerApproval" && request.Status != "PendingHRApproval")
            throw new InvalidOperationException($"Cannot reject a request with status '{request.Status}'.");

        await EnsureMakerCheckerAsync(request, approverId, ct);

        var previousStatus = request.Status;
        request.Status = "Rejected";
        request.RejectionReason = reason;
        request.DecidedAtUtc = DateTime.UtcNow;

        var pendingApproval = await _db.LeaveApprovals
            .Where(a => a.TenantId == tenantId && a.LeaveRequestId == requestId && a.Decision == "Pending")
            .OrderBy(a => a.StepNumber)
            .FirstOrDefaultAsync(ct);
        pendingApproval = await EnsureCanonicalApprovalAsync(request, pendingApproval, ct);
        await ConsumePendingApprovalAsync(pendingApproval, "Rejected", approverId, approverName, reason, ct);
        await SyncApprovalProjectionAsync(request, pendingApproval, "Rejected", approverId, reason, null, null, ct);

        await ReleaseRequestBalancesAsync(tenantId, request, releaseUsed: false,
            $"Rejected: {reason}", approverName, ct);

        await LogAuditAsync(tenantId, "LeaveRequest", requestId.ToString(), "Rejected",
            previousStatus, "Rejected", reason, approverName, ct);

        _db.EmployeeNotifications.Add(new EmployeeNotification
        {
            TenantId = tenantId, EmployeeId = request.EmployeeId, NotificationType = "Warning",
            Title = "Leave rejected",
            Body = $"Your {request.LeaveTypeName} request ({request.StartDate:dd MMM} – {request.EndDate:dd MMM}) was rejected: {reason}",
        });

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

        var wasApproved = request.Status is "Approved" or "CancellationRequested";
        var previousStatus = request.Status;
        request.Status = "Cancelled";
        request.CancellationReason = reason;
        request.CancelledAtUtc = DateTime.UtcNow;

        await ReleaseRequestBalancesAsync(tenantId, request, wasApproved,
            $"Cancelled: {reason}", cancelledByName, ct);

        // Remove any unpaid-leave payroll deduction that has not yet been picked up by a run,
        // so cancelling an approved unpaid leave does not still dock the employee's salary.
        // Already-processed impacts (Status == "Processed") are left for audit integrity.
        var pendingImpacts = await _db.LeavePayrollImpacts
            .Where(x => x.TenantId == tenantId && x.LeaveRequestId == requestId && x.Status != "Processed")
            .ToListAsync(ct);
        if (pendingImpacts.Count > 0)
            _db.LeavePayrollImpacts.RemoveRange(pendingImpacts);

        var projection = await _db.ApprovalRequests.FirstOrDefaultAsync(x => x.TenantId == tenantId
            && x.EntityName == nameof(LeaveRequest) && x.EntityId == requestId.ToString() && x.Status == "Pending", ct);
        if (projection is not null)
        {
            projection.Status = "Cancelled";
            projection.CompletedAtUtc = DateTime.UtcNow;
        }
        var pendingApprovals = await _db.LeaveApprovals
            .Where(x => x.TenantId == tenantId && x.LeaveRequestId == requestId && x.Decision == "Pending")
            .ToListAsync(ct);
        foreach (var approval in pendingApprovals) approval.Decision = "Cancelled";

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

    private async Task<List<LeaveYearSegment>> CalculateYearSegmentsAsync(
        Guid tenantId, DateOnly start, DateOnly end, Guid? policyId, CancellationToken ct)
    {
        var segments = new List<LeaveYearSegment>();
        for (var year = start.Year; year <= end.Year; year++)
        {
            var segmentStart = year == start.Year ? start : new DateOnly(year, 1, 1);
            var segmentEnd = year == end.Year ? end : new DateOnly(year, 12, 31);
            var days = await CalculateWorkingDaysAsync(tenantId, segmentStart, segmentEnd, policyId, ct);
            if (days > 0) segments.Add(new LeaveYearSegment(year, days));
        }
        return segments;
    }

    private async Task<List<LeaveYearSegment>> CalculateRequestYearSegmentsAsync(
        Guid tenantId, LeaveRequest request, Guid? policyId, CancellationToken ct)
    {
        var segments = await CalculateYearSegmentsAsync(tenantId, request.StartDate, request.EndDate, policyId, ct);
        if (segments.Count == 1 && (request.DayType.StartsWith("Half", StringComparison.OrdinalIgnoreCase)
            || request.DayType.Equals("Hourly", StringComparison.OrdinalIgnoreCase)))
            segments[0] = segments[0] with { Days = request.TotalDays };
        return segments;
    }

    private async Task ReleaseRequestBalancesAsync(
        Guid tenantId, LeaveRequest request, bool releaseUsed, string reason, string performedBy, CancellationToken ct)
    {
        foreach (var segment in await CalculateRequestYearSegmentsAsync(tenantId, request, request.PolicyId, ct))
        {
            var balance = await _db.EmployeeLeaveBalances.FirstOrDefaultAsync(b =>
                b.TenantId == tenantId && b.EmployeeId == request.EmployeeId
                && b.LeaveTypeId == request.LeaveTypeId && b.Year == segment.Year, ct);
            if (balance is null) continue;
            var before = balance.Available;
            if (releaseUsed) balance.Used = Math.Max(0, balance.Used - segment.Days);
            else balance.Pending = Math.Max(0, balance.Pending - segment.Days);
            balance.UpdatedAtUtc = DateTime.UtcNow;
            _db.LeaveBalanceTransactions.Add(new LeaveBalanceTransaction
            {
                TenantId = tenantId, CompanyId = request.CompanyId, EmployeeId = request.EmployeeId,
                LeaveTypeId = request.LeaveTypeId, Year = segment.Year, TransactionType = "Reversed",
                Amount = segment.Days, BalanceBefore = before, BalanceAfter = balance.Available,
                Reference = request.Id.ToString(), Reason = reason, PerformedByName = performedBy
            });
        }
    }

    private sealed record LeaveYearSegment(int Year, decimal Days);

    // Resolves the UserAccountId for an employee (used to route the LeaveApproval record to the right user inbox)
    private async Task<Guid?> ResolveUserIdAsync(Guid tenantId, int employeeId, CancellationToken ct)
        => await _db.Employees
            .AsNoTracking()
            .Where(e => e.TenantId == tenantId && e.Id == employeeId)
            .Select(e => e.UserAccountId)
            .FirstOrDefaultAsync(ct);

    private async Task EnsureMakerCheckerAsync(LeaveRequest request, Guid approverId, CancellationToken ct)
    {
        var employeeUserId = await _db.Employees.AsNoTracking()
            .Where(e => e.TenantId == request.TenantId && e.Id == request.EmployeeId && !e.IsDeleted)
            .Select(e => e.UserAccountId)
            .FirstOrDefaultAsync(ct);
        var actualMakerUserId = _db.ApprovalRequests.Local.FirstOrDefault(a => a.Id == request.Id)?.RequestedByUserId
            ?? await _db.ApprovalRequests.AsNoTracking()
                .Where(a => a.TenantId == request.TenantId && a.Id == request.Id)
                .Select(a => a.RequestedByUserId)
                .FirstOrDefaultAsync(ct);
        if (employeeUserId == approverId || actualMakerUserId == approverId)
            throw new InvalidOperationException("Maker-checker violation: the requester cannot approve or reject their own leave request.");
    }

    private async Task<LeaveApproval> EnsureCanonicalApprovalAsync(
        LeaveRequest request,
        LeaveApproval? pendingApproval,
        CancellationToken ct)
    {
        var changed = false;
        var projection = _db.ApprovalRequests.Local.FirstOrDefault(x => x.Id == request.Id)
            ?? await _db.ApprovalRequests.FirstOrDefaultAsync(
                x => x.TenantId == request.TenantId && x.Id == request.Id, ct);
        if (pendingApproval is null)
        {
            // A projection or any historical step proves that this is not a pre-canonical legacy
            // row. In particular, a concurrent winner may have consumed the pending step after
            // this context loaded the request; never manufacture a replacement step in that race.
            var hasApprovalHistory = await _db.LeaveApprovals.AsNoTracking().AnyAsync(
                a => a.TenantId == request.TenantId && a.LeaveRequestId == request.Id, ct);
            if (projection is not null || hasApprovalHistory)
                throw new InvalidOperationException("The current leave approval step has already been decided.");

            // Compatibility bridge for historical/demo rows created before submission produced
            // route records. New requests always enter through the canonical path above.
            pendingApproval = new LeaveApproval
            {
                TenantId = request.TenantId,
                LeaveRequestId = request.Id,
                StepNumber = 1,
                ApproverRole = "HR Manager",
                Decision = "Pending"
            };
            _db.LeaveApprovals.Add(pendingApproval);
            changed = true;
        }

        if (projection is null)
        {
            var approverEmployeeId = pendingApproval.ApproverId.HasValue
                ? await _db.Employees.AsNoTracking()
                    .Where(e => e.TenantId == request.TenantId && e.UserAccountId == pendingApproval.ApproverId && !e.IsDeleted)
                    .Select(e => (int?)e.Id)
                    .FirstOrDefaultAsync(ct)
                : null;
            var requestedBy = await _db.Employees.AsNoTracking()
                .Where(e => e.TenantId == request.TenantId && e.Id == request.EmployeeId && !e.IsDeleted)
                .Select(e => e.UserAccountId)
                .FirstOrDefaultAsync(ct);
            _db.ApprovalRequests.Add(BuildApprovalProjection(
                request, pendingApproval, approverEmployeeId, Guid.Empty, requestedBy));
            changed = true;
        }

        // The pending approval must exist in the database before ExecuteUpdate can consume it.
        // This save stays inside the caller-owned transaction.
        if (changed) await _db.SaveChangesAsync(ct);
        return pendingApproval;
    }

    private async Task ConsumePendingApprovalAsync(
        LeaveApproval approval,
        string decision,
        Guid approverId,
        string approverName,
        string notes,
        CancellationToken ct)
    {
        var actedAt = DateTime.UtcNow;
        if (_db.Database.IsRelational())
        {
            var rows = await _db.LeaveApprovals
                .Where(a => a.TenantId == approval.TenantId
                    && a.Id == approval.Id
                    && a.LeaveRequestId == approval.LeaveRequestId
                    && a.Decision == "Pending")
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(a => a.Decision, decision)
                    .SetProperty(a => a.ApproverId, approverId)
                    .SetProperty(a => a.ApproverName, approverName)
                    .SetProperty(a => a.Notes, notes)
                    .SetProperty(a => a.ActedAtUtc, actedAt), ct);
            if (rows != 1)
                throw new InvalidOperationException("The current leave approval step has already been decided.");

            // ExecuteUpdate bypasses the tracker. Do not let the stale tracked copy overwrite the CAS.
            _db.Entry(approval).State = EntityState.Detached;
        }
        else
        {
            if (!string.Equals(approval.Decision, "Pending", StringComparison.Ordinal))
                throw new InvalidOperationException("The current leave approval step has already been decided.");
            approval.Decision = decision;
            approval.ApproverId = approverId;
            approval.ApproverName = approverName;
            approval.Notes = notes;
            approval.ActedAtUtc = actedAt;
        }
    }

    private async Task SyncApprovalProjectionAsync(
        LeaveRequest request,
        LeaveApproval decidedApproval,
        string decision,
        Guid approverId,
        string comments,
        LeaveApproval? nextApproval,
        int? nextApproverEmployeeId,
        CancellationToken ct)
    {
        var projection = _db.ApprovalRequests.Local.FirstOrDefault(x => x.Id == request.Id)
            ?? await _db.ApprovalRequests.FirstAsync(
                x => x.TenantId == request.TenantId && x.Id == request.Id, ct);
        var now = DateTime.UtcNow;
        _db.ApprovalDecisions.Add(new ApprovalDecision
        {
            TenantId = request.TenantId,
            ApprovalRequestId = projection.Id,
            StepOrder = decidedApproval.StepNumber,
            Decision = decision,
            Comments = comments,
            DecidedByUserId = approverId,
            DecidedAtUtc = now
        });

        if (nextApproval is not null)
        {
            var role = NormalizeApproverRole(nextApproval.ApproverRole);
            projection.Status = "Pending";
            projection.CurrentStepOrder = nextApproval.StepNumber;
            projection.CurrentApproverEmployeeId = nextApproverEmployeeId;
            projection.CurrentApproverUserId = nextApproval.ApproverId;
            projection.CurrentApproverName = nextApproval.ApproverName;
            projection.CurrentApproverRole = role;
            projection.CurrentApproverType = nextApproval.ApproverId.HasValue ? nextApproval.ApproverRole : "Role";
            projection.CurrentQueue = nextApproval.ApproverId.HasValue
                ? $"{nextApproval.ApproverRole}:{nextApproval.ApproverName}"
                : $"Role:{role}";
            projection.DueAtUtc = now.AddHours(projection.SlaHours);
            projection.LastRoutedAtUtc = now;
            projection.CompletedAtUtc = null;
            return;
        }

        projection.Status = decision;
        projection.CompletedAtUtc = now;
        projection.CurrentApproverEmployeeId = null;
        projection.CurrentApproverUserId = null;
        projection.CurrentApproverName = string.Empty;
        projection.CurrentQueue = string.Empty;
        projection.DueAtUtc = null;
    }

    private static string StatusForPendingStep(ResolvedApprovalStep step)
        => string.Equals(step.ApproverType, "HR", StringComparison.OrdinalIgnoreCase)
            || string.Equals(step.ApproverType, "HRBusinessPartner", StringComparison.OrdinalIgnoreCase)
            ? "PendingHRApproval"
            : "PendingManagerApproval";

    private static string ResolvedApproverRole(ResolvedApprovalStep step)
        => string.Equals(step.ApproverType, "Role", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(step.ApproverRole)
                ? step.ApproverRole.Trim()
                : step.ApproverType;

    private static ApprovalRequest BuildApprovalProjection(
        LeaveRequest request,
        LeaveApproval currentApproval,
        int? currentApproverEmployeeId,
        Guid workflowId,
        Guid? requestedByUserId)
    {
        var approverRole = NormalizeApproverRole(currentApproval.ApproverRole);
        var now = DateTime.UtcNow;
        return new ApprovalRequest
        {
            // The leave request ID is the stable identity exposed by both operational surfaces.
            // ApprovalRequest is an indexed routing projection, never a second decision owner.
            Id = request.Id,
            TenantId = request.TenantId,
            WorkflowId = workflowId,
            EntityName = nameof(LeaveRequest),
            EntityId = request.Id.ToString(),
            Title = $"{request.LeaveTypeName} — {request.EmployeeName}",
            Status = "Pending",
            CurrentStepOrder = currentApproval.StepNumber,
            RequestedByUserId = requestedByUserId,
            RequestedForEmployeeId = request.EmployeeId,
            CompanyId = request.CompanyId,
            CurrentApproverEmployeeId = currentApproverEmployeeId,
            CurrentApproverUserId = currentApproval.ApproverId,
            CurrentApproverName = currentApproval.ApproverName,
            CurrentApproverRole = approverRole,
            CurrentApproverType = currentApproval.ApproverId.HasValue ? currentApproval.ApproverRole : "Role",
            CurrentQueue = currentApproval.ApproverId.HasValue
                ? $"{currentApproval.ApproverRole}:{currentApproval.ApproverName}"
                : $"Role:{approverRole}",
            SlaHours = 24,
            DueAtUtc = now.AddHours(24),
            LastRoutedAtUtc = now,
            CreatedAtUtc = request.SubmittedAtUtc ?? now,
            Priority = request.IsEmergency ? "High" : "Normal"
        };
    }

    private static string NormalizeApproverRole(string? approverType)
        => approverType?.Trim().ToUpperInvariant() switch
        {
            "HR" or "HRBUSINESSPARTNER" => "HR Manager",
            "MANAGER" or "DIRECTMANAGER" or "SUPERVISOR" or "DEPARTMENTHEAD" => "Manager",
            _ => string.IsNullOrWhiteSpace(approverType) ? "HR Manager" : approverType.Trim()
        };

    private async Task<LeavePolicy?> ResolveLeavePolicyAsync(Guid tenantId, Employee employee, Guid leaveTypeId, Guid? requestedPolicyId, CancellationToken ct)
    {
        var policies = await _db.LeavePolicies
            .Where(p => p.TenantId == tenantId && p.LeaveTypeId == leaveTypeId && p.Status == "Active")
            .ToListAsync(ct);
        var eligibilityRows = await _db.LeavePolicyEligibilities
            .Where(e => e.TenantId == tenantId && e.IsActive)
            .ToListAsync(ct);
        var countryCode = employee.CompanyId.HasValue
            ? await _db.Companies.AsNoTracking()
                .Where(c => c.TenantId == tenantId && c.Id == employee.CompanyId.Value)
                .Select(c => c.CountryCode)
                .FirstOrDefaultAsync(ct)
            : string.Empty;

        if (requestedPolicyId.HasValue)
        {
            var requested = policies.FirstOrDefault(p => p.Id == requestedPolicyId.Value);
            if (requested is null)
                throw new InvalidOperationException("Selected leave policy is not active for this leave type.");
            if (!IsPolicyEligible(requested, eligibilityRows, countryCode, employee.CompanyId, employee.BranchId, employee.DepartmentId, employee.GradeId, employee.Department, employee.Grade, employee.EmploymentType, employee.ContractType, employee.Gender))
                throw new InvalidOperationException("Selected leave policy is not eligible for the employee's country, legal entity, branch, department, grade, employment type, or contract type.");
            return requested;
        }

        return policies
            .Where(p => IsPolicyEligible(p, eligibilityRows, countryCode, employee.CompanyId, employee.BranchId, employee.DepartmentId, employee.GradeId, employee.Department, employee.Grade, employee.EmploymentType, employee.ContractType, employee.Gender))
            .OrderByDescending(p => PolicySpecificity(p, eligibilityRows, employee.DepartmentId, employee.GradeId))
            .ThenByDescending(p => p.UpdatedAtUtc)
            .FirstOrDefault();
    }

    private static bool IsPolicyEligible(
        LeavePolicy policy,
        IReadOnlyCollection<LeavePolicyEligibility> rows,
        string? countryCode,
        Guid? companyId,
        Guid? branchId,
        Guid? departmentId,
        Guid? gradeId,
        string? departmentName,
        string? grade,
        string? employmentType,
        string? contractType,
        string? gender)
    {
        if (!string.IsNullOrWhiteSpace(policy.CountryCode) && !string.Equals(policy.CountryCode, countryCode, StringComparison.OrdinalIgnoreCase)) return false;
        if (policy.CompanyId.HasValue && policy.CompanyId != companyId) return false;
        if (policy.BranchId.HasValue && policy.BranchId != branchId) return false;
        if (!string.IsNullOrWhiteSpace(policy.DepartmentName) && !string.Equals(policy.DepartmentName, departmentName, StringComparison.OrdinalIgnoreCase)) return false;
        if (!string.IsNullOrWhiteSpace(policy.Grade) && !string.Equals(policy.Grade, grade, StringComparison.OrdinalIgnoreCase)) return false;
        if (!string.IsNullOrWhiteSpace(policy.EmploymentType) && !string.Equals(policy.EmploymentType, employmentType, StringComparison.OrdinalIgnoreCase)) return false;
        if (!string.IsNullOrWhiteSpace(policy.ContractType) && !string.Equals(policy.ContractType, contractType, StringComparison.OrdinalIgnoreCase)) return false;
        if (!string.IsNullOrWhiteSpace(policy.Gender) && !string.Equals(policy.Gender, gender, StringComparison.OrdinalIgnoreCase)) return false;

        var scopedRows = rows.Where(r => r.LeavePolicyId == policy.Id).ToList();
        if (scopedRows.Count == 0) return true;
        return scopedRows.Any(r =>
            (string.IsNullOrWhiteSpace(r.CountryCode) || string.Equals(r.CountryCode, countryCode, StringComparison.OrdinalIgnoreCase)) &&
            (!r.CompanyId.HasValue || r.CompanyId == companyId) &&
            (!r.BranchId.HasValue || r.BranchId == branchId) &&
            (!r.DepartmentId.HasValue || r.DepartmentId == departmentId) &&
            (!r.GradeId.HasValue || r.GradeId == gradeId) &&
            (string.IsNullOrWhiteSpace(r.EmploymentType) || string.Equals(r.EmploymentType, employmentType, StringComparison.OrdinalIgnoreCase)) &&
            (string.IsNullOrWhiteSpace(r.ContractType) || string.Equals(r.ContractType, contractType, StringComparison.OrdinalIgnoreCase)));
    }

    private static int PolicySpecificity(LeavePolicy policy, IReadOnlyCollection<LeavePolicyEligibility> rows, Guid? departmentId, Guid? gradeId)
    {
        var score = 0;
        if (policy.CompanyId.HasValue) score += 8;
        if (policy.BranchId.HasValue) score += 6;
        if (!string.IsNullOrWhiteSpace(policy.DepartmentName)) score += 4;
        if (!string.IsNullOrWhiteSpace(policy.Grade)) score += 3;
        if (!string.IsNullOrWhiteSpace(policy.EmploymentType)) score += 2;
        if (!string.IsNullOrWhiteSpace(policy.ContractType)) score += 2;
        var rowScore = rows.Where(r => r.LeavePolicyId == policy.Id)
            .Select(r =>
                (r.CompanyId.HasValue ? 8 : 0) +
                (r.BranchId.HasValue ? 6 : 0) +
                (r.DepartmentId.HasValue && r.DepartmentId == departmentId ? 5 : 0) +
                (r.GradeId.HasValue && r.GradeId == gradeId ? 4 : 0) +
                (!string.IsNullOrWhiteSpace(r.EmploymentType) ? 2 : 0) +
                (!string.IsNullOrWhiteSpace(r.ContractType) ? 2 : 0))
            .DefaultIfEmpty(0)
            .Max();
        return score + rowScore;
    }
}
