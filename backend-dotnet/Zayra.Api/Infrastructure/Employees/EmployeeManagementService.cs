using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Auth;
using Zayra.Api.Application.Common;
using Zayra.Api.Application.Employees;
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

    public EmployeeManagementService(ZayraDbContext db, IAuditService audit, IDocumentStorage documents, INotificationService notifications)
    {
        _db = db;
        _audit = audit;
        _documents = documents;
        _notifications = notifications;
    }

    public async Task<PagedResult<EmployeeListItemDto>> SearchAsync(Guid tenantId, string? search, string? status, string? department, int page, int pageSize, CancellationToken cancellationToken)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 100);
        var query = _db.Employees.AsNoTracking().Where(x => x.TenantId == tenantId && !x.IsDeleted);
        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim();
            query = query.Where(x => x.EmployeeCode.Contains(term) || x.FullName.Contains(term) || x.EnglishName.Contains(term) || x.ArabicName.Contains(term) || x.WorkEmail.Contains(term));
        }
        if (!string.IsNullOrWhiteSpace(status)) query = query.Where(x => x.Status == status);
        if (!string.IsNullOrWhiteSpace(department)) query = query.Where(x => x.Department == department);

        var total = await query.CountAsync(cancellationToken);
        var items = await query.OrderBy(x => x.EmployeeCode).Skip((page - 1) * pageSize).Take(pageSize)
            .Select(x => new EmployeeListItemDto(x.Id, x.EmployeeCode, x.FullName, x.ArabicName, x.Department, x.Designation, x.Branch, x.ManagerEmployeeId, x.Status, x.ProfileCompletenessScore, x.VisaExpiryDate, x.PassportExpiryDate))
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
        employee.Status = "Draft";
        employee.ProfileCompletenessScore = CalculateCompleteness(employee, request.PayrollProfile, request.ComplianceRecords);
        _db.Employees.Add(employee);
        await _db.SaveChangesAsync(cancellationToken);

        await UpsertPayrollProfile(employee, request.PayrollProfile, context, cancellationToken);
        await UpsertComplianceRecords(employee, request.ComplianceRecords ?? [], context, cancellationToken);
        await AddHistory(employee, "Created", "Employee", string.Empty, employee.EmployeeCode, DateOnly.FromDateTime(DateTime.UtcNow), "Employee created", context, cancellationToken);
        await _db.SaveChangesAsync(cancellationToken);
        await _audit.WriteAsync("employee.created", "Employee", employee.Id.ToString(), context, null, cancellationToken);
        return (await GetAsync(tenantId, employee.Id, true, context, cancellationToken))!;
    }

    public async Task<EmployeeDetailDto?> UpdateAsync(Guid tenantId, int id, EmployeeCreateRequest request, RequestContext context, CancellationToken cancellationToken)
    {
        var employee = await _db.Employees.FirstOrDefaultAsync(x => x.TenantId == tenantId && x.Id == id && !x.IsDeleted, cancellationToken);
        if (employee is null) return null;
        TrackChange(employee, "Department", employee.Department, request.DepartmentId?.ToString() ?? employee.Department, request.JoiningDate, "Department update", context);
        TrackChange(employee, "Designation", employee.Designation, request.DesignationId?.ToString() ?? employee.Designation, request.JoiningDate, "Designation update", context);
        TrackChange(employee, "Manager", employee.ManagerEmployeeId?.ToString() ?? "", request.ReportingManagerEmployeeId?.ToString() ?? "", request.JoiningDate, "Manager update", context);
        TrackChange(employee, "Grade", employee.Grade, request.GradeId?.ToString() ?? employee.Grade, request.JoiningDate, "Grade update", context);

        await ApplyEmployee(employee, request, tenantId, cancellationToken);
        employee.UpdatedAtUtc = DateTime.UtcNow;
        employee.UpdatedBy = context.UserId;
        employee.ProfileCompletenessScore = CalculateCompleteness(employee, request.PayrollProfile, request.ComplianceRecords);
        await UpsertPayrollProfile(employee, request.PayrollProfile, context, cancellationToken);
        await UpsertComplianceRecords(employee, request.ComplianceRecords ?? [], context, cancellationToken);
        await _db.SaveChangesAsync(cancellationToken);
        await _audit.WriteAsync("employee.updated", "Employee", id.ToString(), context, null, cancellationToken);
        return await GetAsync(tenantId, id, true, context, cancellationToken);
    }

    public async Task<EmployeeDetailDto?> ChangeStatusAsync(Guid tenantId, int id, EmployeeStatusChangeRequest request, RequestContext context, CancellationToken cancellationToken)
    {
        var employee = await _db.Employees.FirstOrDefaultAsync(x => x.TenantId == tenantId && x.Id == id && !x.IsDeleted, cancellationToken);
        if (employee is null) return null;
        var oldStatus = employee.Status;
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
        await _db.SaveChangesAsync(cancellationToken);
        await _audit.WriteAsync("employee.status_changed", "Employee", id.ToString(), context, JsonSerializer.Serialize(new { oldStatus, request.Status, request.Reason }), cancellationToken);
        return await GetAsync(tenantId, id, true, context, cancellationToken);
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
        var transfer = new EmployeeTransferRequest
        {
            TenantId = tenantId,
            EmployeeId = employeeId,
            CurrentBranch = employee.Branch,
            CurrentDepartment = employee.Department,
            CurrentDesignation = employee.Designation,
            CurrentManagerEmployeeId = employee.ManagerEmployeeId,
            NewBranch = Clean(request.NewBranch),
            NewDepartment = Clean(request.NewDepartment),
            NewDesignation = Clean(request.NewDesignation),
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
        employee.CompanyId = request.CompanyId;
        employee.BranchId = request.BranchId;
        employee.DepartmentId = request.DepartmentId;
        employee.DesignationId = request.DesignationId;
        employee.GradeId = request.GradeId;
        employee.CostCenterId = request.CostCenterId;
        employee.Branch = request.BranchId is null ? employee.Branch : await _db.Branches.Where(x => x.TenantId == tenantId && x.Id == request.BranchId).Select(x => x.NameEn).FirstOrDefaultAsync(cancellationToken) ?? employee.Branch;
        employee.Department = request.DepartmentId is null ? employee.Department : await _db.Departments.Where(x => x.TenantId == tenantId && x.Id == request.DepartmentId).Select(x => x.NameEn).FirstOrDefaultAsync(cancellationToken) ?? employee.Department;
        employee.Designation = request.DesignationId is null ? employee.Designation : await _db.Designations.Where(x => x.TenantId == tenantId && x.Id == request.DesignationId).Select(x => x.TitleEn).FirstOrDefaultAsync(cancellationToken) ?? employee.Designation;
        employee.Grade = request.GradeId is null ? employee.Grade : await _db.Grades.Where(x => x.TenantId == tenantId && x.Id == request.GradeId).Select(x => x.Code).FirstOrDefaultAsync(cancellationToken) ?? employee.Grade;
        employee.CostCenter = request.CostCenterId is null ? employee.CostCenter : await _db.CostCenters.Where(x => x.TenantId == tenantId && x.Id == request.CostCenterId).Select(x => x.Code).FirstOrDefaultAsync(cancellationToken) ?? employee.CostCenter;
        employee.JobTitle = Clean(request.JobTitle);
        employee.ManagerEmployeeId = request.ReportingManagerEmployeeId;
        employee.SecondLevelManagerEmployeeId = request.SecondLevelManagerEmployeeId;
        employee.EmploymentType = Clean(request.EmploymentType);
        employee.ContractType = Clean(request.ContractType);
        employee.JoiningDate = request.JoiningDate ?? (employee.JoiningDate == default ? DateTime.UtcNow.Date : employee.JoiningDate);
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
        profile.Iban = Clean(request.Iban);
        profile.AccountNumber = Clean(request.AccountNumber);
        profile.PaymentMethod = Clean(request.PaymentMethod);
        profile.SalaryCurrency = Clean(request.SalaryCurrency);
        profile.PayrollGroup = Clean(request.PayrollGroup);
        profile.SalaryStructureReference = Clean(request.SalaryStructureReference);
        profile.WpsEligible = request.WpsEligible;
        profile.EosbEligible = request.EosbEligible;
        profile.SocialInsuranceReference = Clean(request.SocialInsuranceReference);
        profile.UpdatedAtUtc = DateTime.UtcNow;
        profile.UpdatedBy = context.UserId;
        employee.BankName = profile.BankName;
        employee.BankIban = profile.Iban;
        employee.WpsBankDetails = profile.PaymentMethod;
    }

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

    private void TrackChange(Employee employee, string field, string oldValue, string newValue, DateTime? effectiveDate, string reason, RequestContext context)
    {
        if (oldValue == newValue || employee.TenantId is null) return;
        _db.EmployeeHistories.Add(new EmployeeHistory { TenantId = employee.TenantId.Value, EmployeeId = employee.Id, EventType = field + "Change", FieldName = field, OldValue = oldValue, NewValue = newValue, EffectiveDate = DateOnly.FromDateTime(effectiveDate ?? DateTime.UtcNow), Reason = reason, CreatedByUserId = context.UserId, SnapshotJson = JsonSerializer.Serialize(employee) });
    }

    private async Task AddHistory(Employee employee, string eventType, string field, string oldValue, string newValue, DateOnly effectiveDate, string reason, RequestContext context, CancellationToken cancellationToken)
    {
        _db.EmployeeHistories.Add(new EmployeeHistory { TenantId = employee.TenantId ?? context.TenantId!.Value, EmployeeId = employee.Id, EventType = eventType, FieldName = field, OldValue = oldValue, NewValue = newValue, EffectiveDate = effectiveDate, Reason = reason, CreatedByUserId = context.UserId, SnapshotJson = JsonSerializer.Serialize(employee) });
        await Task.CompletedTask;
    }

    private static void ApplyKnownComplianceMirror(Employee employee, EmployeeComplianceRecord record)
    {
        switch (record.FieldKey.ToLowerInvariant())
        {
            case "passport_number": employee.PassportNumber = record.FieldValue; break;
            case "passport_expiry": employee.PassportExpiryDate = record.ExpiryDate; break;
            case "visa_number": employee.VisaNumber = record.FieldValue; break;
            case "visa_expiry": employee.VisaExpiryDate = record.ExpiryDate; break;
            case "iqama_number": employee.IqamaNumber = record.FieldValue; break;
            case "muqeem_reference": employee.MuqeemNumber = record.FieldValue; break;
            case "gosi_reference": employee.GosiReference = record.FieldValue; break;
            case "qiwa_contract_reference": employee.QiwaContractNumber = record.FieldValue; break;
            case "emirates_id": employee.EmiratesId = record.FieldValue; break;
            case "labor_card_number": employee.LaborCardNumber = record.FieldValue; break;
            case "visa_file_number": employee.VisaFileNumber = record.FieldValue; break;
            case "work_permit": employee.WorkPermitNumber = record.FieldValue; break;
            case "sponsor": employee.SponsorName = record.FieldValue; break;
        }
    }

    private static decimal CalculateCompleteness(Employee employee, EmployeePayrollProfileRequest? payroll, IReadOnlyCollection<EmployeeComplianceRecordRequest>? compliance)
    {
        var values = new[] { employee.EnglishName, employee.Gender, employee.Nationality, employee.MaritalStatus, employee.PersonalEmail, employee.WorkEmail, employee.Phone, employee.Department, employee.Designation, employee.Branch, employee.JobTitle, employee.EmploymentType, employee.ContractType, employee.WorkLocation, employee.ShiftPolicyCode, employee.LeavePolicyCode };
        var completed = values.Count(x => !string.IsNullOrWhiteSpace(x)) + (employee.DateOfBirth.HasValue ? 1 : 0) + (employee.JoiningDate != default ? 1 : 0) + (payroll is not null ? 2 : 0) + (compliance?.Count > 0 ? 2 : 0);
        return Math.Round(Math.Min(100m, completed * 100m / 22m), 1);
    }

    private static string Clean(string? value) => value?.Trim() ?? string.Empty;
}
