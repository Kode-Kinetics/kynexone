using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Zayra.Api.Controllers;
using Zayra.Api.Data;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>Shared fixture helpers for the WP-B cost-accounting tests (GL rule, mappings, switch, readiness, SoD, ledger guard).</summary>
internal static class CostAccountingKit
{
    public static readonly string[] ManagePerms = { "finance.gl.read", "finance.gl.manage" };
    public static readonly string[] ReadPerms = { "finance.gl.read" };

    public static (ZayraDbContext db, SqliteConnection conn) NewDb(params IInterceptor[] interceptors)
    {
        var conn = new SqliteConnection("DataSource=:memory:");
        conn.Open();
        var b = new DbContextOptionsBuilder<ZayraDbContext>().UseSqlite(conn);
        if (interceptors.Length > 0) b.AddInterceptors(interceptors);
        var db = new ZayraDbContext(b.Options);
        db.Database.EnsureCreated();
        return (db, conn);
    }

    public static T Attach<T>(T c, Guid tenantId, IEnumerable<string> perms, string? entityScopeJson = null, Guid? userId = null) where T : ControllerBase
    {
        var claims = new List<Claim>
        {
            new("tenant_id", tenantId.ToString()),
            new(ClaimTypes.NameIdentifier, (userId ?? Guid.NewGuid()).ToString()),
        };
        claims.AddRange(perms.Select(p => new Claim("permission", p)));
        if (entityScopeJson is not null) claims.Add(new Claim("entity_scope", entityScopeJson));
        c.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "test")) },
        };
        return c;
    }

    public static string CompanyScope(params Guid[] companies) =>
        JsonSerializer.Serialize(new { v = 2, m = "companies", c = companies });

    public static FinanceGlController Gl(ZayraDbContext db, Guid tid, IEnumerable<string> perms, string? scope = null, Guid? uid = null)
        => Attach(new FinanceGlController(db), tid, perms, scope, uid);

    public static async Task<Company> AddCompany(ZayraDbContext db, Guid tid, bool enabled = false, string currency = "SAR", Action<Company>? cfg = null)
    {
        var c = new Company { TenantId = tid, LegalNameEn = "Co " + Guid.NewGuid().ToString("N")[..4], DefaultCurrency = currency, CostAccountingEnabled = enabled };
        cfg?.Invoke(c);
        db.Companies.Add(c);
        await db.SaveChangesAsync();
        return c;
    }

    public static async Task<GlAccount> AddAccount(ZayraDbContext db, Guid tid, Guid? companyId, string code, string type, string rule = "Optional", bool active = true)
    {
        var a = new GlAccount { TenantId = tid, CompanyId = companyId, Code = code, Name = "Acct " + code, AccountType = type, CostCenterRule = rule, IsActive = active };
        db.GlAccounts.Add(a);
        await db.SaveChangesAsync();
        return a;
    }

    public static async Task<CostCenter> AddCostCentre(ZayraDbContext db, Guid tid, Guid? companyId, string code, bool active = true, bool deleted = false, string? cls = null)
    {
        var cc = new CostCenter { TenantId = tid, CompanyId = companyId, Code = code, Name = "CC " + code, IsActive = active, IsDeleted = deleted, FunctionalClass = cls };
        db.CostCenters.Add(cc);
        await db.SaveChangesAsync();
        return cc;
    }

    public static async Task<PayrollRun> AddRun(ZayraDbContext db, Guid tid, Guid? companyId, string status, int year = 2026, int month = 9, DateTime? lockedAt = null, bool applied = false)
    {
        var r = new PayrollRun { TenantId = tid, CompanyId = companyId, Status = status, Year = year, Month = month, LockedAtUtc = lockedAt, CostAccountingApplied = applied };
        db.PayrollRuns.Add(r);
        await db.SaveChangesAsync();
        return r;
    }

    public static string Label(GlAccount a) => $"{a.Code} - {a.Name}";

    /// <summary>HTTP status of a controller result (200 for a bare Ok, the status code otherwise).</summary>
    public static int Status(IActionResult r) => r switch
    {
        ObjectResult o => o.StatusCode ?? 200,
        StatusCodeResult s => s.StatusCode,
        _ => 200,
    };

    public static JsonElement Body(IActionResult r)
    {
        var value = (r as ObjectResult)?.Value;
        var json = JsonSerializer.Serialize(value);
        return JsonDocument.Parse(json).RootElement.Clone();
    }

    /// <summary>Case-insensitive property lookup (anonymous response types keep their declared casing).</summary>
    public static JsonElement P(this JsonElement e, string name)
    {
        foreach (var p in e.EnumerateObject())
            if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)) return p.Value;
        throw new KeyNotFoundException($"No property '{name}' in {e}");
    }

    public static bool Has(this JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.EnumerateObject().Any(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));

    public static string ErrorCode(IActionResult r)
    {
        var b = Body(r);
        if (b.Has("error")) return b.P("error").GetString() ?? "";
        if (b.Has("code")) return b.P("code").GetString() ?? "";
        return "";
    }

    /// <summary>Fails the SaveChanges that carries an audit row, so the tests can prove audit and change commit together.</summary>
    public sealed class FailOnAuditRow : SaveChangesInterceptor
    {
        public bool Armed { get; set; }
        public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
        {
            Check(eventData.Context);
            return result;
        }
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken ct = default)
        {
            Check(eventData.Context);
            return new ValueTask<InterceptionResult<int>>(result);
        }
        private void Check(DbContext? ctx)
        {
            if (Armed && ctx is not null && ctx.ChangeTracker.Entries<Zayra.Api.Domain.Entities.AuditLog>().Any(e => e.State == EntityState.Added))
                throw new InvalidOperationException("simulated audit insert failure");
        }
    }
}
