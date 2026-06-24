using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Common;
using Zayra.Api.Data;
using Zayra.Api.Models;

namespace Zayra.Api.Controllers.Compliance;

[Authorize]
[ApiController]
[Route("api/compliance/contracts")]
public class ContractsController : ControllerBase
{
    private readonly ZayraDbContext _db;
    public ContractsController(ZayraDbContext db) => _db = db;

    private Guid GetTenantId() =>
        Guid.TryParse(User.FindFirst("tenant_id")?.Value, out var id) ? id : Guid.Empty;

    private Guid? GetUserId() =>
        Guid.TryParse(User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value, out var id) ? id : null;

    private string GetUserName() => User.FindFirst("name")?.Value ?? User.Identity?.Name ?? "System";

    // ── Contract Templates ─────────────────────────────────────────────────────

    [HttpGet("templates")]
    public async Task<IActionResult> ListTemplates([FromQuery] bool activeOnly = true, CancellationToken ct = default)
    {
        var tid = GetTenantId();
        var q = _db.ContractTemplates.Where(x => x.TenantId == tid && !x.IsDeleted);
        if (activeOnly) q = q.Where(x => x.IsActive);

        var items = await q.OrderBy(x => x.NameEn).ToListAsync(ct);
        return Ok(items);
    }

    [HttpGet("templates/{id:guid}")]
    public async Task<IActionResult> GetTemplate(Guid id, CancellationToken ct)
    {
        var tid = GetTenantId();
        var template = await _db.ContractTemplates
            .FirstOrDefaultAsync(x => x.Id == id && x.TenantId == tid && !x.IsDeleted, ct);
        if (template == null) return NotFound();
        return Ok(template);
    }

    [HttpPost("templates")]
    [Authorize(Roles = "Admin,HR Manager")]
    public async Task<IActionResult> CreateTemplate([FromBody] CreateContractTemplateRequest req, CancellationToken ct)
    {
        var tid = GetTenantId();

        var template = new ContractTemplate
        {
            TenantId = tid,
            Code = req.Code,
            NameEn = req.NameEn,
            NameAr = req.NameAr ?? string.Empty,
            ContractType = req.ContractType,
            Language = req.Language ?? "en",
            ContentHtmlEn = req.ContentHtmlEn ?? string.Empty,
            ContentHtmlAr = req.ContentHtmlAr ?? string.Empty,
            Variables = req.Variables ?? string.Empty,
            CountryCode = req.CountryCode ?? "AE",
            CreatedByUserId = GetUserId(),
        };

        _db.ContractTemplates.Add(template);

        _db.ComplianceAuditLogs.Add(new ComplianceAuditLog
        {
            TenantId = tid, EntityType = "ContractTemplate", EntityId = template.Id.ToString(),
            Action = "Created", PerformedByUserId = GetUserId(), PerformedByName = GetUserName(),
            MetadataJson = System.Text.Json.JsonSerializer.Serialize(new { template.Code, template.NameEn }),
        });

        await _db.SaveChangesAsync(ct);
        return Ok(template);
    }

    // ── Employee Contracts ─────────────────────────────────────────────────────

    [HttpGet]
    public async Task<IActionResult> List(
        [FromQuery] Guid? employeeId = null,
        [FromQuery] string? status = null,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        CancellationToken ct = default)
    {
        var tid = GetTenantId();
        var q = _db.EmployeeContracts.Where(x => x.TenantId == tid && !x.IsDeleted);

        if (employeeId.HasValue) q = q.Where(x => x.EmployeeId == employeeId.Value);
        if (!string.IsNullOrEmpty(status)) q = q.Where(x => x.Status == status);

        var total = await q.CountAsync(ct);
        var items = await q.OrderByDescending(x => x.CreatedAtUtc)
            .Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);

        return Ok(new { total, page, pageSize, items });
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct)
    {
        var tid = GetTenantId();
        var contract = await _db.EmployeeContracts
            .FirstOrDefaultAsync(x => x.Id == id && x.TenantId == tid && !x.IsDeleted, ct);
        if (contract == null) return NotFound();
        return Ok(contract);
    }

    [HttpPost]
    [Authorize(Roles = "Admin,HR Manager")]
    public async Task<IActionResult> Create([FromBody] CreateContractRequest req, CancellationToken ct)
    {
        var tid = GetTenantId();

        // EmployeeId in contracts is a Guid reference; name accepted from request

        var count = await _db.EmployeeContracts.CountAsync(x => x.TenantId == tid, ct);
        var contractNumber = $"CON-{DateTime.UtcNow.Year}-{(count + 1):D4}";
        var contractCurrency = !string.IsNullOrWhiteSpace(req.CurrencyCode) ? req.CurrencyCode : await _db.ResolveTenantCurrencyAsync(tid, ct);

        string htmlEn = req.ContentHtmlEn ?? string.Empty;
        string htmlAr = req.ContentHtmlAr ?? string.Empty;

        // If template provided, use its content
        if (req.TemplateId.HasValue)
        {
            var tmpl = await _db.ContractTemplates.FirstOrDefaultAsync(x => x.Id == req.TemplateId.Value && x.TenantId == tid, ct);
            if (tmpl != null)
            {
                htmlEn = string.IsNullOrEmpty(htmlEn) ? tmpl.ContentHtmlEn : htmlEn;
                htmlAr = string.IsNullOrEmpty(htmlAr) ? tmpl.ContentHtmlAr : htmlAr;
            }
        }

        var contract = new EmployeeContract
        {
            TenantId = tid,
            EmployeeId = req.EmployeeId,
            EmployeeName = req.EmployeeName ?? string.Empty,
            TemplateId = req.TemplateId,
            ContractNumber = contractNumber,
            ContractType = req.ContractType ?? "Employment",
            StartDate = req.StartDate,
            EndDate = req.EndDate,
            BasicSalary = req.BasicSalary,
            CurrencyCode = contractCurrency,
            ContentHtmlEn = htmlEn,
            ContentHtmlAr = htmlAr,
            Language = req.Language ?? "en",
            CreatedByUserId = GetUserId(),
        };

        _db.EmployeeContracts.Add(contract);

        _db.ComplianceAuditLogs.Add(new ComplianceAuditLog
        {
            TenantId = tid, EntityType = "Contract", EntityId = contract.Id.ToString(),
            EmployeeId = req.EmployeeId,
            Action = "Created", PerformedByUserId = GetUserId(), PerformedByName = GetUserName(),
            MetadataJson = System.Text.Json.JsonSerializer.Serialize(new { contractNumber, contract.ContractType }),
        });

        await _db.SaveChangesAsync(ct);
        return CreatedAtAction(nameof(Get), new { id = contract.Id }, contract);
    }

    // PATCH /api/compliance/contracts/{id}/status
    [HttpPatch("{id:guid}/status")]
    [Authorize(Roles = "Admin,HR Manager")]
    public async Task<IActionResult> UpdateStatus(Guid id, [FromBody] UpdateContractStatusRequest req, CancellationToken ct)
    {
        var tid = GetTenantId();
        var contract = await _db.EmployeeContracts
            .FirstOrDefaultAsync(x => x.Id == id && x.TenantId == tid && !x.IsDeleted, ct);
        if (contract == null) return NotFound();

        var old = contract.Status;
        contract.Status = req.Status;
        contract.UpdatedAtUtc = DateTime.UtcNow;

        if (req.Status == "Active" && !string.IsNullOrEmpty(req.SignedByHrName))
        {
            contract.SignedByHrName = req.SignedByHrName;
            contract.SignedByHrAtUtc = DateTime.UtcNow;
        }

        _db.ComplianceAuditLogs.Add(new ComplianceAuditLog
        {
            TenantId = tid, EntityType = "Contract", EntityId = id.ToString(),
            EmployeeId = contract.EmployeeId,
            Action = "StatusChanged", PerformedByUserId = GetUserId(), PerformedByName = GetUserName(),
            MetadataJson = System.Text.Json.JsonSerializer.Serialize(new { from = old, to = req.Status }),
        });

        await _db.SaveChangesAsync(ct);
        return Ok(contract);
    }

    // POST /api/compliance/contracts/{id}/supersede — Create new version
    [HttpPost("{id:guid}/supersede")]
    [Authorize(Roles = "Admin,HR Manager")]
    public async Task<IActionResult> Supersede(Guid id, [FromBody] CreateContractRequest req, CancellationToken ct)
    {
        var tid = GetTenantId();
        var old = await _db.EmployeeContracts
            .FirstOrDefaultAsync(x => x.Id == id && x.TenantId == tid && !x.IsDeleted, ct);
        if (old == null) return NotFound();

        old.Status = "Superseded";
        old.UpdatedAtUtc = DateTime.UtcNow;

        var count = await _db.EmployeeContracts.CountAsync(x => x.TenantId == tid, ct);
        var contractNumber = $"CON-{DateTime.UtcNow.Year}-{(count + 1):D4}";

        var newContract = new EmployeeContract
        {
            TenantId = tid, EmployeeId = old.EmployeeId, EmployeeName = old.EmployeeName,
            TemplateId = req.TemplateId ?? old.TemplateId,
            ContractNumber = contractNumber, ContractType = req.ContractType ?? old.ContractType,
            StartDate = req.StartDate, EndDate = req.EndDate,
            BasicSalary = req.BasicSalary, CurrencyCode = req.CurrencyCode ?? old.CurrencyCode,
            ContentHtmlEn = req.ContentHtmlEn ?? old.ContentHtmlEn,
            ContentHtmlAr = req.ContentHtmlAr ?? old.ContentHtmlAr,
            Language = req.Language ?? old.Language,
            Version = old.Version + 1,
            PreviousVersionId = old.Id,
            CreatedByUserId = GetUserId(),
        };

        _db.EmployeeContracts.Add(newContract);

        _db.ComplianceAuditLogs.Add(new ComplianceAuditLog
        {
            TenantId = tid, EntityType = "Contract", EntityId = newContract.Id.ToString(),
            EmployeeId = old.EmployeeId,
            Action = "Superseded", PerformedByUserId = GetUserId(), PerformedByName = GetUserName(),
            MetadataJson = System.Text.Json.JsonSerializer.Serialize(new { previousId = id, newVersion = newContract.Version }),
        });

        await _db.SaveChangesAsync(ct);
        return Ok(newContract);
    }
}

public record CreateContractTemplateRequest(
    string Code, string NameEn, string? NameAr, string ContractType,
    string? Language, string? ContentHtmlEn, string? ContentHtmlAr,
    string? Variables, string? CountryCode);

public record CreateContractRequest(
    Guid EmployeeId, string? EmployeeName, Guid? TemplateId, string? ContractType,
    DateOnly StartDate, DateOnly? EndDate, decimal BasicSalary,
    string? CurrencyCode, string? ContentHtmlEn, string? ContentHtmlAr, string? Language);

public record UpdateContractStatusRequest(string Status, string? SignedByHrName);
