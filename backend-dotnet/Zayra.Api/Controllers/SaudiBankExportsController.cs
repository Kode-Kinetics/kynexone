using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Common;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Payroll.SaudiBankExports;

namespace Zayra.Api.Controllers;

/// <summary>
/// Saudi bank-instruction export (IMPLEMENTATION-CONTRACT.md). Produces a bank INSTRUCTION file for an
/// authorised person to upload in the bank channel — it moves no money, calls no bank, and never changes
/// payroll, payment, WpsStatus or GL. Supported formats are <c>anb-connect-csv-v1</c> and
/// <c>custom:&lt;templateId&gt;</c> (a customer-configured layout whose approved version a second person
/// signed off); any other format id fails closed.
///
/// <para>Security: every action needs a tenant, an actor and <c>payroll.export</c>; settings mutation
/// also needs <c>payroll.structure_manage</c>. Batch actions authorise through the shared
/// <see cref="PaymentBatchScopeExtensions.PaymentBatchScopeErrorAsync"/> (company from batch → run, never
/// from the request) BEFORE any batch data is read. Settings actions authorise the path company
/// against <see cref="ControllerTenantExtensions.GetEntityScope"/>.</para>
/// </summary>
[ApiController]
[SaudiBankExportIntegrity]
[Route("api/payroll/bank-exports")]
[Authorize]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class SaudiBankExportsController : ControllerBase
{
    private readonly ZayraDbContext _db;
    private readonly SaudiBankExportService _service;
    private readonly CustomBankTemplateService _templates;
    private readonly IConfiguration? _activationConfiguration;

    public SaudiBankExportsController(ZayraDbContext db, ISaudiBankEmployeeAddressSource? addresses = null, IConfiguration? configuration = null)
    {
        _db = db;
        _activationConfiguration = configuration;
        _service = new SaudiBankExportService(db, addresses);
        _templates = new CustomBankTemplateService(db);
    }

    [HttpGet("formats")]
    public IActionResult Formats()
    {
        if (Actor() is not { } a) return Forbid();
        if (!SaudiBankExportActivation.EnabledCompanies(_activationConfiguration, a.TenantId)
                .Any(c => this.GetEntityScope().CanAccessCompany(c))) return Disabled();
        return Ok(SaudiBankExportFormats.Supported);
    }

    [HttpGet("companies/{companyId:guid}/settings")]
    public async Task<IActionResult> GetSettings(Guid companyId, CancellationToken ct)
    {
        if (Actor() is not { } a) return Forbid();
        if (await CompanyScopeErrorAsync(a.TenantId, companyId, ct) is { } err) return err;
        if (!SaudiBankExportActivation.IsEnabled(_activationConfiguration, a.TenantId, companyId)) return Disabled();
        return Ok(await _service.GetSettingsAsync(a.TenantId, companyId, ct));
    }

    [HttpPut("companies/{companyId:guid}/settings")]
    public async Task<IActionResult> PutSettings(Guid companyId, [FromBody] SaudiBankExportSettingsDto body, CancellationToken ct)
    {
        if (Actor() is not { } a || !HasPermission("payroll.structure_manage")) return Forbid();
        if (await CompanyScopeErrorAsync(a.TenantId, companyId, ct) is { } err) return err;
        if (!SaudiBankExportActivation.IsEnabled(_activationConfiguration, a.TenantId, companyId)) return Disabled();
        if (body is null) return BadRequest(new { error = "settings_required" });
        var result = await _service.SaveSettingsAsync(a.TenantId, companyId, a.UserId, body, ct);
        return Map(result, v => Ok(v));
    }

    // Availability is the only off-state discovery route. It returns no settings or bank data.
    [HttpGet("batches/{batchId:guid}/availability")]
    public async Task<IActionResult> Availability(Guid batchId, CancellationToken ct)
    {
        if (Actor() is not { } a) return Forbid();
        if (await this.PaymentBatchScopeErrorAsync(_db, a.TenantId, batchId, ct) is { } err) return err;
        var companyId = await BatchCompanyAsync(a.TenantId, batchId, ct);
        return Ok(new SaudiBankExportAvailabilityDto(companyId is Guid c
            && SaudiBankExportActivation.IsEnabled(_activationConfiguration, a.TenantId, c)));
    }

    [HttpGet("batches/{batchId:guid}/context")]
    public async Task<IActionResult> Context(Guid batchId, CancellationToken ct)
    {
        if (Actor() is not { } a) return Forbid();
        if (await BatchScopeAndActivationErrorAsync(a.TenantId, batchId, ct) is { } err) return err;
        return Map(await _service.GetContextAsync(a.TenantId, batchId, ct), v => Ok(v));
    }

    [HttpPost("batches/{batchId:guid}/validate")]
    public async Task<IActionResult> Validate(Guid batchId, [FromBody] SaudiBankExportBatchRequest body, CancellationToken ct)
    {
        if (Actor() is not { } a) return Forbid();
        if (await BatchScopeAndActivationErrorAsync(a.TenantId, batchId, ct) is { } err) return err;
        return Map(await _service.ValidateAsync(a.TenantId, batchId, body ?? new SaudiBankExportBatchRequest(), ct), v => Ok(v));
    }

    [HttpPost("batches/{batchId:guid}/generate")]
    public async Task<IActionResult> Generate(Guid batchId, [FromBody] SaudiBankExportBatchRequest body, CancellationToken ct)
    {
        if (Actor() is not { } a) return Forbid();
        if (await BatchScopeAndActivationErrorAsync(a.TenantId, batchId, ct) is { } err) return err;
        return Map(await _service.GenerateAsync(a.TenantId, a.UserId, batchId, body ?? new SaudiBankExportBatchRequest(), ct), v => Ok(v));
    }

    [HttpGet("batches/{batchId:guid}/download")]
    public async Task<IActionResult> Download(Guid batchId, CancellationToken ct)
    {
        if (Actor() is not { } a) return Forbid();
        if (await BatchScopeAndActivationErrorAsync(a.TenantId, batchId, ct) is { } err) return err;
        var result = await _service.DownloadAsync(a.TenantId, a.UserId, batchId, ct);
        return Map(result, v =>
        {
            Response.Headers.CacheControl = "private, no-store";
            Response.Headers.Pragma = "no-cache";
            Response.Headers["X-Content-Type-Options"] = "nosniff";
            return File(v.ZipBytes, v.ContentType, v.FileName);
        });
    }

    // ── Custom bank templates (layout only; maker-checker) ────────────────────────────────────────
    // A template is a customer-configured LAYOUT. It never relaxes a Saudi wage-file rule. Creating or
    // changing one needs payroll.structure_manage and stages a PENDING version; a different person with
    // payroll.approve (Finance) approves exactly what they reviewed. Template routes answer 404
    // for another tenant's or an inaccessible company's template (no existence oracle).

    [HttpGet("templates/catalog")]
    public IActionResult TemplateCatalog()
    {
        if (Actor() is not { } a) return Forbid();
        if (!SaudiBankExportActivation.EnabledCompanies(_activationConfiguration, a.TenantId)
                .Any(c => this.GetEntityScope().CanAccessCompany(c))) return Disabled();
        return Ok(CustomBankTemplateStarters.Catalog());
    }

    [HttpGet("companies/{companyId:guid}/templates")]
    public async Task<IActionResult> ListTemplates(Guid companyId, CancellationToken ct)
    {
        if (Actor() is not { } a) return Forbid();
        if (await TemplateCompanyErrorAsync(a.TenantId, companyId, ct) is { } err) return err;
        return Ok(await _templates.ListAsync(a.TenantId, companyId, a.UserId, ct));
    }

    [HttpGet("companies/{companyId:guid}/templates/{templateId:guid}")]
    public async Task<IActionResult> GetTemplate(Guid companyId, Guid templateId, CancellationToken ct)
    {
        if (Actor() is not { } a) return Forbid();
        if (await TemplateCompanyErrorAsync(a.TenantId, companyId, ct) is { } err) return err;
        return await _templates.GetAsync(a.TenantId, companyId, templateId, a.UserId, ct) is { } t ? Ok(t) : NotFound();
    }

    [HttpPost("companies/{companyId:guid}/templates")]
    [RequestSizeLimit(65536)]
    public async Task<IActionResult> CreateTemplate(Guid companyId, [FromBody] CustomBankTemplateProposal body, CancellationToken ct)
    {
        if (Actor() is not { } a || !HasPermission("payroll.structure_manage")) return Forbid();
        if (await TemplateCompanyErrorAsync(a.TenantId, companyId, ct) is { } err) return err;
        if (body is null) return BadRequest(new { error = "template_required" });
        return MapTemplate(await _templates.ProposeAsync(a.TenantId, companyId, a.UserId, null, body, ct));
    }

    [HttpPut("companies/{companyId:guid}/templates/{templateId:guid}")]
    [RequestSizeLimit(65536)]
    public async Task<IActionResult> UpdateTemplate(Guid companyId, Guid templateId, [FromBody] CustomBankTemplateProposal body, CancellationToken ct)
    {
        if (Actor() is not { } a || !HasPermission("payroll.structure_manage")) return Forbid();
        if (await TemplateCompanyErrorAsync(a.TenantId, companyId, ct) is { } err) return err;
        if (body is null) return BadRequest(new { error = "template_required" });
        return MapTemplate(await _templates.ProposeAsync(a.TenantId, companyId, a.UserId, templateId, body, ct));
    }

    [HttpPost("companies/{companyId:guid}/templates/{templateId:guid}/approve")]
    [RequestSizeLimit(65536)]
    public Task<IActionResult> ApproveTemplate(Guid companyId, Guid templateId, [FromBody] CustomBankTemplateDecision body, CancellationToken ct) =>
        DecideTemplateAsync(companyId, templateId, approve: true, body, ct);

    [HttpPost("companies/{companyId:guid}/templates/{templateId:guid}/reject")]
    [RequestSizeLimit(65536)]
    public Task<IActionResult> RejectTemplate(Guid companyId, Guid templateId, [FromBody] CustomBankTemplateDecision body, CancellationToken ct) =>
        DecideTemplateAsync(companyId, templateId, approve: false, body, ct);

    private async Task<IActionResult> DecideTemplateAsync(Guid companyId, Guid templateId, bool approve, CustomBankTemplateDecision? body, CancellationToken ct)
    {
        // The checker is Finance: payroll.approve (not payroll.export alone). They need not hold the structure permission.
        if (!HasPermission("payroll.approve")) return Forbid();
        if (this.GetTenantId() is not Guid tenantId || tenantId == Guid.Empty || this.GetUserId() is not Guid userId || userId == Guid.Empty) return Forbid();
        if (await TemplateCompanyErrorAsync(tenantId, companyId, ct) is { } err) return err;
        if (body is null) return BadRequest(new { error = "decision_required" });
        return MapTemplate(await _templates.DecideAsync(tenantId, companyId, userId, templateId, approve, body, ct));
    }

    /// <summary>Layout preview from a batch: the first rows in the real layout with IBANs and IDs masked, plus the
    /// full validation. Writes nothing and never yields a file.</summary>
    [HttpPost("batches/{batchId:guid}/preview")]
    [RequestSizeLimit(65536)]
    public async Task<IActionResult> Preview(Guid batchId, [FromBody] CustomBankPreviewRequest body, CancellationToken ct)
    {
        if (Actor() is not { } a) return Forbid();
        if (await BatchScopeAndActivationErrorAsync(a.TenantId, batchId, ct) is { } err) return err;
        if (body is null) return BadRequest(new { error = "preview_request_required" });
        return Map(await _service.PreviewAsync(a.TenantId, batchId, body, ct), v => Ok(v));
    }

    /// <summary>404 (never 403) for an inaccessible, missing or other-tenant company, and the feature switch applies.</summary>
    private async Task<IActionResult?> TemplateCompanyErrorAsync(Guid tenantId, Guid companyId, CancellationToken ct)
    {
        if (!this.GetEntityScope().CanAccessCompany(companyId)) return NotFound();
        var exists = await _db.Companies.AsNoTracking().AnyAsync(c => c.TenantId == tenantId && c.Id == companyId && !c.IsDeleted, ct);
        if (!exists) return NotFound();
        return SaudiBankExportActivation.IsEnabled(_activationConfiguration, tenantId, companyId) ? null : Disabled();
    }

    private IActionResult MapTemplate(CustomBankResult<CustomBankTemplateDto> r) => r.Outcome switch
    {
        CustomBankOutcome.Ok when r.Value is not null => Ok(new { template = r.Value, warnings = r.Warnings }),
        CustomBankOutcome.NotFound => NotFound(),
        CustomBankOutcome.Forbidden => StatusCode(StatusCodes.Status403Forbidden, new { error = r.Error, message = r.Message }),
        CustomBankOutcome.Conflict => Conflict(new { error = r.Error, message = r.Message }),
        CustomBankOutcome.Invalid => UnprocessableEntity(new { error = r.Error, message = r.Message, errors = r.Errors, warnings = r.Warnings }),
        _ => StatusCode(StatusCodes.Status500InternalServerError, new { error = "unexpected_state" }),
    };

    // ── helpers ───────────────────────────────────────────────────────────────────────────────

    private sealed record ActorInfo(Guid TenantId, Guid UserId);

    /// <summary>Null tenant, null actor or missing payroll.export ⇒ null ⇒ the caller refuses.</summary>
    private ActorInfo? Actor()
    {
        if (!HasPermission("payroll.export")) return null;
        return this.GetTenantId() is Guid t && t != Guid.Empty && this.GetUserId() is Guid u && u != Guid.Empty
            ? new ActorInfo(t, u) : null;
    }

    private bool HasPermission(string permission) =>
        User.Claims.Any(c => c.Type == "permission" && string.Equals(c.Value, permission, StringComparison.OrdinalIgnoreCase));

    private async Task<IActionResult?> CompanyScopeErrorAsync(Guid tenantId, Guid companyId, CancellationToken ct)
    {
        if (!this.GetEntityScope().CanAccessCompany(companyId)) return Forbid();
        var exists = await _db.Companies.AsNoTracking().AnyAsync(c => c.TenantId == tenantId && c.Id == companyId && !c.IsDeleted, ct);
        return exists ? null : NotFound();
    }

    private ObjectResult Disabled() => StatusCode(StatusCodes.Status403Forbidden, new
    {
        error = "bank_export_disabled",
        message = "Bank instruction export is not activated for this legal entity."
    });

    private Task<Guid?> BatchCompanyAsync(Guid tenantId, Guid batchId, CancellationToken ct) =>
        (from batch in _db.PayrollPaymentBatches.AsNoTracking()
         join run in _db.PayrollRuns.AsNoTracking() on batch.PayrollRunId equals run.Id
         where batch.TenantId == tenantId && run.TenantId == tenantId && batch.Id == batchId
         select run.CompanyId).FirstOrDefaultAsync(ct);

    private async Task<IActionResult?> BatchScopeAndActivationErrorAsync(Guid tenantId, Guid batchId, CancellationToken ct)
    {
        if (await this.PaymentBatchScopeErrorAsync(_db, tenantId, batchId, ct) is { } err) return err;
        var companyId = await BatchCompanyAsync(tenantId, batchId, ct);
        return companyId is Guid c && SaudiBankExportActivation.IsEnabled(_activationConfiguration, tenantId, c)
            ? null : Disabled();
    }

    private IActionResult Map<T>(SaudiBankExportResult<T> r, Func<T, IActionResult> ok) => r.Outcome switch
    {
        SaudiBankExportOutcome.Ok when r.Value is not null => ok(r.Value),
        SaudiBankExportOutcome.NotFound => r.Error is null ? NotFound() : NotFound(new { error = r.Error, message = r.Message }),
        SaudiBankExportOutcome.Conflict => Conflict(new { error = r.Error, message = r.Message, validation = r.Validation, errors = r.Validation?.Errors, warnings = r.Validation?.Warnings }),
        SaudiBankExportOutcome.Invalid => UnprocessableEntity(new { error = r.Error, message = r.Message, validation = r.Validation, errors = r.Validation?.Errors, warnings = r.Validation?.Warnings }),
        _ => StatusCode(StatusCodes.Status500InternalServerError, new { error = "unexpected_state" }),
    };
}


public sealed class SaudiBankExportIntegrityAttribute : ExceptionFilterAttribute
{
    public override void OnException(ExceptionContext context)
    {
        if (context.Exception is not SaudiBankExportIntegrityException) return;
        context.Result = new ConflictObjectResult(new { error = "artifact_integrity_failed", message = "The stored bank export is unreadable or has invalid integrity metadata. No file was returned." });
        context.ExceptionHandled = true;
    }
}
