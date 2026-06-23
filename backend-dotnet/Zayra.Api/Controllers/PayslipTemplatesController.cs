using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Documents;
using Zayra.Api.Infrastructure.Documents.Letters;
using Zayra.Api.Infrastructure.Payroll;
using Zayra.Api.Models;

namespace Zayra.Api.Controllers;

[ApiController]
[Route("api/payslip-templates")]
[Authorize]
public class PayslipTemplatesController : ControllerBase
{
    private readonly ZayraDbContext _db;
    private readonly IDocumentStorage _storage;
    private readonly ILetterService _letters;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public PayslipTemplatesController(ZayraDbContext db, IDocumentStorage storage, ILetterService letters)
    {
        _db = db;
        _storage = storage;
        _letters = letters;
    }

    // ── Registry (metadata) ───────────────────────────────────────────────────

    [HttpGet("registry")]
    [AllowAnonymous] // metadata-only, no sensitive data
    public IActionResult GetRegistry() =>
        Ok(PayslipTemplateRegistry.Sections.Values.Select(s => new
        {
            s.Key,
            s.LabelEn,
            s.LabelAr,
            s.CanDisable,
            Fields = s.Fields.Select(f => new { f.Key, f.LabelEn, f.LabelAr, f.IsComplianceLocked }),
        }));

    // ── List ──────────────────────────────────────────────────────────────────

    [HttpGet]
    [Authorize(Roles = "Admin,HR Manager,Payroll Manager,Payroll Officer")]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        var tenantId = GetTenantId();
        var templates = await _db.PayslipTemplates.AsNoTracking()
            .Where(t => t.TenantId == tenantId)
            .OrderByDescending(t => t.IsDefault)
            .ThenBy(t => t.Name)
            .ThenByDescending(t => t.Version)
            .Select(t => new TemplateListItem(
                t.Id, t.Name, t.IsDefault, t.Version, t.Status, t.CreatedAtUtc, t.UpdatedAtUtc, t.ParentTemplateId))
            .ToListAsync(ct);
        return Ok(templates);
    }

    // ── Get one ───────────────────────────────────────────────────────────────

    [HttpGet("{id:guid}")]
    [Authorize(Roles = "Admin,HR Manager,Payroll Manager,Payroll Officer")]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct)
    {
        var tenantId = GetTenantId();
        var t = await _db.PayslipTemplates.AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == id && x.TenantId == tenantId, ct);
        if (t is null) return NotFound();
        return Ok(MapToDto(t));
    }

    // ── Version history ───────────────────────────────────────────────────────

    [HttpGet("{id:guid}/versions")]
    [Authorize(Roles = "Admin,HR Manager,Payroll Manager,Payroll Officer")]
    public async Task<IActionResult> Versions(Guid id, CancellationToken ct)
    {
        var tenantId = GetTenantId();
        var root = await _db.PayslipTemplates.AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == id && x.TenantId == tenantId, ct);
        if (root is null) return NotFound();

        // Walk the name-based version chain (same name, same tenant)
        var chain = await _db.PayslipTemplates.AsNoTracking()
            .Where(t => t.TenantId == tenantId && t.Name == root.Name)
            .OrderByDescending(t => t.Version)
            .Select(t => new TemplateListItem(
                t.Id, t.Name, t.IsDefault, t.Version, t.Status, t.CreatedAtUtc, t.UpdatedAtUtc, t.ParentTemplateId))
            .ToListAsync(ct);
        return Ok(chain);
    }

    // ── Create ────────────────────────────────────────────────────────────────

    [HttpPost]
    [Authorize(Roles = "Admin,HR Manager,Payroll Manager")]
    public async Task<IActionResult> Create([FromBody] TemplateUpsertRequest req, CancellationToken ct)
    {
        var tenantId = GetTenantId();
        var (branding, layout, errors) = ValidateRequest(req);
        if (errors.Count > 0) return BadRequest(new { errors });

        var template = new PayslipTemplate
        {
            TenantId = tenantId,
            Name = req.Name.Trim(),
            IsDefault = req.IsDefault,
            Version = 1,
            Status = "draft",
            BrandingJson = JsonSerializer.Serialize(branding, Json),
            LayoutJson = JsonSerializer.Serialize(layout, Json),
            CreatedByUserId = GetUserId(),
        };

        if (req.IsDefault)
            await ClearDefaultAsync(tenantId, null, ct);

        _db.PayslipTemplates.Add(template);
        await AuditAsync("payslip_template.created", template.Id.ToString(), tenantId, ct);
        await _db.SaveChangesAsync(ct);
        return CreatedAtAction(nameof(Get), new { id = template.Id }, MapToDto(template));
    }

    // ── Update (creates new version, archives old) ────────────────────────────

    [HttpPut("{id:guid}")]
    [Authorize(Roles = "Admin,HR Manager,Payroll Manager")]
    public async Task<IActionResult> Update(Guid id, [FromBody] TemplateUpsertRequest req, CancellationToken ct)
    {
        var tenantId = GetTenantId();
        var existing = await _db.PayslipTemplates
            .FirstOrDefaultAsync(x => x.Id == id && x.TenantId == tenantId, ct);
        if (existing is null) return NotFound();
        if (existing.Status == "archived")
            return Conflict(new { message = "Archived template versions are immutable. Edit the active version." });

        var (branding, layout, errors) = ValidateRequest(req);
        if (errors.Count > 0) return BadRequest(new { errors });

        // Archive the current version
        existing.Status = "archived";
        existing.IsDefault = false;
        existing.UpdatedAtUtc = DateTime.UtcNow;

        var newVersion = new PayslipTemplate
        {
            TenantId = tenantId,
            Name = req.Name.Trim(),
            IsDefault = req.IsDefault,
            Version = existing.Version + 1,
            Status = "active",
            BrandingJson = JsonSerializer.Serialize(branding, Json),
            LayoutJson = JsonSerializer.Serialize(layout, Json),
            ParentTemplateId = existing.Id,
            CreatedByUserId = GetUserId(),
        };

        if (req.IsDefault)
            await ClearDefaultAsync(tenantId, null, ct);

        _db.PayslipTemplates.Add(newVersion);
        await AuditAsync("payslip_template.updated", newVersion.Id.ToString(), tenantId, ct);
        await _db.SaveChangesAsync(ct);
        return Ok(MapToDto(newVersion));
    }

    // ── Set default ───────────────────────────────────────────────────────────

    [HttpPost("{id:guid}/set-default")]
    [Authorize(Roles = "Admin,HR Manager,Payroll Manager")]
    public async Task<IActionResult> SetDefault(Guid id, CancellationToken ct)
    {
        var tenantId = GetTenantId();
        var template = await _db.PayslipTemplates
            .FirstOrDefaultAsync(x => x.Id == id && x.TenantId == tenantId, ct);
        if (template is null) return NotFound();
        if (template.Status == "archived")
            return Conflict(new { message = "Cannot set an archived version as default." });

        await ClearDefaultAsync(tenantId, id, ct);
        template.IsDefault = true;
        template.UpdatedAtUtc = DateTime.UtcNow;
        await AuditAsync("payslip_template.set_default", id.ToString(), tenantId, ct);
        await _db.SaveChangesAsync(ct);
        return Ok(new { id });
    }

    // ── Soft delete ───────────────────────────────────────────────────────────

    [HttpDelete("{id:guid}")]
    [Authorize(Roles = "Admin,HR Manager,Payroll Manager")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        var tenantId = GetTenantId();
        var template = await _db.PayslipTemplates
            .FirstOrDefaultAsync(x => x.Id == id && x.TenantId == tenantId, ct);
        if (template is null) return NotFound();
        if (template.IsDefault)
            return Conflict(new { message = "Cannot delete the default template. Set another template as default first." });

        template.IsDeleted = true;
        template.UpdatedAtUtc = DateTime.UtcNow;
        await AuditAsync("payslip_template.deleted", id.ToString(), tenantId, ct);
        await _db.SaveChangesAsync(ct);
        return NoContent();
    }

    // ── Logo upload ───────────────────────────────────────────────────────────

    [HttpPost("logo")]
    [Authorize(Roles = "Admin,HR Manager,Payroll Manager")]
    public async Task<IActionResult> UploadLogo(IFormFile file, CancellationToken ct)
    {
        var tenantId = GetTenantId();
        if (file is null || file.Length == 0) return BadRequest(new { message = "No file provided." });
        if (file.Length > 2 * 1024 * 1024) return BadRequest(new { message = "Logo must be under 2 MB." });
        var ext = Path.GetExtension(file.FileName).ToLowerInvariant();
        if (ext is not (".png" or ".jpg" or ".jpeg" or ".svg"))
            return BadRequest(new { message = "Logo must be PNG, JPG, or SVG." });

        var stored = await _storage.SaveAsync(tenantId, file, ct);
        return Ok(new { storageUrl = stored.StorageUrl });
    }

    // ── Preview PDF ───────────────────────────────────────────────────────────

    [HttpPost("{id:guid}/preview")]
    [Authorize(Roles = "Admin,HR Manager,Payroll Manager,Payroll Officer")]
    public async Task<IActionResult> Preview(Guid id, CancellationToken ct)
    {
        var tenantId = GetTenantId();
        var template = await _db.PayslipTemplates.AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == id && x.TenantId == tenantId, ct);
        if (template is null) return NotFound();

        var branding = ParseBranding(template.BrandingJson);
        var layout = ParseLayout(template.LayoutJson);
        var items = BuildSampleItems(layout, branding.Locale);

        var logoPath = branding.LogoStorageUrl is not null
            ? _storage.ResolvePath(branding.LogoStorageUrl)
            : null;
        var effectiveBranding = logoPath is not null ? branding with { LogoStorageUrl = logoPath } : branding;

        var data = new PayslipData(
            PayslipNumber: "PS-PREVIEW-001",
            EmployeeCode:  "EMP001",
            EmployeeName:  branding.Locale == "ar" ? "محمد أحمد الرشيدي" : "Mohammed Al-Rashidi",
            Department:    branding.Locale == "ar" ? "الموارد البشرية" : "Human Resources",
            Designation:   branding.Locale == "ar" ? "مدير أول" : "Senior Manager",
            PayYear:       DateTime.UtcNow.Year,
            PayMonth:      DateTime.UtcNow.Month,
            Currency:      "SAR",
            Items:         items,
            CompanyName:   "Acme Corporation Ltd.",
            CompanyNameAr: "شركة أكمي المحدودة",
            GeneratedOn:   DateTime.UtcNow,
            Branding:      effectiveBranding
        );

        var pdf = await _letters.GeneratePayslipPdfAsync(data, ct);
        return File(pdf, "application/pdf", "preview.pdf");
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private Guid GetTenantId() => Guid.Parse(User.FindFirstValue("tenant_id")!);
    private Guid? GetUserId() => Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("sub"), out var id) ? id : null;

    private async Task ClearDefaultAsync(Guid tenantId, Guid? excludeId, CancellationToken ct)
    {
        var currentDefaults = await _db.PayslipTemplates
            .Where(t => t.TenantId == tenantId && t.IsDefault && (excludeId == null || t.Id != excludeId))
            .ToListAsync(ct);
        foreach (var d in currentDefaults)
        {
            d.IsDefault = false;
            d.UpdatedAtUtc = DateTime.UtcNow;
        }
    }

    private async Task AuditAsync(string action, string entityId, Guid tenantId, CancellationToken ct)
    {
        _db.PayrollAuditLogs.Add(new PayrollAuditLog
        {
            TenantId  = tenantId,
            Action    = action,
            EntityName = "PayslipTemplate",
            EntityId  = entityId,
            UserId    = GetUserId(),
            MetadataJson = "{}",
        });
        await Task.CompletedTask;
    }

    private static (PayslipBrandingConfig branding, PayslipLayoutConfig layout, List<string> errors)
        ValidateRequest(TemplateUpsertRequest req)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(req.Name))
            errors.Add("name is required.");

        PayslipBrandingConfig branding;
        PayslipLayoutConfig layout;
        try
        {
            branding = JsonSerializer.Deserialize<PayslipBrandingConfig>(req.BrandingJson, Json)
                       ?? new PayslipBrandingConfig();
        }
        catch { errors.Add("brandingJson is not valid JSON."); branding = new(); }

        try
        {
            layout = JsonSerializer.Deserialize<PayslipLayoutConfig>(req.LayoutJson, Json)
                     ?? new PayslipLayoutConfig();
        }
        catch { errors.Add("layoutJson is not valid JSON."); layout = new(); }

        if (errors.Count > 0) return (branding, layout, errors);

        errors.AddRange(PayslipTemplateRegistry.ValidateBranding(branding));
        errors.AddRange(PayslipTemplateRegistry.ValidateLayout(layout));
        return (branding, layout, errors);
    }

    private static PayslipBrandingConfig ParseBranding(string json)
    {
        try { return JsonSerializer.Deserialize<PayslipBrandingConfig>(json, Json) ?? new(); }
        catch { return new(); }
    }

    private static PayslipLayoutConfig ParseLayout(string json)
    {
        try { return JsonSerializer.Deserialize<PayslipLayoutConfig>(json, Json) ?? new(); }
        catch { return new(); }
    }

    private static IReadOnlyList<PayslipLineItem> BuildSampleItems(PayslipLayoutConfig layout, string locale)
    {
        var ar = locale is "ar" or "bilingual";
        var items = new List<PayslipLineItem>();
        foreach (var sec in layout.Sections.Where(s => s.Enabled).OrderBy(s => s.Order))
        {
            if (!PayslipTemplateRegistry.Sections.TryGetValue(sec.Key, out var def)) continue;
            foreach (var fieldKey in sec.Fields)
            {
                var fd = def.Fields.FirstOrDefault(f => f.Key == fieldKey);
                if (fd is null) continue;
                var label = ar ? fd.LabelAr : fd.LabelEn;
                var sampleAmount = SampleAmounts.GetValueOrDefault(fieldKey, 500m);
                var type = sec.Key is "deductions" or "employer_contributions" ? "Deduction"
                           : sec.Key is "ytd" or "leave_balance" or "loan_balance" or "bank_wps" or "signatory" ? "Info"
                           : "Earning";
                items.Add(new(label, sampleAmount, type));
            }
        }
        items.Add(new(ar ? "صافي الراتب" : "Net Pay", 12_500m, "Net"));
        return items;
    }

    private static readonly Dictionary<string, decimal> SampleAmounts = new()
    {
        ["basic_salary"]           = 10_000m,
        ["housing_allowance"]      = 3_000m,
        ["transport_allowance"]    = 800m,
        ["other_allowances"]       = 500m,
        ["gosi_annuities_ee"]      = 900m,
        ["gosi_saned_ee"]          = 75m,
        ["loan_repayment"]         = 500m,
        ["gosi_annuities_er"]      = 900m,
        ["gosi_oh_er"]             = 200m,
        ["ytd_gross"]              = 84_000m,
        ["ytd_net"]                = 72_000m,
        ["annual_leave_balance"]   = 21m,
    };

    private static TemplateDto MapToDto(PayslipTemplate t) => new(
        t.Id, t.Name, t.IsDefault, t.Version, t.Status, t.BrandingJson, t.LayoutJson,
        t.ParentTemplateId, t.CreatedByUserId, t.CreatedAtUtc, t.UpdatedAtUtc);
}

// ── Request / Response types ─────────────────────────────────────────────────
public record TemplateUpsertRequest(string Name, bool IsDefault, string BrandingJson, string LayoutJson);
public record TemplateDto(Guid Id, string Name, bool IsDefault, int Version, string Status,
    string BrandingJson, string LayoutJson, Guid? ParentTemplateId, Guid? CreatedByUserId,
    DateTime CreatedAtUtc, DateTime UpdatedAtUtc);
public record TemplateListItem(Guid Id, string Name, bool IsDefault, int Version, string Status,
    DateTime CreatedAtUtc, DateTime UpdatedAtUtc, Guid? ParentTemplateId);
