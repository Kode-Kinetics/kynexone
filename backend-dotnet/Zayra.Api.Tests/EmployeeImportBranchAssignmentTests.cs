using Zayra.Api.Infrastructure.Employees;
using Zayra.Api.Models;
using Xunit;

namespace Zayra.Api.Tests;

/// <summary>
/// Branch assignment is never GUESSED. The importer used to silently hand a row with a blank BranchCode
/// the company's FIRST branch (an office the file never named), and — when the company had no branches at
/// all — silently leave the person branch-less with no gap and no warning, because the gap was guarded on
/// a non-blank code. That second path is what a tenant hits when it imports before running org setup
/// (TenantProvisioningBundle seeds zero branches): a blank Branch column for every employee and nothing in
/// the import summary saying why. These tests pin the three not-assigned outcomes apart.
/// </summary>
public class EmployeeImportBranchAssignmentTests
{
    private static Company MakeCompany(string name = "Acme Trading") =>
        new() { Id = Guid.NewGuid(), TenantId = Guid.NewGuid(), LegalNameEn = name, IsActive = true };

    private static Branch MakeBranch(Company company, string code, string nameEn, int createdOffsetMinutes) =>
        new()
        {
            Id = Guid.NewGuid(),
            TenantId = company.TenantId,
            CompanyId = company.Id,
            Code = code,
            NameEn = nameEn,
            IsActive = true,
            CreatedAtUtc = DateTime.UtcNow.AddMinutes(createdOffsetMinutes),
        };

    /// <summary>Lookups holding exactly one company plus the supplied branches — every other master-data
    /// dictionary empty, which is precisely the state of a freshly provisioned tenant.</summary>
    private static ImportLookups Lookups(Company company, params Branch[] branches) => new()
    {
        CompaniesByName = new Dictionary<string, Company> { [company.LegalNameEn.ToUpperInvariant()] = company },
        DefaultCompany = company,
        BranchesByCode = branches.ToDictionary(b => (b.CompanyId, b.Code.ToUpperInvariant()), b => b),
        BranchesByCompany = branches.GroupBy(b => b.CompanyId)
            .ToDictionary(g => g.Key, g => g.OrderBy(b => b.CreatedAtUtc).ToList()),
        CostCentersByCode = new Dictionary<(Guid?, string), CostCenter>(),
        DeptByCode = new Dictionary<string, Guid>(),
        DeptByName = new Dictionary<string, Guid>(),
        DesigByTitle = new Dictionary<string, DesignationRef>(),
        GradeByCode = new Dictionary<string, Grade>(),
        GradeByName = new Dictionary<string, Grade>(),
        GradeById = new Dictionary<Guid, Grade>(),
        PositionsByCode = new Dictionary<string, Position>(),
    };

    private static Dictionary<string, string> Row(string branchCode) => new()
    {
        ["EmployeeCode"] = "EMP-001",
        ["FullName"] = "Sara Ahmed",
        ["CompanyLegalName"] = "Acme Trading",
        ["BranchCode"] = branchCode,
    };

    private static ResolvedImportRow Resolve(Dictionary<string, string> row, ImportLookups lk) =>
        EmployeeImportRowResolver.ResolveRow(row, lk, new HashSet<string>());

    /// <summary>(a) Blank BranchCode while the company HAS branches: the person is NOT quietly filed into
    /// the first one. The branch is left unassigned and the row carries the advisory org:branch gap, so the
    /// operator can see and repair it (the gap self-heals once a branch is assigned).</summary>
    [Fact]
    public void BlankBranchCode_WithBranchesAvailable_LeavesUnassignedAndFlags()
    {
        var company = MakeCompany();
        var hq = MakeBranch(company, "HQ", "Head Office", -60);      // oldest = the one silently inherited before
        var jed = MakeBranch(company, "JED", "Jeddah", -10);

        var r = Resolve(Row(string.Empty), Lookups(company, hq, jed));

        Assert.Equal(company.Id, r.CompanyId);
        Assert.Null(r.BranchId);                                     // was: hq.Id — a branch the file never named
        Assert.NotEqual(hq.Id, r.BranchId);
        Assert.NotEqual(jed.Id, r.BranchId);
        Assert.Equal(string.Empty, r.BranchNameEn);

        var gap = Assert.Single(r.Gaps, g => g.Type == "org:branch");
        Assert.Equal("org", gap.Category);
        Assert.Null(gap.RawValue);                                   // no code to create — one to choose
        Assert.Contains("No BranchCode supplied", gap.Detail);
        Assert.Contains("Acme Trading", gap.Detail);
        Assert.Contains(r.Warnings, w => w.Contains("No BranchCode supplied") && w.Contains("Acme Trading"));
    }

    /// <summary>(b) Blank BranchCode while the company has ZERO branches: unassigned, and its own
    /// tenant-level diagnostic naming the real fix (create the org structure). Completely silent before —
    /// no gap, no warning, nothing in the import summary. Deliberately a warning and NOT a per-employee
    /// gap: the operator cannot resolve "this tenant has no branches" on an individual person's record,
    /// and gapping it would mark every imported human NeedsAttention for one setup task.</summary>
    [Fact]
    public void BlankBranchCode_WithNoBranchesAtAll_IsReportedNotSilent()
    {
        var company = MakeCompany();

        var r = Resolve(Row(string.Empty), Lookups(company));

        Assert.Equal(company.Id, r.CompanyId);
        Assert.Null(r.BranchId);
        Assert.Equal(string.Empty, r.BranchNameEn);

        var warning = Assert.Single(r.Warnings, w => w.Contains("has no branches yet"));
        Assert.Contains("Acme Trading", warning);
        Assert.Contains("Organization setup", warning);
        Assert.DoesNotContain(r.Gaps, g => g.Type == "org:branch");
    }

    /// <summary>(c) Regression guard: a supplied-but-unresolvable BranchCode keeps its existing shape —
    /// unassigned + org:branch gap carrying the raw code + warning naming the company.</summary>
    [Fact]
    public void UnresolvableBranchCode_StillGapsAndWarnsUnchanged()
    {
        var company = MakeCompany();
        var hq = MakeBranch(company, "HQ", "Head Office", -60);

        var r = Resolve(Row("XYZ"), Lookups(company, hq));

        Assert.Null(r.BranchId);
        Assert.Equal(string.Empty, r.BranchNameEn);

        var gap = Assert.Single(r.Gaps, g => g.Type == "org:branch");
        Assert.Equal("org", gap.Category);
        Assert.Equal("XYZ", gap.RawValue);
        Assert.Contains("BranchCode 'XYZ' not found", gap.Detail);
        Assert.Contains(r.Warnings, w => w.Contains("BranchCode 'XYZ' not found") && w.Contains("Acme Trading"));
    }

    /// <summary>A supplied code that DOES resolve still assigns the branch, with no gap and no warning —
    /// the happy path the accept-never-block flags must never disturb.</summary>
    [Fact]
    public void ResolvableBranchCode_AssignsBranchWithoutFlags()
    {
        var company = MakeCompany();
        var hq = MakeBranch(company, "HQ", "Head Office", -60);
        var jed = MakeBranch(company, "JED", "Jeddah", -10);

        var r = Resolve(Row("jed"), Lookups(company, hq, jed));

        Assert.Equal(jed.Id, r.BranchId);
        Assert.Equal("Jeddah", r.BranchNameEn);
        Assert.DoesNotContain(r.Gaps, g => g.Type == "org:branch");
        Assert.DoesNotContain(r.Warnings, w => w.Contains("Branch", StringComparison.OrdinalIgnoreCase));
    }
}
