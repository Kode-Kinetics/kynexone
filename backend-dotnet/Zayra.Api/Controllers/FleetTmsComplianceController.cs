using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Common;
using Zayra.Api.Data;
using Zayra.Api.Models;

namespace Zayra.Api.Controllers;

[ApiController]
[Route("api/fleet-tms")]
[Authorize(Roles = "Admin,HR Director,HR Manager,Manager,Supervisor")]
public class FleetTmsComplianceController : ControllerBase
{
    private const int ExpiryWindowDays = 30;

    private readonly ZayraDbContext _db;

    public FleetTmsComplianceController(ZayraDbContext db) => _db = db;

    [HttpGet("saudi/regions")]
    public async Task<IActionResult> GetRegions(CancellationToken ct)
    {
        if (!CanRead()) return Forbid();

        var items = await _db.SaudiRegionReferences.AsNoTracking()
            .OrderBy(x => x.SortOrder)
            .ThenBy(x => x.NameEn)
            .ToListAsync(ct);

        return Ok(new
        {
            items = items.Select(x => new
            {
                x.Id,
                x.Code,
                x.NameEn,
                x.NameAr,
                x.CountryCode,
                x.IsGccReady,
                cities = ParseCities(x.CitiesJson),
            })
        });
    }

    [HttpGet("compliance/documents")]
    public async Task<IActionResult> GetDocuments([FromQuery] string? kind, [FromQuery] string? subjectType, CancellationToken ct)
    {
        if (!CanRead()) return Forbid();

        var tenantId = RequireTenant();
        var query = _db.FleetReadinessDocuments.AsNoTracking().Where(x => x.TenantId == tenantId);
        if (!string.IsNullOrWhiteSpace(kind))
            query = query.Where(x => x.Kind == kind);
        if (!string.IsNullOrWhiteSpace(subjectType))
            query = query.Where(x => x.SubjectType == subjectType);

        var items = await query
            .OrderByDescending(x => x.GregorianExpiryDate)
            .ThenBy(x => x.SubjectName)
            .ToListAsync(ct);

        return Ok(new { items = items.Select(ToDocumentDto) });
    }

    [HttpPost("compliance/documents")]
    public async Task<IActionResult> CreateDocument([FromBody] FleetReadinessDocumentRequest req, CancellationToken ct)
    {
        if (!CanManage()) return Forbid();
        var validationError = Validate(req);
        if (validationError is not null) return BadRequest(new { message = validationError });

        var tenantId = RequireTenant();
        var entity = MapDocument(tenantId, req);
        _db.FleetReadinessDocuments.Add(entity);
        await _db.SaveChangesAsync(ct);
        return Ok(ToDto(entity));
    }

    [HttpPut("compliance/documents/{id:guid}")]
    public async Task<IActionResult> UpdateDocument(Guid id, [FromBody] FleetReadinessDocumentRequest req, CancellationToken ct)
    {
        if (!CanManage()) return Forbid();
        var validationError = Validate(req);
        if (validationError is not null) return BadRequest(new { message = validationError });

        var tenantId = RequireTenant();
        var entity = await _db.FleetReadinessDocuments.FirstOrDefaultAsync(x => x.Id == id && x.TenantId == tenantId, ct);
        if (entity is null) return NotFound(new { message = "Compliance document not found for this tenant." });

        Apply(entity, req);
        await _db.SaveChangesAsync(ct);
        return Ok(ToDto(entity));
    }

    [HttpGet("compliance/expiries")]
    public async Task<IActionResult> GetExpiries(CancellationToken ct)
    {
        if (!CanRead()) return Forbid();

        var tenantId = RequireTenant();
        var docs = await _db.FleetReadinessDocuments.AsNoTracking()
            .Where(x => x.TenantId == tenantId)
            .ToListAsync(ct);

        var today = DateOnly.FromDateTime(DateTime.UtcNow.Date);
        var rows = docs
            .Select(ToExpiryRow)
            .Where(x => x.DaysRemaining <= ExpiryWindowDays)
            .OrderBy(x => x.DaysRemaining)
            .ThenBy(x => x.GregorianExpiryDate)
            .ToList();

        return Ok(new
        {
            generatedAtUtc = DateTime.UtcNow,
            items = rows,
            summary = new
            {
                totalDocuments = docs.Count,
                expiringSoon = rows.Count(x => x.DaysRemaining >= 0),
                expired = rows.Count(x => x.DaysRemaining < 0),
                healthy = docs.Count - rows.Count,
                windowDays = ExpiryWindowDays,
                today,
            }
        });
    }

    [HttpGet("vat/invoice-ready")]
    public async Task<IActionResult> GetInvoiceReady(CancellationToken ct)
    {
        if (!CanRead()) return Forbid();

        var tenantId = RequireTenant();
        var shipments = _db.FleetShipments.AsNoTracking().Where(x => x.TenantId == tenantId);
        var carriers = _db.Carriers.AsNoTracking().Where(x => x.TenantId == tenantId);
        var branches = _db.Branches.AsNoTracking().Where(x => x.TenantId == tenantId);

        var readyShipments = await shipments
            .Where(x => x.IsInvoiceReady
                && !string.IsNullOrWhiteSpace(x.CustomerVATNumber)
                && !string.IsNullOrWhiteSpace(x.CustomerCommercialRegistrationNo))
            .OrderByDescending(x => x.InvoiceReadyAtUtc)
            .ThenBy(x => x.ShipmentNumber)
            .Select(x => new
            {
                x.Id,
                x.ShipmentNumber,
                x.CustomerName,
                x.Status,
                x.CustomerVATNumber,
                x.CustomerCommercialRegistrationNo,
                x.InvoiceReadyAtUtc,
                x.InvoiceReadinessNotes,
                x.Origin,
                x.Destination,
                x.CarrierName,
                x.RouteCode,
            })
            .Take(10)
            .ToListAsync(ct);

        var blockedShipments = await shipments
            .Where(x => !x.IsInvoiceReady || string.IsNullOrWhiteSpace(x.CustomerVATNumber) || string.IsNullOrWhiteSpace(x.CustomerCommercialRegistrationNo))
            .OrderByDescending(x => x.CreatedAtUtc)
            .Select(x => new
            {
                x.Id,
                x.ShipmentNumber,
                x.CustomerName,
                x.Status,
                x.InvoiceReadinessNotes,
                x.Origin,
                x.Destination,
                x.CarrierName,
                x.RouteCode,
            })
            .Take(10)
            .ToListAsync(ct);

        var carrierReady = await carriers.CountAsync(x => !string.IsNullOrWhiteSpace(x.VATNumber) && !string.IsNullOrWhiteSpace(x.CommercialRegistrationNo), ct);
        var branchReady = await branches.CountAsync(x => !string.IsNullOrWhiteSpace(x.VATNumber) && !string.IsNullOrWhiteSpace(x.CommercialRegistrationNo), ct);
        var readyCount = readyShipments.Count;
        var blockedCount = blockedShipments.Count;
        var readinessPercent = readyCount + blockedCount == 0 ? 0 : Math.Round(readyCount / (decimal)(readyCount + blockedCount) * 100m, 1);

        return Ok(new
        {
            generatedAtUtc = DateTime.UtcNow,
            summary = new
            {
                readyCount,
                blockedCount,
                readinessPercent,
                carrierReady,
                branchReady,
            },
            readyShipments,
            blockedShipments,
        });
    }

    private static string? Validate(FleetReadinessDocumentRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Kind))
            return "Document kind is required.";
        if (string.IsNullOrWhiteSpace(req.SubjectType))
            return "Subject type is required.";
        if (string.IsNullOrWhiteSpace(req.SubjectName))
            return "Subject name is required.";
        if (string.IsNullOrWhiteSpace(req.DocumentType))
            return "Document type is required.";
        if (req.RequiresExpiry && !req.GregorianExpiryDate.HasValue && !req.HijriExpiryDate.HasValue)
            return "Expiry date is required for readiness documents.";
        return null;
    }

    private FleetReadinessDocument MapDocument(Guid tenantId, FleetReadinessDocumentRequest req)
    {
        var entity = new FleetReadinessDocument
        {
            TenantId = tenantId,
            CreatedAtUtc = DateTime.UtcNow,
            UpdatedAtUtc = DateTime.UtcNow,
        };
        Apply(entity, req);
        return entity;
    }

    private static void Apply(FleetReadinessDocument entity, FleetReadinessDocumentRequest req)
    {
        entity.Kind = req.Kind.Trim();
        entity.SubjectType = req.SubjectType.Trim();
        entity.SubjectId = req.SubjectId?.Trim() ?? string.Empty;
        entity.SubjectName = req.SubjectName.Trim();
        entity.DocumentType = req.DocumentType.Trim();
        entity.DocumentNumber = req.DocumentNumber?.Trim() ?? string.Empty;
        entity.TransportDocumentNo = req.TransportDocumentNo?.Trim() ?? string.Empty;
        entity.PermitNo = req.PermitNo?.Trim() ?? string.Empty;
        entity.VATNumber = req.VATNumber?.Trim() ?? string.Empty;
        entity.CommercialRegistrationNo = req.CommercialRegistrationNo?.Trim() ?? string.Empty;
        entity.CountryCode = req.CountryCode?.Trim() ?? "SA";
        entity.NationalAddressBuildingNo = req.NationalAddressBuildingNo?.Trim() ?? string.Empty;
        entity.NationalAddressAdditionalNo = req.NationalAddressAdditionalNo?.Trim() ?? string.Empty;
        entity.District = req.District?.Trim() ?? string.Empty;
        entity.City = req.City?.Trim() ?? string.Empty;
        entity.Region = req.Region?.Trim() ?? string.Empty;
        entity.PostalCode = req.PostalCode?.Trim() ?? string.Empty;
        entity.DocumentStatus = req.DocumentStatus?.Trim() ?? "Active";
        entity.ExpiryStatus = ComputeExpiryStatus(req.GregorianExpiryDate, req.DocumentStatus);
        entity.IssueDate = req.IssueDate;
        entity.HijriExpiryDate = req.HijriExpiryDate;
        entity.GregorianExpiryDate = req.GregorianExpiryDate;
        entity.Notes = req.Notes?.Trim() ?? string.Empty;
        entity.UpdatedAtUtc = DateTime.UtcNow;
    }

    private static object ToDto(FleetReadinessDocument x) => new
    {
        x.Id,
        x.Kind,
        x.SubjectType,
        x.SubjectId,
        x.SubjectName,
        x.DocumentType,
        x.DocumentNumber,
        x.TransportDocumentNo,
        x.PermitNo,
        x.VATNumber,
        x.CommercialRegistrationNo,
        x.CountryCode,
        x.NationalAddressBuildingNo,
        x.NationalAddressAdditionalNo,
        x.District,
        x.City,
        x.Region,
        x.PostalCode,
        x.DocumentStatus,
        x.ExpiryStatus,
        x.IssueDate,
        x.HijriExpiryDate,
        x.GregorianExpiryDate,
        x.Notes,
        x.CreatedAtUtc,
        x.UpdatedAtUtc,
    };

    private static object ToDocumentDto(FleetReadinessDocument x) => ToDto(x);

    private static ExpiryRow ToExpiryRow(FleetReadinessDocument x)
    {
        var expiry = x.GregorianExpiryDate ?? x.HijriExpiryDate;
        var daysRemaining = expiry.HasValue ? expiry.Value.ToDateTime(TimeOnly.MinValue).Date.Subtract(DateTime.UtcNow.Date).Days : int.MaxValue;
        return new ExpiryRow(
            x.Id,
            x.Kind,
            x.SubjectType,
            x.SubjectName,
            x.DocumentType,
            x.DocumentNumber,
            x.DocumentStatus,
            x.ExpiryStatus,
            x.CountryCode,
            x.GregorianExpiryDate,
            x.HijriExpiryDate,
            daysRemaining,
            x.Notes);
    }

    private static string ComputeExpiryStatus(DateOnly? gregorianExpiryDate, string? documentStatus)
    {
        if (!string.IsNullOrWhiteSpace(documentStatus) && !string.Equals(documentStatus, "Active", StringComparison.OrdinalIgnoreCase))
            return documentStatus.Trim();
        if (!gregorianExpiryDate.HasValue)
            return "Healthy";

        var days = gregorianExpiryDate.Value.ToDateTime(TimeOnly.MinValue).Date.Subtract(DateTime.UtcNow.Date).Days;
        if (days < 0) return "Expired";
        if (days <= ExpiryWindowDays) return "ExpiringSoon";
        return "Healthy";
    }

    private static IReadOnlyCollection<string> ParseCities(string citiesJson)
    {
        try
        {
            return JsonSerializer.Deserialize<string[]>(citiesJson) ?? [];
        }
        catch
        {
            return [];
        }
    }

    private Guid RequireTenant()
        => Guid.Parse(User.FindFirstValue("tenant_id") ?? throw new UnauthorizedAccessException("Tenant claim missing."));

    private bool CanRead() => HasPermission("fleet_tms.saudi_readiness.view") || HasPermission("fleet_tms.compliance.view") || HasPermission("fleet.read");
    private bool CanManage() => HasPermission("fleet_tms.saudi_readiness.manage") || HasPermission("fleet_tms.compliance.manage") || HasPermission("fleet.write");
    private bool HasPermission(string permission) =>
        User.Claims.Any(c => c.Type == "permission" && string.Equals(c.Value, permission, StringComparison.OrdinalIgnoreCase));
}

public record FleetReadinessDocumentRequest(
    string Kind,
    string SubjectType,
    string? SubjectId,
    string SubjectName,
    string DocumentType,
    string? DocumentNumber,
    string? TransportDocumentNo,
    string? PermitNo,
    string? VATNumber,
    string? CommercialRegistrationNo,
    string? CountryCode,
    string? NationalAddressBuildingNo,
    string? NationalAddressAdditionalNo,
    string? District,
    string? City,
    string? Region,
    string? PostalCode,
    string? DocumentStatus,
    DateOnly? IssueDate,
    DateOnly? HijriExpiryDate,
    DateOnly? GregorianExpiryDate,
    string? Notes,
    bool RequiresExpiry = true);

public record ExpiryRow(
    Guid Id,
    string Kind,
    string SubjectType,
    string SubjectName,
    string DocumentType,
    string DocumentNumber,
    string DocumentStatus,
    string ExpiryStatus,
    string CountryCode,
    DateOnly? GregorianExpiryDate,
    DateOnly? HijriExpiryDate,
    int DaysRemaining,
    string Notes);
