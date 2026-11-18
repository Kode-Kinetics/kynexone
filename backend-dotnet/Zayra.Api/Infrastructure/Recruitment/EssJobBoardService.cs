using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Common;
using Zayra.Api.Infrastructure.Data;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Recruitment;

/// <summary>What an employee sees of an open job opening: display fields only (no pay band, HR owner, requisition, grade, headcount or company).</summary>
public sealed record EssJobItemDto(
    Guid Id, string JobCode, string Title, string DepartmentName, string DesignationTitle, string EmploymentType,
    string Location, string Description, string Requirements, string Responsibilities, DateTime? PublishedAtUtc);

public sealed record EssJobPageDto(IReadOnlyList<EssJobItemDto> Items, int Total, int Page, int PageSize);

/// <summary>The employee internal job board: the OPEN openings of the caller's OWN company. Read-only.</summary>
public interface IEssJobBoardService
{
    /// <summary>The caller's own employee id (their login link), or null when the login is not linked to an employee.</summary>
    Task<int?> ResolveCallerEmployeeAsync(ClaimsPrincipal caller, Guid tenantId, CancellationToken ct);
    Task<EssJobPageDto> ListAsync(Guid tenantId, int employeeId, string? q, int page, int pageSize, CancellationToken ct);
    Task<EssJobItemDto?> GetAsync(Guid tenantId, int employeeId, Guid openingId, CancellationToken ct);
}

public sealed class EssJobBoardService(ZayraDbContext db) : IEssJobBoardService
{
    public const int MaxPageSize = 100;
    public const string OpenStatus = "Open";

    private const string BypassReason =
        "Employee job board: the company filter follows entity-scope claims, so it is replaced by the caller employee's own company, an Open status and the tenant";

    public Task<int?> ResolveCallerEmployeeAsync(ClaimsPrincipal caller, Guid tenantId, CancellationToken ct) =>
        CallerEmployeeResolver.ResolveAsync(db, caller, tenantId, ct);

    private sealed record Own(Guid? CompanyId);

    public async Task<EssJobPageDto> ListAsync(Guid tenantId, int employeeId, string? q, int page, int pageSize, CancellationToken ct)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, MaxPageSize);
        if (await OwnAsync(tenantId, employeeId, ct) is not { } own) return new EssJobPageDto([], 0, page, pageSize);

        var query = Visible(tenantId, own.CompanyId);
        var term = q?.Trim().ToLowerInvariant();
        if (!string.IsNullOrEmpty(term))
            query = query.Where(o => o.Title.ToLower().Contains(term) || o.DepartmentName.ToLower().Contains(term)
                || o.DesignationTitle.ToLower().Contains(term) || o.Location.ToLower().Contains(term));

        var total = await query.CountAsync(ct);
        var items = await query
            .OrderByDescending(o => o.PublishedAtUtc != null)   // unpublished last on every provider (Postgres sorts NULLs first on DESC)
            .ThenByDescending(o => o.PublishedAtUtc)
            .ThenByDescending(o => o.CreatedAtUtc)
            .ThenBy(o => o.Id)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .Select(o => new EssJobItemDto(o.Id, o.JobCode, o.Title, o.DepartmentName, o.DesignationTitle, o.EmploymentType,
                o.Location, o.Description, o.Requirements, o.Responsibilities, o.PublishedAtUtc))
            .ToListAsync(ct);
        return new EssJobPageDto(items, total, page, pageSize);
    }

    public async Task<EssJobItemDto?> GetAsync(Guid tenantId, int employeeId, Guid openingId, CancellationToken ct)
    {
        if (await OwnAsync(tenantId, employeeId, ct) is not { } own) return null;
        return await Visible(tenantId, own.CompanyId)
            .Where(o => o.Id == openingId)
            .Select(o => new EssJobItemDto(o.Id, o.JobCode, o.Title, o.DepartmentName, o.DesignationTitle, o.EmploymentType,
                o.Location, o.Description, o.Requirements, o.Responsibilities, o.PublishedAtUtc))
            .FirstOrDefaultAsync(ct);
    }

    /// <summary>
    /// IgnoreQueryFilters is intentional (through the sanctioned ScopedBypass helpers, which re-apply the tenant). The global operational company filter follows the caller's entity-scope claims: a
    /// self-service employee normally has no entity-access grant (scope "none", which hides EVERY company-stamped row, their
    /// own company's included), while a group-scope user's filter would show every company and obeys the X-Company-Id
    /// switcher. Neither describes "my company's open jobs". The board is bounded instead by explicit predicates: this
    /// tenant, status Open, and exactly the caller employee's own company (null matches null only). JobOpening has no
    /// soft-delete flag, so there is none to repeat here.
    /// </summary>
    private IQueryable<JobOpening> Visible(Guid tenantId, Guid? companyId) =>
        ScopedBypass.TenantWide(db.JobOpenings, tenantId, BypassReason).AsNoTracking()
            .Where(o => o.TenantId == tenantId
                && o.Status == OpenStatus
                && (companyId == null ? o.CompanyId == null : o.CompanyId == companyId));

    /// <summary>The caller's employee row, same tenant and not deleted (filters ignored for the same reason as above); null when absent.</summary>
    private Task<Own?> OwnAsync(Guid tenantId, int employeeId, CancellationToken ct) =>
        ScopedBypass.NullableTenantWide(db.Employees, tenantId, BypassReason).AsNoTracking()
            .Where(e => e.Id == employeeId && e.TenantId == tenantId && !e.IsDeleted)
            .Select(e => new Own(e.CompanyId))
            .FirstOrDefaultAsync(ct);
}
