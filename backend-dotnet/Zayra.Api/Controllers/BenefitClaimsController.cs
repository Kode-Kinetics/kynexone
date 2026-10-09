using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Approvals;
using Zayra.Api.Application.Auth;
using Zayra.Api.Application.Common;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.Authorization;
using Zayra.Api.Infrastructure.Benefits;
using Zayra.Api.Infrastructure.Common;
using Zayra.Api.Infrastructure.Documents;
using Zayra.Api.Infrastructure.Entitlements;
using Zayra.Api.Models;

namespace Zayra.Api.Controllers;

[ApiController]
[Route("api/compensation/benefits")]
[Authorize]
public class BenefitClaimsController(ZayraDbContext db, IApprovalRouter router, IDocumentStorage storage,
    IApprovalWorkflowService approvals, ITenantClock clock) : ControllerBase
{
    private bool IsEss => Request.Path.StartsWithSegments("/api/ess");
    private RequestContext Context() => new(HttpContext.Connection.RemoteIpAddress?.ToString(), Request.Headers.UserAgent.ToString(),
        this.GetUserId(), this.GetTenantId(), User.FindAll(ClaimTypes.Role).Select(x => x.Value).ToList(), User.FindAll("permission").Select(x => x.Value).ToList());
    private async Task<int?> OwnEmployee(Guid tenantId, bool write, CancellationToken ct)
    {
        if (User.FindFirstValue("access_mode") is "NoLogin" or "KioskOnly" || !User.HasPermission(write ? "ess.write" : "ess.read") && !( !write && User.HasPermission("ess.write"))) return null;
        return await CallerEmployeeResolver.ResolveAsync(db, User, tenantId, ct);
    }

    [HttpPost("claims")]
    [HttpPost("~/api/ess/benefits/claims")]
    [HasPermission("employees.write", "ess.write")]
    public async Task<IActionResult> Submit([FromBody] BenefitClaimRequest input, CancellationToken ct)
    {
        var tenant = this.GetTenantId(); if (tenant is null) return Unauthorized();
        if (IsEss)
        {
            var employee = await OwnEmployee(tenant.Value, true, ct);
            if (employee is null) return Forbid();
            if (!await db.BenefitEnrollments.AnyAsync(x => x.TenantId == tenant && x.Id == input.EnrollmentId && x.EmployeeId == employee, ct)) return NotFound();
        }
        else if (!User.HasPermission("employees.write")) return Forbid();
        try
        {
            var request = await BenefitClaims.SubmitAsync(db, router, storage, tenant.Value, input, Context(), clock, ct, this.GetEntityScope(), IsEss);
            return Ok(await BenefitClaims.ToDtoAsync(db, request, ct, this.GetUserId()));
        }
        catch (ApprovalRoutingException ex) { return UnprocessableEntity(new { code = ex.Code, message = ex.Message, setupUrl = "/benefits#benefit-claim-approval" }); }
        catch (InvalidOperationException ex) { return BadRequest(new { message = ex.Message }); }
    }

    [HttpGet("employees/{employeeId:int}/claims")]
    [HasPermission("employees.write")]
    public async Task<IActionResult> EmployeeClaims(int employeeId, CancellationToken ct)
    {
        var tenant = this.GetTenantId(); if (tenant is null) return Unauthorized();
        var employee = await db.Employees.AsNoTracking().FirstOrDefaultAsync(x => x.TenantId == tenant && x.Id == employeeId && !x.IsDeleted, ct);
        if (employee is null) return NotFound();
        if (!this.GetEntityScope().CanAccessCompany(employee.CompanyId)) return Forbid();
        return await List(tenant.Value, employeeId, ct);
    }
    [HttpGet("~/api/ess/benefits/claims")]
    [HasPermission("ess.read", "ess.write")]
    public async Task<IActionResult> MyClaims(CancellationToken ct)
    {
        var tenant = this.GetTenantId(); if (tenant is null) return Unauthorized();
        var employee = await OwnEmployee(tenant.Value, false, ct); if (employee is null) return Forbid();
        return await List(tenant.Value, employee.Value, ct);
    }
    private async Task<IActionResult> List(Guid tenantId, int employeeId, CancellationToken ct)
    {
        var rows = await db.ApprovalRequests.AsNoTracking().Where(x => x.TenantId == tenantId && x.EntityName == BenefitClaims.EntityName
            && x.RequestedForEmployeeId == employeeId).OrderByDescending(x => x.CreatedAtUtc).Take(100).ToListAsync(ct);
        var items = new List<BenefitClaimDto>();
        foreach (var row in rows) items.Add(await BenefitClaims.ToDtoAsync(db, row, ct, this.GetUserId()));
        return Ok(items);
    }

    [HttpGet("enrollments/{enrollmentId:guid}/claim-balance")]
    [HttpGet("~/api/ess/benefits/enrollments/{enrollmentId:guid}/claim-balance")]
    [HasPermission("employees.write", "ess.read", "ess.write")]
    public async Task<IActionResult> Balance(Guid enrollmentId, [FromQuery] DateOnly? expenseDate, CancellationToken ct)
    {
        var tenant = this.GetTenantId(); if (tenant is null) return Unauthorized();
        var row = await db.BenefitEnrollments.AsNoTracking().FirstOrDefaultAsync(x => x.TenantId == tenant && x.Id == enrollmentId, ct);
        if (row is null) return NotFound();
        if (IsEss)
        {
            if (await OwnEmployee(tenant.Value, false, ct) != row.EmployeeId) return NotFound();
        }
        else if (!User.HasPermission("employees.write") || !this.GetEntityScope().CanAccessCompany(row.CompanyId)) return Forbid();
        var result = await BenefitClaims.BalancesAsync(db, tenant.Value, row.EmployeeId, [row], await clock.TodayAsync(tenant.Value, ct), ct, expenseDate);
        return result.TryGetValue(row.Id, out var balance) ? Ok(balance) : BadRequest(new { message = "This benefit has no reimbursement policy." });
    }
    [HttpGet("claims/{claimId:guid}")]
    [HasPermission("employees.write", "approvals.read")]
    public async Task<IActionResult> Detail(Guid claimId, CancellationToken ct)
    {
        var row = await AuthorizedClaim(claimId, ct); if (row is null) return NotFound();
        return Ok(await BenefitClaims.ToDtoAsync(db, row, ct, this.GetUserId()));
    }

    [HttpPost("claims/{claimId:guid}/withdraw")]
    [HttpPost("~/api/ess/benefits/claims/{claimId:guid}/withdraw")]
    [HasPermission("employees.write", "ess.write")]
    public async Task<IActionResult> Withdraw(Guid claimId, CancellationToken ct)
    {
        var row = await AuthorizedClaim(claimId, ct); if (row is null) return NotFound();
        if (row.RequestedByUserId != this.GetUserId()) return Forbid();
        if (IsEss && await OwnEmployee(row.TenantId, true, ct) is null) return Forbid();
        try
        {
            await approvals.WithdrawAsync(row.TenantId, row.Id, "Claim withdrawn by its requester.", Context(), ct);
            var current = await db.ApprovalRequests.AsNoTracking().FirstAsync(x => x.TenantId == row.TenantId && x.Id == row.Id, ct);
            return Ok(await BenefitClaims.ToDtoAsync(db, current, ct, this.GetUserId()));
        }
        catch (InvalidOperationException ex) { return BadRequest(new { message = ex.Message }); }
    }

    [HttpPost("employees/{employeeId:int}/receipts")]
    [HttpPost("~/api/ess/benefits/receipts")]
    [HasPermission("employees.write", "ess.write")]
    [Consumes("multipart/form-data")]
    [RequestSizeLimit(EssUploadPolicy.MaxDocumentBytes + EssUploadPolicy.MultipartOverheadBytes)]
    [RequestFormLimits(MultipartBodyLengthLimit = EssUploadPolicy.MaxDocumentBytes + EssUploadPolicy.MultipartOverheadBytes)]
    public async Task<IActionResult> Upload([FromForm] IFormFile file, CancellationToken ct, int? employeeId = null)
    {
        var tenant = this.GetTenantId(); if (tenant is null) return Unauthorized();
        if (await EntitlementMatrixService.ReleaseAEnabledAsync(db, tenant.Value, ct)) return Conflict(new { message = "Use the current Benefits by grade authority for this tenant." });
        if (IsEss) employeeId = await OwnEmployee(tenant.Value, true, ct);
        else if (!User.HasPermission("employees.write")) return Forbid();
        if (!employeeId.HasValue) return Forbid();
        var employee = await db.Employees.AsNoTracking().FirstOrDefaultAsync(x => x.TenantId == tenant && x.Id == employeeId && !x.IsDeleted, ct);
        if (employee is null) return NotFound();
        if (!IsEss && !this.GetEntityScope().CanAccessCompany(employee.CompanyId)) return Forbid();
        if (file is null || file.Length > EssUploadPolicy.MaxDocumentBytes) return BadRequest(new { message = "Upload a receipt no larger than 10 MB." });
        await using var stream = new MemoryStream(); await file.CopyToAsync(stream, ct); var bytes = stream.ToArray();
        var verdict = EssUploadPolicy.Check(file.ContentType, file.FileName, bytes, EssUploadPolicy.MaxDocumentBytes, EssUploadPolicy.DocumentTypes);
        if (!verdict.Ok) return BadRequest(new { message = verdict.Error });
        await using var safeStream = new MemoryStream(bytes);
        var safeFile = new FormFile(safeStream, 0, bytes.Length, "file", verdict.FileName) { Headers = new HeaderDictionary(), ContentType = verdict.ContentType };
        var stored = await storage.SaveAsync(tenant.Value, safeFile, ct);
        var document = new EmployeeDocument { TenantId = tenant.Value, CompanyId = employee.CompanyId, EmployeeId = employee.Id,
            DocumentType = BenefitClaims.ReceiptType, DocumentCategory = "Benefit claim evidence", FileName = verdict.FileName, ContentType = verdict.ContentType,
            StorageUrl = stored.StorageUrl, UploadedBy = this.GetUserId(), ApprovalStatus = "Uploaded" };
        db.EmployeeDocuments.Add(document);
        db.AuditLogs.Add(new AuditLog { TenantId = tenant.Value, CompanyId = employee.CompanyId, UserId = this.GetUserId(),
            EntityName = nameof(EmployeeDocument), EntityId = document.Id.ToString(), Action = "benefits.receipt.uploaded",
            Metadata = JsonSerializer.Serialize(new { document.EmployeeId, document.VersionNumber, sha256 = BenefitClaims.Hash(bytes) }) });
        try { await db.SaveChangesAsync(ct); }
        catch { await storage.TryDeleteAsync(tenant.Value, stored.StorageUrl, CancellationToken.None); throw; }
        return Ok(new BenefitReceiptDto(document.Id, document.FileName, document.ContentType, document.VersionNumber));
    }

    [HttpGet("claims/{claimId:guid}/receipts/{documentId:guid}/download")]
    [HttpGet("~/api/ess/benefits/claims/{claimId:guid}/receipts/{documentId:guid}/download")]
    [HasPermission("employees.write", "approvals.read", "ess.read", "ess.write")]
    public async Task<IActionResult> Download(Guid claimId, Guid documentId, CancellationToken ct)
    {
        var row = await AuthorizedClaim(claimId, ct); if (row is null) return NotFound();
        var witness = BenefitClaims.Read(row).Receipts.FirstOrDefault(x => x.Id == documentId); if (witness is null) return NotFound();
        var bytes = await storage.GetBytesAsync(row.TenantId, witness.StorageKey, ct);
        if (BenefitClaims.Hash(bytes) != witness.Sha256) return Conflict(new { message = "Receipt evidence failed its integrity check." });
        BenefitClaims.Audit(db, row, this.GetUserId(), "benefits.receipt.downloaded", new { documentId });
        await db.SaveChangesAsync(ct);
        return File(bytes, witness.ContentType, witness.FileName);
    }
    private async Task<ApprovalRequest?> AuthorizedClaim(Guid id, CancellationToken ct)
    {
        var tenant = this.GetTenantId(); if (tenant is null) return null;
        var row = await db.ApprovalRequests.AsNoTracking().FirstOrDefaultAsync(x => x.TenantId == tenant && x.EntityName == BenefitClaims.EntityName && x.Id == id, ct);
        if (row is null) return null;
        if (IsEss) return await OwnEmployee(tenant.Value, false, ct) == row.RequestedForEmployeeId ? row : null;
        if (!this.GetEntityScope().CanAccessCompany(row.CompanyId)) return null;
        if (User.HasPermission("employees.write")) return row;
        return await approvals.GetRequestAsync(tenant.Value, id, Context(), ct) is null ? null : row;
    }
}
