using System.Security.Claims;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Zayra.Api.Application.Approvals;
using Zayra.Api.Application.Auth;
using Zayra.Api.Application.Employees;
using Zayra.Api.Controllers;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.Approvals;
using Zayra.Api.Infrastructure.Audit;
using Zayra.Api.Infrastructure.Auth;
using Zayra.Api.Infrastructure.Documents;
using Zayra.Api.Infrastructure.Documents.Letters;
using Zayra.Api.Infrastructure.Localization;
using Zayra.Api.Infrastructure.Notifications;
using Zayra.Api.Infrastructure.Organization;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// FOUR DEFECTS OF ONE FAMILY — a value the user supplied is read, accepted, and then dropped with no
/// error. Every test here fails on the code as it was:
///
///  1. <c>ApprovalWorkflowService</c> carried a HAND-COPIED duplicate of the controller's change-applier.
///     It was missing six keys (iqamaExpiryDate, emiratesIdExpiryDate, qidExpiryDate, civilIdExpiryDate,
///     idNumber, sponsorName — four of them fail-closed PAY gates) and had NO <c>default</c> arm, so
///     approving such a change from the Approvals screen wrote nothing and said nothing.
///  2. <c>socialInsuranceReference</c> — a fail-closed PAY gate in five GCC branches — existed in the
///     readiness floor, the registry, the CSV importer and the export, but had NO write path at all:
///     every Bahraini-company employee and every AE/QA/KW/OM national was permanently payroll-blocked.
///  3. The CSV importer gated the whole payroll-profile insert on five cells, so a row supplying
///     AccountNumber / BankRoutingCode / PaymentMethod / PayrollGroup / Currency / SalaryStructureCode
///     had all of them discarded while the import reported success.
///  4. The export hardcoded CompanyLegalName / BranchCode / DepartmentCode / Manager+Supervisor codes to
///     blank and omitted ManagerEmail / SupervisorEmail entirely, so export → re-import moved people
///     between legal entities and erased every reporting line.
/// </summary>
public class EmployeeApplierParityTests
{
    // ── (a) ONE applier: the allow-list is pinned to it, and both apply paths agree ───────────

    /// <summary>
    /// Every key in the write allow-list must reach a real case in the SHARED applier. The controller's
    /// own <c>ApplyChanges</c> is now a one-line delegation to it, so this pins the implementation both
    /// paths actually run instead of one copy of it.
    /// </summary>
    [Fact]
    public void EveryEditableField_IsHandledByTheSharedApplier()
    {
        var employee = new Employee { TenantId = Guid.NewGuid(), FullName = "X", EnglishName = "X" };
        var changes = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var key in EmployeesController.EditableEmployeeFields) changes[key] = SampleFor(key);

        EmployeeChangeApplier.Apply(employee, changes)
            .Should().BeEmpty("every allow-listed key must reach a real case in the one shared applier");
    }

    /// <summary>The <c>default</c> arm the duplicate applier never had: an unrecognised key is RETURNED
    /// to the caller, never applied-and-forgotten.</summary>
    [Fact]
    public void UnrecognisedKey_IsReported_NotSilentlyDropped()
    {
        var employee = new Employee { TenantId = Guid.NewGuid(), FullName = "X", EnglishName = "X" };
        var changes = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
        {
            ["englishName"] = JsonSerializer.SerializeToElement("Applied"),
            ["BankIban"] = JsonSerializer.SerializeToElement("wrong case, ordinal switch"),
            ["notAField"] = JsonSerializer.SerializeToElement("x"),
        };

        EmployeeChangeApplier.Apply(employee, changes).Should().BeEquivalentTo(new[] { "BankIban", "notAField" });
        employee.EnglishName.Should().Be("Applied", "the recognised keys still apply");
    }

    /// <summary>
    /// THE PARITY GUARD. The Approvals screen used to apply NONE of these six keys. It is now the only
    /// approval path (the direct EmployeesController.ApproveChange endpoint is retired), so the guard
    /// is that it reaches exactly the values the approver approved.
    /// </summary>
    [Fact]
    public async Task TheApprovalCenter_AppliesEveryDriftedKey()
    {
        // ApprovalWorkflowService.DecideAsync (the normal Approvals screen).
        await using var workflowDb = CreateDb();
        var workflowFx = await SeedPendingChangeAsync(workflowDb, DriftedKeys);
        var approval = await SeedApprovalRequestAsync(workflowDb, workflowFx);
        var decided = await new ApprovalWorkflowService(workflowDb, new AuditService(workflowDb)).DecideAsync(
            workflowFx.TenantId, approval.Id, new ApprovalDecisionRequest("Approve", "Checked against the card."),
            new RequestContext("127.0.0.1", "xunit", Guid.NewGuid(), workflowFx.TenantId,
                new[] { "HR Manager" }, new[] { "approvals.override" }),
            CancellationToken.None);
        decided!.Status.Should().Be("Approved");
        var workflowState = await ReadDriftStateAsync(workflowDb, workflowFx);

        // The end state is the values the approver approved — not the old ones.
        workflowState.Should().BeEquivalentTo(new DriftState(
            new DateOnly(2027, 3, 1), new DateOnly(2027, 4, 1), new DateOnly(2027, 5, 1), new DateOnly(2027, 6, 1),
            "1099887766", "Acme Sponsor LLC"));
    }

    // ── (b) socialInsuranceReference: a fail-closed pay gate that now has a write path ───────

    /// <summary>
    /// socialInsuranceReference is approval-gated exactly like gosiReference (its Saudi counterpart): PUT files
    /// a change request (202) and writes nothing; the value reaches the payroll profile only when a DIFFERENT
    /// user approves it. Before the fix it had no write path at all; with the write path but without the
    /// SensitiveFields entry, it would have been written immediately, skipping maker-checker.
    /// </summary>
    [Fact]
    public async Task SocialInsuranceReference_SubmittedViaPut_IsApprovalGated_ThenReachesThePayrollProfile()
    {
        await using var db = CreateDb();
        var (tenantId, employee) = await SeedEmployeeAsync(db, "BH", "BH");
        db.EmployeePayrollProfiles.Add(new EmployeePayrollProfile
        {
            TenantId = tenantId, EmployeeId = employee.Id, SalaryCurrency = "BHD",
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var result = await CreateController(db, tenantId, Guid.NewGuid()).UpdateEmployee(employee.Id,
            new EmployeeUpdateRequest(Today, Changes(("socialInsuranceReference", "SIO-556677"))),
            CancellationToken.None);

        result.Should().BeOfType<AcceptedResult>("a social-insurance reference is a maker-checker field, like gosiReference");
        db.ChangeTracker.Clear();
        (await db.EmployeePayrollProfiles.AsNoTracking().SingleAsync(x => x.EmployeeId == employee.Id))
            .SocialInsuranceReference.Should().BeEmpty("nothing is written before an approver has approved it");
        var change = await db.EmployeeChangeRequests.AsNoTracking().SingleAsync(x => x.EmployeeId == employee.Id);
        change.SensitiveFields.Should().Be("socialInsuranceReference");

        (await ApprovalCenterDriver.ApproveChangeAsync(db, tenantId, change.Id)).Status.Should().Be("Approved");
        db.ChangeTracker.Clear();
        var profile = await db.EmployeePayrollProfiles.AsNoTracking().SingleAsync(x => x.EmployeeId == employee.Id);
        profile.SocialInsuranceReference.Should().Be("SIO-556677",
            "the SIO/GPSSA/GRSIA/PIFSS/SPF reference is a fail-closed PAY gate — once approved it must actually persist");
    }

    /// <summary>No profile row yet (the common case for a freshly imported employee) — the approved value
    /// still has to land, because it has nowhere else to live.</summary>
    [Fact]
    public async Task SocialInsuranceReference_Approved_CreatesThePayrollProfile_WhenTheEmployeeHasNone()
    {
        await using var db = CreateDb();
        var (tenantId, employee) = await SeedEmployeeAsync(db, "AE", "AE");

        (await CreateController(db, tenantId, Guid.NewGuid()).UpdateEmployee(employee.Id,
            new EmployeeUpdateRequest(Today, Changes(("socialInsuranceReference", "GPSSA-4321"))),
            CancellationToken.None)).Should().BeOfType<AcceptedResult>();
        db.ChangeTracker.Clear();
        var change = await db.EmployeeChangeRequests.AsNoTracking().SingleAsync(x => x.EmployeeId == employee.Id);
        (await ApprovalCenterDriver.ApproveChangeAsync(db, tenantId, change.Id)).Status.Should().Be("Approved");

        db.ChangeTracker.Clear();
        var profile = await db.EmployeePayrollProfiles.AsNoTracking().SingleAsync(x => x.EmployeeId == employee.Id);
        profile.SocialInsuranceReference.Should().Be("GPSSA-4321");
        profile.SalaryCurrency.Should().Be("SAR",
            "a row created here takes the TENANT's currency — never the entity default \"AED\"");
    }

    // ── (c) The importer's payroll gate: six columns that were read and thrown away ──────────

    [Fact]
    public async Task Import_RowWithOnlyAccountNumberRoutingAndPaymentMethod_PersistsThem()
    {
        await using var db = CreateDb();
        var tenantId = await SeedImportTenantAsync(db);
        var ctrl = HrmHierarchyTests.BuildImportControllerInternal(db, tenantId);

        var csv =
            "EmployeeCode,FullName,JoiningDate,CountryCode,AccountNumber,BankRoutingCode,PaymentMethod\n" +
            "E-PAY-1,Only Bank Cells,2024-01-01,IN,0123456789,RTG-0042,Cash\n";

        var result = await ctrl.Import(new EmployeesController.ImportEmployeesRequest(csv), CancellationToken.None);
        result.Should().BeOfType<OkObjectResult>();

        db.ChangeTracker.Clear();
        var employee = await db.Employees.AsNoTracking().SingleAsync(x => x.EmployeeCode == "E-PAY-1");
        var profile = await db.EmployeePayrollProfiles.AsNoTracking()
            .SingleOrDefaultAsync(x => x.EmployeeId == employee.Id);
        profile.Should().NotBeNull("a row that supplies payroll cells must produce a payroll profile");
        profile!.AccountNumber.Should().Be("0123456789");
        profile.BankRoutingCode.Should().Be("RTG-0042", "the WPS SIF export reads the routing code");
        profile.PaymentMethod.Should().Be("Cash");
    }

    /// <summary>A malformed salary cell is a SALARY-STRUCTURE problem; it must no longer take the row's
    /// bank details with it, and the discard it does cause is reported as a typed gap.</summary>
    [Fact]
    public async Task Import_UnparsableSalary_KeepsTheBankDetails_AndRecordsATypedGap()
    {
        await using var db = CreateDb();
        var tenantId = await SeedImportTenantAsync(db);
        var ctrl = HrmHierarchyTests.BuildImportControllerInternal(db, tenantId);

        var csv =
            "EmployeeCode,FullName,JoiningDate,CountryCode,IBAN,BasicSalary\n" +
            "E-PAY-2,Bad Salary Cell,2024-01-01,IN,SA0380000000608010167519,\"SAR 25000\"\n";

        (await ctrl.Import(new EmployeesController.ImportEmployeesRequest(csv), CancellationToken.None))
            .Should().BeOfType<OkObjectResult>();

        db.ChangeTracker.Clear();
        var employee = await db.Employees.AsNoTracking().SingleAsync(x => x.EmployeeCode == "E-PAY-2");
        var profile = await db.EmployeePayrollProfiles.AsNoTracking().SingleOrDefaultAsync(x => x.EmployeeId == employee.Id);
        profile.Should().NotBeNull("the IBAN is not the malformed cell — it must not be collateral damage");
        profile!.Iban.Should().Be("SA0380000000608010167519");
        (await db.EmployeeSalaryStructures.AsNoTracking().AnyAsync(x => x.EmployeeId == employee.Id))
            .Should().BeFalse("the salary structure is still withheld — that part of the behaviour is unchanged");
        (await db.EmployeeImportGaps.AsNoTracking()
            .Where(g => g.EmployeeId == employee.Id).Select(g => g.GapType).ToListAsync())
            .Should().Contain("pay:salaryReview", "a silent discard must leave a typed gap behind");
    }

    // ── (d) Export must round-trip org placement ─────────────────────────────────────────────

    [Fact]
    public async Task Export_EmitsResolvedOrgPlacement_AndNeverOmitsAHeader()
    {
        await using var db = CreateDb();
        var (tenantId, employee) = await SeedEmployeeAsync(db, "SA", "SA");
        var company = await db.Companies.AsNoTracking().SingleAsync(c => c.TenantId == tenantId);
        var department = await db.Departments.AsNoTracking().SingleAsync(d => d.TenantId == tenantId);
        var branch = new Branch { TenantId = tenantId, CompanyId = company.Id, Code = "RUH-01", NameEn = "Riyadh", IsActive = true };
        db.Branches.Add(branch);
        var manager = new Employee
        {
            TenantId = tenantId, EmployeeCode = "E-MGR-1", FullName = "The Manager", EnglishName = "The Manager",
            WorkEmail = "manager@acme.test", CountryCode = "SA", Status = "Active", JoiningDate = DateTime.UtcNow,
        };
        db.Employees.Add(manager);
        await db.SaveChangesAsync();

        var tracked = await db.Employees.SingleAsync(x => x.Id == employee.Id);
        tracked.CompanyId = company.Id;
        tracked.BranchId = branch.Id;
        tracked.DepartmentId = department.Id;
        tracked.ManagerEmployeeId = manager.Id;
        tracked.SupervisorEmployeeId = manager.Id;
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var file = Assert.IsType<FileContentResult>(
            await CreateController(db, tenantId, Guid.NewGuid()).Export(CancellationToken.None));
        var (headers, row) = ReadExportedRow(Encoding.UTF8.GetString(file.FileContents), "E-EXP");

        row["BranchCode"].Should().Be("RUH-01", "export → re-import must not lose the branch");
        row["DepartmentCode"].Should().Be(department.Code, "export → re-import must not lose the department");
        row["CompanyLegalName"].Should().Be(company.LegalNameEn,
            "a blank here re-imports everyone into the importer's DEFAULT company — a silent legal-entity move");
        row["ManagerEmployeeCode"].Should().Be("E-MGR-1");
        row["SupervisorEmployeeCode"].Should().Be("E-MGR-1");
        row["ManagerEmail"].Should().Be("manager@acme.test");
        row["SupervisorEmail"].Should().Be("manager@acme.test");
        // The two headers that were absent from the value map entirely: every header must be projected.
        headers.Should().Contain(new[] { "ManagerEmail", "SupervisorEmail" });
    }

    // ── Fixtures ─────────────────────────────────────────────────────────────

    /// <summary>The six keys the duplicated applier had lost. Four are fail-closed PAY gates.</summary>
    private static readonly (string Key, string Value)[] DriftedKeys =
    {
        ("iqamaExpiryDate", "2027-03-01"),
        ("emiratesIdExpiryDate", "2027-04-01"),
        ("qidExpiryDate", "2027-05-01"),
        ("civilIdExpiryDate", "2027-06-01"),
        ("idNumber", "1099887766"),
        ("sponsorName", "Acme Sponsor LLC"),
    };

    private sealed record DriftState(
        DateOnly? IqamaExpiry, DateOnly? EmiratesIdExpiry, DateOnly? QidExpiry, DateOnly? CivilIdExpiry,
        string IdNumber, string SponsorName);

    private static async Task<DriftState> ReadDriftStateAsync(ZayraDbContext db, Fixture fx)
    {
        db.ChangeTracker.Clear();
        var e = await db.Employees.AsNoTracking().SingleAsync(x => x.Id == fx.Employee.Id);
        return new DriftState(e.IqamaExpiryDate, e.EmiratesIdExpiryDate, e.QidExpiryDate, e.CivilIdExpiryDate,
            e.IdNumber, e.SponsorName);
    }

    private sealed record Fixture(Guid TenantId, Employee Employee, EmployeeChangeRequest Change, Guid RequesterUserId);

    private static async Task<Fixture> SeedPendingChangeAsync(ZayraDbContext db, (string Key, string Value)[] pairs)
    {
        var (tenantId, employee) = await SeedEmployeeAsync(db, "AE", "AE");
        var requesterUserId = Guid.NewGuid();
        var change = new EmployeeChangeRequest
        {
            TenantId = tenantId,
            EmployeeId = employee.Id,
            RequestedByUserId = requesterUserId,   // never the approver — maker-checker
            EffectiveDate = DateOnly.FromDateTime(DateTime.UtcNow.Date),
            SensitiveFields = string.Join(',', pairs.Select(p => p.Key)),
            ProposedChangesJson = JsonSerializer.Serialize(pairs.ToDictionary(p => p.Key, p => p.Value)),
        };
        db.EmployeeChangeRequests.Add(change);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return new Fixture(tenantId, employee, change, requesterUserId);
    }

    private static async Task<ApprovalRequest> SeedApprovalRequestAsync(ZayraDbContext db, Fixture fx)
    {
        var workflow = new ApprovalWorkflow
        {
            TenantId = fx.TenantId, Code = "EMPLOYEE-CHANGE", Name = "Employee Master Change Approval",
            EntityName = nameof(EmployeeChangeRequest), IsActive = true,
        };
        db.ApprovalWorkflows.Add(workflow);
        db.ApprovalWorkflowSteps.Add(new ApprovalWorkflowStep
        {
            TenantId = fx.TenantId, WorkflowId = workflow.Id, StepOrder = 1, StepName = "HR Final Approval",
            ApproverRole = "HR Manager", ApproverType = "Role", IsFinalStep = true,
        });
        var approval = new ApprovalRequest
        {
            TenantId = fx.TenantId, WorkflowId = workflow.Id, EntityName = nameof(EmployeeChangeRequest),
            EntityId = fx.Change.Id.ToString(), Title = "Employee change approval", Status = "Pending",
            CurrentStepOrder = 1, CurrentApproverRole = "HR Manager", CurrentApproverType = "Role",
            CurrentQueue = "Role:HR Manager", RequestedByUserId = fx.RequesterUserId,
            RequestedForEmployeeId = fx.Employee.Id,
        };
        db.ApprovalRequests.Add(approval);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return approval;
    }

    private static async Task<(Guid TenantId, Employee Employee)> SeedEmployeeAsync(
        ZayraDbContext db, string country, string nationality)
    {
        var tenant = new Tenant { Id = Guid.NewGuid(), Name = "T", Slug = $"t-{Guid.NewGuid():N}" };
        db.Tenants.Add(tenant);
        db.Companies.Add(new Company
        {
            TenantId = tenant.Id, LegalNameEn = "Acme Arabia Ltd", CountryCode = country, Jurisdiction = "test",
            RegistrationNumber = $"RC-{Guid.NewGuid():N}", DefaultCurrency = "SAR", IsActive = true,
        });
        var dept = new Department { TenantId = tenant.Id, Code = "OPS", NameEn = "Operations", IsActive = true };
        var desig = new Designation { TenantId = tenant.Id, Code = "OPS-OFF", TitleEn = "Operations Officer", IsActive = true };
        db.AddRange(dept, desig);
        await db.SaveChangesAsync();

        var employee = new Employee
        {
            TenantId = tenant.Id, EmployeeCode = $"E-EXP-{Guid.NewGuid():N}"[..8], FullName = "Parity Subject",
            EnglishName = "Parity Subject", PreferredName = "PS", CountryCode = country, Nationality = nationality,
            Status = "Active", JoiningDate = DateTime.UtcNow, DepartmentId = dept.Id, DesignationId = desig.Id,
        };
        db.Employees.Add(employee);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return (tenant.Id, employee);
    }

    /// <summary>A tenant the CSV importer can import into: a subscription plus a minimal readiness
    /// profile for the import country, so the rows resolve against a real policy.</summary>
    private static async Task<Guid> SeedImportTenantAsync(ZayraDbContext db)
    {
        var id = Guid.NewGuid();
        db.Tenants.Add(new Tenant { Id = id, Name = "Zayra", Slug = $"z-{id:N}" });
        db.TenantSubscriptions.Add(new TenantSubscription { TenantId = id, MaxEmployees = 1000, Plan = "Enterprise", Status = "Active" });
        db.CompanyComplianceProfiles.Add(new CompanyComplianceProfile
        {
            TenantId = id, CompanyId = null, CountryCode = "IN", Jurisdiction = string.Empty,
            CompliancePack = string.Empty, EffectiveFrom = new DateOnly(2020, 1, 1),
            Status = CompanyPolicyStatuses.Active,
            RequiredFieldsJson = """[{"key":"FullName","category":"personal","failClosed":true}]""",
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return id;
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static DateOnly Today => DateOnly.FromDateTime(DateTime.UtcNow.Date);

    private static ZayraDbContext CreateDb() =>
        new(new DbContextOptionsBuilder<ZayraDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static Dictionary<string, JsonElement> Changes(params (string Key, string Value)[] pairs)
    {
        var d = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var (k, v) in pairs) d[k] = JsonSerializer.SerializeToElement(v);
        return d;
    }

    /// <summary>A value of the JSON KIND each allow-listed key's case reads, so the sweep exercises the
    /// case instead of tripping over a type mismatch inside it.</summary>
    private static JsonElement SampleFor(string key) => key switch
    {
        "managerEmployeeId" => JsonSerializer.SerializeToElement((int?)null),
        "salary" => JsonSerializer.SerializeToElement(1234.56m),
        _ => JsonSerializer.SerializeToElement(
            key.EndsWith("Date", StringComparison.Ordinal) || key is "dateOfBirth" ? "2027-03-01" : "x"),
    };

    /// <summary>Header-keyed view of the exported row whose EmployeeCode starts with <paramref name="codePrefix"/>.
    /// Parsed with the same quoting rules Csv.Build writes.</summary>
    private static (IReadOnlyList<string> Headers, IReadOnlyDictionary<string, string> Row) ReadExportedRow(
        string csv, string codePrefix)
    {
        var lines = csv.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Length > 0).ToList();
        var headers = SplitCsvLine(lines[0]);
        foreach (var line in lines.Skip(1))
        {
            var cells = SplitCsvLine(line);
            if (!cells[0].StartsWith(codePrefix, StringComparison.Ordinal)) continue;
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < headers.Count; i++) map[headers[i]] = i < cells.Count ? cells[i] : string.Empty;
            return (headers, map);
        }
        throw new Xunit.Sdk.XunitException($"No exported row whose EmployeeCode starts with '{codePrefix}'.");
    }

    private static List<string> SplitCsvLine(string line)
    {
        var cells = new List<string>();
        var sb = new StringBuilder();
        var inQuotes = false;
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (inQuotes)
            {
                if (c == '"' && i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i++; }
                else if (c == '"') inQuotes = false;
                else sb.Append(c);
            }
            else if (c == '"') inQuotes = true;
            else if (c == ',') { cells.Add(sb.ToString()); sb.Clear(); }
            else sb.Append(c);
        }
        cells.Add(sb.ToString());
        return cells;
    }

    private static EmployeesController CreateController(ZayraDbContext db, Guid tenantId, Guid userId)
    {
        var audit = new AuditService(db);
        var controller = new EmployeesController(
            db, new Pbkdf2PasswordHasher(), audit, new FakeDocs(), new FakeNotifications(), new FakeHijri(),
            new Zayra.Api.Infrastructure.Common.DataScopeService(db), new FakeLetters(),
            new ApprovalWorkflowService(db, audit), NullLogger<EmployeesController>.Instance,
            new EstablishmentGuardService(db));
        var user = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim("tenant_id", tenantId.ToString()),
            new Claim(ClaimTypes.NameIdentifier, userId.ToString()),
            new Claim(ClaimTypes.Role, "Admin"),
            new Claim("permission", "employees.read"),
            new Claim("permission", "employees.write"),
            new Claim("permission", "employees.approve"),
            new Claim("permission", "employees.sensitive"),
            new Claim("is_group_scope", "true"),
        }, "Test"));
        controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = user } };
        return controller;
    }

    // ── Stubs (mirrors EmployeeBankChangeSyncTests) ──────────────────────────

    private sealed class FakeDocs : IDocumentStorage
    {
        public Task<StoredDocument> SaveAsync(Guid tenantId, IFormFile file, CancellationToken ct) =>
            Task.FromResult(new StoredDocument(file.FileName, file.ContentType ?? "application/octet-stream", "storage/test", "/tmp/test"));
        public string ResolvePath(string storageUrl) => "/tmp/test";
        public Task<byte[]> GetBytesAsync(Guid tenantId, string storageUrl, CancellationToken ct = default) =>
            Task.FromResult(Array.Empty<byte>());
    }

    private sealed class FakeNotifications : INotificationService
    {
        public Task NotifyAsync(Guid t, Guid? u, string title, string msg, string entity, string? entityId, CancellationToken ct) => Task.CompletedTask;
        public Task SendEmailAsync(Guid t, string code, string to, string name, Dictionary<string, string> vars, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class FakeHijri : IHijriDateService
    {
        public DateConversionDto FromGregorian(DateOnly date) => new(date.ToString("yyyy-MM-dd"), "1447-01-01", 1447, 1, 1);
    }

    private sealed class FakeLetters : ILetterService
    {
        public Task<byte[]> GeneratePayslipPdfAsync(PayslipData data, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
        public Task<byte[]> GenerateAppointmentLetterAsync(LetterData data, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
        public Task<byte[]> GenerateExperienceLetterAsync(LetterData data, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
        public Task<byte[]> GenerateOfferLetterAsync(OfferLetterData data, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
    }
}
