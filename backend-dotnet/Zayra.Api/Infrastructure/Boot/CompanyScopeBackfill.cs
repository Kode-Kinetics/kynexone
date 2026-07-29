using Microsoft.EntityFrameworkCore;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Boot;

/// <summary>
/// Phase 1B default-company backfill. Idempotent — every step only touches rows that are
/// still missing their company assignment, so it is safe to run on every boot (guarded by
/// config "CompanyScope:Backfill", default enabled; it no-ops once data is clean).
///
/// Per tenant:
///   1. Ensure one default Company exists (first active company by creation date; if the
///      tenant has none, create one named after the tenant). No Branch records are created.
///   2. Employees: null CompanyId → default company (employees must resolve to a company).
///   3. Employee-linked operational tables: null CompanyId → the OWNING EMPLOYEE's company
///      (correct for multi-company tenants), via int Employee.Id or EmployeeIntId linkage.
///   4. Remaining null operational rows (no resolvable employee link) → default company.
///   5. AccountType promotion: tenants already operating >1 active company → Group.
///
/// ROLLBACK: all touched columns are nullable and newly added this phase — reverting the
/// migration drops the columns (and this assignment data) without touching any
/// pre-existing column. No rows are deleted, renamed, or overwritten (non-null values are
/// never modified).
/// </summary>
public static class CompanyScopeBackfill
{
    public static async Task<BackfillSummary> RunAsync(ZayraDbContext db, ILogger logger, CancellationToken ct = default)
    {
        var summary = new BackfillSummary();
        var tenants = await db.Tenants.AsNoTracking().Where(t => t.IsActive).Select(t => new { t.Id, t.Name, t.AccountType }).ToListAsync(ct);

        foreach (var tenant in tenants)
        {
            // 1. Default company — oldest active, else create one.
            var defaultCompany = await db.Companies
                .Where(c => c.TenantId == tenant.Id && !c.IsDeleted && c.IsActive)
                .OrderBy(c => c.CreatedAtUtc)
                .FirstOrDefaultAsync(ct);
            if (defaultCompany is null)
            {
                defaultCompany = new Company
                {
                    TenantId = tenant.Id,
                    LegalNameEn = tenant.Name,
                    TradeName = tenant.Name,
                    IsActive = true,
                };
                db.Companies.Add(defaultCompany);
                await db.SaveChangesAsync(ct);
                summary.CompaniesCreated++;
                logger.LogInformation("CompanyScopeBackfill: created default company for tenant {TenantId} ({Name})", tenant.Id, tenant.Name);
            }
            var defaultId = defaultCompany.Id;

            // 2. Employees resolve to the default company. IgnoreQueryFilters is intentional:
            // this is a SYSTEM repair pass with no HttpContext; predicates below re-scope
            // every statement to this tenant only.
            summary.EmployeesAssigned += await SystemScope(db.Employees)
                .Where(e => e.TenantId == tenant.Id && e.CompanyId == null)
                .ExecuteUpdateAsync(s => s.SetProperty(e => e.CompanyId, defaultId), ct);

            // 3. Employee-accurate pass (matters for multi-company tenants): rows follow
            //    their owning employee's company, not the tenant default.
            foreach (var company in await SystemScope(db.Companies)
                         .Where(c => c.TenantId == tenant.Id && !c.IsDeleted).Select(c => c.Id).ToListAsync(ct))
            {
                var empIds = await SystemScope(db.Employees)
                    .Where(e => e.TenantId == tenant.Id && e.CompanyId == company)
                    .Select(e => e.Id).ToListAsync(ct);
                if (empIds.Count == 0) continue;

                summary.RowsAssigned += await SystemScope(db.AttendanceRecords)
                    .Where(x => x.TenantId == tenant.Id && x.CompanyId == null && empIds.Contains(x.EmployeeId))
                    .ExecuteUpdateAsync(s => s.SetProperty(x => x.CompanyId, company), ct);
                summary.RowsAssigned += await SystemScope(db.AttendanceRegularizationRequests)
                    .Where(x => x.TenantId == tenant.Id && x.CompanyId == null && empIds.Contains(x.EmployeeId))
                    .ExecuteUpdateAsync(s => s.SetProperty(x => x.CompanyId, company), ct);
                summary.RowsAssigned += await SystemScope(db.LeaveRequests)
                    .Where(x => x.TenantId == tenant.Id && x.CompanyId == null && empIds.Contains(x.EmployeeId))
                    .ExecuteUpdateAsync(s => s.SetProperty(x => x.CompanyId, company), ct);
                summary.RowsAssigned += await SystemScope(db.LeaveBalanceTransactions)
                    .Where(x => x.TenantId == tenant.Id && x.CompanyId == null && empIds.Contains(x.EmployeeId))
                    .ExecuteUpdateAsync(s => s.SetProperty(x => x.CompanyId, company), ct);
                summary.RowsAssigned += await SystemScope(db.OvertimeRequests)
                    .Where(x => x.TenantId == tenant.Id && x.CompanyId == null && empIds.Contains(x.EmployeeId))
                    .ExecuteUpdateAsync(s => s.SetProperty(x => x.CompanyId, company), ct);
                summary.RowsAssigned += await SystemScope(db.ShiftAssignments)
                    .Where(x => x.TenantId == tenant.Id && x.CompanyId == null && empIds.Contains(x.EmployeeId))
                    .ExecuteUpdateAsync(s => s.SetProperty(x => x.CompanyId, company), ct);
                summary.RowsAssigned += await SystemScope(db.Payslips)
                    .Where(x => x.TenantId == tenant.Id && x.CompanyId == null && empIds.Contains(x.EmployeeId))
                    .ExecuteUpdateAsync(s => s.SetProperty(x => x.CompanyId, company), ct);
                summary.RowsAssigned += await SystemScope(db.PayrollSlips)
                    .Where(x => x.TenantId == tenant.Id && x.CompanyId == null && empIds.Contains(x.EmployeeId))
                    .ExecuteUpdateAsync(s => s.SetProperty(x => x.CompanyId, company), ct);
                summary.RowsAssigned += await SystemScope(db.PayrollDeductions)
                    .Where(x => x.TenantId == tenant.Id && x.CompanyId == null && empIds.Contains(x.EmployeeId))
                    .ExecuteUpdateAsync(s => s.SetProperty(x => x.CompanyId, company), ct);
                summary.RowsAssigned += await SystemScope(db.EmployeeDocuments)
                    .Where(x => x.TenantId == tenant.Id && x.CompanyId == null && x.EmployeeId != null && empIds.Contains(x.EmployeeId.Value))
                    .ExecuteUpdateAsync(s => s.SetProperty(x => x.CompanyId, company), ct);
                // Guid-keyed finance tables link via EmployeeIntId.
                summary.RowsAssigned += await SystemScope(db.EmployeeLoans)
                    .Where(x => x.TenantId == tenant.Id && x.CompanyId == null && x.EmployeeIntId != null && empIds.Contains(x.EmployeeIntId.Value))
                    .ExecuteUpdateAsync(s => s.SetProperty(x => x.CompanyId, company), ct);
                summary.RowsAssigned += await SystemScope(db.SalaryAdvances)
                    .Where(x => x.TenantId == tenant.Id && x.CompanyId == null && x.EmployeeIntId != null && empIds.Contains(x.EmployeeIntId.Value))
                    .ExecuteUpdateAsync(s => s.SetProperty(x => x.CompanyId, company), ct);
                summary.RowsAssigned += await SystemScope(db.EmployeeBonuses)
                    .Where(x => x.TenantId == tenant.Id && x.CompanyId == null && x.EmployeeIntId != null && empIds.Contains(x.EmployeeIntId.Value))
                    .ExecuteUpdateAsync(s => s.SetProperty(x => x.CompanyId, company), ct);
            }

            // 4. Final sweep: anything still unassigned (no resolvable employee link — visa/
            //    passport/permit registers keyed by legacy Guid, contracts, WPS batches, and
            //    stragglers from step 3) goes to the tenant default company.
            summary.RowsDefaulted += await SweepToDefaultAsync(db, tenant.Id, defaultId, ct);

            // 5. AccountType promotion: tenants already running multiple active companies
            //    clearly operate as a group; single-company tenants keep the default.
            if (tenant.AccountType != TenantAccountTypes.Group)
            {
                var activeCompanies = await SystemScope(db.Companies)
                    .CountAsync(c => c.TenantId == tenant.Id && !c.IsDeleted && c.IsActive, ct);
                if (activeCompanies > 1)
                {
                    await db.Tenants.Where(t => t.Id == tenant.Id)
                        .ExecuteUpdateAsync(s => s.SetProperty(t => t.AccountType, TenantAccountTypes.Group), ct);
                    summary.TenantsPromotedToGroup++;
                }
            }
        }

        logger.LogInformation(
            "CompanyScopeBackfill complete: {Companies} default companies created, {Employees} employees assigned, " +
            "{Rows} rows employee-matched, {Defaulted} rows defaulted, {Promoted} tenants promoted to Group",
            summary.CompaniesCreated, summary.EmployeesAssigned, summary.RowsAssigned, summary.RowsDefaulted, summary.TenantsPromotedToGroup);
        return summary;
    }

    private static async Task<int> SweepToDefaultAsync(ZayraDbContext db, Guid tenantId, Guid defaultCompanyId, CancellationToken ct)
    {
        var n = 0;
        n += await SystemScope(db.AttendanceRecords).Where(x => x.TenantId == tenantId && x.CompanyId == null).ExecuteUpdateAsync(s => s.SetProperty(x => x.CompanyId, defaultCompanyId), ct);
        n += await SystemScope(db.AttendanceRegularizationRequests).Where(x => x.TenantId == tenantId && x.CompanyId == null).ExecuteUpdateAsync(s => s.SetProperty(x => x.CompanyId, defaultCompanyId), ct);
        n += await SystemScope(db.LeaveRequests).Where(x => x.TenantId == tenantId && x.CompanyId == null).ExecuteUpdateAsync(s => s.SetProperty(x => x.CompanyId, defaultCompanyId), ct);
        n += await SystemScope(db.LeaveBalanceTransactions).Where(x => x.TenantId == tenantId && x.CompanyId == null).ExecuteUpdateAsync(s => s.SetProperty(x => x.CompanyId, defaultCompanyId), ct);
        n += await SystemScope(db.OvertimeRequests).Where(x => x.TenantId == tenantId && x.CompanyId == null).ExecuteUpdateAsync(s => s.SetProperty(x => x.CompanyId, defaultCompanyId), ct);
        n += await SystemScope(db.ShiftAssignments).Where(x => x.TenantId == tenantId && x.CompanyId == null).ExecuteUpdateAsync(s => s.SetProperty(x => x.CompanyId, defaultCompanyId), ct);
        n += await SystemScope(db.Payslips).Where(x => x.TenantId == tenantId && x.CompanyId == null).ExecuteUpdateAsync(s => s.SetProperty(x => x.CompanyId, defaultCompanyId), ct);
        n += await SystemScope(db.PayrollSlips).Where(x => x.TenantId == tenantId && x.CompanyId == null).ExecuteUpdateAsync(s => s.SetProperty(x => x.CompanyId, defaultCompanyId), ct);
        n += await SystemScope(db.PayrollDeductions).Where(x => x.TenantId == tenantId && x.CompanyId == null).ExecuteUpdateAsync(s => s.SetProperty(x => x.CompanyId, defaultCompanyId), ct);
        n += await SystemScope(db.PayrollRuns).Where(x => x.TenantId == tenantId && x.CompanyId == null).ExecuteUpdateAsync(s => s.SetProperty(x => x.CompanyId, defaultCompanyId), ct);
        n += await SystemScope(db.EmployeeDocuments).Where(x => x.TenantId == tenantId && x.CompanyId == null).ExecuteUpdateAsync(s => s.SetProperty(x => x.CompanyId, defaultCompanyId), ct);
        n += await SystemScope(db.EmployeeLoans).Where(x => x.TenantId == tenantId && x.CompanyId == null).ExecuteUpdateAsync(s => s.SetProperty(x => x.CompanyId, defaultCompanyId), ct);
        n += await SystemScope(db.SalaryAdvances).Where(x => x.TenantId == tenantId && x.CompanyId == null).ExecuteUpdateAsync(s => s.SetProperty(x => x.CompanyId, defaultCompanyId), ct);
        n += await SystemScope(db.EmployeeBonuses).Where(x => x.TenantId == tenantId && x.CompanyId == null).ExecuteUpdateAsync(s => s.SetProperty(x => x.CompanyId, defaultCompanyId), ct);
        n += await SystemScope(db.EmployeeContracts).Where(x => x.TenantId == tenantId && x.CompanyId == null).ExecuteUpdateAsync(s => s.SetProperty(x => x.CompanyId, defaultCompanyId), ct);
        n += await SystemScope(db.VisaRecords).Where(x => x.TenantId == tenantId && x.CompanyId == null).ExecuteUpdateAsync(s => s.SetProperty(x => x.CompanyId, defaultCompanyId), ct);
        n += await SystemScope(db.PassportRecords).Where(x => x.TenantId == tenantId && x.CompanyId == null).ExecuteUpdateAsync(s => s.SetProperty(x => x.CompanyId, defaultCompanyId), ct);
        n += await SystemScope(db.WorkPermitRecords).Where(x => x.TenantId == tenantId && x.CompanyId == null).ExecuteUpdateAsync(s => s.SetProperty(x => x.CompanyId, defaultCompanyId), ct);
        n += await SystemScope(db.WPSFileBatches).Where(x => x.TenantId == tenantId && x.CompanyId == null).ExecuteUpdateAsync(s => s.SetProperty(x => x.CompanyId, defaultCompanyId), ct);
        // Recruitment: candidates/applications/offers have no reliable employee link, so they
        // belong in the default sweep (not the employee-accurate pass). Without this, legacy
        // null-CompanyId recruitment rows become invisible to company-scoped users.
        n += await SystemScope(db.Candidates).Where(x => x.TenantId == tenantId && x.CompanyId == null).ExecuteUpdateAsync(s => s.SetProperty(x => x.CompanyId, defaultCompanyId), ct);
        n += await SystemScope(db.JobApplications).Where(x => x.TenantId == tenantId && x.CompanyId == null).ExecuteUpdateAsync(s => s.SetProperty(x => x.CompanyId, defaultCompanyId), ct);
        n += await SystemScope(db.OfferLetters).Where(x => x.TenantId == tenantId && x.CompanyId == null).ExecuteUpdateAsync(s => s.SetProperty(x => x.CompanyId, defaultCompanyId), ct);
        return n;
    }

    /// <summary>
    /// Single choke-point for the backfill's filter bypass so every call shares one
    /// audited justification.
    /// IgnoreQueryFilters is intentional: this is a SYSTEM repair pass with no
    /// HttpContext (startup), so the tenant filter is already a no-op and the company
    /// filter would wrongly hide the null-CompanyId rows this service exists to fix.
    /// SYSTEM CONTEXT: tenant scope intentionally bypassed — every statement re-applies
    /// an explicit TenantId predicate; no cross-tenant read or write is possible.
    /// </summary>
    private static IQueryable<T> SystemScope<T>(IQueryable<T> query) where T : class
        => query.IgnoreQueryFilters();

    public sealed class BackfillSummary
    {
        public int CompaniesCreated { get; set; }
        public int EmployeesAssigned { get; set; }
        public int RowsAssigned { get; set; }
        public int RowsDefaulted { get; set; }
        public int TenantsPromotedToGroup { get; set; }
    }
}
