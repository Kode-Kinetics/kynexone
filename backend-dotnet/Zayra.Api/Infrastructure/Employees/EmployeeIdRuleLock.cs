using Microsoft.EntityFrameworkCore;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Jobs;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Employees;

/// <summary>
/// ONE way to take the tenant's employee-ID sequence, for every path that dispenses a generated code: the CSV
/// import's dispenser, the Add Employee form (EmployeeManagementService) and draft approval.
///
/// <para>WHY. The import locked the rule row FOR UPDATE for its whole transaction; the form and draft approval read
/// it with a plain SELECT. A hire saved while an import was running read the sequence from before the import,
/// handed out a code the import had already given, and wrote the sequence BACK — a duplicate code refused at the
/// unique index, and a sequence that later dispensed taken codes. Every path now locks the SAME rows the SAME way
/// (every active rule of the tenant, in id order, so two paths can never deadlock on each other) inside its own
/// transaction, re-reads them under the lock, and skips any code already in use.</para>
/// </summary>
public static class EmployeeIdRuleLock
{
    /// <summary>Lock every active ID rule of the tenant FOR UPDATE, in id order, and return them as read under the lock.
    /// Must run inside the caller's transaction on a relational provider (outside one the lock ends with the statement).</summary>
    public static async Task<List<EmployeeIdRule>> LockAsync(ZayraDbContext db, Guid tenantId, CancellationToken ct)
    {
        var rules = await db.EmployeeIdRules
            .TagWith(RowLockingInterceptor.ForUpdateTag)
            .Where(x => x.TenantId == tenantId && x.IsActive && !x.IsDeleted)
            .OrderBy(x => x.Id)
            .ToListAsync(ct);
        // A rule this context already tracked (an earlier attempt, an earlier read) keeps its stale values through a
        // tracking query; re-read each one so the sequence is the one the lock protects.
        foreach (var rule in rules)
            await db.Entry(rule).ReloadAsync(ct);
        return rules;
    }

    /// <summary>True when <paramref name="code"/> is held by any employee of the tenant — every company, deleted rows
    /// included, exactly the span of the unique (TenantId, EmployeeCode) index.</summary>
    public static Task<bool> CodeTakenAsync(ZayraDbContext db, Guid tenantId, string code, CancellationToken ct) =>
        Zayra.Api.Infrastructure.Data.ScopedBypass
            .NullableTenantWide(db.Employees, tenantId,
                "Employee-code uniqueness: the unique (TenantId, EmployeeCode) index spans every company and soft-deleted rows, so a generated code must be free across all of them. Tenant re-applied; only the code is compared.")
            .AnyAsync(e => e.EmployeeCode == code, ct);
}
