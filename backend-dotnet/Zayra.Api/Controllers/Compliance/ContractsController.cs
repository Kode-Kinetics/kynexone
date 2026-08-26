using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Common;
using Zayra.Api.Application.Employees;
using Zayra.Api.Data;
using Zayra.Api.Models;

namespace Zayra.Api.Controllers.Compliance;

// Employment contracts carry compensation terms and other sensitive employment data. Contract
// management is an HR-administrative function, so the whole controller (READS included) is gated to
// HR roles. Previously only [Authorize] guarded the class, so GET / and GET /{id} were open to any
// authenticated tenant user, leaking every employee's contract terms (IDOR, CWE-639). Write actions
// keep their stricter Admin/HR-Manager attributes.
[Authorize(Roles = "Admin,HR Manager,HR Officer")]
[ApiController]
[Route("api/compliance/contracts")]
public class ContractsController : ControllerBase
{
    private static readonly IReadOnlyDictionary<string, string[]> AllowedTransitions =
        new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["Draft"] = ["PendingApproval"],
            ["PendingApproval"] = ["Draft", "Active"],
            ["Active"] = ["Expired", "Terminated"],
            ["Expired"] = [],
            ["Terminated"] = [],
            ["Superseded"] = [],
        };

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
        var identity = await _db.ResolveEmployeeAsync(tid, req.EmployeeId, null, ct);
        if (!identity.IsSuccess) return BadRequest(identity.Error);
        var employee = identity.Employee!;
        if (req.StartDate == default || (req.EndDate.HasValue && req.EndDate.Value < req.StartDate))
            return BadRequest(new { error = "invalid_contract_dates", message = "End date must be on or after the start date." });
        if (req.BasicSalary < 0m)
            return BadRequest(new { error = "invalid_basic_salary", message = "Basic salary cannot be negative." });

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
            CompanyId = employee.CompanyId,
            EmployeeId = employee.PublicId,
            EmployeeName = employee.FullName,
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
            EmployeeId = employee.PublicId,
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

        var requested = req.Status?.Trim();
        if (string.IsNullOrWhiteSpace(requested)
            || !AllowedTransitions.ContainsKey(requested))
            return BadRequest(new
            {
                error = "invalid_contract_status",
                message = $"Status must be one of: {string.Join(", ", AllowedTransitions.Keys)}.",
            });

        var old = contract.Status;
        if (string.Equals(old, requested, StringComparison.Ordinal))
            return Conflict(new { error = "contract_status_unchanged", message = $"Contract is already '{old}'." });
        if (!AllowedTransitions.TryGetValue(old, out var allowed) || !allowed.Contains(requested, StringComparer.Ordinal))
            return Conflict(new
            {
                error = "invalid_contract_transition",
                message = $"Contract cannot transition from '{old}' to '{requested}'.",
                currentStatus = old,
                allowedStatuses = allowed ?? [],
            });
        if (requested == "Active" && string.IsNullOrWhiteSpace(req.SignedByHrName))
            return BadRequest(new { error = "hr_signature_required", message = "HR signatory name is required to activate a contract." });
        if (requested == "Expired" && (!contract.EndDate.HasValue || contract.EndDate.Value > DateOnly.FromDateTime(DateTime.UtcNow)))
            return BadRequest(new { error = "contract_not_expired", message = "A contract can only be marked Expired on or after its recorded end date." });

        contract.Status = requested;
        contract.UpdatedAtUtc = DateTime.UtcNow;

        if (requested == "Active")
        {
            contract.SignedByHrName = req.SignedByHrName!.Trim();
            contract.SignedByHrAtUtc = DateTime.UtcNow;
        }

        _db.ComplianceAuditLogs.Add(new ComplianceAuditLog
        {
            TenantId = tid, EntityType = "Contract", EntityId = id.ToString(),
            EmployeeId = contract.EmployeeId,
            Action = "StatusChanged", PerformedByUserId = GetUserId(), PerformedByName = GetUserName(),
            MetadataJson = System.Text.Json.JsonSerializer.Serialize(new { from = old, to = requested }),
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
        if (old.Status != "Active")
            return Conflict(new
            {
                error = "invalid_contract_transition",
                message = $"Only an Active contract can be superseded (current: '{old.Status}').",
                currentStatus = old.Status,
            });
        if (req.StartDate == default || req.StartDate < old.StartDate
            || (req.EndDate.HasValue && req.EndDate.Value < req.StartDate))
            return BadRequest(new { error = "invalid_contract_dates", message = "The replacement contract must start on or after the prior start date and end on or after its start date." });
        if (req.BasicSalary < 0m)
            return BadRequest(new { error = "invalid_basic_salary", message = "Basic salary cannot be negative." });

        old.Status = "Superseded";
        old.UpdatedAtUtc = DateTime.UtcNow;

        var count = await _db.EmployeeContracts.CountAsync(x => x.TenantId == tid, ct);
        var contractNumber = $"CON-{DateTime.UtcNow.Year}-{(count + 1):D4}";

        var newContract = new EmployeeContract
        {
            TenantId = tid, CompanyId = old.CompanyId,
            EmployeeId = old.EmployeeId, EmployeeName = old.EmployeeName,
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
