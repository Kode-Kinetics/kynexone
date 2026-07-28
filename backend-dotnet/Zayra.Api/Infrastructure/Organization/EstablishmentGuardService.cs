using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Zayra.Api.Application.Auth;
using Zayra.Api.Application.Organization;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Audit;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Organization;

/// <summary>
/// Implementation of the establishment matrix guard. Design invariants:
///
///  1. ABSOLUTE COUNTS — every query here runs with IgnoreQueryFilters() + explicit TenantId /
///     !IsDeleted predicates. Employee carries ICompanyScopedOperational, so the ambient global
///     filter narrows by the CALLER'S company scope; a company-scoped HR user's guard would
///     otherwise under-count occupancy and admit over-budget hires. Guard counts are absolute and
///     caller-independent (panel ↔ popup parity holds exactly for group-scope viewers).
///  2. FAIL-OPEN, LOUDLY — no budget row, no department id, no designation, unmapped designation,
///     or mode Off ⇒ allowed (the matrix endpoint surfaces unclassified/unresolved counts).
///  3. BLOCK AUDITS SURVIVE ROLLBACK — establishment.assignment_blocked is written through a FRESH
///     DbContext from IServiceScopeFactory (its own connection ⇒ not enlisted in the caller's
///     transaction, which rolls back on the block path). Without a scope factory (direct
///     construction in the InMemory test suite, where there is no rollback) it writes through the
///     ambient context.
///  4. RETRY-SAFE TRANSACTIONS — Program.cs enables EnableRetryOnFailure; a bare BeginTransaction
///     throws under that strategy, so EnforceAndExecuteAsync wraps transaction + lock + enforce +
///     body in Database.CreateExecutionStrategy().ExecuteAsync(...).
///  5. Audit metadata is DENORMALIZED (department code/name, level code/EN/AR names) so the trail
///     stays readable over the retention horizon even after renames/deletes.
/// </summary>
public sealed class EstablishmentGuardService : IEstablishmentGuard
{
    public const string ModeOff = "Off";
    public const string ModeAdvisory = "Advisory";
    public const string ModeEnforced = "Enforced";

    private readonly ZayraDbContext _db;
    private readonly IServiceScopeFactory? _scopeFactory;

    public EstablishmentGuardService(ZayraDbContext db, IServiceScopeFactory? scopeFactory = null)
    {
        _db = db;
        _scopeFactory = scopeFactory;
    }

    public async Task<string> GetEnforcementModeAsync(Guid tenantId, CancellationToken ct = default)
    {
        // IgnoreQueryFilters is intentional: guard counts must be absolute — the ambient company-scope filter would under-count occupancy for scoped callers; explicit TenantId (+ !IsDeleted) predicates are applied inline.
        var mode = await _db.TenantHrConfigs.IgnoreQueryFilters().AsNoTracking()
            .Where(x => x.TenantId == tenantId)
            .Select(x => x.EstablishmentEnforcementMode)
            .FirstOrDefaultAsync(ct);
        return Normalize(mode);
    }

    public static string Normalize(string? mode) => mode switch
    {
        ModeOff => ModeOff,
        ModeAdvisory => ModeAdvisory,
        // Absent row / null / "" / unknown value ⇒ documented default.
        _ => ModeEnforced
    };

    public async Task<Guid?> ResolveLevelIdAsync(Guid tenantId, Guid? designationId, CancellationToken ct = default)
    {
        if (designationId is null) return null;
        // IgnoreQueryFilters is intentional: guard counts must be absolute — the ambient company-scope filter would under-count occupancy for scoped callers; explicit TenantId (+ !IsDeleted) predicates are applied inline.
        return await _db.Designations.IgnoreQueryFilters().AsNoTracking()
            .Where(d => d.TenantId == tenantId && d.Id == designationId && !d.IsDeleted)
            .Select(d => d.StaffingLevelId)
            .FirstOrDefaultAsync(ct);
    }

    public async Task<EstablishmentCheck> CheckAsync(Guid tenantId, Guid? departmentId, Guid? designationId,
        int? excludeEmployeeId, int attempted = 1, CancellationToken ct = default)
    {
        var mode = await GetEnforcementModeAsync(tenantId, ct);
        if (mode == ModeOff) return EstablishmentCheck.Pass;

        // No designation, or designation without a level mapping ⇒ Unclassified: never counted,
        // never blocked (fail-open, surfaced by the matrix read).
        if (designationId is null) return EstablishmentCheck.PassUnclassified;
        var levelId = await ResolveLevelIdAsync(tenantId, designationId, ct);
        if (levelId is null) return EstablishmentCheck.PassUnclassified;

        // String-only department (legacy) ⇒ never blocks; surfaced via the reconciliation count.
        if (departmentId is null) return EstablishmentCheck.Pass;

        // IgnoreQueryFilters is intentional: guard counts must be absolute — the ambient company-scope filter would under-count occupancy for scoped callers; explicit TenantId (+ !IsDeleted) predicates are applied inline.
        var budget = await _db.DepartmentStaffingBudgets.IgnoreQueryFilters().AsNoTracking()
            .Where(b => b.TenantId == tenantId && b.DepartmentId == departmentId
                     && b.StaffingLevelId == levelId && !b.IsDeleted)
            .Select(b => (int?)b.BudgetedHeadcount)
            .FirstOrDefaultAsync(ct);
        // Row ABSENT = uncontrolled (exactly pre-matrix behaviour). Explicit 0 = frozen.
        if (budget is null) return EstablishmentCheck.Pass;

        var department = await _db.Departments.IgnoreQueryFilters().AsNoTracking()
            .Where(d => d.TenantId == tenantId && d.Id == departmentId && !d.IsDeleted)
            .Select(d => new { d.Id, d.NameEn })
            .FirstOrDefaultAsync(ct);
        if (department is null) return EstablishmentCheck.Pass;

        // IgnoreQueryFilters is intentional: guard counts must be absolute — the ambient company-scope filter would under-count occupancy for scoped callers; explicit TenantId (+ !IsDeleted) predicates are applied inline.
        var levelDesignationIds = await _db.Designations.IgnoreQueryFilters().AsNoTracking()
            .Where(d => d.TenantId == tenantId && d.StaffingLevelId == levelId && !d.IsDeleted)
            .Select(d => d.Id)
            .ToListAsync(ct);

        // Shared occupancy predicate (Conflict R3) over an unfiltered source (absolute counts).
        var occupants = await EstablishmentOccupancy
            .Occupying(_db.Employees.IgnoreQueryFilters().AsNoTracking(), tenantId)
            .Where(e => e.DesignationId != null && levelDesignationIds.Contains(e.DesignationId!.Value))
            .Where(e => e.DepartmentId == department.Id
                     || (e.DepartmentId == null && e.Department == department.NameEn))
            .Where(e => excludeEmployeeId == null || e.Id != excludeEmployeeId)
            .Select(e => new { e.Id, e.Status })
            .ToListAsync(ct);

        var current = occupants.Count;
        if (current + attempted <= budget.Value) return EstablishmentCheck.Pass;

        // IgnoreQueryFilters is intentional: guard counts must be absolute — the ambient company-scope filter would under-count occupancy for scoped callers; explicit TenantId (+ !IsDeleted) predicates are applied inline.
        var level = await _db.StaffingLevels.IgnoreQueryFilters().AsNoTracking()
            .Where(l => l.TenantId == tenantId && l.Id == levelId)
            .Select(l => new { l.Id, l.Code, l.NameEn, l.NameAr })
            .FirstAsync(ct);

        var block = new EstablishmentBlock(
            department.Id, department.NameEn,
            level.Id, level.Code, level.NameEn, level.NameAr,
            budget.Value, current, attempted,
            ExitingIncumbents: occupants.Count(o => o.Status == EmployeeStatuses.Offboarded));

        return new EstablishmentCheck(false, Advisory: mode == ModeAdvisory, block, Unclassified: false);
    }

    public async Task<EstablishmentCheck> EnforceAsync(Guid tenantId, Guid? departmentId, Guid? designationId,
        int? excludeEmployeeId, string path, RequestContext context, CancellationToken ct = default)
    {
        var check = await CheckAsync(tenantId, departmentId, designationId, excludeEmployeeId, 1, ct);
        if (check.Allowed) return check;

        await WriteBlockAuditAsync(tenantId, check.Block!, path, advisory: check.Advisory, excludeEmployeeId, context, ct);

        if (!check.Advisory)
            throw new EstablishmentBudgetExceededException(check.Block!);
        return check;
    }

    public async Task<T> EnforceAndExecuteAsync<T>(Guid tenantId, Guid? departmentId, Guid? designationId,
        int? excludeEmployeeId, string path, RequestContext context, Func<Task<T>> body, CancellationToken ct = default)
    {
        // Fast path: nothing lockable applies (no dept / unmapped designation / mode Off).
        Guid? levelId = null;
        var lockable = departmentId is not null
                       && (levelId = await ResolveLevelIdAsync(tenantId, designationId, ct)) is not null
                       && await GetEnforcementModeAsync(tenantId, ct) != ModeOff;

        if (!_db.Database.IsRelational())
        {
            // EF InMemory: no transactions, no advisory locks — enforce then run.
            await EnforceAsync(tenantId, departmentId, designationId, excludeEmployeeId, path, context, ct);
            return await body();
        }

        if (!lockable)
        {
            await EnforceAsync(tenantId, departmentId, designationId, excludeEmployeeId, path, context, ct);
            return await body();
        }

        if (_db.Database.CurrentTransaction is not null)
        {
            // Ambient transaction (e.g. a caller composing several guarded writes): join it.
            await AcquireSlotLockAsync(tenantId, departmentId!.Value, levelId!.Value, ct);
            await EnforceAsync(tenantId, departmentId, designationId, excludeEmployeeId, path, context, ct);
            return await body();
        }

        // EnableRetryOnFailure demands the whole unit (tx open → commit) live inside the
        // execution strategy delegate; the delegate must be safe to re-run from scratch.
        var strategy = _db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            await using var tx = await _db.Database.BeginTransactionAsync(ct);
            await AcquireSlotLockAsync(tenantId, departmentId!.Value, levelId!.Value, ct);
            await EnforceAsync(tenantId, departmentId, designationId, excludeEmployeeId, path, context, ct);
            var result = await body();
            await tx.CommitAsync(ct);
            return result;
        });
    }

    public async Task AcquireSlotLockAsync(Guid tenantId, Guid departmentId, Guid staffingLevelId, CancellationToken ct = default)
    {
        if (!_db.Database.IsRelational()) return;
        var key = ComputeLockKey(tenantId, departmentId, staffingLevelId);
        // Transaction-scoped advisory lock: serializes concurrent writers on the same matrix cell;
        // auto-released at commit/rollback. (Interpolated value binds as a parameter.)
        await _db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock({key})", ct);
    }

    /// <summary>Stable 64-bit key: first 8 bytes (big-endian) of SHA256(tenant ‖ department ‖ level).
    /// Public so multi-cell callers (CSV import) can sort keys before acquiring — deadlock-free.</summary>
    public static long ComputeLockKey(Guid tenantId, Guid departmentId, Guid staffingLevelId)
    {
        Span<byte> buffer = stackalloc byte[48];
        tenantId.TryWriteBytes(buffer[..16]);
        departmentId.TryWriteBytes(buffer.Slice(16, 16));
        staffingLevelId.TryWriteBytes(buffer.Slice(32, 16));
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(buffer, hash);
        return System.Buffers.Binary.BinaryPrimitives.ReadInt64BigEndian(hash[..8]);
    }

    /// <summary>
    /// Writes establishment.assignment_blocked so every guarded path logs identically. Uses a
    /// fresh scoped DbContext when available so the record survives the caller's rollback
    /// (Enforced-mode blocks roll their business transaction back by definition).
    /// Metadata is denormalized and includes the blocked employee's GOSI classification when the
    /// employee is known (Saudization telemetry — metadata only, never used in enforcement).
    /// </summary>
    private async Task WriteBlockAuditAsync(Guid tenantId, EstablishmentBlock block, string path,
        bool advisory, int? employeeId, RequestContext context, CancellationToken ct)
    {
        string? gosiClassification = null;
        if (employeeId is not null)
        {
            // IgnoreQueryFilters is intentional: guard counts must be absolute — the ambient company-scope filter would under-count occupancy for scoped callers; explicit TenantId (+ !IsDeleted) predicates are applied inline.
            var nationality = await _db.Employees.IgnoreQueryFilters().AsNoTracking()
                .Where(e => e.TenantId == tenantId && e.Id == employeeId)
                .Select(e => e.Nationality)
                .FirstOrDefaultAsync(ct);
            gosiClassification = Payroll.GosiCalculationService.DeriveClassification(nationality);
        }

        var metadata = JsonSerializer.Serialize(new
        {
            path,
            advisory,
            employeeId,
            departmentId = block.DepartmentId,
            departmentName = block.DepartmentName,
            staffingLevelId = block.StaffingLevelId,
            levelCode = block.LevelCode,
            levelNameEn = block.LevelNameEn,
            levelNameAr = block.LevelNameAr,
            budgeted = block.Budgeted,
            current = block.Current,
            attempted = block.Attempted,
            exitingIncumbents = block.ExitingIncumbents,
            gosiClassification
        });

        var auditContext = context with { TenantId = context.TenantId ?? tenantId };
        try
        {
            // ALWAYS a fresh context — two reasons: (1) a fresh connection is not enlisted in the
            // caller's transaction, so the block record survives the rollback that Enforced-mode
            // blocks cause by definition; (2) AuditService.WriteAsync calls SaveChangesAsync, and
            // doing that on the AMBIENT context would flush the caller's half-applied pending
            // changes (the exact state the block is preventing).
            if (_scopeFactory is not null)
            {
                using var scope = _scopeFactory.CreateScope();
                var freshDb = scope.ServiceProvider.GetRequiredService<ZayraDbContext>();
                await new AuditService(freshDb).WriteAsync("establishment.assignment_blocked", "Department",
                    block.DepartmentId.ToString(), auditContext, metadata, ct);
            }
            else
            {
                // Direct construction (test harnesses): rebuild a context on the same store from
                // the ambient options. InMemory shares its store by database name, so the audit
                // row is visible to the test's context; relational gets its own connection.
                var options = (DbContextOptions<ZayraDbContext>)_db.GetService<IDbContextOptions>();
                await using var freshDb = new ZayraDbContext(options);
                await new AuditService(freshDb).WriteAsync("establishment.assignment_blocked", "Department",
                    block.DepartmentId.ToString(), auditContext, metadata, ct);
            }
        }
        catch
        {
            // The audit stream must never turn a legible 409 into a 500; the block itself is
            // still returned to the caller.
        }
    }
}
