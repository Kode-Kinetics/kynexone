using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Data;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Finance;

/// <summary>Observes employment changes; never changes principal, paid history or an existing schedule.</summary>
public sealed class LoanLifecycleService(ZayraDbContext db)
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    public static readonly string[] ExceptionCodes = ["MinService", "Probation", "AmountLimit", "ConcurrentLoans", "Cooldown", "SalaryAffordability", "ContractType"];

    public async Task<LoanLifecycleRefreshResult> RefreshAsync(Guid tenantId, EmployeeLoan loan, CancellationToken ct)
    {
        if (loan.TenantId != tenantId) throw new InvalidOperationException("Loan tenant mismatch.");
        if (loan.IsDeleted || loan.Status is "Cancelled" or "Rejected" or "Settled" or "Closed")
            return new(false, loan.ReviewRequired, loan.ReviewReason, loan.CollectionStatus);
        var current = await CaptureAsync(tenantId, loan, ct);
        if (current != null && current.CompanyId != loan.CompanyId)
        {
            // The original lender must know that a transfer occurred, not the new employer's
            // compensation/contract/leave details. Never copy those facts into its audit ledger.
            current = current with { Salary = 0, SalaryCurrency = string.Empty, GradeId = null, DesignationId = null,
                Grade = string.Empty, Designation = string.Empty, JobTitle = string.Empty, ContractType = string.Empty,
                ContractStartDate = null, ContractEndDate = null, ProbationStartDate = null, ProbationEndDate = null,
                ConfirmationDate = null, UnpaidLeaveIds = [], OffboardingId = null, OffboardingStatus = null,
                SeparationType = null, LastWorkingDay = null, LatestOffboardingId = null, LatestOffboardingStatus = null,
                HasActiveSeparation = false, OnProbation = false, ContractExpired = false };
        }
        var previous = ReadSnapshot(loan.EmploymentSnapshotJson);
        var reasons = new List<string>();
        if (current == null)
            reasons.Add("Employment record is unavailable in this company's scope.");
        else if (previous == null)
        {
            if (current.EmployeeStatus != EmployeeStatuses.Active) reasons.Add("Employee is not active.");
            if (current.CompanyId != loan.CompanyId) reasons.Add("Employee legal entity differs from the loan owner.");
            if (current.HasActiveSeparation) reasons.Add("Employee has an active separation or notice record.");
            if (current.ContractExpired) reasons.Add("Employment contract has expired.");
            if (current.UnpaidLeaveIds.Length > 0) reasons.Add("Employee is on approved unpaid leave.");
        }
        else
        {
            if (previous.EmployeeStatus != current.EmployeeStatus) reasons.Add("Employment status changed.");
            if (previous.CompanyId != current.CompanyId) reasons.Add("Employee legal entity changed; the original loan owner and debt are retained.");
            if (previous.GradeId != current.GradeId || previous.DesignationId != current.DesignationId || previous.Grade != current.Grade || previous.Designation != current.Designation || previous.JobTitle != current.JobTitle)
                reasons.Add("Grade or designation changed; approved loan terms remain unchanged.");
            if (previous.Salary != current.Salary || previous.SalaryCurrency != current.SalaryCurrency) reasons.Add("Salary changed; affordability requires review.");
            if (previous.ContractType != current.ContractType || previous.ContractStartDate != current.ContractStartDate || previous.ContractEndDate != current.ContractEndDate || previous.ContractExpired != current.ContractExpired)
                reasons.Add("Employment contract changed or expired.");
            if (previous.ProbationStartDate != current.ProbationStartDate || previous.ProbationEndDate != current.ProbationEndDate || previous.ConfirmationDate != current.ConfirmationDate || previous.OnProbation != current.OnProbation)
                reasons.Add("Probation or confirmation changed.");
            if (previous.JoiningDate != current.JoiningDate) reasons.Add("Joining or rehire date changed; existing debt is retained.");
            if (previous.OffboardingId != current.OffboardingId || previous.OffboardingStatus != current.OffboardingStatus || previous.SeparationType != current.SeparationType || previous.LastWorkingDay != current.LastWorkingDay
                || previous.LatestOffboardingId != current.LatestOffboardingId || previous.LatestOffboardingStatus != current.LatestOffboardingStatus || previous.HasActiveSeparation != current.HasActiveSeparation)
                reasons.Add("Separation, notice, or withdrawal changed; a manual review is required.");
            if (!previous.UnpaidLeaveIds.SequenceEqual(current.UnpaidLeaveIds)) reasons.Add("Approved unpaid leave changed.");
        }
        var newEstateCase = current != null && IsEstateCase(current) && (previous == null || !IsEstateCase(previous));
        if (newEstateCase) reasons.Add("Death or incapacity requires an authorized collection review; debt is not written off.");
        var oldSnapshot = loan.EmploymentSnapshotJson;
        // Restricted financial projection, NOT EmployeeHistory or a serialized Employee: the typed
        // LoanEmploymentSnapshot contains only loan review facts. Exact salary is needed to detect
        // affordability changes, while foreign-company compensation is cleared above. Its explicit
        // field allowlist and exclusion of identity/bank/medical data are pinned by LoanLifecycleTests;
        // EmployeeSnapshotMaskingTests exempts only this exact assignment in this exact source file.
        if (current != null) loan.EmploymentSnapshotJson = JsonSerializer.Serialize(current);
        var newReasons = reasons.Where(reason => !loan.ReviewRequired || !loan.ReviewReason.Contains(reason, StringComparison.Ordinal)).ToArray();
        if (reasons.Count > 0) loan.ReviewRequired = true;
        if (newEstateCase) loan.CollectionStatus = "OnHold";
        var delinquencyChanged = await RefreshDelinquencyAsync(tenantId, loan, ct);
        if (reasons.Count > 0 && (newReasons.Length > 0 || oldSnapshot != loan.EmploymentSnapshotJson))
        {
            loan.ReviewReason = string.Join(" ", new[] { loan.ReviewReason }.Where(x => !string.IsNullOrWhiteSpace(x)).Concat(newReasons));
            loan.UpdatedAtUtc = DateTime.UtcNow;
            db.LoanAuditLogs.Add(new LoanAuditLog
            {
                TenantId = tenantId, LoanId = loan.Id, Action = "EmploymentReviewRequired", OldValuesJson = oldSnapshot,
                NewValuesJson = JsonSerializer.Serialize(new { reasons, loan.CollectionStatus, EmploymentSnapshot = current }),
                PerformedByName = "Loan lifecycle monitor"
            });
            await NotifyAsync(tenantId, loan, "Loan employment review", "Employment changes require a review of your loan. Existing balances and payments are retained.", ct);
        }
        // Snapshot capture and flags are saved atomically by the caller, under the loan lock.
        return new(delinquencyChanged || oldSnapshot != loan.EmploymentSnapshotJson || newReasons.Length > 0, loan.ReviewRequired, loan.ReviewReason, loan.CollectionStatus);
    }

    private async Task<bool> RefreshDelinquencyAsync(Guid tenantId, EmployeeLoan loan, CancellationToken ct)
    {
        if (loan.Status is not ("Active" or "Overdue")) return false;
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var overdue = await db.LoanInstallments.AnyAsync(x => x.TenantId == tenantId && x.LoanId == loan.Id && x.DueDate < today
            && x.AmountDue > x.AmountPaid && x.Status != "Cancelled" && x.Status != "Waived", ct);
        var status = overdue ? "Overdue" : "Active";
        if (status == loan.Status) return false;
        var oldStatus = loan.Status;
        loan.Status = status;
        loan.UpdatedAtUtc = DateTime.UtcNow;
        db.LoanAuditLogs.Add(new LoanAuditLog
        {
            TenantId = tenantId, LoanId = loan.Id, Action = overdue ? "LoanBecameOverdue" : "LoanBecameCurrent",
            OldValuesJson = JsonSerializer.Serialize(new { Status = oldStatus }),
            NewValuesJson = JsonSerializer.Serialize(new { Status = status, loan.OutstandingBalance }), PerformedByName = "Loan lifecycle monitor"
        });
        if (overdue && loan.CollectionStatus != "OnHold")
            await NotifyAsync(tenantId, loan, "Loan installment overdue", "Your loan has an overdue installment. Review the repayment schedule or contact Finance.", ct);
        return true;
    }

    public async Task<LoanEmploymentSnapshot?> CaptureAsync(Guid tenantId, EmployeeLoan loan, CancellationToken ct)
    {
        // Deliberately use normal company filters. A reviewer of the original lender may not inspect
        // a transferred employee's new employer; unavailable employment is itself a review condition.
        var employee = await db.Employees.AsNoTracking().FirstOrDefaultAsync(x => x.TenantId == tenantId && x.Id == loan.EmployeeIntId && !x.IsDeleted, ct);
        if (employee == null) return null;
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var salary = await db.EmployeeSalaryStructures.AsNoTracking().Where(x => x.TenantId == tenantId && x.EmployeeId == employee.Id && x.IsActive && x.EffectiveDate <= today)
            .OrderByDescending(x => x.EffectiveDate).ThenByDescending(x => x.CreatedAtUtc).FirstOrDefaultAsync(ct);
        var offboardings = await db.EmployeeOffboardings.AsNoTracking().Where(x => x.TenantId == tenantId && x.EmployeeId == employee.Id)
            .OrderByDescending(x => x.CreatedAtUtc).ThenByDescending(x => x.Id).ToListAsync(ct);
        var latestOffboarding = offboardings.FirstOrDefault();
        var activeOffboarding = offboardings.FirstOrDefault(x => x.Status != "Cancelled");
        var offboarding = activeOffboarding ?? latestOffboarding;
        var salaryCurrency = salary?.Currency ?? await db.Companies.AsNoTracking().Where(x => x.TenantId == tenantId && x.Id == employee.CompanyId)
            .Select(x => x.DefaultCurrency).FirstOrDefaultAsync(ct) ?? loan.Currency ?? string.Empty;
        var unpaidIds = await (from leave in db.LeaveRequests.AsNoTracking()
                              join type in db.LeaveTypes.AsNoTracking() on leave.LeaveTypeId equals type.Id
                              where leave.TenantId == tenantId && type.TenantId == tenantId && leave.EmployeeId == employee.Id
                                  && leave.Status == "Approved" && leave.StartDate <= today && leave.EndDate >= today
                                  && (!type.IsPaid || leave.PayrollImpact == "Unpaid")
                              orderby leave.Id
                              select leave.Id).ToArrayAsync(ct);
        return new(employee.Status, employee.CompanyId, employee.GradeId, employee.DesignationId,
            salary == null ? employee.Salary ?? 0m : salary.BasicSalary + salary.HousingAllowance + salary.TransportAllowance + salary.FoodAllowance + salary.MobileAllowance + salary.OtherAllowance,
            employee.ContractType, employee.ContractStartDate, employee.ContractEndDate, employee.ProbationStartDate,
            employee.ProbationEndDate, employee.ConfirmationDate, employee.JoiningDate,
            employee.ProbationEndDate.HasValue && employee.ProbationEndDate >= today && !employee.ConfirmationDate.HasValue,
            employee.ContractEndDate.HasValue && employee.ContractEndDate < today,
            offboarding?.Id, offboarding?.Status, offboarding?.SeparationType, offboarding?.LastWorkingDay, unpaidIds,
            employee.Grade, employee.Designation, employee.JobTitle, salaryCurrency, activeOffboarding != null, latestOffboarding?.Id, latestOffboarding?.Status);
    }

    public static bool IsEstateCase(LoanEmploymentSnapshot context) =>
        context.EmployeeStatus is "Deceased" or "Incapacitated" || (context.HasActiveSeparation && context.SeparationType is "Death" or "Incapacity");

    public static LoanEmploymentSnapshot? ReadSnapshot(string json)
    {
        try
        {
            var result = JsonSerializer.Deserialize<LoanEmploymentSnapshot>(json, JsonOptions);
            return string.IsNullOrWhiteSpace(result?.EmployeeStatus) ? null : result with { UnpaidLeaveIds = result.UnpaidLeaveIds ?? [] };
        }
        catch (JsonException) { return null; }
    }

    public static bool PolicyAllows(EmployeeLoan loan, string property)
    {
        try
        {
            using var doc = JsonDocument.Parse(loan.PolicySnapshotJson);
            return doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.EnumerateObject()
                .Any(x => x.Name.Equals(property, StringComparison.OrdinalIgnoreCase) && x.Value.ValueKind == JsonValueKind.True);
        }
        catch (JsonException) { return false; }
    }

    public static string ExceptionSnapshot(EmployeeLoan loan, IEnumerable<string> codes) => JsonSerializer.Serialize(
        new LoanExceptionSnapshot(codes.Distinct(StringComparer.Ordinal).OrderBy(x => x).ToArray(), loan.PolicyId, loan.PolicyVersion, loan.PolicySnapshotJson));

    public static LoanExceptionSnapshot? ReadException(string json)
    {
        try { return JsonSerializer.Deserialize<LoanExceptionSnapshot>(json, JsonOptions); }
        catch (JsonException) { return null; }
    }

    public async Task<bool> IsExceptionApprovedAsync(Guid tenantId, EmployeeLoan loan, string code, CancellationToken ct)
    {
        if (loan.TenantId != tenantId || !ExceptionCodes.Contains(code, StringComparer.Ordinal) || !PolicyAllows(loan, "AllowExceptions")) return false;
        var requests = await db.Set<LoanChangeRequest>().AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.LoanId == loan.Id && x.ChangeType == "PolicyException" && x.Status == "Approved").ToListAsync(ct);
        return requests.Any(request => ExceptionMatches(loan, ReadException(request.RequestedExceptionsJson), code));
    }

    public static bool ExceptionMatches(EmployeeLoan loan, LoanExceptionSnapshot? snapshot, string? code = null) => snapshot != null
        && snapshot.PolicyId == loan.PolicyId && snapshot.PolicyVersion == loan.PolicyVersion && snapshot.PolicySnapshotJson == loan.PolicySnapshotJson
        && snapshot.Codes is { Length: > 0 } && snapshot.Codes.All(x => ExceptionCodes.Contains(x, StringComparer.Ordinal))
        && (code == null || snapshot.Codes.Contains(code, StringComparer.Ordinal));

    public async Task NotifyAsync(Guid tenantId, EmployeeLoan loan, string title, string message, CancellationToken ct)
    {
        var employee = await db.Employees.AsNoTracking().FirstOrDefaultAsync(x => x.TenantId == tenantId && x.Id == loan.EmployeeIntId && !x.IsDeleted, ct);
        var candidates = await db.LoanApprovals.AsNoTracking().Where(x => x.TenantId == tenantId && x.LoanId == loan.Id && x.ApprovedBy.HasValue)
            .Select(x => x.ApprovedBy!.Value).ToListAsync(ct);
        var batchActors = await (from line in db.Set<LoanDisbursementLine>().AsNoTracking()
                                 join batch in db.Set<LoanDisbursementBatch>().AsNoTracking() on line.BatchId equals batch.Id
                                 where line.TenantId == tenantId && batch.TenantId == tenantId && line.LoanId == loan.Id
                                 select new { batch.CreatedBy, batch.ApprovedBy, batch.PaidBy }).ToListAsync(ct);
        candidates.AddRange(batchActors.SelectMany(x => new[] { x.CreatedBy, x.ApprovedBy, x.PaidBy }).Where(x => x.HasValue).Select(x => x!.Value));
        // Finance receives a targeted inbox item only when an explicit grant names this company.
        // No null-recipient notification and no tenant-wide role broadcast is created.
        candidates.AddRange(await db.UserEntityAccesses.AsNoTracking().Where(x => x.TenantId == tenantId && x.IsActive && x.CompanyId == loan.CompanyId
            && x.CompanyId != null && (x.Role == "Finance" || x.Role == "Finance Approver")).Select(x => x.UserId).ToListAsync(ct));
        if (employee?.HRBusinessPartnerEmployeeId is int hrId)
        {
            var hrUser = await db.Employees.AsNoTracking().Where(x => x.TenantId == tenantId && x.Id == hrId && x.CompanyId == loan.CompanyId && !x.IsDeleted)
                .Select(x => x.UserAccountId).FirstOrDefaultAsync(ct);
            if (hrUser.HasValue) candidates.Add(hrUser.Value);
        }
        var roles = new[] { "Admin", "Finance", "Finance Approver", "HR Manager", "HR Director" };
        var staff = await (from user in db.Users.AsNoTracking()
                           join assignment in db.UserRoles on user.Id equals assignment.UserId
                           join role in db.Roles on assignment.RoleId equals role.Id
                           where user.TenantId == tenantId && user.IsActive && !user.IsDeleted && candidates.Contains(user.Id)
                               && (role.TenantId == tenantId || role.TenantId == null) && role.IsActive && !role.IsDeleted && roles.Contains(role.Name)
                           select new { user.Id, user.IsGroupScope }).Distinct().ToListAsync(ct);
        var recipients = new HashSet<Guid>();
        foreach (var user in staff)
            if (user.IsGroupScope || await db.UserEntityAccesses.AnyAsync(x => x.TenantId == tenantId && x.UserId == user.Id && x.IsActive
                    && (x.CompanyId == loan.CompanyId || x.GrantMode == "AllCurrentCompanies" || x.GrantMode == "AllCurrentAndFutureCompanies"), ct)) recipients.Add(user.Id);
        if (employee?.UserAccountId is Guid employeeUser && await db.Users.AnyAsync(x => x.TenantId == tenantId && x.Id == employeeUser && x.IsActive && !x.IsDeleted, ct)) recipients.Add(employeeUser);
        foreach (var recipient in recipients)
            db.Notifications.Add(new Notification { TenantId = tenantId, UserId = recipient, Title = title, Message = message, EntityName = "EmployeeLoan", EntityId = loan.Id.ToString() });
    }
}

public sealed record LoanLifecycleRefreshResult(bool Changed, bool ReviewRequired, string ReviewReason, string CollectionStatus);
public sealed record LoanExceptionSnapshot(string[] Codes, Guid? PolicyId, int? PolicyVersion, string PolicySnapshotJson);
public sealed record LoanEmploymentSnapshot(string EmployeeStatus, Guid? CompanyId, Guid? GradeId, Guid? DesignationId, decimal Salary,
    string ContractType, DateOnly? ContractStartDate, DateOnly? ContractEndDate, DateOnly? ProbationStartDate, DateOnly? ProbationEndDate,
    DateOnly? ConfirmationDate, DateTime JoiningDate, bool OnProbation, bool ContractExpired, Guid? OffboardingId, string? OffboardingStatus,
    string? SeparationType, DateOnly? LastWorkingDay, Guid[] UnpaidLeaveIds, string Grade, string Designation, string JobTitle,
    string SalaryCurrency, bool HasActiveSeparation, Guid? LatestOffboardingId, string? LatestOffboardingStatus);
