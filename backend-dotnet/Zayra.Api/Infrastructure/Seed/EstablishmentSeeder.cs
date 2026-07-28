using Microsoft.EntityFrameworkCore;
using Zayra.Api.Data;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Seed;

/// <summary>
/// Seeds the default staffing-level catalog per tenant as EDITABLE DATA (project rule: staffing
/// bands are tenant-configurable data, never a compile-time enum — these strings exist ONLY here;
/// no code path anywhere references a band name literal). Invoked from
/// AuthSeeder.EnsureTenantRolesAsync (covering every provisioning path, including future ones)
/// and lazily from GET /api/establishment/levels for pre-existing tenants.
/// </summary>
public class EstablishmentSeeder
{
    private readonly ZayraDbContext _db;
    public EstablishmentSeeder(ZayraDbContext db) => _db = db;

    public async Task EnsureStaffingLevelsAsync(Guid tenantId, CancellationToken ct = default)
    {
        // "Ever seeded" check INCLUDES soft-deleted rows: a tenant that deliberately deleted its
        // levels is not re-seeded (their data, their choice).
        // IgnoreQueryFilters is intentional: the ever-seeded check must see soft-deleted rows (deliberate deletion is respected); explicit TenantId filter applied.
        if (await _db.StaffingLevels.IgnoreQueryFilters()
                .AnyAsync(x => x.TenantId == tenantId, ct)) return;

        _db.StaffingLevels.AddRange(Defaults(tenantId));
        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // Concurrent first visit: another request seeded between our check and save and the
            // unique (TenantId, Code) index rejected the duplicates. Their seed won; drop ours.
            foreach (var entry in _db.ChangeTracker.Entries<StaffingLevel>()
                         .Where(e => e.State == EntityState.Added).ToList())
                entry.State = EntityState.Detached;
        }
    }

    private static IEnumerable<StaffingLevel> Defaults(Guid tenantId) => new (string Code, string En, string Ar, int Rank)[]
    {
        ("DEPT_HEAD",    "Department Head",   "رئيس القسم", 1),
        ("MANAGER",      "Manager",           "مدير",       2),
        ("ASST_MANAGER", "Assistant Manager", "مساعد مدير", 3),
        ("SUPERVISOR",   "Supervisor",        "مشرف",       4),
        ("STAFF",        "Staff",             "موظف",       5),
    }.Select(d => new StaffingLevel
    {
        TenantId = tenantId,
        Code = d.Code,
        NameEn = d.En,
        NameAr = d.Ar,
        Rank = d.Rank,
        IsActive = true
    });
}
