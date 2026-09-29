using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Auth;
using Zayra.Api.Application.Common;
using Zayra.Api.Controllers;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.Audit;
using Zayra.Api.Infrastructure.Employees;
using Zayra.Api.Infrastructure.Notifications;
using Zayra.Api.Models;
using Xunit;

namespace Zayra.Api.Tests;

/// <summary>
/// Preview ↔ commit parity for the payroll side of the employee import: one salary parser, one set of gap
/// producers, one repair decision and one storage check, so the dry run predicts what the commit does.
/// (The first four tests come from the parallel WT session; the rest pin review defects 2 and 5.)
/// </summary>
public class EmployeeImportPayrollParityTests
{
    private static ZayraDbContext CreateDb() =>
        new(new DbContextOptionsBuilder<ZayraDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static async Task<Guid> SeedTenant(ZayraDbContext db)
    {
        var id = Guid.NewGuid();
        db.Tenants.Add(new Tenant { Id = id, Name = "Zayra", Slug = $"z-{id:N}" });
        db.TenantSubscriptions.Add(new TenantSubscription { TenantId = id, MaxEmployees = 1000, Plan = "Enterprise", Status = "Active" });
        await db.SaveChangesAsync();
        return id;
    }

    private static async Task<Company> SeedCompany(ZayraDbContext db, Guid tenantId, string name)
    {
        var c = new Company
        {
            TenantId = tenantId, LegalNameEn = name, CountryCode = "SA", Jurisdiction = "test",
            RegistrationNumber = $"RC-{Guid.NewGuid():N}", DefaultCurrency = "SAR", IsActive = true, CreatedAtUtc = DateTime.UtcNow,
        };
        db.Companies.Add(c);
        await db.SaveChangesAsync();
        return c;
    }

    private static async Task<Department> SeedDepartment(ZayraDbContext db, Guid tenantId, string code, string name)
    {
        var d = new Department { TenantId = tenantId, Code = code, NameEn = name, IsActive = true };
        db.Departments.Add(d);
        await db.SaveChangesAsync();
        return d;
    }

    private static EmployeesController ImportController(ZayraDbContext db, Guid tenantId) =>
        HrmHierarchyTests.BuildImportControllerInternal(db, tenantId);

    private static string S(object v) => JsonSerializer.Serialize(v);
    private static object Payload(IActionResult r) => Assert.IsType<OkObjectResult>(r).Value!;


    private static async Task SeedGrade(ZayraDbContext db, Guid tenant)
    {
        db.Grades.Add(new Grade { TenantId = tenant, Code = "G1", Name = "Demo grade", MinSalary = 0, MaxSalary = 100000, Currency = "SAR", IsActive = true });
        await db.SaveChangesAsync();
    }
    private static JsonElement JsonPayload(IActionResult response) => JsonSerializer.SerializeToElement(Payload(response));

    [Fact]
    public async Task HeldSalary_PreviewMustListItAsUnresolved_NotComplete()
    {
        await using var db = CreateDb();
        var tenant = await SeedTenant(db); await SeedCompany(db, tenant, "Acme");
        var ctrl = ImportController(db, tenant);
        var csv = "EmployeeCode,FullName,CompanyLegalName,Grade,BasicSalary,JoiningDate\nE1,Demo Person,Acme,UNKNOWN,5000,2024-01-01\n";
        var preview = JsonPayload(await ctrl.ImportPreview(new EmployeesController.ImportEmployeesRequest(csv), CancellationToken.None));
        var row = preview.GetProperty("rows")[0];
        Assert.Contains(row.GetProperty("recommended").EnumerateArray(), x => x.GetString()!.Contains("Salary held"));
    }

    [Theory]
    [InlineData("5000", "NOT-A-NUMBER", "HousingAllowance")]
    [InlineData("0", "1500", "BasicSalary")]
    public async Task InvalidSalary_PreviewExplainsWhyNoAssignmentWillBeWritten(string basic, string housing, string field)
    {
        await using var db = CreateDb();
        var tenant = await SeedTenant(db); await SeedCompany(db, tenant, "Acme"); await SeedGrade(db, tenant);
        var ctrl = ImportController(db, tenant);
        var csv = $"EmployeeCode,FullName,CompanyLegalName,Grade,BasicSalary,HousingAllowance,JoiningDate\nE1,Demo Person,Acme,G1,{basic},{housing},2024-01-01\n";
        var preview = JsonPayload(await ctrl.ImportPreview(new EmployeesController.ImportEmployeesRequest(csv), CancellationToken.None));
        Assert.Contains(preview.GetProperty("rows")[0].GetProperty("warnings").EnumerateArray(), x => x.GetString()!.Contains(field));
    }

    [Fact]
    public async Task InvalidSalary_DoesNotSilentlyDiscardValidBankProfile()
    {
        await using var db = CreateDb();
        var tenant = await SeedTenant(db); await SeedCompany(db, tenant, "Acme"); await SeedGrade(db, tenant);
        var ctrl = ImportController(db, tenant);
        var csv = "EmployeeCode,FullName,CompanyLegalName,Grade,BasicSalary,HousingAllowance,BankName,JoiningDate\nE1,Demo Person,Acme,G1,5000,BAD,Demo Bank,2024-01-01\n";
        var commit = JsonPayload(await ctrl.Import(new EmployeesController.ImportEmployeesRequest(csv), CancellationToken.None));
        Assert.Equal(1, commit.GetProperty("created").GetInt32());
        Assert.Equal("Demo Bank", (await db.EmployeePayrollProfiles.SingleAsync(x => x.TenantId == tenant)).BankName);
        Assert.Empty(await db.EmployeeSalaryStructures.Where(x => x.TenantId == tenant).ToListAsync());
    }

    [Fact]
    public async Task TwoHundredFiftyEmployees_PreserveSalaryAndHierarchy_RepeatDoesNotDuplicate()
    {
        await using var db = CreateDb();
        var tenant = await SeedTenant(db); await SeedCompany(db, tenant, "Acme"); await SeedGrade(db, tenant);
        var ctrl = ImportController(db, tenant);
        var lines = Enumerable.Range(1, 250).Select(i => $"E{i:D4},Demo Person {i},Acme,G1,{5000+i},1000,500,SAR,2024-01-01,{(i == 1 ? "" : $"E{Math.Max(1, (i - 1) / 5):D4}")}");
        var csv = "EmployeeCode,FullName,CompanyLegalName,Grade,BasicSalary,HousingAllowance,TransportAllowance,Currency,JoiningDate,ManagerEmployeeCode\n" + string.Join("\n", lines);
        var commit = JsonPayload(await ctrl.Import(new EmployeesController.ImportEmployeesRequest(csv), CancellationToken.None));
        Assert.Equal(250, commit.GetProperty("created").GetInt32());
        // One shared grade structure, not 250 unsaved duplicates hidden by InMemory.
        Assert.Single(await db.SalaryStructures.Where(x => x.TenantId == tenant).ToListAsync());
        Assert.Equal(250, await db.EmployeeSalaryStructures.CountAsync(x => x.TenantId == tenant && x.IsActive));
        Assert.Equal(249, await db.Employees.CountAsync(x => x.TenantId == tenant && x.ManagerEmployeeId != null));
        Assert.Equal(Enumerable.Range(1,250).Sum(i => 5000m+i), await db.EmployeeSalaryStructures.Where(x => x.TenantId == tenant).SumAsync(x => x.BasicSalary));
        var retry = JsonPayload(await ctrl.Import(new EmployeesController.ImportEmployeesRequest(csv), CancellationToken.None));
        Assert.Equal(0, retry.GetProperty("created").GetInt32());
        Assert.Equal(250, await db.Employees.CountAsync(x => x.TenantId == tenant));
        Assert.Equal(250, await db.EmployeeSalaryStructures.CountAsync(x => x.TenantId == tenant));
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    //  Defect 2 — the preview uses the commit's parsers and gap producers
    // ══════════════════════════════════════════════════════════════════════════════════════════

    private static JsonElement PreviewRow(JsonElement preview, int index) => preview.GetProperty("rows")[index];
    private static IEnumerable<string> Strings(JsonElement array) => array.EnumerateArray().Select(x => x.GetString()!);

    /// <summary>On main the preview parsed JoiningDate with the server culture and fell back to TODAY, so an
    /// unreadable date projected a clean Active row. It now projects exactly what the commit does: Draft, blocked
    /// on the joining date, with the data:unparsedDate gap in the "most common gaps" strip.</summary>
    [Fact]
    public async Task Preview_UnreadableJoiningDate_ProjectsDraftBlocked_ExactlyAsTheCommitLands()
    {
        await using var db = CreateDb();
        var tenant = await SeedTenant(db); await SeedCompany(db, tenant, "Acme");
        var ctrl = ImportController(db, tenant);
        var csv = "EmployeeCode,FullName,CompanyLegalName,JoiningDate,Status\nE1,Demo Person,Acme,YYYY-MM-DD,Active\n";

        var preview = JsonPayload(await ctrl.ImportPreview(new EmployeesController.ImportEmployeesRequest(csv), CancellationToken.None));
        var row = PreviewRow(preview, 0);
        Assert.Equal("Draft", row.GetProperty("projectedStatus").GetString());
        Assert.Contains(Strings(row.GetProperty("blocking")), l => l.Contains("Joining date"));
        Assert.Contains(preview.GetProperty("fieldGaps").EnumerateArray(), g => g.GetProperty("field").GetString() == "data:unparsedDate");

        await ctrl.Import(new EmployeesController.ImportEmployeesRequest(csv), CancellationToken.None);
        var employee = await db.Employees.SingleAsync(e => e.TenantId == tenant);
        Assert.Equal("Draft", employee.Status);
        Assert.Equal(default, employee.JoiningDate);
    }

    /// <summary>On main the preview checked BasicSalary with the SERVER culture while the commit used the
    /// invariant one, so under a comma-decimal locale "1.500,50" previewed as fine and was then refused a salary
    /// at commit. Both now use ParseImportSalary, whatever the server's culture.</summary>
    [Fact]
    public async Task Preview_UsesTheCommitsSalaryParser_UnderACommaDecimalServerCulture()
    {
        var previous = System.Globalization.CultureInfo.CurrentCulture;
        System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
        try
        {
            await using var db = CreateDb();
            var tenant = await SeedTenant(db); await SeedCompany(db, tenant, "Acme"); await SeedGrade(db, tenant);
            var ctrl = ImportController(db, tenant);
            var csv = "EmployeeCode,FullName,CompanyLegalName,Grade,BasicSalary,JoiningDate\nE1,Demo Person,Acme,G1,\"1.500,50\",2024-01-01\n";

            var preview = JsonPayload(await ctrl.ImportPreview(new EmployeesController.ImportEmployeesRequest(csv), CancellationToken.None));
            var row = PreviewRow(preview, 0);
            Assert.Contains(Strings(row.GetProperty("warnings")), w => w.Contains("BasicSalary is not a number"));
            Assert.Contains(Strings(row.GetProperty("recommended")), r => r.Contains("Salary needs review"));

            await ctrl.Import(new EmployeesController.ImportEmployeesRequest(csv), CancellationToken.None);
            Assert.Empty(await db.EmployeeSalaryStructures.Where(x => x.TenantId == tenant).ToListAsync());
            Assert.Contains(await db.EmployeeImportGaps.Where(g => g.TenantId == tenant).Select(g => g.GapType).ToListAsync(),
                t => t == "pay:salaryReview");
        }
        finally { System.Globalization.CultureInfo.CurrentCulture = previous; }
    }

    /// <summary>The preview used to emit none of the data:unparsed* gaps the commit records, so an unreadable
    /// expiry (a fail-closed PAY gate in five GCC branches) was invisible until after the import.</summary>
    [Fact]
    public async Task Preview_ReportsAnUnreadableExpiry_AsTheSameGapTheCommitPersists()
    {
        await using var db = CreateDb();
        var tenant = await SeedTenant(db); await SeedCompany(db, tenant, "Acme");
        var ctrl = ImportController(db, tenant);
        var csv = "EmployeeCode,FullName,CompanyLegalName,JoiningDate,PassportExpiryDate\nE1,Demo Person,Acme,2024-01-01,31-13-2025\n";

        var preview = JsonPayload(await ctrl.ImportPreview(new EmployeesController.ImportEmployeesRequest(csv), CancellationToken.None));
        Assert.Contains(preview.GetProperty("fieldGaps").EnumerateArray(), g => g.GetProperty("field").GetString() == "data:unparsedDate");
        Assert.Contains(Strings(PreviewRow(preview, 0).GetProperty("recommended")), r => r.Contains("could not be read"));

        await ctrl.Import(new EmployeesController.ImportEmployeesRequest(csv), CancellationToken.None);
        Assert.Contains(await db.EmployeeImportGaps.Where(g => g.TenantId == tenant).Select(g => g.GapType).ToListAsync(),
            t => t == "data:unparsedDate");
    }

    /// <summary>One mixed file: the preview's counts are the commit's counts.</summary>
    [Fact]
    public async Task Preview_And_Commit_AgreeOnCreatedRepairedAndSkippedCounts()
    {
        await using var db = CreateDb();
        var tenant = await SeedTenant(db); var company = await SeedCompany(db, tenant, "Acme"); await SeedGrade(db, tenant);
        db.Employees.Add(new Employee { TenantId = tenant, CompanyId = company.Id, EmployeeCode = "OLD-1", FullName = "Already Complete", Status = "Active", JoiningDate = new DateTime(2023, 1, 1, 0, 0, 0, DateTimeKind.Utc) });
        db.Employees.Add(new Employee { TenantId = tenant, CompanyId = company.Id, EmployeeCode = "OLD-2", FullName = "Missing Payroll", Status = "Active", JoiningDate = new DateTime(2023, 1, 1, 0, 0, 0, DateTimeKind.Utc) });
        await db.SaveChangesAsync();
        var ctrl = ImportController(db, tenant);
        var csv = "EmployeeCode,FullName,CompanyLegalName,Grade,BasicSalary,JoiningDate,BankName\n"
                  + "N1,New Person,Acme,G1,5000,2024-01-01,Bank\n"
                  + "N2,,Acme,G1,5000,2024-01-01,Bank\n"              // no name → skipped
                  + "N1,Same Code Twice,Acme,G1,5000,2024-01-01,Bank\n" // duplicate in file → skipped
                  + "OLD-1,Already Complete,Acme,,,2023-01-01,\n"      // exists, nothing to fill → skipped
                  + "OLD-2,Missing Payroll,Acme,G1,6000,2023-01-01,Bank\n" // exists, profile + salary missing → repaired
                  + "N3,Bad Date,Acme,G1,5000,notadate,Bank\n";        // created (Draft, blocked)

        var preview = JsonPayload(await ctrl.ImportPreview(new EmployeesController.ImportEmployeesRequest(csv), CancellationToken.None));
        var commit = JsonPayload(await ctrl.Import(new EmployeesController.ImportEmployeesRequest(csv), CancellationToken.None));

        Assert.Equal(2, preview.GetProperty("wouldCreate").GetInt32());
        Assert.Equal(1, preview.GetProperty("wouldRepair").GetInt32());
        Assert.Equal(3, preview.GetProperty("wouldSkip").GetInt32());
        Assert.Equal(preview.GetProperty("wouldCreate").GetInt32(), commit.GetProperty("created").GetInt32());
        Assert.Equal(preview.GetProperty("wouldRepair").GetInt32(), commit.GetProperty("repaired").GetInt32());
        Assert.Equal(preview.GetProperty("wouldSkip").GetInt32(), commit.GetProperty("skipped").GetInt32());
        Assert.Equal(0, commit.GetProperty("failed").GetInt32());
        Assert.Equal(commit.GetProperty("received").GetInt32(),
            commit.GetProperty("created").GetInt32() + commit.GetProperty("repaired").GetInt32() + commit.GetProperty("skipped").GetInt32());
    }

    /// <summary>A value the database cannot store (FullName is varchar(150)) is named by row and column in both
    /// the preview ("WillFail") and the commit (422, nothing written) — it used to surface only as a batched
    /// SaveChanges failure that could name no row at all.</summary>
    [Fact]
    public async Task AnOverlongValue_IsNamedByRowAndColumn_InThePreviewAndTheRefusal()
    {
        await using var db = CreateDb();
        var tenant = await SeedTenant(db); await SeedCompany(db, tenant, "Acme");
        var ctrl = ImportController(db, tenant);
        var longName = new string('A', 151);
        var csv = $"EmployeeCode,FullName,CompanyLegalName,JoiningDate\nE1,Fine Person,Acme,2024-01-01\nE2,{longName},Acme,2024-01-01\n";

        var preview = JsonPayload(await ctrl.ImportPreview(new EmployeesController.ImportEmployeesRequest(csv), CancellationToken.None));
        Assert.Equal(1, preview.GetProperty("wouldFail").GetInt32());
        Assert.Equal("WillFail", PreviewRow(preview, 1).GetProperty("status").GetString());
        Assert.Contains(Strings(PreviewRow(preview, 1).GetProperty("errors")), e => e.Contains("FullName") && e.Contains("150"));

        var refused = Assert.IsType<UnprocessableEntityObjectResult>(await ctrl.Import(new EmployeesController.ImportEmployeesRequest(csv), CancellationToken.None));
        var body = JsonSerializer.SerializeToElement(refused.Value);
        Assert.Equal("import_row_invalid", body.GetProperty("error").GetString());
        var failed = body.GetProperty("failedRows")[0];
        Assert.Equal(3, failed.GetProperty("row").GetInt32());   // header is row 1
        Assert.Equal("E2", failed.GetProperty("employeeCode").GetString());
        Assert.Equal("FullName", failed.GetProperty("column").GetString());
        Assert.Empty(await db.Employees.Where(e => e.TenantId == tenant).ToListAsync());
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    //  Defect 5 — bank details land in BOTH homes, whether or not a salary structure is created
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>On main the import wrote Employee.BankName/BankIban only when it ALSO created a salary structure,
    /// so a held salary (unknown grade), a bad salary cell or no salary left the bank details on the payroll
    /// profile only — the employee record, its readiness and the People list showed none.</summary>
    [Theory]
    [InlineData("UNKNOWN", "5000")]   // salary held: no such grade
    [InlineData("G1", "BAD")]         // salary refused: unreadable cell
    [InlineData("G1", "")]            // no salary at all
    public async Task ImportedBankDetails_AreOnTheEmployeeAndTheProfile_WhateverHappensToTheSalary(string grade, string basic)
    {
        await using var db = CreateDb();
        var tenant = await SeedTenant(db); await SeedCompany(db, tenant, "Acme"); await SeedGrade(db, tenant);
        var ctrl = ImportController(db, tenant);
        var csv = $"EmployeeCode,FullName,CompanyLegalName,Grade,BasicSalary,JoiningDate,BankName,IBAN\nE1,Demo Person,Acme,{grade},{basic},2024-01-01,Demo Bank,SA0380000000608010167519\n";

        await ctrl.Import(new EmployeesController.ImportEmployeesRequest(csv), CancellationToken.None);

        var employee = await db.Employees.SingleAsync(e => e.TenantId == tenant);
        var profile = await db.EmployeePayrollProfiles.SingleAsync(p => p.TenantId == tenant);
        Assert.Equal("Demo Bank", profile.BankName);
        Assert.Equal("SA0380000000608010167519", profile.Iban);
        Assert.Equal(profile.BankName, employee.BankName);
        Assert.Equal(profile.Iban, employee.BankIban);
    }
}

