using Microsoft.EntityFrameworkCore;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Payroll;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Compliance;

/// <summary>
/// Builds a per-employee GOSI readiness report using the official contribution-rule
/// engine (GosiCalculationService + GosiReadinessValidator).
///
/// No sensitive identifiers (GosiReference, NationalId, Iqama, IBAN, or raw
/// contributory wage) appear in any output record.
/// </summary>
public sealed class GosiReadinessReportService
{
    private readonly ZayraDbContext _db;

    public GosiReadinessReportService(ZayraDbContext db) => _db = db;

    public async Task<GosiReadinessReport> BuildAsync(Guid tenantId, CancellationToken ct)
    {
        var periodDate = DateOnly.FromDateTime(DateTime.UtcNow);

        var employees = await _db.Employees.AsNoTracking()
            .Where(e => e.TenantId == tenantId && !e.IsDeleted && e.Status == "Active")
            .ToListAsync(ct);

        var salaries = await _db.EmployeeSalaryStructures.AsNoTracking()
            .Where(s => s.TenantId == tenantId && s.IsActive)
            .ToListAsync(ct);

        // IgnoreQueryFilters is intentional: platform-wide default rules carry TenantId==Guid.Empty
        // and are excluded by the global tenant filter. We bypass the filter and re-apply explicit
        // scope: own-tenant overrides + Guid.Empty defaults only. No other tenant's rows are visible.
        var rules = await _db.GosiContributionRules
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(r => (r.TenantId == Guid.Empty || r.TenantId == tenantId) && r.IsActive)
            .ToListAsync(ct);

        var rows = new List<GosiEmployeeReadinessRow>(employees.Count);
        var readyCount = 0;

        foreach (var emp in employees)
        {
            var salary = salaries
                .Where(s => s.EmployeeId == emp.Id && s.EffectiveDate <= periodDate)
                .OrderByDescending(s => s.EffectiveDate)
                .FirstOrDefault();

            var applicable = GosiCalculationService.SelectActiveRules(
                GosiCalculationService.DeriveClassification(emp.Nationality),
                rules, periodDate, tenantId);

            var readiness = GosiReadinessValidator.Validate(emp, salary?.BasicSalary, applicable);

            decimal employeeTotal = 0m;
            decimal employerTotal = 0m;
            var lines = Array.Empty<GosiContributionLineDto>();

            if (readiness.IsReady)
            {
                var calc = GosiCalculationService.Calculate(
                    emp.Nationality, salary!.BasicSalary, rules, periodDate, tenantId);

                employeeTotal = calc.EmployeeTotal;
                employerTotal = calc.EmployerTotal;
                // ContributoryWage excluded — it reveals the employee's basic salary.
                lines = calc.Lines
                    .Select(l => new GosiContributionLineDto(l.Branch, l.Payer, l.Rate, l.Amount))
                    .ToArray();

                readyCount++;
            }

            rows.Add(new GosiEmployeeReadinessRow(
                EmployeeId:               emp.Id,
                EmployeeCode:             emp.EmployeeCode,
                FullName:                 emp.FullName,
                Classification:           readiness.Classification,
                IsReady:                  readiness.IsReady,
                BlockingIssues:           readiness.BlockingIssues.Select(i => new GosiIssueDto(i.Code, i.Message)).ToArray(),
                Warnings:                 readiness.Warnings.Select(i => new GosiIssueDto(i.Code, i.Message)).ToArray(),
                EmployeeContributionTotal: employeeTotal,
                EmployerContributionTotal: employerTotal,
                Lines:                    lines));
        }

        return new GosiReadinessReport(
            TotalEmployees: employees.Count,
            ReadyCount:     readyCount,
            BlockedCount:   employees.Count - readyCount,
            PeriodDate:     periodDate,
            CalculatedAt:   DateTime.UtcNow,
            Disclaimer:     "This report is illustrative readiness guidance based on configured rules; validate statutory filings with GOSI and qualified Saudi compliance advisers.",
            Employees:      rows);
    }
}

// ── Response types (no sensitive salary or identifier values) ─────────────────

public record GosiReadinessReport(
    int                                  TotalEmployees,
    int                                  ReadyCount,
    int                                  BlockedCount,
    DateOnly                             PeriodDate,
    DateTime                             CalculatedAt,
    string                               Disclaimer,
    IReadOnlyList<GosiEmployeeReadinessRow> Employees);

public record GosiEmployeeReadinessRow(
    int                                  EmployeeId,
    string                               EmployeeCode,
    string                               FullName,
    string                               Classification,
    bool                                 IsReady,
    IReadOnlyList<GosiIssueDto>          BlockingIssues,
    IReadOnlyList<GosiIssueDto>          Warnings,
    decimal                              EmployeeContributionTotal,
    decimal                              EmployerContributionTotal,
    IReadOnlyList<GosiContributionLineDto> Lines);

/// <summary>Issue DTO — symbolic code + human-readable message.  Never contains raw identifiers.</summary>
public record GosiIssueDto(string Code, string Message);

/// <summary>Per-branch contribution line.  ContributoryWage omitted to avoid leaking basic salary.</summary>
public record GosiContributionLineDto(string Branch, string Payer, decimal Rate, decimal Amount);
