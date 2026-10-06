using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Approvals;
using Zayra.Api.Application.Auth;
using Zayra.Api.Application.Employees;
using Zayra.Api.Controllers;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Approvals;
using Zayra.Api.Infrastructure.Audit;
using Zayra.Api.Infrastructure.Employees;
using Zayra.Api.Infrastructure.Payroll;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// P0 (review of #129): the import's repair path bypassed the sensitive-field approval. A row whose EmployeeCode
/// already existed "filled blank columns" — IBAN, bank name, account, routing code, social-insurance reference —
/// on the payroll profile and the employee, with no change request. The seeded HR Officer role (employees.write +
/// employees.bulk_import, NOT employees.sensitive) is refused a bank change on PUT, but could set an existing
/// employee's paying account by CSV — a TERMINATED employee's included, whose final settlement then pays it.
/// These tests assert the SAFE behaviour; the first one is the review's proof, inverted. The last two run under
/// PRODUCTION company scope (a DbContext with an HttpContext accessor, so every query filter is on) — every
/// other Postgres test here uses fx.CreateDb(), whose system scope turns ALL query filters off.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration")]
public sealed class EmployeeImportRepairSensitiveFieldsPostgresTests(PostgresFixture fx)
{
    private const string FileIban = "SA0380000000608010167519";
    private static readonly DateTime Joined = new(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static ClaimsPrincipal User(Guid tenant, string role, Guid? companyId, params string[] permissions)
    {
        var claims = new List<Claim>
        {
            new("tenant_id", tenant.ToString()),
            new(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
            new(ClaimTypes.Role, role),
        };
        claims.Add(companyId is { } c
            ? new Claim("entity_access", JsonSerializer.Serialize(new { c, r = role }))
            : new Claim("is_group_scope", "true"));
        claims.AddRange(permissions.Select(p => new Claim("permission", p)));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "Test"));
    }

    /// <summary>The seeded HR Officer: employees.write + employees.bulk_import, NOT employees.sensitive.</summary>
    private static EmployeesController HrOfficer(ZayraDbContext db, Guid tenant, Guid? companyId = null)
    {
        var ctrl = HrmHierarchyTests.BuildImportControllerInternal(db, tenant);
        ctrl.HttpContext.User = User(tenant, "HR Officer", companyId, "employees.read", "employees.write", "employees.bulk_import");
        return ctrl;
    }

    private static JsonElement Json(IActionResult r) => JsonSerializer.SerializeToElement(Assert.IsType<OkObjectResult>(r).Value);

    [Fact]
    public async Task ImportRepair_NeverWritesApprovalGatedValues_AndNeverTouchesASeparatedEmployee()
    {
        await using var db = fx.CreateDb();
        var tenant = await PostgresFixture.SeedMinimalTenant(db);
        var active = new Employee { TenantId = tenant, EmployeeCode = "BYP-1", FullName = "Existing Active", Status = "Active", CountryCode = "SA", Nationality = "Saudi", JoiningDate = Joined };
        var separated = new Employee { TenantId = tenant, EmployeeCode = "BYP-2", FullName = "Already Terminated", Status = "Terminated", CountryCode = "SA", Nationality = "Saudi", JoiningDate = Joined };
        var control = new Employee { TenantId = tenant, EmployeeCode = "BYP-3", FullName = "Edit Modal Control", Status = "Active", CountryCode = "SA", Nationality = "Saudi", JoiningDate = Joined };
        db.AddRange(active, separated, control);
        await db.SaveChangesAsync();
        // An existing payroll profile with the bank columns still blank (e.g. imported without bank details).
        db.EmployeePayrollProfiles.Add(new EmployeePayrollProfile { TenantId = tenant, EmployeeId = active.Id, SalaryCurrency = "SAR", MolId = "MOL-1" });
        await db.SaveChangesAsync();

        // CONTROL: the same user may not even REQUEST a bank change through PUT /employees/{id}.
        var ctrl = HrOfficer(db, tenant);
        var put = await ctrl.UpdateEmployee(control.Id, new EmployeeUpdateRequest(DateOnly.FromDateTime(DateTime.UtcNow),
            new() { ["bankIban"] = JsonSerializer.SerializeToElement(FileIban) }), default);
        put.Should().BeOfType<ForbidResult>();
        db.ChangeTracker.Clear();

        // The import: same codes as existing employees, carrying bank, routing, social insurance, salary — and one
        // legitimately missing, NON-sensitive detail (the payroll group) for the active employee.
        var csv = "EmployeeCode,FullName,BankName,IBAN,AccountNumber,BankRoutingCode,SocialInsuranceReference,BasicSalary,PayrollGroup\n"
                  + $"BYP-1,Existing Active,Attacker Bank,{FileIban},999000111,RTG-9,SIO-9,9000,MONTHLY\n"
                  + $"BYP-2,Already Terminated,Attacker Bank,{FileIban},999000111,RTG-9,SIO-9,9000,MONTHLY\n";
        var json = Json(await ctrl.Import(new EmployeesController.ImportEmployeesRequest(csv), default));

        json.GetProperty("created").GetInt32().Should().Be(0);
        json.GetProperty("repaired").GetInt32().Should().Be(1, "only the payroll group is filled, and only on the active employee");
        json.GetProperty("skipped").GetInt32().Should().Be(1);
        json.GetProperty("skippedSeparated").GetInt32().Should().Be(1);
        var needsApproval = json.GetProperty("approvalRequired").EnumerateArray().ToList();
        needsApproval.Should().ContainSingle(a => a.GetProperty("employeeCode").GetString() == "BYP-1");
        needsApproval[0].GetProperty("fields").EnumerateArray().Select(f => f.GetString())
            .Should().BeEquivalentTo(new[] { "IBAN", "BankName", "AccountNumber", "BankRoutingCode", "SocialInsuranceReference", "Salary" });

        db.ChangeTracker.Clear();
        var activeProfile = await db.EmployeePayrollProfiles.SingleAsync(p => p.TenantId == tenant && p.EmployeeId == active.Id);
        var activeReloaded = await db.Employees.SingleAsync(e => e.Id == active.Id);
        var separatedReloaded = await db.Employees.SingleAsync(e => e.Id == separated.Id);
        // Nothing approval-gated was written: WPS/SIF pays from these columns.
        activeProfile.Iban.Should().BeEmpty();
        activeProfile.BankName.Should().BeEmpty();
        activeProfile.AccountNumber.Should().BeEmpty();
        activeProfile.BankRoutingCode.Should().BeEmpty();
        activeProfile.SocialInsuranceReference.Should().BeEmpty();
        activeProfile.MolId.Should().Be("MOL-1");
        activeReloaded.BankIban.Should().BeEmpty();
        activeReloaded.BankName.Should().BeEmpty();
        (await db.EmployeeSalaryStructures.CountAsync(s => s.TenantId == tenant)).Should().Be(0, "an existing employee's salary is never set by an import");
        // …while the NON-sensitive gap was filled.
        activeProfile.PayrollGroup.Should().Be("MONTHLY");
        // The terminated employee was not touched at all: no profile, no bank, same status.
        (await db.EmployeePayrollProfiles.AnyAsync(p => p.TenantId == tenant && p.EmployeeId == separated.Id)).Should().BeFalse();
        separatedReloaded.BankIban.Should().BeEmpty();
        separatedReloaded.Status.Should().Be("Terminated");
        // No change request was created either: nothing was applied, and nothing was requested on their behalf.
        (await db.EmployeeChangeRequests.CountAsync(c => c.TenantId == tenant)).Should().Be(0);
    }

    [Fact]
    public async Task WhenTheProfileHasAnIbanButTheEmployeeDoesNot_TheFilesAccountAndRoutingNeverLandNextToIt()
    {
        // The second hole: the "disagreeing IBAN" check compared only Employee.BankIban, so a blank employee IBAN
        // let the file's account number and routing code fill the profile beside a DIFFERENT, authoritative IBAN.
        await using var db = fx.CreateDb();
        var tenant = await PostgresFixture.SeedMinimalTenant(db);
        var emp = new Employee { TenantId = tenant, EmployeeCode = "HOLE-1", FullName = "Profile Iban Only", Status = "Active", JoiningDate = Joined, BankIban = "" };
        db.Employees.Add(emp);
        await db.SaveChangesAsync();
        const string paying = "SA4420000001234567891234";
        db.EmployeePayrollProfiles.Add(new EmployeePayrollProfile { TenantId = tenant, EmployeeId = emp.Id, Iban = paying, BankName = "Paying Bank", SalaryCurrency = "SAR" });
        await db.SaveChangesAsync();

        var csv = "EmployeeCode,FullName,IBAN,AccountNumber,BankRoutingCode,PayrollGroup\n"
                  + $"HOLE-1,Profile Iban Only,{FileIban},777000222,RTG-OTHER,MONTHLY\n";
        var json = Json(await HrOfficer(db, tenant).Import(new EmployeesController.ImportEmployeesRequest(csv), default));

        json.GetProperty("repaired").GetInt32().Should().Be(1);
        db.ChangeTracker.Clear();
        var profile = await db.EmployeePayrollProfiles.SingleAsync(p => p.TenantId == tenant && p.EmployeeId == emp.Id);
        profile.Iban.Should().Be(paying);
        profile.AccountNumber.Should().BeEmpty();
        profile.BankRoutingCode.Should().BeEmpty();
        profile.PayrollGroup.Should().Be("MONTHLY");
        (await db.Employees.SingleAsync(e => e.Id == emp.Id)).BankIban.Should().BeEmpty("the employee copy is not written by a repair either");
    }

    [Fact]
    public async Task NewEmployees_StillGetTheirBankDetails_AsInitialDataEntry()
    {
        // A NEW employee's bank details are initial data entry — POST /api/employees takes them with employees.write,
        // and so does the import. Only EXISTING employees are protected by maker-checker.
        await using var db = fx.CreateDb();
        var tenant = await PostgresFixture.SeedMinimalTenant(db);
        var csv = "EmployeeCode,FullName,BankName,IBAN,AccountNumber,BankRoutingCode,JoiningDate\n"
                  + $"NEW-1,Brand New,First Bank,{FileIban},123456,RTG-1,2024-01-01\n";
        var imported = Json(await HrOfficer(db, tenant).Import(new EmployeesController.ImportEmployeesRequest(csv), default));
        imported.GetProperty("created").GetInt32().Should().Be(1);
        imported.GetProperty("warnings").EnumerateArray().Select(w => w.GetString())
            .Should().Contain(w => w!.Contains("NEW-1") && w.Contains("before their first payroll"),
                "bank details nobody else has reviewed are flagged for first-payroll verification");

        db.ChangeTracker.Clear();
        var emp = await db.Employees.SingleAsync(e => e.TenantId == tenant && e.EmployeeCode == "NEW-1");
        var profile = await db.EmployeePayrollProfiles.SingleAsync(p => p.EmployeeId == emp.Id);
        profile.Iban.Should().Be(FileIban);
        profile.AccountNumber.Should().Be("123456");
        profile.BankRoutingCode.Should().Be("RTG-1");
        emp.BankIban.Should().Be(FileIban);
    }

    // ── PRODUCTION company scope (HttpContext accessor ⇒ every query filter on) ─────────────────────

    private async Task<(Guid Tenant, Company A, Company B)> SeedTwoCompaniesAsync()
    {
        await using var seed = fx.CreateDb();
        var tenant = await PostgresFixture.SeedMinimalTenant(seed);
        var a = new Company { TenantId = tenant, LegalNameEn = "Scope A Co", CountryCode = "SA", Jurisdiction = "test", RegistrationNumber = $"A-{Guid.NewGuid():N}"[..20], DefaultCurrency = "SAR", IsActive = true };
        var b = new Company { TenantId = tenant, LegalNameEn = "Scope B Co", CountryCode = "SA", Jurisdiction = "test", RegistrationNumber = $"B-{Guid.NewGuid():N}"[..20], DefaultCurrency = "SAR", IsActive = true };
        seed.Companies.AddRange(a, b);
        await seed.SaveChangesAsync();
        return (tenant, a, b);
    }

    [Fact]
    public async Task UnderProductionCompanyScope_TheImportRepairsOnlyVisibleEmployees_AndNeverAppliesBankDetails()
    {
        var (tenant, a, b) = await SeedTwoCompaniesAsync();
        int inA, inB;
        await using (var seed = fx.CreateDb())
        {
            var ea = new Employee { TenantId = tenant, CompanyId = a.Id, EmployeeCode = "SCP-A", FullName = "In Company A", Status = "Active", JoiningDate = Joined };
            var eb = new Employee { TenantId = tenant, CompanyId = b.Id, EmployeeCode = "SCP-B", FullName = "In Company B", Status = "Active", JoiningDate = Joined };
            seed.Employees.AddRange(ea, eb);
            await seed.SaveChangesAsync();
            inA = ea.Id; inB = eb.Id;
        }

        var accessor = new HttpContextAccessor();
        await using (var scoped = fx.CreateDbWithAccessor(accessor))
        {
            var ctrl = HrOfficer(scoped, tenant, companyId: a.Id);
            accessor.HttpContext = ctrl.HttpContext;
            var csv = "EmployeeCode,FullName,CompanyLegalName,IBAN,BankName,PayrollGroup,JoiningDate\n"
                      + $"SCP-A,In Company A,Scope A Co,{FileIban},File Bank,MONTHLY,2024-01-01\n"
                      + $"SCP-B,In Company B,Scope B Co,{FileIban},File Bank,MONTHLY,2024-01-01\n"
                      + $"SCP-NEW,New In A,Scope A Co,{FileIban},File Bank,MONTHLY,2024-01-01\n";
            // Another company's code is taken and invisible to this importer: the WHOLE file is refused, naming the
            // row, rather than landing the other two while silently skipping it (all-or-nothing import).
            var refused = await ctrl.Import(new EmployeesController.ImportEmployeesRequest(csv), default);
            refused.Should().BeOfType<UnprocessableEntityObjectResult>();
            var refusal = JsonSerializer.SerializeToElement(((ObjectResult)refused).Value);
            refusal.GetProperty("error").GetString().Should().Be("import_rows_invalid");
            refusal.GetProperty("failedRows")[0].GetProperty("row").GetInt32().Should().Be(3);
            refusal.GetProperty("message").GetString().Should().NotContain("In Company B", "no detail of another company's employee");
            scoped.ChangeTracker.Clear();
            (await scoped.Employees.IgnoreQueryFilters().AnyAsync(e => e.TenantId == tenant && e.EmployeeCode == "SCP-NEW"))
                .Should().BeFalse("a refused file writes nothing");

            csv = string.Join("\n", csv.Split('\n').Where(line => !line.StartsWith("SCP-B,")));
            var json = Json(await ctrl.Import(new EmployeesController.ImportEmployeesRequest(csv), default));
            json.GetProperty("created").GetInt32().Should().Be(1);
            json.GetProperty("repaired").GetInt32().Should().Be(1, "only the employee the importer can see");
            json.GetProperty("skipped").GetInt32().Should().Be(0);
            json.GetProperty("approvalRequired").EnumerateArray().Select(x => x.GetProperty("employeeCode").GetString())
                .Should().Equal(new[] { "SCP-A" }, "a scoped importer is told nothing about another company's employee");
        }

        await using var verify = fx.CreateDb();
        var profileA = await verify.EmployeePayrollProfiles.SingleAsync(p => p.TenantId == tenant && p.EmployeeId == inA);
        profileA.Iban.Should().BeEmpty();
        profileA.PayrollGroup.Should().Be("MONTHLY");
        (await verify.EmployeePayrollProfiles.AnyAsync(p => p.TenantId == tenant && p.EmployeeId == inB)).Should().BeFalse();
        var created = await verify.Employees.SingleAsync(e => e.TenantId == tenant && e.EmployeeCode == "SCP-NEW");
        created.CompanyId.Should().Be(a.Id);
        created.BankIban.Should().Be(FileIban, "a new employee's bank details are initial data entry");
    }

    [Fact]
    public async Task UnderProductionCompanyScope_AnApprovedMoveToAnotherBank_ClearsTheOldRoutingCode_AndPayGatesUntilANewOneIsApproved()
    {
        var (tenant, a, _) = await SeedTwoCompaniesAsync();
        int employeeId;
        await using (var seed = fx.CreateDb())
        {
            var e = new Employee { TenantId = tenant, CompanyId = a.Id, EmployeeCode = "RTG-1", FullName = "Bank Mover", Status = "Active", CountryCode = "SA", Nationality = "Saudi", IdNumber = "1010101010", JoiningDate = Joined, BankName = "Old Bank", BankIban = FileIban };
            seed.Employees.Add(e);
            await seed.SaveChangesAsync();
            employeeId = e.Id;
            seed.EmployeePayrollProfiles.Add(new EmployeePayrollProfile { TenantId = tenant, EmployeeId = e.Id, Iban = FileIban, BankName = "Old Bank", AccountNumber = "608010167519", BankRoutingCode = "RJHISARI", SalaryCurrency = "SAR", MolId = "MOL-7" });
            await seed.SaveChangesAsync();
        }
        var newBankIban = IbanValidator.WithValidCheckDigits("SA0020000001234567891234");   // bank code 20, not 80
        BankIdentifierDiffers(FileIban, newBankIban).Should().BeTrue();

        await RequestAndApproveAsync(tenant, a.Id, employeeId, "bankIban", newBankIban);

        await using (var verify = fx.CreateDb())
        {
            var profile = await verify.EmployeePayrollProfiles.SingleAsync(p => p.TenantId == tenant && p.EmployeeId == employeeId);
            profile.Iban.Should().Be(newBankIban);
            profile.BankName.Should().Be("Old Bank", "an IBAN-only approval changes only the IBAN");
            profile.BankRoutingCode.Should().BeEmpty("the old bank's routing code must never ride on the new bank's wage line");
            profile.AccountNumber.Should().BeEmpty("the old account number belongs to the old IBAN");
            profile.MolId.Should().Be("MOL-7");
            var readiness = await new EmployeeActivationGuard(verify).EvaluateEmployeeAsync(tenant, employeeId, default);
            readiness!.Value.Readiness.PayBlocking.Should().Contain(i => i.Key == "BankRoutingCode",
                "pay stays gated until the new bank's routing code is approved");
        }

        // The routing code is now an approval-gated edit key; approving it lifts the pay gate.
        await RequestAndApproveAsync(tenant, a.Id, employeeId, "bankRoutingCode", "NCBKSAJE");
        await using (var verify = fx.CreateDb())
        {
            (await verify.EmployeePayrollProfiles.SingleAsync(p => p.TenantId == tenant && p.EmployeeId == employeeId))
                .BankRoutingCode.Should().Be("NCBKSAJE");
            var readiness = await new EmployeeActivationGuard(verify).EvaluateEmployeeAsync(tenant, employeeId, default);
            readiness!.Value.Readiness.PayBlocking.Should().NotContain(i => i.Key == "BankRoutingCode");
        }
    }

    private static bool BankIdentifierDiffers(string a, string b) =>
        EmployeeBankProfileSync.BankIdentifier(a) != EmployeeBankProfileSync.BankIdentifier(b);

    /// <summary>A company-scoped HR Manager requests; a DIFFERENT company-scoped HR Manager approves in the
    /// Approval Center — both through DbContexts carrying production query filters.</summary>
    private async Task RequestAndApproveAsync(Guid tenant, Guid companyId, int employeeId, string field, string value)
    {
        Guid approvalId;
        var requesterAccessor = new HttpContextAccessor();
        await using (var requesterDb = fx.CreateDbWithAccessor(requesterAccessor))
        {
            var requester = HrmHierarchyTests.BuildImportControllerInternal(requesterDb, tenant);
            requester.HttpContext.User = User(tenant, "HR Manager", companyId, "employees.read", "employees.write", "employees.sensitive");
            requesterAccessor.HttpContext = requester.HttpContext;
            (await requester.UpdateEmployee(employeeId, new EmployeeUpdateRequest(DateOnly.FromDateTime(DateTime.UtcNow),
                new() { [field] = JsonSerializer.SerializeToElement(value) }), default)).Should().BeOfType<AcceptedResult>();
            approvalId = (await requesterDb.EmployeeChangeRequests
                .Where(c => c.TenantId == tenant && c.EmployeeId == employeeId && c.Status == "PendingApproval")
                .Select(c => c.ApprovalRequestId).SingleAsync())!.Value;
        }

        var approverAccessor = new HttpContextAccessor();
        await using var approverDb = fx.CreateDbWithAccessor(approverAccessor);
        var approver = User(tenant, "HR Manager", companyId, "employees.read", "employees.approve");
        approverAccessor.HttpContext = new DefaultHttpContext { User = approver };
        var approverId = Guid.Parse(approver.FindFirstValue(ClaimTypes.NameIdentifier)!);
        var decided = await new ApprovalWorkflowService(approverDb, new AuditService(approverDb)).DecideAsync(
            tenant, approvalId, new ApprovalDecisionRequest("Approve", "Checked against the bank letter."),
            new RequestContext("127.0.0.1", "test", approverId, tenant, ["HR Manager"], []), default);
        decided!.Status.Should().Be("Approved");
    }
}
