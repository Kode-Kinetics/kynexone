using System.Reflection;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Zayra.Api.Application.Common;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Models;

namespace Zayra.Api.Data;

public class ZayraDbContext : DbContext
{
    /// <summary>
    /// IHttpContextAccessor is a singleton backed by AsyncLocal, so it always reflects the
    /// current request's HttpContext even when this DbContext instance is reused from the
    /// DbContextPool (pool reuse skips the constructor; reading lazily here avoids stale
    /// per-request values that caused the "Company not found or not active" bug).
    /// </summary>
    private readonly IHttpContextAccessor? _httpContextAccessor;
    private readonly ILogger<ZayraDbContext>? _logger;
    private readonly IOptions<EntityScopeOptions>? _scopeOptions;

    /// <summary>
    /// Current request's tenant, resolved lazily from the ambient HttpContext.
    /// Null when there is no HTTP context (startup seeding, login/refresh before auth,
    /// background work) — in that case the global tenant query filter is bypassed.
    /// </summary>
    private Guid? _tenantId
    {
        get
        {
            if (Guid.TryParse(_httpContextAccessor?.HttpContext?.User?.FindFirstValue("tenant_id"), out var tid))
                return tid;
            return null;
        }
    }

    /// <summary>
    /// Current authenticated user ID. Resolved lazily so pool-reused contexts stamp the
    /// correct actor rather than the actor from the context's first-ever request.
    /// </summary>
    private Guid? _actorId
    {
        get
        {
            var user = _httpContextAccessor?.HttpContext?.User;
            if (user is null) return null;
            var sub = user.FindFirstValue(ClaimTypes.NameIdentifier) ?? user.FindFirstValue("sub");
            return Guid.TryParse(sub, out var uid) ? uid : (Guid?)null;
        }
    }

    /// <summary>Company-switcher header: narrows the request's company view. Can only narrow, never widen.</summary>
    public const string CompanySelectionHeader = "X-Company-Id";

    /// <summary>
    /// The request's effective company scope: token claims (v2/legacy) narrowed by the
    /// optional X-Company-Id switcher header. An inaccessible or malformed selection
    /// fails closed. No HTTP context (seeding/background) = trusted group scope.
    /// </summary>
    private EntityScopeContext ResolveRequestScope()
    {
        var ctx = _httpContextAccessor?.HttpContext;
        var user = ctx?.User;
        if (user is null) return EntityScopeContext.GroupLevel;
        var scope = EntityScopeContext.FromClaims(user, _scopeOptions?.Value.StrictMode ?? false);
        var header = ctx!.Request.Headers[CompanySelectionHeader].FirstOrDefault();
        return scope.NarrowTo(header);
    }

    // Company scope — derived lazily from JWT claims + switcher header.
    // True when no HTTP context (admin/background work) or user has group-level access.
    private bool _isGroupScope => ResolveRequestScope().IsGroupLevel;

    // Explicit company IDs the current user may access. Empty when _isGroupScope=true.
    private List<Guid> _companyScopeIds => ResolveRequestScope().AccessibleCompanyIds.ToList();

    public ZayraDbContext(
        DbContextOptions<ZayraDbContext> options,
        IHttpContextAccessor? httpContextAccessor = null,
        ILogger<ZayraDbContext>? logger = null,
        IOptions<EntityScopeOptions>? scopeOptions = null)
        : base(options)
    {
        _logger = logger;
        _httpContextAccessor = httpContextAccessor;
        _scopeOptions = scopeOptions;
    }

    /// <summary>
    /// Intercepts every write to auto-populate timestamp and actor audit fields.
    /// This is the single authoritative place where CreatedAtUtc / UpdatedAtUtc are set —
    /// services should never set them manually (doing so is harmless but redundant).
    /// </summary>
    public override async Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        var now = DateTime.UtcNow;
        EnforceAuditLogAppendOnly();
        foreach (var entry in ChangeTracker.Entries())
        {
            if (entry.State == EntityState.Added)
            {
                TryStamp(entry, "CreatedAtUtc", now, skipIfSet: true);
                TryStamp(entry, "UpdatedAtUtc", now);
                if (_actorId.HasValue) TryStamp(entry, "CreatedBy", _actorId.Value, skipIfSet: true);
                if (_actorId.HasValue) TryStamp(entry, "UpdatedBy", _actorId.Value);
            }
            else if (entry.State == EntityState.Modified)
            {
                TryStamp(entry, "UpdatedAtUtc", now);
                if (_actorId.HasValue) TryStamp(entry, "UpdatedBy", _actorId.Value);
            }

            if (entry.State is EntityState.Added or EntityState.Modified)
                NormalizeDateTimeKinds(entry);
        }
        await EnforceCompanyScopeOnWritesAsync(cancellationToken);
        return await base.SaveChangesAsync(cancellationToken);
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        EnforceAuditLogAppendOnly();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    private void EnforceAuditLogAppendOnly()
    {
        var auditMutation = ChangeTracker.Entries<AuditLog>()
            .FirstOrDefault(e => e.State is EntityState.Modified or EntityState.Deleted);
        if (auditMutation is not null)
            throw new InvalidOperationException("audit_log_append_only_violation: central audit log rows cannot be modified or deleted.");
    }

    /// <summary>
    /// Phase 2 write-side enforcement for ICompanyScopedOperational entities. The read
    /// filter alone cannot stop a request from WRITING rows into another company, and a
    /// forgotten CompanyId would create rows invisible to scoped users (poison default).
    ///
    /// Rules per entry (user contexts only — system contexts with no tenant claim, i.e.
    /// seeders / backfill / background workers, are trusted group-scope by design):
    ///   Added, CompanyId set    → actor must have access to that company (fail closed).
    ///   Added, CompanyId null   → server-side resolution, in order:
    ///       (a) owning employee's company (EmployeeId/EmployeeIntId linkage — the "safe
    ///           route": ESS and manager flows never carry a company explicitly);
    ///       (b) tenant has exactly one active company → that company (SingleCompany);
    ///       (c) actor's scope covers exactly one of the tenant's companies → that one;
    ///       (d) otherwise FAIL CLOSED — Group tenants require explicit company context.
    ///       (0 active companies → no company dimension yet; null passes through.)
    ///   Modified                → reassigning or nulling-out a non-null CompanyId is
    ///       blocked (explicit transfer workflows come later and must opt in); assigning
    ///       a previously-null CompanyId is allowed (repair) but access-validated.
    /// </summary>
    private async Task EnforceCompanyScopeOnWritesAsync(CancellationToken ct)
    {
        var entries = ChangeTracker.Entries()
            .Where(e => e.Entity is ICompanyScopedOperational && e.State is EntityState.Added or EntityState.Modified)
            .ToList();
        if (entries.Count == 0) return;

        var user = _httpContextAccessor?.HttpContext?.User;
        var tenantClaim = _tenantId;
        if (user is null || tenantClaim is null) return; // system context — trusted by design

        // Same narrowed scope as reads: with the switcher on Company A, writes resolve
        // and validate against Company A — the switcher context IS the write context.
        var scope = ResolveRequestScope();

        Dictionary<Guid, List<Guid>>? companiesByTenant = null;
        Dictionary<int, Guid?>? employeeCompanyCache = null;

        foreach (var entry in entries)
        {
            var scoped = (ICompanyScopedOperational)entry.Entity;

            if (entry.State == EntityState.Modified)
            {
                var companyProp = entry.Property(nameof(ICompanyScoped.CompanyId));
                var original = (Guid?)companyProp.OriginalValue;
                var current = scoped.CompanyId;
                if (original is not null && current != original)
                    throw new InvalidOperationException(
                        $"company_reassignment_blocked: {entry.Metadata.ClrType.Name} rows cannot change company " +
                        "(or lose their company) outside an explicit transfer workflow.");
                if (current is not null && !scope.CanAccessCompany(current))
                    throw new UnauthorizedAccessException(
                        $"company_scope_denied: no access to the company that owns this {entry.Metadata.ClrType.Name}.");
                continue;
            }

            // Added
            if (scoped.CompanyId is Guid explicitCompany)
            {
                if (!scope.CanAccessCompany(explicitCompany))
                    throw new UnauthorizedAccessException(
                        $"company_scope_denied: cannot create {entry.Metadata.ClrType.Name} in a company outside your access.");
                continue;
            }

            var entityTenant = ResolveEntryTenantId(entry) ?? tenantClaim.Value;

            // (a) Safe route: follow the owning employee's company.
            var linkedEmployeeId = ResolveLinkedEmployeeId(entry);
            if (linkedEmployeeId is int empId and > 0)
            {
                employeeCompanyCache ??= new Dictionary<int, Guid?>();
                if (!employeeCompanyCache.TryGetValue(empId, out var empCompany))
                {
                    // IgnoreQueryFilters is intentional: server-side stamping must see the
                    // employee row even when the ACTOR's scope wouldn't (the deny decision
                    // is made below); TenantId predicate keeps this tenant-contained.
                    empCompany = await Employees.IgnoreQueryFilters().AsNoTracking()
                        .Where(e => e.TenantId == entityTenant && e.Id == empId)
                        .Select(e => e.CompanyId)
                        .FirstOrDefaultAsync(ct);
                    employeeCompanyCache[empId] = empCompany;
                }
                if (empCompany is Guid fromEmployee)
                {
                    if (!scope.CanAccessCompany(fromEmployee))
                        throw new UnauthorizedAccessException(
                            $"company_scope_denied: cannot create {entry.Metadata.ClrType.Name} for an employee outside your company access.");
                    scoped.CompanyId = fromEmployee;
                    continue;
                }
            }

            // (b)/(c)/(d): tenant-level resolution.
            companiesByTenant ??= new Dictionary<Guid, List<Guid>>();
            if (!companiesByTenant.TryGetValue(entityTenant, out var activeCompanies))
            {
                // IgnoreQueryFilters is intentional: default-company resolution needs the
                // tenant's full active company list regardless of actor scope; explicit
                // TenantId predicate keeps this tenant-contained.
                activeCompanies = await Companies.IgnoreQueryFilters().AsNoTracking()
                    .Where(c => c.TenantId == entityTenant && c.IsActive && !c.IsDeleted)
                    .OrderBy(c => c.CreatedAtUtc)
                    .Select(c => c.Id)
                    .ToListAsync(ct);
                companiesByTenant[entityTenant] = activeCompanies;
            }

            if (activeCompanies.Count == 0) continue; // no company dimension yet
            if (activeCompanies.Count == 1)
            {
                var only = activeCompanies[0];
                if (!scope.CanAccessCompany(only))
                    throw new UnauthorizedAccessException(
                        $"company_scope_denied: cannot create {entry.Metadata.ClrType.Name} in this tenant's company.");
                scoped.CompanyId = only;
                continue;
            }

            if (!scope.IsGroupLevel)
            {
                var accessible = scope.AccessibleCompanyIds.Intersect(activeCompanies).ToList();
                if (accessible.Count == 1)
                {
                    scoped.CompanyId = accessible[0];
                    continue;
                }
            }

            throw new InvalidOperationException(
                $"company_scope_required: {entry.Metadata.ClrType.Name} in a multi-company tenant needs an " +
                "explicit CompanyId (or an employee linkage to derive it from).");
        }
    }

    private static Guid? ResolveEntryTenantId(Microsoft.EntityFrameworkCore.ChangeTracking.EntityEntry entry) =>
        entry.Entity switch
        {
            ITenantOwned owned => owned.TenantId,
            INullableTenantOwned nullable => nullable.TenantId,
            _ => null,
        };

    /// <summary>Owning-employee linkage: int Employee.Id via "EmployeeId" (int/int?) or "EmployeeIntId".</summary>
    private static int? ResolveLinkedEmployeeId(Microsoft.EntityFrameworkCore.ChangeTracking.EntityEntry entry)
    {
        var type = entry.Entity.GetType();
        var direct = type.GetProperty("EmployeeId");
        if (direct?.PropertyType == typeof(int)) return (int)direct.GetValue(entry.Entity)!;
        if (direct?.PropertyType == typeof(int?) && direct.GetValue(entry.Entity) is int nullableInt) return nullableInt;
        var intLink = type.GetProperty("EmployeeIntId");
        if (intLink?.PropertyType == typeof(int?) && intLink.GetValue(entry.Entity) is int viaIntLink) return viaIntLink;
        return null;
    }

    /// <summary>
    /// Npgsql maps DateTime columns to 'timestamp with time zone' and throws when a value's
    /// Kind is Unspecified. JSON model binding produces Unspecified for payloads like
    /// "2024-01-01", and DateOnly.ToDateTime() does too — a recurring 500 class. Normalize
    /// every DateTime being written: Unspecified is taken as UTC wall-clock; Local converts.
    /// </summary>
    private static void NormalizeDateTimeKinds(Microsoft.EntityFrameworkCore.ChangeTracking.EntityEntry entry)
    {
        foreach (var prop in entry.Properties)
        {
            var clr = prop.Metadata.ClrType;
            if (clr != typeof(DateTime) && clr != typeof(DateTime?)) continue;
            if (prop.CurrentValue is not DateTime dt) continue;
            if (dt.Kind == DateTimeKind.Unspecified) prop.CurrentValue = DateTime.SpecifyKind(dt, DateTimeKind.Utc);
            else if (dt.Kind == DateTimeKind.Local) prop.CurrentValue = dt.ToUniversalTime();
        }
    }

    private static void TryStamp(Microsoft.EntityFrameworkCore.ChangeTracking.EntityEntry entry, string prop, object value, bool skipIfSet = false)
    {
        if (entry.Metadata.FindProperty(prop) is null) return;
        if (skipIfSet)
        {
            var cur = entry.Property(prop).CurrentValue;
            if (cur is DateTime dt && dt != default) return;
            if (cur is Guid g && g != Guid.Empty) return;
        }
        entry.Property(prop).CurrentValue = value;
    }

    public DbSet<Employee> Employees => Set<Employee>();
    public DbSet<Position> Positions => Set<Position>();
    public DbSet<AttendanceRecord> AttendanceRecords => Set<AttendanceRecord>();
    public DbSet<AttendanceDevice> AttendanceDevices => Set<AttendanceDevice>();
    public DbSet<AttendanceDeviceConnector> AttendanceDeviceConnectors => Set<AttendanceDeviceConnector>();
    public DbSet<AttendanceDeviceSyncLog> AttendanceDeviceSyncLogs => Set<AttendanceDeviceSyncLog>();
    public DbSet<AttendanceRawEvent> AttendanceRawEvents => Set<AttendanceRawEvent>();
    public DbSet<AttendanceDailyRecord> AttendanceDailyRecords => Set<AttendanceDailyRecord>();
    public DbSet<AttendancePolicy> AttendancePolicies => Set<AttendancePolicy>();
    public DbSet<AttendanceRule> AttendanceRules => Set<AttendanceRule>();
    public DbSet<AttendanceLocation> AttendanceLocations => Set<AttendanceLocation>();
    public DbSet<AttendanceGeofence> AttendanceGeofences => Set<AttendanceGeofence>();
    public DbSet<AttendanceRegularizationRequest> AttendanceRegularizationRequests => Set<AttendanceRegularizationRequest>();
    public DbSet<AttendanceCorrectionApproval> AttendanceCorrectionApprovals => Set<AttendanceCorrectionApproval>();
    public DbSet<AttendancePayrollImpact> AttendancePayrollImpacts => Set<AttendancePayrollImpact>();
    public DbSet<AttendanceImportBatch> AttendanceImportBatches => Set<AttendanceImportBatch>();
    public DbSet<AttendanceImportError> AttendanceImportErrors => Set<AttendanceImportError>();
    public DbSet<AttendanceException> AttendanceExceptions => Set<AttendanceException>();
    public DbSet<AttendanceLockPeriod> AttendanceLockPeriods => Set<AttendanceLockPeriod>();
    public DbSet<AttendanceAIInsight> AttendanceAIInsights => Set<AttendanceAIInsight>();
    public DbSet<AttendanceAuditLog> AttendanceAuditLogs => Set<AttendanceAuditLog>();
    // ── Leave Management ──────────────────────────────────────────────────────────
    public DbSet<LeaveType> LeaveTypes => Set<LeaveType>();
    public DbSet<LeavePolicy> LeavePolicies => Set<LeavePolicy>();
    public DbSet<LeavePolicyEligibility> LeavePolicyEligibilities => Set<LeavePolicyEligibility>();
    public DbSet<LeaveAccrualRule> LeaveAccrualRules => Set<LeaveAccrualRule>();
    public DbSet<EmployeeLeaveBalance> EmployeeLeaveBalances => Set<EmployeeLeaveBalance>();
    public DbSet<LeaveBalanceTransaction> LeaveBalanceTransactions => Set<LeaveBalanceTransaction>();
    public DbSet<LeaveRequest> LeaveRequests => Set<LeaveRequest>();
    public DbSet<LeaveRequestDate> LeaveRequestDates => Set<LeaveRequestDate>();
    public DbSet<LeaveApproval> LeaveApprovals => Set<LeaveApproval>();
    public DbSet<LeaveAttachment> LeaveAttachments => Set<LeaveAttachment>();
    public DbSet<LeaveCancellationRequest> LeaveCancellationRequests => Set<LeaveCancellationRequest>();
    public DbSet<LeaveModificationRequest> LeaveModificationRequests => Set<LeaveModificationRequest>();
    public DbSet<PublicHolidayCalendar> PublicHolidayCalendars => Set<PublicHolidayCalendar>();
    public DbSet<PublicHoliday> PublicHolidays => Set<PublicHoliday>();
    public DbSet<LeaveBlackoutDate> LeaveBlackoutDates => Set<LeaveBlackoutDate>();
    public DbSet<LeaveEncashmentRequest> LeaveEncashmentRequests => Set<LeaveEncashmentRequest>();
    public DbSet<CompOffCredit> CompOffCredits => Set<CompOffCredit>();
    public DbSet<CompOffUsage> CompOffUsages => Set<CompOffUsage>();
    public DbSet<AbsenceRecord> AbsenceRecords => Set<AbsenceRecord>();
    public DbSet<AbsenceRegularizationRequest> AbsenceRegularizationRequests => Set<AbsenceRegularizationRequest>();
    public DbSet<LeaveDelegation> LeaveDelegations => Set<LeaveDelegation>();
    public DbSet<LeavePayrollImpact> LeavePayrollImpacts => Set<LeavePayrollImpact>();
    public DbSet<LeaveAuditLog> LeaveAuditLogs => Set<LeaveAuditLog>();
    public DbSet<LeaveAIInsight> LeaveAIInsights => Set<LeaveAIInsight>();
    public DbSet<OvertimePolicy> OvertimePolicies => Set<OvertimePolicy>();
    public DbSet<OvertimeType> OvertimeTypes => Set<OvertimeType>();
    public DbSet<OvertimeMultiplier> OvertimeMultipliers => Set<OvertimeMultiplier>();
    public DbSet<OvertimeRule> OvertimeRules => Set<OvertimeRule>();
    public DbSet<OvertimeRequest> OvertimeRequests => Set<OvertimeRequest>();
    public DbSet<OvertimeApproval> OvertimeApprovals => Set<OvertimeApproval>();
    public DbSet<OvertimeCalculation> OvertimeCalculations => Set<OvertimeCalculation>();
    public DbSet<OvertimePayrollImpact> OvertimePayrollImpacts => Set<OvertimePayrollImpact>();
    public DbSet<OvertimeAdjustment> OvertimeAdjustments => Set<OvertimeAdjustment>();
    public DbSet<OvertimeBudget> OvertimeBudgets => Set<OvertimeBudget>();
    public DbSet<OvertimeCompOffConversion> OvertimeCompOffConversions => Set<OvertimeCompOffConversion>();
    public DbSet<OvertimeAuditLog> OvertimeAuditLogs => Set<OvertimeAuditLog>();
    public DbSet<PayrollRun> PayrollRuns => Set<PayrollRun>();
    public DbSet<PayrollSlip> PayrollSlips => Set<PayrollSlip>();
    public DbSet<SalaryStructure> SalaryStructures => Set<SalaryStructure>();
    public DbSet<SalaryComponent> SalaryComponents => Set<SalaryComponent>();
    public DbSet<EmployeeSalaryStructure> EmployeeSalaryStructures => Set<EmployeeSalaryStructure>();
    public DbSet<PayrollGroup> PayrollGroups => Set<PayrollGroup>();
    public DbSet<PayrollCycle> PayrollCycles => Set<PayrollCycle>();
    public DbSet<PayrollRunEmployee> PayrollRunEmployees => Set<PayrollRunEmployee>();
    public DbSet<PayrollEarning> PayrollEarnings => Set<PayrollEarning>();
    public DbSet<PayrollDeduction> PayrollDeductions => Set<PayrollDeduction>();
    public DbSet<BenefitPlan> BenefitPlans => Set<BenefitPlan>();
    public DbSet<BenefitEligibilityRule> BenefitEligibilityRules => Set<BenefitEligibilityRule>();
    public DbSet<BenefitEnrollment> BenefitEnrollments => Set<BenefitEnrollment>();
    public DbSet<BenefitContribution> BenefitContributions => Set<BenefitContribution>();
    public DbSet<BenefitPayrollDeductionLink> BenefitPayrollDeductionLinks => Set<BenefitPayrollDeductionLink>();
    public DbSet<PayrollAllowance> PayrollAllowances => Set<PayrollAllowance>();
    public DbSet<PayrollAdjustment> PayrollAdjustments => Set<PayrollAdjustment>();
    public DbSet<PayrollApproval> PayrollApprovals => Set<PayrollApproval>();
    public DbSet<PayrollValidationResult> PayrollValidationResults => Set<PayrollValidationResult>();
    public DbSet<PayrollException> PayrollExceptions => Set<PayrollException>();
    public DbSet<Payslip> Payslips => Set<Payslip>();
    public DbSet<PayslipComponent> PayslipComponents => Set<PayslipComponent>();
    public DbSet<PayslipTemplate> PayslipTemplates => Set<PayslipTemplate>();
    public DbSet<PayrollPaymentBatch> PayrollPaymentBatches => Set<PayrollPaymentBatch>();
    public DbSet<PayrollPaymentRecord> PayrollPaymentRecords => Set<PayrollPaymentRecord>();
    public DbSet<PayrollOpeningBalance> PayrollOpeningBalances => Set<PayrollOpeningBalance>();
    public DbSet<BankTransferFile> BankTransferFiles => Set<BankTransferFile>();
    public DbSet<WPSFileBatch> WPSFileBatches => Set<WPSFileBatch>();
    public DbSet<SIFFileRecord> SIFFileRecords => Set<SIFFileRecord>();
    public DbSet<EOSBCalculation> EOSBCalculations => Set<EOSBCalculation>();
    public DbSet<PayrollAuditLog> PayrollAuditLogs => Set<PayrollAuditLog>();
    public DbSet<ShiftDefinition> ShiftDefinitions => Set<ShiftDefinition>();
    public DbSet<ShiftAssignment> ShiftAssignments => Set<ShiftAssignment>();
    public DbSet<ShiftPolicy> ShiftPolicies => Set<ShiftPolicy>();
    public DbSet<EmployeeOffboarding> EmployeeOffboardings => Set<EmployeeOffboarding>();
    public DbSet<ManpowerRequisition> ManpowerRequisitions => Set<ManpowerRequisition>();
    public DbSet<JobOpening> JobOpenings => Set<JobOpening>();
    public DbSet<Candidate> Candidates => Set<Candidate>();
    public DbSet<JobApplication> JobApplications => Set<JobApplication>();
    public DbSet<ApplicationEvent> ApplicationEvents => Set<ApplicationEvent>();
    public DbSet<InterviewSchedule> InterviewSchedules => Set<InterviewSchedule>();
    public DbSet<OfferLetter> OfferLetters => Set<OfferLetter>();
    // ── Performance & Appraisals ─────────────────────────────────────────────
    public DbSet<PerformanceCycle> PerformanceCycles => Set<PerformanceCycle>();
    public DbSet<PerformanceScorecardTemplate> PerformanceScorecardTemplates => Set<PerformanceScorecardTemplate>();
    public DbSet<PerformanceRatingScale> PerformanceRatingScales => Set<PerformanceRatingScale>();
    public DbSet<PerformanceRatingOption> PerformanceRatingOptions => Set<PerformanceRatingOption>();
    public DbSet<PerformanceCycleEmployee> PerformanceCycleEmployees => Set<PerformanceCycleEmployee>();
    public DbSet<Competency> Competencies => Set<Competency>();
    public DbSet<RoleCompetency> RoleCompetencies => Set<RoleCompetency>();
    public DbSet<EmployeeGoal> EmployeeGoals => Set<EmployeeGoal>();
    public DbSet<GoalProgressUpdate> GoalProgressUpdates => Set<GoalProgressUpdate>();
    public DbSet<AppraisalReview> AppraisalReviews => Set<AppraisalReview>();
    public DbSet<AppraisalScoreBreakdown> AppraisalScoreBreakdowns => Set<AppraisalScoreBreakdown>();
    public DbSet<AppraisalCompetencyRating> AppraisalCompetencyRatings => Set<AppraisalCompetencyRating>();
    public DbSet<Feedback360> Feedback360 => Set<Feedback360>();
    public DbSet<AppraisalCalibration> AppraisalCalibrations => Set<AppraisalCalibration>();
    public DbSet<AppraisalAppeal> AppraisalAppeals => Set<AppraisalAppeal>();
    public DbSet<IncrementRecommendation> IncrementRecommendations => Set<IncrementRecommendation>();
    public DbSet<PromotionRecommendation> PromotionRecommendations => Set<PromotionRecommendation>();
    public DbSet<BonusRecommendation> BonusRecommendations => Set<BonusRecommendation>();
    public DbSet<PerformanceImprovementPlan> PerformanceImprovementPlans => Set<PerformanceImprovementPlan>();
    public DbSet<PIPCheckIn> PIPCheckIns => Set<PIPCheckIn>();
    public DbSet<ProbationReview> ProbationReviews => Set<ProbationReview>();
    public DbSet<ContinuousFeedback> ContinuousFeedback => Set<ContinuousFeedback>();
    public DbSet<PerformanceAuditLog> PerformanceAuditLogs => Set<PerformanceAuditLog>();
    public DbSet<EmployeeDraft> EmployeeDrafts => Set<EmployeeDraft>();
    public DbSet<EmployeeDocument> EmployeeDocuments => Set<EmployeeDocument>();
    public DbSet<EmployeeDocumentVersion> EmployeeDocumentVersions => Set<EmployeeDocumentVersion>();
    public DbSet<EmployeeHistory> EmployeeHistories => Set<EmployeeHistory>();
    public DbSet<EmployeeStatusHistory> EmployeeStatusHistories => Set<EmployeeStatusHistory>();
    public DbSet<EmployeeChangeRequest> EmployeeChangeRequests => Set<EmployeeChangeRequest>();
    public DbSet<EmployeeTransferRequest> EmployeeTransferRequests => Set<EmployeeTransferRequest>();
    public DbSet<EmployeePayrollProfile> EmployeePayrollProfiles => Set<EmployeePayrollProfile>();
    public DbSet<EmployeeComplianceRecord> EmployeeComplianceRecords => Set<EmployeeComplianceRecord>();
    public DbSet<EmployeeDependent> EmployeeDependents => Set<EmployeeDependent>();
    public DbSet<EmployeeUserAccount> EmployeeUserAccounts => Set<EmployeeUserAccount>();
    public DbSet<UserPermissionOverride> UserPermissionOverrides => Set<UserPermissionOverride>();
    public DbSet<ApprovalDelegation> ApprovalDelegations => Set<ApprovalDelegation>();
    public DbSet<ApprovalAuthority> ApprovalAuthorities => Set<ApprovalAuthority>();
    public DbSet<ESSDashboardPreference> ESSDashboardPreferences => Set<ESSDashboardPreference>();
    public DbSet<EmployeeProfileChangeRequest> EmployeeProfileChangeRequests => Set<EmployeeProfileChangeRequest>();
    public DbSet<EmployeeDocumentRequest> EmployeeDocumentRequests => Set<EmployeeDocumentRequest>();
    public DbSet<HRRequest> HRRequests => Set<HRRequest>();
    public DbSet<HRRequestCategory> HRRequestCategories => Set<HRRequestCategory>();
    public DbSet<HRRequestComment> HRRequestComments => Set<HRRequestComment>();
    public DbSet<HRRequestAttachment> HRRequestAttachments => Set<HRRequestAttachment>();
    public DbSet<HRRequestSLA> HRRequestSLAs => Set<HRRequestSLA>();
    public DbSet<EmployeePolicyAcknowledgement> EmployeePolicyAcknowledgements => Set<EmployeePolicyAcknowledgement>();
    public DbSet<EmployeeAnnouncement> EmployeeAnnouncements => Set<EmployeeAnnouncement>();
    public DbSet<EmployeeNotification> EmployeeNotifications => Set<EmployeeNotification>();
    public DbSet<EmployeeNotificationPreference> EmployeeNotificationPreferences => Set<EmployeeNotificationPreference>();
    public DbSet<EmployeePayslipAccessLog> EmployeePayslipAccessLogs => Set<EmployeePayslipAccessLog>();
    public DbSet<EmployeeSelfServiceAuditLog> EmployeeSelfServiceAuditLogs => Set<EmployeeSelfServiceAuditLog>();
    public DbSet<EmployeeAIQueryLog> EmployeeAIQueryLogs => Set<EmployeeAIQueryLog>();
    public DbSet<EmployeeActionItem> EmployeeActionItems => Set<EmployeeActionItem>();
    public DbSet<EmployeeSentimentPulse> EmployeeSentimentPulses => Set<EmployeeSentimentPulse>();
    public DbSet<EmployeeMobileDevice> EmployeeMobileDevices => Set<EmployeeMobileDevice>();
    // ── SaaS Platform ──────────────────────────────────────────────────────────
    public DbSet<TenantSubscription> TenantSubscriptions => Set<TenantSubscription>();
    public DbSet<TenantFeatureFlag> TenantFeatureFlags => Set<TenantFeatureFlag>();
    public DbSet<TenantLocalizationSetting> TenantLocalizationSettings => Set<TenantLocalizationSetting>();
    public DbSet<TenantBranding> TenantBrandings => Set<TenantBranding>();
    public DbSet<CountryPayrollRule> CountryPayrollRules => Set<CountryPayrollRule>();
    public DbSet<StatutoryRule> StatutoryRules => Set<StatutoryRule>();
    public DbSet<CompanyTaxPolicy> CompanyTaxPolicies => Set<CompanyTaxPolicy>();
    public DbSet<CompanyComplianceProfile> CompanyComplianceProfiles => Set<CompanyComplianceProfile>();
    public DbSet<TenantFieldHelpText> TenantFieldHelpTexts => Set<TenantFieldHelpText>();
    public DbSet<PlatformSupportSession> PlatformSupportSessions => Set<PlatformSupportSession>();
    public DbSet<PlatformUser> PlatformUsers => Set<PlatformUser>();
    public DbSet<MfaChallengeToken> MfaChallengeTokens => Set<MfaChallengeToken>();
    public DbSet<PlatformAnnouncement> PlatformAnnouncements => Set<PlatformAnnouncement>();
    public DbSet<PlatformLead> PlatformLeads => Set<PlatformLead>();
    public DbSet<PlatformComplianceControl> PlatformComplianceControls => Set<PlatformComplianceControl>();
    public DbSet<PlatformSecurityIncident> PlatformSecurityIncidents => Set<PlatformSecurityIncident>();
    public DbSet<PlatformConfigEntry> PlatformConfigEntries => Set<PlatformConfigEntry>();
    // ── AI Intelligence ────────────────────────────────────────────────────────
    public DbSet<AIModelConfig> AIModelConfigs => Set<AIModelConfig>();
    public DbSet<AIInsight> AIInsights => Set<AIInsight>();
    public DbSet<AIRecommendation> AIRecommendations => Set<AIRecommendation>();
    public DbSet<AIHRQueryLog> AIHRQueryLogs => Set<AIHRQueryLog>();
    public DbSet<AIHRQueryCache> AIHRQueryCaches => Set<AIHRQueryCache>();
    public DbSet<TenantAiUsage> TenantAiUsages => Set<TenantAiUsage>();
    public DbSet<TenantInvoice> TenantInvoices => Set<TenantInvoice>();
    public DbSet<TenantInvoiceLine> TenantInvoiceLines => Set<TenantInvoiceLine>();
    public DbSet<TenantPayment> TenantPayments => Set<TenantPayment>();
    public DbSet<LoginActivity> LoginActivities => Set<LoginActivity>();
    public DbSet<PricingConfig> PricingConfigs => Set<PricingConfig>();
    public DbSet<PricingModuleConfig> PricingModuleConfigs => Set<PricingModuleConfig>();
    public DbSet<PricingQuote> PricingQuotes => Set<PricingQuote>();
    public DbSet<ResumeParseResult> ResumeParseResults => Set<ResumeParseResult>();
    public DbSet<CandidateAIScore> CandidateAIScores => Set<CandidateAIScore>();
    public DbSet<PayrollAIValidationResult> PayrollAIValidationResults => Set<PayrollAIValidationResult>();
    public DbSet<EmployeeRiskScore> EmployeeRiskScores => Set<EmployeeRiskScore>();
    public DbSet<EmployeeChurnPrediction> EmployeeChurnPredictions => Set<EmployeeChurnPrediction>();
    public DbSet<BurnoutRiskSignal> BurnoutRiskSignals => Set<BurnoutRiskSignal>();
    // ── Recruitment Extended ───────────────────────────────────────────────────
    public DbSet<WorkforcePlan> WorkforcePlans => Set<WorkforcePlan>();
    public DbSet<CandidateDocument> CandidateDocuments => Set<CandidateDocument>();
    public DbSet<InterviewFeedback> InterviewFeedbacks => Set<InterviewFeedback>();
    public DbSet<AssessmentTemplate> AssessmentTemplates => Set<AssessmentTemplate>();
    public DbSet<AssessmentQuestion> AssessmentQuestions => Set<AssessmentQuestion>();
    public DbSet<CandidateAssessment> CandidateAssessments => Set<CandidateAssessment>();
    public DbSet<OfferApproval> OfferApprovals => Set<OfferApproval>();
    public DbSet<OnboardingChecklist> OnboardingChecklists => Set<OnboardingChecklist>();
    public DbSet<OnboardingChecklistTemplateTask> OnboardingChecklistTemplateTasks => Set<OnboardingChecklistTemplateTask>();
    public DbSet<OnboardingTask> OnboardingTasks => Set<OnboardingTask>();
    public DbSet<RecruitmentAuditLog> RecruitmentAuditLogs => Set<RecruitmentAuditLog>();
    // ── Compliance Module ──────────────────────────────────────────────────────
    public DbSet<DocType> DocTypes => Set<DocType>();
    public DbSet<ContractTemplate> ContractTemplates => Set<ContractTemplate>();
    public DbSet<EmployeeContract> EmployeeContracts => Set<EmployeeContract>();
    public DbSet<ComplianceRequirement> ComplianceRequirements => Set<ComplianceRequirement>();
    public DbSet<ComplianceRenewal> ComplianceRenewals => Set<ComplianceRenewal>();
    public DbSet<ComplianceReminder> ComplianceReminders => Set<ComplianceReminder>();
    public DbSet<VisaRecord> VisaRecords => Set<VisaRecord>();
    public DbSet<PassportRecord> PassportRecords => Set<PassportRecord>();
    public DbSet<WorkPermitRecord> WorkPermitRecords => Set<WorkPermitRecord>();
    public DbSet<ComplianceAuditLog> ComplianceAuditLogs => Set<ComplianceAuditLog>();
    public DbSet<ComplianceAIInsight> ComplianceAIInsights => Set<ComplianceAIInsight>();
    public DbSet<Notification> Notifications => Set<Notification>();
    public DbSet<Company> Companies => Set<Company>();
    public DbSet<Branch> Branches => Set<Branch>();
    public DbSet<Department> Departments => Set<Department>();
    public DbSet<Designation> Designations => Set<Designation>();
    // ── Establishment matrix (per-department, per-level staffing budgets) ──────
    public DbSet<StaffingLevel> StaffingLevels => Set<StaffingLevel>();
    public DbSet<DepartmentStaffingBudget> DepartmentStaffingBudgets => Set<DepartmentStaffingBudget>();
    public DbSet<Grade> Grades => Set<Grade>();
    public DbSet<GradePayScaleComponent> GradePayScaleComponents => Set<GradePayScaleComponent>();
    public DbSet<CostCenter> CostCenters => Set<CostCenter>();
    public DbSet<EmployeeIdRule> EmployeeIdRules => Set<EmployeeIdRule>();
    public DbSet<ApprovalWorkflow> ApprovalWorkflows => Set<ApprovalWorkflow>();
    public DbSet<ApprovalWorkflowStep> ApprovalWorkflowSteps => Set<ApprovalWorkflowStep>();
    public DbSet<ApprovalRequest> ApprovalRequests => Set<ApprovalRequest>();
    public DbSet<ApprovalDecision> ApprovalDecisions => Set<ApprovalDecision>();
    public DbSet<ReportingLine> ReportingLines => Set<ReportingLine>();
    public DbSet<ApprovalPolicy> ApprovalPolicies => Set<ApprovalPolicy>();
    public DbSet<ApprovalPolicyStep> ApprovalPolicySteps => Set<ApprovalPolicyStep>();
    public DbSet<Tenant> Tenants => Set<Tenant>();
    public DbSet<User> Users => Set<User>();
    public DbSet<Role> Roles => Set<Role>();
    public DbSet<Permission> Permissions => Set<Permission>();
    public DbSet<UserRole> UserRoles => Set<UserRole>();
    public DbSet<RolePermission> RolePermissions => Set<RolePermission>();
    public DbSet<MigrationImportBatch> MigrationImportBatches => Set<MigrationImportBatch>();
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();
    public DbSet<PasswordResetToken> PasswordResetTokens => Set<PasswordResetToken>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
    // ── Policy RAG Documents ───────────────────────────────────────────────────
    public DbSet<PolicyDocument> PolicyDocuments { get; set; }
    public DbSet<DocumentChunk> DocumentChunks { get; set; }
    // ── Setup & Admin ──────────────────────────────────────────────────────────
    public DbSet<MasterDataType> MasterDataTypes => Set<MasterDataType>();
    public DbSet<MasterDataValue> MasterDataValues => Set<MasterDataValue>();
    public DbSet<NumberingRule> NumberingRules => Set<NumberingRule>();
    public DbSet<SystemSetting> SystemSettings => Set<SystemSetting>();
    public DbSet<GCCComplianceSetting> GCCComplianceSettings => Set<GCCComplianceSetting>();
    public DbSet<Location> Locations => Set<Location>();
    public DbSet<FiscalYear> FiscalYears => Set<FiscalYear>();
    public DbSet<NotificationTemplate> NotificationTemplates => Set<NotificationTemplate>();
    public DbSet<AdminAuditLog> AdminAuditLogs => Set<AdminAuditLog>();
    // ── GOSI ───────────────────────────────────────────────────────────────────
    public DbSet<GosiContributionRule> GosiContributionRules => Set<GosiContributionRule>();
    // ── Qiwa Integration ───────────────────────────────────────────────────────
    public DbSet<QiwaTenantConnection> QiwaTenantConnections => Set<QiwaTenantConnection>();
    public DbSet<QiwaSyncLog> QiwaSyncLogs => Set<QiwaSyncLog>();
    public DbSet<QiwaApiCredential> QiwaApiCredentials => Set<QiwaApiCredential>();
    // ── Loans, Advances & Bonuses ──────────────────────────────────────────────
    public DbSet<LoanType> LoanTypes => Set<LoanType>();
    public DbSet<LoanPolicy> LoanPolicies => Set<LoanPolicy>();
    public DbSet<EmployeeLoan> EmployeeLoans => Set<EmployeeLoan>();
    public DbSet<LoanApproval> LoanApprovals => Set<LoanApproval>();
    public DbSet<LoanInstallment> LoanInstallments => Set<LoanInstallment>();
    public DbSet<LoanSettlement> LoanSettlements => Set<LoanSettlement>();
    public DbSet<LoanAuditLog> LoanAuditLogs => Set<LoanAuditLog>();
    public DbSet<AdvancePolicy> AdvancePolicies => Set<AdvancePolicy>();
    public DbSet<SalaryAdvance> SalaryAdvances => Set<SalaryAdvance>();
    public DbSet<AdvanceApproval> AdvanceApprovals => Set<AdvanceApproval>();
    public DbSet<AdvanceInstallment> AdvanceInstallments => Set<AdvanceInstallment>();
    public DbSet<AdvanceAuditLog> AdvanceAuditLogs => Set<AdvanceAuditLog>();
    public DbSet<BonusType> BonusTypes => Set<BonusType>();
    public DbSet<BonusBatch> BonusBatches => Set<BonusBatch>();
    public DbSet<EmployeeBonus> EmployeeBonuses => Set<EmployeeBonus>();
    public DbSet<BonusApproval> BonusApprovals => Set<BonusApproval>();
    public DbSet<BonusAuditLog> BonusAuditLogs => Set<BonusAuditLog>();
    public DbSet<FinanceGlEntry> FinanceGlEntries => Set<FinanceGlEntry>();
    public DbSet<GlAccount> GlAccounts => Set<GlAccount>();
    public DbSet<GlAccountMapping> GlAccountMappings => Set<GlAccountMapping>();
    public DbSet<GlDriver> GlDrivers => Set<GlDriver>();
    public DbSet<PayComponent> PayComponents => Set<PayComponent>();
    public DbSet<CompanyRatePolicy> CompanyRatePolicies => Set<CompanyRatePolicy>();
    public DbSet<CompanyStatutoryOverride> CompanyStatutoryOverrides => Set<CompanyStatutoryOverride>();
    public DbSet<ClientRateDefinition> ClientRateDefinitions => Set<ClientRateDefinition>();
    // ── Reports & Analytics ────────────────────────────────────────────────────
    public DbSet<SavedReport> SavedReports => Set<SavedReport>();
    public DbSet<ReportSchedule> ReportSchedules => Set<ReportSchedule>();
    public DbSet<ReportExecutionLog> ReportExecutionLogs => Set<ReportExecutionLog>();
    // ── Identity & Security ────────────────────────────────────────────────────
    public DbSet<SecuritySetting> SecuritySettings => Set<SecuritySetting>();
    public DbSet<TenantIdentityProviderSetting> TenantIdentityProviderSettings => Set<TenantIdentityProviderSetting>();
    public DbSet<EnterpriseIdentityProvisioningEvent> EnterpriseIdentityProvisioningEvents => Set<EnterpriseIdentityProvisioningEvent>();
    public DbSet<PermissionGrantorRecord> PermissionGrantorRecords => Set<PermissionGrantorRecord>();
    public DbSet<UserEntityAccess> UserEntityAccesses => Set<UserEntityAccess>();
    // ── HR Workflow Configuration ──────────────────────────────────────────────
    public DbSet<TenantHrConfig> TenantHrConfigs => Set<TenantHrConfig>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        ApplySnakeCaseColumns(modelBuilder);


        modelBuilder.Entity<Employee>(entity =>
        {
            entity.ToTable("employees");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.EmployeeCode).HasMaxLength(50).IsRequired();
            entity.Property(x => x.FullName).HasMaxLength(150).IsRequired();
            entity.Property(x => x.Salary).HasPrecision(12, 2);
            entity.Property(x => x.ProfileCompletenessScore).HasPrecision(5, 2);
            entity.Property(x => x.PrivacyStatus).HasMaxLength(80);
            entity.Property(x => x.ReadinessState).HasMaxLength(20).HasDefaultValue("Blocked");
            entity.HasIndex(x => new { x.TenantId, x.EmployeeCode }).IsUnique();
            entity.HasIndex(x => new { x.TenantId, x.Status });
            // "Needs info" worklist filter — employees whose readiness is Blocked.
            entity.HasIndex(x => new { x.TenantId, x.ReadinessState });
            entity.HasIndex(x => new { x.TenantId, x.Department });
            entity.HasIndex(x => new { x.TenantId, x.IsDeleted });
            entity.HasIndex(x => new { x.TenantId, x.PositionId });
            // Partial occupancy index for the establishment guard/matrix. The status literals
            // mirror EstablishmentOccupancy.OccupyingStatuses (EmployeeStatuses constants — the
            // employment-lifecycle vocabulary, not tenant business data). HasFilter is ignored by
            // the InMemory provider, so the test suite is unaffected.
            entity.HasIndex(x => new { x.TenantId, x.DepartmentId, x.DesignationId })
                  .HasDatabaseName("ix_employees_occupancy")
                  .HasFilter("NOT is_deleted AND status IN ('Active','Offboarded','Suspended')");
        });

        modelBuilder.Entity<Position>(entity =>
        {
            entity.ToTable("positions");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Code).HasMaxLength(60).IsRequired();
            entity.Property(x => x.Title).HasMaxLength(180).IsRequired();
            entity.Property(x => x.Fte).HasPrecision(6, 2);
            entity.Property(x => x.BudgetedMonthlyCost).HasPrecision(14, 2);
            entity.Property(x => x.Currency).HasMaxLength(8);
            entity.Property(x => x.Status).HasMaxLength(20);
            entity.HasIndex(x => new { x.TenantId, x.Code }).IsUnique();
            entity.HasIndex(x => new { x.TenantId, x.CompanyId, x.Status });
        });

        modelBuilder.Entity<QiwaApiCredential>(entity =>
        {
            entity.ToTable("qiwa_api_credentials");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.ClientId).HasMaxLength(200);
            entity.Property(x => x.EncryptedClientSecret).HasMaxLength(2000);
            entity.Property(x => x.Environment).HasMaxLength(20);
            entity.Property(x => x.CachedAccessToken).HasMaxLength(4000);
            entity.HasIndex(x => x.TenantId).IsUnique();
        });

        modelBuilder.Entity<QiwaSyncLog>(entity =>
        {
            entity.Property(x => x.DeadLetterReason).HasMaxLength(500);
            entity.Property(x => x.Status).HasMaxLength(20);
            entity.HasIndex(x => new { x.TenantId, x.Status });
        });

        modelBuilder.Entity<GosiContributionRule>(entity =>
        {
            entity.ToTable("gosi_contribution_rules");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Rate).HasPrecision(7, 4);
            entity.Property(x => x.MinContributoryWage).HasPrecision(12, 2);
            entity.Property(x => x.MaxContributoryWage).HasPrecision(12, 2);
            entity.Property(x => x.Classification).HasMaxLength(20);
            entity.Property(x => x.Branch).HasMaxLength(30);
            entity.Property(x => x.Payer).HasMaxLength(20);
            entity.Property(x => x.CountryCode).HasMaxLength(5);
            entity.Property(x => x.SourceReference).HasMaxLength(200);
            entity.Property(x => x.Notes).HasMaxLength(500);
            // Lookup index: find active rules for a classification + date range
            entity.HasIndex(x => new { x.TenantId, x.Classification, x.Branch, x.Payer, x.IsActive });
        });


        modelBuilder.Entity<EmployeeDraft>(entity =>
        {
            entity.ToTable("employee_drafts");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Salary).HasPrecision(12, 2);
            entity.Property(x => x.ProfileCompletenessScore).HasPrecision(5, 2);
            entity.HasIndex(x => new { x.TenantId, x.Status });
        });

        modelBuilder.Entity<EmployeeDocument>(entity =>
        {
            entity.ToTable("employee_documents");
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.TenantId, x.EmployeeId });
            entity.HasIndex(x => new { x.TenantId, x.DraftId });
            entity.HasIndex(x => new { x.TenantId, x.EmployeeId, x.DocumentType, x.IsDeleted });
        });

        modelBuilder.Entity<EmployeeDocumentVersion>(entity =>
        {
            entity.ToTable("employee_document_versions");
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.TenantId, x.EmployeeDocumentId, x.VersionNumber }).IsUnique();
        });

        modelBuilder.Entity<EmployeeHistory>(entity =>
        {
            entity.ToTable("employee_histories");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.SnapshotJson).HasColumnType("json");
            entity.HasIndex(x => new { x.TenantId, x.EmployeeId, x.CreatedAtUtc });
        });

        modelBuilder.Entity<EmployeeStatusHistory>(entity =>
        {
            entity.ToTable("employee_status_histories");
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.TenantId, x.EmployeeId, x.CreatedAtUtc });
        });

        modelBuilder.Entity<EmployeeChangeRequest>(entity =>
        {
            entity.ToTable("employee_change_requests");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Status).HasMaxLength(80).IsRequired();
            entity.Property(x => x.SensitiveFields).HasMaxLength(1000);
            entity.Property(x => x.ProposedChangesJson).HasColumnType("json");
            entity.Property(x => x.RejectionReason).HasMaxLength(1000);
            entity.HasIndex(x => x.ApprovalRequestId);
            entity.HasIndex(x => new { x.TenantId, x.EmployeeId, x.Status });
        });

        modelBuilder.Entity<EmployeeTransferRequest>(entity =>
        {
            entity.ToTable("employee_transfer_requests");
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.TenantId, x.EmployeeId, x.Status });
        });

        modelBuilder.Entity<EmployeePayrollProfile>(entity =>
        {
            entity.ToTable("employee_payroll_profiles");
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.TenantId, x.EmployeeId }).IsUnique();
        });

        modelBuilder.Entity<EmployeeComplianceRecord>(entity =>
        {
            entity.ToTable("employee_compliance_records");
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.TenantId, x.EmployeeId, x.CountryCode, x.FieldKey }).IsUnique();
            entity.HasIndex(x => new { x.TenantId, x.ExpiryDate });
        });

        modelBuilder.Entity<EmployeeDependent>(entity =>
        {
            entity.ToTable("employee_dependents");
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.TenantId, x.EmployeeId });
        });

        modelBuilder.Entity<EmployeeUserAccount>(entity =>
        {
            entity.ToTable("employee_user_accounts");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.AccessMode).HasMaxLength(40).IsRequired();
            entity.Property(x => x.Status).HasMaxLength(40).IsRequired();
            entity.Property(x => x.InvitationTokenHash).HasMaxLength(128);
            entity.HasIndex(x => new { x.TenantId, x.EmployeeId, x.IsPrimary });
            entity.HasIndex(x => new { x.TenantId, x.UserId }).IsUnique();
            entity.HasIndex(x => new { x.TenantId, x.InvitationTokenHash });
            entity.HasOne(x => x.User).WithMany(x => x.EmployeeUserAccounts).HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<UserPermissionOverride>(entity =>
        {
            entity.ToTable("user_permission_overrides");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.PermissionKey).HasMaxLength(120).IsRequired();
            entity.Property(x => x.Effect).HasMaxLength(20).IsRequired();
            entity.HasIndex(x => new { x.TenantId, x.UserId, x.PermissionKey }).IsUnique();
            entity.HasOne(x => x.User).WithMany(x => x.PermissionOverrides).HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ApprovalDelegation>(entity =>
        {
            entity.ToTable("approval_delegations");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Scope).HasMaxLength(120).IsRequired();
            entity.Property(x => x.Status).HasMaxLength(40).IsRequired();
            entity.HasIndex(x => new { x.TenantId, x.FromEmployeeId, x.ToEmployeeId, x.Status });
            entity.HasIndex(x => new { x.TenantId, x.StartDate, x.EndDate });
        });

        modelBuilder.Entity<ApprovalAuthority>(entity =>
        {
            entity.ToTable("approval_authorities");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.AmountLimit).HasPrecision(14, 2);
            entity.HasIndex(x => new { x.TenantId, x.EmployeeId, x.AuthorityScope, x.IsActive });
        });

        modelBuilder.Entity<ESSDashboardPreference>(entity =>
        {
            entity.ToTable("ess_dashboard_preferences");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.WidgetLayoutJson).HasColumnType("json");
            entity.HasIndex(x => new { x.TenantId, x.EmployeeId }).IsUnique();
        });

        modelBuilder.Entity<EmployeeProfileChangeRequest>(entity =>
        {
            entity.ToTable("employee_profile_change_requests");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.RequestedChangesJson).HasColumnType("json");
            entity.HasIndex(x => new { x.TenantId, x.EmployeeId, x.Status });
        });

        modelBuilder.Entity<EmployeeDocumentRequest>(entity =>
        {
            entity.ToTable("employee_document_requests");
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.TenantId, x.EmployeeId, x.Status });
        });

        modelBuilder.Entity<HRRequest>(entity =>
        {
            entity.ToTable("hr_requests");
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.TenantId, x.EmployeeId, x.Status });
            entity.HasIndex(x => new { x.TenantId, x.DueAtUtc });
        });

        modelBuilder.Entity<HRRequestCategory>(entity =>
        {
            entity.ToTable("hr_request_categories");
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.TenantId, x.Code }).IsUnique();
        });

        modelBuilder.Entity<HRRequestComment>(entity =>
        {
            entity.ToTable("hr_request_comments");
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.TenantId, x.HRRequestId, x.CreatedAtUtc });
        });

        modelBuilder.Entity<HRRequestAttachment>(entity =>
        {
            entity.ToTable("hr_request_attachments");
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.TenantId, x.HRRequestId });
        });

        modelBuilder.Entity<HRRequestSLA>(entity =>
        {
            entity.ToTable("hr_request_slas");
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.TenantId, x.CategoryId, x.Priority });
        });

        modelBuilder.Entity<EmployeePolicyAcknowledgement>(entity =>
        {
            entity.ToTable("employee_policy_acknowledgements");
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.TenantId, x.EmployeeId, x.PolicyId }).IsUnique();
        });

        modelBuilder.Entity<EmployeeAnnouncement>(entity =>
        {
            entity.ToTable("employee_announcements");
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.TenantId, x.IsActive, x.PublishedAtUtc });
        });

        modelBuilder.Entity<EmployeeNotification>(entity =>
        {
            entity.ToTable("employee_notifications");
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.TenantId, x.EmployeeId, x.IsRead });
            // Notification inbox pagination: ORDER BY created_at_utc DESC with tenant+employee filter
            entity.HasIndex(x => new { x.TenantId, x.EmployeeId, x.CreatedAtUtc });
        });

        modelBuilder.Entity<EmployeeNotificationPreference>(entity =>
        {
            entity.ToTable("employee_notification_preferences");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.QuietHoursJson).HasColumnType("json");
            entity.HasIndex(x => new { x.TenantId, x.EmployeeId }).IsUnique();
        });

        modelBuilder.Entity<EmployeePayslipAccessLog>(entity =>
        {
            entity.ToTable("employee_payslip_access_logs");
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.TenantId, x.EmployeeId, x.PayslipId });
        });

        modelBuilder.Entity<EmployeeSelfServiceAuditLog>(entity =>
        {
            entity.ToTable("employee_self_service_audit_logs");
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.TenantId, x.EmployeeId, x.CreatedAtUtc });
        });

        modelBuilder.Entity<EmployeeAIQueryLog>(entity =>
        {
            entity.ToTable("employee_ai_query_logs");
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.TenantId, x.EmployeeId, x.CreatedAtUtc });
        });

        modelBuilder.Entity<EmployeeActionItem>(entity =>
        {
            entity.ToTable("employee_action_items");
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.TenantId, x.EmployeeId, x.Status });
        });

        modelBuilder.Entity<EmployeeSentimentPulse>(entity =>
        {
            entity.ToTable("employee_sentiment_pulses");
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.TenantId, x.EmployeeId, x.CreatedAtUtc });
        });

        modelBuilder.Entity<EmployeeMobileDevice>(entity =>
        {
            entity.ToTable("employee_mobile_devices");
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.TenantId, x.EmployeeId, x.DeviceIdentifier }).IsUnique();
        });


        modelBuilder.Entity<Notification>(entity =>
        {
            entity.ToTable("notifications");
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.TenantId, x.UserId, x.Status, x.CreatedAtUtc });
        });

        modelBuilder.Entity<Company>(entity =>
        {
            entity.ToTable("companies");
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.TenantId, x.LegalNameEn });
            entity.HasIndex(x => new { x.TenantId, x.RegistrationNumber });
            entity.HasIndex(x => new { x.TenantId, x.IsDeleted });
            entity.Property(x => x.Jurisdiction).HasMaxLength(30);
        });

        modelBuilder.Entity<Branch>(entity =>
        {
            entity.ToTable("branches");
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.TenantId, x.Code }).IsUnique();
            entity.HasIndex(x => new { x.TenantId, x.CompanyId });
            entity.HasIndex(x => new { x.TenantId, x.IsDeleted });
        });

        modelBuilder.Entity<Department>(entity =>
        {
            entity.ToTable("departments");
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.TenantId, x.Code }).IsUnique();
            entity.HasIndex(x => new { x.TenantId, x.BranchId });
            entity.HasIndex(x => new { x.TenantId, x.ParentDepartmentId });
            entity.HasIndex(x => new { x.TenantId, x.IsDeleted });
        });

        modelBuilder.Entity<Designation>(entity =>
        {
            entity.ToTable("designations");
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.TenantId, x.Code }).IsUnique();
            entity.HasIndex(x => new { x.TenantId, x.DepartmentId });
            entity.HasIndex(x => new { x.TenantId, x.IsDeleted });
            entity.HasIndex(x => new { x.TenantId, x.StaffingLevelId });
        });

        modelBuilder.Entity<StaffingLevel>(entity =>
        {
            entity.ToTable("staffing_levels");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Code).HasMaxLength(60).IsRequired();
            entity.Property(x => x.NameEn).HasMaxLength(150).IsRequired();
            entity.Property(x => x.NameAr).HasMaxLength(150);
            entity.HasIndex(x => new { x.TenantId, x.Code }).IsUnique();
            entity.HasIndex(x => new { x.TenantId, x.IsDeleted });
        });

        modelBuilder.Entity<DepartmentStaffingBudget>(entity =>
        {
            entity.ToTable("department_staffing_budgets");
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.TenantId, x.DepartmentId, x.StaffingLevelId }).IsUnique();
            entity.HasIndex(x => new { x.TenantId, x.DepartmentId });
        });

        modelBuilder.Entity<Grade>(entity =>
        {
            entity.ToTable("grades");
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.TenantId, x.Code }).IsUnique();
            entity.HasIndex(x => new { x.TenantId, x.IsDeleted });
            entity.Property(x => x.MinSalary).HasPrecision(14, 2);
            entity.Property(x => x.MidSalary).HasPrecision(14, 2);
            entity.Property(x => x.MaxSalary).HasPrecision(14, 2);
            entity.Property(x => x.Currency).HasMaxLength(8);
        });

        modelBuilder.Entity<GradePayScaleComponent>(entity =>
        {
            entity.ToTable("grade_pay_scale_components");
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.TenantId, x.GradeId });
            entity.Property(x => x.Amount).HasPrecision(14, 2);
            entity.Property(x => x.Percentage).HasPrecision(7, 4);
        });

        modelBuilder.Entity<GlAccount>(entity =>
        {
            entity.ToTable("gl_accounts");
            entity.HasKey(x => x.Id);
            // Non-unique helper index; the real UNIQUE (tenant, company, code) NULLS NOT DISTINCT
            // is created via raw SQL in RekeyGlUniquesForCompanyScope (Npgsql 8 can't express it).
            entity.HasIndex(x => new { x.TenantId, x.CompanyId, x.Code });
        });

        modelBuilder.Entity<GlAccountMapping>(entity =>
        {
            entity.ToTable("gl_account_mappings");
            entity.HasKey(x => x.Id);
            // Non-unique helper index; real UNIQUE (tenant, company, driver_key) NULLS NOT DISTINCT via raw SQL.
            entity.HasIndex(x => new { x.TenantId, x.CompanyId, x.DriverKey });
            entity.HasOne<GlAccount>().WithMany()
                  .HasForeignKey(x => x.AccountId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<GlDriver>(entity =>
        {
            entity.ToTable("gl_drivers");
            entity.HasKey(x => x.Id);
            // Non-unique helper index; real UNIQUE (tenant, company, key) NULLS NOT DISTINCT via raw SQL.
            entity.HasIndex(x => new { x.TenantId, x.CompanyId, x.Key });
        });

        modelBuilder.Entity<PayComponent>(entity =>
        {
            entity.ToTable("pay_components");
            entity.HasKey(x => x.Id);
            // Non-unique helper index; the real UNIQUE (tenant, company, code, component_type) NULLS NOT
            // DISTINCT is created via raw SQL in the AddPayComponentDefinitions migration (Npgsql 8 cannot
            // express NULLS NOT DISTINCT fluently — same idiom as gl_drivers). component_type is part of the
            // key because "ADJ" is legitimately both an earning family and a deduction family.
            entity.HasIndex(x => new { x.TenantId, x.CompanyId, x.Code, x.ComponentType });
            entity.HasIndex(x => new { x.TenantId, x.CompanyId, x.IsActive, x.IsDeleted });
            entity.Property(x => x.Code).HasMaxLength(64);
            entity.Property(x => x.NameEn).HasMaxLength(200);
            entity.Property(x => x.NameAr).HasMaxLength(200);
            entity.Property(x => x.ComponentType).HasMaxLength(40);
            entity.Property(x => x.CalcMethod).HasMaxLength(40);
            entity.Property(x => x.StructureField).HasMaxLength(64);
            entity.Property(x => x.ProviderKey).HasMaxLength(64);
            entity.Property(x => x.GlDriverKey).HasMaxLength(80);
            entity.Property(x => x.FormulaExpression).HasMaxLength(1024);
            entity.Property(x => x.Value).HasPrecision(18, 4);
        });

        modelBuilder.Entity<CompanyRatePolicy>(entity =>
        {
            entity.ToTable("company_rate_policies");
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.TenantId, x.CompanyId, x.RateKey, x.Status, x.EffectiveFrom });
            entity.Property(x => x.RateValue).HasMaxLength(128);
        });

        modelBuilder.Entity<CompanyStatutoryOverride>(entity =>
        {
            entity.ToTable("company_statutory_overrides");
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.TenantId, x.CompanyId, x.CountryCode, x.Jurisdiction, x.RuleKey, x.Status, x.EffectiveFrom });
        });

        modelBuilder.Entity<ClientRateDefinition>(entity =>
        {
            entity.ToTable("client_rate_definitions");
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.TenantId, x.RateKey }).IsUnique();
        });

        modelBuilder.Entity<CostCenter>(entity =>
        {
            entity.ToTable("cost_centers");
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.TenantId, x.Code }).IsUnique();
            entity.HasIndex(x => new { x.TenantId, x.CompanyId });
            entity.HasIndex(x => new { x.TenantId, x.IsDeleted });
        });

        modelBuilder.Entity<EmployeeIdRule>(entity =>
        {
            entity.ToTable("employee_id_rules");
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.TenantId, x.CompanyId, x.IsActive });
        });

        modelBuilder.Entity<ApprovalWorkflow>(entity =>
        {
            entity.ToTable("approval_workflows");
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.TenantId, x.Code }).IsUnique();
            entity.HasMany(x => x.Steps).WithOne().HasForeignKey(x => x.WorkflowId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ApprovalWorkflowStep>(entity =>
        {
            entity.ToTable("approval_workflow_steps");
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.TenantId, x.WorkflowId, x.StepOrder }).IsUnique();
        });

        modelBuilder.Entity<ApprovalRequest>(entity =>
        {
            entity.ToTable("approval_requests");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.EntityName).HasMaxLength(120);
            entity.Property(x => x.EntityId).HasMaxLength(80);
            entity.Property(x => x.Status).HasMaxLength(40);
            entity.Property(x => x.Title).HasMaxLength(240);
            entity.Property(x => x.CurrentApproverName).HasMaxLength(180);
            entity.Property(x => x.CurrentApproverRole).HasMaxLength(80);
            entity.Property(x => x.CurrentApproverType).HasMaxLength(60);
            entity.Property(x => x.CurrentQueue).HasMaxLength(180);
            entity.Property(x => x.EscalatedToRole).HasMaxLength(80);
            entity.Property(x => x.Priority).HasMaxLength(40);
            entity.HasIndex(x => new { x.TenantId, x.EntityName, x.EntityId, x.Status });
            entity.HasIndex(x => new { x.TenantId, x.Status, x.CurrentApproverUserId });
            entity.HasIndex(x => new { x.TenantId, x.Status, x.CurrentApproverEmployeeId });
            entity.HasIndex(x => new { x.TenantId, x.Status, x.DueAtUtc });
            entity.HasIndex(x => new { x.TenantId, x.CompanyId, x.Status });
            entity.HasMany(x => x.Decisions).WithOne().HasForeignKey(x => x.ApprovalRequestId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ApprovalDecision>(entity =>
        {
            entity.ToTable("approval_decisions");
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.TenantId, x.ApprovalRequestId, x.StepOrder });
        });

        modelBuilder.Entity<ReportingLine>(entity =>
        {
            entity.ToTable("reporting_lines");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.RelationshipType).HasMaxLength(40).IsRequired();
            entity.HasIndex(x => new { x.TenantId, x.EmployeeId, x.RelationshipType, x.IsActive });
            entity.HasIndex(x => new { x.TenantId, x.ManagerEmployeeId, x.IsActive });
        });

        modelBuilder.Entity<ApprovalPolicy>(entity =>
        {
            entity.ToTable("approval_policies");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.WorkflowType).HasMaxLength(60).IsRequired();
            entity.Property(x => x.Name).HasMaxLength(180).IsRequired();
            entity.HasIndex(x => new { x.TenantId, x.WorkflowType, x.IsDefault, x.IsActive });
            entity.HasIndex(x => new { x.TenantId, x.WorkflowType, x.DepartmentId, x.GradeId }).IsUnique();
            entity.HasMany(x => x.Steps).WithOne().HasForeignKey(x => x.PolicyId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ApprovalPolicyStep>(entity =>
        {
            entity.ToTable("approval_policy_steps");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.ApproverType).HasMaxLength(60).IsRequired();
            entity.Property(x => x.StepName).HasMaxLength(180).IsRequired();
            entity.HasIndex(x => new { x.TenantId, x.PolicyId, x.StepOrder }).IsUnique();
        });

        modelBuilder.Entity<AttendanceRecord>(entity =>
        {
            entity.ToTable("attendance_records");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Status).HasMaxLength(30).IsRequired();
            entity.Property(x => x.OvertimeHours).HasPrecision(5, 2);
            entity.HasIndex(x => new { x.TenantId, x.EmployeeId, x.WorkDate }).IsUnique();
            entity.HasIndex(x => new { x.TenantId, x.WorkDate, x.Status });
        });

        modelBuilder.Entity<AttendanceDevice>(entity =>
        {
            entity.ToTable("attendance_devices");
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.TenantId, x.SerialNumber }).IsUnique();
            entity.HasIndex(x => new { x.TenantId, x.Vendor, x.DeviceType, x.IsActive });
            entity.HasIndex(x => new { x.TenantId, x.IsDeleted });
        });
        modelBuilder.Entity<AttendanceDeviceConnector>(entity =>
        {
            entity.ToTable("attendance_device_connectors");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.SettingsJson).HasColumnType("json");
            entity.HasIndex(x => new { x.TenantId, x.ConnectorCode }).IsUnique();
        });
        modelBuilder.Entity<AttendanceDeviceSyncLog>(entity =>
        {
            entity.ToTable("attendance_device_sync_logs");
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.TenantId, x.DeviceId, x.StartedAtUtc });
        });
        modelBuilder.Entity<AttendanceRawEvent>(entity =>
        {
            entity.ToTable("attendance_raw_events");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Latitude).HasPrecision(10, 7);
            entity.Property(x => x.Longitude).HasPrecision(10, 7);
            entity.Property(x => x.ConfidenceScore).HasPrecision(5, 2);
            entity.Property(x => x.RawPayloadJson).HasColumnType("json");
            entity.HasIndex(x => new { x.TenantId, x.EmployeeId, x.PunchTimestampUtc, x.PunchDirection, x.DeviceId }).IsUnique();
            entity.HasIndex(x => new { x.TenantId, x.IsProcessed, x.PunchTimestampUtc });
            entity.HasIndex(x => new { x.TenantId, x.SyncBatchReference });
        });
        modelBuilder.Entity<AttendanceDailyRecord>(entity =>
        {
            entity.ToTable("attendance_daily_records");
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.TenantId, x.EmployeeId, x.WorkDate }).IsUnique();
            entity.HasIndex(x => new { x.TenantId, x.WorkDate, x.Status });
            entity.HasIndex(x => new { x.TenantId, x.MissingPunch });
        });
        modelBuilder.Entity<AttendancePolicy>(entity =>
        {
            entity.ToTable("attendance_policies");
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.TenantId, x.Code }).IsUnique();
            entity.HasIndex(x => new { x.TenantId, x.IsActive });
        });
        modelBuilder.Entity<AttendanceRule>(entity =>
        {
            entity.ToTable("attendance_rules");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.RuleValueJson).HasColumnType("json");
            entity.HasIndex(x => new { x.TenantId, x.AttendancePolicyId, x.RuleType });
        });
        modelBuilder.Entity<AttendanceLocation>(entity =>
        {
            entity.ToTable("attendance_locations");
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.TenantId, x.BranchId });
        });
        modelBuilder.Entity<AttendanceGeofence>(entity =>
        {
            entity.ToTable("attendance_geofences");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Latitude).HasPrecision(10, 7);
            entity.Property(x => x.Longitude).HasPrecision(10, 7);
            entity.HasIndex(x => new { x.TenantId, x.AttendanceLocationId });
        });
        modelBuilder.Entity<AttendanceRegularizationRequest>(entity =>
        {
            entity.ToTable("attendance_regularization_requests");
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.TenantId, x.EmployeeId, x.WorkDate });
            entity.HasIndex(x => new { x.TenantId, x.Status });
        });
        modelBuilder.Entity<AttendanceCorrectionApproval>(entity =>
        {
            entity.ToTable("attendance_correction_approvals");
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.TenantId, x.RegularizationRequestId, x.ApprovalLevel });
        });
        modelBuilder.Entity<AttendancePayrollImpact>(entity =>
        {
            entity.ToTable("attendance_payroll_impacts");
            entity.HasKey(x => x.Id);
            entity.Ignore(x => x.Hours);
            entity.HasIndex(x => new { x.TenantId, x.EmployeeId, x.WorkDate });
            entity.HasIndex(x => new { x.TenantId, x.Status });
        });
        modelBuilder.Entity<AttendanceImportBatch>(entity =>
        {
            entity.ToTable("attendance_import_batches");
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.TenantId, x.CreatedAtUtc });
        });
        modelBuilder.Entity<AttendanceImportError>(entity =>
        {
            entity.ToTable("attendance_import_errors");
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.TenantId, x.ImportBatchId });
        });
        modelBuilder.Entity<AttendanceException>(entity =>
        {
            entity.ToTable("attendance_exceptions");
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.TenantId, x.WorkDate, x.ExceptionType, x.IsResolved });
        });
        modelBuilder.Entity<AttendanceLockPeriod>(entity =>
        {
            entity.ToTable("attendance_lock_periods");
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.TenantId, x.PeriodStart, x.PeriodEnd, x.LockType });
        });
        modelBuilder.Entity<AttendanceAIInsight>(entity =>
        {
            entity.ToTable("attendance_ai_insights");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.DataJson).HasColumnType("json");
            entity.HasIndex(x => new { x.TenantId, x.InsightType, x.IsAcknowledged });
        });
        modelBuilder.Entity<AttendanceAuditLog>(entity =>
        {
            entity.ToTable("attendance_audit_logs");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.MetadataJson).HasColumnType("json");
            entity.HasIndex(x => new { x.TenantId, x.EntityName, x.EntityId, x.CreatedAtUtc });
        });

        // ── Leave Management ──────────────────────────────────────────────────────
        modelBuilder.Entity<LeaveType>(entity => {
            entity.ToTable("leave_types");
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.TenantId, x.Code }).IsUnique();
            entity.HasIndex(x => new { x.TenantId, x.IsActive });
        });
        modelBuilder.Entity<LeavePolicy>(entity => {
            entity.ToTable("leave_policies");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.AnnualEntitlementDays).HasPrecision(6,2);
            entity.Property(x => x.CarryForwardMax).HasPrecision(6,2);
            entity.Property(x => x.EncashmentMaxDays).HasPrecision(6,2);
            entity.Property(x => x.MinimumDaysPerRequest).HasPrecision(5,2);
            entity.Property(x => x.MaximumDaysPerRequest).HasPrecision(5,2);
            entity.HasIndex(x => new { x.TenantId, x.LeaveTypeId, x.Status });
        });
        modelBuilder.Entity<LeavePolicyEligibility>(entity => { entity.ToTable("leave_policy_eligibilities"); entity.HasKey(x => x.Id); entity.HasIndex(x => new { x.TenantId, x.LeavePolicyId, x.IsActive }); });
        modelBuilder.Entity<LeaveAccrualRule>(entity => { entity.ToTable("leave_accrual_rules"); entity.HasKey(x => x.Id); entity.Property(x => x.AccrualDays).HasPrecision(6,2); entity.Property(x => x.CarryForwardMaxDays).HasPrecision(6,2); entity.HasIndex(x => new { x.TenantId, x.LeavePolicyId, x.IsActive }); });
        modelBuilder.Entity<EmployeeLeaveBalance>(entity => {
            entity.ToTable("employee_leave_balances");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Entitled).HasPrecision(7,2);
            entity.Property(x => x.Accrued).HasPrecision(7,2);
            entity.Property(x => x.Used).HasPrecision(7,2);
            entity.Property(x => x.Pending).HasPrecision(7,2);
            entity.Property(x => x.CarriedForward).HasPrecision(7,2);
            entity.Property(x => x.Encashed).HasPrecision(7,2);
            entity.Property(x => x.Expired).HasPrecision(7,2);
            entity.Property(x => x.ManualAdjustment).HasPrecision(7,2);
            entity.Ignore(x => x.Available);
            entity.HasIndex(x => new { x.TenantId, x.EmployeeId, x.LeaveTypeId, x.Year }).IsUnique();
        });
        modelBuilder.Entity<LeaveBalanceTransaction>(entity => {
            entity.ToTable("leave_balance_transactions");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Amount).HasPrecision(7,2);
            entity.Property(x => x.BalanceBefore).HasPrecision(7,2);
            entity.Property(x => x.BalanceAfter).HasPrecision(7,2);
            entity.HasIndex(x => new { x.TenantId, x.EmployeeId, x.LeaveTypeId });
        });
        modelBuilder.Entity<LeaveRequest>(entity => {
            entity.ToTable("leave_requests");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.TotalDays).HasPrecision(6,2);
            entity.Property(x => x.HoursRequested).HasPrecision(5,2);
            entity.HasIndex(x => new { x.TenantId, x.Status });
            entity.HasIndex(x => new { x.TenantId, x.EmployeeId, x.StartDate });
        });
        modelBuilder.Entity<LeaveRequestDate>(entity => { entity.ToTable("leave_request_dates"); entity.HasKey(x => x.Id); entity.Property(x => x.DayValue).HasPrecision(4,2); entity.HasIndex(x => new { x.TenantId, x.LeaveRequestId, x.LeaveDate }).IsUnique(); });
        modelBuilder.Entity<LeaveApproval>(entity => { entity.ToTable("leave_approvals"); entity.HasKey(x => x.Id); entity.HasIndex(x => new { x.TenantId, x.LeaveRequestId }); });
        modelBuilder.Entity<LeaveAttachment>(entity => { entity.ToTable("leave_attachments"); entity.HasKey(x => x.Id); entity.HasIndex(x => new { x.TenantId, x.LeaveRequestId }); });
        modelBuilder.Entity<LeaveCancellationRequest>(entity => { entity.ToTable("leave_cancellation_requests"); entity.HasKey(x => x.Id); });
        modelBuilder.Entity<LeaveModificationRequest>(entity => {
            entity.ToTable("leave_modification_requests"); entity.HasKey(x => x.Id);
            entity.Property(x => x.NewTotalDays).HasPrecision(6,2);
        });
        modelBuilder.Entity<PublicHolidayCalendar>(entity => { entity.ToTable("public_holiday_calendars"); entity.HasKey(x => x.Id); entity.HasIndex(x => new { x.TenantId, x.CountryCode, x.CalendarYear }); });
        modelBuilder.Entity<PublicHoliday>(entity => { entity.ToTable("public_holidays"); entity.HasKey(x => x.Id); entity.HasIndex(x => new { x.TenantId, x.CalendarId, x.Date }); });
        modelBuilder.Entity<LeaveBlackoutDate>(entity => { entity.ToTable("leave_blackout_dates"); entity.HasKey(x => x.Id); entity.HasIndex(x => new { x.TenantId, x.StartDate }); });
        modelBuilder.Entity<LeaveEncashmentRequest>(entity => {
            entity.ToTable("leave_encashment_requests"); entity.HasKey(x => x.Id);
            entity.Property(x => x.DaysToEncash).HasPrecision(6,2);
            entity.Property(x => x.AmountPerDay).HasPrecision(10,2);
            entity.Property(x => x.TotalAmount).HasPrecision(12,2);
            entity.HasIndex(x => new { x.TenantId, x.Status });
        });
        modelBuilder.Entity<CompOffCredit>(entity => {
            entity.ToTable("comp_off_credits"); entity.HasKey(x => x.Id);
            entity.Property(x => x.HoursWorked).HasPrecision(5,2);
            entity.Property(x => x.DaysEarned).HasPrecision(5,2);
            entity.HasIndex(x => new { x.TenantId, x.EmployeeId, x.Status });
        });
        modelBuilder.Entity<CompOffUsage>(entity => { entity.ToTable("comp_off_usages"); entity.HasKey(x => x.Id); entity.Property(x => x.DaysUsed).HasPrecision(5,2); });
        modelBuilder.Entity<AbsenceRecord>(entity => { entity.ToTable("absence_records"); entity.HasKey(x => x.Id); entity.HasIndex(x => new { x.TenantId, x.EmployeeId, x.AbsenceDate }); });
        modelBuilder.Entity<AbsenceRegularizationRequest>(entity => { entity.ToTable("absence_regularization_requests"); entity.HasKey(x => x.Id); entity.HasIndex(x => new { x.TenantId, x.Status }); });
        modelBuilder.Entity<LeaveDelegation>(entity => { entity.ToTable("leave_delegations"); entity.HasKey(x => x.Id); entity.HasIndex(x => new { x.TenantId, x.EmployeeId, x.Status }); });
        modelBuilder.Entity<LeavePayrollImpact>(entity => {
            entity.ToTable("leave_payroll_impacts"); entity.HasKey(x => x.Id);
            entity.Property(x => x.Days).HasPrecision(6,2);
            entity.Property(x => x.Amount).HasPrecision(12,2);
            entity.HasIndex(x => new { x.TenantId, x.Status });
        });
        modelBuilder.Entity<LeaveAuditLog>(entity => { entity.ToTable("leave_audit_logs"); entity.HasKey(x => x.Id); entity.HasIndex(x => new { x.TenantId, x.EntityType, x.EntityId }); });
        modelBuilder.Entity<LeaveAIInsight>(entity => { entity.ToTable("leave_ai_insights"); entity.HasKey(x => x.Id); entity.HasIndex(x => new { x.TenantId, x.InsightType, x.IsAcknowledged }); });

        modelBuilder.Entity<OvertimePolicy>(entity => {
            entity.ToTable("overtime_policies"); entity.HasKey(x => x.Id);
            entity.Property(x => x.FixedHourlyRate).HasPrecision(12,2);
            entity.HasIndex(x => new { x.TenantId, x.Code }).IsUnique();
        });
        modelBuilder.Entity<OvertimeType>(entity => { entity.ToTable("overtime_types"); entity.HasKey(x => x.Id); entity.HasIndex(x => new { x.TenantId, x.Code }).IsUnique(); });
        modelBuilder.Entity<OvertimeMultiplier>(entity => { entity.ToTable("overtime_multipliers"); entity.HasKey(x => x.Id); entity.Property(x => x.Multiplier).HasPrecision(6,3); entity.HasIndex(x => new { x.TenantId, x.OvertimePolicyId, x.DayCategory }); });
        modelBuilder.Entity<OvertimeRule>(entity => { entity.ToTable("overtime_rules"); entity.HasKey(x => x.Id); entity.Property(x => x.RuleValueJson).HasColumnType("json"); entity.HasIndex(x => new { x.TenantId, x.OvertimePolicyId, x.RuleType }); });
        modelBuilder.Entity<OvertimeRequest>(entity => { entity.ToTable("overtime_requests"); entity.HasKey(x => x.Id); entity.HasIndex(x => new { x.TenantId, x.EmployeeId, x.WorkDate }); entity.HasIndex(x => new { x.TenantId, x.Status }); });
        modelBuilder.Entity<OvertimeApproval>(entity => { entity.ToTable("overtime_approvals"); entity.HasKey(x => x.Id); entity.HasIndex(x => new { x.TenantId, x.OvertimeRequestId }); });
        modelBuilder.Entity<OvertimeCalculation>(entity => { entity.ToTable("overtime_calculations"); entity.HasKey(x => x.Id); entity.Property(x => x.ApprovedHours).HasPrecision(8,2); entity.Property(x => x.HourlyRate).HasPrecision(12,2); entity.Property(x => x.Multiplier).HasPrecision(6,3); entity.Property(x => x.Amount).HasPrecision(14,2); entity.Property(x => x.CalculationJson).HasColumnType("json"); entity.HasIndex(x => new { x.TenantId, x.OvertimeRequestId }); });
        modelBuilder.Entity<OvertimePayrollImpact>(entity => { entity.ToTable("overtime_payroll_impacts"); entity.HasKey(x => x.Id); entity.Property(x => x.Hours).HasPrecision(8,2); entity.Property(x => x.Amount).HasPrecision(14,2); entity.Property(x => x.ApprovedMultiplier).HasPrecision(4,2).HasDefaultValue(0m); entity.HasIndex(x => new { x.TenantId, x.EmployeeId, x.Status }); });
        modelBuilder.Entity<OvertimeAdjustment>(entity => { entity.ToTable("overtime_adjustments"); entity.HasKey(x => x.Id); entity.Property(x => x.HoursAdjustment).HasPrecision(8,2); entity.Property(x => x.AmountAdjustment).HasPrecision(14,2); });
        modelBuilder.Entity<OvertimeBudget>(entity => { entity.ToTable("overtime_budgets"); entity.HasKey(x => x.Id); entity.Property(x => x.BudgetAmount).HasPrecision(14,2); entity.Property(x => x.ConsumedAmount).HasPrecision(14,2); entity.HasIndex(x => new { x.TenantId, x.Year, x.Month }); });
        modelBuilder.Entity<OvertimeCompOffConversion>(entity => { entity.ToTable("overtime_comp_off_conversions"); entity.HasKey(x => x.Id); entity.Property(x => x.OvertimeHours).HasPrecision(8,2); entity.Property(x => x.CompOffDays).HasPrecision(6,2); });
        modelBuilder.Entity<OvertimeAuditLog>(entity => { entity.ToTable("overtime_audit_logs"); entity.HasKey(x => x.Id); entity.Property(x => x.MetadataJson).HasColumnType("json"); entity.HasIndex(x => new { x.TenantId, x.EntityName, x.EntityId }); });

        modelBuilder.Entity<SalaryStructure>(entity => { entity.ToTable("salary_structures"); entity.HasKey(x => x.Id); entity.Property(x => x.MinGrossSalary).HasPrecision(14,2); entity.Property(x => x.MaxGrossSalary).HasPrecision(14,2); entity.Property(x => x.MinBasicSalary).HasPrecision(14,2); entity.Property(x => x.MaxBasicSalary).HasPrecision(14,2); entity.Property(x => x.EligibleGradeIdsJson).HasColumnType("json"); entity.Property(x => x.EligibleDesignationIdsJson).HasColumnType("json"); entity.HasIndex(x => new { x.TenantId, x.CompanyId, x.Code }).IsUnique(); entity.HasIndex(x => new { x.TenantId, x.CompanyId }); });
        modelBuilder.Entity<SalaryComponent>(entity => { entity.ToTable("salary_components"); entity.HasKey(x => x.Id); entity.Property(x => x.Amount).HasPrecision(14,2); entity.Property(x => x.Percentage).HasPrecision(6,3); entity.HasIndex(x => new { x.TenantId, x.SalaryStructureId, x.Code }); });
        modelBuilder.Entity<EmployeeSalaryStructure>(entity => { entity.ToTable("employee_salary_structures"); entity.HasKey(x => x.Id); entity.Property(x => x.BasicSalary).HasPrecision(14,2); entity.Property(x => x.HousingAllowance).HasPrecision(14,2); entity.Property(x => x.TransportAllowance).HasPrecision(14,2); entity.Property(x => x.FoodAllowance).HasPrecision(14,2); entity.Property(x => x.MobileAllowance).HasPrecision(14,2); entity.Property(x => x.OtherAllowance).HasPrecision(14,2); entity.Property(x => x.FixedDeduction).HasPrecision(14,2); entity.HasIndex(x => new { x.TenantId, x.EmployeeId, x.IsActive }); });
        modelBuilder.Entity<PayrollGroup>(entity => { entity.ToTable("payroll_groups"); entity.HasKey(x => x.Id); entity.HasIndex(x => new { x.TenantId, x.Code }).IsUnique(); });
        modelBuilder.Entity<PayrollCycle>(entity => { entity.ToTable("payroll_cycles"); entity.HasKey(x => x.Id); entity.HasIndex(x => new { x.TenantId, x.Year, x.Month }); });
        modelBuilder.Entity<PayrollRunEmployee>(entity => { entity.ToTable("payroll_run_employees"); entity.HasKey(x => x.Id); entity.Property(x => x.GrossEarnings).HasPrecision(14,2); entity.Property(x => x.TotalDeductions).HasPrecision(14,2); entity.Property(x => x.NetPay).HasPrecision(14,2); entity.HasIndex(x => new { x.TenantId, x.PayrollRunId, x.EmployeeId }).IsUnique(); });
        modelBuilder.Entity<PayrollEarning>(entity => { entity.ToTable("payroll_earnings"); entity.HasKey(x => x.Id); entity.Property(x => x.Amount).HasPrecision(14,2); entity.HasIndex(x => new { x.TenantId, x.PayrollRunId, x.EmployeeId }); });
        modelBuilder.Entity<PayrollDeduction>(entity => { entity.ToTable("payroll_deductions"); entity.HasKey(x => x.Id); entity.Property(x => x.Amount).HasPrecision(14,2); entity.Property(x => x.IsEmployerContribution).HasDefaultValue(false); entity.HasIndex(x => new { x.TenantId, x.PayrollRunId, x.EmployeeId }); });
        modelBuilder.Entity<BenefitPlan>(entity => { entity.ToTable("benefit_plans"); entity.HasKey(x => x.Id); entity.HasIndex(x => new { x.TenantId, x.CompanyId, x.Code }).IsUnique(); entity.HasIndex(x => new { x.TenantId, x.CompanyId, x.IsActive }); });
        modelBuilder.Entity<BenefitEligibilityRule>(entity => { entity.ToTable("benefit_eligibility_rules"); entity.HasKey(x => x.Id); entity.HasIndex(x => new { x.TenantId, x.BenefitPlanId, x.CompanyId, x.GradeId, x.IsActive }); });
        modelBuilder.Entity<BenefitEnrollment>(entity => { entity.ToTable("benefit_enrollments"); entity.HasKey(x => x.Id); entity.HasIndex(x => new { x.TenantId, x.BenefitPlanId, x.EmployeeId, x.Status }); entity.HasIndex(x => new { x.TenantId, x.EmployeeId, x.EffectiveFrom }); });
        modelBuilder.Entity<BenefitContribution>(entity => { entity.ToTable("benefit_contributions"); entity.HasKey(x => x.Id); entity.Property(x => x.EmployeeAmount).HasPrecision(14,2); entity.Property(x => x.EmployerAmount).HasPrecision(14,2); entity.HasIndex(x => new { x.TenantId, x.BenefitEnrollmentId, x.IsActive }); entity.HasIndex(x => new { x.TenantId, x.EmployeeId, x.EffectiveFrom }); });
        modelBuilder.Entity<BenefitPayrollDeductionLink>(entity => { entity.ToTable("benefit_payroll_deduction_links"); entity.HasKey(x => x.Id); entity.Property(x => x.LinkedAmount).HasPrecision(14,2); entity.HasIndex(x => new { x.TenantId, x.BenefitEnrollmentId, x.PayrollRunId }); entity.HasIndex(x => new { x.TenantId, x.PayrollDeductionId }).IsUnique(); });
        modelBuilder.Entity<PayrollAllowance>(entity => { entity.ToTable("payroll_allowances"); entity.HasKey(x => x.Id); entity.Property(x => x.Amount).HasPrecision(14,2); });
        modelBuilder.Entity<PayrollAdjustment>(entity => { entity.ToTable("payroll_adjustments"); entity.HasKey(x => x.Id); entity.Property(x => x.Amount).HasPrecision(14,2); entity.HasIndex(x => new { x.TenantId, x.PayrollRunId, x.EmployeeId }); });
        modelBuilder.Entity<PayrollApproval>(entity => { entity.ToTable("payroll_approvals"); entity.HasKey(x => x.Id); entity.HasIndex(x => new { x.TenantId, x.PayrollRunId }); });
        modelBuilder.Entity<PayrollValidationResult>(entity => { entity.ToTable("payroll_validation_results"); entity.HasKey(x => x.Id); entity.HasIndex(x => new { x.TenantId, x.PayrollRunId, x.Severity }); });
        modelBuilder.Entity<PayrollException>(entity => { entity.ToTable("payroll_exceptions"); entity.HasKey(x => x.Id); entity.HasIndex(x => new { x.TenantId, x.PayrollRunId, x.Status }); });
        modelBuilder.Entity<Payslip>(entity => { entity.ToTable("payslips"); entity.HasKey(x => x.Id); entity.HasIndex(x => new { x.TenantId, x.PayrollRunId, x.EmployeeId }).IsUnique(); });
        modelBuilder.Entity<PayslipTemplate>(entity =>
        {
            entity.ToTable("payslip_templates");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Name).HasMaxLength(200);
            entity.Property(x => x.Status).HasMaxLength(20);
            entity.HasIndex(x => new { x.TenantId, x.IsDefault });
            entity.HasIndex(x => new { x.TenantId, x.Name, x.Version });
        });
        modelBuilder.Entity<PayslipComponent>(entity => { entity.ToTable("payslip_components"); entity.HasKey(x => x.Id); entity.Property(x => x.Amount).HasPrecision(14,2); entity.HasIndex(x => new { x.TenantId, x.PayslipId }); });
        modelBuilder.Entity<PayrollPaymentBatch>(entity =>
        {
            entity.ToTable("payroll_payment_batches");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.TotalAmount).HasPrecision(14,2);
            entity.Property(x => x.WpsStatus).HasMaxLength(40);
            entity.Property(x => x.WpsSubmissionReference).HasMaxLength(120);
            entity.Property(x => x.WpsRejectionReason).HasMaxLength(1000);
            entity.HasIndex(x => new { x.TenantId, x.PayrollRunId });
        });
        modelBuilder.Entity<PayrollPaymentRecord>(entity => { entity.ToTable("payroll_payment_records"); entity.HasKey(x => x.Id); entity.Property(x => x.Amount).HasPrecision(14,2); entity.HasIndex(x => new { x.TenantId, x.PaymentBatchId, x.EmployeeId }); });
        modelBuilder.Entity<PayrollOpeningBalance>(entity =>
        {
            entity.ToTable("payroll_opening_balances");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Amount).HasPrecision(14, 2);
            entity.HasIndex(x => new { x.TenantId, x.EmployeeId, x.Year, x.BalanceType, x.ComponentCode }).IsUnique();
            entity.HasIndex(x => new { x.TenantId, x.CompanyId, x.Year });
        });
        modelBuilder.Entity<BankTransferFile>(entity => { entity.ToTable("bank_transfer_files"); entity.HasKey(x => x.Id); entity.HasIndex(x => new { x.TenantId, x.PaymentBatchId }); });
        modelBuilder.Entity<WPSFileBatch>(entity =>
        {
            entity.ToTable("wps_file_batches");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.FilingStatus).HasMaxLength(40);
            entity.Property(x => x.SubmissionReference).HasMaxLength(120);
            entity.Property(x => x.RejectionReason).HasMaxLength(1000);
            entity.HasIndex(x => new { x.TenantId, x.PaymentBatchId });
            entity.HasIndex(x => new { x.TenantId, x.FilingStatus });
        });
        modelBuilder.Entity<SIFFileRecord>(entity => {
            entity.ToTable("sif_file_records"); entity.HasKey(x => x.Id);
            entity.Property(x => x.WPSFileBatchId).HasColumnName("wps_file_batch_id");
            entity.Property(x => x.NetPay).HasPrecision(14,2);
            entity.HasIndex(x => new { x.TenantId, x.WPSFileBatchId });
        });
        modelBuilder.Entity<EOSBCalculation>(entity => { entity.ToTable("eosb_calculations"); entity.HasKey(x => x.Id); entity.Property(x => x.EligibleSalary).HasPrecision(14,2); entity.Property(x => x.CalculatedAmount).HasPrecision(14,2); entity.Property(x => x.RulesSnapshotJson).HasColumnType("json"); entity.HasIndex(x => new { x.TenantId, x.EmployeeId, x.Status }); });
        modelBuilder.Entity<PayrollAuditLog>(entity => { entity.ToTable("payroll_audit_logs"); entity.HasKey(x => x.Id); entity.Property(x => x.MetadataJson).HasColumnType("json"); entity.HasIndex(x => new { x.TenantId, x.EntityName, x.EntityId }); });

        modelBuilder.Entity<PayrollRun>(entity =>
        {
            entity.ToTable("payroll_runs");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.TotalGrossSalary).HasPrecision(14, 2);
            entity.Property(x => x.TotalDeductions).HasPrecision(14, 2);
            entity.Property(x => x.TotalNetSalary).HasPrecision(14, 2);
            entity.Property(x => x.Status).HasMaxLength(40);
            entity.Property(x => x.ErpPostingStatus).HasMaxLength(40);
            entity.Property(x => x.ErpPostingReference).HasMaxLength(120);
            entity.Property(x => x.ErpPostingFailureReason).HasMaxLength(1000);
            entity.HasIndex(x => new { x.TenantId, x.CompanyId, x.Year, x.Month });
            entity.HasIndex(x => new { x.TenantId, x.CompanyId, x.Status });
            entity.HasIndex(x => new { x.TenantId, x.ErpPostingStatus });
            entity.HasIndex(x => new { x.TenantId, x.CompanyId, x.Year, x.Month }).IsUnique()
                .HasDatabaseName("IX_payroll_runs_tenant_id_company_id_year_month")
                .HasFilter("\"status\" != 'Voided'");
        });

        modelBuilder.Entity<PayrollSlip>(entity =>
        {
            entity.ToTable("payroll_slips");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.BasicSalary).HasPrecision(12, 2);
            entity.Property(x => x.HousingAllowance).HasPrecision(12, 2);
            entity.Property(x => x.TransportAllowance).HasPrecision(12, 2);
            entity.Property(x => x.OtherAllowances).HasPrecision(12, 2);
            entity.Property(x => x.GrossSalary).HasPrecision(12, 2);
            entity.Property(x => x.Deductions).HasPrecision(12, 2);
            entity.Property(x => x.NetSalary).HasPrecision(12, 2);
            entity.Property(x => x.Status).HasMaxLength(40);
            entity.Property(x => x.YtdGross).HasPrecision(14, 2);
            entity.Property(x => x.YtdDeductions).HasPrecision(14, 2);
            entity.Property(x => x.YtdNet).HasPrecision(14, 2);
            entity.Property(x => x.LoanDeductions).HasPrecision(14, 2);
            entity.HasIndex(x => new { x.TenantId, x.RunId, x.EmployeeId }).IsUnique();
        });

        modelBuilder.Entity<ManpowerRequisition>(entity =>
        {
            entity.ToTable("manpower_requisitions");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.BudgetFrom).HasPrecision(12, 2);
            entity.Property(x => x.BudgetTo).HasPrecision(12, 2);
            entity.HasIndex(x => new { x.TenantId, x.RequisitionNumber }).IsUnique();
            entity.HasIndex(x => new { x.TenantId, x.Status });
        });

        modelBuilder.Entity<JobOpening>(entity =>
        {
            entity.ToTable("job_openings");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.SalaryFrom).HasPrecision(12, 2);
            entity.Property(x => x.SalaryTo).HasPrecision(12, 2);
            entity.HasIndex(x => new { x.TenantId, x.JobCode }).IsUnique();
            entity.HasIndex(x => new { x.TenantId, x.Status });
        });

        modelBuilder.Entity<Candidate>(entity =>
        {
            entity.ToTable("candidates");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.TotalExperienceYears).HasPrecision(5, 1);
            entity.HasIndex(x => new { x.TenantId, x.Email }).IsUnique();
            entity.HasIndex(x => new { x.TenantId, x.Status });
        });

        modelBuilder.Entity<JobApplication>(entity =>
        {
            entity.ToTable("job_applications");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.OfferedSalary).HasPrecision(12, 2);
            entity.HasIndex(x => new { x.TenantId, x.JobOpeningId, x.CandidateId }).IsUnique();
            entity.HasIndex(x => new { x.TenantId, x.JobOpeningId, x.Stage });
        });

        modelBuilder.Entity<ApplicationEvent>(entity =>
        {
            entity.ToTable("application_events");
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.TenantId, x.ApplicationId, x.CreatedAtUtc });
        });

        modelBuilder.Entity<InterviewSchedule>(entity =>
        {
            entity.ToTable("interview_schedules");
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.TenantId, x.ApplicationId });
        });

        modelBuilder.Entity<OfferLetter>(entity =>
        {
            entity.ToTable("offer_letters");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.BasicSalary).HasPrecision(12, 2);
            entity.Property(x => x.HousingAllowance).HasPrecision(12, 2);
            entity.Property(x => x.TransportAllowance).HasPrecision(12, 2);
            entity.Property(x => x.OtherAllowances).HasPrecision(12, 2);
            entity.Property(x => x.GrossSalary).HasPrecision(12, 2);
            entity.Property(x => x.ContentHtml).HasColumnType("text");
            entity.HasIndex(x => new { x.TenantId, x.ApplicationId });
        });

        modelBuilder.Entity<ShiftDefinition>(entity =>
        {
            entity.ToTable("shift_definitions");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Code).HasMaxLength(20).IsRequired();
            entity.Property(x => x.Name).HasMaxLength(120).IsRequired();
            entity.Property(x => x.Color).HasMaxLength(20);
            entity.HasIndex(x => new { x.TenantId, x.Code }).IsUnique();
        });

        modelBuilder.Entity<ShiftAssignment>(entity =>
        {
            entity.ToTable("shift_assignments");
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.TenantId, x.EmployeeId, x.AssignedDate }).IsUnique();
            entity.HasIndex(x => new { x.TenantId, x.AssignedDate });
        });

        modelBuilder.Entity<ShiftPolicy>(entity =>
        {
            entity.ToTable("shift_policies");
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => x.TenantId).IsUnique(); // one policy row per tenant
            entity.Property(x => x.GenderShiftRulesJson).HasColumnType("text");
            entity.Property(x => x.VoluntaryShiftCodesJson).HasColumnType("text");
            entity.Property(x => x.WeekendDemandJson).HasColumnType("text");
            entity.Property(x => x.HolidayDemandJson).HasColumnType("text");
        });

        modelBuilder.Entity<EmployeeOffboarding>(entity =>
        {
            entity.ToTable("employee_offboardings");
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.TenantId, x.EmployeeId });
            entity.HasIndex(x => new { x.TenantId, x.Status });
        });

        modelBuilder.Entity<Tenant>(entity =>
        {
            entity.ToTable("tenants");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Name).HasMaxLength(160).IsRequired();
            entity.Property(x => x.Slug).HasMaxLength(80).IsRequired();
            entity.HasIndex(x => x.Slug).IsUnique();
        });

        modelBuilder.Entity<User>(entity =>
        {
            entity.ToTable("users");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Email).HasMaxLength(256).IsRequired();
            entity.Property(x => x.NormalizedEmail).HasMaxLength(256).IsRequired();
            entity.Property(x => x.FullName).HasMaxLength(180).IsRequired();
            entity.Property(x => x.PasswordHash).HasMaxLength(512).IsRequired();
            entity.Property(x => x.PhoneNumber).HasMaxLength(40);
            entity.Property(x => x.PreferredLanguage).HasMaxLength(10).HasDefaultValue("en");
            entity.Property(x => x.Timezone).HasMaxLength(80).HasDefaultValue("UTC");
            entity.Property(x => x.ExternalId).HasMaxLength(256).HasDefaultValue("");
            entity.Property(x => x.IdentityProvider).HasMaxLength(40).HasDefaultValue("Local");
            entity.Property(x => x.ProvisioningSource).HasMaxLength(40).HasDefaultValue("Local");
            entity.Property(x => x.Status).HasMaxLength(40).HasDefaultValue("Active");
            entity.Property(x => x.AccessMode).HasMaxLength(40).HasDefaultValue("FullPortal");
            entity.Property(x => x.MfaSecretEncrypted).HasMaxLength(1024);
            entity.HasIndex(x => new { x.TenantId, x.NormalizedEmail }).IsUnique();
            entity.HasIndex(x => new { x.TenantId, x.Status });
            entity.HasIndex(x => new { x.TenantId, x.IsDeleted });
            entity.HasOne(x => x.Tenant).WithMany(x => x.Users).HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<SecuritySetting>(entity =>
        {
            entity.ToTable("security_settings");
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => x.TenantId).IsUnique();
        });

        modelBuilder.Entity<TenantIdentityProviderSetting>(entity =>
        {
            entity.ToTable("tenant_identity_provider_settings");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.AllowedDomainsCsv).HasMaxLength(2000);
            entity.Property(x => x.SamlEntityId).HasMaxLength(512);
            entity.Property(x => x.SamlSsoUrl).HasMaxLength(1024);
            entity.Property(x => x.SamlCertificateThumbprint).HasMaxLength(160);
            entity.Property(x => x.OidcAuthority).HasMaxLength(1024);
            entity.Property(x => x.OidcClientId).HasMaxLength(256);
            entity.Property(x => x.ScimTokenHash).HasMaxLength(128);
            entity.HasIndex(x => x.TenantId).IsUnique();
            entity.HasIndex(x => x.ScimTokenHash);
        });

        modelBuilder.Entity<EnterpriseIdentityProvisioningEvent>(entity =>
        {
            entity.ToTable("enterprise_identity_provisioning_events");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Protocol).HasMaxLength(40).IsRequired();
            entity.Property(x => x.Action).HasMaxLength(120).IsRequired();
            entity.Property(x => x.ExternalId).HasMaxLength(256);
            entity.Property(x => x.Status).HasMaxLength(40).IsRequired();
            entity.Property(x => x.DetailsJson).HasColumnType("json");
            entity.HasIndex(x => new { x.TenantId, x.ExternalId });
            entity.HasIndex(x => new { x.TenantId, x.Action, x.CreatedAtUtc });
        });

        modelBuilder.Entity<PermissionGrantorRecord>(entity =>
        {
            entity.ToTable("permission_grantor_records");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.PermissionScope).HasMaxLength(2000).IsRequired();
            entity.Property(x => x.Reason).HasMaxLength(500);
            entity.HasIndex(x => new { x.TenantId, x.GrantorUserId, x.IsActive });
        });

        modelBuilder.Entity<TenantHrConfig>(e => {
            e.ToTable("tenant_hr_configs");
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.TenantId).IsUnique();
        });

        modelBuilder.Entity<Role>(entity =>
        {
            entity.ToTable("roles");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Name).HasMaxLength(80).IsRequired();
            entity.Property(x => x.NormalizedName).HasMaxLength(80).IsRequired();
            entity.Property(x => x.Description).HasMaxLength(240).IsRequired();
            entity.HasIndex(x => new { x.TenantId, x.NormalizedName }).IsUnique();
        });

        modelBuilder.Entity<Permission>(entity =>
        {
            entity.ToTable("permissions");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Key).HasColumnName("permission_key").HasMaxLength(120).IsRequired();
            entity.Property(x => x.Module).HasMaxLength(80).IsRequired();
            entity.Property(x => x.Description).HasMaxLength(240).IsRequired();
            entity.HasIndex(x => x.Key).IsUnique();
        });

        modelBuilder.Entity<UserRole>(entity =>
        {
            entity.ToTable("user_roles");
            entity.HasKey(x => new { x.UserId, x.RoleId });
            entity.HasOne(x => x.User).WithMany(x => x.UserRoles).HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(x => x.Role).WithMany(x => x.UserRoles).HasForeignKey(x => x.RoleId).OnDelete(DeleteBehavior.Cascade);
        });
        modelBuilder.Entity<MigrationImportBatch>(entity =>
        {
            entity.ToTable("migration_import_batches");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.PackageType).HasMaxLength(80).HasDefaultValue("OrganizationStructure");
            entity.Property(x => x.PayloadJson).HasColumnType("json");
            entity.Property(x => x.ReconciliationJson).HasColumnType("json");
            entity.Property(x => x.ErrorJson).HasColumnType("json");
            entity.Property(x => x.ResultJson).HasColumnType("json");
            entity.HasIndex(x => new { x.TenantId, x.ExternalBatchId }).IsUnique().HasFilter("external_batch_id IS NOT NULL");
            entity.HasIndex(x => new { x.TenantId, x.PackageChecksum });
            entity.HasIndex(x => new { x.TenantId, x.Status, x.CreatedAtUtc });
        });

        modelBuilder.Entity<RolePermission>(entity =>
        {
            entity.ToTable("role_permissions");
            entity.HasKey(x => new { x.RoleId, x.PermissionId });
            entity.HasOne(x => x.Role).WithMany(x => x.RolePermissions).HasForeignKey(x => x.RoleId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(x => x.Permission).WithMany(x => x.RolePermissions).HasForeignKey(x => x.PermissionId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<RefreshToken>(entity =>
        {
            entity.ToTable("refresh_tokens");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.TokenHash).HasMaxLength(128).IsRequired();
            entity.Property(x => x.ReplacedByTokenHash).HasMaxLength(128);
            entity.Property(x => x.CreatedByIp).HasMaxLength(64);
            entity.Property(x => x.RevokedByIp).HasMaxLength(64);
            entity.HasIndex(x => x.TokenHash).IsUnique();
            entity.HasOne(x => x.User).WithMany(x => x.RefreshTokens).HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<PasswordResetToken>(entity =>
        {
            entity.ToTable("password_reset_tokens");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.TokenHash).HasMaxLength(128).IsRequired();
            entity.Property(x => x.CreatedByIp).HasMaxLength(64);
            entity.HasIndex(x => x.TokenHash).IsUnique();
            entity.HasOne(x => x.User).WithMany(x => x.PasswordResetTokens).HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<AuditLog>(entity =>
        {
            entity.ToTable("audit_logs");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Action).HasMaxLength(120).IsRequired();
            entity.Property(x => x.EntityName).HasMaxLength(120).IsRequired();
            entity.Property(x => x.EntityId).HasMaxLength(80);
            entity.Property(x => x.IpAddress).HasMaxLength(64);
            entity.Property(x => x.UserAgent).HasMaxLength(512);
            entity.Property(x => x.Metadata).HasColumnType("json");
            entity.Property(x => x.PreviousHash).HasMaxLength(128).IsRequired();
            entity.Property(x => x.EntryHash).HasMaxLength(128).IsRequired();
            entity.Property(x => x.HashAlgorithm).HasMaxLength(32).IsRequired();
            entity.HasIndex(x => new { x.TenantId, x.CreatedAtUtc });
            entity.HasIndex(x => new { x.UserId, x.CreatedAtUtc });
            entity.HasIndex(x => new { x.TenantId, x.EntryHash });
            // Entity audit trail: "show all changes to Employee #123" — covers the common
            // WHERE tenant_id = ? AND entity_name = ? AND entity_id = ? ORDER BY created_at_utc
            entity.HasIndex(x => new { x.TenantId, x.EntityName, x.EntityId, x.CreatedAtUtc });
        });

        // ── Performance & Appraisals ───────────────────────────────────────────

        modelBuilder.Entity<PerformanceCycle>(entity =>
        {
            entity.ToTable("performance_cycles");
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.TenantId, x.Status });
        });

        modelBuilder.Entity<PerformanceScorecardTemplate>(entity =>
        {
            entity.ToTable("performance_scorecard_templates");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.KpiWeight).HasPrecision(5, 2);
            entity.Property(x => x.CompetencyWeight).HasPrecision(5, 2);
            entity.Property(x => x.AttendanceWeight).HasPrecision(5, 2);
            entity.Property(x => x.ProductivityWeight).HasPrecision(5, 2);
            entity.Property(x => x.FeedbackWeight).HasPrecision(5, 2);
            entity.Property(x => x.DisciplineWeight).HasPrecision(5, 2);
            entity.Property(x => x.MinPassingScore).HasPrecision(5, 2);
            entity.HasIndex(x => new { x.TenantId, x.IsActive });
        });

        modelBuilder.Entity<PerformanceRatingScale>(entity =>
        {
            entity.ToTable("performance_rating_scales");
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.TenantId, x.IsDefault });
        });

        modelBuilder.Entity<PerformanceRatingOption>(entity =>
        {
            entity.ToTable("performance_rating_options");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.MinScore).HasPrecision(5, 2);
            entity.Property(x => x.MaxScore).HasPrecision(5, 2);
            entity.HasIndex(x => new { x.TenantId, x.ScaleId });
        });

        modelBuilder.Entity<PerformanceCycleEmployee>(entity =>
        {
            entity.ToTable("performance_cycle_employees");
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.TenantId, x.CycleId, x.EmployeeId }).IsUnique();
            entity.HasIndex(x => new { x.TenantId, x.CycleId, x.Status });
        });

        modelBuilder.Entity<Competency>(entity =>
        {
            entity.ToTable("competencies");
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.TenantId, x.Category, x.IsActive });
        });

        modelBuilder.Entity<RoleCompetency>(entity =>
        {
            entity.ToTable("role_competencies");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Weight).HasPrecision(5, 2);
            entity.HasIndex(x => new { x.TenantId, x.DepartmentName });
        });

        modelBuilder.Entity<EmployeeGoal>(entity =>
        {
            entity.ToTable("employee_goals");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.TargetValue).HasPrecision(14, 4);
            entity.Property(x => x.ActualValue).HasPrecision(14, 4);
            entity.Property(x => x.Weight).HasPrecision(5, 2);
            entity.Property(x => x.AchievementPct).HasPrecision(5, 2);
            entity.HasIndex(x => new { x.TenantId, x.EmployeeId, x.Status });
            entity.HasIndex(x => new { x.TenantId, x.CycleId });
        });

        modelBuilder.Entity<GoalProgressUpdate>(entity =>
        {
            entity.ToTable("goal_progress_updates");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.UpdatedValue).HasPrecision(14, 4);
            entity.HasIndex(x => new { x.TenantId, x.GoalId });
        });

        modelBuilder.Entity<AppraisalReview>(entity =>
        {
            entity.ToTable("appraisal_reviews");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.KpiScore).HasPrecision(5, 2);
            entity.Property(x => x.CompetencyScore).HasPrecision(5, 2);
            entity.Property(x => x.AttendanceScore).HasPrecision(5, 2);
            entity.Property(x => x.ProductivityScore).HasPrecision(5, 2);
            entity.Property(x => x.FeedbackScore).HasPrecision(5, 2);
            entity.Property(x => x.DisciplineScore).HasPrecision(5, 2);
            entity.Property(x => x.FinalScore).HasPrecision(5, 2);
            entity.Property(x => x.CalibrationAdjustment).HasPrecision(5, 2);
            entity.HasIndex(x => new { x.TenantId, x.CycleId, x.EmployeeId }).IsUnique();
            entity.HasIndex(x => new { x.TenantId, x.Status });
            entity.HasIndex(x => new { x.TenantId, x.DepartmentName });
        });

        modelBuilder.Entity<AppraisalScoreBreakdown>(entity =>
        {
            entity.ToTable("appraisal_score_breakdowns");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.RawScore).HasPrecision(5, 2);
            entity.Property(x => x.Weight).HasPrecision(5, 2);
            entity.Property(x => x.WeightedScore).HasPrecision(5, 2);
            entity.HasIndex(x => new { x.TenantId, x.ReviewId });
        });

        modelBuilder.Entity<AppraisalCompetencyRating>(entity =>
        {
            entity.ToTable("appraisal_competency_ratings");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.SelfRating).HasPrecision(4, 2);
            entity.Property(x => x.ManagerRating).HasPrecision(4, 2);
            entity.Property(x => x.Weight).HasPrecision(5, 2);
            entity.HasIndex(x => new { x.TenantId, x.ReviewId, x.CompetencyId }).IsUnique();
        });

        modelBuilder.Entity<Feedback360>(entity =>
        {
            entity.ToTable("feedback_360");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Score).HasPrecision(4, 2);
            entity.HasIndex(x => new { x.TenantId, x.ReviewId });
        });

        modelBuilder.Entity<AppraisalCalibration>(entity =>
        {
            entity.ToTable("appraisal_calibrations");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.OriginalScore).HasPrecision(5, 2);
            entity.Property(x => x.AdjustedScore).HasPrecision(5, 2);
            entity.HasIndex(x => new { x.TenantId, x.CycleId });
            entity.HasIndex(x => new { x.TenantId, x.ReviewId });
        });

        modelBuilder.Entity<AppraisalAppeal>(entity =>
        {
            entity.ToTable("appraisal_appeals");
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.TenantId, x.ReviewId });
            entity.HasIndex(x => new { x.TenantId, x.Status });
        });

        modelBuilder.Entity<IncrementRecommendation>(entity =>
        {
            entity.ToTable("increment_recommendations");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.CurrentSalary).HasPrecision(14, 2);
            entity.Property(x => x.RecommendedIncrementPct).HasPrecision(5, 2);
            entity.Property(x => x.RecommendedIncrementAmount).HasPrecision(14, 2);
            entity.Property(x => x.NewSalary).HasPrecision(14, 2);
            entity.HasIndex(x => new { x.TenantId, x.Status });
        });

        modelBuilder.Entity<PromotionRecommendation>(entity =>
        {
            entity.ToTable("promotion_recommendations");
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.TenantId, x.Status });
        });

        modelBuilder.Entity<BonusRecommendation>(entity =>
        {
            entity.ToTable("bonus_recommendations");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.BonusAmount).HasPrecision(14, 2);
            entity.HasIndex(x => new { x.TenantId, x.Status });
        });

        modelBuilder.Entity<PerformanceImprovementPlan>(entity =>
        {
            entity.ToTable("performance_improvement_plans");
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.TenantId, x.EmployeeId, x.Status });
        });

        modelBuilder.Entity<PIPCheckIn>(entity =>
        {
            entity.ToTable("pip_check_ins");
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.TenantId, x.PipId });
        });

        modelBuilder.Entity<ProbationReview>(entity =>
        {
            entity.ToTable("probation_reviews");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.OverallRating).HasPrecision(4, 2);
            entity.HasIndex(x => new { x.TenantId, x.EmployeeId, x.Status });
        });

        modelBuilder.Entity<ContinuousFeedback>(entity =>
        {
            entity.ToTable("continuous_feedback");
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.TenantId, x.EmployeeId });
            entity.HasIndex(x => new { x.TenantId, x.FeedbackType });
        });

        modelBuilder.Entity<PerformanceAuditLog>(entity =>
        {
            entity.ToTable("performance_audit_logs");
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.TenantId, x.EntityType, x.EntityId });
            entity.HasIndex(x => new { x.TenantId, x.CreatedAtUtc });
        });

        // ── SaaS Platform ──────────────────────────────────────────────────────
        modelBuilder.Entity<TenantSubscription>(entity =>
        {
            entity.ToTable("tenant_subscriptions");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.MonthlyAmount).HasPrecision(10, 2);
            entity.HasIndex(x => new { x.TenantId, x.Status });
        });

        modelBuilder.Entity<TenantFeatureFlag>(entity =>
        {
            entity.ToTable("tenant_feature_flags");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.ConfigJson).HasColumnType("json");
            entity.HasIndex(x => new { x.TenantId, x.FeatureKey }).IsUnique();
        });

        modelBuilder.Entity<TenantLocalizationSetting>(entity =>
        {
            entity.ToTable("tenant_localization_settings");
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => x.TenantId).IsUnique();
        });

        modelBuilder.Entity<TenantBranding>(entity =>
        {
            entity.ToTable("tenant_brandings");
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => x.TenantId).IsUnique();
        });

        modelBuilder.Entity<CountryPayrollRule>(entity =>
        {
            entity.ToTable("country_payroll_rules");
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.TenantId, x.CountryCode, x.RuleKey, x.EffectiveFrom });
        });

        modelBuilder.Entity<StatutoryRule>(entity =>
        {
            entity.ToTable("statutory_rules");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.CountryCode).HasMaxLength(5);
            entity.Property(x => x.Jurisdiction).HasMaxLength(30);
            entity.Property(x => x.RuleKey).HasMaxLength(120);
            entity.Property(x => x.DataType).HasMaxLength(20);
            entity.HasIndex(x => new { x.TenantId, x.CountryCode, x.Jurisdiction, x.RuleKey, x.EffectiveFrom }).IsUnique();
        });

        modelBuilder.Entity<TenantFieldHelpText>(entity =>
        {
            entity.ToTable("tenant_field_help_texts");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.FieldKey).HasMaxLength(120);
            entity.Property(x => x.Text).HasMaxLength(500);
            entity.HasIndex(x => new { x.TenantId, x.FieldKey }).IsUnique();
        });

        modelBuilder.Entity<PlatformUser>(entity =>
        {
            entity.ToTable("platform_users");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Email).HasMaxLength(256);
            entity.Property(x => x.FullName).HasMaxLength(180);
            entity.Property(x => x.PasswordHash).HasMaxLength(512);
            entity.Property(x => x.Role).HasMaxLength(40);
            entity.Property(x => x.LastLoginIp).HasMaxLength(64);
            entity.Property(x => x.MfaSecretEncrypted).HasMaxLength(1024);
            entity.HasIndex(x => x.Email).IsUnique();
            entity.Property(x => x.IsActive).HasDefaultValue(true);
        });

        modelBuilder.Entity<MfaChallengeToken>(entity =>
        {
            entity.ToTable("mfa_challenge_tokens");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.TokenHash).HasMaxLength(128).IsRequired();
            entity.Property(x => x.CreatedByIp).HasMaxLength(64);
            entity.HasIndex(x => x.TokenHash).IsUnique();
            entity.HasIndex(x => x.ExpiresAtUtc);
            entity.Ignore(x => x.IsValid);
        });

        modelBuilder.Entity<PlatformAnnouncement>(entity =>
        {
            entity.ToTable("platform_announcements");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Title).HasMaxLength(200);
            entity.Property(x => x.TargetPlan).HasMaxLength(40);
            entity.Property(x => x.Status).HasMaxLength(20);
            entity.Property(x => x.CreatedByEmail).HasMaxLength(256);
        });

        modelBuilder.Entity<PlatformLead>(entity =>
        {
            entity.ToTable("platform_leads");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.CompanyName).HasMaxLength(200);
            entity.Property(x => x.ContactName).HasMaxLength(180);
            entity.Property(x => x.ContactEmail).HasMaxLength(256);
            entity.Property(x => x.Phone).HasMaxLength(40);
            entity.Property(x => x.Status).HasMaxLength(30);
            entity.Property(x => x.Source).HasMaxLength(30);
            entity.Property(x => x.AssignedTo).HasMaxLength(256);
        });

        modelBuilder.Entity<PlatformSupportSession>(entity =>
        {
            entity.ToTable("platform_support_sessions");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Reason).HasMaxLength(500);
            entity.Property(x => x.StartedByEmail).HasMaxLength(256);
            entity.Property(x => x.StartedByIp).HasMaxLength(64);
            entity.Property(x => x.TargetUserEmail).HasMaxLength(256);
            entity.Property(x => x.TokenHash).HasMaxLength(256);
            entity.Ignore(x => x.IsActive);
            entity.HasIndex(x => new { x.TenantId, x.StartedAtUtc });
            entity.HasIndex(x => x.TargetUserId);
        });

        modelBuilder.Entity<PlatformComplianceControl>(entity =>
        {
            entity.ToTable("platform_compliance_controls");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Category).HasMaxLength(120);
            entity.Property(x => x.ControlId).HasMaxLength(20);
            entity.Property(x => x.Title).HasMaxLength(300);
            entity.Property(x => x.Status).HasMaxLength(30);
            entity.Property(x => x.Owner).HasMaxLength(256);
            entity.Property(x => x.EvidenceUrl).HasMaxLength(1000);
            entity.HasIndex(x => new { x.Category, x.ControlId }).IsUnique();
        });

        modelBuilder.Entity<PlatformSecurityIncident>(entity =>
        {
            entity.ToTable("platform_security_incidents");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Title).HasMaxLength(300);
            entity.Property(x => x.Severity).HasMaxLength(20);
            entity.Property(x => x.Status).HasMaxLength(30);
            entity.Property(x => x.Reporter).HasMaxLength(256);
            entity.Property(x => x.AffectedSystems).HasMaxLength(500);
            entity.HasIndex(x => new { x.Status, x.Severity });
        });

        modelBuilder.Entity<PlatformConfigEntry>(entity =>
        {
            entity.ToTable("platform_config_entries");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Key).HasMaxLength(100);
            entity.Property(x => x.Value).HasMaxLength(2000);
            entity.HasIndex(x => x.Key).IsUnique();
        });

        // ── AI Intelligence ────────────────────────────────────────────────────
        modelBuilder.Entity<AIModelConfig>(entity =>
        {
            entity.ToTable("ai_model_configs");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.ConfigJson).HasColumnType("json");
            entity.HasIndex(x => new { x.TenantId, x.UseCase, x.IsActive });
        });

        modelBuilder.Entity<AIInsight>(entity =>
        {
            entity.ToTable("ai_insights");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.DataJson).HasColumnType("json");
            entity.HasIndex(x => new { x.TenantId, x.Module, x.InsightType, x.IsAcknowledged });
            entity.HasIndex(x => new { x.TenantId, x.EmployeeId });
        });

        modelBuilder.Entity<AIRecommendation>(entity =>
        {
            entity.ToTable("ai_recommendations");
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.TenantId, x.Module, x.Status });
            entity.HasIndex(x => new { x.TenantId, x.EmployeeId });
        });

        modelBuilder.Entity<AIHRQueryLog>(entity =>
        {
            entity.ToTable("ai_hr_query_logs");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.LoggedPrompt).HasColumnType("text");
            entity.Property(x => x.PromptSummary).HasColumnType("text");
            entity.Property(x => x.PromptHash).HasMaxLength(128);
            entity.Property(x => x.Provider).HasMaxLength(50);
            entity.Property(x => x.Model).HasMaxLength(100);
            entity.Property(x => x.ResponseStatus).HasMaxLength(50);
            entity.HasIndex(x => new { x.TenantId, x.UserId, x.CreatedAtUtc });
            entity.HasIndex(x => new { x.TenantId, x.CreatedAtUtc });
        });

        modelBuilder.Entity<AIHRQueryCache>(entity =>
        {
            entity.ToTable("ai_hr_query_cache");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.NormalizedQuery).HasColumnType("text");
            entity.Property(x => x.Answer).HasColumnType("text");
            entity.Property(x => x.QueryHash).HasMaxLength(128);
            entity.Property(x => x.CacheKey).HasMaxLength(191);
            entity.Property(x => x.UserRoleSignature).HasColumnType("text");
            entity.Property(x => x.PermissionSignature).HasColumnType("text");
            entity.Property(x => x.Provider).HasMaxLength(50);
            entity.Property(x => x.Model).HasMaxLength(100);
            entity.Property(x => x.ResponseStatus).HasMaxLength(50);
            entity.HasIndex(x => new { x.TenantId, x.CacheKey }).IsUnique();
            entity.HasIndex(x => new { x.TenantId, x.ExpiresAtUtc });
            entity.HasIndex(x => new { x.TenantId, x.IntentClassified, x.Module });
        });

        modelBuilder.Entity<TenantAiUsage>(entity =>
        {
            entity.ToTable("tenant_ai_usage");
            entity.HasKey(x => new { x.TenantId, x.YearMonth });
        });

        modelBuilder.Entity<TenantInvoice>(entity =>
        {
            entity.ToTable("tenant_invoices");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Amount).HasPrecision(12, 2);
            entity.HasIndex(x => new { x.TenantId, x.Status });
            entity.HasIndex(x => new { x.TenantId, x.InvoiceDate });
        });

        modelBuilder.Entity<TenantInvoiceLine>(entity =>
        {
            entity.ToTable("tenant_invoice_lines");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.UnitPrice).HasPrecision(12, 2);
            entity.Property(x => x.DiscountAmount).HasPrecision(12, 2);
            entity.Property(x => x.TaxRate).HasPrecision(6, 4);
            entity.Property(x => x.TaxAmount).HasPrecision(12, 2);
            entity.Property(x => x.LineTotal).HasPrecision(12, 2);
            entity.HasIndex(x => new { x.InvoiceId, x.SortOrder });
            entity.HasIndex(x => x.TenantId);
        });

        modelBuilder.Entity<TenantPayment>(entity =>
        {
            entity.ToTable("tenant_payments");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Amount).HasPrecision(12, 2);
            entity.HasIndex(x => x.TenantId);
            entity.HasIndex(x => x.InvoiceId);
            entity.HasIndex(x => new { x.TenantId, x.Status });
        });

        modelBuilder.Entity<LoginActivity>(entity =>
        {
            entity.ToTable("login_activity");
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => x.TenantId);
            entity.HasIndex(x => x.UserId);
            entity.HasIndex(x => x.OccurredAtUtc);
            entity.HasIndex(x => new { x.TenantId, x.EventType, x.OccurredAtUtc });
        });

        // ── Pricing ───────────────────────────────────────────────────────────
        modelBuilder.Entity<PricingConfig>(entity =>
        {
            entity.ToTable("pricing_config");
            entity.HasKey(x => x.Key);
            entity.Property(x => x.Key).HasMaxLength(80);
            entity.Property(x => x.Label).HasMaxLength(200);
            entity.Property(x => x.Group).HasMaxLength(50);
            entity.Property(x => x.Plan).HasMaxLength(30);
            entity.Property(x => x.Value).HasPrecision(12, 2);
        });

        modelBuilder.Entity<PricingModuleConfig>(entity =>
        {
            entity.ToTable("pricing_module_configs");
            entity.HasKey(x => x.ModuleKey);
            entity.Property(x => x.ModuleKey).HasMaxLength(60);
            entity.Property(x => x.ModuleName).HasMaxLength(100);
            entity.Property(x => x.AddonPriceMonthly).HasPrecision(10, 2);
        });

        modelBuilder.Entity<PricingQuote>(entity =>
        {
            entity.ToTable("pricing_quotes");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.CompanyName).HasMaxLength(200);
            entity.Property(x => x.ContactName).HasMaxLength(180);
            entity.Property(x => x.ContactEmail).HasMaxLength(256);
            entity.Property(x => x.Phone).HasMaxLength(40);
            entity.Property(x => x.OrgType).HasMaxLength(40);
            entity.Property(x => x.SelectedModulesJson).HasColumnType("json");
            entity.Property(x => x.EstimatedMonthlyAmount).HasPrecision(12, 2);
            entity.Property(x => x.EstimatedAnnualAmount).HasPrecision(12, 2);
            entity.Property(x => x.Status).HasMaxLength(20);
            entity.HasIndex(x => x.Status);
            entity.HasIndex(x => x.CreatedAtUtc);
        });

        modelBuilder.Entity<ResumeParseResult>(entity =>
        {
            entity.ToTable("resume_parse_results");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.ParsedTextJson).HasColumnType("json");
            entity.HasIndex(x => new { x.TenantId, x.CandidateId });
            entity.HasIndex(x => new { x.TenantId, x.ParseStatus });
        });

        modelBuilder.Entity<CandidateAIScore>(entity =>
        {
            entity.ToTable("candidate_ai_scores");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.OverallScore).HasPrecision(5, 2);
            entity.Property(x => x.SkillMatchScore).HasPrecision(5, 2);
            entity.Property(x => x.ExperienceScore).HasPrecision(5, 2);
            entity.Property(x => x.EducationScore).HasPrecision(5, 2);
            entity.HasIndex(x => new { x.TenantId, x.CandidateId, x.JobOpeningId });
        });

        modelBuilder.Entity<PayrollAIValidationResult>(entity =>
        {
            entity.ToTable("payroll_ai_validation_results");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.DataJson).HasColumnType("json");
            entity.HasIndex(x => new { x.TenantId, x.PayrollRunId, x.Severity });
            entity.HasIndex(x => new { x.TenantId, x.IsResolved });
        });

        modelBuilder.Entity<EmployeeRiskScore>(entity =>
        {
            entity.ToTable("employee_risk_scores");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.ChurnRiskScore).HasPrecision(5, 2);
            entity.Property(x => x.BurnoutRiskScore).HasPrecision(5, 2);
            entity.Property(x => x.PerformanceDeclineScore).HasPrecision(5, 2);
            entity.Property(x => x.RiskFactorsJson).HasColumnType("json");
            entity.HasIndex(x => new { x.TenantId, x.EmployeeId, x.ComputedAtUtc });
            entity.HasIndex(x => new { x.TenantId, x.OverallRiskLevel });
        });

        modelBuilder.Entity<EmployeeChurnPrediction>(entity =>
        {
            entity.ToTable("employee_churn_predictions");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.ChurnProbability).HasPrecision(4, 3);
            entity.Ignore(x => x.ContributingFactors);
            entity.HasIndex(x => new { x.TenantId, x.EmployeeId, x.ComputedAtUtc });
        });

        modelBuilder.Entity<BurnoutRiskSignal>(entity =>
        {
            entity.ToTable("burnout_risk_signals");
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.TenantId, x.EmployeeId, x.DetectedDate });
            entity.HasIndex(x => new { x.TenantId, x.SignalType, x.IsAcknowledged });
        });

        // ── Recruitment Extended ───────────────────────────────────────────────
        modelBuilder.Entity<WorkforcePlan>(entity =>
        {
            entity.ToTable("workforce_plans");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.BudgetAllocated).HasPrecision(14, 2);
            entity.Property(x => x.BudgetUtilized).HasPrecision(14, 2);
            entity.HasIndex(x => new { x.TenantId, x.PlanCode }).IsUnique();
            entity.HasIndex(x => new { x.TenantId, x.PlanYear, x.Status });
            entity.HasIndex(x => new { x.TenantId, x.IsDeleted });
        });

        modelBuilder.Entity<CandidateDocument>(entity =>
        {
            entity.ToTable("candidate_documents");
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.TenantId, x.CandidateId, x.IsDeleted });
            entity.HasIndex(x => new { x.TenantId, x.ApplicationId });
        });

        modelBuilder.Entity<InterviewFeedback>(entity =>
        {
            entity.ToTable("interview_feedbacks");
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.TenantId, x.InterviewScheduleId });
            entity.HasIndex(x => new { x.TenantId, x.ApplicationId });
        });

        modelBuilder.Entity<AssessmentTemplate>(entity =>
        {
            entity.ToTable("assessment_templates");
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.TenantId, x.Code }).IsUnique();
            entity.HasIndex(x => new { x.TenantId, x.IsActive, x.IsDeleted });
        });

        modelBuilder.Entity<AssessmentQuestion>(entity =>
        {
            entity.ToTable("assessment_questions");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.OptionsJson).HasColumnType("json");
            entity.HasIndex(x => new { x.TenantId, x.TemplateId, x.OrderIndex });
        });

        modelBuilder.Entity<CandidateAssessment>(entity =>
        {
            entity.ToTable("candidate_assessments");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.ScorePercentage).HasPrecision(5, 2);
            entity.Property(x => x.ResultJson).HasColumnType("json");
            entity.HasIndex(x => new { x.TenantId, x.ApplicationId });
            entity.HasIndex(x => new { x.TenantId, x.CandidateId, x.Status });
            entity.HasIndex(x => new { x.TenantId, x.InvitationToken }).IsUnique();
        });

        modelBuilder.Entity<OfferApproval>(entity =>
        {
            entity.ToTable("offer_approvals");
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.TenantId, x.OfferLetterId, x.StepOrder });
            entity.HasIndex(x => new { x.TenantId, x.ApplicationId, x.Status });
        });

        modelBuilder.Entity<OnboardingChecklist>(entity =>
        {
            entity.ToTable("onboarding_checklists");
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.TenantId, x.Code }).IsUnique();
            entity.HasIndex(x => new { x.TenantId, x.IsActive, x.IsDeleted });
        });

        modelBuilder.Entity<OnboardingChecklistTemplateTask>(entity =>
        {
            entity.ToTable("onboarding_checklist_template_tasks");
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.TenantId, x.ChecklistId, x.OrderIndex });
            entity.HasIndex(x => new { x.TenantId, x.ChecklistId, x.TaskTitle }).IsUnique();
        });

        modelBuilder.Entity<OnboardingTask>(entity =>
        {
            entity.ToTable("onboarding_tasks");
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.TenantId, x.EmployeeId, x.Status });
            entity.HasIndex(x => new { x.TenantId, x.ApplicationId, x.Status });
            entity.HasIndex(x => new { x.TenantId, x.ChecklistId, x.OrderIndex });
        });

        modelBuilder.Entity<RecruitmentAuditLog>(entity =>
        {
            entity.ToTable("recruitment_audit_logs");
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.TenantId, x.EntityType, x.EntityId, x.CreatedAtUtc });
        });

        // ── Compliance Module ──────────────────────────────────────────────────
        modelBuilder.Entity<DocType>(entity =>
        {
            entity.ToTable("doc_types");
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.TenantId, x.Code }).IsUnique();
            entity.HasIndex(x => new { x.TenantId, x.IsActive, x.IsDeleted });
        });

        modelBuilder.Entity<ContractTemplate>(entity =>
        {
            entity.ToTable("contract_templates");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.ContentHtmlEn).HasColumnType("text");
            entity.Property(x => x.ContentHtmlAr).HasColumnType("text");
            entity.HasIndex(x => new { x.TenantId, x.Code }).IsUnique();
            entity.HasIndex(x => new { x.TenantId, x.IsActive, x.IsDeleted });
        });

        modelBuilder.Entity<EmployeeContract>(entity =>
        {
            entity.ToTable("employee_contracts");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.BasicSalary).HasPrecision(14, 2);
            entity.Property(x => x.ContentHtmlEn).HasColumnType("text");
            entity.Property(x => x.ContentHtmlAr).HasColumnType("text");
            entity.HasIndex(x => new { x.TenantId, x.ContractNumber }).IsUnique();
            entity.HasIndex(x => new { x.TenantId, x.EmployeeId, x.Status });
            entity.HasIndex(x => new { x.TenantId, x.IsDeleted });
        });

        modelBuilder.Entity<ComplianceRequirement>(entity =>
        {
            entity.ToTable("compliance_requirements");
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.TenantId, x.DocTypeId, x.CountryCode });
            entity.HasIndex(x => new { x.TenantId, x.IsActive });
        });

        modelBuilder.Entity<ComplianceRenewal>(entity =>
        {
            entity.ToTable("compliance_renewals");
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.TenantId, x.EmployeeId, x.Status });
            entity.HasIndex(x => new { x.TenantId, x.ExpiryDate, x.Status });
        });

        modelBuilder.Entity<ComplianceReminder>(entity =>
        {
            entity.ToTable("compliance_reminders");
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.TenantId, x.EmployeeId, x.Status });
            entity.HasIndex(x => new { x.TenantId, x.ReminderType, x.Status });
        });

        modelBuilder.Entity<VisaRecord>(entity =>
        {
            entity.ToTable("visa_records");
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.TenantId, x.EmployeeId, x.IsDeleted });
            entity.HasIndex(x => new { x.TenantId, x.VisaNumber });
            entity.HasIndex(x => new { x.TenantId, x.ExpiryDate, x.Status });
        });

        modelBuilder.Entity<PassportRecord>(entity =>
        {
            entity.ToTable("passport_records");
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.TenantId, x.EmployeeId, x.IsDeleted });
            entity.HasIndex(x => new { x.TenantId, x.PassportNumber });
            entity.HasIndex(x => new { x.TenantId, x.ExpiryDate, x.Status });
        });

        modelBuilder.Entity<WorkPermitRecord>(entity =>
        {
            entity.ToTable("work_permit_records");
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.TenantId, x.EmployeeId, x.IsDeleted });
            entity.HasIndex(x => new { x.TenantId, x.PermitNumber });
            entity.HasIndex(x => new { x.TenantId, x.ExpiryDate, x.Status });
        });

        modelBuilder.Entity<ComplianceAuditLog>(entity =>
        {
            entity.ToTable("compliance_audit_logs");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.MetadataJson).HasColumnType("json");
            entity.HasIndex(x => new { x.TenantId, x.EntityType, x.EntityId });
            entity.HasIndex(x => new { x.TenantId, x.EmployeeId, x.CreatedAtUtc });
        });

        modelBuilder.Entity<ComplianceAIInsight>(entity =>
        {
            entity.ToTable("compliance_ai_insights");
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.TenantId, x.InsightType, x.IsAcknowledged });
            entity.HasIndex(x => new { x.TenantId, x.EmployeeId });
        });

        // ── Setup & Admin ──────────────────────────────────────────────────────
        modelBuilder.Entity<MasterDataType>(entity =>
        {
            entity.ToTable("master_data_types");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Code).HasMaxLength(100);
            entity.HasIndex(x => new { x.TenantId, x.Code }).IsUnique();
            entity.HasIndex(x => new { x.TenantId, x.IsActive });
        });

        modelBuilder.Entity<MasterDataValue>(entity =>
        {
            entity.ToTable("master_data_values");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Code).HasMaxLength(100);
            entity.Property(x => x.ExtraJson).HasColumnType("json");
            entity.HasIndex(x => new { x.TenantId, x.TypeId, x.Code }).IsUnique();
            entity.HasIndex(x => new { x.TenantId, x.TypeId, x.IsActive });
        });

        modelBuilder.Entity<NumberingRule>(entity =>
        {
            entity.ToTable("numbering_rules");
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.TenantId, x.EntityType }).IsUnique();
        });

        modelBuilder.Entity<SystemSetting>(entity =>
        {
            entity.ToTable("system_settings");
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.TenantId, x.Category, x.SettingKey }).IsUnique();
        });

        modelBuilder.Entity<GCCComplianceSetting>(entity =>
        {
            entity.ToTable("gcc_compliance_settings");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.EosbYears1To5Rate).HasPrecision(8, 2);
            entity.Property(x => x.EosbYearsAbove5Rate).HasPrecision(8, 2);
            // T+C natural key: a tenant-default row (CompanyId = null) and a per-company
            // override (CompanyId set) may coexist for the same country. Non-null CompanyId
            // uniqueness is DB-enforced; the null-default row's single-row invariant is held
            // in-app by the provisioning bundle (insert-if-absent).
            entity.HasIndex(x => new { x.TenantId, x.CompanyId, x.CountryCode }).IsUnique();
        });

        modelBuilder.Entity<Location>(entity =>
        {
            entity.ToTable("locations");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Latitude).HasPrecision(10, 7);
            entity.Property(x => x.Longitude).HasPrecision(10, 7);
            entity.Property(x => x.GeofenceRadiusMeters).HasPrecision(10, 2);
            entity.HasIndex(x => new { x.TenantId, x.Code }).IsUnique();
            entity.HasIndex(x => new { x.TenantId, x.IsActive });
        });

        modelBuilder.Entity<FiscalYear>(entity =>
        {
            entity.ToTable("fiscal_years");
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.TenantId, x.Year }).IsUnique();
            entity.HasIndex(x => new { x.TenantId, x.IsCurrent });
        });

        modelBuilder.Entity<NotificationTemplate>(entity =>
        {
            entity.ToTable("notification_templates");
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.TenantId, x.Code, x.Channel }).IsUnique();
            entity.HasIndex(x => new { x.TenantId, x.EventType });
        });

        modelBuilder.Entity<AdminAuditLog>(entity =>
        {
            entity.ToTable("admin_audit_logs");
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.TenantId, x.EntityType, x.EntityId });
            entity.HasIndex(x => new { x.TenantId, x.CreatedAtUtc });
        });

        // ── Company governance (Phase 1B: per-legal-entity policy foundation) ─────
        modelBuilder.Entity<CompanyTaxPolicy>(entity =>
        {
            entity.ToTable("company_tax_policies");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.CountryCode).HasMaxLength(2);
            entity.Property(x => x.Status).HasMaxLength(20);
            entity.Property(x => x.IncomeTaxRatePercent).HasPrecision(8, 4);
            entity.Property(x => x.Notes).HasMaxLength(1000);
            // Resolution query: tenant + company (or null default) + Active + date window.
            entity.HasIndex(x => new { x.TenantId, x.CompanyId, x.Status, x.EffectiveFrom });
        });

        modelBuilder.Entity<CompanyComplianceProfile>(entity =>
        {
            entity.ToTable("company_compliance_profiles");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.CountryCode).HasMaxLength(2);
            entity.Property(x => x.Jurisdiction).HasMaxLength(40);
            entity.Property(x => x.CompliancePack).HasMaxLength(60);
            entity.Property(x => x.Status).HasMaxLength(20);
            entity.Property(x => x.Notes).HasMaxLength(1000);
            entity.HasIndex(x => new { x.TenantId, x.CompanyId, x.Status, x.EffectiveFrom });
        });

        // ── Loans ──────────────────────────────────────────────────────────────
        modelBuilder.Entity<LoanType>(entity =>
        {
            entity.ToTable("loan_types");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.MaxAmount).HasPrecision(14, 2);
            entity.Property(x => x.InterestRate).HasPrecision(8, 4);
            entity.HasIndex(x => new { x.TenantId, x.Code }).IsUnique();
        });

        modelBuilder.Entity<LoanPolicy>(entity =>
        {
            entity.ToTable("loan_policies");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.MaxMultiplierOfSalary).HasPrecision(8, 2);
            entity.HasIndex(x => new { x.TenantId, x.LoanTypeId });
        });

        modelBuilder.Entity<EmployeeLoan>(entity =>
        {
            entity.ToTable("employee_loans");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.RequestedAmount).HasPrecision(14, 2);
            entity.Property(x => x.ApprovedAmount).HasPrecision(14, 2);
            entity.Property(x => x.InstallmentAmount).HasPrecision(14, 2);
            entity.Property(x => x.TotalRepaid).HasPrecision(14, 2);
            entity.Property(x => x.OutstandingBalance).HasPrecision(14, 2);
            entity.HasIndex(x => new { x.TenantId, x.LoanNumber }).IsUnique();
            entity.HasIndex(x => new { x.TenantId, x.EmployeeId, x.Status });
        });

        modelBuilder.Entity<LoanApproval>(entity =>
        {
            entity.ToTable("loan_approvals");
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.TenantId, x.LoanId, x.StepOrder });
        });

        modelBuilder.Entity<LoanInstallment>(entity =>
        {
            entity.ToTable("loan_installments");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.AmountDue).HasPrecision(14, 2);
            entity.Property(x => x.AmountPaid).HasPrecision(14, 2);
            entity.HasIndex(x => new { x.TenantId, x.LoanId, x.InstallmentNumber }).IsUnique();
            entity.HasIndex(x => new { x.TenantId, x.Status });
        });

        modelBuilder.Entity<LoanSettlement>(entity =>
        {
            entity.ToTable("loan_settlements");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.SettlementAmount).HasPrecision(14, 2);
            entity.HasIndex(x => new { x.TenantId, x.LoanId });
        });

        modelBuilder.Entity<LoanAuditLog>(entity =>
        {
            entity.ToTable("loan_audit_logs");
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.TenantId, x.LoanId });
        });

        // ── Advances ───────────────────────────────────────────────────────────
        modelBuilder.Entity<AdvancePolicy>(entity =>
        {
            entity.ToTable("advance_policies");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.MaxPercentageOfSalary).HasPrecision(8, 2);
            entity.HasIndex(x => new { x.TenantId, x.IsActive });
        });

        modelBuilder.Entity<SalaryAdvance>(entity =>
        {
            entity.ToTable("salary_advances");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.RequestedAmount).HasPrecision(14, 2);
            entity.Property(x => x.ApprovedAmount).HasPrecision(14, 2);
            entity.Property(x => x.InstallmentAmount).HasPrecision(14, 2);
            entity.Property(x => x.TotalRepaid).HasPrecision(14, 2);
            entity.Property(x => x.OutstandingBalance).HasPrecision(14, 2);
            entity.HasIndex(x => new { x.TenantId, x.AdvanceNumber }).IsUnique();
            entity.HasIndex(x => new { x.TenantId, x.EmployeeId, x.Status });
        });

        modelBuilder.Entity<AdvanceApproval>(entity =>
        {
            entity.ToTable("advance_approvals");
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.TenantId, x.AdvanceId, x.StepOrder });
        });

        modelBuilder.Entity<AdvanceInstallment>(entity =>
        {
            entity.ToTable("advance_installments");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.AmountDue).HasPrecision(14, 2);
            entity.Property(x => x.AmountPaid).HasPrecision(14, 2);
            entity.HasIndex(x => new { x.TenantId, x.AdvanceId, x.InstallmentNumber }).IsUnique();
        });

        modelBuilder.Entity<AdvanceAuditLog>(entity =>
        {
            entity.ToTable("advance_audit_logs");
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.TenantId, x.AdvanceId });
        });

        // ── Bonuses ────────────────────────────────────────────────────────────
        modelBuilder.Entity<BonusType>(entity =>
        {
            entity.ToTable("bonus_types");
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.TenantId, x.Code }).IsUnique();
        });

        modelBuilder.Entity<BonusBatch>(entity =>
        {
            entity.ToTable("bonus_batches");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.TotalAmount).HasPrecision(16, 2);
            entity.HasIndex(x => new { x.TenantId, x.BatchNumber }).IsUnique();
            entity.HasIndex(x => new { x.TenantId, x.Status });
        });

        modelBuilder.Entity<EmployeeBonus>(entity =>
        {
            entity.ToTable("employee_bonuses");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.BasicSalary).HasPrecision(14, 2);
            entity.Property(x => x.CalculationValue).HasPrecision(10, 4);
            entity.Property(x => x.BonusAmount).HasPrecision(14, 2);
            entity.HasIndex(x => new { x.TenantId, x.BonusBatchId, x.EmployeeId });
            entity.HasIndex(x => new { x.TenantId, x.EmployeeId, x.Status });
        });

        modelBuilder.Entity<BonusApproval>(entity =>
        {
            entity.ToTable("bonus_approvals");
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.TenantId, x.BonusBatchId, x.StepOrder });
        });

        modelBuilder.Entity<BonusAuditLog>(entity =>
        {
            entity.ToTable("bonus_audit_logs");
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.TenantId, x.BonusBatchId });
        });

        modelBuilder.Entity<FinanceGlEntry>(entity =>
        {
            entity.ToTable("finance_gl_entries");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Amount).HasColumnType("decimal(18,4)");
            entity.Property(x => x.ErpPostingStatus).HasMaxLength(40);
            entity.Property(x => x.ErpDocumentNumber).HasMaxLength(120);
            entity.Property(x => x.ErpRejectionReason).HasMaxLength(1000);
            entity.HasIndex(x => new { x.TenantId, x.SourceModule, x.SourceEntityId });
            entity.HasIndex(x => new { x.TenantId, x.Period });
            entity.HasIndex(x => new { x.TenantId, x.ErpPostingStatus });
        });

        // ── Reports & Analytics ────────────────────────────────────────────────
        modelBuilder.Entity<SavedReport>(entity =>
        {
            entity.ToTable("saved_reports");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.FiltersJson).HasColumnType("json");
            entity.Property(x => x.ColumnsJson).HasColumnType("json");
            entity.HasIndex(x => new { x.TenantId, x.CreatedBy });
            entity.HasIndex(x => new { x.TenantId, x.Category });
        });

        modelBuilder.Entity<ReportSchedule>(entity =>
        {
            entity.ToTable("report_schedules");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.FiltersJson).HasColumnType("json");
            entity.HasIndex(x => new { x.TenantId, x.IsActive });
        });

        modelBuilder.Entity<ReportExecutionLog>(entity =>
        {
            entity.ToTable("report_execution_logs");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.FiltersJson).HasColumnType("json");
            entity.HasIndex(x => new { x.TenantId, x.ReportKey });
            entity.HasIndex(x => new { x.TenantId, x.CreatedAtUtc });
        });

        // ── Policy RAG Documents ───────────────────────────────────────────────
        modelBuilder.Entity<PolicyDocument>(entity =>
        {
            entity.ToTable("policy_documents");
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.TenantId, x.IsDeleted });
            entity.HasIndex(x => new { x.TenantId, x.Status });
            entity.HasMany(x => x.Chunks).WithOne(x => x.Document).HasForeignKey(x => x.DocumentId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<DocumentChunk>(entity =>
        {
            entity.ToTable("document_chunks");
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.TenantId, x.DocumentId, x.ChunkIndex });
        });

        modelBuilder.Entity<UserEntityAccess>(entity =>
        {
            entity.ToTable("user_entity_accesses");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Role).HasMaxLength(80).IsRequired();
            entity.HasIndex(x => new { x.TenantId, x.UserId, x.IsActive });
            entity.HasIndex(x => new { x.TenantId, x.UserId, x.CompanyId, x.Role }).IsUnique();
            entity.HasOne(x => x.User).WithMany(x => x.EntityAccesses).HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(x => x.Company).WithMany().HasForeignKey(x => x.CompanyId).OnDelete(DeleteBehavior.SetNull);
        });

        ApplyTenantQueryFilters(modelBuilder);
        ApplyCompanyScopeIndexes(modelBuilder);
    }

    /// <summary>
    /// Convention-driven indexing for the company dimension: every ICompanyScoped entity
    /// that also carries TenantId gets a composite (TenantId, CompanyId) index so the
    /// automatic company filter never table-scans. Hot operational tables additionally
    /// get a (TenantId, CompanyId, status/date) index for their dominant list queries.
    /// HasIndex on an existing property set is idempotent, so manual per-entity indexes
    /// (e.g. cost_centers) are not duplicated.
    /// </summary>
    private static void ApplyCompanyScopeIndexes(ModelBuilder modelBuilder)
    {
        var hotPathExtras = new Dictionary<Type, string[]>
        {
            [typeof(Models.AttendanceRecord)] = new[] { "TenantId", "CompanyId", "WorkDate" },
            [typeof(Models.LeaveRequest)] = new[] { "TenantId", "CompanyId", "Status" },
            [typeof(Models.PayrollRun)] = new[] { "TenantId", "CompanyId", "Status" },
        };

        foreach (var entityType in modelBuilder.Model.GetEntityTypes())
        {
            if (entityType.IsOwned() || entityType.BaseType is not null) continue;
            var clr = entityType.ClrType;
            if (!typeof(ICompanyScoped).IsAssignableFrom(clr)) continue;
            if (clr.GetProperty("TenantId") is null) continue;

            modelBuilder.Entity(clr).HasIndex("TenantId", "CompanyId");
            if (hotPathExtras.TryGetValue(clr, out var extra))
                modelBuilder.Entity(clr).HasIndex(extra);
        }
    }

    private static readonly MethodInfo _setTenantFilterNonNull =
        typeof(ZayraDbContext).GetMethod(nameof(SetTenantFilterNonNull), BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly MethodInfo _setTenantFilterNullable =
        typeof(ZayraDbContext).GetMethod(nameof(SetTenantFilterNullable), BindingFlags.Instance | BindingFlags.NonPublic)!;

    /// <summary>
    /// Defence-in-depth tenant isolation: every entity implementing <see cref="ITenantOwned"/>
    /// or <see cref="INullableTenantOwned"/> gets a global query filter so a forgotten
    /// <c>.Where(x => x.TenantId == ...)</c> cannot leak across tenants.
    ///
    /// Discovery is now driven by interface membership (not by "has a property named TenantId")
    /// so a misnamed property cannot silently lose its filter.  A mis-declared entity — one that
    /// has a TenantId property but doesn't implement the interface — is caught by
    /// <see cref="Zayra.Api.Infrastructure.Boot.TenantOwnershipBootAssertion"/> at startup.
    ///
    /// The filter is bypassed when <see cref="_tenantId"/> is null (seeding, login/refresh,
    /// background work — see the field doc).
    /// </summary>
    private void ApplyTenantQueryFilters(ModelBuilder modelBuilder)
    {
        foreach (var entityType in modelBuilder.Model.GetEntityTypes())
        {
            if (entityType.IsOwned() || entityType.BaseType is not null) continue;
            var clr = entityType.ClrType;

            if (typeof(ITenantOwned).IsAssignableFrom(clr))
                _setTenantFilterNonNull.MakeGenericMethod(clr).Invoke(this, new object[] { modelBuilder });
            else if (typeof(INullableTenantOwned).IsAssignableFrom(clr))
                _setTenantFilterNullable.MakeGenericMethod(clr).Invoke(this, new object[] { modelBuilder });
        }
    }

    // The lambdas close over instance properties (_tenantId, _isGroupScope, _companyScopeIds)
    // so EF Core re-parameterises per request (lazy resolution from the ambient HttpContext).
    // Each method AND-s in the soft-delete guard and, for ICompanyScoped entities, the
    // company-scope guard in one HasQueryFilter call (EF Core only supports one per entity type).
    // Code that intentionally needs deleted/cross-company records must call .IgnoreQueryFilters().
    //
    // Two company-scope tiers:
    //   ICompanyScoped (config/template): CompanyId == null ⇒ tenant-wide, visible to all.
    //   ICompanyScopedOperational:        CompanyId == null ⇒ visible to GROUP scope only —
    //     a scoped user never sees unassigned operational rows (poison-default prevention).
    private void SetTenantFilterNonNull<TEntity>(ModelBuilder modelBuilder) where TEntity : class
    {
        var hasSoftDelete = typeof(TEntity).GetProperty("IsDeleted") != null;
        var isOperationalScope = typeof(ICompanyScopedOperational).IsAssignableFrom(typeof(TEntity));
        var isConfigScope = !isOperationalScope && typeof(ICompanyScoped).IsAssignableFrom(typeof(TEntity));

        if (hasSoftDelete && isOperationalScope)
            modelBuilder.Entity<TEntity>().HasQueryFilter(
                e => (_tenantId == null || EF.Property<Guid>(e, "TenantId") == _tenantId)
                     && !EF.Property<bool>(e, "IsDeleted")
                     && (_isGroupScope || (EF.Property<Guid?>(e, "CompanyId") != null
                         && _companyScopeIds.Contains(EF.Property<Guid?>(e, "CompanyId")!.Value))));
        else if (hasSoftDelete && isConfigScope)
            modelBuilder.Entity<TEntity>().HasQueryFilter(
                e => (_tenantId == null || EF.Property<Guid>(e, "TenantId") == _tenantId)
                     && !EF.Property<bool>(e, "IsDeleted")
                     && (_isGroupScope || EF.Property<Guid?>(e, "CompanyId") == null
                         || _companyScopeIds.Contains(EF.Property<Guid?>(e, "CompanyId")!.Value)));
        else if (hasSoftDelete)
            modelBuilder.Entity<TEntity>().HasQueryFilter(
                e => (_tenantId == null || EF.Property<Guid>(e, "TenantId") == _tenantId)
                     && !EF.Property<bool>(e, "IsDeleted"));
        else if (isOperationalScope)
            modelBuilder.Entity<TEntity>().HasQueryFilter(
                e => (_tenantId == null || EF.Property<Guid>(e, "TenantId") == _tenantId)
                     && (_isGroupScope || (EF.Property<Guid?>(e, "CompanyId") != null
                         && _companyScopeIds.Contains(EF.Property<Guid?>(e, "CompanyId")!.Value))));
        else if (isConfigScope)
            modelBuilder.Entity<TEntity>().HasQueryFilter(
                e => (_tenantId == null || EF.Property<Guid>(e, "TenantId") == _tenantId)
                     && (_isGroupScope || EF.Property<Guid?>(e, "CompanyId") == null
                         || _companyScopeIds.Contains(EF.Property<Guid?>(e, "CompanyId")!.Value)));
        else
            modelBuilder.Entity<TEntity>().HasQueryFilter(
                e => _tenantId == null || EF.Property<Guid>(e, "TenantId") == _tenantId);
    }

    private void SetTenantFilterNullable<TEntity>(ModelBuilder modelBuilder) where TEntity : class
    {
        var hasSoftDelete = typeof(TEntity).GetProperty("IsDeleted") != null;
        var isOperationalScope = typeof(ICompanyScopedOperational).IsAssignableFrom(typeof(TEntity));
        var isConfigScope = !isOperationalScope && typeof(ICompanyScoped).IsAssignableFrom(typeof(TEntity));

        if (hasSoftDelete && isOperationalScope)
            modelBuilder.Entity<TEntity>().HasQueryFilter(
                e => (_tenantId == null || EF.Property<Guid?>(e, "TenantId") == _tenantId)
                     && !EF.Property<bool>(e, "IsDeleted")
                     && (_isGroupScope || (EF.Property<Guid?>(e, "CompanyId") != null
                         && _companyScopeIds.Contains(EF.Property<Guid?>(e, "CompanyId")!.Value))));
        else if (hasSoftDelete && isConfigScope)
            modelBuilder.Entity<TEntity>().HasQueryFilter(
                e => (_tenantId == null || EF.Property<Guid?>(e, "TenantId") == _tenantId)
                     && !EF.Property<bool>(e, "IsDeleted")
                     && (_isGroupScope || EF.Property<Guid?>(e, "CompanyId") == null
                         || _companyScopeIds.Contains(EF.Property<Guid?>(e, "CompanyId")!.Value)));
        else if (hasSoftDelete)
            modelBuilder.Entity<TEntity>().HasQueryFilter(
                e => (_tenantId == null || EF.Property<Guid?>(e, "TenantId") == _tenantId)
                     && !EF.Property<bool>(e, "IsDeleted"));
        else if (isOperationalScope)
            modelBuilder.Entity<TEntity>().HasQueryFilter(
                e => (_tenantId == null || EF.Property<Guid?>(e, "TenantId") == _tenantId)
                     && (_isGroupScope || (EF.Property<Guid?>(e, "CompanyId") != null
                         && _companyScopeIds.Contains(EF.Property<Guid?>(e, "CompanyId")!.Value))));
        else if (isConfigScope)
            modelBuilder.Entity<TEntity>().HasQueryFilter(
                e => (_tenantId == null || EF.Property<Guid?>(e, "TenantId") == _tenantId)
                     && (_isGroupScope || EF.Property<Guid?>(e, "CompanyId") == null
                         || _companyScopeIds.Contains(EF.Property<Guid?>(e, "CompanyId")!.Value)));
        else
            modelBuilder.Entity<TEntity>().HasQueryFilter(
                e => _tenantId == null || EF.Property<Guid?>(e, "TenantId") == _tenantId);
    }

    private static void ApplySnakeCaseColumns(ModelBuilder modelBuilder)
    {
        foreach (var entity in modelBuilder.Model.GetEntityTypes())
        {
            foreach (var property in entity.GetProperties())
            {
                property.SetColumnName(ToSnakeCase(property.Name));
            }
        }
    }

    private static string ToSnakeCase(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return value;
        var builder = new StringBuilder(value.Length + 8);
        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];
            if (char.IsUpper(character) && index > 0) builder.Append('_');
            builder.Append(char.ToLowerInvariant(character));
        }
        return builder.ToString();
    }
}
