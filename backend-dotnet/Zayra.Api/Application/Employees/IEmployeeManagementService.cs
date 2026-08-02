using Microsoft.AspNetCore.Http;
using Zayra.Api.Application.Auth;
using Zayra.Api.Application.Common;
using Zayra.Api.Controllers;
using Zayra.Api.Models;

namespace Zayra.Api.Application.Employees;

public interface IEmployeeManagementService
{
    Task<PagedResult<EmployeeListItemDto>> SearchAsync(Guid tenantId, string? search, string? status, string? department, int page, int pageSize, CancellationToken cancellationToken);

    /// <summary>People-list search with the server-side readiness / import-gap deep-link filters.</summary>
    Task<PagedResult<EmployeeListItemDto>> SearchAsync(Guid tenantId, string? search, string? status, string? department, string? readiness, Guid? importBatchId, string? gapType, int page, int pageSize, CancellationToken cancellationToken);
    Task<EmployeeDetailDto?> GetAsync(Guid tenantId, int id, bool includeSensitive, RequestContext context, CancellationToken cancellationToken);
    Task<EmployeeDetailDto> CreateAsync(Guid tenantId, EmployeeCreateRequest request, RequestContext context, CancellationToken cancellationToken);
    Task<EmployeeDetailDto?> UpdateAsync(Guid tenantId, int id, EmployeeCreateRequest request, RequestContext context, CancellationToken cancellationToken);
    Task<EmployeeDetailDto?> ChangeStatusAsync(Guid tenantId, int id, EmployeeStatusChangeRequest request, RequestContext context, CancellationToken cancellationToken);
    Task<EmployeeDocument> UploadDocumentAsync(Guid tenantId, int employeeId, EmployeeDocumentUploadMetadata request, IFormFile file, RequestContext context, CancellationToken cancellationToken);
    Task<IReadOnlyCollection<EmployeeDocument>> GetDocumentsAsync(Guid tenantId, int employeeId, CancellationToken cancellationToken);
    Task<EmployeeDocument?> UpdateDocumentAsync(Guid tenantId, int employeeId, Guid documentId, UpdateDocumentMetadataRequest request, RequestContext context, CancellationToken cancellationToken);
    Task<EmployeeDocument?> VerifyDocumentAsync(Guid tenantId, int employeeId, Guid documentId, string? notes, RequestContext context, CancellationToken cancellationToken);
    Task<EmployeeDocument?> RejectDocumentAsync(Guid tenantId, int employeeId, Guid documentId, string reason, RequestContext context, CancellationToken cancellationToken);
    Task<bool> ArchiveDocumentAsync(Guid tenantId, int employeeId, Guid documentId, RequestContext context, CancellationToken cancellationToken);
    Task<DocumentExpiryCheckResult> CheckDocumentExpiryAsync(Guid tenantId, CancellationToken cancellationToken);
    Task<IReadOnlyCollection<EmployeeHistory>> GetHistoryAsync(Guid tenantId, int employeeId, CancellationToken cancellationToken);
    Task<EmployeeTransferRequest?> RequestTransferAsync(Guid tenantId, int employeeId, EmployeeTransferCreateRequest request, RequestContext context, CancellationToken cancellationToken);
    Task<EmployeeDetailDto?> ActivateAsync(Guid tenantId, int employeeId, EmployeeStatusChangeRequest request, RequestContext context, CancellationToken cancellationToken);
    Task<EmployeeDetailDto?> TerminateAsync(Guid tenantId, int employeeId, EmployeeStatusChangeRequest request, RequestContext context, CancellationToken cancellationToken);
    Task<EmployeeHeadcountReportDto> HeadcountAsync(Guid tenantId, CancellationToken cancellationToken);
    Task<IReadOnlyCollection<EmployeeExpiringDocumentDto>> ExpiringDocumentsAsync(Guid tenantId, int days, CancellationToken cancellationToken);
    Task<IReadOnlyCollection<EmployeeMissingDocumentsReportDto>> MissingDocumentsAsync(Guid tenantId, CancellationToken cancellationToken);
    Task<EmployeeStatusSummaryDto> StatusSummaryAsync(Guid tenantId, CancellationToken cancellationToken);
}
