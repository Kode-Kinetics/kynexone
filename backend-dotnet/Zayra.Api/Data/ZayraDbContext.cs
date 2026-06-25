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

    // Company scope — derived lazily from JWT entity_access claims.
    // True when no HTTP context (admin/background work) or user has group-level access.
    private bool _isGroupScope
    {
        get
        {
            var user = _httpContextAccessor?.HttpContext?.User;
            if (user is null) return true;
            return EntityScopeContext.FromClaims(user, _scopeOptions?.Value.StrictMode ?? false).IsGroupLevel;
        }
    }

    // Explicit company IDs the current user may access. Empty when _isGroupScope=true.
    private List<Guid> _companyScopeIds
    {
        get
        {
            var user = _httpContextAccessor?.HttpContext?.User;
            if (user is null) return [];
            return EntityScopeContext.FromClaims(user, _scopeOptions?.Value.StrictMode ?? false)
                .AccessibleCompanyIds.ToList();
        }
    }

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
        }
        return await base.SaveChangesAsync(cancellationToken);
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
    public DbSet<BankTransferFile> BankTransferFiles => Set<BankTransferFile>();
    public DbSet<WPSFileBatch> WPSFileBatches => Set<WPSFileBatch>();
    public DbSet<SIFFileRecord> SIFFileRecords => Set<SIFFileRecord>();
    public DbSet<EOSBCalculation> EOSBCalculations => Set<EOSBCalculation>();
    public DbSet<PayrollAuditLog> PayrollAuditLogs => Set<PayrollAuditLog>();
    public DbSet<ShiftDefinition> ShiftDefinitions => Set<ShiftDefinition>();
    public DbSet<ShiftAssignment> ShiftAssignments => Set<ShiftAssignment>();
    public DbSet<ManpowerRequisition> ManpowerRequisitions => Set<ManpowerRequisition>();
    public DbSet<JobOpening> JobOpenings => Set<JobOpening>();
    public DbSet<Candidate> Candidates => Set<Candidate>();
    public DbSet<JobApplication> JobApplications => Set<JobApplication>();
    public DbSet<ApplicationEvent> ApplicationEvents => Set<ApplicationEvent>();
    public DbSet<InterviewSchedule> InterviewSchedules => Set<InterviewSchedule>();
    public DbSet<OfferLetter> OfferLetters => Set<OfferLetter>();
    public DbSet<DispatchOrder> DispatchOrders => Set<DispatchOrder>();
    public DbSet<DeliveryRoute> DeliveryRoutes => Set<DeliveryRoute>();
    public DbSet<LastMileStop> LastMileStops => Set<LastMileStop>();
    public DbSet<FleetShipment> FleetShipments => Set<FleetShipment>();
    public DbSet<FleetVehicle> FleetVehicles => Set<FleetVehicle>();
    public DbSet<FleetTrackingPoint> FleetTrackingPoints => Set<FleetTrackingPoint>();
    public DbSet<FleetMaintenanceTicket> FleetMaintenanceTickets => Set<FleetMaintenanceTicket>();
    public DbSet<FleetFuelEvent> FleetFuelEvents => Set<FleetFuelEvent>();
    public DbSet<ShipmentStop> ShipmentStops => Set<ShipmentStop>();
    public DbSet<ProofOfDelivery> ProofOfDeliveries => Set<ProofOfDelivery>();
    public DbSet<CustomerTrackingLink> CustomerTrackingLinks => Set<CustomerTrackingLink>();
    public DbSet<ShipmentEvent> ShipmentEvents => Set<ShipmentEvent>();
    public DbSet<DriverTask> DriverTasks => Set<DriverTask>();
    public DbSet<Carrier> Carriers => Set<Carrier>();
    public DbSet<CarrierContact> CarrierContacts => Set<CarrierContact>();
    public DbSet<CarrierPerformanceScore> CarrierPerformanceScores => Set<CarrierPerformanceScore>();
    public DbSet<ShipmentCarrierAssignment> ShipmentCarrierAssignments => Set<ShipmentCarrierAssignment>();
    public DbSet<BookingRequest> BookingRequests => Set<BookingRequest>();
    public DbSet<QuoteRequest> QuoteRequests => Set<QuoteRequest>();
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
    public DbSet<Grade> Grades => Set<Grade>();
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
    // ── Reports & Analytics ────────────────────────────────────────────────────
    public DbSet<SavedReport> SavedReports => Set<SavedReport>();
    public DbSet<ReportSchedule> ReportSchedules => Set<ReportSchedule>();
    public DbSet<ReportExecutionLog> ReportExecutionLogs => Set<ReportExecutionLog>();
    // ── Identity & Security ────────────────────────────────────────────────────
    public DbSet<SecuritySetting> SecuritySettings => Set<SecuritySetting>();
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
            entity.HasIndex(x => new { x.TenantId, x.EmployeeCode }).IsUnique();
            entity.HasIndex(x => new { x.TenantId, x.Status });
            entity.HasIndex(x => new { x.TenantId, x.Department });
            entity.HasIndex(x => new { x.TenantId, x.IsDeleted });
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
            entity.Property(x => x.ProposedChangesJson).HasColumnType("json");
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
        });

        modelBuilder.Entity<Grade>(entity =>
        {
            entity.ToTable("grades");
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.TenantId, x.Code }).IsUnique();
            entity.HasIndex(x => new { x.TenantId, x.IsDeleted });
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
            entity.HasIndex(x => new { x.TenantId, x.EntityName, x.EntityId, x.Status });
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

        modelBuilder.Entity<SalaryStructure>(entity => { entity.ToTable("salary_structures"); entity.HasKey(x => x.Id); entity.HasIndex(x => new { x.TenantId, x.Code }).IsUnique(); entity.HasIndex(x => new { x.TenantId, x.CompanyId }); });
        modelBuilder.Entity<SalaryComponent>(entity => { entity.ToTable("salary_components"); entity.HasKey(x => x.Id); entity.Property(x => x.Amount).HasPrecision(14,2); entity.Property(x => x.Percentage).HasPrecision(6,3); entity.HasIndex(x => new { x.TenantId, x.SalaryStructureId, x.Code }); });
        modelBuilder.Entity<EmployeeSalaryStructure>(entity => { entity.ToTable("employee_salary_structures"); entity.HasKey(x => x.Id); entity.Property(x => x.BasicSalary).HasPrecision(14,2); entity.Property(x => x.HousingAllowance).HasPrecision(14,2); entity.Property(x => x.TransportAllowance).HasPrecision(14,2); entity.Property(x => x.FoodAllowance).HasPrecision(14,2); entity.Property(x => x.MobileAllowance).HasPrecision(14,2); entity.Property(x => x.OtherAllowance).HasPrecision(14,2); entity.Property(x => x.FixedDeduction).HasPrecision(14,2); entity.HasIndex(x => new { x.TenantId, x.EmployeeId, x.IsActive }); });
        modelBuilder.Entity<PayrollGroup>(entity => { entity.ToTable("payroll_groups"); entity.HasKey(x => x.Id); entity.HasIndex(x => new { x.TenantId, x.Code }).IsUnique(); });
        modelBuilder.Entity<PayrollCycle>(entity => { entity.ToTable("payroll_cycles"); entity.HasKey(x => x.Id); entity.HasIndex(x => new { x.TenantId, x.Year, x.Month }); });
        modelBuilder.Entity<PayrollRunEmployee>(entity => { entity.ToTable("payroll_run_employees"); entity.HasKey(x => x.Id); entity.Property(x => x.GrossEarnings).HasPrecision(14,2); entity.Property(x => x.TotalDeductions).HasPrecision(14,2); entity.Property(x => x.NetPay).HasPrecision(14,2); entity.HasIndex(x => new { x.TenantId, x.PayrollRunId, x.EmployeeId }).IsUnique(); });
        modelBuilder.Entity<PayrollEarning>(entity => { entity.ToTable("payroll_earnings"); entity.HasKey(x => x.Id); entity.Property(x => x.Amount).HasPrecision(14,2); entity.HasIndex(x => new { x.TenantId, x.PayrollRunId, x.EmployeeId }); });
        modelBuilder.Entity<PayrollDeduction>(entity => { entity.ToTable("payroll_deductions"); entity.HasKey(x => x.Id); entity.Property(x => x.Amount).HasPrecision(14,2); entity.Property(x => x.IsEmployerContribution).HasDefaultValue(false); entity.HasIndex(x => new { x.TenantId, x.PayrollRunId, x.EmployeeId }); });
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
        modelBuilder.Entity<PayrollPaymentBatch>(entity => { entity.ToTable("payroll_payment_batches"); entity.HasKey(x => x.Id); entity.Property(x => x.TotalAmount).HasPrecision(14,2); entity.HasIndex(x => new { x.TenantId, x.PayrollRunId }); });
        modelBuilder.Entity<PayrollPaymentRecord>(entity => { entity.ToTable("payroll_payment_records"); entity.HasKey(x => x.Id); entity.Property(x => x.Amount).HasPrecision(14,2); entity.HasIndex(x => new { x.TenantId, x.PaymentBatchId, x.EmployeeId }); });
        modelBuilder.Entity<BankTransferFile>(entity => { entity.ToTable("bank_transfer_files"); entity.HasKey(x => x.Id); entity.HasIndex(x => new { x.TenantId, x.PaymentBatchId }); });
        modelBuilder.Entity<WPSFileBatch>(entity => { entity.ToTable("wps_file_batches"); entity.HasKey(x => x.Id); entity.HasIndex(x => new { x.TenantId, x.PaymentBatchId }); });
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
            entity.HasIndex(x => new { x.TenantId, x.CompanyId, x.Year, x.Month });
            entity.HasIndex(x => new { x.TenantId, x.CompanyId, x.Status });
            entity.HasIndex(x => new { x.TenantId, x.Year, x.Month }).IsUnique()
                .HasDatabaseName("IX_payroll_runs_tenant_id_year_month")
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

        modelBuilder.Entity<DispatchOrder>(entity =>
        {
            entity.ToTable("dispatch_orders");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.OrderValue).HasPrecision(12, 2);
            entity.HasIndex(x => new { x.TenantId, x.OrderNumber }).IsUnique();
            entity.HasIndex(x => new { x.TenantId, x.Status });
            entity.HasIndex(x => new { x.TenantId, x.RouteCode });
        });

        modelBuilder.Entity<DeliveryRoute>(entity =>
        {
            entity.ToTable("delivery_routes");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.DistanceKm).HasPrecision(10, 2);
            entity.Property(x => x.CompletionPercent).HasPrecision(5, 2);
            entity.HasIndex(x => new { x.TenantId, x.RouteCode }).IsUnique();
            entity.HasIndex(x => new { x.TenantId, x.Status });
        });

        modelBuilder.Entity<LastMileStop>(entity =>
        {
            entity.ToTable("last_mile_stops");
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.TenantId, x.OrderNumber }).IsUnique();
            entity.HasIndex(x => new { x.TenantId, x.RouteCode });
            entity.HasIndex(x => new { x.TenantId, x.Status });
            entity.HasIndex(x => new { x.TenantId, x.EtaUtc });
        });

        modelBuilder.Entity<FleetShipment>(entity =>
        {
            entity.ToTable("fleet_shipments");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.WeightKg).HasPrecision(12, 2);
            entity.Property(x => x.VolumeCbm).HasPrecision(12, 2);
            entity.Property(x => x.DeclaredValue).HasPrecision(12, 2);
            entity.HasIndex(x => new { x.TenantId, x.ShipmentNumber }).IsUnique();
            entity.HasIndex(x => new { x.TenantId, x.Status });
            entity.HasIndex(x => new { x.TenantId, x.RouteCode });
        });

        modelBuilder.Entity<ShipmentStop>(entity =>
        {
            entity.ToTable("shipment_stops");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Latitude).HasPrecision(10, 7);
            entity.Property(x => x.Longitude).HasPrecision(10, 7);
            entity.HasIndex(x => new { x.TenantId, x.ShipmentId, x.SequenceNo }).IsUnique();
            entity.HasIndex(x => new { x.TenantId, x.ShipmentId, x.Status });
        });

        modelBuilder.Entity<ProofOfDelivery>(entity =>
        {
            entity.ToTable("proofs_of_delivery");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.CapturedLatitude).HasPrecision(10, 7);
            entity.Property(x => x.CapturedLongitude).HasPrecision(10, 7);
            entity.HasIndex(x => new { x.TenantId, x.ShipmentId });
            entity.HasIndex(x => new { x.TenantId, x.StopId });
            entity.HasIndex(x => new { x.TenantId, x.Status });
        });

        modelBuilder.Entity<CustomerTrackingLink>(entity =>
        {
            entity.ToTable("customer_tracking_links");
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.TenantId, x.ShipmentId });
            entity.HasIndex(x => new { x.TenantId, x.Token }).IsUnique();
            entity.HasIndex(x => new { x.TenantId, x.IsRevoked, x.ExpiresAtUtc });
        });

        modelBuilder.Entity<ShipmentEvent>(entity =>
        {
            entity.ToTable("shipment_events");
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.TenantId, x.ShipmentId, x.OccurredAtUtc });
            entity.HasIndex(x => new { x.TenantId, x.Visibility });
        });

        modelBuilder.Entity<DriverTask>(entity =>
        {
            entity.ToTable("driver_tasks");
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.TenantId, x.ShipmentId });
            entity.HasIndex(x => new { x.TenantId, x.DriverName, x.Status });
            entity.HasIndex(x => new { x.TenantId, x.DueAtUtc });
        });

        modelBuilder.Entity<Carrier>(entity =>
        {
            entity.ToTable("carriers");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.OnTimeScore).HasPrecision(5, 2);
            entity.Property(x => x.DamageScore).HasPrecision(5, 2);
            entity.Property(x => x.CostScore).HasPrecision(5, 2);
            entity.HasIndex(x => new { x.TenantId, x.Code }).IsUnique();
            entity.HasIndex(x => new { x.TenantId, x.Status });
        });

        modelBuilder.Entity<CarrierContact>(entity =>
        {
            entity.ToTable("carrier_contacts");
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.TenantId, x.CarrierId });
        });

        modelBuilder.Entity<CarrierPerformanceScore>(entity =>
        {
            entity.ToTable("carrier_performance_scores");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.OnTimePct).HasPrecision(5, 2);
            entity.Property(x => x.DamagePct).HasPrecision(5, 2);
            entity.Property(x => x.AcceptancePct).HasPrecision(5, 2);
            entity.Property(x => x.OverallScore).HasPrecision(5, 2);
            entity.HasIndex(x => new { x.TenantId, x.CarrierId, x.ScoredAtUtc });
        });

        modelBuilder.Entity<ShipmentCarrierAssignment>(entity =>
        {
            entity.ToTable("shipment_carrier_assignments");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.QuotedAmount).HasPrecision(14, 2);
            entity.Property(x => x.AgreedAmount).HasPrecision(14, 2);
            entity.HasIndex(x => new { x.TenantId, x.ShipmentId }).IsUnique();
            entity.HasIndex(x => new { x.TenantId, x.CarrierId });
        });

        modelBuilder.Entity<BookingRequest>(entity =>
        {
            entity.ToTable("booking_requests");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.EstimatedWeightKg).HasPrecision(12, 2);
            entity.Property(x => x.EstimatedVolumeCbm).HasPrecision(12, 2);
            entity.HasIndex(x => new { x.TenantId, x.RequestNumber }).IsUnique();
            entity.HasIndex(x => new { x.TenantId, x.Status });
        });

        modelBuilder.Entity<QuoteRequest>(entity =>
        {
            entity.ToTable("quote_requests");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.EstimatedAmount).HasPrecision(14, 2);
            entity.Property(x => x.MarginPct).HasPrecision(5, 2);
            entity.HasIndex(x => new { x.TenantId, x.QuoteNumber }).IsUnique();
            entity.HasIndex(x => new { x.TenantId, x.Status });
        });

        modelBuilder.Entity<FleetVehicle>(entity =>
        {
            entity.ToTable("fleet_vehicles");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.CapacityKg).HasPrecision(12, 2);
            entity.Property(x => x.CapacityCbm).HasPrecision(12, 2);
            entity.Property(x => x.CurrentLoadKg).HasPrecision(12, 2);
            entity.Property(x => x.FuelLevelPercent).HasPrecision(5, 2);
            entity.Property(x => x.OdometerKm).HasPrecision(12, 2);
            entity.Property(x => x.TemperatureCelsius).HasPrecision(5, 2);
            entity.HasIndex(x => new { x.TenantId, x.VehicleNumber }).IsUnique();
            entity.HasIndex(x => new { x.TenantId, x.Status });
            entity.HasIndex(x => new { x.TenantId, x.DriverName });
        });

        modelBuilder.Entity<FleetTrackingPoint>(entity =>
        {
            entity.ToTable("fleet_tracking_points");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Latitude).HasPrecision(10, 7);
            entity.Property(x => x.Longitude).HasPrecision(10, 7);
            entity.Property(x => x.SpeedKph).HasPrecision(8, 2);
            entity.HasIndex(x => new { x.TenantId, x.ShipmentNumber });
            entity.HasIndex(x => new { x.TenantId, x.VehicleNumber });
            entity.HasIndex(x => new { x.TenantId, x.RecordedAtUtc });
        });

        modelBuilder.Entity<FleetMaintenanceTicket>(entity =>
        {
            entity.ToTable("fleet_maintenance_tickets");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.EstimatedCost).HasPrecision(14, 2);
            entity.Property(x => x.ActualCost).HasPrecision(14, 2);
            entity.Property(x => x.DowntimeHours).HasPrecision(8, 2);
            entity.HasIndex(x => new { x.TenantId, x.WorkOrderNumber }).IsUnique();
            entity.HasIndex(x => new { x.TenantId, x.VehicleNumber });
            entity.HasIndex(x => new { x.TenantId, x.Status });
        });

        modelBuilder.Entity<FleetFuelEvent>(entity =>
        {
            entity.ToTable("fleet_fuel_events");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Liters).HasPrecision(12, 2);
            entity.Property(x => x.Cost).HasPrecision(12, 2);
            entity.Property(x => x.OdometerKm).HasPrecision(12, 2);
            entity.HasIndex(x => new { x.TenantId, x.VehicleNumber });
            entity.HasIndex(x => new { x.TenantId, x.AnomalyFlag });
            entity.HasIndex(x => new { x.TenantId, x.RecordedAtUtc });
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
            entity.HasIndex(x => new { x.TenantId, x.CreatedAtUtc });
            entity.HasIndex(x => new { x.UserId, x.CreatedAtUtc });
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
            entity.HasIndex(x => new { x.TenantId, x.CountryCode }).IsUnique();
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
            entity.HasIndex(x => new { x.TenantId, x.SourceModule, x.SourceEntityId });
            entity.HasIndex(x => new { x.TenantId, x.Period });
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
    private void SetTenantFilterNonNull<TEntity>(ModelBuilder modelBuilder) where TEntity : class
    {
        var hasSoftDelete = typeof(TEntity).GetProperty("IsDeleted") != null;
        var hasCompanyScope = typeof(ICompanyScoped).IsAssignableFrom(typeof(TEntity));

        if (hasSoftDelete && hasCompanyScope)
            modelBuilder.Entity<TEntity>().HasQueryFilter(
                e => (_tenantId == null || EF.Property<Guid>(e, "TenantId") == _tenantId)
                     && !EF.Property<bool>(e, "IsDeleted")
                     && (_isGroupScope || EF.Property<Guid?>(e, "CompanyId") == null
                         || _companyScopeIds.Contains(EF.Property<Guid?>(e, "CompanyId")!.Value)));
        else if (hasSoftDelete)
            modelBuilder.Entity<TEntity>().HasQueryFilter(
                e => (_tenantId == null || EF.Property<Guid>(e, "TenantId") == _tenantId)
                     && !EF.Property<bool>(e, "IsDeleted"));
        else if (hasCompanyScope)
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
        var hasCompanyScope = typeof(ICompanyScoped).IsAssignableFrom(typeof(TEntity));

        if (hasSoftDelete && hasCompanyScope)
            modelBuilder.Entity<TEntity>().HasQueryFilter(
                e => (_tenantId == null || EF.Property<Guid?>(e, "TenantId") == _tenantId)
                     && !EF.Property<bool>(e, "IsDeleted")
                     && (_isGroupScope || EF.Property<Guid?>(e, "CompanyId") == null
                         || _companyScopeIds.Contains(EF.Property<Guid?>(e, "CompanyId")!.Value)));
        else if (hasSoftDelete)
            modelBuilder.Entity<TEntity>().HasQueryFilter(
                e => (_tenantId == null || EF.Property<Guid?>(e, "TenantId") == _tenantId)
                     && !EF.Property<bool>(e, "IsDeleted"));
        else if (hasCompanyScope)
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
