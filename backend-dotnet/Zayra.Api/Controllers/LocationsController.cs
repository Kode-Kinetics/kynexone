using System.Text;
using Zayra.Api.Application.Auth;
using Zayra.Api.Application.Organization;
using Zayra.Api.Infrastructure.Organization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Common;
using Zayra.Api.Application.Common.Import;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Authorization;
using Zayra.Api.Models;

namespace Zayra.Api.Controllers;

[ApiController]
[Route("api/locations")]
[Authorize(Roles = "Admin,HR Manager")]
public class LocationsController : ControllerBase
{
    private readonly ZayraDbContext _db;

    private static readonly string[] CsvHeaders =
        { "Code", "NameEn", "NameAr", "CountryCode", "City", "IsActive" };

    /// <summary>Neutral example row, kept adjacent to the header it is positionally paired with.</summary>
    private static readonly string[] CsvExampleRow =
        { "LOC-001", "Main Office", "", "SA", "Main City", "true" };

    private readonly IAuditService _audit;

    public LocationsController(ZayraDbContext db, IAuditService audit) { _db = db; _audit = audit; }

    private RequestContext Context() => new(HttpContext.Connection.RemoteIpAddress?.ToString(), Request.Headers.UserAgent.ToString(), this.GetUserId(), this.GetTenantId());

    private LocationService Locations => new(_db, _audit);

    private Func<Guid?, bool> MayWrite => company => OrgWriteRules.MayWriteCompany(this, company);

    private IActionResult Refuse(LocationRuleException ex) => ex.Status == 403 ? Forbid() : StatusCode(ex.Status, ex.ToPayload());

    private static LocationInput ToInput(LocationRequest r) =>
        new(null, r.Code, r.NameEn, r.NameAr, null, r.City, r.CountryCode, null, null, null, null, r.IsActive, r.Reason);

    [HttpGet]
    public async Task<IActionResult> Search([FromQuery] int page = 1, [FromQuery] int pageSize = 25, CancellationToken ct = default)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 100);
        var tenantId = this.GetTenantId();
        if (tenantId is null) return Unauthorized();

        var query = _db.Locations.AsNoTracking().Where(l => l.TenantId == tenantId.Value && !l.IsDeleted);
        var total = await query.CountAsync(ct);
        var items = (await query.OrderBy(l => l.Code).Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct)).Select(LocationDto.From).ToList();
        return Ok(new { items, total, page, pageSize });
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct)
    {
        var tenantId = this.GetTenantId();
        if (tenantId is null) return Unauthorized();
        var loc = await _db.Locations.AsNoTracking().FirstOrDefaultAsync(l => l.Id == id && l.TenantId == tenantId.Value && !l.IsDeleted, ct);
        return loc is null ? NotFound() : Ok(LocationDto.From(loc));
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] LocationRequest req, CancellationToken ct)
    {
        var tenantId = this.GetTenantId();
        if (tenantId is null) return Unauthorized();
        // A location made here belongs to no company, so it is the whole group's: group scope only (same rule as remove / restore).
        if (!OrgWriteRules.MayWriteCompany(this, null)) return Forbid();
        try
        {
            var loc = await Locations.CreateAsync(tenantId.Value, ToInput(req), Context(), MayWrite, ct);
            return CreatedAtAction(nameof(Get), new { id = loc.Id }, loc);
        }
        catch (LocationRuleException ex) { return Refuse(ex); }
    }

    [HttpPut("{id:guid}")]
    public async Task<IActionResult> Update(Guid id, [FromBody] LocationRequest req, CancellationToken ct)
    {
        var tenantId = this.GetTenantId();
        if (tenantId is null) return Unauthorized();
        try
        {
            var loc = await Locations.UpdateAsync(tenantId.Value, id, ToInput(req), replaceOptional: false, Context(), MayWrite, ct);
            return loc is null ? NotFound() : Ok(loc);
        }
        catch (LocationRuleException ex) { return Refuse(ex); }
    }

    [HttpDelete("{id:guid}")]
    [HasPermission("organization.delete")]
    public Task<IActionResult> Delete(Guid id, CancellationToken ct)
        => OrgWriteRules.RemoveAsync(this, new OrgRowRemoval(_db, _audit), OrgRowKind.Location, id, Context(), ct);

    [HttpPost("{id:guid}/restore")]
    [HasPermission("organization.delete")]
    public Task<IActionResult> Restore(Guid id, CancellationToken ct)
        => OrgWriteRules.RestoreAsync(this, new OrgRowRemoval(_db, _audit), OrgRowKind.Location, id, Context(), ct);

    [HttpGet("export")]
    public async Task<IActionResult> Export(CancellationToken ct)
    {
        var tenantId = this.GetTenantId();
        if (tenantId is null) return Unauthorized();
        var items = await _db.Locations.AsNoTracking().Where(l => l.TenantId == tenantId.Value && !l.IsDeleted).OrderBy(l => l.Code).ToListAsync(ct);
        var rows = items.Select(l => (IReadOnlyList<object?>)new object?[] { l.Code, l.NameEn, l.NameAr, l.CountryCode, l.City, l.IsActive ? "true" : "false" });
        return CsvDownload.Result(Csv.Build(CsvHeaders, rows), $"locations_{DateTime.UtcNow:yyyyMMdd}.csv");
    }

    [HttpGet("import-template")]
    public IActionResult ImportTemplate() =>
        CsvDownload.Result(Csv.Template(CsvHeaders, CsvExampleRow), "locations_import_template.csv");

    [HttpPost("import-preview")]
    public Task<IActionResult> ImportPreview([FromBody] LocationImportRequest req, CancellationToken ct) => RunImportAsync(req.Csv, commit: false, ct);

    [HttpPost("import")]
    public Task<IActionResult> Import([FromBody] LocationImportRequest req, CancellationToken ct) => RunImportAsync(req.Csv, commit: true, ct);

    /// <summary>Preview and commit walk the SAME loop in the service, so a spreadsheet cannot be judged one way in the dry run and another for real.</summary>
    private async Task<IActionResult> RunImportAsync(string csv, bool commit, CancellationToken ct)
    {
        var tenantId = this.GetTenantId();
        if (tenantId is null) return Unauthorized();
        try
        {
            var outcome = await Locations.ImportAsync(tenantId.Value, Csv.Parse(csv), commit, Context(), MayWrite, ct);
            return Ok(commit
                ? new ImportCommitResult(outcome.Received, outcome.Created, outcome.Updated, outcome.Skipped, outcome.Rows, Array.Empty<string>())
                : (object)new ImportPreviewResult(outcome.Received, outcome.Created, outcome.Updated, outcome.Skipped, outcome.Rows));
        }
        catch (LocationRuleException ex) { return Refuse(ex); }
    }
}

public record LocationRequest(string? Code, string? NameEn, string? NameAr, string? CountryCode, string? City, bool IsActive = true, string? Reason = null);
public record LocationImportRequest(string Csv);
