using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Common;
using Zayra.Api.Data;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Payroll;

/// <summary>
/// A new payroll-money record that arrives without a currency takes the employing company's currency when it is
/// saved. These records used to default to "AED", so a Saudi company's new payroll profile was silently written in
/// dirhams. A currency the caller did set is never touched.
/// </summary>
public static class CompanyCurrencyDefaults
{
    /// <summary>The records whose currency column used to default to a hard-coded literal.</summary>
    public static readonly IReadOnlySet<Type> CoveredTypes = new HashSet<Type>()
    {
        typeof(EmployeePayrollProfile), typeof(OvertimeCalculation), typeof(OvertimeBudget), typeof(SalaryStructure),
        typeof(EmployeeSalaryStructure), typeof(PayrollGroup), typeof(BenefitPlan), typeof(PayrollPaymentBatch),
        typeof(PayrollOpeningBalance), typeof(EosbProvisionSnapshot), typeof(GlAdjustmentRequest), typeof(FinanceGlEntry),
        typeof(Position), typeof(Grade), typeof(EmployeeFinalSettlement), typeof(EmployeeContract), typeof(WorkforcePlan),
    };

    /// <summary>The currency column of a covered entity: SalaryCurrency, Currency or CurrencyCode.</summary>
    public static string? CurrencyPropertyName(Microsoft.EntityFrameworkCore.Metadata.IEntityType type)
        => (type.FindProperty("SalaryCurrency") ?? type.FindProperty("Currency") ?? type.FindProperty("CurrencyCode"))?.Name;

    public static async Task ApplyAsync(ZayraDbContext db, CancellationToken ct)
    {
        var pending = db.ChangeTracker.Entries()
            .Where(e => e.State == EntityState.Added && CoveredTypes.Contains(e.Entity.GetType()))
            .Select(e => (Entry: e, Prop: e.Metadata.FindProperty("SalaryCurrency") ?? e.Metadata.FindProperty("Currency") ?? e.Metadata.FindProperty("CurrencyCode")))
            .Where(x => x.Prop is not null && string.IsNullOrWhiteSpace(x.Entry.CurrentValues[x.Prop!] as string))
            .ToList();
        if (pending.Count == 0) return;

        var byCompany = new Dictionary<(Guid Tenant, Guid? Company), string>();
        foreach (var (entry, prop) in pending)
        {
            var tenantId = entry.CurrentValues["TenantId"] is Guid t ? t : Guid.Empty;
            var companyId = entry.Metadata.FindProperty("CompanyId") is { } cp ? entry.CurrentValues[cp] as Guid? : null;
            if (companyId is null && entry.Metadata.FindProperty("EmployeeId") is { } ep && entry.CurrentValues[ep] is int employeeId)
            {
                var local = db.ChangeTracker.Entries<Employee>().FirstOrDefault(x => x.Entity.Id == employeeId && x.Entity.TenantId == tenantId)?.Entity;
                companyId = local is not null
                    ? local.CompanyId
                    : await db.Employees.AsNoTracking().Where(x => x.TenantId == tenantId && x.Id == employeeId)
                        .Select(x => x.CompanyId).FirstOrDefaultAsync(ct);
            }
            if (!byCompany.TryGetValue((tenantId, companyId), out var currency))
            {
                var fromCompany = companyId is null ? null : await db.Companies.AsNoTracking()
                    .Where(c => c.TenantId == tenantId && c.Id == companyId)
                    .Select(c => c.DefaultCurrency).FirstOrDefaultAsync(ct);
                currency = !string.IsNullOrWhiteSpace(fromCompany)
                    ? fromCompany.Trim().ToUpperInvariant()
                    : (await db.ResolveTenantCurrencyAsync(tenantId, ct)).Trim().ToUpperInvariant();
                byCompany[(tenantId, companyId)] = currency;
            }
            entry.CurrentValues[prop!] = currency;
        }
    }
}
