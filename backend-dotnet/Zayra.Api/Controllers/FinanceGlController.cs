using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Common;
using Zayra.Api.Data;
using Zayra.Api.Models;

namespace Zayra.Api.Controllers;

/// <summary>
/// Chart of accounts + payroll GL driver→account mappings. Lets finance point payroll
/// posting at their own GL codes instead of the built-in defaults.
/// </summary>
[ApiController]
[Route("api/finance/gl")]
[Authorize(Roles = "Admin,Finance")]
public class FinanceGlController : ControllerBase
{
    private readonly ZayraDbContext _db;
    public FinanceGlController(ZayraDbContext db) => _db = db;

    // ── Chart of accounts ────────────────────────────────────────────────────

    [HttpGet("accounts")]
    public async Task<IActionResult> ListAccounts(CancellationToken ct)
    {
        var tid = this.GetTenantId(); if (tid is null) return Unauthorized();
        var accounts = await _db.GlAccounts.AsNoTracking()
            .Where(a => a.TenantId == tid).OrderBy(a => a.Code).ToListAsync(ct);
        return Ok(accounts.Select(ToDto));
    }

    [HttpPost("accounts")]
    public async Task<IActionResult> CreateAccount([FromBody] GlAccountRequest req, CancellationToken ct)
    {
        var tid = this.GetTenantId(); if (tid is null) return Unauthorized();
        var code = (req.Code ?? "").Trim();
        if (string.IsNullOrWhiteSpace(code) || string.IsNullOrWhiteSpace(req.Name)) return BadRequest(new { message = "Code and Name are required." });
        if (await _db.GlAccounts.AnyAsync(a => a.TenantId == tid && a.Code == code, ct))
            return BadRequest(new { message = $"Account code '{code}' already exists." });
        var acct = new GlAccount { TenantId = tid.Value, Code = code, Name = req.Name.Trim(), AccountType = req.AccountType, IsActive = req.IsActive };
        _db.GlAccounts.Add(acct);
        await _db.SaveChangesAsync(ct);
        return Ok(ToDto(acct));
    }

    [HttpPut("accounts/{id:guid}")]
    public async Task<IActionResult> UpdateAccount(Guid id, [FromBody] GlAccountRequest req, CancellationToken ct)
    {
        var tid = this.GetTenantId(); if (tid is null) return Unauthorized();
        var acct = await _db.GlAccounts.FirstOrDefaultAsync(a => a.TenantId == tid && a.Id == id, ct);
        if (acct is null) return NotFound();
        acct.Name = req.Name.Trim(); acct.AccountType = req.AccountType; acct.IsActive = req.IsActive; acct.UpdatedAtUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        return Ok(ToDto(acct));
    }

    [HttpDelete("accounts/{id:guid}")]
    public async Task<IActionResult> DeleteAccount(Guid id, CancellationToken ct)
    {
        var tid = this.GetTenantId(); if (tid is null) return Unauthorized();
        var acct = await _db.GlAccounts.FirstOrDefaultAsync(a => a.TenantId == tid && a.Id == id, ct);
        if (acct is null) return NotFound();
        if (await _db.GlAccountMappings.AnyAsync(m => m.TenantId == tid && m.AccountId == id, ct))
            return BadRequest(new { message = "Account is in use by a payroll mapping; remap it first." });
        _db.GlAccounts.Remove(acct);
        await _db.SaveChangesAsync(ct);
        return NoContent();
    }

    // ── Driver → account mappings ────────────────────────────────────────────

    /// <summary>Full driver catalog with the tenant's mapping (or the built-in default) for each.</summary>
    [HttpGet("mappings")]
    public async Task<IActionResult> ListMappings(CancellationToken ct)
    {
        var tid = this.GetTenantId(); if (tid is null) return Unauthorized();
        var maps = await _db.GlAccountMappings.AsNoTracking().Where(m => m.TenantId == tid).ToListAsync(ct);
        var byDriver = maps.ToDictionary(m => m.DriverKey, m => m.AccountId);
        var result = PayrollGlCatalog.Drivers.Select(d => new
        {
            driverKey = d.Key,
            label = d.Label,
            accountType = d.AccountType,
            defaultAccount = $"{d.DefaultCode} - {d.DefaultName}",
            mappedAccountId = byDriver.TryGetValue(d.Key, out var aid) ? aid : (Guid?)null,
        });
        return Ok(result);
    }

    /// <summary>Replace the tenant's driver→account mappings wholesale.</summary>
    [HttpPut("mappings")]
    public async Task<IActionResult> SetMappings([FromBody] List<GlMappingRequest> mappings, CancellationToken ct)
    {
        var tid = this.GetTenantId(); if (tid is null) return Unauthorized();
        var validDrivers = PayrollGlCatalog.Drivers.Select(d => d.Key).ToHashSet();
        var accountIds = await _db.GlAccounts.Where(a => a.TenantId == tid).Select(a => a.Id).ToListAsync(ct);
        var accountSet = accountIds.ToHashSet();

        foreach (var m in mappings)
        {
            if (!validDrivers.Contains(m.DriverKey)) return BadRequest(new { message = $"Unknown driver '{m.DriverKey}'." });
            if (!accountSet.Contains(m.AccountId)) return BadRequest(new { message = $"Account '{m.AccountId}' not found in this tenant's chart." });
        }

        var existing = await _db.GlAccountMappings.Where(m => m.TenantId == tid).ToListAsync(ct);
        _db.GlAccountMappings.RemoveRange(existing);
        foreach (var m in mappings)
            _db.GlAccountMappings.Add(new GlAccountMapping { TenantId = tid.Value, DriverKey = m.DriverKey, AccountId = m.AccountId, IsActive = true });
        await _db.SaveChangesAsync(ct);
        return Ok(new { count = mappings.Count });
    }

    /// <summary>Seed the built-in default chart of accounts + mappings for tenants starting fresh.</summary>
    [HttpPost("seed-defaults")]
    public async Task<IActionResult> SeedDefaults(CancellationToken ct)
    {
        var tid = this.GetTenantId(); if (tid is null) return Unauthorized();
        var existingCodes = (await _db.GlAccounts.Where(a => a.TenantId == tid).Select(a => a.Code).ToListAsync(ct)).ToHashSet();
        var existingDrivers = (await _db.GlAccountMappings.Where(m => m.TenantId == tid).Select(m => m.DriverKey).ToListAsync(ct)).ToHashSet();

        var codeToId = new Dictionary<string, Guid>();
        foreach (var d in PayrollGlCatalog.Drivers)
        {
            if (!codeToId.ContainsKey(d.DefaultCode) && !existingCodes.Contains(d.DefaultCode))
            {
                var acct = new GlAccount { TenantId = tid.Value, Code = d.DefaultCode, Name = d.DefaultName, AccountType = d.AccountType };
                _db.GlAccounts.Add(acct);
                codeToId[d.DefaultCode] = acct.Id;
            }
        }
        await _db.SaveChangesAsync(ct);

        // Resolve final account ids (newly added + pre-existing) for mapping.
        var allAccounts = await _db.GlAccounts.Where(a => a.TenantId == tid).ToDictionaryAsync(a => a.Code, a => a.Id, ct);
        int created = 0;
        foreach (var d in PayrollGlCatalog.Drivers)
        {
            if (existingDrivers.Contains(d.Key)) continue;
            if (!allAccounts.TryGetValue(d.DefaultCode, out var aid)) continue;
            _db.GlAccountMappings.Add(new GlAccountMapping { TenantId = tid.Value, DriverKey = d.Key, AccountId = aid });
            created++;
        }
        await _db.SaveChangesAsync(ct);
        return Ok(new { accounts = codeToId.Count, mappings = created });
    }

    private static object ToDto(GlAccount a) => new { a.Id, a.Code, a.Name, a.AccountType, a.IsActive };
}

public record GlAccountRequest(string Code, string Name, string AccountType = "Expense", bool IsActive = true);
public record GlMappingRequest(string DriverKey, Guid AccountId);
