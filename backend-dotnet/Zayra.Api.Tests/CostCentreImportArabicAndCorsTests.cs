using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Cors.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Common;
using Zayra.Api.Application.Common.Import;
using Zayra.Api.Application.Organization;
using Zayra.Api.Controllers;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.Audit;
using Zayra.Api.Infrastructure.Organization;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// Cost-centre accounting API gaps, items 4 and 5.
/// Item 4: the browser may read <c>X-Links-To-Clear</c> (the department-import preview's cleared-link count) and <c>X-Journal-Export-Hash</c> cross-origin.
/// Item 5: the import messages the cost-centre work introduced come back in Arabic too. The pattern is a PARALLEL array: <c>ErrorsAr</c>/<c>WarningsAr</c> on
/// <see cref="ImportRowResult"/> (same length and order as <c>Errors</c>/<c>Warnings</c>, an entry is null when that message has no Arabic text, the whole
/// array is null when none has), and <c>warningsAr</c> beside <c>warnings</c> in the employee import response. English stays untouched.
/// </summary>
public class CostCentreImportArabicAndCorsTests
{
    // ── Item 4: CORS ──────────────────────────────────────────────────────────

    [Fact]
    public void TheCorsPolicy_ExposesTheLinksToClearAndJournalHashHeaders()
    {
        var builder = new CorsPolicyBuilder();
        KynexOneCorsPolicy.Apply(builder, ["https://app.example.com"]);
        var policy = builder.Build();
        policy.ExposedHeaders.Should().Contain(["X-Links-To-Clear", "X-Journal-Export-Hash"]);
        (policy.AllowAnyHeader, policy.AllowAnyMethod).Should().Be((true, true));
        policy.Origins.Should().Equal("https://app.example.com");
    }

    [Fact]
    public void ProgramCs_UsesThatPolicy_SoTheHeaderListCannotDriftFromWhatTheHostRegisters()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "backend-dotnet", "Zayra.Api", "Program.cs"))) dir = dir.Parent;
        dir.Should().NotBeNull("the repo root is above the test binaries");
        var program = File.ReadAllText(Path.Combine(dir!.FullName, "backend-dotnet", "Zayra.Api", "Program.cs"));
        program.Should().Contain("KynexOneCorsPolicy.Apply(policy, allowedOrigins)");
    }

    // ── Item 5: the translator ────────────────────────────────────────────────

    [Theory]
    [InlineData("Clears the cost centre link of this existing department.", "سيؤدي هذا إلى مسح ربط مركز التكلفة لهذا القسم الحالي.")]
    [InlineData("Clears the parent department link of this existing department.", "سيؤدي هذا إلى مسح ربط القسم الأب لهذا القسم الحالي.")]
    [InlineData("CostCenterCode 'OPS' not found", "رمز مركز التكلفة 'OPS' غير موجود")]
    [InlineData("Cost center reference 'OPS' not found", "مرجع مركز التكلفة 'OPS' غير موجود")]
    [InlineData("CostCenterCode 'OPS' belongs to Alpha/Beta, not 'Gamma'", "رمز مركز التكلفة 'OPS' يتبع Alpha/Beta وليس 'Gamma'")]
    [InlineData("DepartmentCode 'X' not found — will be ignored", "رمز القسم 'X' غير موجود وسيتم تجاهله")]
    [InlineData("CostCenterCode 'OPS' not found for company 'Alpha' — imported without a cost center.", "رمز مركز التكلفة 'OPS' غير موجود في الشركة 'Alpha' — تم الاستيراد بدون مركز تكلفة.")]
    [InlineData("CostCenterCode 'OPS' supplied but no company could be resolved — imported without a cost center.", "تم تقديم رمز مركز التكلفة 'OPS' لكن تعذر تحديد الشركة — تم الاستيراد بدون مركز تكلفة.")]
    [InlineData("Row 4: CostCenterCode 'OPS' not found for company 'Alpha' — imported without a cost center.", "الصف 4: رمز مركز التكلفة 'OPS' غير موجود في الشركة 'Alpha' — تم الاستيراد بدون مركز تكلفة.")]
    [InlineData("Employee E1: CostCenterCode 'OPS' not found for company 'Alpha' — imported without a cost center.", "الموظف E1: رمز مركز التكلفة 'OPS' غير موجود في الشركة 'Alpha' — تم الاستيراد بدون مركز تكلفة.")]
    public void Translator_KnowsTheCostCentreMessages(string english, string arabic) =>
        ImportMessagesAr.Translate(english).Should().Be(arabic);

    [Fact]
    public void Translator_KnowsTheMultipleMatchWarning_AndTheCodeFormatError_AndThePositionMove()
    {
        ImportMessagesAr.Translate("CostCenterCode 'OPS' matches more than one cost center (a, b) — imported without a cost center.")
            .Should().Be("رمز مركز التكلفة 'OPS' يطابق أكثر من مركز تكلفة (a, b) — تم الاستيراد بدون مركز تكلفة.");
        ImportMessagesAr.Translate("Cost center code 'bad code!': " + "A cost centre code starts with a letter or digit and then uses only letters, digits, dot, dash or underscore, up to 30 characters.")
            .Should().Be("رمز مركز التكلفة 'bad code!': " + CostCentreRuleException.CodeInvalid().MessageAr);
        ImportMessagesAr.Translate("This row moves the position to 'Beta', but its saved cost center, branch belong to another company. Give the new company's cost center, branch in this row.")
            .Should().Be("ينقل هذا الصف المنصب إلى 'Beta'، لكن مركز التكلفة، الفرع المحفوظ يتبع شركة أخرى. أدخل مركز التكلفة، الفرع الخاص بالشركة الجديدة في هذا الصف.");
    }

    [Fact]
    public void Translator_LeavesAnUnknownMessageAlone_AndParallelKeepsTheIndexes()
    {
        ImportMessagesAr.Translate("Some other warning").Should().BeNull();
        ImportMessagesAr.Parallel(["Some other warning"]).Should().BeNull("nothing translated: the array is omitted");
        var p = ImportMessagesAr.Parallel(["Some other warning", "CostCenterCode 'OPS' not found"])!;
        p.Should().HaveCount(2);
        (p[0], p[1]).Should().Be((null, "رمز مركز التكلفة 'OPS' غير موجود"));
    }

    [Fact]
    public void TheRowDto_StillBuildsWithTheOldPositionalShape_AndCarriesTheNewArrays()
    {
        var old = new ImportRowResult(2, "C", "N", ImportRowStatus.Ok, [], []);
        (old.ErrorsAr, old.WarningsAr).Should().Be(((IReadOnlyList<string?>?)null, (IReadOnlyList<string?>?)null));
    }

    // ── Item 5: department import ─────────────────────────────────────────────

    private static async Task<(ZayraDbContext Db, Guid Tenant, Company A)> SeedDepartments()
    {
        var db = new ZayraDbContext(new DbContextOptionsBuilder<ZayraDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var tenant = Guid.NewGuid();
        var a = new Company { TenantId = tenant, LegalNameEn = "Alpha", RegistrationNumber = "A-1", IsActive = true, CountryCode = "SA", DefaultCurrency = "SAR" };
        var cc = new CostCenter { TenantId = tenant, CompanyId = a.Id, Code = "OPS", Name = "Ops" };
        db.AddRange(a, cc, new Department { TenantId = tenant, CompanyId = a.Id, Code = "SALES", NameEn = "Sales", CostCenterId = cc.Id });
        await db.SaveChangesAsync();
        return (db, tenant, a);
    }

    private static DepartmentsController Departments(ZayraDbContext db, Guid tenant) =>
        new(new OrganizationSetupService(db, new AuditService(db)), db)
        { ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = EstablishmentApiTests.User(tenant, "organization.write", "organization.costcenter.manage") } } };

    private const string DeptHeader = "Code,NameEn,NameAr,ParentDepartmentCode,ManagerEmployeeCode,CostCenterCode,IsActive\n";

    [Fact]
    public async Task DepartmentImportPreview_ClearsWarning_AndNotFoundError_ComeBackInArabicToo()
    {
        var (db, tenant, a) = await SeedDepartments();
        var csv = DeptHeader + "SALES,Sales,,,,[CLEAR],true\nNEW,New,,,,NOPE,true\n";
        var preview = (ImportPreviewResult)((OkObjectResult)await Departments(db, tenant).ImportPreview(new DeptImportRequest(csv, a.Id), default)).Value!;

        var clears = preview.Rows.Single(r => r.EntityCode == "SALES");
        clears.Warnings.Should().Equal("Clears the cost centre link of this existing department.");
        clears.WarningsAr.Should().Equal("سيؤدي هذا إلى مسح ربط مركز التكلفة لهذا القسم الحالي.");
        clears.ErrorsAr.Should().BeNull();

        var missing = preview.Rows.Single(r => r.EntityCode == "NEW");
        missing.Errors.Should().Equal("CostCenterCode 'NOPE' not found");
        missing.ErrorsAr.Should().Equal("رمز مركز التكلفة 'NOPE' غير موجود");
    }

    [Fact]
    public async Task DepartmentImportCommit_RefusalToClearUnacknowledged_CarriesArabicMessage()
    {
        var (db, tenant, a) = await SeedDepartments();
        var conflict = (ConflictObjectResult)await Departments(db, tenant).Import(new DeptImportRequest(DeptHeader + "SALES,Sales,,,,[CLEAR],true\n", a.Id), default);
        var json = JsonSerializer.SerializeToElement(conflict.Value, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        json.GetProperty("messageAr").GetString().Should().Contain("مركز التكلفة").And.Contain("1");
    }

    // ── Item 5: cost-centre import ────────────────────────────────────────────

    [Fact]
    public async Task CostCentreImport_DepartmentCodeWarning_HasArabic()
    {
        var (db, tenant, a) = await SeedDepartments();
        var ctl = new CostCentersController(new OrganizationSetupService(db, new AuditService(db)), db)
        { ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = EstablishmentApiTests.User(tenant, "organization.costcenter.manage") } } };
        var result = (ImportPreviewResult)((OkObjectResult)await ctl.ImportPreview(new CostCenterImportRequest("Code,Name,NameAr,DepartmentCode,IsActive\nNEWCC,New,,GHOST,true\n", a.Id), default)).Value!;
        var row = result.Rows.Single();
        row.Warnings.Should().Equal("DepartmentCode 'GHOST' not found — will be ignored");
        row.WarningsAr.Should().Equal("رمز القسم 'GHOST' غير موجود وسيتم تجاهله");
    }

    // ── Item 5: structure import ──────────────────────────────────────────────

    [Fact]
    public async Task StructureImportPreview_CostCentreFormatError_HasArabic_AndStaysParallelToTheEnglish()
    {
        var db = new ZayraDbContext(new DbContextOptionsBuilder<ZayraDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var tenant = Guid.NewGuid();
        db.Companies.Add(new Company { TenantId = tenant, LegalNameEn = "Alpha Trading", RegistrationNumber = "A-1", IsActive = true });
        await db.SaveChangesAsync();
        var ctl = new OrganizationStructureImportController(db, new AuditService(db))
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(new[]
                    {
                        new Claim("tenant_id", tenant.ToString()), new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
                        new Claim("sub", Guid.NewGuid().ToString()), new Claim(ClaimTypes.Role, "Admin"),
                        new Claim("permission", "organization.costcenter.manage"),
                        new Claim(EntityScopeContext.V2ClaimType, JsonSerializer.Serialize(new { v = 2, m = "group", c = Array.Empty<Guid>() })),
                    }, "Test"))
                }
            }
        };
        var result = (OrganizationStructureImportResult)((OkObjectResult)(await ctl.Preview(
            new OrganizationStructureImportRequest(null, null, "CompanyLegalName,Code,Name,IsActive\nAlpha Trading,bad code!,Bad,true\n", null, null, null, null), default)).Result!).Value!;

        var row = result.Rows.Single(r => r.Status == ImportRowStatus.Error);
        row.ErrorsAr.Should().NotBeNull();
        row.ErrorsAr!.Should().HaveSameCount(row.Errors);
        var index = row.Errors.ToList().FindIndex(e => e.StartsWith("Cost center code"));
        index.Should().BeGreaterOrEqualTo(0);
        row.ErrorsAr![index].Should().Be("رمز مركز التكلفة 'bad code!': " + CostCentreRuleException.CodeInvalid().MessageAr);
    }

    // ── Item 5: employee import ───────────────────────────────────────────────

    [Fact]
    public async Task EmployeeImport_CostCentreWarning_HasAParallelArabicEntry()
    {
        var db = new ZayraDbContext(new DbContextOptionsBuilder<ZayraDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var tenant = Guid.NewGuid();
        db.Tenants.Add(new Tenant { Id = tenant, Name = "T", Slug = $"t-{tenant:N}" });
        db.TenantSubscriptions.Add(new TenantSubscription { TenantId = tenant, MaxEmployees = 1000, Plan = "Enterprise", Status = "Active" });
        db.Companies.Add(new Company { TenantId = tenant, LegalNameEn = "Alpha", RegistrationNumber = "A-1", IsActive = true, CountryCode = "SA", DefaultCurrency = "SAR" });
        await db.SaveChangesAsync();
        var ctl = HrmHierarchyTests.BuildImportControllerInternal(db, tenant, "organization.costcenter.manage");

        var result = await ctl.Import(new EmployeesController.ImportEmployeesRequest(
            "EmployeeCode,FullName,CompanyLegalName,DepartmentCode,CostCenterCode,JoiningDate\nE1,Alice,Alpha,,NOPE,2024-01-01\n"), CancellationToken.None);
        var json = JsonSerializer.SerializeToElement(((OkObjectResult)result).Value, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        var warnings = json.GetProperty("warnings").EnumerateArray().Select(x => x.GetString()).ToList();
        var warningsAr = json.GetProperty("warningsAr").EnumerateArray().Select(x => x.GetString()).ToList();
        warningsAr.Should().HaveSameCount(warnings);
        var i = warnings.FindIndex(w => w!.Contains("CostCenterCode 'NOPE'"));
        i.Should().BeGreaterOrEqualTo(0);
        warningsAr[i].Should().StartWith("الصف ").And.EndWith(": رمز مركز التكلفة 'NOPE' غير موجود في الشركة 'Alpha' — تم الاستيراد بدون مركز تكلفة.");
    }
}
