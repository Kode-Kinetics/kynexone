using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Common;
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
        if (!this.HasGroupEntityScope()) return Forbid();
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
        if (!this.HasGroupEntityScope()) return Forbid();
        var tid = GetTenantId();
        var uid = GetUserId();
        var t = await _db.MasterDataTypes.FirstOrDefaultAsync(x => x.Id == id && x.TenantId == tid && !x.IsDeleted, ct);
        if (t == null) return NotFound();
        // Status is an explicit decision: absent means "keep", never "reactivate". A system-defined type backs
        // built-in forms, so — like delete — it cannot be retired.
        if (req.IsActive == false && t.IsSystemDefined && t.IsActive)
            return BadRequest("System-defined types cannot be deactivated.");
        var old = System.Text.Json.JsonSerializer.Serialize(new { t.NameEn, t.IsActive });
        t.NameEn = req.NameEn; t.NameAr = req.NameAr ?? string.Empty;
        t.Description = req.Description ?? string.Empty; t.AllowCustomValues = req.AllowCustomValues;
        if (req.IsActive is bool typeActive) t.IsActive = typeActive;
        t.UpdatedAtUtc = DateTime.UtcNow; t.UpdatedBy = uid;
        await _db.SaveChangesAsync(ct);
        await WriteAdminAuditLog(tid, uid, "MasterDataType", id.ToString(), "Updated", old, System.Text.Json.JsonSerializer.Serialize(new { t.NameEn, t.IsActive }), ct);
        return Ok(t);
    }

    [HttpDelete("types/{id:guid}")]
    [HasPermission("organization.delete")]
    public async Task<IActionResult> DeleteType(Guid id, CancellationToken ct)
    {
        if (!this.HasGroupEntityScope()) return Forbid();
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
        if (!this.HasGroupEntityScope()) return Forbid();
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
        if (!this.HasGroupEntityScope()) return Forbid();
        var tid = GetTenantId();
        var uid = GetUserId();
        var v = await _db.MasterDataValues.FirstOrDefaultAsync(x => x.Id == id && x.TenantId == tid && !x.IsDeleted, ct);
        if (v == null) return NotFound();
        var old = System.Text.Json.JsonSerializer.Serialize(new { v.ValueEn, v.IsActive });
        v.ValueEn = req.ValueEn; v.ValueAr = req.ValueAr ?? string.Empty;
        // Absent means "keep" for the two fields the edit form does not carry: an unrelated rename used to
        // reactivate a retired value (IsActive defaulted to true) and wipe its ExtraJson metadata.
        if (req.ExtraJson is not null) v.ExtraJson = req.ExtraJson;
        v.SortOrder = req.SortOrder;
        v.IsDefault = req.IsDefault;
        if (req.IsActive is bool valueActive) v.IsActive = valueActive;
        v.UpdatedAtUtc = DateTime.UtcNow; v.UpdatedBy = uid;
        await _db.SaveChangesAsync(ct);
        await WriteAdminAuditLog(tid, uid, "MasterDataValue", id.ToString(), "Updated", old, System.Text.Json.JsonSerializer.Serialize(new { v.ValueEn, v.IsActive }), ct);
        return Ok(v);
    }

    [HttpDelete("values/{id:guid}")]
    [HasPermission("organization.write")]
    public async Task<IActionResult> DeleteValue(Guid id, CancellationToken ct)
    {
        if (!this.HasGroupEntityScope()) return Forbid();
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

/// <summary><c>IsActive</c> is ignored on create (new types start active); on update, null keeps the current status.</summary>
public record MasterDataTypeRequest(string Code, string NameEn, string? NameAr, string? Description, bool AllowCustomValues, bool? IsActive = null);
/// <summary>New values always start active. On update, a null <c>IsActive</c> or <c>ExtraJson</c> keeps the current value.</summary>
public record MasterDataValueRequest(string Code, string ValueEn, string? ValueAr, string? ExtraJson, int SortOrder, bool IsDefault, bool? IsActive = null);
