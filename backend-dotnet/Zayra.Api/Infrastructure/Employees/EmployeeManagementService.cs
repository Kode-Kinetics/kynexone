using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Auth;
using Zayra.Api.Application.Common;
using Zayra.Api.Application.Employees;
using Zayra.Api.Application.Organization;
using Zayra.Api.Controllers;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Documents;
using Zayra.Api.Infrastructure.Notifications;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Employees;

public class EmployeeManagementService : IEmployeeManagementService
{
    // Qiwa-ready required document types (SA labour + GCC compliance).
    private static readonly string[] RequiredDocumentTypes =
    [
        "Passport", "Visa", "Contract", "Bank letter",
        "Iqama", "Work Permit", "National ID", "Residence Permit",
        "Offer Letter", "NDA"
    ];

    private readonly ZayraDbContext _db;
    private readonly IAuditService _audit;
    private readonly IDocumentStorage _documents;
    private readonly INotificationService _notifications;
    private readonly IEstablishmentGuard _establishmentGuard;
    private readonly IEmployeeActivationGuard _activationGuard;

    public EmployeeManagementService(ZayraDbContext db, IAuditService audit, IDocumentStorage documents, INotificationService notifications, IEstablishmentGuard? establishmentGuard = null, IEmployeeActivationGuard? activationGuard = null)
    {
        _db = db;
        _audit = audit;
        _documents = documents;
        _notifications = notifications;
        // Optional with a concrete fallback: DI always supplies the registered guard in production
        // (Program.cs); the fallback keeps the many direct-construction test call sites compiling
        // and STILL ENFORCING (same pattern as EmployeesController's ApprovalWorkflowService).
        _establishmentGuard = establishmentGuard ?? new Zayra.Api.Infrastructure.Organization.EstablishmentGuardService(db);
        // Readiness/activation gate — same optional-with-fallback pattern; self-composes its resolver
        // + evaluator from db so direct-construction test sites keep enforcing.
        _activationGuard = activationGuard ?? new EmployeeActivationGuard(db);
    }

    public async Task<PagedResult<EmployeeListItemDto>> SearchAsync(Guid tenantId, string? search, string? status, string? department, int page, int pageSize, CancellationToken cancellationToken)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 100);
        // Active People list: exclude soft-deleted AND every terminal/former-employee status
        // (Archived / Offboarded / Terminated / Exited) — those live in the Ex-Employees archive.
        var query = _db.Employees.AsNoTracking().Where(x => x.TenantId == tenantId && !x.IsDeleted && !ExitEmployeeStatuses.Exit.Contains(x.Status));
        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim();
            query = query.Where(x => x.EmployeeCode.Contains(term) || x.FullName.Contains(term) || x.EnglishName.Contains(term) || x.ArabicName.Contains(term) || x.WorkEmail.Contains(term));
        }
        if (!string.IsNullOrWhiteSpace(status)) query = query.Where(x => x.Status == status);
        if (!string.IsNullOrWhiteSpace(department)) query = query.Where(x => x.Department == department);

        var total = await query.CountAsync(cancellationToken);
        var items = await query.OrderBy(x => x.EmployeeCode).Skip((page - 1) * pageSize).Take(pageSize)
            .Select(x => new EmployeeListItemDto(x.Id, x.EmployeeCode, x.FullName, x.ArabicName, x.Department, x.Designation, x.Branch, x.ManagerEmployeeId, x.Status, x.ProfileCompletenessScore, x.VisaExpiryDate, x.PassportExpiryDate, x.ReadinessState, x.ActivationBlockersCount))
            .ToListAsync(cancellationToken);
        return new PagedResult<EmployeeListItemDto>(items, total, page, pageSize);
    }

    public async Task<EmployeeDetailDto?> GetAsync(Guid tenantId, int id, bool includeSensitive, RequestContext context, CancellationToken cancellationToken)
    {
        var employee = await _db.Employees.AsNoTracking().FirstOrDefaultAsync(x => x.TenantId == tenantId && x.Id == id && !x.IsDeleted, cancellationToken);
        if (employee is null) return null;
        if (includeSensitive)
            await _audit.WriteAsync("employee.sensitive_viewed", "Employee", id.ToString(), context, null, cancellationToken);

        return EmployeeDetailDto.Project(
            employee,
            includeSensitive,
            includeSensitive ? await _db.EmployeePayrollProfiles.AsNoTracking().FirstOrDefaultAsync(x => x.TenantId == tenantId && x.EmployeeId == id && !x.IsDeleted, cancellationToken) : null,
            includeSensitive ? await _db.EmployeeComplianceRecords.AsNoTracking().Where(x => x.TenantId == tenantId && x.EmployeeId == id && !x.IsDeleted).ToListAsync(cancellationToken) : [],
            await GetDocumentsAsync(tenantId, id, cancellationToken),
            await GetHistoryAsync(tenantId, id, cancellationToken),
            await _db.EmployeeTransferRequests.AsNoTracking().Where(x => x.TenantId == tenantId && x.EmployeeId == id).OrderByDescending(x => x.CreatedAtUtc).ToListAsync(cancellationToken));
    }

    public async Task<EmployeeDetailDto> CreateAsync(Guid tenantId, EmployeeCreateRequest request, RequestContext context, CancellationToken cancellationToken)
    {
        var code = request.ManualEmployeeCode ? Clean(request.EmployeeCode) : await GenerateEmployeeCode(tenantId, request, context, cancellationToken);
        if (string.IsNullOrWhiteSpace(code)) throw new InvalidOperationException("Employee code is required.");
        if (await _db.Employees.AnyAsync(x => x.TenantId == tenantId && x.EmployeeCode == code && !x.IsDeleted, cancellationToken))
        {
            throw new InvalidOperationException("Employee code already exists in this tenant.");
        }

        var employee = new Employee { TenantId = tenantId, EmployeeCode = code, CreatedBy = context.UserId };
        await ApplyEmployee(employee, request, tenantId, cancellationToken);
        // Employees must always resolve to a legal entity: operational company scoping
        // treats null CompanyId as invisible to scoped users, so default it here.
        employee.CompanyId ??= await ResolveDefaultCompanyId(tenantId, cancellationToken);
        await ValidatePositionAndSalaryAsync(employee, request.SalaryBreakdown, tenantId, cancellationToken);
        // Reject a bad IBAN BEFORE persisting anything (position/salary already validate pre-save), so a
        // create never leaves a half-saved Draft when the bank details fail the checksum. UpsertPayrollProfile
        // below is the backstop for other callers.
        var createIban = Clean(request.PayrollProfile?.Iban);
        if (!string.IsNullOrWhiteSpace(createIban) && !Zayra.Api.Infrastructure.Payroll.IbanValidator.IsValid(createIban))
            throw new InvalidOperationException($"IBAN '{createIban}' is invalid — it fails the ISO 13616 mod-97 checksum. Enter a correct IBAN before saving.");
        employee.Status = "Draft";
        employee.ProfileCompletenessScore = CalculateCompleteness(employee, request.PayrollProfile, request.ComplianceRecords);
        // ESTABLISHMENT GUARD (path "create"): hard-enforced at the form save even though a Draft
        // does not occupy a seat — a Draft that could never activate would be a wasted data-entry
        // session. The authoritative consume re-check happens at the occupying transition
        // (ChangeStatusAsync). Lock + enforce + persist run atomically under the execution
        // strategy; over-budget throws EstablishmentBudgetExceededException → structured 409.
        await _establishmentGuard.EnforceAndExecuteAsync(tenantId, employee.DepartmentId, employee.DesignationId,
            excludeEmployeeId: null, path: "create", context, async () =>
            {
                _db.Employees.Add(employee);
                await _db.SaveChangesAsync(cancellationToken);
                await EnsurePrimaryReportingLineAsync(employee, context, cancellationToken);
                await SynchronizePositionIncumbencyAsync(employee, null, context, cancellationToken);

                await UpsertPayrollProfile(employee, request.PayrollProfile, context, cancellationToken);
                await UpsertEmployeeSalaryStructure(employee, request.SalaryBreakdown, context, cancellationToken);
                await UpsertComplianceRecords(employee, request.ComplianceRecords ?? [], context, cancellationToken);
                await AddHistory(employee, "Created", "Employee", string.Empty, employee.EmployeeCode, DateOnly.FromDateTime(DateTime.UtcNow), "Employee created", context, cancellationToken);
                await _db.SaveChangesAsync(cancellationToken);
                return true;
            }, cancellationToken);
        // Stamp the readiness badge now that the employee + payroll/compliance rows are persisted.
        await RefreshReadinessSnapshotAsync(employee, cancellationToken);
        await _db.SaveChangesAsync(cancellationToken);
        await _audit.WriteAsync("employee.created", "Employee", employee.Id.ToString(), context, null, cancellationToken);
        return (await GetAsync(tenantId, employee.Id, true, context, cancellationToken))!;
    }

    public async Task<EmployeeDetailDto?> UpdateAsync(Guid tenantId, int id, EmployeeCreateRequest request, RequestContext context, CancellationToken cancellationToken)
    {
        var employee = await _db.Employees.FirstOrDefaultAsync(x => x.TenantId == tenantId && x.Id == id && !x.IsDeleted, cancellationToken);
        if (employee is null) return null;
        var priorPositionId = employee.PositionId;
        TrackChange(employee, "Department", employee.Department, request.DepartmentId?.ToString() ?? employee.Department, request.JoiningDate, "Department update", context);
        TrackChange(employee, "Designation", employee.Designation, request.DesignationId?.ToString() ?? employee.Designation, request.JoiningDate, "Designation update", context);
        TrackChange(employee, "Manager", employee.ManagerEmployeeId?.ToString() ?? "", request.ReportingManagerEmployeeId?.ToString() ?? "", request.JoiningDate, "Manager update", context);
        TrackChange(employee, "Grade", employee.Grade, request.GradeId?.ToString() ?? employee.Grade, request.JoiningDate, "Grade update", context);

        var priorDepartmentId = employee.DepartmentId;
        var priorDesignationId = employee.DesignationId;
        await ApplyEmployee(employee, request, tenantId, cancellationToken);
        await ValidatePositionAndSalaryAsync(employee, request.SalaryBreakdown, tenantId, cancellationToken);
        employee.UpdatedAtUtc = DateTime.UtcNow;
        employee.UpdatedBy = context.UserId;
        employee.ProfileCompletenessScore = CalculateCompleteness(employee, request.PayrollProfile, request.ComplianceRecords);
        await UpsertPayrollProfile(employee, request.PayrollProfile, context, cancellationToken);
        await UpsertEmployeeSalaryStructure(employee, request.SalaryBreakdown, context, cancellationToken);
        await UpsertComplianceRecords(employee, request.ComplianceRecords ?? [], context, cancellationToken);
        // ESTABLISHMENT GUARD (path "update"): fires ONLY when the (department, designation) pair
        // actually changed — an unrelated edit (phone number, IBAN, …) can never trip it, and an
        // over-budget department is never invalidated by editing its existing employees
        // (grandfathering). The employee excludes themself from the target count (idempotent
        // re-saves and promotions-within-level are free).
        if (employee.DepartmentId != priorDepartmentId || employee.DesignationId != priorDesignationId)
        {
            await _establishmentGuard.EnforceAndExecuteAsync(tenantId, employee.DepartmentId, employee.DesignationId,
                excludeEmployeeId: employee.Id, path: "update", context, async () =>
                {
                    await _db.SaveChangesAsync(cancellationToken);
                    await SynchronizePositionIncumbencyAsync(employee, priorPositionId, context, cancellationToken);
                    return true;
                }, cancellationToken);
        }
        else
        {
            await _db.SaveChangesAsync(cancellationToken);
            await SynchronizePositionIncumbencyAsync(employee, priorPositionId, context, cancellationToken);
        }
        await RefreshReadinessSnapshotAsync(employee, cancellationToken);
        await _db.SaveChangesAsync(cancellationToken);
        await _audit.WriteAsync("employee.updated", "Employee", id.ToString(), context, null, cancellationToken);
        return await GetAsync(tenantId, id, true, context, cancellationToken);
    }

    public async Task<EmployeeDetailDto?> ChangeStatusAsync(Guid tenantId, int id, EmployeeStatusChangeRequest request, RequestContext context, CancellationToken cancellationToken)
    {
        var employee = await _db.Employees.FirstOrDefaultAsync(x => x.TenantId == tenantId && x.Id == id && !x.IsDeleted, cancellationToken);
        if (employee is null) return null;
        var oldStatus = employee.Status;
        // READINESS GATE (§5.2/§5.3): fire ONLY when becoming Active from a NON-occupying status —
        // first-time activations and rehires (Draft/Invited/Terminated/…→Active). Runs BEFORE any
        // mutation so a block leaves the record untouched (the guard is a pure read; it throws
        // EmployeeActivationBlockedException). Suspended→Active / Offboarded→Active reinstatements are
        // occupying→occupying and are deliberately NOT gated (mandated reinstatement is never refused).
        if (_activationGuard.ShouldGate(oldStatus, request.Status))
        {
            var gateSnapshot = await _activationGuard.BuildSnapshotAsync(tenantId, id, cancellationToken);
            if (gateSnapshot is not null)
                await _activationGuard.EnsureActivatableAsync(tenantId, employee.CompanyId, gateSnapshot, context, cancellationToken);
        }
        employee.Status = request.Status;
        employee.UpdatedAtUtc = DateTime.UtcNow;
        employee.UpdatedBy = context.UserId;
        _db.EmployeeStatusHistories.Add(new EmployeeStatusHistory
        {
            TenantId = tenantId,
            EmployeeId = id,
            OldStatus = oldStatus,
            NewStatus = request.Status,
            EffectiveDate = request.EffectiveDate,
            Reason = request.Reason,
            ChangedByUserId = context.UserId
        });
        await AddHistory(employee, "StatusChange", "Status", oldStatus, request.Status, request.EffectiveDate, request.Reason, context, cancellationToken);
        // Stamp ActivatedAtUtc on the first successful activation (the gate above already passed).
        if (string.Equals(request.Status, EmployeeStatuses.Active, StringComparison.OrdinalIgnoreCase) && employee.ActivatedAtUtc is null)
            employee.ActivatedAtUtc = DateTime.UtcNow;
        // ESTABLISHMENT GUARD (path "reactivate"): only a transition INTO an occupying status
        // (Draft/Invited/Terminated/… → Active, activation, rehire) consumes a seat and is
        // enforced. Occupying→occupying (e.g. Suspended→Active reinstatement, Active→Offboarded
        // notice) and transitions OUT of occupancy never call the guard.
        if (EstablishmentOccupancy.IsOccupyingStatus(request.Status) && !EstablishmentOccupancy.IsOccupyingStatus(oldStatus))
        {
            await _establishmentGuard.EnforceAndExecuteAsync(tenantId, employee.DepartmentId, employee.DesignationId,
                excludeEmployeeId: employee.Id, path: "reactivate", context, async () =>
                {
                    await _db.SaveChangesAsync(cancellationToken);
                    return true;
                }, cancellationToken);
        }
        else
        {
            await _db.SaveChangesAsync(cancellationToken);
        }
        await RefreshReadinessSnapshotAsync(employee, cancellationToken);
        await _db.SaveChangesAsync(cancellationToken);
        await _audit.WriteAsync("employee.status_changed", "Employee", id.ToString(), context, JsonSerializer.Serialize(new { oldStatus, request.Status, request.Reason }), cancellationToken);

        // EXIT CASCADE (terminate / direct status change into a terminal state): deactivate the
        // WPS footprint so an ex-employee cannot be swept into a SIF export. The salary STRUCTURE is
        // intentionally left active here — CalculateEosb / FinalSettlement (PayrollController) read the
        // active salary row and are computed for a Terminated (still non-deleted) employee, so
        // deactivating it now would corrupt end-of-service / final-settlement money. It is deactivated
        // instead at the points where those calculators can no longer read it (soft-delete;
        // offboarding-complete once final settlement is done). Idempotent — safe to re-run.
        if (ExitEmployeeStatuses.PayrollDeactivation.Contains(request.Status, StringComparer.OrdinalIgnoreCase))
            await DeactivatePayrollFootprintAsync(_db, _audit, tenantId, id,
                $"status:{oldStatus}->{request.Status}", deactivateSalaryStructure: false, context, cancellationToken);

        return await GetAsync(tenantId, id, true, context, cancellationToken);
    }

    /// <summary>
    /// Exit cascade primitive: deactivates an employee's payroll footprint on separation so nothing
    /// dangles active. Always marks the payroll profile WPS-ineligible; deactivates the salary
    /// structure(s) only when <paramref name="deactivateSalaryStructure"/> is set (the caller has
    /// established the end-of-service / final-settlement calculators can no longer read the row).
    /// <see cref="EmployeePayrollProfile.EosbEligible"/> is never touched — gratuity is paid ON exit.
    /// Both updates are guarded (WpsEligible / IsActive), so re-running (e.g. terminate then delete)
    /// is a no-op. Static so the soft-delete (EmployeesController) and offboarding-complete
    /// (OffboardingController) paths can share the exact same primitive without an interface change.
    /// </summary>
    public static async Task<int> DeactivatePayrollFootprintAsync(
        ZayraDbContext db, IAuditService audit, Guid tenantId, int employeeId,
        string reason, bool deactivateSalaryStructure, RequestContext context, CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;

        // Tracked-entity update (not ExecuteUpdate): the EF InMemory provider used by the test suite
        // cannot translate ExecuteUpdate, and this cascade runs inside offboarding/terminate/delete
        // paths that ARE InMemory-tested. Idempotency is preserved by the WHERE clauses selecting only
        // rows that still need changing, so re-running (e.g. terminate then delete) is a no-op.

        // WPS ineligibility — safe on every exit path; a second gate against a stale profile reaching
        // a SIF file. EosbEligible is deliberately left untouched — gratuity is paid ON exit.
        var profiles = await db.EmployeePayrollProfiles
            .Where(x => x.TenantId == tenantId && x.EmployeeId == employeeId && !x.IsDeleted && x.WpsEligible)
            .ToListAsync(cancellationToken);
        foreach (var profile in profiles)
        {
            profile.WpsEligible = false;
            profile.UpdatedAtUtc = now;
            profile.UpdatedBy = context.UserId;
        }

        // Deactivate ALL active salary-structure assignments (including future-dated) so no live
        // package of any effective date survives. Guarded on IsActive → idempotent.
        var salaryDeactivated = 0;
        if (deactivateSalaryStructure)
        {
            var structures = await db.EmployeeSalaryStructures
                .Where(x => x.TenantId == tenantId && x.EmployeeId == employeeId && x.IsActive)
                .ToListAsync(cancellationToken);
            foreach (var structure in structures)
                structure.IsActive = false;
            salaryDeactivated = structures.Count;
        }

        var profileDeactivated = profiles.Count;
        if (profileDeactivated > 0 || salaryDeactivated > 0)
        {
            await db.SaveChangesAsync(cancellationToken);
            await audit.WriteAsync("employee.payroll_footprint_deactivated", "Employee", employeeId.ToString(),
                context, JsonSerializer.Serialize(new { reason, salaryDeactivated, profileDeactivated }), cancellationToken);
        }

        return profileDeactivated + salaryDeactivated;
    }


    public async Task<EmployeeDocument> UploadDocumentAsync(Guid tenantId, int employeeId, EmployeeDocumentUploadMetadata request, IFormFile file, RequestContext context, CancellationToken cancellationToken)
    {
        if (!await _db.Employees.AnyAsync(x => x.TenantId == tenantId && x.Id == employeeId && !x.IsDeleted, cancellationToken)) throw new InvalidOperationException("Employee not found.");
        var stored = await _documents.SaveAsync(tenantId, file, cancellationToken);
        var existingVersions = await _db.EmployeeDocuments.CountAsync(x => x.TenantId == tenantId && x.EmployeeId == employeeId && x.DocumentType == request.DocumentType, cancellationToken);
        var document = new EmployeeDocument
        {
            TenantId = tenantId,
            EmployeeId = employeeId,
            DocumentType = request.DocumentType.Trim(),
            DocumentCategory = Clean(request.DocumentCategory),
            FileName = stored.FileName,
            ContentType = stored.ContentType,
            StorageUrl = stored.StorageUrl,
            IsRequired = request.IsRequired,
            IssueDate = request.IssueDate,
            ExpiryDate = request.ExpiryDate,
            RenewalReminderDate = request.RenewalReminderDate,
            ApprovalStatus = string.IsNullOrWhiteSpace(request.ApprovalStatus) ? "Pending" : request.ApprovalStatus.Trim(),
            Notes = Clean(request.Notes),
            VersionNumber = existingVersions + 1,
            UploadedBy = context.UserId
        };
        _db.EmployeeDocuments.Add(document);
        _db.EmployeeDocumentVersions.Add(new EmployeeDocumentVersion { TenantId = tenantId, EmployeeDocumentId = document.Id, VersionNumber = document.VersionNumber, FileName = document.FileName, ContentType = document.ContentType, StorageUrl = document.StorageUrl, CreatedBy = context.UserId });
        await AddHistory(new Employee { Id = employeeId, TenantId = tenantId }, "DocumentRenewal", "Document", string.Empty, document.DocumentType, DateOnly.FromDateTime(DateTime.UtcNow), "Document uploaded", context, cancellationToken);
        await _db.SaveChangesAsync(cancellationToken);
        // A newly-uploaded doc can satisfy a doc:{Type} requirement — refresh the readiness badge.
        await RefreshEmployeeReadinessByIdAsync(tenantId, employeeId, cancellationToken);
        await _audit.WriteAsync("employee.document_uploaded", "EmployeeDocument", document.Id.ToString(), context, null, cancellationToken);
        return document;
    }

    public async Task<IReadOnlyCollection<EmployeeDocument>> GetDocumentsAsync(Guid tenantId, int employeeId, CancellationToken cancellationToken)
    {
        return await _db.EmployeeDocuments.AsNoTracking().Where(x => x.TenantId == tenantId && x.EmployeeId == employeeId && !x.IsDeleted).OrderBy(x => x.DocumentType).ThenByDescending(x => x.UploadedAtUtc).ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyCollection<EmployeeHistory>> GetHistoryAsync(Guid tenantId, int employeeId, CancellationToken cancellationToken)
    {
        return await _db.EmployeeHistories.AsNoTracking().Where(x => x.TenantId == tenantId && x.EmployeeId == employeeId).OrderByDescending(x => x.CreatedAtUtc).ToListAsync(cancellationToken);
    }

    public async Task<EmployeeTransferRequest?> RequestTransferAsync(Guid tenantId, int employeeId, EmployeeTransferCreateRequest request, RequestContext context, CancellationToken cancellationToken)
    {
        var employee = await _db.Employees.FirstOrDefaultAsync(x => x.TenantId == tenantId && x.Id == employeeId && !x.IsDeleted, cancellationToken);
        if (employee is null) return null;

        // Transfer-integrity fix (Batch A / Conflict R7): resolve free-text targets to IDs AT
        // REQUEST TIME so a transfer can never manufacture a string-only (uncountable) employee.
        // A non-empty name that resolves to nothing throws → 422 naming the missing master data.
        // Legacy display strings are kept alongside the IDs.
        Guid? newDepartmentId = null; var newDepartmentName = Clean(request.NewDepartment);
        if (!string.IsNullOrWhiteSpace(newDepartmentName))
            (newDepartmentId, newDepartmentName) = await EmployeeOrgFieldResolver.ResolveDepartmentAsync(_db, tenantId, newDepartmentName, cancellationToken);
        Guid? newBranchId = null; var newBranchName = Clean(request.NewBranch);
        if (!string.IsNullOrWhiteSpace(newBranchName))
            (newBranchId, newBranchName) = await EmployeeOrgFieldResolver.ResolveBranchAsync(_db, tenantId, newBranchName, cancellationToken);
        Guid? newDesignationId = null; var newDesignationTitle = Clean(request.NewDesignation);
        if (!string.IsNullOrWhiteSpace(newDesignationTitle))
            (newDesignationId, newDesignationTitle) = await EmployeeOrgFieldResolver.ResolveDesignationAsync(_db, tenantId, newDesignationTitle, cancellationToken);

        var transfer = new EmployeeTransferRequest
        {
            TenantId = tenantId,
            EmployeeId = employeeId,
            CurrentBranch = employee.Branch,
            CurrentDepartment = employee.Department,
            CurrentDesignation = employee.Designation,
            CurrentDepartmentId = employee.DepartmentId,
            CurrentManagerEmployeeId = employee.ManagerEmployeeId,
            NewBranch = newBranchName,
            NewDepartment = newDepartmentName,
            NewDesignation = newDesignationTitle,
            NewBranchId = newBranchId,
            NewDepartmentId = newDepartmentId,
            NewDesignationId = newDesignationId,
            NewManagerEmployeeId = request.NewManagerEmployeeId,
            EffectiveDate = request.EffectiveDate,
            Reason = Clean(request.Reason),
            RequestedByUserId = context.UserId
        };
        _db.EmployeeTransferRequests.Add(transfer);
        await _db.SaveChangesAsync(cancellationToken);
        await _audit.WriteAsync("employee.transfer_requested", "EmployeeTransferRequest", transfer.Id.ToString(), context, null, cancellationToken);
        return transfer;
    }

    public Task<EmployeeDetailDto?> ActivateAsync(Guid tenantId, int employeeId, EmployeeStatusChangeRequest request, RequestContext context, CancellationToken cancellationToken)
        => ChangeStatusAsync(tenantId, employeeId, request with { Status = "Active" }, context, cancellationToken);

    public Task<EmployeeDetailDto?> TerminateAsync(Guid tenantId, int employeeId, EmployeeStatusChangeRequest request, RequestContext context, CancellationToken cancellationToken)
        => ChangeStatusAsync(tenantId, employeeId, request with { Status = "Terminated" }, context, cancellationToken);

    public async Task<EmployeeHeadcountReportDto> HeadcountAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        var employees = _db.Employees.AsNoTracking().Where(x => x.TenantId == tenantId && !x.IsDeleted);
        return new EmployeeHeadcountReportDto(
            await employees.CountAsync(cancellationToken),
            await employees.GroupBy(x => x.Branch).Select(x => new EmployeeGroupCountDto(x.Key, x.Count())).ToListAsync(cancellationToken),
            await employees.GroupBy(x => x.Department).Select(x => new EmployeeGroupCountDto(x.Key, x.Count())).ToListAsync(cancellationToken),
            await employees.GroupBy(x => x.Status).Select(x => new EmployeeGroupCountDto(x.Key, x.Count())).ToListAsync(cancellationToken));
    }

    public async Task<IReadOnlyCollection<EmployeeExpiringDocumentDto>> ExpiringDocumentsAsync(Guid tenantId, int days, CancellationToken cancellationToken)
    {
        var until = DateOnly.FromDateTime(DateTime.UtcNow.Date).AddDays(Math.Clamp(days, 1, 365));
        return await _db.EmployeeDocuments.AsNoTracking()
            .Where(x => x.TenantId == tenantId && !x.IsDeleted && x.EmployeeId != null && x.ExpiryDate != null && x.ExpiryDate <= until)
            .Join(_db.Employees.AsNoTracking(), d => d.EmployeeId!.Value, e => e.Id, (d, e) => new { d, e })
            .Where(x => x.e.TenantId == tenantId && !x.e.IsDeleted)
            .Select(x => new EmployeeExpiringDocumentDto(x.e.Id, x.e.EmployeeCode, x.e.FullName, x.d.DocumentType, x.d.ExpiryDate))
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyCollection<EmployeeMissingDocumentsReportDto>> MissingDocumentsAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        var employees = await _db.Employees.AsNoTracking().Where(x => x.TenantId == tenantId && !x.IsDeleted).ToListAsync(cancellationToken);
        var docs = await _db.EmployeeDocuments.AsNoTracking().Where(x => x.TenantId == tenantId && x.EmployeeId != null && !x.IsDeleted).ToListAsync(cancellationToken);
        return employees.Select(employee =>
        {
            var existing = docs.Where(x => x.EmployeeId == employee.Id).Select(x => x.DocumentType).ToHashSet(StringComparer.OrdinalIgnoreCase);
            return new EmployeeMissingDocumentsReportDto(employee.Id, employee.EmployeeCode, employee.FullName, RequiredDocumentTypes.Where(x => !existing.Contains(x)).ToList());
        }).Where(x => x.MissingDocumentTypes.Count > 0).ToList();
    }

    public async Task<EmployeeDocument?> UpdateDocumentAsync(Guid tenantId, int employeeId, Guid documentId, UpdateDocumentMetadataRequest request, RequestContext context, CancellationToken cancellationToken)
    {
        var doc = await _db.EmployeeDocuments.FirstOrDefaultAsync(x => x.TenantId == tenantId && x.Id == documentId && x.EmployeeId == employeeId && !x.IsDeleted, cancellationToken);
        if (doc is null) return null;

        if (!string.IsNullOrWhiteSpace(request.DocumentType)) doc.DocumentType = request.DocumentType.Trim();
        if (request.DocumentCategory is not null) doc.DocumentCategory = request.DocumentCategory.Trim();
        if (request.IssueDate.HasValue) doc.IssueDate = request.IssueDate;
        if (request.ExpiryDate.HasValue) doc.ExpiryDate = request.ExpiryDate;
        if (request.RenewalReminderDate.HasValue) doc.RenewalReminderDate = request.RenewalReminderDate;
        if (request.IsRequired.HasValue) doc.IsRequired = request.IsRequired.Value;
        if (request.Notes is not null) doc.Notes = request.Notes.Trim();

        await _db.SaveChangesAsync(cancellationToken);
        await _audit.WriteAsync("employee.document_updated", "EmployeeDocument", doc.Id.ToString(), context,
            JsonSerializer.Serialize(new { documentType = doc.DocumentType, category = doc.DocumentCategory, expiryDate = doc.ExpiryDate }), cancellationToken);
        return doc;
    }

    public async Task<EmployeeDocument?> VerifyDocumentAsync(Guid tenantId, int employeeId, Guid documentId, string? notes, RequestContext context, CancellationToken cancellationToken)
    {
        var doc = await _db.EmployeeDocuments.FirstOrDefaultAsync(x => x.TenantId == tenantId && x.Id == documentId && x.EmployeeId == employeeId && !x.IsDeleted, cancellationToken);
        if (doc is null) return null;

        var before = doc.ApprovalStatus;
        doc.ApprovalStatus = "Verified";
        doc.VerifiedAtUtc = DateTime.UtcNow;
        doc.VerifiedBy = context.UserId;
        if (notes is not null) doc.Notes = notes.Trim();

        await _db.SaveChangesAsync(cancellationToken);
        // Verification can satisfy a requireVerified doc requirement — refresh the readiness badge.
        await RefreshEmployeeReadinessByIdAsync(tenantId, employeeId, cancellationToken);
        await _audit.WriteAsync("employee.document_verified", "EmployeeDocument", doc.Id.ToString(), context,
            JsonSerializer.Serialize(new { before, after = "Verified", documentType = doc.DocumentType }), cancellationToken);

        var employee = await _db.Employees.AsNoTracking().FirstOrDefaultAsync(x => x.TenantId == tenantId && x.Id == employeeId && !x.IsDeleted, cancellationToken);
        if (employee?.UserAccountId is not null)
            await _notifications.NotifyAsync(tenantId, employee.UserAccountId, "Document Verified",
                $"Your {doc.DocumentType} document has been verified.", "EmployeeDocument", doc.Id.ToString(), cancellationToken);

        return doc;
    }

    public async Task<EmployeeDocument?> RejectDocumentAsync(Guid tenantId, int employeeId, Guid documentId, string reason, RequestContext context, CancellationToken cancellationToken)
    {
        var doc = await _db.EmployeeDocuments.FirstOrDefaultAsync(x => x.TenantId == tenantId && x.Id == documentId && x.EmployeeId == employeeId && !x.IsDeleted, cancellationToken);
        if (doc is null) return null;

        var before = doc.ApprovalStatus;
        doc.ApprovalStatus = "Rejected";
        doc.Notes = string.IsNullOrWhiteSpace(doc.Notes) ? reason.Trim() : doc.Notes + " | Rejection: " + reason.Trim();

        await _db.SaveChangesAsync(cancellationToken);
        await _audit.WriteAsync("employee.document_rejected", "EmployeeDocument", doc.Id.ToString(), context,
            JsonSerializer.Serialize(new { before, after = "Rejected", reason, documentType = doc.DocumentType }), cancellationToken);

        var employee = await _db.Employees.AsNoTracking().FirstOrDefaultAsync(x => x.TenantId == tenantId && x.Id == employeeId && !x.IsDeleted, cancellationToken);
        if (employee?.UserAccountId is not null)
            await _notifications.NotifyAsync(tenantId, employee.UserAccountId, "Document Rejected",
                $"Your {doc.DocumentType} document was rejected. Reason: {reason}", "EmployeeDocument", doc.Id.ToString(), cancellationToken);

        return doc;
    }

    public async Task<bool> ArchiveDocumentAsync(Guid tenantId, int employeeId, Guid documentId, RequestContext context, CancellationToken cancellationToken)
    {
        var doc = await _db.EmployeeDocuments.FirstOrDefaultAsync(x => x.TenantId == tenantId && x.Id == documentId && x.EmployeeId == employeeId && !x.IsDeleted, cancellationToken);
        if (doc is null) return false;

        doc.IsDeleted = true;
        doc.DeletedAtUtc = DateTime.UtcNow;
        doc.DeletedBy = context.UserId;

        await _db.SaveChangesAsync(cancellationToken);
        await _audit.WriteAsync("employee.document_archived", "EmployeeDocument", doc.Id.ToString(), context,
            JsonSerializer.Serialize(new { documentType = doc.DocumentType, archivedBy = context.UserId }), cancellationToken);
        return true;
    }

    public async Task<DocumentExpiryCheckResult> CheckDocumentExpiryAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow.Date);
        var context = new RequestContext(null, "expiry-check", null, tenantId);

        // Mark expired: ExpiryDate has passed and not already Expired or deleted.
        var expired = await _db.EmployeeDocuments
            .Where(x => x.TenantId == tenantId && !x.IsDeleted && x.EmployeeId != null
                && x.ExpiryDate != null && x.ExpiryDate < today
                && x.ApprovalStatus != "Expired")
            .ToListAsync(cancellationToken);

        foreach (var doc in expired)
        {
            var before = doc.ApprovalStatus;
            doc.ApprovalStatus = "Expired";
            await _audit.WriteAsync("employee.document_expired", "EmployeeDocument", doc.Id.ToString(), context,
                JsonSerializer.Serialize(new { before, after = "Expired", expiryDate = doc.ExpiryDate, documentType = doc.DocumentType }), cancellationToken);
            var employee = await _db.Employees.AsNoTracking().FirstOrDefaultAsync(x => x.TenantId == tenantId && x.Id == doc.EmployeeId && !x.IsDeleted, cancellationToken);
            if (employee?.UserAccountId is not null)
                await _notifications.NotifyAsync(tenantId, employee.UserAccountId, "Document Expired",
                    $"Your {doc.DocumentType} document expired on {doc.ExpiryDate}. Please renew it.", "EmployeeDocument", doc.Id.ToString(), cancellationToken);
        }

        // Send renewal reminders: RenewalReminderDate has arrived, document not yet expired, not deleted.
        var reminders = await _db.EmployeeDocuments
            .Where(x => x.TenantId == tenantId && !x.IsDeleted && x.EmployeeId != null
                && x.RenewalReminderDate != null && x.RenewalReminderDate <= today
                && x.ExpiryDate != null && x.ExpiryDate >= today
                && x.ApprovalStatus != "Expired")
            .ToListAsync(cancellationToken);

        foreach (var doc in reminders)
        {
            await _audit.WriteAsync("employee.document_expiry_reminder", "EmployeeDocument", doc.Id.ToString(), context,
                JsonSerializer.Serialize(new { expiryDate = doc.ExpiryDate, documentType = doc.DocumentType }), cancellationToken);
            var employee = await _db.Employees.AsNoTracking().FirstOrDefaultAsync(x => x.TenantId == tenantId && x.Id == doc.EmployeeId && !x.IsDeleted, cancellationToken);
            if (employee?.UserAccountId is not null)
                await _notifications.NotifyAsync(tenantId, employee.UserAccountId, "Document Expiring Soon",
                    $"Your {doc.DocumentType} document expires on {doc.ExpiryDate}. Please renew before the expiry date.", "EmployeeDocument", doc.Id.ToString(), cancellationToken);
        }

        if (expired.Count > 0 || reminders.Count > 0)
            await _db.SaveChangesAsync(cancellationToken);

        return new DocumentExpiryCheckResult(expired.Count, reminders.Count);
    }

    public async Task<EmployeeStatusSummaryDto> StatusSummaryAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        var items = await _db.Employees.AsNoTracking().Where(x => x.TenantId == tenantId && !x.IsDeleted).GroupBy(x => x.Status).Select(x => new EmployeeGroupCountDto(x.Key, x.Count())).ToListAsync(cancellationToken);
        return new EmployeeStatusSummaryDto(items);
    }

    private async Task ApplyEmployee(Employee employee, EmployeeCreateRequest request, Guid tenantId, CancellationToken cancellationToken)
    {
        employee.EnglishName = Clean(request.EnglishName);
        employee.ArabicName = Clean(request.ArabicName);
        employee.FullName = employee.EnglishName;
        employee.PreferredName = Clean(request.PreferredName);
        employee.Gender = Clean(request.Gender);
        employee.DateOfBirth = request.DateOfBirth;
        employee.Nationality = Clean(request.Nationality);
        employee.MaritalStatus = Clean(request.MaritalStatus);
        employee.PersonalEmail = Clean(request.PersonalEmail);
        employee.WorkEmail = Clean(request.WorkEmail);
        employee.Phone = Clean(request.MobileNumber);
        employee.ProfilePhotoUrl = Clean(request.ProfilePhotoUrl);
        // Never null-out an existing company assignment: under operational company scoping
        // a null CompanyId hides the employee from scoped users. Reassignment requires an
        // explicit value; omission keeps the current legal entity.
        employee.CompanyId = request.CompanyId ?? employee.CompanyId;
        employee.BranchId = request.BranchId;
        employee.DepartmentId = request.DepartmentId;
        employee.DesignationId = request.DesignationId;
        employee.GradeId = request.GradeId;
        employee.CostCenterId = request.CostCenterId;
        employee.PositionId = request.PositionId;
        employee.Branch = request.BranchId is null ? employee.Branch : await _db.Branches.Where(x => x.TenantId == tenantId && x.Id == request.BranchId).Select(x => x.NameEn).FirstOrDefaultAsync(cancellationToken) ?? employee.Branch;
        var department = request.DepartmentId is null ? null : await _db.Departments
            .Where(x => x.TenantId == tenantId && x.Id == request.DepartmentId && x.IsActive && !x.IsDeleted)
            .Select(x => new { x.NameEn, x.ManagerEmployeeId })
            .FirstOrDefaultAsync(cancellationToken);
        employee.Department = request.DepartmentId is null ? employee.Department : department?.NameEn ?? employee.Department;
        if (request.DesignationId is not null)
        {
            var designation = await _db.Designations
                .Where(x => x.TenantId == tenantId && x.Id == request.DesignationId && !x.IsDeleted)
                .Select(x => new { x.TitleEn, x.GradeId })
                .FirstOrDefaultAsync(cancellationToken);
            if (designation is not null)
            {
                employee.Designation = designation.TitleEn;
                employee.GradeId ??= designation.GradeId;
            }
        }
        employee.Grade = request.GradeId is null ? employee.Grade : await _db.Grades.Where(x => x.TenantId == tenantId && x.Id == request.GradeId).Select(x => x.Code).FirstOrDefaultAsync(cancellationToken) ?? employee.Grade;
        employee.CostCenter = request.CostCenterId is null ? employee.CostCenter : await _db.CostCenters.Where(x => x.TenantId == tenantId && x.Id == request.CostCenterId).Select(x => x.Code).FirstOrDefaultAsync(cancellationToken) ?? employee.CostCenter;
        employee.JobTitle = Clean(request.JobTitle);
        employee.ManagerEmployeeId = request.ReportingManagerEmployeeId
            ?? await ResolveDepartmentManagerAsync(tenantId, department?.ManagerEmployeeId, cancellationToken)
            ?? (request.DepartmentId is null ? employee.ManagerEmployeeId : null);
        employee.SecondLevelManagerEmployeeId = request.SecondLevelManagerEmployeeId;
        employee.EmploymentType = Clean(request.EmploymentType);
        employee.ContractType = Clean(request.ContractType);
        employee.JoiningDate = UtcDate(request.JoiningDate)
            ?? (employee.JoiningDate == default ? DateTime.UtcNow.Date : DateTime.SpecifyKind(employee.JoiningDate.Date, DateTimeKind.Utc));
        employee.ConfirmationDate = request.ConfirmationDate;
        employee.ProbationStartDate = request.ProbationStartDate;
        employee.ProbationEndDate = request.ProbationEndDate;
        employee.NoticePeriodDays = request.NoticePeriodDays;
        employee.WorkLocation = Clean(request.WorkLocation);
        employee.PayrollProfileCode = Clean(request.PayrollGroup);
        employee.ShiftPolicyCode = Clean(request.ShiftPolicyCode);
        employee.LeavePolicyCode = Clean(request.LeavePolicyCode);
        employee.AttendancePolicyCode = Clean(request.AttendancePolicyCode);
        employee.CountryCode = request.ComplianceRecords?.FirstOrDefault()?.CountryCode ?? employee.CountryCode;
    }

    private async Task<string> GenerateEmployeeCode(Guid tenantId, EmployeeCreateRequest request, RequestContext context, CancellationToken cancellationToken)
    {
        var rule = await _db.EmployeeIdRules.FirstOrDefaultAsync(x => x.TenantId == tenantId && x.IsActive && !x.IsDeleted && (x.CompanyId == request.CompanyId || x.CompanyId == null), cancellationToken);
        if (rule is null)
        {
            rule = new EmployeeIdRule { TenantId = tenantId, CompanyId = request.CompanyId, CreatedBy = context.UserId };
            _db.EmployeeIdRules.Add(rule);
            await _db.SaveChangesAsync(cancellationToken);
        }
        var parts = new List<string> { Clean(rule.CompanyPrefix) };
        var country = request.ComplianceRecords?.FirstOrDefault()?.CountryCode ?? "";
        if (rule.UseCountryPrefix && !string.IsNullOrWhiteSpace(country)) parts.Add(country.ToUpperInvariant());
        if (rule.UseBranchPrefix && request.BranchId is not null)
        {
            var branchCode = await _db.Branches.Where(x => x.TenantId == tenantId && x.Id == request.BranchId).Select(x => x.Code).FirstOrDefaultAsync(cancellationToken);
            if (!string.IsNullOrWhiteSpace(branchCode)) parts.Add(branchCode);
        }
        if (rule.UseDepartmentPrefix && request.DepartmentId is not null)
        {
            var deptCode = await _db.Departments.Where(x => x.TenantId == tenantId && x.Id == request.DepartmentId).Select(x => x.Code).FirstOrDefaultAsync(cancellationToken);
            if (!string.IsNullOrWhiteSpace(deptCode)) parts.Add(deptCode);
        }
        if (rule.UseYear) parts.Add(DateTime.UtcNow.Year.ToString());
        var code = string.Join('-', parts.Where(x => !string.IsNullOrWhiteSpace(x))) + "-" + rule.NextSequence.ToString().PadLeft(rule.PaddingLength, '0');
        rule.NextSequence += 1;
        rule.UpdatedAtUtc = DateTime.UtcNow;
        rule.UpdatedBy = context.UserId;
        return code;
    }

    private async Task UpsertPayrollProfile(Employee employee, EmployeePayrollProfileRequest? request, RequestContext context, CancellationToken cancellationToken)
    {
        if (request is null || employee.TenantId is null) return;
        var profile = await _db.EmployeePayrollProfiles.FirstOrDefaultAsync(x => x.TenantId == employee.TenantId && x.EmployeeId == employee.Id && !x.IsDeleted, cancellationToken);
        if (profile is null)
        {
            profile = new EmployeePayrollProfile { TenantId = employee.TenantId.Value, EmployeeId = employee.Id, CreatedBy = context.UserId };
            _db.EmployeePayrollProfiles.Add(profile);
        }
        profile.BankName = Clean(request.BankName);
        // Validate the IBAN at ENTRY (create/update), not only late at payroll-run/WPS-export time.
        // A bad IBAN persisted here silently blocks the entire payroll run weeks later; fail fast so
        // the person entering it fixes it now. Empty is allowed (bank details filled in later).
        var cleanIban = Clean(request.Iban);
        if (!string.IsNullOrWhiteSpace(cleanIban) && !Zayra.Api.Infrastructure.Payroll.IbanValidator.IsValid(cleanIban))
            throw new InvalidOperationException($"IBAN '{cleanIban}' is invalid — it fails the ISO 13616 mod-97 checksum. Enter a correct IBAN before saving.");
        profile.Iban = cleanIban;
        profile.AccountNumber = Clean(request.AccountNumber);
        profile.PaymentMethod = Clean(request.PaymentMethod);
        profile.SalaryCurrency = Clean(request.SalaryCurrency);
        profile.PayrollGroup = Clean(request.PayrollGroup);
        profile.SalaryStructureReference = Clean(request.SalaryStructureReference);
        profile.WpsEligible = request.WpsEligible;
        profile.EosbEligible = request.EosbEligible;
        profile.SocialInsuranceReference = Clean(request.SocialInsuranceReference);
        profile.MolId = Clean(request.MolId);
        profile.BankRoutingCode = Clean(request.BankRoutingCode);
        profile.UpdatedAtUtc = DateTime.UtcNow;
        profile.UpdatedBy = context.UserId;
        employee.BankName = profile.BankName;
        employee.BankIban = profile.Iban;
        employee.WpsBankDetails = profile.PaymentMethod;
    }

    private async Task ValidatePositionAndSalaryAsync(Employee employee, EmployeeSalaryBreakdownRequest? salary, Guid tenantId, CancellationToken cancellationToken)
    {
        if (employee.PositionId is not null)
        {
            var position = await _db.Positions.AsNoTracking()
                .FirstOrDefaultAsync(x => x.TenantId == tenantId && x.Id == employee.PositionId && !x.IsDeleted, cancellationToken);
            if (position is null) throw new InvalidOperationException("Selected position was not found.");
            if (position.Status is PositionStatuses.Frozen or PositionStatuses.Closed)
                throw new InvalidOperationException($"Position '{position.Code}' is {position.Status.ToLowerInvariant()} and cannot receive an employee.");
            if (position.EffectiveFrom > DateOnly.FromDateTime(employee.JoiningDate))
                throw new InvalidOperationException($"Position '{position.Code}' is not effective on the employee joining date.");
            if (position.EffectiveTo is not null && position.EffectiveTo < DateOnly.FromDateTime(employee.JoiningDate))
                throw new InvalidOperationException($"Position '{position.Code}' expired before the employee joining date.");
            if (position.IncumbentEmployeeId is not null && position.IncumbentEmployeeId != employee.Id)
                throw new InvalidOperationException($"Position '{position.Code}' is already occupied by another employee.");

            void RequireMatch(Guid? expected, Guid? actual, string label)
            {
                if (expected is not null && actual != expected)
                    throw new InvalidOperationException($"Position '{position.Code}' requires {label} '{expected}'.");
            }

            RequireMatch(position.CompanyId, employee.CompanyId, "its configured legal entity");
            RequireMatch(position.BranchId, employee.BranchId, "its configured branch");
            RequireMatch(position.DepartmentId, employee.DepartmentId, "its configured department");
            RequireMatch(position.CostCenterId, employee.CostCenterId, "its configured cost center");
            RequireMatch(position.DesignationId, employee.DesignationId, "its configured designation");
            RequireMatch(position.GradeId, employee.GradeId, "its configured grade");
        }

        if (employee.DesignationId is not null)
        {
            var designation = await _db.Designations
                .AsNoTracking()
                .Where(x => x.TenantId == tenantId && x.Id == employee.DesignationId && x.IsActive && !x.IsDeleted)
                .Select(x => new { x.GradeId, x.TitleEn })
                .FirstOrDefaultAsync(cancellationToken);
            if (designation is null) throw new InvalidOperationException("Selected designation is not active.");
            if (designation.GradeId is not null && employee.GradeId is not null && designation.GradeId != employee.GradeId)
                throw new InvalidOperationException($"Designation '{designation.TitleEn}' is eligible only for its configured grade.");
            employee.GradeId ??= designation.GradeId;
        }

        if (employee.GradeId is null) return;

        var grade = await _db.Grades
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.TenantId == tenantId && x.Id == employee.GradeId && x.IsActive && !x.IsDeleted, cancellationToken);
        if (grade is null) throw new InvalidOperationException("Selected grade is not active.");
        employee.Grade = grade.Code;

        var grossSalary = GrossSalary(salary);
        if (grossSalary <= 0) return;
        if (grade.MinSalary > 0 && grossSalary < grade.MinSalary)
            throw new InvalidOperationException($"Salary package {grossSalary:N2} is below grade {grade.Code} minimum {grade.MinSalary:N2} {grade.Currency}.");
        if (grade.MaxSalary > 0 && grossSalary > grade.MaxSalary)
            throw new InvalidOperationException($"Salary package {grossSalary:N2} exceeds grade {grade.Code} maximum {grade.MaxSalary:N2} {grade.Currency}.");
    }

    private async Task SynchronizePositionIncumbencyAsync(Employee employee, Guid? priorPositionId, RequestContext context, CancellationToken cancellationToken)
    {
        if (priorPositionId is not null && priorPositionId != employee.PositionId)
        {
            var prior = await _db.Positions.FirstOrDefaultAsync(x => x.TenantId == employee.TenantId && x.Id == priorPositionId && !x.IsDeleted, cancellationToken);
            if (prior?.IncumbentEmployeeId == employee.Id)
            {
                prior.IncumbentEmployeeId = null;
                prior.Status = PositionStatuses.Open;
                prior.UpdatedAtUtc = DateTime.UtcNow;
                prior.UpdatedBy = context.UserId;
            }
        }
        if (employee.PositionId is not null)
        {
            var position = await _db.Positions.FirstAsync(x => x.TenantId == employee.TenantId && x.Id == employee.PositionId && !x.IsDeleted, cancellationToken);
            position.IncumbentEmployeeId = employee.Id;
            position.Status = PositionStatuses.Filled;
            position.UpdatedAtUtc = DateTime.UtcNow;
            position.UpdatedBy = context.UserId;
        }
        await _db.SaveChangesAsync(cancellationToken);
    }

    private async Task UpsertEmployeeSalaryStructure(Employee employee, EmployeeSalaryBreakdownRequest? request, RequestContext context, CancellationToken cancellationToken)
    {
        if (employee.TenantId is null || employee.GradeId is null) return;
        var grade = await _db.Grades.AsNoTracking().FirstOrDefaultAsync(x => x.TenantId == employee.TenantId && x.Id == employee.GradeId && !x.IsDeleted, cancellationToken);
        if (grade is null) return;

        var components = await _db.GradePayScaleComponents
            .AsNoTracking()
            .Where(x => x.TenantId == employee.TenantId && x.GradeId == grade.Id && x.IsActive)
            .OrderBy(x => x.SortOrder)
            .ToListAsync(cancellationToken);

        var salary = BuildSalaryBreakdown(request, components);
        if (GrossSalary(salary) <= 0) return;

        var effectiveDate = request?.EffectiveDate ?? DateOnly.FromDateTime(employee.JoiningDate == default ? DateTime.UtcNow : employee.JoiningDate);
        await _db.EmployeeSalaryStructures
            .Where(x => x.TenantId == employee.TenantId && x.EmployeeId == employee.Id && x.IsActive && x.EffectiveDate == effectiveDate)
            .ExecuteUpdateAsync(x => x.SetProperty(p => p.IsActive, false), cancellationToken);

        var structureCode = Clean(request?.SalaryStructureCode);
        if (string.IsNullOrWhiteSpace(structureCode)) structureCode = $"GRADE-{grade.Code}";
        var structure = await _db.SalaryStructures.FirstOrDefaultAsync(x => x.TenantId == employee.TenantId && x.Code == structureCode && !x.IsDeleted, cancellationToken);
        if (structure is null)
        {
            structure = new SalaryStructure
            {
                TenantId = employee.TenantId.Value,
                CompanyId = employee.CompanyId,
                Code = structureCode,
                Name = $"{grade.Name} salary structure",
                Currency = Clean(request?.Currency) is { Length: > 0 } requestCurrency ? requestCurrency.ToUpperInvariant() : grade.Currency,
                EffectiveDate = effectiveDate,
                CreatedBy = context.UserId
            };
            _db.SalaryStructures.Add(structure);

            foreach (var component in components)
            {
                _db.SalaryComponents.Add(new SalaryComponent
                {
                    TenantId = employee.TenantId.Value,
                    SalaryStructureId = structure.Id,
                    Code = component.ComponentCode,
                    Name = component.ComponentName,
                    ComponentType = component.ComponentType,
                    CalculationType = component.CalculationType,
                    Amount = component.Amount,
                    Percentage = component.Percentage,
                    IsTaxable = component.IsTaxable
                });
            }
        }

        var assignment = new EmployeeSalaryStructure
        {
            TenantId = employee.TenantId.Value,
            EmployeeId = employee.Id,
            SalaryStructureId = structure.Id,
            BasicSalary = salary.BasicSalary ?? 0m,
            HousingAllowance = salary.HousingAllowance ?? 0m,
            TransportAllowance = salary.TransportAllowance ?? 0m,
            FoodAllowance = salary.FoodAllowance ?? 0m,
            MobileAllowance = salary.MobileAllowance ?? 0m,
            OtherAllowance = salary.OtherAllowance ?? 0m,
            FixedDeduction = salary.FixedDeduction ?? 0m,
            EffectiveDate = effectiveDate,
            Currency = Clean(request?.Currency) is { Length: > 0 } currency ? currency.ToUpperInvariant() : structure.Currency,
            CreatedBy = context.UserId
        };
        _db.EmployeeSalaryStructures.Add(assignment);
        employee.Salary = GrossSalary(salary);
        employee.PayrollProfileCode = string.IsNullOrWhiteSpace(employee.PayrollProfileCode) ? structure.Code : employee.PayrollProfileCode;
    }

    private static EmployeeSalaryBreakdownRequest BuildSalaryBreakdown(EmployeeSalaryBreakdownRequest? request, IReadOnlyCollection<GradePayScaleComponent> components)
    {
        if (request is not null && GrossSalary(request) > 0) return request;

        decimal basic = 0, housing = 0, transport = 0, food = 0, mobile = 0, other = 0, fixedDeduction = 0;
        foreach (var component in components)
        {
            var code = component.ComponentCode.ToUpperInvariant();
            var amount = component.Amount;
            if (code.Contains("BASIC")) basic += amount;
            else if (code.Contains("HOUS")) housing += amount;
            else if (code.Contains("TRANS")) transport += amount;
            else if (code.Contains("FOOD")) food += amount;
            else if (code.Contains("MOBILE")) mobile += amount;
            else if (component.ComponentType.Equals("Deduction", StringComparison.OrdinalIgnoreCase)) fixedDeduction += amount;
            else other += amount;
        }

        return new EmployeeSalaryBreakdownRequest(basic, housing, transport, food, mobile, other, fixedDeduction, request?.SalaryStructureCode, request?.EffectiveDate, request?.Currency);
    }

    private static decimal GrossSalary(EmployeeSalaryBreakdownRequest? salary)
        => (salary?.BasicSalary ?? 0m)
           + (salary?.HousingAllowance ?? 0m)
           + (salary?.TransportAllowance ?? 0m)
           + (salary?.FoodAllowance ?? 0m)
           + (salary?.MobileAllowance ?? 0m)
           + (salary?.OtherAllowance ?? 0m);

    private async Task UpsertComplianceRecords(Employee employee, IReadOnlyCollection<EmployeeComplianceRecordRequest> records, RequestContext context, CancellationToken cancellationToken)
    {
        if (employee.TenantId is null) return;
        foreach (var request in records)
        {
            var key = Clean(request.FieldKey);
            var record = await _db.EmployeeComplianceRecords.FirstOrDefaultAsync(x => x.TenantId == employee.TenantId && x.EmployeeId == employee.Id && x.CountryCode == request.CountryCode && x.FieldKey == key && !x.IsDeleted, cancellationToken);
            if (record is null)
            {
                record = new EmployeeComplianceRecord { TenantId = employee.TenantId.Value, EmployeeId = employee.Id, CountryCode = request.CountryCode, FieldKey = key, CreatedBy = context.UserId };
                _db.EmployeeComplianceRecords.Add(record);
            }
            record.FieldLabel = Clean(request.FieldLabel);
            record.FieldValue = Clean(request.FieldValue);
            record.IssueDate = request.IssueDate;
            record.ExpiryDate = request.ExpiryDate;
            record.IsSensitive = request.IsSensitive;
            record.IsRequired = request.IsRequired;
            record.UpdatedAtUtc = DateTime.UtcNow;
            record.UpdatedBy = context.UserId;
            ApplyKnownComplianceMirror(employee, record);
        }
    }

    /// <summary>
    /// Unambiguous default only: with exactly one active company that company is the
    /// answer; a multi-company (Group) tenant returns null so the write-side company
    /// stamping in ZayraDbContext enforces explicit company context instead of a silent
    /// oldest-company guess.
    /// </summary>
    private async Task<Guid?> ResolveDefaultCompanyId(Guid tenantId, CancellationToken cancellationToken)
    {
        var ids = await _db.Companies
            .Where(c => c.TenantId == tenantId && !c.IsDeleted && c.IsActive)
            .Select(c => (Guid?)c.Id)
            .Take(2)
            .ToListAsync(cancellationToken);
        return ids.Count == 1 ? ids[0] : null;
    }

    private void TrackChange(Employee employee, string field, string oldValue, string newValue, DateTime? effectiveDate, string reason, RequestContext context)
    {
        if (oldValue == newValue || employee.TenantId is null) return;
        _db.EmployeeHistories.Add(new EmployeeHistory { TenantId = employee.TenantId.Value, EmployeeId = employee.Id, EventType = field + "Change", FieldName = field, OldValue = EmployeeSafeSnapshot.SanitizeFieldValue(field, oldValue), NewValue = EmployeeSafeSnapshot.SanitizeFieldValue(field, newValue), EffectiveDate = DateOnly.FromDateTime(effectiveDate ?? DateTime.UtcNow), Reason = reason, CreatedByUserId = context.UserId, SnapshotJson = EmployeeSafeSnapshot.Serialize(employee) });
    }

    private async Task AddHistory(Employee employee, string eventType, string field, string oldValue, string newValue, DateOnly effectiveDate, string reason, RequestContext context, CancellationToken cancellationToken)
    {
        _db.EmployeeHistories.Add(new EmployeeHistory { TenantId = employee.TenantId ?? context.TenantId!.Value, EmployeeId = employee.Id, EventType = eventType, FieldName = field, OldValue = EmployeeSafeSnapshot.SanitizeFieldValue(field, oldValue), NewValue = EmployeeSafeSnapshot.SanitizeFieldValue(field, newValue), EffectiveDate = effectiveDate, Reason = reason, CreatedByUserId = context.UserId, SnapshotJson = EmployeeSafeSnapshot.Serialize(employee) });
        await Task.CompletedTask;
    }

    private static void ApplyKnownComplianceMirror(Employee employee, EmployeeComplianceRecord record)
    {
        // For the GCC-ID number records the create modal carries the expiry on the SAME record as the
        // number (fieldKey = *_number / emirates_id / qid / civil_id, `.ExpiryDate`). Mirror that expiry
        // to the first-class scalar the readiness pay-gate reads (Δ19/Δ20) — but ONLY when the record
        // actually carries one, so a number-only edit never wipes an expiry set by a separate record.
        switch (record.FieldKey.ToLowerInvariant())
        {
            case "passport_number":
                employee.PassportNumber = record.FieldValue;
                if (record.ExpiryDate.HasValue) employee.PassportExpiryDate = record.ExpiryDate;
                break;
            case "passport_expiry": employee.PassportExpiryDate = record.ExpiryDate; break;
            case "visa_number":
                employee.VisaNumber = record.FieldValue;
                if (record.ExpiryDate.HasValue) employee.VisaExpiryDate = record.ExpiryDate;
                break;
            case "visa_expiry": employee.VisaExpiryDate = record.ExpiryDate; break;
            case "iqama_number":
                employee.IqamaNumber = record.FieldValue;
                if (record.ExpiryDate.HasValue) employee.IqamaExpiryDate = record.ExpiryDate;
                break;
            case "iqama_expiry": employee.IqamaExpiryDate = record.ExpiryDate; break;
            case "muqeem_reference": employee.MuqeemNumber = record.FieldValue; break;
            case "gosi_reference": employee.GosiReference = record.FieldValue; break;
            case "qiwa_contract_reference": employee.QiwaContractNumber = record.FieldValue; break;
            case "emirates_id":
                employee.EmiratesId = record.FieldValue;
                if (record.ExpiryDate.HasValue) employee.EmiratesIdExpiryDate = record.ExpiryDate;
                break;
            case "emirates_id_expiry": employee.EmiratesIdExpiryDate = record.ExpiryDate; break;
            case "labor_card_number": employee.LaborCardNumber = record.FieldValue; break;
            case "visa_file_number": employee.VisaFileNumber = record.FieldValue; break;
            case "qid":
                employee.Qid = record.FieldValue;
                if (record.ExpiryDate.HasValue) employee.QidExpiryDate = record.ExpiryDate;
                break;
            case "qid_expiry": employee.QidExpiryDate = record.ExpiryDate; break;
            case "civil_id":
                employee.CivilId = record.FieldValue;
                if (record.ExpiryDate.HasValue) employee.CivilIdExpiryDate = record.ExpiryDate;
                break;
            case "civil_id_expiry": employee.CivilIdExpiryDate = record.ExpiryDate; break;
            case "residency_number": employee.ResidencyNumber = record.FieldValue; break;
            case "work_permit": employee.WorkPermitNumber = record.FieldValue; break;
            case "sponsor": employee.SponsorName = record.FieldValue; break;
        }
    }

    // ── Readiness snapshot recompute-and-stamp (§4.3) ─────────────────────────
    // Refreshes the DENORMALIZED display columns (ReadinessState / ActivationBlockersCount /
    // ReadinessEvaluatedAtUtc) for the list badge. Display-only and best-effort (never throws) — the
    // activation gate always recomputes live and never trusts these columns. Callers SaveChanges after.
    // "already calls CalculateCompleteness" is FALSE for these paths — each needs an explicit refresh.
    private async Task RefreshReadinessSnapshotAsync(Employee employee, CancellationToken ct)
    {
        if (employee.TenantId is null || employee.Id == 0) return;
        var snapshot = await _activationGuard.BuildSnapshotAsync(employee.TenantId.Value, employee.Id, ct);
        if (snapshot is null) return;
        var policy = await _activationGuard.ResolvePolicyAsync(employee.TenantId.Value, employee.CompanyId, employee.CountryCode, employee.Nationality, ct);
        var readiness = _activationGuard.Evaluate(snapshot, policy);
        employee.ReadinessState = readiness.State;
        employee.ActivationBlockersCount = readiness.Blocking.Count;
        employee.ReadinessEvaluatedAtUtc = DateTime.UtcNow;
        // Policy-aware completeness (§4.2): the score now reflects readiness against the resolved policy
        // so the % and the badge never contradict (was a fixed ~22-field HR denominator with ZERO
        // statutory fields — an employee read 100% while missing Iqama/GOSI/IBAN). Only overwrite when a
        // policy actually applies, so a no-policy tenant keeps the prior HR-completeness signal.
        if (policy.Items.Count > 0)
            employee.ProfileCompletenessScore = readiness.Score;
    }

    private async Task RefreshEmployeeReadinessByIdAsync(Guid tenantId, int employeeId, CancellationToken ct)
    {
        var employee = await _db.Employees.FirstOrDefaultAsync(x => x.TenantId == tenantId && x.Id == employeeId && !x.IsDeleted, ct);
        if (employee is null) return;
        await RefreshReadinessSnapshotAsync(employee, ct);
        await _db.SaveChangesAsync(ct);
    }

    private static decimal CalculateCompleteness(Employee employee, EmployeePayrollProfileRequest? payroll, IReadOnlyCollection<EmployeeComplianceRecordRequest>? compliance)
    {
        var values = new[] { employee.EnglishName, employee.Gender, employee.Nationality, employee.MaritalStatus, employee.PersonalEmail, employee.WorkEmail, employee.Phone, employee.Department, employee.Designation, employee.Branch, employee.JobTitle, employee.EmploymentType, employee.ContractType, employee.WorkLocation, employee.ShiftPolicyCode, employee.LeavePolicyCode };
        var completed = values.Count(x => !string.IsNullOrWhiteSpace(x)) + (employee.DateOfBirth.HasValue ? 1 : 0) + (employee.JoiningDate != default ? 1 : 0) + (payroll is not null ? 2 : 0) + (compliance?.Count > 0 ? 2 : 0);
        return Math.Round(Math.Min(100m, completed * 100m / 22m), 1);
    }

    private static string Clean(string? value) => value?.Trim() ?? string.Empty;

    private static DateTime? UtcDate(DateTime? value)
        => value.HasValue ? DateTime.SpecifyKind(value.Value.Date, DateTimeKind.Utc) : null;

    private async Task<int?> ResolveDepartmentManagerAsync(Guid tenantId, int? managerEmployeeId, CancellationToken cancellationToken)
    {
        if (!managerEmployeeId.HasValue) return null;
        return await _db.Employees.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.Id == managerEmployeeId.Value && !x.IsDeleted)
            .Select(x => (int?)x.Id)
            .FirstOrDefaultAsync(cancellationToken);
    }

    private async Task EnsurePrimaryReportingLineAsync(Employee employee, RequestContext context, CancellationToken cancellationToken)
    {
        if (!employee.ManagerEmployeeId.HasValue || employee.ManagerEmployeeId.Value == employee.Id || employee.TenantId is null) return;

        var manager = await _db.Employees.AsNoTracking()
            .Where(x => x.TenantId == employee.TenantId && x.Id == employee.ManagerEmployeeId.Value && !x.IsDeleted)
            .Select(x => new { x.Id, x.CompanyId })
            .FirstOrDefaultAsync(cancellationToken);
        if (manager is null) return;
        if (employee.CompanyId.HasValue && manager.CompanyId.HasValue && employee.CompanyId != manager.CompanyId) return;

        var exists = await _db.ReportingLines.AnyAsync(x =>
            x.TenantId == employee.TenantId &&
            x.EmployeeId == employee.Id &&
            x.ManagerEmployeeId == manager.Id &&
            x.RelationshipType == "SolidLine" &&
            x.IsPrimary &&
            x.IsActive, cancellationToken);
        if (exists) return;

        _db.ReportingLines.Add(new ReportingLine
        {
            TenantId = employee.TenantId.Value,
            EmployeeId = employee.Id,
            ManagerEmployeeId = manager.Id,
            RelationshipType = "SolidLine",
            EffectiveFrom = DateTime.UtcNow,
            IsPrimary = true,
            IsActive = true,
            CreatedBy = context.UserId
        });
        await _db.SaveChangesAsync(cancellationToken);
    }
}
