using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Controllers;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// Pilot-readiness, real-data import safety, on REAL PostgreSQL with production's retrying strategy:
/// a real employee file is imported whole or not at all, the preview's verdict is the commit's own answer
/// (a rolled-back run of the same code), and master data that differs only in letter case — or is
/// duplicated by name — never breaks the tenant's import. Every test asserts on persisted rows.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration")]
public sealed class EmployeeImportPilotSafetyPostgresTests
{
    private readonly PostgresFixture _fixture;
    public EmployeeImportPilotSafetyPostgresTests(PostgresFixture fixture) => _fixture = fixture;

    private const string Header = "EmployeeCode,FullName,CompanyLegalName,Department,Grade,BasicSalary,Currency,JoiningDate,ManagerEmployeeCode,BankName";

    private async Task<Guid> SeedAsync()
    {
        await using var seed = _fixture.CreateDb();
        var tenant = await PostgresFixture.SeedMinimalTenant(seed);
        seed.TenantSubscriptions.Add(new TenantSubscription { TenantId = tenant, Plan = "Enterprise", Status = "Active", MaxEmployees = 300 });
        seed.Companies.Add(Company(tenant, "Pilot Demo"));
        seed.Grades.Add(new Grade { TenantId = tenant, Code = "G1", Name = "Demo grade", Currency = "SAR", MinSalary = 1, MaxSalary = 50000, IsActive = true });
        await seed.SaveChangesAsync();
        return tenant;
    }

    private static Company Company(Guid tenant, string name) => new()
    {
        TenantId = tenant, LegalNameEn = name, CountryCode = "SA", Jurisdiction = "test",
        RegistrationNumber = $"PIL-{Guid.NewGuid():N}"[..20], DefaultCurrency = "SAR", IsActive = true,
    };

    /// <summary>A 50-person file. <paramref name="badLine"/> is the FILE line (header = line 1) to break.</summary>
    private static string FiftyRows(int? badLine = null, Func<int, string>? breakRow = null) =>
        Header + "\n" + string.Join("\n", Enumerable.Range(1, 50).Select(i =>
        {
            var line = i + 1;
            if (line == badLine && breakRow is not null) return breakRow(i);
            return $"PS{i:D4},Synthetic Pilot {i},Pilot Demo,Unresolved Department,G1,{5000 + i},SAR,2024-01-01,{(i == 1 ? "" : "PS0001")},Synthetic Bank";
        }));

    private static EmployeesController Controller(ZayraDbContext db, Guid tenant) =>
        HrmHierarchyTests.BuildImportControllerInternal(db, tenant);

    private static JsonElement Body(IActionResult r) => JsonSerializer.SerializeToElement(((ObjectResult)r).Value);

    private async Task<int> PersistedEmployees(Guid tenant)
    {
        await using var verify = _fixture.CreateDb();
        return await verify.Employees.IgnoreQueryFilters().CountAsync(e => e.TenantId == tenant);
    }

    private async Task AssertNothingPersisted(Guid tenant)
    {
        await using var verify = _fixture.CreateDb();
        Assert.Equal(0, await verify.Employees.IgnoreQueryFilters().CountAsync(e => e.TenantId == tenant));
        Assert.Equal(0, await verify.EmployeePayrollProfiles.IgnoreQueryFilters().CountAsync(e => e.TenantId == tenant));
        Assert.Equal(0, await verify.EmployeeSalaryStructures.IgnoreQueryFilters().CountAsync(e => e.TenantId == tenant));
        Assert.Equal(0, await verify.EmployeeImportGaps.IgnoreQueryFilters().CountAsync(e => e.TenantId == tenant));
        Assert.Equal(0, await verify.AuditLogs.IgnoreQueryFilters().CountAsync(a => a.TenantId == tenant && a.EntityName == EmployeesController.ImportBatchEntityName));
    }

    // ── F01: a 50-row file with a bad row 37 writes 0 rows and reports row 37 ────────────────────────

    [Fact]
    public async Task AFiftyRowFileWithANamelessRow37_WritesNothing_AndNamesRow37()
    {
        var tenant = await SeedAsync();
        var csv = FiftyRows(37, i => $"PS{i:D4},,Pilot Demo,Unresolved Department,G1,{5000 + i},SAR,2024-01-01,PS0001,Synthetic Bank");

        await using var db = _fixture.CreateDb();
        var refused = Assert.IsType<UnprocessableEntityObjectResult>(
            await Controller(db, tenant).Import(new EmployeesController.ImportEmployeesRequest(csv), CancellationToken.None));

        await AssertNothingPersisted(tenant);
        var body = Body(refused);
        Assert.Equal("import_rows_invalid", body.GetProperty("error").GetString());
        Assert.Equal(37, body.GetProperty("failedRows")[0].GetProperty("row").GetInt32());
        Assert.Equal(1, body.GetProperty("failedRows").GetArrayLength());
        Assert.StartsWith("Row 37 (EmployeeCode 'PS0036')", body.GetProperty("message").GetString());
        Assert.Contains("Nothing from this file was imported", body.GetProperty("message").GetString());
        Assert.Equal(0, body.GetProperty("created").GetInt32());
    }

    [Fact]
    public async Task AFiftyRowFileWithAnUnquotedThousandsSeparatorOnRow37_WritesNothing_AndNamesRow37AndBothCounts()
    {
        var tenant = await SeedAsync();
        // "8,000" unquoted: one cell becomes two and every later column on the row would shift by one.
        var csv = FiftyRows(37, i => $"PS{i:D4},Synthetic Pilot {i},Pilot Demo,Unresolved Department,G1,8,000,SAR,2024-01-01,PS0001,Synthetic Bank");

        await using var db = _fixture.CreateDb();
        var refused = Assert.IsType<UnprocessableEntityObjectResult>(
            await Controller(db, tenant).Import(new EmployeesController.ImportEmployeesRequest(csv), CancellationToken.None));

        await AssertNothingPersisted(tenant);
        var body = Body(refused);
        Assert.Equal("csv_row_shape", body.GetProperty("error").GetString());
        var row = body.GetProperty("failedRows")[0];
        Assert.Equal(37, row.GetProperty("row").GetInt32());
        Assert.Equal(11, row.GetProperty("cells").GetInt32());
        Assert.Equal(10, row.GetProperty("expected").GetInt32());
        Assert.Contains("CSV row 37 has 11 cell(s) but the header declares 10", body.GetProperty("message").GetString());

        // The preview refuses the same file the same way, before anything else is read.
        await using var previewDb = _fixture.CreateDb();
        var previewRefused = Assert.IsType<UnprocessableEntityObjectResult>(
            await Controller(previewDb, tenant).ImportPreview(new EmployeesController.ImportEmployeesRequest(csv), CancellationToken.None));
        Assert.Equal(37, Body(previewRefused).GetProperty("failedRows")[0].GetProperty("row").GetInt32());
    }

    // ── The preview's verdict is the commit's, from a rolled-back run of the same code ──────────────

    [Fact]
    public async Task ThePreviewOfABadFile_CarriesTheCommitsOwnRefusal_AndPersistsNothing()
    {
        var tenant = await SeedAsync();
        var csv = FiftyRows(37, i => $"PS{i:D4},,Pilot Demo,Unresolved Department,G1,{5000 + i},SAR,2024-01-01,PS0001,Synthetic Bank");

        await using var db = _fixture.CreateDb();
        var preview = Body(Assert.IsType<OkObjectResult>(
            await Controller(db, tenant).ImportPreview(new EmployeesController.ImportEmployeesRequest(csv), CancellationToken.None)));

        await AssertNothingPersisted(tenant);
        var check = preview.GetProperty("commitCheck");
        Assert.Equal("would_refuse", check.GetProperty("outcome").GetString());
        Assert.Equal("import_rows_invalid", check.GetProperty("error").GetString());
        Assert.Equal(37, check.GetProperty("failedRows")[0].GetProperty("row").GetInt32());
        Assert.Equal(1, preview.GetProperty("wouldFail").GetInt32());
        var row37 = preview.GetProperty("rows").EnumerateArray().Single(r => r.GetProperty("row").GetInt32() == 37);
        Assert.Equal("WillFail", row37.GetProperty("status").GetString());
    }

    [Fact]
    public async Task ThePreviewOfAGoodFile_PredictsTheCommitExactly_AndPersistsNothingUntilTheCommit()
    {
        var tenant = await SeedAsync();
        var csv = FiftyRows();

        await using (var previewDb = _fixture.CreateDb())
        {
            var preview = Body(Assert.IsType<OkObjectResult>(
                await Controller(previewDb, tenant).ImportPreview(new EmployeesController.ImportEmployeesRequest(csv), CancellationToken.None)));
            var check = preview.GetProperty("commitCheck");
            Assert.Equal("would_import", check.GetProperty("outcome").GetString());
            Assert.Equal(50, check.GetProperty("created").GetInt32());
            Assert.Equal(preview.GetProperty("wouldCreate").GetInt32(), check.GetProperty("created").GetInt32());
        }
        await AssertNothingPersisted(tenant);

        // Previewing twice is harmless too (the dry run leaves no batch marker, no codes, no audit rows).
        await using (var again = _fixture.CreateDb())
            Assert.IsType<OkObjectResult>(await Controller(again, tenant).ImportPreview(new EmployeesController.ImportEmployeesRequest(csv), CancellationToken.None));
        await AssertNothingPersisted(tenant);

        await using var db = _fixture.CreateDb();
        var commit = Body(Assert.IsType<OkObjectResult>(
            await Controller(db, tenant).Import(new EmployeesController.ImportEmployeesRequest(csv, Guid.NewGuid()), CancellationToken.None)));
        Assert.Equal(50, commit.GetProperty("created").GetInt32());
        Assert.Equal(50, await PersistedEmployees(tenant));
    }

    // ── F06: master data that differs only in case, or shares a name, never breaks the import ───────

    [Fact]
    public async Task CaseCollidingCodesAndSameNamedCompanies_AreFlaggedPerRow_InsteadOfFailingEveryImport()
    {
        var tenant = await SeedAsync();
        Guid riyadh, jeddah, financeRiyadh;
        await using (var seed = _fixture.CreateDb())
        {
            var company = await seed.Companies.SingleAsync(c => c.TenantId == tenant);
            seed.Companies.AddRange(Company(tenant, "Twin Holding"), Company(tenant, "TWIN HOLDING"));
            var r = new Branch { TenantId = tenant, CompanyId = company.Id, Code = "RUH", NameEn = "Riyadh" };
            var j = new Branch { TenantId = tenant, CompanyId = company.Id, Code = "JED", NameEn = "Jeddah" };
            seed.Branches.AddRange(r, j);
            // The unique (TenantId, Code) index is case-SENSITIVE, so 'ops' and 'OPS' can both exist (an import
            // stored one, the form the other). Building the import's lookup used to throw on them.
            seed.Departments.AddRange(
                new Department { TenantId = tenant, Code = "ops", NameEn = "Operations" },
                new Department { TenantId = tenant, Code = "OPS", NameEn = "Operations Support" });
            var fin1 = new Department { TenantId = tenant, Code = "FIN-RUH", NameEn = "Finance", BranchId = r.Id };
            seed.Departments.AddRange(fin1, new Department { TenantId = tenant, Code = "FIN-JED", NameEn = "Finance", BranchId = j.Id });
            seed.Positions.AddRange(
                new Position { TenantId = tenant, Code = "p-100", Title = "Clerk", EffectiveFrom = new DateOnly(2020, 1, 1) },
                new Position { TenantId = tenant, Code = "P-100", Title = "Clerk", EffectiveFrom = new DateOnly(2020, 1, 1) });
            await seed.SaveChangesAsync();
            riyadh = r.Id; jeddah = j.Id; financeRiyadh = fin1.Id;
        }

        var csv = "EmployeeCode,FullName,CompanyLegalName,BranchCode,Department,DepartmentCode,PositionCode,JoiningDate\n"
                  + "CC1,Case One,Pilot Demo,RUH,,OPS,,2024-01-01\n"
                  + "CC2,Case Two,Pilot Demo,RUH,Finance,,,2024-01-01\n"
                  + "CC3,Case Three,Pilot Demo,,Finance,,,2024-01-01\n"
                  + "CC4,Case Four,Twin Holding,,,,,2024-01-01\n"
                  + "CC5,Case Five,Pilot Demo,,,,P-100,2024-01-01\n";

        await using (var previewDb = _fixture.CreateDb())
            Assert.IsType<OkObjectResult>(await Controller(previewDb, tenant).ImportPreview(new EmployeesController.ImportEmployeesRequest(csv), CancellationToken.None));

        await using var db = _fixture.CreateDb();
        var summary = Body(Assert.IsType<OkObjectResult>(
            await Controller(db, tenant).Import(new EmployeesController.ImportEmployeesRequest(csv), CancellationToken.None)));
        Assert.Equal(5, summary.GetProperty("created").GetInt32());

        await using var verify = _fixture.CreateDb();
        var people = await verify.Employees.IgnoreQueryFilters().Where(e => e.TenantId == tenant).ToDictionaryAsync(e => e.EmployeeCode);
        var gaps = await verify.EmployeeImportGaps.IgnoreQueryFilters().Where(g => g.TenantId == tenant).ToListAsync();
        string GapDetail(string code, string type) => gaps.Single(g => g.EmployeeId == people[code].Id && g.GapType == type).Detail;

        Assert.Null(people["CC1"].DepartmentId);
        Assert.Contains("matches more than one department ('OPS', 'ops')", GapDetail("CC1", "org:department"));
        Assert.Equal(financeRiyadh, people["CC2"].DepartmentId); // same name, told apart by the row's branch
        Assert.Equal(riyadh, people["CC2"].BranchId);
        Assert.Null(people["CC3"].DepartmentId);                // no branch: nothing to tell them apart by
        Assert.Contains("matches more than one department", GapDetail("CC3", "org:department"));
        Assert.Null(people["CC4"].CompanyId);                   // never the first of two same-named companies
        Assert.Contains("matches more than one company", GapDetail("CC4", "org:company"));
        Assert.Null(people["CC5"].PositionId);
        Assert.Contains("matches more than one position", GapDetail("CC5", "org:position"));
        _ = jeddah;
    }

    // ── F03: the import applies the form's gates (default company, inactive grade, position dates) ───

    [Fact]
    public async Task TheImportAppliesTheFormsAssignmentRules_NeverFilingIntoTheOldestCompany()
    {
        var tenant = await SeedAsync();
        await using (var seed = _fixture.CreateDb())
        {
            seed.Companies.Add(Company(tenant, "Second Entity"));
            seed.Grades.Add(new Grade { TenantId = tenant, Code = "OLD", Name = "Retired grade", Currency = "SAR", IsActive = false });
            seed.Positions.Add(new Position { TenantId = tenant, Code = "FUTURE-1", Title = "Opens later", EffectiveFrom = new DateOnly(2030, 1, 1) });
            await seed.SaveChangesAsync();
        }

        var csv = "EmployeeCode,FullName,CompanyLegalName,Grade,PositionCode,JoiningDate\n"
                  + "FR1,No Company Named,,,,2024-01-01\n"
                  + "FR2,Retired Grade,Pilot Demo,OLD,,2024-01-01\n"
                  + "FR3,Future Position,Pilot Demo,,FUTURE-1,2024-01-01\n";

        await using var db = _fixture.CreateDb();
        Assert.IsType<OkObjectResult>(await Controller(db, tenant).Import(new EmployeesController.ImportEmployeesRequest(csv), CancellationToken.None));

        await using var verify = _fixture.CreateDb();
        var people = await verify.Employees.IgnoreQueryFilters().Where(e => e.TenantId == tenant).ToDictionaryAsync(e => e.EmployeeCode);
        var gaps = await verify.EmployeeImportGaps.IgnoreQueryFilters().Where(g => g.TenantId == tenant).ToListAsync();
        bool HasGap(string code, string type, string text) =>
            gaps.Any(g => g.EmployeeId == people[code].Id && g.GapType == type && g.Detail.Contains(text));

        Assert.Null(people["FR1"].CompanyId);
        Assert.True(HasGap("FR1", "org:company", "the tenant has 2 companies — none is assumed"));
        Assert.Null(people["FR2"].GradeId);
        Assert.True(HasGap("FR2", "org:grade", "is inactive"));
        Assert.Null(people["FR3"].PositionId);
        Assert.True(HasGap("FR3", "org:position", "is not effective on the joining date"));
    }

    // ── The frontend specs read these REAL responses (route-mocked browser check + unit lane) ──────────

    /// <summary>
    /// Captures the real field-catalog, import-preview and import-refusal bodies into
    /// <c>frontend/unit/fixtures/employeeImportResponses.json</c> and fails when they drift, so the frontend
    /// unit specs and the browser check are always fed what this API actually returns.
    /// Refresh with <c>KYNEX_WRITE_FIXTURES=1</c>.
    /// </summary>
    [Fact]
    public async Task ImportAndFieldCatalogResponses_MatchTheFixtureTheFrontendSpecsUse()
    {
        var tenant = await SeedAsync();
        var web = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var responses = new SortedDictionary<string, JsonElement>(StringComparer.Ordinal);
        async Task Capture(string name, Func<EmployeesController, Task<IActionResult>> call)
        {
            await using var db = _fixture.CreateDb();
            var result = await call(Controller(db, tenant));
            responses[name] = JsonSerializer.SerializeToElement(((ObjectResult)result).Value, web);
        }

        const string head = "EmployeeCode,FullName,CompanyLegalName,Grade,BasicSalary,Currency,JoiningDate,BankName";
        var refused = head + "\nFX1,Fixture One,Pilot Demo,G1,5000,SAR,2024-01-01,Synthetic Bank\nFX2,,Pilot Demo,G1,5000,SAR,2024-01-01,Synthetic Bank\nFX1,Fixture Twin,Pilot Demo,G1,5000,SAR,2024-01-01,Synthetic Bank\n";
        var good = head + "\nFX1,Fixture One,Pilot Demo,G1,5000,SAR,2024-01-01,Synthetic Bank\nFX2,Fixture Two,Pilot Demo,G1,6000,SAR,2024-01-01,Synthetic Bank\n";
        var shifted = head + "\nFX1,Fixture One,Pilot Demo,G1,8,000,SAR,2024-01-01,Synthetic Bank\n";

        await Capture("FieldCatalogSaSaudi", c => c.FieldCatalog(null, "SA", "Saudi", CancellationToken.None));
        await Capture("FieldCatalogSaIndian", c => c.FieldCatalog(null, "SA", "Indian", CancellationToken.None));
        await Capture("PreviewRefused", c => c.ImportPreview(new EmployeesController.ImportEmployeesRequest(refused), CancellationToken.None));
        await Capture("PreviewGood", c => c.ImportPreview(new EmployeesController.ImportEmployeesRequest(good), CancellationToken.None));
        await Capture("ImportRefused", c => c.Import(new EmployeesController.ImportEmployeesRequest(refused), CancellationToken.None));
        await Capture("ImportShapeRefused", c => c.Import(new EmployeesController.ImportEmployeesRequest(shifted), CancellationToken.None));
        await AssertNothingPersisted(tenant);

        var actual = NormaliseIds(JsonSerializer.Serialize(responses, new JsonSerializerOptions
        {
            WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        })) + "\n";
        var path = RepoPath("frontend/unit/fixtures/employeeImportResponses.json");
        if (Environment.GetEnvironmentVariable("KYNEX_WRITE_FIXTURES") == "1")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, actual);
        }
        Assert.True(File.Exists(path), $"Missing {path}. Run this test with KYNEX_WRITE_FIXTURES=1 to create it.");
        Assert.Equal(await File.ReadAllTextAsync(path), actual);
    }

    private static string NormaliseIds(string json)
    {
        var seen = new Dictionary<string, string>();
        return System.Text.RegularExpressions.Regex.Replace(json, "[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}",
            m => seen.TryGetValue(m.Value, out var v) ? v : seen[m.Value] = $"00000000-0000-0000-0000-{seen.Count + 1:D12}");
    }

    private static string RepoPath(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "frontend")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return Path.Combine(dir!.FullName, relative);
    }
}
