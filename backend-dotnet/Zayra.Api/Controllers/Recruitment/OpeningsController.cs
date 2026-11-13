using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Common;
using Zayra.Api.Application.Recruitment;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Notifications;
using Zayra.Api.Infrastructure.Recruitment;
using Zayra.Api.Models;

namespace Zayra.Api.Controllers.Recruitment;

[ApiController]
[Route("api/recruitment/openings")]
[Authorize]
public class OpeningsController : ControllerBase
{
    private readonly ZayraDbContext _db;
    private readonly IRecruitmentService _svc;
    private readonly INotificationService _notify;

    public OpeningsController(ZayraDbContext db, IRecruitmentService svc, INotificationService notify)
    {
        _db = db; _svc = svc; _notify = notify;
    }

    [HttpGet]
    public async Task<IActionResult> List(
        [FromQuery] string? status,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 25,
        CancellationToken ct = default)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 100);
        var tenantId = this.GetTenantId()!.Value;
        var query = _db.JobOpenings.Where(j => j.TenantId == tenantId);
        if (!string.IsNullOrWhiteSpace(status)) query = query.Where(j => j.Status == status);

        var total = await query.CountAsync(ct);
        var items = await query.OrderByDescending(j => j.CreatedAtUtc).Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);

        // Enrich with application counts per opening
        var ids = items.Select(j => j.Id).ToList();
        var counts = await _db.JobApplications
            .Where(a => a.TenantId == tenantId && ids.Contains(a.JobOpeningId) && a.Status == "Active")
            .GroupBy(a => a.JobOpeningId)
            .Select(g => new { JobOpeningId = g.Key, Count = g.Count() })
            .ToListAsync(ct);
        var countMap = counts.ToDictionary(c => c.JobOpeningId, c => c.Count);

        var enriched = items.Select(j => new
        {
            j.Id, j.JobCode, j.RequisitionId, j.Title, j.DepartmentName, j.DesignationTitle,
            j.EmploymentType, j.HeadCount, j.FilledCount, j.SalaryFrom, j.SalaryTo, j.Location,
            j.Status, j.AssignedHrName, j.CreatedAtUtc, j.PublishedAtUtc,
            ActiveApplications = countMap.GetValueOrDefault(j.Id, 0),
            Remaining = j.HeadCount - j.FilledCount,
        });

        return Ok(new { items = enriched, total, page, pageSize });
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct)
    {
        var tenantId = this.GetTenantId()!.Value;
        var j = await _db.JobOpenings.FirstOrDefaultAsync(x => x.Id == id && x.TenantId == tenantId, ct);
        if (j is null) return NotFound();

        var stageCounts = await _db.JobApplications
            .Where(a => a.TenantId == tenantId && a.JobOpeningId == id && a.Status == "Active")
            .GroupBy(a => a.Stage)
            .Select(g => new { Stage = g.Key, Count = g.Count() })
            .ToListAsync(ct);

        return Ok(new { opening = j, stageCounts });
    }

    [HttpPost]
    [Authorize(Roles = "Admin,HR Manager,HR Officer")]
    public async Task<IActionResult> Create([FromBody] CreateOpeningRequest req, CancellationToken ct)
    {
        var tenantId = this.GetTenantId()!.Value;
        var userId = this.GetUserId();
        var code = await _svc.GenerateJobCodeAsync(tenantId, ct);

        // Mark source requisition as Converted. The opening belongs to the company that raised the requisition; a requisition
        // outside the caller's company scope is not found, so an opening can never be linked to another company's requisition.
        Guid? companyId = req.CompanyId;
        if (req.RequisitionId.HasValue)
        {
            var requisition = await _db.ManpowerRequisitions
                .FirstOrDefaultAsync(r => r.Id == req.RequisitionId && r.TenantId == tenantId, ct);
            if (requisition is null) return BadRequest(new { message = "Requisition not found." });
            if (req.CompanyId is Guid named && requisition.CompanyId is Guid owner && named != owner)
                return BadRequest(new { message = "The requisition belongs to a different company than the one named." });
            companyId = requisition.CompanyId ?? req.CompanyId;
            if (requisition.Status == "Approved")
            {
                requisition.Status = "Converted";
                await _db.SaveChangesAsync(ct);
            }
        }

        // An opening starts from its requisition: placement, budget range and skills carry over unless the request states its own.
        var requisitionForTerms = req.RequisitionId.HasValue
            ? await _db.ManpowerRequisitions.AsNoTracking().FirstOrDefaultAsync(r => r.Id == req.RequisitionId && r.TenantId == tenantId, ct)
            : null;
        var terms = OpeningTerms.Inherit(req, requisitionForTerms);
        var (catalogueDepartment, catalogueDesignation, placementError) = await CataloguePlacementAsync(tenantId, terms.DepartmentId, terms.DesignationId, terms.GradeId, ct);
        if (placementError is not null) return placementError;

        var j = new JobOpening
        {
            TenantId = tenantId,
            CompanyId = companyId,
            JobCode = code,
            RequisitionId = req.RequisitionId,
            Title = req.Title,
            DepartmentId = terms.DepartmentId,
            DepartmentName = catalogueDepartment?.NameEn ?? terms.DepartmentName,
            DesignationId = terms.DesignationId,
            DesignationTitle = catalogueDesignation?.TitleEn ?? terms.DesignationTitle,
            GradeId = terms.GradeId,
            EmploymentType = req.EmploymentType,
            HeadCount = req.HeadCount,
            Description = req.Description,
            Requirements = terms.Requirements,
            Responsibilities = req.Responsibilities,
            SalaryFrom = terms.SalaryFrom,
            SalaryTo = terms.SalaryTo,
            Location = req.Location,
            Status = "Open",
            AssignedHrUserId = userId,
            AssignedHrName = req.AssignedHrName,
            PublishedAtUtc = DateTime.UtcNow,
        };
        _db.JobOpenings.Add(j);
        await _db.SaveChangesAsync(ct);
        return Created($"/api/recruitment/openings/{j.Id}", j);
    }

    [HttpPut("{id:guid}")]
    [Authorize(Roles = "Admin,HR Manager,HR Officer")]
    public async Task<IActionResult> Update(Guid id, [FromBody] CreateOpeningRequest req, CancellationToken ct)
    {
        var tenantId = this.GetTenantId()!.Value;
        var j = await _db.JobOpenings.FirstOrDefaultAsync(x => x.Id == id && x.TenantId == tenantId, ct);
        if (j is null) return NotFound();

        // The same placement rules as Create: an id the request names must be an active record of this organisation.
        var (catalogueDepartment, catalogueDesignation, placementError) = await CataloguePlacementAsync(tenantId, req.DepartmentId, req.DesignationId, req.GradeId, ct);
        if (placementError is not null) return placementError;

        j.Title = req.Title;
        // The opening's own placement (a recruitment record, not an employee's attribution): an id the request names replaces the stored one.
        _db.Entry(j).CurrentValues.SetValues(new { DepartmentId = req.DepartmentId ?? j.DepartmentId, DesignationId = req.DesignationId ?? j.DesignationId, GradeId = req.GradeId ?? j.GradeId });
        // A name is taken from the record, never typed: a newly named record names it, a kept record keeps its name, and free text only
        // stands where there is no record at all.
        j.DepartmentName = catalogueDepartment?.NameEn ?? (j.DepartmentId is null ? req.DepartmentName : j.DepartmentName);
        j.DesignationTitle = catalogueDesignation?.TitleEn ?? (j.DesignationId is null ? req.DesignationTitle : j.DesignationTitle);
        j.EmploymentType = req.EmploymentType;
        j.HeadCount = req.HeadCount;
        j.Description = req.Description;
        j.Requirements = req.Requirements;
        j.Responsibilities = req.Responsibilities;
        j.SalaryFrom = req.SalaryFrom;
        j.SalaryTo = req.SalaryTo;
        j.Location = req.Location;
        j.AssignedHrName = req.AssignedHrName;
        await _db.SaveChangesAsync(ct);
        return Ok(j);
    }

    private async Task<(Department? Department, Designation? Designation, IActionResult? Error)> CataloguePlacementAsync(
        Guid tenantId, Guid? departmentId, Guid? designationId, Guid? gradeId, CancellationToken ct)
    {
        var p = await RecruitmentPlacement.ResolveAsync(_db, tenantId, departmentId, designationId, gradeId, ct);
        return (p.Department, p.Designation, p.Error is null ? null : BadRequest(new { message = p.Error }));
    }

    [HttpPost("{id:guid}/close")]
    [Authorize(Roles = "Admin,HR Manager")]
    public async Task<IActionResult> Close(Guid id, CancellationToken ct)
    {
        var tenantId = this.GetTenantId()!.Value;
        var j = await _db.JobOpenings.FirstOrDefaultAsync(x => x.Id == id && x.TenantId == tenantId, ct);
        if (j is null) return NotFound();
        j.Status = "Closed";
        j.ClosedAtUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        return Ok(j);
    }

    [HttpGet("stats")]
    public async Task<IActionResult> Stats(CancellationToken ct)
    {
        var tenantId = this.GetTenantId()!.Value;
        var openings = await _db.JobOpenings.Where(j => j.TenantId == tenantId).ToListAsync(ct);
        var apps = await _db.JobApplications.Where(a => a.TenantId == tenantId).ToListAsync(ct);
        return Ok(new
        {
            openPositions = openings.Where(j => j.Status is "Open" or "InProgress").Sum(j => j.HeadCount - j.FilledCount),
            totalOpenings = openings.Count(j => j.Status is "Open" or "InProgress"),
            activeApplications = apps.Count(a => a.Status == "Active"),
            hiredThisMonth = apps.Count(a => a.Status == "Hired" && a.HiredAtUtc >= DateTime.UtcNow.AddDays(-30)),
            offersPending = apps.Count(a => a.Stage == "Offer" && a.Status == "Active"),
        });
    }

    // ── Export / Import / Template ───────────────────────────────────────────
    private static readonly string[] JobOpeningCsvHeaders =
        { "Title", "DepartmentName", "DesignationTitle", "EmploymentType", "HeadCount", "Location", "SalaryFrom", "SalaryTo", "Description", "Requirements", "Responsibilities", "AssignedHrName", "Status" };

    /// <summary>Neutral example row, kept adjacent to the header it is positionally paired with.</summary>
    private static readonly string[] JobOpeningCsvExampleRow =
        { "Example Job Title", "Operations", "Operations Officer", "Full-Time", "1", "Main City", "8000", "12000", "Example description", "Example requirements", "Example responsibilities", "", "Open" };

    [HttpGet("export")]
    public async Task<IActionResult> Export(CancellationToken ct)
    {
        var tenantId = this.GetTenantId()!.Value;
        var openings = await _db.JobOpenings
            .Where(j => j.TenantId == tenantId)
            .OrderByDescending(j => j.CreatedAtUtc)
            .ToListAsync(ct);
        var rows = openings.Select(j => (IReadOnlyList<object?>)new object?[]
        {
            j.Title, j.DepartmentName, j.DesignationTitle, j.EmploymentType, j.HeadCount,
            j.Location, j.SalaryFrom, j.SalaryTo, j.Description, j.Requirements, j.Responsibilities, j.AssignedHrName, j.Status
        });
        var csv = Csv.Build(JobOpeningCsvHeaders, rows);
        Response.Headers["Content-Disposition"] = "attachment; filename=job_openings_export.csv";
        return CsvDownload.Result(csv);
    }

    [HttpGet("import-template")]
    public IActionResult ImportTemplate()
    {
        Response.Headers["Content-Disposition"] = "attachment; filename=job_openings_import_template.csv";
        return CsvDownload.Result(Csv.Template(JobOpeningCsvHeaders, JobOpeningCsvExampleRow));
    }

    [HttpPost("import")]
    [Authorize(Roles = "Admin,HR Manager,HR Officer")]
    public async Task<IActionResult> Import([FromBody] ImportJobOpeningsRequest req, CancellationToken ct)
    {
        var tenantId = this.GetTenantId()!.Value;
        var rows = Csv.Parse(req.CsvContent ?? string.Empty);
        int created = 0, skipped = 0;
        var errors = new List<string>();
        var rowNum = 1;
        foreach (var row in rows)
        {
            rowNum++;
            var title = row.GetValueOrDefault("Title", string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(title)) { skipped++; continue; }
            // Dedup by title + department
            var dept = row.GetValueOrDefault("DepartmentName", string.Empty).Trim();
            if (await _db.JobOpenings.AnyAsync(j => j.TenantId == tenantId && j.Title == title && j.DepartmentName == dept && j.Status == "Open", ct))
            { skipped++; errors.Add($"Row {rowNum}: An open job '{title}' in '{dept}' already exists."); continue; }
            int.TryParse(row.GetValueOrDefault("HeadCount", "1"), out var headCount);
            if (headCount <= 0) headCount = 1;
            decimal.TryParse(row.GetValueOrDefault("SalaryFrom", string.Empty), out var salaryFrom);
            decimal.TryParse(row.GetValueOrDefault("SalaryTo", string.Empty), out var salaryTo);
            var code = await _svc.GenerateJobCodeAsync(tenantId, ct);
            _db.JobOpenings.Add(new JobOpening
            {
                TenantId = tenantId,
                CompanyId = req.CompanyId,
                JobCode = code,
                Title = title,
                DepartmentName = dept,
                DesignationTitle = row.GetValueOrDefault("DesignationTitle", string.Empty),
                EmploymentType = row.GetValueOrDefault("EmploymentType", "Full-Time"),
                HeadCount = headCount,
                Location = row.GetValueOrDefault("Location", string.Empty),
                SalaryFrom = salaryFrom > 0 ? salaryFrom : null,
                SalaryTo = salaryTo > 0 ? salaryTo : null,
                Description = row.GetValueOrDefault("Description", string.Empty),
                Requirements = row.GetValueOrDefault("Requirements", string.Empty),
                Responsibilities = row.GetValueOrDefault("Responsibilities", string.Empty),
                AssignedHrName = row.GetValueOrDefault("AssignedHrName", string.Empty),
                Status = row.GetValueOrDefault("Status", "Open"),
                PublishedAtUtc = DateTime.UtcNow
            });
            created++;
        }
        await _db.SaveChangesAsync(ct);
        return Ok(new { received = rows.Count, created, skipped, errors = errors.Take(20) });
    }
}

public record ImportJobOpeningsRequest(string CsvContent, Guid? CompanyId = null);

public record CreateOpeningRequest(
    Guid? RequisitionId, string Title, string DepartmentName, string DesignationTitle,
    string EmploymentType, int HeadCount, string Description, string Requirements,
    string Responsibilities, decimal? SalaryFrom, decimal? SalaryTo,
    string Location, string AssignedHrName, Guid? CompanyId = null,
    Guid? DepartmentId = null, Guid? DesignationId = null, Guid? GradeId = null);
