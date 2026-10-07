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
    private readonly Zayra.Api.Infrastructure.Contracts.IContractTermLifecycleDispatcher? _termLifecycle;
    private readonly Zayra.Api.Application.Common.ITenantClock? _clock;

    /// <param name="termLifecycle">Release A term-activation hooks (chain stamp, package freeze). Optional so direct
    /// constructions keep compiling; the dispatcher itself is a no-op unless the tenant has release_a on.</param>
    /// <param name="clock">Tenant-local "today" for the Release A replacement check (UTC date when absent).</param>
    public ContractsController(ZayraDbContext db,
        Zayra.Api.Infrastructure.Contracts.IContractTermLifecycleDispatcher? termLifecycle = null,
        Zayra.Api.Application.Common.ITenantClock? clock = null)
    {
        _db = db;
        _termLifecycle = termLifecycle;
        _clock = clock;
    }

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
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 100);
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
        var mergedFromTemplate = false;
        if (req.TemplateId.HasValue)
        {
            var tmpl = await _db.ContractTemplates.FirstOrDefaultAsync(x => x.Id == req.TemplateId.Value && x.TenantId == tid, ct);
            if (tmpl != null)
            {
                htmlEn = string.IsNullOrEmpty(htmlEn) ? tmpl.ContentHtmlEn : htmlEn;
                htmlAr = string.IsNullOrEmpty(htmlAr) ? tmpl.ContentHtmlAr : htmlAr;

                // ── Merge fields ────────────────────────────────────────────────────────────
                // The template body used to be copied verbatim, so a template written with
                // {{employee_name}} produced a contract that literally said "{{employee_name}}" —
                // and ContractTemplate.Variables, the declared merge-field list, was written on
                // create and read by nothing. Both halves are live now: the declaration is checked
                // against what this build can supply, and the body is filled. See
                // ContractMergeFields for why an unresolved placeholder refuses rather than ships.
                if (Zayra.Api.Infrastructure.Compliance.ContractMergeFields
                        .ValidateDeclaredVariables(tmpl.Variables) is { } declarationError)
                    return BadRequest(new { error = declarationError.Code, message = declarationError.Message });

                var companyName = employee.CompanyId is { } companyId
                    ? await _db.Companies.Where(c => c.TenantId == tid && c.Id == companyId)
                        // The legal name, not the trade name: this is the party to the contract.
                        .Select(c => c.LegalNameEn).FirstOrDefaultAsync(ct) ?? string.Empty
                    : string.Empty;

                var values = Zayra.Api.Infrastructure.Compliance.ContractMergeFields.BuildValues(
                    employeeName: employee.FullName,
                    employeeCode: employee.EmployeeCode,
                    designation: employee.Designation,
                    department: employee.Department,
                    startDate: req.StartDate,
                    endDate: req.EndDate,
                    basicSalary: req.BasicSalary,
                    currency: contractCurrency,
                    contractNumber: contractNumber,
                    contractType: req.ContractType ?? "Employment",
                    companyName: companyName);

                var mergedEn = Zayra.Api.Infrastructure.Compliance.ContractMergeFields.Merge(htmlEn, values);
                if (!mergedEn.IsSuccess)
                    return BadRequest(new { error = mergedEn.Error!.Value.Code, message = mergedEn.Error!.Value.Message, language = "en" });
                var mergedAr = Zayra.Api.Infrastructure.Compliance.ContractMergeFields.Merge(htmlAr, values);
                if (!mergedAr.IsSuccess)
                    return BadRequest(new { error = mergedAr.Error!.Value.Code, message = mergedAr.Error!.Value.Message, language = "ar" });

                htmlEn = mergedEn.Html;
                htmlAr = mergedAr.Html;
                mergedFromTemplate = true;
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
            MetadataJson = System.Text.Json.JsonSerializer.Serialize(new { contractNumber, contract.ContractType, mergedFromTemplate }),
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
        // Release A: every change to a term's status runs under the per-employee package lock, in one transaction, so two
        // activations (or an activation and a freeze) for the same employee cannot both pass the writer's overlap check.
        var employeeId = await _db.EmployeeContracts.AsNoTracking()
            .Where(x => x.Id == id && x.TenantId == tid && !x.IsDeleted).Select(x => (Guid?)x.EmployeeId).FirstOrDefaultAsync(ct);
        if (employeeId is null) return NotFound();
        try
        {
            return await Zayra.Api.Infrastructure.Finance.FinanceDecisionSerializer.SerializeAsync(_db,
                Zayra.Api.Infrastructure.Finance.FinanceDecisionSerializer.ScopeEmployeePackage, tid, employeeId.Value,
                () => UpdateStatusCoreAsync(id, req, tid, ct), ct);
        }
        catch (Zayra.Api.Infrastructure.Entitlements.EntitlementLifecycleBlockedException ex)
        {
            return PackageConflict(ex.Code, ex.Message, ex.PossibleFrom);
        }
        catch (Exception ex) when (Zayra.Api.Infrastructure.Entitlements.PackageReasons.FromDatabase(ex) is { } code)
        {
            // A DbUpdateException at SaveChanges, or a bare PostgresException from a deferred trigger at COMMIT.
            return PackageConflict(code, "The contract's benefits don't fit this change.", null);
        }
    }

    private ObjectResult PackageConflict(string code, string message, DateOnly? possibleFrom) =>
        Conflict(new { error = code, message, possibleFrom, reason = Zayra.Api.Infrastructure.Entitlements.PackageReasons.Describe(code) });

    private async Task<IActionResult> UpdateStatusCoreAsync(Guid id, UpdateContractStatusRequest req, Guid tid, CancellationToken ct)
    {
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
        var today = _clock is not null ? await _clock.TodayAsync(tid, ct) : DateOnly.FromDateTime(DateTime.UtcNow);
        if (requested == "Expired" && (!contract.EndDate.HasValue || contract.EndDate.Value > today))
            return BadRequest(new { error = "contract_not_expired", message = "A contract can only be marked Expired on or after its recorded end date." });

        contract.Status = requested;
        contract.UpdatedAtUtc = DateTime.UtcNow;

        if (requested == "Active")
        {
            contract.SignedByHrName = req.SignedByHrName!.Trim();
            contract.SignedByHrAtUtc = DateTime.UtcNow;
            // Release A: stamp the chain and freeze the contract-year package in this same SaveChanges.
            if (_termLifecycle is not null) await _termLifecycle.OnActivatedAsync(contract, ct);
        }
        else if (old == "Active" && (requested is "Expired" or "Terminated") && _termLifecycle is not null)
        {
            // Release A, in this SaveChanges. Terminated cancels an open renewal case (T21) and closes the package;
            // Expired is RECORD-ONLY — the employee working on renews the contract by law (Art. 74(2)), so the case
            // stays open for the holdover (T22, R6). See IContractTermLifecycle.OnEndedAsync.
            await _termLifecycle.OnEndedAsync(contract, requested == "Expired"
                ? Zayra.Api.Application.Entitlements.ContractEndReasons.Expired
                : Zayra.Api.Application.Entitlements.ContractEndReasons.Terminated, ct);
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
        // Release A: under the per-employee package lock, in one transaction — a supersede and a proposal confirm (or an
        // activation) for the same employee are serialised, so neither commits onto a term the other just replaced.
        var employeeId = await _db.EmployeeContracts.AsNoTracking()
            .Where(x => x.Id == id && x.TenantId == tid && !x.IsDeleted).Select(x => (Guid?)x.EmployeeId).FirstOrDefaultAsync(ct);
        if (employeeId is null) return NotFound();
        try
        {
            return await Zayra.Api.Infrastructure.Finance.FinanceDecisionSerializer.SerializeAsync(_db,
                Zayra.Api.Infrastructure.Finance.FinanceDecisionSerializer.ScopeEmployeePackage, tid, employeeId.Value,
                () => SupersedeCoreAsync(id, req, tid, ct), ct);
        }
        catch (Zayra.Api.Infrastructure.Entitlements.EntitlementLifecycleBlockedException ex)
        {
            return PackageConflict(ex.Code, ex.Message, ex.PossibleFrom);
        }
        catch (Exception ex) when (Zayra.Api.Infrastructure.Entitlements.PackageReasons.FromDatabase(ex) is { } code)
        {
            return PackageConflict(code, "The contract's benefits don't fit this change.", null);
        }
    }

    private async Task<IActionResult> SupersedeCoreAsync(Guid id, CreateContractRequest req, Guid tid, CancellationToken ct)
    {
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
        // Release A: an open renewal review follows its term (RenewalCaseCarry). An amendment within the term is allowed and
        // the review moves onto the new version when that version is activated; a new term after the end is the renewal's
        // own decision, and a changed end date once an action is chosen is refused.
        var carry = await Zayra.Api.Infrastructure.Contracts.RenewalCaseCarry.DecideAsync(_db, tid, old, req.StartDate, req.EndDate, ct);
        if (carry.RefusalCode == Zayra.Api.Application.Contracts.ReleaseABlockReasons.RenewalCaseInProgress)
        {
            var reason = Zayra.Api.Application.Contracts.ReleaseABlockReasons.All[carry.RefusalCode];
            return Conflict(new { error = carry.RefusalCode, reason, message = reason.WhyEn, messageAr = reason.WhyAr });
        }
        if (carry.RefusalCode is not null)
            return Conflict(new { error = carry.RefusalCode, message = carry.RefusalEn, messageAr = carry.RefusalAr });

        // Release A: refuse BEFORE anything changes when the replacement would need a fixed benefit that has not started
        // removed (the database never removes one) — the current version then simply stays in force.
        var today = _clock is not null ? await _clock.TodayAsync(tid, ct) : DateOnly.FromDateTime(DateTime.UtcNow);
        if (await Zayra.Api.Infrastructure.Entitlements.EntitlementWriter.ReplacementBlockAsync(_db, tid, old.Id, req.StartDate, today, ct) is { } blocked)
            return PackageConflict(blocked.Code, blocked.Message, blocked.PossibleFrom);

        old.Status = "Superseded";
        old.UpdatedAtUtc = DateTime.UtcNow;
        if (_termLifecycle is not null)
            await _termLifecycle.OnEndedAsync(old, Zayra.Api.Application.Entitlements.ContractEndReasons.Superseded, ct);

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
            // Release A: an amendment is the same worker under the same chain — keep the stamped nationality class, so
            // nationality-scoped benefits are not "needs confirmation" on every new version.
            // The same term's renewal terms carry over to the new version (Release A).
            AutoRenew = old.AutoRenew,
            NonRenewalNoticeDays = old.NonRenewalNoticeDays,
            WorkerNationalityClass = old.WorkerNationalityClass,
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
