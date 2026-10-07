using System.Text;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using Zayra.Api.Application.Auth;
using Zayra.Api.Application.Common;
using Zayra.Api.Application.Common.Import;
using Zayra.Api.Application.Organization;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.Auth;
using Zayra.Api.Infrastructure.Organization;
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
        // Each section is written through Csv.Template, which refuses an example row whose cell count
        // does not match its header (CSV rows are positional — a short row silently shifts every value
        // one column left) and applies the same formula-injection escaping the export path does.
        void Section(string name, string[] headers, params string[] sample)
        {
            sb.Append("# ").Append(name).Append('\n');
            sb.Append(Csv.Template(headers, sample));
            sb.Append('\n');
        }

        // One consistent, self-explanatory EXAMPLE row per section. Every value is
        // neutral (no brand/demo data) and every cross-section reference resolves
        // within this package, so downloading the template and re-uploading it passes
        // Preview() with no blocking errors (see OrganizationStructureTemplateRoundTripTests).
        // ManagerEmployeeCode / ParentDepartmentCode are intentionally blank — an empty
        // tenant has no employees or parent departments to reference yet.
        Section("companies", CompaniesHeaders,
            "Example Company Ltd", "", "Example Company", "SA", "SA-default", "", "", "", "", "", "SAR", "true");
        Section("branches", BranchesHeaders,
            "Example Company Ltd", "HQ", "Head Office", "", "SA", "Main City", "", "", "Asia/Riyadh", "", "true", "true");
        Section("costCenters", CostCentersHeaders,
            "Example Company Ltd", "CC-OPS", "Operations", "true");
        Section("departments", DepartmentsHeaders,
            "Example Company Ltd", "HQ", "OPS", "Operations", "", "", "", "CC-OPS", "10", "100000", "true");
        Section("grades", GradesHeaders,
            "G1", "Grade 1", "Staff", "1", "8000", "10000", "12000", "SAR", "true");
        Section("gradePayComponents", GradePayHeaders,
            "G1", "BASIC", "Basic Salary", "Earning", "Fixed", "8000", "0", "Monthly", "false", "true");
        Section("designations", DesignationsHeaders,
            "OPS-OFF", "Operations Officer", "", "OPS", "G1", "G1", "Staff", "", "false", "5", "true");
        Section("positions", PositionsHeaders,
            "POS-OPS-001", "Operations Officer", "Example Company Ltd", "HQ", "OPS", "CC-OPS", "OPS-OFF", "G1", "1", "10000", "SAR", "Open", "2026-01-01", "");
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
        // Commit() now runs its unit through the execution strategy, and a retry inside that
        // strategy clears the change tracker. Re-attach the batch from persisted state so the
        // status updates below are actually written instead of being applied to a detached
        // entity and silently dropped by SaveChanges.
        if (_db.Entry(batch).State == EntityState.Detached)
            batch = await _db.MigrationImportBatches.FirstAsync(x => x.TenantId == tenantId && x.Id == id, ct);
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

        // Program.cs registers the DbContext with EnableRetryOnFailure, so the ambient execution
        // strategy is NpgsqlRetryingExecutionStrategy, and it refuses a user-initiated
        // BeginTransactionAsync unless the whole unit runs inside
        // Database.CreateExecutionStrategy().ExecuteAsync(...). The bare transaction that stood
        // here threw InvalidOperationException before touching a single row, and the generic
        // exception handler turned it into HTTP 400 — so the org-structure import could never
        // commit, 100% of the time.
        var counts = new Dictionary<string, int>();

        if (!_db.Database.IsRelational())
        {
            // Preserved branch: the in-memory provider the fast unit tests use has neither
            // transactions nor an execution strategy to satisfy.
            await ApplyStructureAsync(tenantId, parsed, counts, ct);
        }
        else
        {
            var strategy = _db.Database.CreateExecutionStrategy();
            var attempt = 0;
            await strategy.ExecuteAsync(async () =>
            {
                if (attempt++ > 0)
                {
                    // ExecuteAsync may re-run this delegate. A retry must not inherit the change
                    // tracker a failed attempt left behind: every Company/Branch/Department it
                    // added is still pending and would be inserted a second time, and — worse —
                    // rows whose SaveChanges succeeded before a transient failure swallowed the
                    // COMMIT are tracked as Unchanged, so a naive retry would silently write
                    // nothing at all. ApplyStructureAsync re-reads every lookup from the database,
                    // so a cleared tracker means the attempt restarts from persisted state.
                    _db.ChangeTracker.Clear();
                    counts.Clear();
                }

                var tx = await _db.Database.BeginTransactionAsync(ct);
                try
                {
                    await ApplyStructureAsync(tenantId, parsed, counts, ct);
                    await tx.CommitAsync(ct);
                }
                catch
                {
                    // Never let a failing rollback mask the original error: the strategy has to
                    // see the real exception to classify it as transient. Dispose still releases.
                    try { await tx.RollbackAsync(ct); } catch { /* connection already gone */ }
                    throw;
                }
                finally
                {
                    await tx.DisposeAsync();
                }
            });
        }

        // The audit rows (one per entity, then the bulk marker) are written inside ApplyStructureAsync, in the
        // same transaction as the data: there is no committed import without its trail.
        return Ok(validation with { Applied = counts, Committed = true });
    }

    /// <summary>
    /// Upserts every organization-structure section and persists it. Extracted from
    /// <see cref="Commit"/> verbatim so the whole apply can be handed to the ambient execution
    /// strategy as one retriable unit. Every lookup dictionary is re-read from the database on
    /// entry, so with a cleared change tracker the strategy may safely run it again.
    /// </summary>
    private async Task ApplyStructureAsync(
        Guid tenantId,
        ParsedOrgPackage parsed,
        Dictionary<string, int> counts,
        CancellationToken ct)
    {
        void Bump(string key) => counts[key] = counts.GetValueOrDefault(key) + 1;
        // One audit row per entity, with the Setup forms' own action names, written in THIS transaction (see Commit).
        var audited = new List<(string Action, string Entity, Guid Id, string Key)>();
        void Audit(string action, string entity, Guid id, string key) => audited.Add((action, entity, id, key));
        var pendingCompanyCreates = 0;

        var companies = SavedIndex.Of(await _db.Companies.Where(x => x.TenantId == tenantId && !x.IsDeleted).ToListAsync(ct), x => x.LegalNameEn, "company");
        foreach (var row in parsed.Companies)
        {
            var name = Val(row, "LegalNameEn");
            var active = Bool(row, "IsActive", true);
            if (companies.TryGetValue(name.ToUpperInvariant(), out var company))
            {
                if (HasValue(row, "IsActive") && active != company.IsActive)
                    throw new InvalidOperationException(
                        "Organization-structure import cannot change an existing company's activation state. Use the controlled company lifecycle workflow.");
                company.LegalNameAr = Val(row, "LegalNameAr");
                company.TradeName = Val(row, "TradeName");
                company.CountryCode = Val(row, "CountryCode").ToUpperInvariant();
                company.Jurisdiction = Val(row, "Jurisdiction");
                company.RegistrationNumber = Val(row, "RegistrationNumber");
                company.TaxNumber = Val(row, "TaxNumber");
                company.WpsEmployerId = Val(row, "WpsEmployerId");
                company.GosiEmployerId = Val(row, "GosiEmployerId");
                company.QiwaEstablishmentId = Val(row, "QiwaEstablishmentId");
                company.DefaultCurrency = Val(row, "DefaultCurrency", "SAR").ToUpperInvariant();
                company.UpdatedAtUtc = DateTime.UtcNow;
                Audit("organization.company_updated", nameof(Company), company.Id, name);
            }
            else
            {
                // Last line of defence behind validation (a concurrent create between preview and commit): the
                // form's gate, re-asked inside the transaction; a refusal rolls back the whole package.
                var gate = await CompanyCreationGate.EvaluateAsync(_db, tenantId, ct, pendingCompanyCreates);
                if (!gate.Allowed) throw new InvalidOperationException(gate.Message);
                pendingCompanyCreates++;
                company = new Company
                {
                    TenantId = tenantId,
                    LegalNameEn = name,
                    LegalNameAr = Val(row, "LegalNameAr"),
                    TradeName = Val(row, "TradeName"),
                    CountryCode = Val(row, "CountryCode").ToUpperInvariant(),
                    Jurisdiction = Val(row, "Jurisdiction"),
                    RegistrationNumber = Val(row, "RegistrationNumber"),
                    TaxNumber = Val(row, "TaxNumber"),
                    WpsEmployerId = Val(row, "WpsEmployerId"),
                    GosiEmployerId = Val(row, "GosiEmployerId"),
                    QiwaEstablishmentId = Val(row, "QiwaEstablishmentId"),
                    DefaultCurrency = Val(row, "DefaultCurrency", "SAR").ToUpperInvariant(),
                    IsActive = active,
                    CreatedBy = GetUserId()
                };
                if (gate.AsDraft)
                {
                    // Draft-approval tenants: created inactive, awaiting platform approval — exactly as the form does.
                    company.ApprovalStatus = CompanyApprovalStatuses.Draft;
                    company.IsActive = false;
                }
                _db.Companies.Add(company);
                Audit("organization.company_created", nameof(Company), company.Id, name);
                companies[name.ToUpperInvariant()] = company;
                Bump("companies");
            }
        }

        var branches = SavedIndex.Of(await _db.Branches.Where(x => x.TenantId == tenantId && !x.IsDeleted).ToListAsync(ct), x => x.Code, "branch");
        foreach (var row in parsed.Branches)
        {
            var code = Val(row, "Code");
            var company = companies[Val(row, "CompanyLegalName").ToUpperInvariant()];
            if (branches.TryGetValue(code.ToUpperInvariant(), out var branch))
            {
                // The Branches importer's rule: a branch stays in its company (validation reports it; this backs it up).
                if (branch.CompanyId != company.Id)
                    throw new InvalidOperationException($"Branch '{code}' belongs to another company and cannot be moved to another company by import.");
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
                Audit("organization.branch_updated", nameof(Branch), branch.Id, code);
            }
            else
            {
                branch = new Branch
                {
                    TenantId = tenantId,
                    CompanyId = company.Id,
                    Code = OrgCodes.Normalize(code),
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
                Audit("organization.branch_created", nameof(Branch), branch.Id, code);
                branches[code.ToUpperInvariant()] = branch;
                Bump("branches");
            }
        }

        var costCenters = SavedIndex.Of(await _db.CostCenters.Where(x => x.TenantId == tenantId && !x.IsDeleted).ToListAsync(ct), x => x.Code, "cost center");
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
                Audit("organization.cost_center_updated", nameof(CostCenter), cc.Id, code);
            }
            else
            {
                cc = new CostCenter { TenantId = tenantId, CompanyId = company.Id, Code = OrgCodes.Normalize(code), Name = Val(row, "Name"), IsActive = Bool(row, "IsActive", true), CreatedBy = GetUserId() };
                _db.CostCenters.Add(cc);
                Audit("organization.cost_center_created", nameof(CostCenter), cc.Id, code);
                costCenters[code.ToUpperInvariant()] = cc;
                Bump("costCenters");
            }
        }

        var grades = SavedIndex.Of(await _db.Grades.Where(x => x.TenantId == tenantId && !x.IsDeleted).ToListAsync(ct), x => x.Code, "grade");
        foreach (var row in parsed.Grades)
        {
            var code = Val(row, "Code");
            if (!grades.TryGetValue(code.ToUpperInvariant(), out var grade))
            {
                grade = new Grade { TenantId = tenantId, Code = OrgCodes.Normalize(code), CreatedBy = GetUserId() };
                _db.Grades.Add(grade);
                Audit("organization.grade_created", nameof(Grade), grade.Id, code);
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
            if (_db.Entry(grade).State != EntityState.Added) Audit("organization.grade_updated", nameof(Grade), grade.Id, code);
        }

        var departments = SavedIndex.Of(await _db.Departments.Where(x => x.TenantId == tenantId && !x.IsDeleted).ToListAsync(ct), x => x.Code, "department");
        var employeesByCode = SavedIndex.Of(await _db.Employees.Where(x => x.TenantId == tenantId && !x.IsDeleted).ToListAsync(ct), x => x.EmployeeCode, "employee");
        foreach (var row in parsed.Departments)
        {
            var code = Val(row, "Code");
            if (!departments.TryGetValue(code.ToUpperInvariant(), out var department))
            {
                department = new Department { TenantId = tenantId, Code = OrgCodes.Normalize(code), CreatedBy = GetUserId() };
                _db.Departments.Add(department);
                Audit("organization.department_created", nameof(Department), department.Id, code);
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
            if (_db.Entry(department).State != EntityState.Added) Audit("organization.department_updated", nameof(Department), department.Id, code);
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
            var component = new GradePayScaleComponent
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
            };
            _db.GradePayScaleComponents.Add(component);
            Audit("organization.grade_pay_component_created", nameof(GradePayScaleComponent), component.Id, $"{grade.Code}/{code}");
            Bump("gradePayComponents");
        }

        var designations = SavedIndex.Of(await _db.Designations.Where(x => x.TenantId == tenantId && !x.IsDeleted).ToListAsync(ct), x => x.Code, "designation");
        foreach (var row in parsed.Designations)
        {
            var code = Val(row, "Code");
            if (!designations.TryGetValue(code.ToUpperInvariant(), out var designation))
            {
                designation = new Designation { TenantId = tenantId, Code = OrgCodes.Normalize(code), CreatedBy = GetUserId() };
                _db.Designations.Add(designation);
                Audit("organization.designation_created", nameof(Designation), designation.Id, code);
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
            if (_db.Entry(designation).State != EntityState.Added) Audit("organization.designation_updated", nameof(Designation), designation.Id, code);
        }

        await _db.SaveChangesAsync(ct);

        var positions = SavedIndex.Of(await _db.Positions.Where(x => x.TenantId == tenantId && !x.IsDeleted).ToListAsync(ct), x => x.Code, "position");
        foreach (var row in parsed.Positions)
        {
            var code = Val(row, "Code");
            if (!positions.TryGetValue(code.ToUpperInvariant(), out var position))
            {
                position = new Position { TenantId = tenantId, Code = code, CreatedBy = GetUserId() };
                _db.Positions.Add(position);
                Audit("position.created", nameof(Position), position.Id, code);
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
            if (_db.Entry(position).State != EntityState.Added) Audit("position.updated", nameof(Position), position.Id, code);
        }

        await _db.SaveChangesAsync(ct);

        // ── AUDIT, ONE ROW PER ENTITY, IN THE SAME TRANSACTION ─────────────────────────────────────────
        // It used to be ONE bulk row written after the commit — so who created which company or grade could not
        // be told from the trail, and a failure writing it left the import unaudited. Same action names as the
        // Setup forms, tagged with the import as the source.
        var context = BuildContext(tenantId);
        var at = DateTime.UtcNow;
        foreach (var (action, entity, id, key) in audited.DistinctBy(a => (a.Action, a.Id)))
            _db.AuditLogs.Add(AuthAuditEntry.Create(Guid.NewGuid(), at, action, entity, id.ToString(), context,
                JsonSerializer.Serialize(new { source = "organization_structure_import", key })));
        _db.AuditLogs.Add(AuthAuditEntry.Create(Guid.NewGuid(), at, "setup.organization_structure_import_committed",
            "OrganizationStructureImport", "bulk", context,
            JsonSerializer.Serialize(new { received = parsed.TotalRows, applied = counts, entities = audited.Count })));
        await _db.SaveChangesAsync(ct);
    }

    private async Task<OrganizationStructureImportResult> ValidateAsync(Guid tenantId, ParsedOrgPackage parsed, EntityScopeContext scope, CancellationToken ct)
    {
        var rows = new List<ImportRowResult>();
        var existingCompanies = await _db.Companies.AsNoTracking()
            .Where(x => x.TenantId == tenantId && !x.IsDeleted)
            .Select(x => new { x.Id, x.LegalNameEn, x.IsActive })
            .ToListAsync(ct);
        var companyNames = existingCompanies.Select(x => x.LegalNameEn).ToHashSet(StringComparer.OrdinalIgnoreCase);
        // Saved names or codes can differ only in letter case ("Evostel Trading LLC" and
        // "EVOSTEL TRADING LLC", or codes "OPS" and "ops": the unique indexes are case-sensitive).
        // Import matches case-insensitively, so ToDictionary threw on them — a 500 whatever the
        // package held. The first record stands in for lookups; a package row that names an
        // ambiguous key is refused below with a row error instead.
        var companyIdsByName = existingCompanies.GroupBy(x => x.LegalNameEn, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().Id, StringComparer.OrdinalIgnoreCase);
        var companyLifecycleByName = existingCompanies.GroupBy(x => x.LegalNameEn, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().IsActive, StringComparer.OrdinalIgnoreCase);
        var branchRows = await _db.Branches.AsNoTracking()
            .Where(x => x.TenantId == tenantId && !x.IsDeleted)
            .Select(x => new { x.Code, x.CompanyId })
            .ToListAsync(ct);
        var branchCodes = branchRows.Select(x => x.Code).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var branchCompanyByCode = branchRows.GroupBy(x => x.Code, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => (Guid?)g.First().CompanyId, StringComparer.OrdinalIgnoreCase);
        var costCenterRows = await _db.CostCenters.AsNoTracking()
            .Where(x => x.TenantId == tenantId && !x.IsDeleted)
            .Select(x => new { x.Code, x.CompanyId })
            .ToListAsync(ct);
        var costCenterCodes = costCenterRows.Select(x => x.Code).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var costCenterCompanyByCode = costCenterRows.GroupBy(x => x.Code, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => (Guid?)g.First().CompanyId, StringComparer.OrdinalIgnoreCase);
        var savedDepartmentCodes = await _db.Departments.AsNoTracking()
            .Where(x => x.TenantId == tenantId && !x.IsDeleted)
            .Select(x => x.Code)
            .ToListAsync(ct);
        var savedGradeCodes = await _db.Grades.AsNoTracking().Where(x => x.TenantId == tenantId && !x.IsDeleted).Select(x => x.Code).ToListAsync(ct);
        var savedEmployeeCodes = await _db.Employees.AsNoTracking().Where(x => x.TenantId == tenantId && !x.IsDeleted).Select(x => x.EmployeeCode).ToListAsync(ct);
        var savedDesignationCodes = await _db.Designations.AsNoTracking().Where(x => x.TenantId == tenantId && !x.IsDeleted).Select(x => x.Code).ToListAsync(ct);
        var savedPositionCodes = await _db.Positions.AsNoTracking().Where(x => x.TenantId == tenantId && !x.IsDeleted).Select(x => x.Code).ToListAsync(ct);
        var departmentCodes = savedDepartmentCodes.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var gradeCodes = savedGradeCodes.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var employeeCodes = savedEmployeeCodes.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var designationCodes = savedDesignationCodes.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var positionCodes = savedPositionCodes.ToHashSet(StringComparer.OrdinalIgnoreCase);

        var ambiguousCompanies = Ambiguous(existingCompanies.Select(x => x.LegalNameEn));
        var ambiguousBranches = Ambiguous(branchRows.Select(x => x.Code));
        var ambiguousCostCenters = Ambiguous(costCenterRows.Select(x => x.Code));
        var ambiguousDepartments = Ambiguous(savedDepartmentCodes);
        var ambiguousGrades = Ambiguous(savedGradeCodes);
        var ambiguousEmployees = Ambiguous(savedEmployeeCodes);
        var ambiguousDesignations = Ambiguous(savedDesignationCodes);
        var ambiguousPositions = Ambiguous(savedPositionCodes);
        AddAmbiguityRows("companies", parsed.Companies, "LegalNameEn", rows, ("LegalNameEn", ambiguousCompanies, "company"));
        AddAmbiguityRows("branches", parsed.Branches, "Code", rows,
            ("Code", ambiguousBranches, "branch"), ("CompanyLegalName", ambiguousCompanies, "company"));
        AddAmbiguityRows("costCenters", parsed.CostCenters, "Code", rows,
            ("Code", ambiguousCostCenters, "cost center"), ("CompanyLegalName", ambiguousCompanies, "company"));
        AddAmbiguityRows("grades", parsed.Grades, "Code", rows, ("Code", ambiguousGrades, "grade"));
        AddAmbiguityRows("departments", parsed.Departments, "Code", rows,
            ("Code", ambiguousDepartments, "department"), ("ParentDepartmentCode", ambiguousDepartments, "department"),
            ("CompanyLegalName", ambiguousCompanies, "company"), ("BranchCode", ambiguousBranches, "branch"),
            ("CostCenterCode", ambiguousCostCenters, "cost center"), ("ManagerEmployeeCode", ambiguousEmployees, "employee"));
        AddAmbiguityRows("gradePayComponents", parsed.GradePayComponents, "ComponentCode", rows, ("GradeCode", ambiguousGrades, "grade"));
        AddAmbiguityRows("designations", parsed.Designations, "Code", rows,
            ("Code", ambiguousDesignations, "designation"), ("DepartmentCode", ambiguousDepartments, "department"),
            ("GradeCode", ambiguousGrades, "grade"), ("JobGrade", ambiguousGrades, "grade"));
        AddAmbiguityRows("positions", parsed.Positions, "Code", rows,
            ("Code", ambiguousPositions, "position"), ("CompanyLegalName", ambiguousCompanies, "company"),
            ("BranchCode", ambiguousBranches, "branch"), ("DepartmentCode", ambiguousDepartments, "department"),
            ("CostCenterCode", ambiguousCostCenters, "cost center"), ("DesignationCode", ambiguousDesignations, "designation"),
            ("GradeCode", ambiguousGrades, "grade"));

        AddScopeRows(parsed, scope, companyNames, companyIdsByName, branchCompanyByCode, costCenterCompanyByCode, rows);

        AddRows("companies", parsed.Companies, "LegalNameEn", "LegalNameEn", required: ["LegalNameEn", "CountryCode", "DefaultCurrency"], known: companyNames, rows);
        for (var i = 0; i < parsed.Companies.Count; i++)
        {
            var row = parsed.Companies[i];
            var name = Val(row, "LegalNameEn");
            if (companyLifecycleByName.TryGetValue(name, out var current)
                && HasValue(row, "IsActive")
                && Bool(row, "IsActive", true) != current)
            {
                rows.Add(new ImportRowResult(
                    i + 2,
                    $"companies:{name}",
                    name,
                    ImportRowStatus.Error,
                    ["Existing company activation cannot be changed by organization import. Use the controlled company lifecycle workflow."],
                    []));
            }
        }
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
        // A parent may be saved already or arrive in this same file, above or below its child.
        // Commit applies parents in a second pass, so validation must accept both. The saved set
        // stays separate so a new row is not reported as "already exists and will be updated".
        var parentCandidates = new HashSet<string>(departmentCodes, StringComparer.OrdinalIgnoreCase);
        Merge(parentCandidates, parsed.Departments.Select(x => Val(x, "Code")));
        AddRows("departments", parsed.Departments, "Code", "NameEn", required: ["Code", "NameEn"], known: departmentCodes, rows,
            refs: [("CompanyLegalName", companyNames, "Company"), ("BranchCode", branchCodes, "Branch"), ("CostCenterCode", costCenterCodes, "Cost center"), ("ParentDepartmentCode", parentCandidates, "Parent department"), ("ManagerEmployeeCode", employeeCodes, "Manager employee")]);
        AddDepartmentCompanyConsistencyRows(parsed.Departments, parsed.Branches, parsed.CostCenters, rows);
        Merge(departmentCodes, parsed.Departments.Select(x => Val(x, "Code")));
        AddRows("gradePayComponents", parsed.GradePayComponents, "ComponentCode", "ComponentName", required: ["GradeCode", "ComponentCode", "ComponentName"], known: new HashSet<string>(StringComparer.OrdinalIgnoreCase), rows,
            // BASIC under G1 and BASIC under G2 are two components. Commit keys them by (grade, code).
            refs: [("GradeCode", gradeCodes, "Grade")], duplicateScopeKey: "GradeCode");
        AddGradePayComponentSanityRows(parsed.GradePayComponents, rows);
        AddRows("designations", parsed.Designations, "Code", "TitleEn", required: ["Code", "TitleEn"], known: new HashSet<string>(StringComparer.OrdinalIgnoreCase), rows,
            refs: [("DepartmentCode", departmentCodes, "Department"), ("GradeCode", gradeCodes, "Grade")]);
        Merge(designationCodes, parsed.Designations.Select(x => Val(x, "Code")));
        AddRows("positions", parsed.Positions, "Code", "Title", required: ["Code", "Title"], known: positionCodes, rows,
            refs: [("CompanyLegalName", companyNames, "Company"), ("BranchCode", branchCodes, "Branch"), ("DepartmentCode", departmentCodes, "Department"), ("CostCenterCode", costCenterCodes, "Cost center"), ("DesignationCode", designationCodes, "Designation"), ("GradeCode", gradeCodes, "Grade")]);
        AddPositionSanityRows(parsed.Positions, rows);
        await AddFormGateRowsAsync(tenantId, parsed, rows, ct);

        var parentMap = parsed.Departments
            .Where(x => !string.IsNullOrWhiteSpace(Val(x, "Code")) && !string.IsNullOrWhiteSpace(Val(x, "ParentDepartmentCode")))
            // A duplicated code already carries a blocking row error; reporting it must not throw.
            .GroupBy(x => Val(x, "Code"), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => Val(g.First(), "ParentDepartmentCode"), StringComparer.OrdinalIgnoreCase);
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

    /// <summary>
    /// THE SETUP FORMS' GATES, applied to the bulk import (P0). The import used to write companies and branches
    /// straight to the database: a single-company account could add a second legal entity, a platform-controlled
    /// tenant could create companies itself, the plan's company limit was never counted, a draft-approval tenant
    /// got an ACTIVE company, a registration number could be duplicated, "Saudi" was stored as a country code, and
    /// a branch could be moved to another company. Each is now a blocking row error, so the preview names it and
    /// the commit refuses the WHOLE package — nothing is written. The rules are the forms' own:
    /// <see cref="CompanyCreationGate"/>, <see cref="OrganizationSetupService.CountryCodeProblem"/>, the company
    /// registration-number uniqueness check, and the Branches importer's no-move rule.
    /// </summary>
    private async Task AddFormGateRowsAsync(Guid tenantId, ParsedOrgPackage parsed, List<ImportRowResult> rows, CancellationToken ct)
    {
        var saved = await _db.Companies.AsNoTracking()
            .Where(x => x.TenantId == tenantId && !x.IsDeleted)
            .Select(x => new { x.Id, x.LegalNameEn, x.RegistrationNumber })
            .ToListAsync(ct);
        var savedByName = saved.GroupBy(x => x.LegalNameEn.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().Id, StringComparer.OrdinalIgnoreCase);
        var registrationOwner = saved.Where(x => !string.IsNullOrWhiteSpace(x.RegistrationNumber))
            .GroupBy(x => x.RegistrationNumber.Trim(), StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First().Id, StringComparer.Ordinal);

        var pendingCreates = 0;
        var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < parsed.Companies.Count; i++)
        {
            var row = parsed.Companies[i];
            var name = Val(row, "LegalNameEn");
            var errors = new List<string>();
            if (OrganizationSetupService.CountryCodeProblem(Val(row, "CountryCode")) is { } countryProblem) errors.Add(countryProblem);

            savedByName.TryGetValue(name, out var existingId);
            var registration = Val(row, "RegistrationNumber");
            // Non-blank numbers only: many legacy extracts carry none, and a blank is not an identity.
            if (registration.Length > 0 && registrationOwner.TryGetValue(registration, out var owner) && owner != existingId)
                errors.Add("Company registration number already exists in this tenant.");
            else if (registration.Length > 0 && existingId == Guid.Empty)
                registrationOwner[registration] = Guid.NewGuid(); // claimed by this new row; a later row repeating it is a duplicate

            if (existingId == Guid.Empty && name.Length > 0 && seenNames.Add(name))
            {
                // Counts the companies this file has already created, so "creates 4" cannot become "created 1, refused 3".
                var gate = await CompanyCreationGate.EvaluateAsync(_db, tenantId, ct, pendingCreates);
                if (gate.Allowed) pendingCreates++;
                else errors.Add(gate.Message);
            }

            if (errors.Count > 0)
                rows.Add(new ImportRowResult(i + 2, $"companies:{name}", name, ImportRowStatus.Error, errors, []));
        }

        var savedBranches = await _db.Branches.AsNoTracking()
            .Where(x => x.TenantId == tenantId && !x.IsDeleted)
            .Select(x => new { x.Code, x.CompanyId })
            .ToListAsync(ct);
        var branchCompany = savedBranches.GroupBy(x => x.Code.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().CompanyId, StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < parsed.Branches.Count; i++)
        {
            var row = parsed.Branches[i];
            var code = Val(row, "Code");
            var errors = new List<string>();
            if (OrganizationSetupService.CountryCodeProblem(Val(row, "CountryCode")) is { } countryProblem) errors.Add(countryProblem);
            if (branchCompany.TryGetValue(code, out var currentCompany)
                && savedByName.TryGetValue(Val(row, "CompanyLegalName"), out var rowCompany)
                && rowCompany != currentCompany)
                errors.Add($"Branch '{code}' belongs to another company and cannot be moved to another company by import.");
            if (errors.Count > 0)
                rows.Add(new ImportRowResult(i + 2, $"branches:{code}", Val(row, "NameEn"), ImportRowStatus.Error, errors, []));
        }
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

    /// <summary>Saved keys that more than one saved record answers to, ignoring letter case.</summary>
    private static HashSet<string> Ambiguous(IEnumerable<string> savedKeys) =>
        savedKeys.Where(k => !string.IsNullOrWhiteSpace(k))
            .GroupBy(k => k.Trim(), StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>One blocking row error for each package row that names an ambiguous saved key, so the
    /// preview says which record to fix instead of the commit guessing between them.</summary>
    private static void AddAmbiguityRows(string section, IReadOnlyList<Dictionary<string, string>> source, string codeKey,
        List<ImportRowResult> rows, params (string Key, HashSet<string> Ambiguous, string Label)[] refs)
    {
        if (refs.All(r => r.Ambiguous.Count == 0)) return;
        for (var i = 0; i < source.Count; i++)
        {
            var row = source[i];
            var errors = refs
                .Where(r => !string.IsNullOrWhiteSpace(Val(row, r.Key)) && r.Ambiguous.Contains(Val(row, r.Key)))
                .Select(r => $"{r.Key} '{Val(row, r.Key)}' matches more than one saved {r.Label} (their names differ only in letter case). Rename or remove the duplicate in Setup, then import again.")
                .Distinct()
                .ToList();
            if (errors.Count > 0)
                rows.Add(new ImportRowResult(i + 2, $"{section}:{Val(row, codeKey)}", Val(row, codeKey), ImportRowStatus.Error, errors, []));
        }
    }

    /// <summary>
    /// Saved records keyed case-insensitively, as import matches them. Keys that more than one saved
    /// record answers to are ambiguous: validation refuses any row that names one, and looking one
    /// up here throws rather than silently picking either record.
    /// </summary>
    private sealed class SavedIndex<T>
    {
        private readonly Dictionary<string, T> _unique = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _ambiguous = new(StringComparer.OrdinalIgnoreCase);
        private readonly string _label;

        public SavedIndex(IEnumerable<T> rows, Func<T, string> key, string label)
        {
            _label = label;
            foreach (var group in rows.GroupBy(r => (key(r) ?? string.Empty).Trim(), StringComparer.OrdinalIgnoreCase))
            {
                if (group.Count() > 1) _ambiguous.Add(group.Key);
                else _unique[group.Key] = group.First();
            }
        }

        public bool TryGetValue(string key, [System.Diagnostics.CodeAnalysis.MaybeNullWhen(false)] out T value)
        {
            if (_ambiguous.Contains(key.Trim()))
                throw new InvalidOperationException(
                    $"More than one saved {_label} matches '{key}' (their names differ only in letter case). Rename or remove the duplicate in Setup, then import again.");
            return _unique.TryGetValue(key.Trim(), out value);
        }

        public T this[string key]
        {
            get => TryGetValue(key, out var value) ? value : throw new KeyNotFoundException($"No saved {_label} matches '{key}'.");
            set => _unique[key.Trim()] = value;
        }
    }

    private static class SavedIndex
    {
        public static SavedIndex<T> Of<T>(IEnumerable<T> rows, Func<T, string> key, string label) => new(rows, key, label);
    }

    private static void AddRows(string section, IReadOnlyList<Dictionary<string, string>> source, string codeKey, string nameKey, string[] required, HashSet<string> known, List<ImportRowResult> rows, (string Key, HashSet<string> Known, string Label)[]? refs = null, string? duplicateScopeKey = null)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < source.Count; i++)
        {
            var row = source[i];
            var code = Val(row, codeKey);
            var errors = required.Where(key => string.IsNullOrWhiteSpace(Val(row, key))).Select(key => $"{key} is required").ToList();
            var warnings = new List<string>();
            // A scoped code is unique within its scope. The tuple is serialized so a code containing
            // a separator character cannot collide with a different (scope, code) pair.
            var uniqueKey = duplicateScopeKey is null
                ? code
                : JsonSerializer.Serialize(new[] { Val(row, duplicateScopeKey).ToUpperInvariant(), code.ToUpperInvariant() });
            if (!string.IsNullOrWhiteSpace(code) && !seen.Add(uniqueKey))
                errors.Add($"Duplicate {codeKey} '{code}' in {section}"
                    + (duplicateScopeKey is null ? string.Empty : $" for {duplicateScopeKey} '{Val(row, duplicateScopeKey)}'"));
            if (!string.IsNullOrWhiteSpace(code) && known.Contains(code))
                warnings.Add($"{section} record '{code}' already exists and will be updated");
            foreach (var (key, knownRefs, label) in refs ?? [])
            {
                var value = Val(row, key);
                if (!string.IsNullOrWhiteSpace(value) && !knownRefs.Contains(value))
                    errors.Add($"{label} reference '{value}' not found");
            }
            var status = errors.Count > 0 ? ImportRowStatus.Error : warnings.Count > 0 ? ImportRowStatus.Warning : ImportRowStatus.Ok;
            // Same identifier the grade/component sanity rows use, so both findings group together.
            var rowKey = duplicateScopeKey is null ? code : $"{Val(row, duplicateScopeKey)}/{code}";
            rows.Add(new ImportRowResult(i + 2, $"{section}:{rowKey}", Val(row, nameKey), status, errors, warnings));
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
    private static bool HasValue(Dictionary<string, string> row, string key) =>
        row.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value);
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
            batch.CompletedAtUtc,
            DeserializeOrDefault<OrganizationStructureImportResult>(batch.ResultJson));
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
    DateTime? CompletedAtUtc,
    // The full validation result (row-level findings + applied counts) the panel needs to render the
    // blocking/warning findings list, the warning count, and the post-commit "Applied" summary. It is
    // persisted on the batch as ResultJson; without surfacing it here the upload landing renders the
    // "Blocked" pill but no list of WHAT to fix.
    OrganizationStructureImportResult? Result = null);

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
