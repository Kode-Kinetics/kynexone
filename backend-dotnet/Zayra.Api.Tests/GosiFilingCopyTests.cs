using Microsoft.EntityFrameworkCore;
using Zayra.Api.Data;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// The GOSI Filing screen showed a customer: "Company 'Test for Claude' has no CountryCode;
/// expected GOSI cannot be recomputed." `CountryCode` is a column name, not a word anyone outside
/// this repository knows, and "recomputed" describes our machinery rather than their problem.
/// These tests pin the note to plain commercial language that names the fix.
/// </summary>
public class GosiFilingCopyTests
{
    private static readonly Guid TenantId = Guid.NewGuid();

    private static ZayraDbContext MakeDb()
    {
        var opts = new DbContextOptionsBuilder<ZayraDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new ZayraDbContext(opts);
    }

    /// <summary>Identifiers that must never reach a sentence a customer reads.</summary>
    private static readonly string[] InternalIdentifiers =
        { "CountryCode", "TenantId", "EmployeeId", "CompanyId", "LegalNameEn", "GosiEmployerId", "RunId" };

    [Fact]
    public async Task PackStatusNote_ForACompanyWithNoCountry_ReadsAsPlainEnglishAndNamesTheFix()
    {
        using var db = MakeDb();

        var company = new Company
        {
            TenantId    = TenantId,
            LegalNameEn = "Test for Claude",
            CountryCode = "",
            IsActive    = true,
            IsDeleted   = false,
        };
        db.Companies.Add(company);
        var run = new PayrollRun { TenantId = TenantId, Year = 2026, Month = 5, Status = "Locked", CompanyId = company.Id };
        db.PayrollRuns.Add(run);
        await db.SaveChangesAsync();

        var result = await TestReconciliation.For(db).ReconcileAsync(TenantId, run, CancellationToken.None);

        Assert.NotNull(result.PackStatusNote);
        var note = result.PackStatusNote!;

        Assert.Contains("Test for Claude", note);
        Assert.Contains("has no country set", note);
        Assert.Contains("Setup → Companies", note);
        foreach (var leak in InternalIdentifiers)
            Assert.DoesNotContain(leak, note);
    }

    [Fact]
    public async Task PackStatusNote_ForAnUnsupportedCountry_NamesThePlace_NotAnEmptyQuotedCode()
    {
        using var db = MakeDb();

        var company = new Company
        {
            TenantId    = TenantId,
            LegalNameEn = "Test for Claude",
            CountryCode = "ZZ",
            IsActive    = true,
            IsDeleted   = false,
        };
        db.Companies.Add(company);
        var run = new PayrollRun { TenantId = TenantId, Year = 2026, Month = 5, Status = "Locked", CompanyId = company.Id };
        db.PayrollRuns.Add(run);
        await db.SaveChangesAsync();

        var result = await TestReconciliation.For(db).ReconcileAsync(TenantId, run, CancellationToken.None);

        Assert.NotNull(result.PackStatusNote);
        var note = result.PackStatusNote!;

        Assert.Contains("ZZ", note);
        Assert.DoesNotContain("''", note);
        foreach (var leak in InternalIdentifiers)
            Assert.DoesNotContain(leak, note);
    }
}
