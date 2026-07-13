using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;
using Zayra.Api.Application.Common;
using Zayra.Api.Application.Common.Import;
using Zayra.Api.Controllers;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Models;
using Zayra.Api.Application.Auth;
using System.Text;

namespace Zayra.Api.Tests;

/// <summary>
/// Integration-style tests for the import/export framework.
/// Uses InMemory EF and direct controller invocation (no HTTP stack).
/// </summary>
public class ImportExportEngineTests
{
    // ── Helpers ───────────────────────────────────────────────────────────────

    private static ZayraDbContext CreateDb() =>
        new(new DbContextOptionsBuilder<ZayraDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static (DepartmentsController ctrl, ZayraDbContext db, Guid tenantId) DeptSetup(ZayraDbContext? existingDb = null)
    {
        var db = existingDb ?? CreateDb();
        var tenantId = Guid.NewGuid();
        var ctrl = MakeDeptController(db, tenantId);
        return (ctrl, db, tenantId);
    }

    private static DepartmentsController MakeDeptController(ZayraDbContext db, Guid tenantId)
    {
        // We need IOrganizationSetupService — use a stub via CreateDb's Moq-free approach
        // by constructing with a real service backed by the same in-memory db.
        var svc = new Zayra.Api.Infrastructure.Organization.OrganizationSetupService(db, new Zayra.Api.Infrastructure.Audit.AuditService(db));
        var ctrl = new DepartmentsController(svc, db);
        ctrl.ControllerContext = MakeContext(tenantId);
        return ctrl;
    }

    private static ControllerContext MakeContext(Guid tenantId)
    {
        var claims = new[] { new Claim("tenant_id", tenantId.ToString()), new Claim(System.Security.Claims.ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()) };
        var identity = new ClaimsIdentity(claims, "test");
        var principal = new ClaimsPrincipal(identity);
        return new ControllerContext { HttpContext = new DefaultHttpContext { User = principal } };
    }

    private static string BuildDeptCsv(params string[] dataRows)
    {
        var header = "Code,NameEn,NameAr,ParentDepartmentCode,ManagerEmployeeCode,CostCenterCode,IsActive";
        return string.Join('\n', new[] { header }.Concat(dataRows)) + '\n';
    }

    private static string BuildCompanyCsv(params string[] dataRows)
    {
        var header = "LegalNameEn,LegalNameAr,TradeName,CountryCode,Jurisdiction,RegistrationNumber,TaxNumber,WpsEmployerId,GosiEmployerId,QiwaEstablishmentId,DefaultCurrency,IsActive";
        return string.Join('\n', new[] { header }.Concat(dataRows)) + '\n';
    }

    private static string BuildBranchCsv(params string[] dataRows)
    {
        var header = "CompanyLegalName,Code,NameEn,NameAr,CountryCode,City,AddressLine1,AddressLine2,TimeZoneId,LaborOfficeCode,IsHeadOffice,IsActive";
        return string.Join('\n', new[] { header }.Concat(dataRows)) + '\n';
    }

    private static string BuildDesigCsv(params string[] dataRows)
    {
        var header = "Code,TitleEn,TitleAr,DepartmentCode,JobGrade,IsActive";
        return string.Join('\n', new[] { header }.Concat(dataRows)) + '\n';
    }

    private static string BuildGradeCsv(params string[] dataRows)
    {
        var header = "Code,Name,Level,MinSalary,MaxSalary,IsActive";
        return string.Join('\n', new[] { header }.Concat(dataRows)) + '\n';
    }

    private static string BuildCostCenterCsv(params string[] dataRows)
    {
        var header = "Code,Name,NameAr,DepartmentCode,IsActive";
        return string.Join('\n', new[] { header }.Concat(dataRows)) + '\n';
    }

    private static string BuildApprovalPolicyCsv(string[] policyRows, string[] stepRows)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("Code,Name,WorkflowType,IsDefault,IsActive");
        foreach (var r in policyRows) sb.AppendLine(r);
        sb.AppendLine();
        sb.AppendLine("PolicyCode,StepOrder,StepName,ApproverType,SpecificEmployeeCode,IsFinalStep");
        foreach (var r in stepRows) sb.AppendLine(r);
        return sb.ToString();
    }

    // ── Test 1: Department import preview — valid rows ─────────────────────────

    [Fact]
    public async Task CompanyImport_PersistsStatutoryAndComplianceIdentifiers()
    {
        var db = CreateDb();
        var tenantId = Guid.NewGuid();
        var svc = new Zayra.Api.Infrastructure.Organization.OrganizationSetupService(db, new Zayra.Api.Infrastructure.Audit.AuditService(db));
        var ctrl = new CompaniesController(svc, db);
        ctrl.ControllerContext = MakeContext(tenantId);

        var csv = BuildCompanyCsv("Acme Arabia LLC,أكمي,Acme,SA,KSA-mainland,REG-001,TAX-001,WPS-001,GOSI-001,QIWA-001,SAR,true");
        var result = await ctrl.Import(new CompanyImportRequest(csv), CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        var commit = Assert.IsType<ImportCommitResult>(ok.Value);
        Assert.Equal(1, commit.Created);

        var company = await db.Companies.SingleAsync(c => c.TenantId == tenantId && c.LegalNameEn == "Acme Arabia LLC");
        Assert.Equal("KSA-mainland", company.Jurisdiction);
        Assert.Equal("WPS-001", company.WpsEmployerId);
        Assert.Equal("GOSI-001", company.GosiEmployerId);
        Assert.Equal("QIWA-001", company.QiwaEstablishmentId);
    }

    [Fact]
    public async Task BranchImport_PersistsAddressTimezoneAndLaborOffice()
    {
        var db = CreateDb();
        var tenantId = Guid.NewGuid();
        var company = new Company { TenantId = tenantId, LegalNameEn = "Acme Arabia LLC", CountryCode = "SA", RegistrationNumber = "REG-001", DefaultCurrency = "SAR" };
        db.Companies.Add(company);
        await db.SaveChangesAsync();

        var svc = new Zayra.Api.Infrastructure.Organization.OrganizationSetupService(db, new Zayra.Api.Infrastructure.Audit.AuditService(db));
        var ctrl = new BranchesController(svc, db);
        ctrl.ControllerContext = MakeContext(tenantId);

        var csv = BuildBranchCsv("Acme Arabia LLC,RYD,Riyadh HQ,الرياض,SA,Riyadh,King Fahd Road,Level 9,Asia/Riyadh,LAB-RYD-001,true,true");
        var result = await ctrl.Import(new BranchImportRequest(csv), CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        var commit = Assert.IsType<ImportCommitResult>(ok.Value);
        Assert.Equal(1, commit.Created);

        var branch = await db.Branches.SingleAsync(b => b.TenantId == tenantId && b.Code == "RYD");
        Assert.Equal("King Fahd Road", branch.AddressLine1);
        Assert.Equal("Level 9", branch.AddressLine2);
        Assert.Equal("Asia/Riyadh", branch.TimeZoneId);
        Assert.Equal("LAB-RYD-001", branch.LaborOfficeCode);
    }

    [Fact]
    public async Task DepartmentPreview_ValidRows_ReturnsWouldCreate()
    {
        var db = CreateDb();
        var tenantId = Guid.NewGuid();
        var ctrl = MakeDeptController(db, tenantId);

        var csv = BuildDeptCsv("DEPT-001,Engineering,الهندسة,,,, true", "DEPT-002,Marketing,التسويق,,,, true");
        var result = await ctrl.ImportPreview(new DeptImportRequest(csv), CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        var preview = Assert.IsType<ImportPreviewResult>(ok.Value);
        Assert.Equal(2, preview.Received);
        Assert.Equal(2, preview.WouldCreate);
        Assert.Equal(0, preview.WouldSkip);
    }

    // ── Test 2: Department import preview — duplicate code in batch → error ────

    [Fact]
    public async Task DepartmentPreview_DuplicateCodeInBatch_ReturnsError()
    {
        var db = CreateDb();
        var tenantId = Guid.NewGuid();
        var ctrl = MakeDeptController(db, tenantId);

        var csv = BuildDeptCsv("DEPT-001,Engineering,الهندسة,,,,true", "DEPT-001,Engineering Dupe,الهندسة,,,,true");
        var result = await ctrl.ImportPreview(new DeptImportRequest(csv), CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        var preview = Assert.IsType<ImportPreviewResult>(ok.Value);
        Assert.Equal(2, preview.Received);
        Assert.Equal(1, preview.WouldCreate);
        Assert.Equal(1, preview.WouldSkip);
        var errorRow = preview.Rows.First(r => r.Status == ImportRowStatus.Error);
        Assert.Contains(errorRow.Errors, e => e.Contains("Duplicate"));
    }

    // ── Test 3: Department import commit — creates records in DB ───────────────

    [Fact]
    public async Task DepartmentImport_Commit_CreatesRecords()
    {
        var db = CreateDb();
        var tenantId = Guid.NewGuid();
        var ctrl = MakeDeptController(db, tenantId);

        var csv = BuildDeptCsv("DEPT-A,Alpha,ألفا,,,,true", "DEPT-B,Beta,بيتا,,,,true");
        var result = await ctrl.Import(new DeptImportRequest(csv), CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        var commitResult = Assert.IsType<ImportCommitResult>(ok.Value);
        Assert.Equal(2, commitResult.Created);
        Assert.Equal(0, commitResult.Updated);

        var depts = await db.Departments.Where(d => d.TenantId == tenantId).ToListAsync();
        Assert.Equal(2, depts.Count);
        Assert.Contains(depts, d => d.Code == "DEPT-A" && d.NameEn == "Alpha");
        Assert.Contains(depts, d => d.Code == "DEPT-B" && d.NameEn == "Beta");
    }

    // ── Test 4: Department import commit — updates existing records ────────────

    [Fact]
    public async Task DepartmentImport_Commit_UpdatesExistingRecords()
    {
        var db = CreateDb();
        var tenantId = Guid.NewGuid();

        // Pre-seed an existing department
        db.Departments.Add(new Department { TenantId = tenantId, Code = "DEPT-X", NameEn = "Original", IsActive = true });
        await db.SaveChangesAsync();

        var ctrl = MakeDeptController(db, tenantId);
        var csv = BuildDeptCsv("DEPT-X,Updated Name,تحديث,,,,true");
        var result = await ctrl.Import(new DeptImportRequest(csv), CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        var commitResult = Assert.IsType<ImportCommitResult>(ok.Value);
        Assert.Equal(0, commitResult.Created);
        Assert.Equal(1, commitResult.Updated);

        var dept = await db.Departments.FirstAsync(d => d.TenantId == tenantId && d.Code == "DEPT-X");
        Assert.Equal("Updated Name", dept.NameEn);
    }

    // ── Test 5: Department export — returns correct CSV headers ───────────────

    [Fact]
    public async Task DepartmentExport_ReturnsCorrectCsvHeadersAndData()
    {
        var db = CreateDb();
        var tenantId = Guid.NewGuid();

        db.Departments.AddRange(
            new Department { TenantId = tenantId, Code = "EXP-001", NameEn = "Export Test", IsActive = true },
            new Department { TenantId = tenantId, Code = "EXP-002", NameEn = "Export Test 2", IsActive = false });
        await db.SaveChangesAsync();

        var ctrl = MakeDeptController(db, tenantId);
        var result = await ctrl.Export(CancellationToken.None);

        var fileResult = Assert.IsType<FileContentResult>(result);
        var content = System.Text.Encoding.UTF8.GetString(fileResult.FileContents);
        Assert.Contains("Code,NameEn,NameAr,ParentDepartmentCode,ManagerEmployeeCode,CostCenterCode,IsActive", content);
        Assert.Contains("EXP-001", content);
        Assert.Contains("EXP-002", content);
    }

    // ── Test 6: Designation import — resolves DepartmentCode to DepartmentId ──

    [Fact]
    public async Task DesignationImport_ResolvesDepartmentCodeToId()
    {
        var db = CreateDb();
        var tenantId = Guid.NewGuid();
        var dept = new Department { TenantId = tenantId, Code = "DEPT-HR", NameEn = "HR", IsActive = true };
        db.Departments.Add(dept);
        await db.SaveChangesAsync();

        var svc = new Zayra.Api.Infrastructure.Organization.OrganizationSetupService(db, new Zayra.Api.Infrastructure.Audit.AuditService(db));
        var ctrl = new DesignationsController(svc, db);
        ctrl.ControllerContext = MakeContext(tenantId);

        var csv = BuildDesigCsv("DES-HR-001,HR Manager,مدير الموارد,DEPT-HR,G5,true");
        var result = await ctrl.Import(new DesigImportRequest(csv), CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        var commitResult = Assert.IsType<ImportCommitResult>(ok.Value);
        Assert.Equal(1, commitResult.Created);

        var desig = await db.Designations.FirstAsync(d => d.TenantId == tenantId && d.Code == "DES-HR-001");
        Assert.Equal(dept.Id, desig.DepartmentId);
    }

    // ── Test 7: Grade import — invalid Level value → error ────────────────────

    [Fact]
    public async Task GradeImport_InvalidLevelValue_ReturnsError()
    {
        var db = CreateDb();
        var tenantId = Guid.NewGuid();
        var svc = new Zayra.Api.Infrastructure.Organization.OrganizationSetupService(db, new Zayra.Api.Infrastructure.Audit.AuditService(db));
        var ctrl = new GradesController(svc, db);
        ctrl.ControllerContext = MakeContext(tenantId);

        var csv = BuildGradeCsv("G-BAD,Bad Grade,not-a-number,,,true");
        var result = await ctrl.ImportPreview(new GradeImportRequest(csv), CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        var preview = Assert.IsType<ImportPreviewResult>(ok.Value);
        Assert.Equal(1, preview.WouldSkip);
        Assert.Contains(preview.Rows.First().Errors, e => e.Contains("not a valid integer"));
    }

    // ── Test 8: CostCenter import — unknown DepartmentCode → warning ──────────

    [Fact]
    public async Task CostCenterImport_UnknownDepartmentCode_ReturnsWarningNotError()
    {
        var db = CreateDb();
        var tenantId = Guid.NewGuid();
        var svc = new Zayra.Api.Infrastructure.Organization.OrganizationSetupService(db, new Zayra.Api.Infrastructure.Audit.AuditService(db));
        var ctrl = new CostCentersController(svc, db);
        ctrl.ControllerContext = MakeContext(tenantId);

        var csv = BuildCostCenterCsv("CC-001,Operations,العمليات,NONEXISTENT-DEPT,true");
        var result = await ctrl.ImportPreview(new CostCenterImportRequest(csv), CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        var preview = Assert.IsType<ImportPreviewResult>(ok.Value);
        // Should be created (warning, not error)
        Assert.Equal(1, preview.WouldCreate);
        Assert.Equal(0, preview.WouldSkip);
        var row = preview.Rows.First();
        Assert.Equal(ImportRowStatus.Warning, row.Status);
        Assert.Contains(row.Warnings, w => w.Contains("DepartmentCode") && w.Contains("not found"));
    }

    // ── Test 9: ApprovalPolicy import — creates policy + steps ───────────────

    [Fact]
    public async Task ApprovalPolicyImport_CreatesPolicyAndSteps()
    {
        var db = CreateDb();
        var tenantId = Guid.NewGuid();
        var ctrl = new ApprovalPoliciesController(db);
        ctrl.ControllerContext = MakeContext(tenantId);

        var csv = BuildApprovalPolicyCsv(
            new[] { "POL-001,Leave Default Policy,Leave,true,true" },
            new[] { "POL-001,1,Manager Approval,Manager,,false", "POL-001,2,HR Final,HR,,true" });

        var result = await ctrl.Import(new ApprovalPolicyImportRequest(csv), CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        var commitResult = Assert.IsType<ImportCommitResult>(ok.Value);
        Assert.Equal(1, commitResult.Created);

        var policy = await db.ApprovalPolicies.Include(p => p.Steps).FirstAsync(p => p.TenantId == tenantId);
        Assert.Equal("Leave Default Policy", policy.Name);
        Assert.Equal("Leave", policy.WorkflowType);
        Assert.True(policy.IsDefault);
        Assert.Equal(2, policy.Steps.Count);
        Assert.Contains(policy.Steps, s => s.IsFinalStep && s.ApproverType == "HR");
    }

    // ── Test 10: TenantHrConfig GET — returns safe defaults when not configured

    [Fact]
    public async Task TenantHrConfigGet_NotConfigured_ReturnsSafeDefaults()
    {
        var db = CreateDb();
        var tenantId = Guid.NewGuid();
        var ctrl = new TenantHrConfigController(db);
        ctrl.ControllerContext = MakeContext(tenantId);

        var result = await ctrl.Get(CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        var config = Assert.IsType<TenantHrConfig>(ok.Value);
        // Verify safe defaults
        Assert.True(config.UseDeptHeadApproval);
        Assert.True(config.UseHrFinalApproval);
        Assert.False(config.UseSupervisorBeforeManager);
        Assert.True(config.RequireImportPreviewBeforeCommit);
        Assert.Equal(tenantId, config.TenantId);
        // Should NOT have been persisted
        Assert.Equal(0, await db.TenantHrConfigs.CountAsync());
    }

    // ── Test 11: TenantHrConfig PUT — persists changes, second GET returns updated values

    [Fact]
    public async Task TenantHrConfigPut_PersistsChanges_SecondGetReturnsUpdatedValues()
    {
        var db = CreateDb();
        var tenantId = Guid.NewGuid();
        var ctrl = new TenantHrConfigController(db);
        ctrl.ControllerContext = MakeContext(tenantId);

        // PUT with custom values
        var req = new TenantHrConfigRequest(
            UseDeptHeadApproval: false,
            UseHrFinalApproval: false,
            UseSupervisorBeforeManager: true,
            AllowDottedLineApproval: true,
            AutoCreateDeptOnImport: true,
            AutoCreateDesignationOnImport: false,
            RequireImportPreviewBeforeCommit: false,
            AllowCrossDeptManager: false,
            AllowCrossLocationManager: false,
            RequireCostCenterForPayroll: true,
            RequireGradeForApprovalPolicy: true);

        var putResult = await ctrl.Upsert(req, CancellationToken.None);
        Assert.IsType<OkObjectResult>(putResult);

        // GET again — should see persisted values
        var getResult = await ctrl.Get(CancellationToken.None);
        var ok = Assert.IsType<OkObjectResult>(getResult);
        var config = Assert.IsType<TenantHrConfig>(ok.Value);

        Assert.False(config.UseDeptHeadApproval);
        Assert.True(config.UseSupervisorBeforeManager);
        Assert.True(config.AutoCreateDeptOnImport);
        Assert.False(config.RequireImportPreviewBeforeCommit);
        Assert.True(config.RequireCostCenterForPayroll);

        // Second PUT — should update (not insert again)
        var req2 = req with { UseDeptHeadApproval = true };
        await ctrl.Upsert(req2, CancellationToken.None);
        Assert.Equal(1, await db.TenantHrConfigs.CountAsync());

        var getResult2 = await ctrl.Get(CancellationToken.None);
        var ok2 = Assert.IsType<OkObjectResult>(getResult2);
        var config2 = Assert.IsType<TenantHrConfig>(ok2.Value);
        Assert.True(config2.UseDeptHeadApproval);
    }

    // ── Test 12: Department preview — missing required Code → error ───────────

    [Fact]
    public async Task DepartmentPreview_MissingCode_ReturnsError()
    {
        var db = CreateDb();
        var tenantId = Guid.NewGuid();
        var ctrl = MakeDeptController(db, tenantId);

        // Row with no Code
        var csv = BuildDeptCsv(",No Code Dept,,,,,true");
        var result = await ctrl.ImportPreview(new DeptImportRequest(csv), CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        var preview = Assert.IsType<ImportPreviewResult>(ok.Value);
        Assert.Equal(1, preview.WouldSkip);
        Assert.Contains(preview.Rows.First().Errors, e => e.Contains("Code is required"));
    }

    // ── Test 13: Department import — cross-tenant isolation ───────────────────

    [Fact]
    public async Task DepartmentImport_TenantIsolation_CannotReadOtherTenantData()
    {
        var db = CreateDb();
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();

        // Seed a department for tenant A
        db.Departments.Add(new Department { TenantId = tenantA, Code = "DEPT-A", NameEn = "Tenant A Dept", IsActive = true });
        await db.SaveChangesAsync();

        // Tenant B imports with a code that exists in Tenant A — should CREATE (not update Tenant A's)
        var ctrlB = MakeDeptController(db, tenantB);
        var csv = BuildDeptCsv("DEPT-A,Tenant B Dept,,,,,true");
        var result = await ctrlB.Import(new DeptImportRequest(csv), CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        var commit = Assert.IsType<ImportCommitResult>(ok.Value);
        Assert.Equal(1, commit.Created); // Created for Tenant B, NOT updated Tenant A
        Assert.Equal(0, commit.Updated);

        // Ensure Tenant A's record is untouched
        var tenantADept = await db.Departments.FirstAsync(d => d.TenantId == tenantA && d.Code == "DEPT-A");
        Assert.Equal("Tenant A Dept", tenantADept.NameEn);
    }

    // ── Test 14: Department export — returns only own tenant's records ─────────

    [Fact]
    public async Task DepartmentExport_TenantIsolation_OnlyReturnsTenantOwnRecords()
    {
        var db = CreateDb();
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();

        db.Departments.AddRange(
            new Department { TenantId = tenantA, Code = "A-DEPT", NameEn = "Tenant A Only", IsActive = true },
            new Department { TenantId = tenantB, Code = "B-DEPT", NameEn = "Tenant B Only", IsActive = true });
        await db.SaveChangesAsync();

        var ctrlA = MakeDeptController(db, tenantA);
        var result = await ctrlA.Export(CancellationToken.None);

        var file = Assert.IsType<FileContentResult>(result);
        var csv = Encoding.UTF8.GetString(file.FileContents);

        Assert.Contains("A-DEPT", csv);
        Assert.DoesNotContain("B-DEPT", csv);
        Assert.DoesNotContain("Tenant B Only", csv);
    }

    // ── Test 15: Approval policy — tenant isolation prevents cross-tenant access ─

    [Fact]
    public async Task ApprovalPolicyImport_TenantIsolation_CannotAccessOtherTenantPolicies()
    {
        var db = CreateDb();
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();

        // Seed an approval policy for tenant A
        db.ApprovalPolicies.Add(new ApprovalPolicy
        {
            TenantId = tenantA, Name = "Tenant A Policy", WorkflowType = "Leave",
            IsDefault = true, IsActive = true
        });
        await db.SaveChangesAsync();

        // Tenant B creates a controller and exports — should see zero policies
        var ctrlB = new ApprovalPoliciesController(db);
        ctrlB.ControllerContext = MakeContext(tenantB);
        var result = await ctrlB.Export(CancellationToken.None);

        var file = Assert.IsType<FileContentResult>(result);
        var csv = Encoding.UTF8.GetString(file.FileContents);

        // No data rows from Tenant A — only headers/template rows allowed
        Assert.DoesNotContain("Tenant A Policy", csv);
    }
}
