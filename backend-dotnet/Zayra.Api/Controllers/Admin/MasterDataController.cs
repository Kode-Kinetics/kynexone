using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Authorization;
using Zayra.Api.Models;

namespace Zayra.Api.Controllers.Admin;

[Authorize]
[ApiController]
[Route("api/admin/master-data")]
public class MasterDataController : ControllerBase
{
    private readonly ZayraDbContext _db;
    public MasterDataController(ZayraDbContext db) => _db = db;

    private Guid GetTenantId() =>
        Guid.TryParse(User.FindFirst("tenant_id")?.Value, out var id) ? id : Guid.Empty;
    private Guid? GetUserId() =>
        Guid.TryParse(User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value, out var id) ? id : null;

    // ── Types ─────────────────────────────────────────────────────────────────

    [HttpGet("types")]
    public async Task<IActionResult> ListTypes([FromQuery] bool activeOnly = true, CancellationToken ct = default)
    {
        var tid = GetTenantId();
        var q = _db.MasterDataTypes.Where(x => x.TenantId == tid && !x.IsDeleted);
        if (activeOnly) q = q.Where(x => x.IsActive);
        return Ok(await q.OrderBy(x => x.NameEn).ToListAsync(ct));
    }

    [HttpPost("types")]
    [HasPermission("organization.write")]
    public async Task<IActionResult> CreateType([FromBody] MasterDataTypeRequest req, CancellationToken ct)
    {
        var tid = GetTenantId();
        var uid = GetUserId();
        if (await _db.MasterDataTypes.AnyAsync(x => x.TenantId == tid && x.Code == req.Code && !x.IsDeleted, ct))
            return Conflict("A type with this code already exists.");

        var t = new MasterDataType
        {
            TenantId = tid, Code = req.Code, NameEn = req.NameEn, NameAr = req.NameAr ?? string.Empty,
            Description = req.Description ?? string.Empty, AllowCustomValues = req.AllowCustomValues,
            IsActive = true, CreatedBy = uid,
        };
        _db.MasterDataTypes.Add(t);
        await _db.SaveChangesAsync(ct);
        await WriteAdminAuditLog(tid, uid, "MasterDataType", t.Id.ToString(), "Created", null, System.Text.Json.JsonSerializer.Serialize(new { t.Code, t.NameEn }), ct);
        return Ok(t);
    }

    [HttpPut("types/{id:guid}")]
    [HasPermission("organization.write")]
    public async Task<IActionResult> UpdateType(Guid id, [FromBody] MasterDataTypeRequest req, CancellationToken ct)
    {
        var tid = GetTenantId();
        var uid = GetUserId();
        var t = await _db.MasterDataTypes.FirstOrDefaultAsync(x => x.Id == id && x.TenantId == tid && !x.IsDeleted, ct);
        if (t == null) return NotFound();
        var old = System.Text.Json.JsonSerializer.Serialize(new { t.NameEn, t.IsActive });
        t.NameEn = req.NameEn; t.NameAr = req.NameAr ?? string.Empty;
        t.Description = req.Description ?? string.Empty; t.AllowCustomValues = req.AllowCustomValues;
        t.UpdatedAtUtc = DateTime.UtcNow; t.UpdatedBy = uid;
        await _db.SaveChangesAsync(ct);
        await WriteAdminAuditLog(tid, uid, "MasterDataType", id.ToString(), "Updated", old, System.Text.Json.JsonSerializer.Serialize(new { t.NameEn, t.IsActive }), ct);
        return Ok(t);
    }

    [HttpDelete("types/{id:guid}")]
    [HasPermission("organization.delete")]
    public async Task<IActionResult> DeleteType(Guid id, CancellationToken ct)
    {
        var tid = GetTenantId();
        var t = await _db.MasterDataTypes.FirstOrDefaultAsync(x => x.Id == id && x.TenantId == tid && !x.IsDeleted, ct);
        if (t == null) return NotFound();
        if (t.IsSystemDefined) return BadRequest("System-defined types cannot be deleted.");
        t.IsDeleted = true; t.UpdatedAtUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        return NoContent();
    }

    // ── Values ────────────────────────────────────────────────────────────────

    [HttpGet("types/{typeId:guid}/values")]
    public async Task<IActionResult> ListValues(Guid typeId, [FromQuery] bool activeOnly = true, CancellationToken ct = default)
    {
        var tid = GetTenantId();
        var q = _db.MasterDataValues.Where(x => x.TenantId == tid && x.TypeId == typeId && !x.IsDeleted);
        if (activeOnly) q = q.Where(x => x.IsActive);
        return Ok(await q.OrderBy(x => x.SortOrder).ThenBy(x => x.ValueEn).ToListAsync(ct));
    }

    [HttpGet("values")]
    public async Task<IActionResult> ListValuesByTypeCode([FromQuery] string typeCode, [FromQuery] bool activeOnly = true, CancellationToken ct = default)
    {
        var tid = GetTenantId();
        var type = await _db.MasterDataTypes.FirstOrDefaultAsync(x => x.TenantId == tid && x.Code == typeCode && !x.IsDeleted, ct);
        if (type == null) return NotFound();
        var q = _db.MasterDataValues.Where(x => x.TenantId == tid && x.TypeId == type.Id && !x.IsDeleted);
        if (activeOnly) q = q.Where(x => x.IsActive);
        return Ok(await q.OrderBy(x => x.SortOrder).ThenBy(x => x.ValueEn).ToListAsync(ct));
    }

    [HttpPost("types/{typeId:guid}/values")]
    [HasPermission("organization.write")]
    public async Task<IActionResult> CreateValue(Guid typeId, [FromBody] MasterDataValueRequest req, CancellationToken ct)
    {
        var tid = GetTenantId();
        var uid = GetUserId();
        if (!await _db.MasterDataTypes.AnyAsync(x => x.Id == typeId && x.TenantId == tid && !x.IsDeleted, ct))
            return NotFound("Type not found.");
        if (await _db.MasterDataValues.AnyAsync(x => x.TenantId == tid && x.TypeId == typeId && x.Code == req.Code && !x.IsDeleted, ct))
            return Conflict("A value with this code already exists in this type.");

        var v = new MasterDataValue
        {
            TenantId = tid, TypeId = typeId, Code = req.Code, ValueEn = req.ValueEn,
            ValueAr = req.ValueAr ?? string.Empty, ExtraJson = req.ExtraJson,
            SortOrder = req.SortOrder, IsDefault = req.IsDefault, IsActive = true, CreatedBy = uid,
        };
        _db.MasterDataValues.Add(v);
        await _db.SaveChangesAsync(ct);
        return Ok(v);
    }

    [HttpPut("values/{id:guid}")]
    [HasPermission("organization.write")]
    public async Task<IActionResult> UpdateValue(Guid id, [FromBody] MasterDataValueRequest req, CancellationToken ct)
    {
        var tid = GetTenantId();
        var uid = GetUserId();
        var v = await _db.MasterDataValues.FirstOrDefaultAsync(x => x.Id == id && x.TenantId == tid && !x.IsDeleted, ct);
        if (v == null) return NotFound();
        v.ValueEn = req.ValueEn; v.ValueAr = req.ValueAr ?? string.Empty;
        v.ExtraJson = req.ExtraJson; v.SortOrder = req.SortOrder;
        v.IsDefault = req.IsDefault; v.IsActive = req.IsActive;
        v.UpdatedAtUtc = DateTime.UtcNow; v.UpdatedBy = uid;
        await _db.SaveChangesAsync(ct);
        return Ok(v);
    }

    [HttpDelete("values/{id:guid}")]
    [HasPermission("organization.write")]
    public async Task<IActionResult> DeleteValue(Guid id, CancellationToken ct)
    {
        var tid = GetTenantId();
        var v = await _db.MasterDataValues.FirstOrDefaultAsync(x => x.Id == id && x.TenantId == tid && !x.IsDeleted, ct);
        if (v == null) return NotFound();
        if (v.IsSystemDefined) return BadRequest("System-defined values cannot be deleted.");
        v.IsDeleted = true; v.UpdatedAtUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        return NoContent();
    }

    private async Task WriteAdminAuditLog(Guid tid, Guid? uid, string entity, string entityId, string action, string? oldVal, string newVal, CancellationToken ct)
    {
        _db.AdminAuditLogs.Add(new AdminAuditLog
        {
            TenantId = tid, EntityType = entity, EntityId = entityId, Action = action,
            OldValuesJson = oldVal ?? string.Empty, NewValuesJson = newVal,
            PerformedBy = uid, CreatedAtUtc = DateTime.UtcNow,
        });
        await _db.SaveChangesAsync(ct);
    }
}

public record MasterDataTypeRequest(string Code, string NameEn, string? NameAr, string? Description, bool AllowCustomValues);
public record MasterDataValueRequest(string Code, string ValueEn, string? ValueAr, string? ExtraJson, int SortOrder, bool IsDefault, bool IsActive = true);
