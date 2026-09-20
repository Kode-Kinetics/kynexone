using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Approvals;
using Zayra.Api.Application.Assets;
using Zayra.Api.Application.Auth;
using Zayra.Api.Application.Common;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Assets;
using Zayra.Api.Infrastructure.Authorization;
using Zayra.Api.Models;

namespace Zayra.Api.Controllers;

/// <summary>
/// W2-C — the asset register and custody operations (issue, return, transfer, write-off, retire, repair).
///
/// <para>Authorization reuses the employee-record permissions HR already holds: reading the register is
/// <c>employees.read</c>, every custody change is <c>employees.write</c>. The loss control on a write-off is
/// the approval workflow (F1 router, <c>AssetWriteOff</c>), not the permission. Company isolation comes from
/// the global query filter — every table here is <c>ICompanyScopedOperational</c>.</para>
/// </summary>
[ApiController]
[Route("api/assets")]
[Authorize]
public class AssetsController : ControllerBase
{
    private static readonly AssetLookupValueDto[] DefaultCategories =
    [
        new("LAPTOP", "Laptop"), new("DESKTOP", "Desktop"), new("MONITOR", "Monitor"), new("MOBILE_PHONE", "Mobile phone"),
        new("TABLET", "Tablet"), new("SIM_CARD", "SIM card"), new("ACCESS_CARD", "Access card"), new("VEHICLE", "Vehicle"),
        new("TOOLS", "Tools & equipment"), new("FURNITURE", "Furniture"), new("OTHER", "Other"),
    ];

    private static readonly AssetLookupValueDto[] DefaultConditions =
    [
        new("NEW", "New"), new("GOOD", "Good"), new("FAIR", "Fair"), new("POOR", "Poor"), new("DAMAGED", "Damaged"),
    ];

    private readonly ZayraDbContext _db;
    private readonly IAssetCustodyService _custody;

    public AssetsController(ZayraDbContext db, IAssetCustodyService custody)
    {
        _db = db;
        _custody = custody;
    }

    // ── Reads ────────────────────────────────────────────────────────────────────

    [HttpGet]
    [HasPermission("employees.read")]
    public async Task<ActionResult<AssetPagedResult>> List(
        [FromQuery] string? status, [FromQuery] string? category, [FromQuery] int? employeeId,
        [FromQuery] Guid? companyId, [FromQuery] string? q, [FromQuery] bool overdue = false,
        [FromQuery] int page = 1, [FromQuery] int pageSize = 50, CancellationToken ct = default)
    {
        var tenantId = Tenant();
        await _custody.ReconcileWriteOffsAsync(tenantId, Context(), ct);
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 200);
        var today = AssetCustodyService.Today();

        var active = _db.AssetAssignments.AsNoTracking()
            .Where(a => a.TenantId == tenantId && a.Status == AssetAssignmentStatuses.Active);
        var query = _db.Assets.AsNoTracking().Where(a => a.TenantId == tenantId);
        if (!string.IsNullOrWhiteSpace(status)) query = query.Where(a => a.Status == status);
        if (!string.IsNullOrWhiteSpace(category)) query = query.Where(a => a.CategoryCode == category.ToUpper());
        if (companyId is Guid cid) query = query.Where(a => a.CompanyId == cid);
        if (employeeId is int eid) query = query.Where(a => active.Any(x => x.AssetId == a.Id && x.EmployeeId == eid));
        if (overdue) query = query.Where(a => active.Any(x => x.AssetId == a.Id && x.ExpectedReturnDate != null && x.ExpectedReturnDate < today));
        if (!string.IsNullOrWhiteSpace(q))
        {
            var term = $"%{q.Trim()}%";
            query = query.Where(a => EF.Functions.ILike(a.AssetTag, term) || EF.Functions.ILike(a.Name, term)
                                     || EF.Functions.ILike(a.SerialNumber, term) || EF.Functions.ILike(a.Make + " " + a.Model, term)
                                     || active.Any(x => x.AssetId == a.Id && (EF.Functions.ILike(x.EmployeeName, term) || EF.Functions.ILike(x.EmployeeCode, term))));
        }

        var total = await query.CountAsync(ct);
        var assets = await query.OrderBy(a => a.AssetTag).Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
        var items = await ProjectAsync(tenantId, assets, today, ct);
        return Ok(new AssetPagedResult(total, page, pageSize, items));
    }

    [HttpGet("summary")]
    [HasPermission("employees.read")]
    public async Task<ActionResult<AssetSummaryDto>> Summary(CancellationToken ct)
    {
        var tenantId = Tenant();
        await _custody.ReconcileWriteOffsAsync(tenantId, Context(), ct);
        var today = AssetCustodyService.Today();
        var soon = today.AddDays(AssetReturnReminderWorker.DueSoonWindowDays);
        var byStatus = await _db.Assets.AsNoTracking().Where(a => a.TenantId == tenantId)
            .GroupBy(a => a.Status).Select(g => new { g.Key, Count = g.Count() }).ToListAsync(ct);
        var byCategory = await _db.Assets.AsNoTracking().Where(a => a.TenantId == tenantId && a.Status != AssetStatuses.Retired && a.Status != AssetStatuses.Lost)
            .GroupBy(a => a.CategoryCode).Select(g => new AssetCategoryCountDto(g.Key, g.Count())).ToListAsync(ct);
        var active = _db.AssetAssignments.AsNoTracking().Where(a => a.TenantId == tenantId && a.Status == AssetAssignmentStatuses.Active);
        var overdue = await active.CountAsync(a => a.ExpectedReturnDate != null && a.ExpectedReturnDate < today, ct);
        var dueSoon = await active.CountAsync(a => a.ExpectedReturnDate != null && a.ExpectedReturnDate >= today && a.ExpectedReturnDate <= soon, ct);
        var pending = await _db.AssetWriteOffRequests.CountAsync(w => w.TenantId == tenantId && w.Status == AssetWriteOffStatuses.Pending, ct);
        var statusMap = AssetStatuses.All.ToDictionary(s => s, s => byStatus.FirstOrDefault(x => x.Key == s)?.Count ?? 0);
        return Ok(new AssetSummaryDto(statusMap.Values.Sum(), statusMap, overdue, dueSoon, pending,
            byCategory.OrderByDescending(x => x.Count).ToList()));
    }

    [HttpGet("lookups")]
    [HasPermission("employees.read")]
    public async Task<ActionResult<AssetLookupsDto>> Lookups(CancellationToken ct)
    {
        var tenantId = Tenant();
        return Ok(new AssetLookupsDto(
            await LookupAsync(tenantId, "AssetCategory", DefaultCategories, ct),
            await LookupAsync(tenantId, "AssetCondition", DefaultConditions, ct)));
    }

    [HttpGet("{id:guid}")]
    [HasPermission("employees.read")]
    public async Task<ActionResult<AssetDetailDto>> Get(Guid id, CancellationToken ct)
    {
        var tenantId = Tenant();
        await _custody.ReconcileWriteOffsAsync(tenantId, Context(), ct, id);
        var asset = await _db.Assets.AsNoTracking().FirstOrDefaultAsync(a => a.TenantId == tenantId && a.Id == id, ct);
        if (asset is null) return NotFound();
        var today = AssetCustodyService.Today();
        var assignments = await _db.AssetAssignments.AsNoTracking()
            .Where(a => a.TenantId == tenantId && a.AssetId == id)
            .OrderByDescending(a => a.IssuedAtUtc).ToListAsync(ct);
        var writeOffs = await _db.AssetWriteOffRequests.AsNoTracking()
            .Where(w => w.TenantId == tenantId && w.AssetId == id)
            .OrderByDescending(w => w.RequestedAtUtc).ToListAsync(ct);
        var idText = id.ToString();
        var audit = await _db.AuditLogs.AsNoTracking()
            .Where(l => l.TenantId == tenantId && l.EntityName == nameof(Asset) && l.EntityId == idText)
            .OrderByDescending(l => l.CreatedAtUtc).Take(100)
            .Select(l => new AssetAuditEntryDto(l.Action, l.CreatedAtUtc, l.UserId, l.Metadata))
            .ToListAsync(ct);
        var holder = assignments.FirstOrDefault(a => a.Status == AssetAssignmentStatuses.Active);
        return Ok(new AssetDetailDto(
            AssetMapping.ToListItem(asset, holder, writeOffs.Any(w => w.Status == AssetWriteOffStatuses.Pending), today),
            assignments.Select(a => AssetMapping.ToDto(a, asset.AssetTag, asset.Name, asset.CategoryCode, today)).ToList(),
            writeOffs.Select(AssetMapping.ToDto).ToList(),
            audit));
    }

    /// <summary>Everything an employee holds now and has held before.</summary>
    [HttpGet("employees/{employeeId:int}")]
    [HasPermission("employees.read")]
    public async Task<ActionResult<EmployeeAssetsDto>> ForEmployee(int employeeId, CancellationToken ct)
    {
        var tenantId = Tenant();
        if (!await _db.Employees.AnyAsync(e => e.TenantId == tenantId && e.Id == employeeId && !e.IsDeleted, ct))
            return NotFound();
        await _custody.ReconcileWriteOffsAsync(tenantId, Context(), ct);
        return Ok(await LoadEmployeeAssetsAsync(_db, tenantId, employeeId, ct));
    }

    /// <summary>Offboarding clearance: the leaver's outstanding items and any write-offs awaiting approval.</summary>
    [HttpGet("clearance/{employeeId:int}")]
    [HasPermission("employees.read")]
    public async Task<ActionResult<AssetClearanceDto>> Clearance(int employeeId, CancellationToken ct)
    {
        var tenantId = Tenant();
        if (!await _db.Employees.AnyAsync(e => e.TenantId == tenantId && e.Id == employeeId && !e.IsDeleted, ct))
            return NotFound();
        await _custody.ReconcileWriteOffsAsync(tenantId, Context(), ct);
        return Ok(await new AssetClearanceService(_db).GetClearanceAsync(tenantId, employeeId, ct));
    }

    // ── Writes ───────────────────────────────────────────────────────────────────

    [HttpPost]
    [HasPermission("employees.write")]
    public Task<IActionResult> Create([FromBody] AssetUpsertRequest request, CancellationToken ct)
        => Execute(async () => await DetailOf((await _custody.CreateAsync(Tenant(), request, Context(), ct)).Id, ct));

    [HttpPut("{id:guid}")]
    [HasPermission("employees.write")]
    public Task<IActionResult> Update(Guid id, [FromBody] AssetUpsertRequest request, CancellationToken ct)
        => Execute(async () => await DetailOf((await _custody.UpdateAsync(Tenant(), id, request, Context(), ct)).Id, ct));

    [HttpPost("{id:guid}/issue")]
    [HasPermission("employees.write")]
    public Task<IActionResult> Issue(Guid id, [FromBody] IssueAssetRequest request, CancellationToken ct)
        => Execute(async () => { await _custody.IssueAsync(Tenant(), id, request, Context(), ct); return await DetailOf(id, ct); });

    [HttpPost("{id:guid}/return")]
    [HasPermission("employees.write")]
    public Task<IActionResult> Return(Guid id, [FromBody] ReturnAssetRequest request, CancellationToken ct)
        => Execute(async () => { await _custody.ReturnAsync(Tenant(), id, request, Context(), ct); return await DetailOf(id, ct); });

    [HttpPost("{id:guid}/transfer")]
    [HasPermission("employees.write")]
    public Task<IActionResult> Transfer(Guid id, [FromBody] TransferAssetRequest request, CancellationToken ct)
        => Execute(async () => { await _custody.TransferAsync(Tenant(), id, request, Context(), ct); return await DetailOf(id, ct); });

    /// <summary>Report an item lost or damaged. Creates a write-off request routed for approval; nothing
    /// changes on the asset until the workflow's final step approves it.</summary>
    [HttpPost("{id:guid}/write-off")]
    [HasPermission("employees.write")]
    public Task<IActionResult> WriteOff(Guid id, [FromBody] WriteOffAssetRequest request, CancellationToken ct)
        => Execute(async () => { await _custody.RequestWriteOffAsync(Tenant(), id, request, Context(), ct); return await DetailOf(id, ct); });

    [HttpPost("{id:guid}/retire")]
    [HasPermission("employees.write")]
    public Task<IActionResult> Retire(Guid id, [FromBody] RetireAssetRequest request, CancellationToken ct)
        => Execute(async () => { await _custody.RetireAsync(Tenant(), id, request, Context(), ct); return await DetailOf(id, ct); });

    [HttpPost("{id:guid}/repair")]
    [HasPermission("employees.write")]
    public Task<IActionResult> Repair(Guid id, [FromBody] RepairAssetRequest request, CancellationToken ct)
        => Execute(async () => { await _custody.RepairAsync(Tenant(), id, request, Context(), ct); return await DetailOf(id, ct); });

    [HttpPatch("assignments/{assignmentId:guid}/expected-return")]
    [HasPermission("employees.write")]
    public Task<IActionResult> ExtendReturn(Guid assignmentId, [FromBody] ExtendReturnDateRequest request, CancellationToken ct)
        => Execute(async () =>
        {
            var a = await _custody.SetExpectedReturnDateAsync(Tenant(), assignmentId, request.ExpectedReturnDate, Context(), ct);
            return await DetailOf(a.AssetId, ct);
        });

    // ── Shared ───────────────────────────────────────────────────────────────────

    internal static async Task<EmployeeAssetsDto> LoadEmployeeAssetsAsync(ZayraDbContext db, Guid tenantId, int employeeId, CancellationToken ct)
    {
        var today = AssetCustodyService.Today();
        var rows = await (
                from a in db.AssetAssignments.AsNoTracking()
                join s in db.Assets.AsNoTracking() on a.AssetId equals s.Id
                where a.TenantId == tenantId && a.EmployeeId == employeeId
                orderby a.IssuedAtUtc descending
                select new { a, s.AssetTag, s.Name, s.CategoryCode })
            .ToListAsync(ct);
        var dtos = rows.Select(x => AssetMapping.ToDto(x.a, x.AssetTag, x.Name, x.CategoryCode, today)).ToList();
        return new EmployeeAssetsDto(employeeId,
            dtos.Where(d => d.Status == AssetAssignmentStatuses.Active).ToList(),
            dtos.Where(d => d.Status != AssetAssignmentStatuses.Active).ToList());
    }

    private async Task<IReadOnlyList<AssetListItemDto>> ProjectAsync(Guid tenantId, List<Asset> assets, DateOnly today, CancellationToken ct)
    {
        var ids = assets.Select(a => a.Id).ToList();
        var holders = await _db.AssetAssignments.AsNoTracking()
            .Where(a => a.TenantId == tenantId && ids.Contains(a.AssetId) && a.Status == AssetAssignmentStatuses.Active)
            .ToListAsync(ct);
        var pending = (await _db.AssetWriteOffRequests.AsNoTracking()
                .Where(w => w.TenantId == tenantId && ids.Contains(w.AssetId) && w.Status == AssetWriteOffStatuses.Pending)
                .Select(w => w.AssetId).ToListAsync(ct))
            .ToHashSet();
        return assets.Select(a => AssetMapping.ToListItem(a, holders.FirstOrDefault(h => h.AssetId == a.Id), pending.Contains(a.Id), today)).ToList();
    }

    private async Task<IActionResult> DetailOf(Guid id, CancellationToken ct)
    {
        var result = await Get(id, ct);
        return result.Result ?? Ok(result.Value);
    }

    private async Task<IReadOnlyList<AssetLookupValueDto>> LookupAsync(Guid tenantId, string typeCode, AssetLookupValueDto[] defaults, CancellationToken ct)
    {
        var type = await _db.MasterDataTypes.AsNoTracking()
            .FirstOrDefaultAsync(t => t.TenantId == tenantId && t.Code == typeCode && !t.IsDeleted && t.IsActive, ct);
        if (type is null) return defaults;
        var values = await _db.MasterDataValues.AsNoTracking()
            .Where(v => v.TenantId == tenantId && v.TypeId == type.Id && !v.IsDeleted && v.IsActive)
            .OrderBy(v => v.SortOrder).ThenBy(v => v.ValueEn)
            .Select(v => new AssetLookupValueDto(v.Code, v.ValueEn))
            .ToListAsync(ct);
        return values.Count > 0 ? values : defaults;
    }

    private async Task<IActionResult> Execute(Func<Task<IActionResult>> action)
    {
        try
        {
            return await action();
        }
        catch (AssetOperationException ex)
        {
            return StatusCode(ex.Status, new { code = ex.Code, error = ex.Code, message = ex.Message, details = ex.Details });
        }
        catch (ApprovalRoutingException ex)
        {
            return UnprocessableEntity(new { code = ex.Code, error = ex.Code, message = ex.Message });
        }
        catch (UnauthorizedAccessException)
        {
            return Forbid();
        }
    }

    private Guid Tenant() => this.GetTenantId() ?? throw new UnauthorizedAccessException("Tenant claim is missing.");

    private RequestContext Context() => new(
        HttpContext?.Connection.RemoteIpAddress?.ToString(),
        HttpContext?.Request.Headers.UserAgent.ToString(),
        this.GetUserId(),
        this.GetTenantId());
}
