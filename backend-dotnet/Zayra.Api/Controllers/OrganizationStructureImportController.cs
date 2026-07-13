using System.Text;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using Zayra.Api.Application.Auth;
using Zayra.Api.Application.Common;
using Zayra.Api.Application.Common.Import;
using Zayra.Api.Data;
using Zayra.Api.Models;

namespace Zayra.Api.Controllers;

[ApiController]
[Route("api/setup/organization-structure-import")]
[Authorize(Roles = "Admin,HR Manager")]
public class OrganizationStructureImportController : ControllerBase
{
    private readonly ZayraDbContext _db;
    private readonly IAuditService _audit;

    private static readonly string[] CompaniesHeaders = { "LegalNameEn", "LegalNameAr", "TradeName", "CountryCode", "Jurisdiction", "RegistrationNumber", "TaxNumber", "WpsEmployerId", "GosiEmployerId", "QiwaEstablishmentId", "DefaultCurrency", "IsActive" };
    private static readonly string[] BranchesHeaders = { "CompanyLegalName", "Code", "NameEn", "NameAr", "CountryCode", "City", "AddressLine1", "AddressLine2", "TimeZoneId", "LaborOfficeCode", "IsHeadOffice", "IsActive" };
    private static readonly string[] CostCentersHeaders = { "CompanyLegalName", "Code", "Name", "IsActive" };
    private static readonly string[] DepartmentsHeaders = { "CompanyLegalName", "BranchCode", "Code", "NameEn", "NameAr", "ParentDepartmentCode", "ManagerEmployeeCode", "CostCenterCode", "ApprovedHeadcount", "MonthlyBudgetAmount", "IsActive" };
    private static readonly string[] GradesHeaders = { "Code", "Name", "Band", "Level", "MinSalary", "MidSalary", "MaxSalary", "Currency", "IsActive" };
    private static readonly string[] GradePayHeaders = { "GradeCode", "ComponentCode", "ComponentName", "ComponentType", "CalculationType", "Amount", "Percentage", "Frequency", "IsTaxable", "IsActive" };
    private static readonly string[] DesignationsHeaders = { "Code", "TitleEn", "TitleAr", "DepartmentCode", "GradeCode", "JobGrade", "JobLevel", "JobDescription", "IsManagerRole", "LevelRank", "IsActive" };
    private static readonly string[] PositionsHeaders = { "Code", "Title", "CompanyLegalName", "BranchCode", "DepartmentCode", "CostCenterCode", "DesignationCode", "GradeCode", "Fte", "BudgetedMonthlyCost", "Currency", "Status", "EffectiveFrom", "EffectiveTo" };

    public OrganizationStructureImportController(ZayraDbContext db, IAuditService audit)
    {
        _db = db;
        _audit = audit;
    }

    [HttpGet("template")]
    public IActionResult Template()
    {
        var sb = new StringBuilder();
        void Section(string name, string[] headers, string sample)
        {
            sb.AppendLine($"# {name}");
            sb.AppendLine(string.Join(",", headers.Select(Csv.Escape)));
            sb.AppendLine(sample);
            sb.AppendLine();
        }

        Section("companies", CompaniesHeaders, "Zayra Demo LLC,زيرا ديمو,Zayra,SA,SA-default,CR-001,TAX-001,WPS-001,GOSI-001,QIWA-001,SAR,true");
        Section("branches", BranchesHeaders, "Zayra Demo LLC,HQ,Head Office,المقر الرئيسي,SA,Riyadh,King Fahd Road,Floor 12,Asia/Riyadh,RYD-LAB-001,true,true");
        Section("costCenters", CostCentersHeaders, "Zayra Demo LLC,CC-HR,Human Resources,true");
        Section("departments", DepartmentsHeaders, "Zayra Demo LLC,HQ,HR,Human Resources,الموارد البشرية,,EMP-0001,CC-HR,10,120000,true");
        Section("grades", GradesHeaders, "G3,Grade 3,Professional,3,10000,13500,17000,SAR,true");
        Section("gradePayComponents", GradePayHeaders, "G3,BASIC,Basic Salary,Earning,Fixed,8100,0,Monthly,false,true");
        Section("designations", DesignationsHeaders, "HR_OFF,HR Officer,مسؤول موارد بشرية,HR,G3,G3,Staff,Handles HR operations,false,5,true");
        Section("positions", PositionsHeaders, "POS-HR-001,HR Officer,Zayra Demo LLC,HQ,HR,CC-HR,HR_OFF,G3,1,13500,SAR,Open,2026-01-01,");
        return File(Encoding.UTF8.GetBytes(sb.ToString()), "text/plain", "organization_structure_import_package.txt");
    }

    [HttpPost("batches/dry-run")]
    public async Task<ActionResult<MigrationImportBatchDto>> CreateDryRunBatch([FromBody] OrganizationStructureImportRequest req, CancellationToken ct)
    {
        var tenantId = GetTenantId();
        var payloadJson = StableJson(req);
        var checksum = Checksum(payloadJson);

        var batch = await _db.MigrationImportBatches
            .FirstOrDefaultAsync(x => x.TenantId == tenantId && x.PackageChecksum == checksum && x.Status != "Committed", ct);

        var parsed = ParsePackage(req);
        var validation = await ValidateAsync(tenantId, parsed, this.GetEntityScope(), ct);
        var reconciliation = await BuildReconciliationAsync(tenantId, parsed, validation, ct);
        var errors = validation.Rows.SelectMany(x => x.Errors).ToList();

        if (batch is null)
        {
            batch = new MigrationImportBatch
            {
                TenantId = tenantId,
                PackageChecksum = checksum,
                PackageType = "OrganizationStructure",
                PayloadJson = payloadJson,
                DryRun = true,
                CreatedBy = GetUserId(),
                StartedAtUtc = DateTime.UtcNow
            };
            _db.MigrationImportBatches.Add(batch);
        }

        batch.ExternalBatchId = string.IsNullOrWhiteSpace(batch.ExternalBatchId) ? $"ORG-{DateTime.UtcNow:yyyyMMddHHmmss}-{batch.Id.ToString()[..8]}" : batch.ExternalBatchId;
        batch.Status = validation.HasBlockingErrors ? "DryRunBlocked" : "DryRunPassed";
        batch.CurrentSection = validation.HasBlockingErrors ? "validation" : "ready_to_commit";
        batch.ReceivedRows = validation.Received;
        batch.CreatedRows = CountWouldCreate(validation);
        batch.UpdatedRows = CountWouldUpdate(validation);
        batch.SkippedRows = validation.Rows.Count(x => x.Status == ImportRowStatus.Skipped);
        batch.ErrorRows = validation.Errors;
        batch.ReconciliationJson = StableJson(reconciliation);
        batch.ErrorJson = StableJson(errors);
        batch.ResultJson = StableJson(validation);
        batch.UpdatedAtUtc = DateTime.UtcNow;
        batch.CompletedAtUtc = DateTime.UtcNow;

        await _db.SaveChangesAsync(ct);
        return Ok(ToDto(batch, reconciliation, errors));
    }

    [HttpGet("batches/{id:guid}/reconciliation")]
    public async Task<ActionResult<MigrationImportBatchDto>> GetBatch(Guid id, CancellationToken ct)
    {
        var tenantId = GetTenantId();
        var batch = await _db.MigrationImportBatches.AsNoTracking()
            .FirstOrDefaultAsync(x => x.TenantId == tenantId && x.Id == id, ct);
        if (batch is null) return NotFound();
        return Ok(ToDto(batch));
    }

    [HttpPost("batches/{id:guid}/commit")]
    public async Task<ActionResult<MigrationImportBatchDto>> CommitBatch(Guid id, CancellationToken ct)
    {
        var tenantId = GetTenantId();
        var batch = await _db.MigrationImportBatches.FirstOrDefaultAsync(x => x.TenantId == tenantId && x.Id == id, ct);
        if (batch is null) return NotFound();
        if (batch.Status == "Committed") return Ok(ToDto(batch));
        if (batch.Status != "DryRunPassed")
            return Conflict(ToDto(batch, errors: [$"Batch is {batch.Status}; run a clean dry-run before commit."]));

        var req = JsonSerializer.Deserialize<OrganizationStructureImportRequest>(batch.PayloadJson, JsonOptions);
        if (req is null)
        {
            batch.Status = "Failed";
            batch.CurrentSection = "payload";
            batch.ErrorJson = StableJson(new[] { "Stored import payload could not be read." });
            batch.UpdatedAtUtc = DateTime.UtcNow;
            await _db.SaveChangesAsync(ct);
            return UnprocessableEntity(ToDto(batch));
        }

        batch.Status = "Committing";
        batch.CurrentSection = "organization_structure";
        batch.UpdatedAtUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);

        var commit = await Commit(req, ct);
        if (commit.Result is OkObjectResult ok && ok.Value is OrganizationStructureImportResult result)
        {
            var reconciliation = await BuildReconciliationAsync(tenantId, ParsePackage(req), result, ct);
            batch.Status = "Committed";
            batch.CurrentSection = "committed";
            batch.DryRun = false;
            batch.CreatedRows = result.Applied.Values.Sum();
            batch.UpdatedRows = CountWouldUpdate(result);
            batch.SkippedRows = result.Rows.Count(x => x.Status == ImportRowStatus.Skipped);
            batch.ErrorRows = result.Errors;
            batch.ResultJson = StableJson(result);
            batch.ReconciliationJson = StableJson(reconciliation);
            batch.ErrorJson = "[]";
            batch.CompletedAtUtc = DateTime.UtcNow;
            batch.UpdatedAtUtc = DateTime.UtcNow;
            await _db.SaveChangesAsync(ct);
            return Ok(ToDto(batch, reconciliation));
        }

        var failed = commit.Result is ObjectResult objectResult && objectResult.Value is OrganizationStructureImportResult failedResult
            ? failedResult
            : null;
        var errors = failed?.Rows.SelectMany(x => x.Errors).ToList() ?? ["Commit failed before all sections completed."];
        batch.Status = "Failed";
        batch.CurrentSection = "commit_failed";
        batch.ErrorRows = failed?.Errors ?? errors.Count;
        batch.ErrorJson = StableJson(errors);
        batch.ResultJson = failed is null ? "{}" : StableJson(failed);
        batch.UpdatedAtUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        return UnprocessableEntity(ToDto(batch, errors: errors));
    }

    [HttpPost("preview")]
    public async Task<ActionResult<OrganizationStructureImportResult>> Preview([FromBody] OrganizationStructureImportRequest req, CancellationToken ct)
    {
        var tenantId = GetTenantId();
        var parsed = ParsePackage(req);
        return Ok(await ValidateAsync(tenantId, parsed, this.GetEntityScope(), ct));
    }

    [HttpPost("commit")]
    public async Task<ActionResult<OrganizationStructureImportResult>> Commit([FromBody] OrganizationStructureImportRequest req, CancellationToken ct)
    {
        var tenantId = GetTenantId();
        var parsed = ParsePackage(req);
        var validation = await ValidateAsync(tenantId, parsed, this.GetEntityScope(), ct);
        if (validation.HasBlockingErrors)
            return UnprocessableEntity(validation);

        await using var tx = await _db.Database.BeginTransactionAsync(ct);
        var counts = new Dictionary<string, int>();
        void Bump(string key) => counts[key] = counts.GetValueOrDefault(key) + 1;

        var companies = await _db.Companies.Where(x => x.TenantId == tenantId && !x.IsDeleted).ToDictionaryAsync(x => x.LegalNameEn.ToUpperInvariant(), ct);
        foreach (var row in parsed.Companies)
        {
            var name = Val(row, "LegalNameEn");
            var active = Bool(row, "IsActive", true);
            if (companies.TryGetValue(name.ToUpperInvariant(), out var company))
            {
                company.LegalNameAr = Val(row, "LegalNameAr");
                company.TradeName = Val(row, "TradeName");
                company.CountryCode = Val(row, "CountryCode");
                company.Jurisdiction = Val(row, "Jurisdiction");
                company.RegistrationNumber = Val(row, "RegistrationNumber");
                company.TaxNumber = Val(row, "TaxNumber");
                company.WpsEmployerId = Val(row, "WpsEmployerId");
                company.GosiEmployerId = Val(row, "GosiEmployerId");
                company.QiwaEstablishmentId = Val(row, "QiwaEstablishmentId");
                company.DefaultCurrency = Val(row, "DefaultCurrency", "SAR");
                company.IsActive = active;
                company.UpdatedAtUtc = DateTime.UtcNow;
            }
            else
            {
                company = new Company
                {
                    TenantId = tenantId,
                    LegalNameEn = name,
                    LegalNameAr = Val(row, "LegalNameAr"),
                    TradeName = Val(row, "TradeName"),
                    CountryCode = Val(row, "CountryCode"),
                    Jurisdiction = Val(row, "Jurisdiction"),
                    RegistrationNumber = Val(row, "RegistrationNumber"),
                    TaxNumber = Val(row, "TaxNumber"),
                    WpsEmployerId = Val(row, "WpsEmployerId"),
                    GosiEmployerId = Val(row, "GosiEmployerId"),
                    QiwaEstablishmentId = Val(row, "QiwaEstablishmentId"),
                    DefaultCurrency = Val(row, "DefaultCurrency", "SAR"),
                    IsActive = active,
                    CreatedBy = GetUserId()
                };
                _db.Companies.Add(company);
                companies[name.ToUpperInvariant()] = company;
                Bump("companies");
            }
        }

        var branches = await _db.Branches.Where(x => x.TenantId == tenantId && !x.IsDeleted).ToDictionaryAsync(x => x.Code.ToUpperInvariant(), ct);
        foreach (var row in parsed.Branches)
        {
            var code = Val(row, "Code");
            var company = companies[Val(row, "CompanyLegalName").ToUpperInvariant()];
            if (branches.TryGetValue(code.ToUpperInvariant(), out var branch))
            {
                branch.CompanyId = company.Id;
                branch.NameEn = Val(row, "NameEn");
                branch.NameAr = Val(row, "NameAr");
                branch.CountryCode = Val(row, "CountryCode", company.CountryCode);
                branch.City = Val(row, "City");
                branch.AddressLine1 = Val(row, "AddressLine1");
                branch.AddressLine2 = Val(row, "AddressLine2");
                branch.TimeZoneId = Val(row, "TimeZoneId", "Asia/Riyadh");
                branch.LaborOfficeCode = Val(row, "LaborOfficeCode");
                branch.IsHeadOffice = Bool(row, "IsHeadOffice", false);
                branch.IsActive = Bool(row, "IsActive", true);
                branch.UpdatedAtUtc = DateTime.UtcNow;
            }
            else
            {
                branch = new Branch
                {
                    TenantId = tenantId,
                    CompanyId = company.Id,
                    Code = code,
                    NameEn = Val(row, "NameEn"),
                    NameAr = Val(row, "NameAr"),
                    CountryCode = Val(row, "CountryCode", company.CountryCode),
                    City = Val(row, "City"),
                    AddressLine1 = Val(row, "AddressLine1"),
                    AddressLine2 = Val(row, "AddressLine2"),
                    TimeZoneId = Val(row, "TimeZoneId", "Asia/Riyadh"),
                    LaborOfficeCode = Val(row, "LaborOfficeCode"),
                    IsHeadOffice = Bool(row, "IsHeadOffice", false),
                    IsActive = Bool(row, "IsActive", true),
                    CreatedBy = GetUserId()
                };
                _db.Branches.Add(branch);
                branches[code.ToUpperInvariant()] = branch;
                Bump("branches");
            }
        }

        var costCenters = await _db.CostCenters.Where(x => x.TenantId == tenantId && !x.IsDeleted).ToDictionaryAsync(x => x.Code.ToUpperInvariant(), ct);
        foreach (var row in parsed.CostCenters)
        {
            var code = Val(row, "Code");
            var company = companies[Val(row, "CompanyLegalName").ToUpperInvariant()];
            if (costCenters.TryGetValue(code.ToUpperInvariant(), out var cc))
            {
                cc.CompanyId = company.Id;
                cc.Name = Val(row, "Name");
                cc.IsActive = Bool(row, "IsActive", true);
                cc.UpdatedAtUtc = DateTime.UtcNow;
            }
            else
            {
                cc = new CostCenter { TenantId = tenantId, CompanyId = company.Id, Code = code, Name = Val(row, "Name"), IsActive = Bool(row, "IsActive", true), CreatedBy = GetUserId() };
                _db.CostCenters.Add(cc);
                costCenters[code.ToUpperInvariant()] = cc;
                Bump("costCenters");
            }
        }

        var grades = await _db.Grades.Where(x => x.TenantId == tenantId && !x.IsDeleted).ToDictionaryAsync(x => x.Code.ToUpperInvariant(), ct);
        foreach (var row in parsed.Grades)
        {
            var code = Val(row, "Code");
            if (!grades.TryGetValue(code.ToUpperInvariant(), out var grade))
            {
                grade = new Grade { TenantId = tenantId, Code = code, CreatedBy = GetUserId() };
                _db.Grades.Add(grade);
                grades[code.ToUpperInvariant()] = grade;
                Bump("grades");
            }
            grade.Name = Val(row, "Name");
            grade.Band = Val(row, "Band");
            grade.Level = Int(row, "Level");
            grade.MinSalary = Dec(row, "MinSalary");
            grade.MidSalary = Dec(row, "MidSalary");
            grade.MaxSalary = Dec(row, "MaxSalary");
            grade.Currency = Val(row, "Currency", "SAR");
            grade.IsActive = Bool(row, "IsActive", true);
            grade.UpdatedAtUtc = DateTime.UtcNow;
        }

        var departments = await _db.Departments.Where(x => x.TenantId == tenantId && !x.IsDeleted).ToDictionaryAsync(x => x.Code.ToUpperInvariant(), ct);
        var employeesByCode = await _db.Employees.Where(x => x.TenantId == tenantId && !x.IsDeleted).ToDictionaryAsync(x => x.EmployeeCode.ToUpperInvariant(), ct);
        foreach (var row in parsed.Departments)
        {
            var code = Val(row, "Code");
            if (!departments.TryGetValue(code.ToUpperInvariant(), out var department))
            {
                department = new Department { TenantId = tenantId, Code = code, CreatedBy = GetUserId() };
                _db.Departments.Add(department);
                departments[code.ToUpperInvariant()] = department;
                Bump("departments");
            }
            department.NameEn = Val(row, "NameEn");
            department.NameAr = Val(row, "NameAr");
            var departmentCompany = string.IsNullOrWhiteSpace(Val(row, "CompanyLegalName")) ? null : companies[Val(row, "CompanyLegalName").ToUpperInvariant()];
            var departmentBranch = string.IsNullOrWhiteSpace(Val(row, "BranchCode")) ? null : branches[Val(row, "BranchCode").ToUpperInvariant()];
            var departmentCostCenter = string.IsNullOrWhiteSpace(Val(row, "CostCenterCode")) ? null : costCenters[Val(row, "CostCenterCode").ToUpperInvariant()];
            if (departmentCompany is not null && departmentBranch is not null && departmentBranch.CompanyId != departmentCompany.Id)
                throw new InvalidOperationException($"Department '{code}' branch does not belong to '{departmentCompany.LegalNameEn}'.");
            if (departmentCompany is not null && departmentCostCenter is not null && departmentCostCenter.CompanyId != departmentCompany.Id)
                throw new InvalidOperationException($"Department '{code}' cost center does not belong to '{departmentCompany.LegalNameEn}'.");
            department.BranchId = departmentBranch?.Id;
            department.CostCenterId = departmentCostCenter?.Id;
            department.ManagerEmployeeId = string.IsNullOrWhiteSpace(Val(row, "ManagerEmployeeCode")) ? null : employeesByCode[Val(row, "ManagerEmployeeCode").ToUpperInvariant()].Id;
            department.ApprovedHeadcount = Int(row, "ApprovedHeadcount");
            department.MonthlyBudgetAmount = Dec(row, "MonthlyBudgetAmount");
            department.IsActive = Bool(row, "IsActive", true);
            department.UpdatedAtUtc = DateTime.UtcNow;
        }
        foreach (var row in parsed.Departments)
        {
            var parentCode = Val(row, "ParentDepartmentCode");
            if (!string.IsNullOrWhiteSpace(parentCode))
                departments[Val(row, "Code").ToUpperInvariant()].ParentDepartmentId = departments[parentCode.ToUpperInvariant()].Id;
        }

        await _db.SaveChangesAsync(ct);

        foreach (var row in parsed.GradePayComponents)
        {
            var grade = grades[Val(row, "GradeCode").ToUpperInvariant()];
            var code = Val(row, "ComponentCode");
            var exists = await _db.GradePayScaleComponents.AnyAsync(x => x.TenantId == tenantId && x.GradeId == grade.Id && x.ComponentCode == code, ct);
            if (exists) continue;
            _db.GradePayScaleComponents.Add(new GradePayScaleComponent
            {
                TenantId = tenantId,
                GradeId = grade.Id,
                ComponentCode = code,
                ComponentName = Val(row, "ComponentName"),
                ComponentType = Val(row, "ComponentType", "Earning"),
                CalculationType = Val(row, "CalculationType", "Fixed"),
                Amount = Dec(row, "Amount"),
                Percentage = Dec(row, "Percentage"),
                Frequency = Val(row, "Frequency", "Monthly"),
                IsTaxable = Bool(row, "IsTaxable", false),
                IsActive = Bool(row, "IsActive", true)
            });
            Bump("gradePayComponents");
        }

        var designations = await _db.Designations.Where(x => x.TenantId == tenantId && !x.IsDeleted).ToDictionaryAsync(x => x.Code.ToUpperInvariant(), ct);
        foreach (var row in parsed.Designations)
        {
            var code = Val(row, "Code");
            if (!designations.TryGetValue(code.ToUpperInvariant(), out var designation))
            {
                designation = new Designation { TenantId = tenantId, Code = code, CreatedBy = GetUserId() };
                _db.Designations.Add(designation);
                designations[code.ToUpperInvariant()] = designation;
                Bump("designations");
            }
            designation.TitleEn = Val(row, "TitleEn");
            designation.TitleAr = Val(row, "TitleAr");
            designation.DepartmentId = string.IsNullOrWhiteSpace(Val(row, "DepartmentCode")) ? null : departments[Val(row, "DepartmentCode").ToUpperInvariant()].Id;
            var gradeCode = Val(row, "GradeCode", Val(row, "JobGrade"));
            designation.GradeId = string.IsNullOrWhiteSpace(gradeCode) ? null : grades[gradeCode.ToUpperInvariant()].Id;
            designation.JobGrade = gradeCode;
            designation.JobLevel = Val(row, "JobLevel");
            designation.JobDescription = Val(row, "JobDescription");
            designation.IsManagerRole = Bool(row, "IsManagerRole", false);
            designation.LevelRank = Int(row, "LevelRank", 1);
            designation.IsActive = Bool(row, "IsActive", true);
            designation.UpdatedAtUtc = DateTime.UtcNow;
        }

        await _db.SaveChangesAsync(ct);

        var positions = await _db.Positions.Where(x => x.TenantId == tenantId && !x.IsDeleted).ToDictionaryAsync(x => x.Code.ToUpperInvariant(), ct);
        foreach (var row in parsed.Positions)
        {
            var code = Val(row, "Code");
            if (!positions.TryGetValue(code.ToUpperInvariant(), out var position))
            {
                position = new Position { TenantId = tenantId, Code = code, CreatedBy = GetUserId() };
                _db.Positions.Add(position);
                positions[code.ToUpperInvariant()] = position;
                Bump("positions");
            }
            position.Title = Val(row, "Title");
            position.CompanyId = string.IsNullOrWhiteSpace(Val(row, "CompanyLegalName")) ? null : companies[Val(row, "CompanyLegalName").ToUpperInvariant()].Id;
            position.BranchId = string.IsNullOrWhiteSpace(Val(row, "BranchCode")) ? null : branches[Val(row, "BranchCode").ToUpperInvariant()].Id;
            position.DepartmentId = string.IsNullOrWhiteSpace(Val(row, "DepartmentCode")) ? null : departments[Val(row, "DepartmentCode").ToUpperInvariant()].Id;
            position.CostCenterId = string.IsNullOrWhiteSpace(Val(row, "CostCenterCode")) ? null : costCenters[Val(row, "CostCenterCode").ToUpperInvariant()].Id;
            position.DesignationId = string.IsNullOrWhiteSpace(Val(row, "DesignationCode")) ? null : designations[Val(row, "DesignationCode").ToUpperInvariant()].Id;
            position.GradeId = string.IsNullOrWhiteSpace(Val(row, "GradeCode")) ? null : grades[Val(row, "GradeCode").ToUpperInvariant()].Id;
            position.Fte = Dec(row, "Fte", 1m);
            position.BudgetedMonthlyCost = Dec(row, "BudgetedMonthlyCost");
            position.Currency = Val(row, "Currency", "SAR").ToUpperInvariant();
            position.Status = Val(row, "Status", PositionStatuses.Open);
            position.EffectiveFrom = Date(row, "EffectiveFrom", DateOnly.FromDateTime(DateTime.UtcNow));
            position.EffectiveTo = DateOrNull(row, "EffectiveTo");
            position.UpdatedAtUtc = DateTime.UtcNow;
            position.UpdatedBy = GetUserId();
        }

        await _db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        await _audit.WriteAsync(
            "setup.organization_structure_import_committed",
            "OrganizationStructureImport",
            "bulk",
            BuildContext(tenantId),
            JsonSerializer.Serialize(new { received = validation.Received, warnings = validation.Warnings, applied = counts }),
            ct);
        return Ok(validation with { Applied = counts, Committed = true });
    }

    private async Task<OrganizationStructureImportResult> ValidateAsync(Guid tenantId, ParsedOrgPackage parsed, EntityScopeContext scope, CancellationToken ct)
    {
        var rows = new List<ImportRowResult>();
        var existingCompanies = await _db.Companies.AsNoTracking()
            .Where(x => x.TenantId == tenantId && !x.IsDeleted)
            .Select(x => new { x.Id, x.LegalNameEn })
            .ToListAsync(ct);
        var companyNames = existingCompanies.Select(x => x.LegalNameEn).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var companyIdsByName = existingCompanies.ToDictionary(x => x.LegalNameEn, x => x.Id, StringComparer.OrdinalIgnoreCase);
        var branchRows = await _db.Branches.AsNoTracking()
            .Where(x => x.TenantId == tenantId && !x.IsDeleted)
            .Select(x => new { x.Code, x.CompanyId })
            .ToListAsync(ct);
        var branchCodes = branchRows.Select(x => x.Code).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var branchCompanyByCode = branchRows.ToDictionary(x => x.Code, x => (Guid?)x.CompanyId, StringComparer.OrdinalIgnoreCase);
        var costCenterRows = await _db.CostCenters.AsNoTracking()
            .Where(x => x.TenantId == tenantId && !x.IsDeleted)
            .Select(x => new { x.Code, x.CompanyId })
            .ToListAsync(ct);
        var costCenterCodes = costCenterRows.Select(x => x.Code).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var costCenterCompanyByCode = costCenterRows.ToDictionary(x => x.Code, x => (Guid?)x.CompanyId, StringComparer.OrdinalIgnoreCase);
        var departmentCodes = (await _db.Departments.AsNoTracking()
            .Where(x => x.TenantId == tenantId && !x.IsDeleted)
            .Select(x => x.Code)
            .ToListAsync(ct)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var gradeCodes = (await _db.Grades.AsNoTracking().Where(x => x.TenantId == tenantId && !x.IsDeleted).Select(x => x.Code).ToListAsync(ct)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var employeeCodes = (await _db.Employees.AsNoTracking().Where(x => x.TenantId == tenantId && !x.IsDeleted).Select(x => x.EmployeeCode).ToListAsync(ct)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var designationCodes = (await _db.Designations.AsNoTracking().Where(x => x.TenantId == tenantId && !x.IsDeleted).Select(x => x.Code).ToListAsync(ct)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var positionCodes = (await _db.Positions.AsNoTracking().Where(x => x.TenantId == tenantId && !x.IsDeleted).Select(x => x.Code).ToListAsync(ct)).ToHashSet(StringComparer.OrdinalIgnoreCase);

        AddScopeRows(parsed, scope, companyNames, companyIdsByName, branchCompanyByCode, costCenterCompanyByCode, rows);

        AddRows("companies", parsed.Companies, "LegalNameEn", "LegalNameEn", required: ["LegalNameEn", "CountryCode", "DefaultCurrency"], known: companyNames, rows);
        Merge(companyNames, parsed.Companies.Select(x => Val(x, "LegalNameEn")));
        AddRows("branches", parsed.Branches, "Code", "NameEn", required: ["CompanyLegalName", "Code", "NameEn"], known: branchCodes, rows,
            refs: [("CompanyLegalName", companyNames, "Company")]);
        Merge(branchCodes, parsed.Branches.Select(x => Val(x, "Code")));
        AddRows("costCenters", parsed.CostCenters, "Code", "Name", required: ["CompanyLegalName", "Code", "Name"], known: costCenterCodes, rows,
            refs: [("CompanyLegalName", companyNames, "Company")]);
        Merge(costCenterCodes, parsed.CostCenters.Select(x => Val(x, "Code")));
        AddRows("grades", parsed.Grades, "Code", "Name", required: ["Code", "Name"], known: gradeCodes, rows);
        AddGradeSanityRows(parsed.Grades, rows);
        Merge(gradeCodes, parsed.Grades.Select(x => Val(x, "Code")));
        AddRows("departments", parsed.Departments, "Code", "NameEn", required: ["Code", "NameEn"], known: departmentCodes, rows,
            refs: [("CompanyLegalName", companyNames, "Company"), ("BranchCode", branchCodes, "Branch"), ("CostCenterCode", costCenterCodes, "Cost center"), ("ParentDepartmentCode", departmentCodes, "Parent department"), ("ManagerEmployeeCode", employeeCodes, "Manager employee")]);
        AddDepartmentCompanyConsistencyRows(parsed.Departments, parsed.Branches, parsed.CostCenters, rows);
        Merge(departmentCodes, parsed.Departments.Select(x => Val(x, "Code")));
        AddRows("gradePayComponents", parsed.GradePayComponents, "ComponentCode", "ComponentName", required: ["GradeCode", "ComponentCode", "ComponentName"], known: new HashSet<string>(StringComparer.OrdinalIgnoreCase), rows,
            refs: [("GradeCode", gradeCodes, "Grade")]);
        AddGradePayComponentSanityRows(parsed.GradePayComponents, rows);
        AddRows("designations", parsed.Designations, "Code", "TitleEn", required: ["Code", "TitleEn"], known: new HashSet<string>(StringComparer.OrdinalIgnoreCase), rows,
            refs: [("DepartmentCode", departmentCodes, "Department"), ("GradeCode", gradeCodes, "Grade")]);
        Merge(designationCodes, parsed.Designations.Select(x => Val(x, "Code")));
        AddRows("positions", parsed.Positions, "Code", "Title", required: ["Code", "Title"], known: positionCodes, rows,
            refs: [("CompanyLegalName", companyNames, "Company"), ("BranchCode", branchCodes, "Branch"), ("DepartmentCode", departmentCodes, "Department"), ("CostCenterCode", costCenterCodes, "Cost center"), ("DesignationCode", designationCodes, "Designation"), ("GradeCode", gradeCodes, "Grade")]);
        AddPositionSanityRows(parsed.Positions, rows);

        var parentMap = parsed.Departments
            .Where(x => !string.IsNullOrWhiteSpace(Val(x, "Code")) && !string.IsNullOrWhiteSpace(Val(x, "ParentDepartmentCode")))
            .ToDictionary(x => Val(x, "Code"), x => Val(x, "ParentDepartmentCode"), StringComparer.OrdinalIgnoreCase);
        foreach (var start in parentMap.Keys)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { start };
            var cursor = parentMap[start];
            while (!string.IsNullOrWhiteSpace(cursor) && parentMap.TryGetValue(cursor, out var next))
            {
                if (!seen.Add(cursor))
                {
                    rows.Add(new ImportRowResult(0, start, "Department hierarchy", ImportRowStatus.Error, [$"ParentDepartmentCode creates a cycle at '{cursor}'"], []));
                    break;
                }
                cursor = next;
            }
        }

        var errors = rows.Count(x => x.Status == ImportRowStatus.Error);
        return new OrganizationStructureImportResult(
            Received: parsed.TotalRows,
            Errors: errors,
            Warnings: rows.Count(x => x.Status == ImportRowStatus.Warning),
            Rows: rows,
            HasBlockingErrors: errors > 0,
            Committed: false,
            Applied: new Dictionary<string, int>());
    }

    private static void AddScopeRows(
        ParsedOrgPackage parsed,
        EntityScopeContext scope,
        HashSet<string> companyNames,
        Dictionary<string, Guid> companyIdsByName,
        Dictionary<string, Guid?> branchCompanyByCode,
        Dictionary<string, Guid?> costCenterCompanyByCode,
        List<ImportRowResult> rows)
    {
        if (scope.IsGroupLevel) return;

        if (parsed.Companies.Count > 0 || parsed.Grades.Count > 0 || parsed.GradePayComponents.Count > 0 || parsed.Designations.Any(x => string.IsNullOrWhiteSpace(Val(x, "DepartmentCode"))))
        {
            rows.Add(new ImportRowResult(0, "scope:group-master-data", "Group master data import", ImportRowStatus.Error,
                ["Only group-scope HR/Admin users can import companies, grades, grade pay components, or unbound designations."], []));
        }

        foreach (var row in parsed.Branches)
            AddCompanyAccessError("branches", Val(row, "Code"), Val(row, "NameEn"), Val(row, "CompanyLegalName"), scope, companyIdsByName, rows);

        foreach (var row in parsed.CostCenters)
            AddCompanyAccessError("costCenters", Val(row, "Code"), Val(row, "Name"), Val(row, "CompanyLegalName"), scope, companyIdsByName, rows);

        foreach (var row in parsed.Positions)
            AddCompanyAccessError("positions", Val(row, "Code"), Val(row, "Title"), Val(row, "CompanyLegalName"), scope, companyIdsByName, rows);

        foreach (var row in parsed.Departments)
        {
            var companyName = Val(row, "CompanyLegalName");
            var branchCode = Val(row, "BranchCode");
            var costCenterCode = Val(row, "CostCenterCode");
            Guid? inferredCompany = null;
            if (!string.IsNullOrWhiteSpace(companyName) && companyIdsByName.TryGetValue(companyName, out var directCompany))
                inferredCompany = directCompany;
            else if (!string.IsNullOrWhiteSpace(branchCode) && branchCompanyByCode.TryGetValue(branchCode, out var branchCompany))
                inferredCompany = branchCompany;
            else if (!string.IsNullOrWhiteSpace(costCenterCode) && costCenterCompanyByCode.TryGetValue(costCenterCode, out var ccCompany))
                inferredCompany = ccCompany;

            if (inferredCompany is null)
            {
                rows.Add(new ImportRowResult(0, $"departments:{Val(row, "Code")}", Val(row, "NameEn"), ImportRowStatus.Error,
                    ["CompanyLegalName, BranchCode, or CostCenterCode must resolve to an accessible company for company-scoped import."], []));
                continue;
            }
            if (!scope.CanAccessCompany(inferredCompany))
                rows.Add(new ImportRowResult(0, $"departments:{Val(row, "Code")}", Val(row, "NameEn"), ImportRowStatus.Error,
                    ["Department belongs to a company outside the caller's entity scope."], []));
        }
    }

    private static void AddCompanyAccessError(
        string section,
        string code,
        string name,
        string companyName,
        EntityScopeContext scope,
        Dictionary<string, Guid> companyIdsByName,
        List<ImportRowResult> rows)
    {
        if (string.IsNullOrWhiteSpace(companyName) || !companyIdsByName.TryGetValue(companyName, out var companyId)) return;
        if (scope.CanAccessCompany(companyId)) return;
        rows.Add(new ImportRowResult(0, $"{section}:{code}", name, ImportRowStatus.Error,
            [$"{section} row references company '{companyName}' outside the caller's entity scope."], []));
    }

    private static void AddRows(string section, IReadOnlyList<Dictionary<string, string>> source, string codeKey, string nameKey, string[] required, HashSet<string> known, List<ImportRowResult> rows, (string Key, HashSet<string> Known, string Label)[]? refs = null)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < source.Count; i++)
        {
            var row = source[i];
            var code = Val(row, codeKey);
            var errors = required.Where(key => string.IsNullOrWhiteSpace(Val(row, key))).Select(key => $"{key} is required").ToList();
            var warnings = new List<string>();
            if (!string.IsNullOrWhiteSpace(code) && !seen.Add(code)) errors.Add($"Duplicate {codeKey} '{code}' in {section}");
            if (!string.IsNullOrWhiteSpace(code) && known.Contains(code))
                warnings.Add($"{section} record '{code}' already exists and will be updated");
            foreach (var (key, knownRefs, label) in refs ?? [])
            {
                var value = Val(row, key);
                if (!string.IsNullOrWhiteSpace(value) && !knownRefs.Contains(value))
                    errors.Add($"{label} reference '{value}' not found");
            }
            var status = errors.Count > 0 ? ImportRowStatus.Error : warnings.Count > 0 ? ImportRowStatus.Warning : ImportRowStatus.Ok;
            rows.Add(new ImportRowResult(i + 2, $"{section}:{code}", Val(row, nameKey), status, errors, warnings));
        }
    }

    private static void AddGradeSanityRows(IReadOnlyList<Dictionary<string, string>> source, List<ImportRowResult> rows)
    {
        for (var i = 0; i < source.Count; i++)
        {
            var row = source[i];
            var code = Val(row, "Code");
            var min = Dec(row, "MinSalary");
            var mid = Dec(row, "MidSalary");
            var max = Dec(row, "MaxSalary");
            var errors = new List<string>();
            var warnings = new List<string>();
            if (min < 0 || mid < 0 || max < 0) errors.Add("Salary range cannot contain negative values");
            if (max > 0 && min > max) errors.Add("MinSalary cannot exceed MaxSalary");
            if (mid > 0 && min > 0 && mid < min) warnings.Add("MidSalary is below MinSalary");
            if (mid > 0 && max > 0 && mid > max) warnings.Add("MidSalary is above MaxSalary");
            if (min == 0 && max == 0) warnings.Add("Grade has no salary range; employee salary eligibility cannot be enforced");
            if (errors.Count == 0 && warnings.Count == 0) continue;
            rows.Add(new ImportRowResult(i + 2, $"grades:{code}", Val(row, "Name"), errors.Count > 0 ? ImportRowStatus.Error : ImportRowStatus.Warning, errors, warnings));
        }
    }

    private static void AddGradePayComponentSanityRows(IReadOnlyList<Dictionary<string, string>> source, List<ImportRowResult> rows)
    {
        var totals = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < source.Count; i++)
        {
            var row = source[i];
            var gradeCode = Val(row, "GradeCode");
            var code = Val(row, "ComponentCode");
            var calculationType = Val(row, "CalculationType");
            var amount = Dec(row, "Amount");
            var percentage = Dec(row, "Percentage");
            var errors = new List<string>();
            var warnings = new List<string>();
            if (string.Equals(calculationType, "PercentOfBasic", StringComparison.OrdinalIgnoreCase) && percentage <= 0)
                errors.Add("PercentOfBasic components require a positive Percentage");
            if (string.Equals(calculationType, "Fixed", StringComparison.OrdinalIgnoreCase) && amount <= 0)
                warnings.Add("Fixed pay component has no positive Amount");
            if (percentage < 0 || percentage > 100) errors.Add("Percentage must be between 0 and 100");
            if (string.Equals(calculationType, "PercentOfBasic", StringComparison.OrdinalIgnoreCase))
                totals[gradeCode] = totals.GetValueOrDefault(gradeCode) + percentage;
            if (errors.Count == 0 && warnings.Count == 0) continue;
            rows.Add(new ImportRowResult(i + 2, $"gradePayComponents:{gradeCode}/{code}", Val(row, "ComponentName"), errors.Count > 0 ? ImportRowStatus.Error : ImportRowStatus.Warning, errors, warnings));
        }

        foreach (var (gradeCode, total) in totals.Where(x => x.Value > 100m))
        {
            rows.Add(new ImportRowResult(0, $"gradePayComponents:{gradeCode}", "Component percentage total", ImportRowStatus.Warning, [],
                [$"PercentOfBasic components total {total:0.##}% for grade {gradeCode}; confirm this is intentional"]));
        }
    }

    private static void AddPositionSanityRows(IReadOnlyList<Dictionary<string, string>> source, List<ImportRowResult> rows)
    {
        for (var i = 0; i < source.Count; i++)
        {
            var row = source[i];
            var code = Val(row, "Code");
            var errors = new List<string>();
            var fte = Dec(row, "Fte", 1m);
            var budget = Dec(row, "BudgetedMonthlyCost");
            if (fte <= 0 || fte > 100) errors.Add("Fte must be greater than zero and no more than 100");
            if (budget < 0) errors.Add("BudgetedMonthlyCost cannot be negative");
            if (!string.IsNullOrWhiteSpace(Val(row, "EffectiveTo")) && DateOrNull(row, "EffectiveTo") < Date(row, "EffectiveFrom", DateOnly.FromDateTime(DateTime.UtcNow)))
                errors.Add("EffectiveTo cannot be before EffectiveFrom");
            var status = Val(row, "Status", PositionStatuses.Open);
            if (!new[] { PositionStatuses.Open, PositionStatuses.Filled, PositionStatuses.Frozen, PositionStatuses.Closed }.Contains(status))
                errors.Add($"Status '{status}' is not valid");
            if (errors.Count > 0)
                rows.Add(new ImportRowResult(i + 2, $"positions:{code}", Val(row, "Title"), ImportRowStatus.Error, errors, []));
        }
    }

    private static void AddDepartmentCompanyConsistencyRows(
        IReadOnlyList<Dictionary<string, string>> departments,
        IReadOnlyList<Dictionary<string, string>> branches,
        IReadOnlyList<Dictionary<string, string>> costCenters,
        List<ImportRowResult> rows)
    {
        var branchCompany = branches
            .Where(x => !string.IsNullOrWhiteSpace(Val(x, "Code")))
            .GroupBy(x => Val(x, "Code"), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Select(x => Val(x, "CompanyLegalName")).Distinct(StringComparer.OrdinalIgnoreCase).ToList(), StringComparer.OrdinalIgnoreCase);
        var costCenterCompany = costCenters
            .Where(x => !string.IsNullOrWhiteSpace(Val(x, "Code")))
            .GroupBy(x => Val(x, "Code"), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Select(x => Val(x, "CompanyLegalName")).Distinct(StringComparer.OrdinalIgnoreCase).ToList(), StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < departments.Count; i++)
        {
            var row = departments[i];
            var company = Val(row, "CompanyLegalName");
            if (string.IsNullOrWhiteSpace(company)) continue;
            var code = Val(row, "Code");
            var errors = new List<string>();
            var branchCode = Val(row, "BranchCode");
            if (!string.IsNullOrWhiteSpace(branchCode) && branchCompany.TryGetValue(branchCode, out var branchCompanies) && branchCompanies.Count > 0 && !branchCompanies.Contains(company, StringComparer.OrdinalIgnoreCase))
                errors.Add($"BranchCode '{branchCode}' belongs to {string.Join("/", branchCompanies)}, not '{company}'");
            var costCenterCode = Val(row, "CostCenterCode");
            if (!string.IsNullOrWhiteSpace(costCenterCode) && costCenterCompany.TryGetValue(costCenterCode, out var ccCompanies) && ccCompanies.Count > 0 && !ccCompanies.Contains(company, StringComparer.OrdinalIgnoreCase))
                errors.Add($"CostCenterCode '{costCenterCode}' belongs to {string.Join("/", ccCompanies)}, not '{company}'");
            if (errors.Count > 0)
                rows.Add(new ImportRowResult(i + 2, $"departments:{code}", Val(row, "NameEn"), ImportRowStatus.Error, errors, []));
        }
    }

    private static void Merge(HashSet<string> target, IEnumerable<string> values)
    {
        foreach (var value in values)
            if (!string.IsNullOrWhiteSpace(value)) target.Add(value);
    }

    private static ParsedOrgPackage ParsePackage(OrganizationStructureImportRequest req) => new(
        Csv.Parse(req.CompaniesCsv ?? string.Empty),
        Csv.Parse(req.BranchesCsv ?? string.Empty),
        Csv.Parse(req.CostCentersCsv ?? string.Empty),
        Csv.Parse(req.DepartmentsCsv ?? string.Empty),
        Csv.Parse(req.GradesCsv ?? string.Empty),
        Csv.Parse(req.GradePayComponentsCsv ?? string.Empty),
        Csv.Parse(req.DesignationsCsv ?? string.Empty),
        Csv.Parse(req.PositionsCsv ?? string.Empty));

    private static string Val(Dictionary<string, string> row, string key, string fallback = "") => row.GetValueOrDefault(key, fallback).Trim();
    private static bool Bool(Dictionary<string, string> row, string key, bool fallback) => !row.TryGetValue(key, out var v) ? fallback : !string.Equals(v.Trim(), "false", StringComparison.OrdinalIgnoreCase);
    private static int Int(Dictionary<string, string> row, string key, int fallback = 0) => int.TryParse(Val(row, key), out var v) ? v : fallback;
    private static decimal Dec(Dictionary<string, string> row, string key) => decimal.TryParse(Val(row, key), out var v) ? v : 0m;
    private static decimal Dec(Dictionary<string, string> row, string key, decimal fallback) => decimal.TryParse(Val(row, key), out var v) ? v : fallback;
    private static DateOnly Date(Dictionary<string, string> row, string key, DateOnly fallback) => DateOnly.TryParse(Val(row, key), out var v) ? v : fallback;
    private static DateOnly? DateOrNull(Dictionary<string, string> row, string key) => DateOnly.TryParse(Val(row, key), out var v) ? v : null;
    private Guid GetTenantId() => Guid.Parse(User.FindFirst("tenant_id")!.Value);
    private Guid? GetUserId() => Guid.TryParse(User.FindFirst("sub")?.Value, out var id) ? id : null;
    private RequestContext BuildContext(Guid tenantId) => new(
        HttpContext.Connection.RemoteIpAddress?.ToString(),
        Request.Headers.UserAgent.ToString(),
        GetUserId(),
        tenantId);

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = false };

    private static string StableJson<T>(T value) => JsonSerializer.Serialize(value, JsonOptions);

    private static string Checksum(string payloadJson)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(payloadJson));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static int CountWouldCreate(OrganizationStructureImportResult result) =>
        result.Rows.Count(x => x.Status == ImportRowStatus.Ok);

    private static int CountWouldUpdate(OrganizationStructureImportResult result) =>
        result.Rows.Count(x => x.Warnings.Any(w => w.Contains("already exists and will be updated", StringComparison.OrdinalIgnoreCase)));

    private async Task<MigrationReconciliationDetails> BuildReconciliationAsync(Guid tenantId, ParsedOrgPackage parsed, OrganizationStructureImportResult validation, CancellationToken ct)
    {
        // IgnoreQueryFilters is intentional: reconciliation is a tenant-scoped migration control-plane read.
        var userIds = await _db.Users.IgnoreQueryFilters().AsNoTracking()
            .Where(x => x.TenantId == tenantId && !x.IsDeleted)
            .Select(x => x.Id)
            .ToListAsync(ct);
        // IgnoreQueryFilters is intentional: the reconciliation is still constrained to this tenant.
        var roleIds = await _db.Roles.IgnoreQueryFilters().AsNoTracking()
            .Where(x => x.TenantId == tenantId && !x.IsDeleted)
            .Select(x => x.Id)
            .ToListAsync(ct);

        var sectionCounts = new Dictionary<string, int>
        {
            ["companies"] = parsed.Companies.Count,
            ["branches"] = parsed.Branches.Count,
            ["costCenters"] = parsed.CostCenters.Count,
            ["departments"] = parsed.Departments.Count,
            ["grades"] = parsed.Grades.Count,
            ["gradePayComponents"] = parsed.GradePayComponents.Count,
            ["designations"] = parsed.Designations.Count,
            ["positions"] = parsed.Positions.Count
        };

        var identityCounts = new Dictionary<string, int>
        {
            ["users"] = userIds.Count,
            ["roles"] = roleIds.Count,
            ["userRoleAssignments"] = await _db.UserRoles.AsNoTracking().CountAsync(x => userIds.Contains(x.UserId) && roleIds.Contains(x.RoleId), ct)
        };

        var operationalCounts = new Dictionary<string, int>
        {
            // IgnoreQueryFilters is intentional: reconciliation is a tenant-scoped migration control-plane read.
            ["employees"] = await _db.Employees.IgnoreQueryFilters().AsNoTracking().CountAsync(x => x.TenantId == tenantId && !x.IsDeleted, ct),
            // IgnoreQueryFilters is intentional: reconciliation is a tenant-scoped migration control-plane read.
            ["attendanceRawEvents"] = await _db.AttendanceRawEvents.IgnoreQueryFilters().AsNoTracking().CountAsync(x => x.TenantId == tenantId, ct),
            // IgnoreQueryFilters is intentional: reconciliation is a tenant-scoped migration control-plane read.
            ["leaveRequests"] = await _db.LeaveRequests.IgnoreQueryFilters().AsNoTracking().CountAsync(x => x.TenantId == tenantId, ct),
            // IgnoreQueryFilters is intentional: reconciliation is a tenant-scoped migration control-plane read.
            ["payrollRuns"] = await _db.PayrollRuns.IgnoreQueryFilters().AsNoTracking().CountAsync(x => x.TenantId == tenantId, ct)
        };

        return new MigrationReconciliationDetails(
            SectionCounts: sectionCounts,
            IdentityCounts: identityCounts,
            OperationalCounts: operationalCounts,
            ValidationErrors: validation.Errors,
            ValidationWarnings: validation.Warnings);
    }

    private static MigrationImportBatchDto ToDto(MigrationImportBatch batch, MigrationReconciliationDetails? reconciliation = null, IReadOnlyList<string>? errors = null)
    {
        reconciliation ??= DeserializeOrDefault<MigrationReconciliationDetails>(batch.ReconciliationJson) ??
            new MigrationReconciliationDetails(new Dictionary<string, int>(), new Dictionary<string, int>(), new Dictionary<string, int>(), 0, 0);
        errors ??= DeserializeOrDefault<List<string>>(batch.ErrorJson) ?? [];
        return new MigrationImportBatchDto(
            batch.Id,
            batch.ExternalBatchId,
            batch.PackageType,
            batch.Status,
            batch.PackageChecksum,
            batch.DryRun,
            batch.CurrentSection,
            batch.ReceivedRows,
            batch.CreatedRows,
            batch.UpdatedRows,
            batch.SkippedRows,
            batch.ErrorRows,
            reconciliation,
            errors,
            batch.CreatedAtUtc,
            batch.UpdatedAtUtc,
            batch.CompletedAtUtc);
    }

    private static T? DeserializeOrDefault<T>(string json)
    {
        try { return JsonSerializer.Deserialize<T>(json, JsonOptions); }
        catch { return default; }
    }
}

public record OrganizationStructureImportRequest(
    string? CompaniesCsv,
    string? BranchesCsv,
    string? CostCentersCsv,
    string? DepartmentsCsv,
    string? GradesCsv,
    string? GradePayComponentsCsv,
    string? DesignationsCsv,
    string? PositionsCsv = null);

public record OrganizationStructureImportResult(
    int Received,
    int Errors,
    int Warnings,
    IReadOnlyList<ImportRowResult> Rows,
    bool HasBlockingErrors,
    bool Committed,
    IReadOnlyDictionary<string, int> Applied);

public record MigrationImportBatchDto(
    Guid Id,
    string? ExternalBatchId,
    string PackageType,
    string Status,
    string PackageChecksum,
    bool DryRun,
    string CurrentSection,
    int ReceivedRows,
    int CreatedRows,
    int UpdatedRows,
    int SkippedRows,
    int ErrorRows,
    MigrationReconciliationDetails Reconciliation,
    IReadOnlyList<string> Errors,
    DateTime CreatedAtUtc,
    DateTime? UpdatedAtUtc,
    DateTime? CompletedAtUtc);

public record MigrationReconciliationDetails(
    IReadOnlyDictionary<string, int> SectionCounts,
    IReadOnlyDictionary<string, int> IdentityCounts,
    IReadOnlyDictionary<string, int> OperationalCounts,
    int ValidationErrors,
    int ValidationWarnings);

internal record ParsedOrgPackage(
    List<Dictionary<string, string>> Companies,
    List<Dictionary<string, string>> Branches,
    List<Dictionary<string, string>> CostCenters,
    List<Dictionary<string, string>> Departments,
    List<Dictionary<string, string>> Grades,
    List<Dictionary<string, string>> GradePayComponents,
    List<Dictionary<string, string>> Designations,
    List<Dictionary<string, string>> Positions)
{
    public int TotalRows => Companies.Count + Branches.Count + CostCenters.Count + Departments.Count + Grades.Count + GradePayComponents.Count + Designations.Count + Positions.Count;
}
