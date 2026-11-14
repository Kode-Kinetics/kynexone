using Microsoft.EntityFrameworkCore;
using Zayra.Api.Data;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Payroll;

/// <summary>
/// The currency money on an employee's record is written in: the currency of the company that employs them. There is no fallback to a
/// literal, because a hard-coded dirham on a Saudi company's salary structure or benefit enrolment is a wrong amount that looks right.
/// </summary>
public static class EmployeeCompanyCurrency
{
    /// <summary>The employee's company currency (upper-case), or <see cref="CompanyCurrencyUnknownException"/> when it cannot be known.</summary>
    public static async Task<string> ResolveAsync(ZayraDbContext db, Employee employee, CancellationToken ct)
    {
        if (employee.CompanyId is not Guid companyId)
            throw new CompanyCurrencyUnknownException($"Employee {employee.EmployeeCode} is not assigned to a company, so there is no currency to write their amounts in. Assign the company first.");
        var currency = await db.Companies.AsNoTracking()
            .Where(x => x.TenantId == employee.TenantId && x.Id == companyId).Select(x => x.DefaultCurrency).FirstOrDefaultAsync(ct);
        if (string.IsNullOrWhiteSpace(currency))
            throw new CompanyCurrencyUnknownException($"The company of employee {employee.EmployeeCode} has no currency set. Set the company currency first.");
        return currency.Trim().ToUpperInvariant();
    }
}

/// <summary>The employee has no company, or their company has no currency, so no amount can be written for them.</summary>
public sealed class CompanyCurrencyUnknownException(string message) : InvalidOperationException(message)
{
    public const string Code = "company_currency_unknown";
}
