using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Controllers;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.Payroll;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// A NEW employee's bank details set by an import used to carry a "verify before first payroll" warning in the
/// import RESPONSE only: close the page and it was gone. It is now a persisted typed gap
/// (<see cref="EmployeeImportGap.BankDetailsUnverified"/>) that shows on the employee's readiness checklist,
/// raises a pre-lock payroll Warning, and is cleared by a second person with an audit row.
/// </summary>
public class ImportedBankDetailsVerificationTests
{
    private const string ValidSaudiIban = "SA4420000001234567891234";

    private static ZayraDbContext NewDb(params Microsoft.EntityFrameworkCore.Diagnostics.IInterceptor[] interceptors)
    {
        var options = new DbContextOptionsBuilder<ZayraDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString());
        if (interceptors.Length > 0) options.AddInterceptors(interceptors);
        return new ZayraDbContext(options.Options);
    }

    private static async Task<Guid> SeedTenant(ZayraDbContext db)
    {
        var id = Guid.NewGuid();
        db.Tenants.Add(new Tenant { Id = id, Name = "Zayra", Slug = $"z-{id:N}" });
        db.TenantSubscriptions.Add(new TenantSubscription { TenantId = id, MaxEmployees = 1000, Plan = "Enterprise", Status = "Active" });
        db.CompanyComplianceProfiles.Add(new CompanyComplianceProfile
        {
            TenantId = id, CompanyId = null, CountryCode = "IN", Jurisdiction = string.Empty, CompliancePack = string.Empty,
            EffectiveFrom = new DateOnly(2020, 1, 1), Status = CompanyPolicyStatuses.Active,
            RequiredFieldsJson = """[{"key":"FullName","category":"personal","failClosed":true}]""",
        });
        await db.SaveChangesAsync();
        return id;
    }

    private static async Task<(ZayraDbContext Db, Guid Tenant, EmployeesController Importer, Employee WithIban, Employee WithoutBank)> ImportTwoAsync(
        params Microsoft.EntityFrameworkCore.Diagnostics.IInterceptor[] interceptors)
    {
        var db = NewDb(interceptors);
        var tenantId = await SeedTenant(db);
        var importer = Hr(db, tenantId);
        var csv =
            "EmployeeCode,FullName,CountryCode,JoiningDate,BankName,IBAN\n" +
            $"BNK-1,Alice,IN,2024-01-01,Bank A,{ValidSaudiIban}\n" +
            "BNK-2,Bob,IN,2024-01-02,,\n";
        (await importer.Import(new EmployeesController.ImportEmployeesRequest(csv), CancellationToken.None))
            .Should().BeOfType<OkObjectResult>();
        var withIban = await db.Employees.SingleAsync(e => e.EmployeeCode == "BNK-1");
        var withoutBank = await db.Employees.SingleAsync(e => e.EmployeeCode == "BNK-2");
        return (db, tenantId, importer, withIban, withoutBank);
    }

    /// <summary>An HR user: org-wide scope and sight of bank details.</summary>
    private static EmployeesController Hr(ZayraDbContext db, Guid tenantId) => AsHr(HrmHierarchyTests.BuildImportControllerInternal(db, tenantId));

    private static EmployeesController AsHr(EmployeesController c)
    {
        c.User.AddIdentity(new ClaimsIdentity(
            new[] { "employees.read", "employees.write", "employees.sensitive" }.Select(p => new Claim("permission", p))));
        return c;
    }

    private static Task<List<EmployeeImportGap>> OpenBankGaps(ZayraDbContext db, int employeeId) =>
        db.EmployeeImportGaps.AsNoTracking()
            .Where(g => g.EmployeeId == employeeId && g.GapType == EmployeeImportGap.BankDetailsUnverified && g.ResolvedAtUtc == null)
            .ToListAsync();

    [Fact]
    public async Task Importing_a_new_employee_with_an_iban_persists_the_verify_flag()
    {
        var (db, _, _, withIban, withoutBank) = await ImportTwoAsync();

        (await OpenBankGaps(db, withIban.Id)).Should().ContainSingle("the warning outlives the import response");
        (await OpenBankGaps(db, withoutBank.Id)).Should().BeEmpty("no bank details were imported for this row");
        (await db.Employees.AsNoTracking().SingleAsync(e => e.Id == withIban.Id)).ReadinessState
            .Should().NotBe("Ready", "the open flag shows on the employee's readiness checklist");
    }

    [Fact]
    public async Task Only_a_second_person_can_confirm_the_imported_details_and_it_is_audited()
    {
        var (db, tenantId, importer, withIban, _) = await ImportTwoAsync();
        var request = new ConfirmBankDetailsRequest("Checked against the employee's bank letter");

        (await importer.ConfirmImportedBankDetails(withIban.Id, request, CancellationToken.None))
            .Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(403, "the importer cannot be their own second review");

        var reviewer = Hr(db, tenantId);
        (await reviewer.ConfirmImportedBankDetails(withIban.Id, new ConfirmBankDetailsRequest(" "), CancellationToken.None))
            .Should().BeOfType<BadRequestObjectResult>("a note saying how it was confirmed is required");
        (await OpenBankGaps(db, withIban.Id)).Should().ContainSingle();

        (await reviewer.ConfirmImportedBankDetails(withIban.Id, request, CancellationToken.None))
            .Should().BeOfType<OkObjectResult>();
        (await OpenBankGaps(db, withIban.Id)).Should().BeEmpty();
        (await db.AuditLogs.AsNoTracking().AnyAsync(a => a.Action == "employee.imported_bank_details_confirmed"
            && a.EntityId == withIban.Id.ToString() && a.Metadata!.Contains("bank letter")))
            .Should().BeTrue();

        (await reviewer.ConfirmImportedBankDetails(withIban.Id, request, CancellationToken.None))
            .Should().BeOfType<ConflictObjectResult>("there is nothing left to confirm");
    }

    [Fact]
    public async Task The_employee_cannot_confirm_their_own_bank_details()
    {
        var (db, tenantId, _, withIban, _) = await ImportTwoAsync();
        var reviewer = Hr(db, tenantId);
        var reviewerId = Guid.Parse(reviewer.User.FindFirstValue(ClaimTypes.NameIdentifier)!);
        (await db.Employees.SingleAsync(e => e.Id == withIban.Id)).UserAccountId = reviewerId;
        await db.SaveChangesAsync();

        (await reviewer.ConfirmImportedBankDetails(withIban.Id, new ConfirmBankDetailsRequest("mine"), CancellationToken.None))
            .Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(403);
        (await OpenBankGaps(db, withIban.Id)).Should().ContainSingle();
    }

    /// <summary>The profile's CreatedBy is not the only record of who imported: the import's commit marker
    /// names the actor too, so a profile with no CreatedBy does not let the importer through.</summary>
    [Fact]
    public async Task With_no_profile_creator_the_import_marker_still_identifies_the_importer()
    {
        var (db, tenantId, importer, withIban, _) = await ImportTwoAsync();
        (await db.EmployeePayrollProfiles.SingleAsync(p => p.EmployeeId == withIban.Id)).CreatedBy = null;
        await db.SaveChangesAsync();

        (await importer.ConfirmImportedBankDetails(withIban.Id, new ConfirmBankDetailsRequest("checked"), CancellationToken.None))
            .Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(403);
        (await OpenBankGaps(db, withIban.Id)).Should().ContainSingle();

        (await Hr(db, tenantId).ConfirmImportedBankDetails(withIban.Id, new ConfirmBankDetailsRequest("checked"), CancellationToken.None))
            .Should().BeOfType<OkObjectResult>("someone else can still confirm");
    }

    [Fact]
    public async Task With_no_record_of_the_importer_at_all_confirmation_is_refused()
    {
        // A flag whose import left no commit marker (the audit log is append-only, so this is a batch id with no
        // marker) on an employee with no imported payroll profile: nothing names the importer.
        var (db, tenantId, _, _, orphan) = await ImportTwoAsync();
        db.EmployeeImportGaps.Add(new EmployeeImportGap
        {
            TenantId = tenantId, ImportBatchId = Guid.NewGuid(), EmployeeId = orphan.Id,
            GapType = EmployeeImportGap.BankDetailsUnverified, GapCategory = "pay", Detail = "legacy",
        });
        await db.SaveChangesAsync();

        var refused = await Hr(db, tenantId).ConfirmImportedBankDetails(orphan.Id, new ConfirmBankDetailsRequest("checked"), CancellationToken.None);

        var result = refused.Should().BeOfType<ObjectResult>().Subject;
        result.StatusCode.Should().Be(403, "an unknown importer fails closed: the second-person check cannot be made");
        result.Value!.ToString().Should().Contain("importer_unknown");
        (await OpenBankGaps(db, orphan.Id)).Should().ContainSingle();
    }

    private sealed class FailOnConfirmAudit : Microsoft.EntityFrameworkCore.Diagnostics.SaveChangesInterceptor
    {
        public override ValueTask<Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<int>> SavingChangesAsync(
            Microsoft.EntityFrameworkCore.Diagnostics.DbContextEventData eventData,
            Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<int> result, CancellationToken ct = default)
        {
            if (eventData.Context!.ChangeTracker.Entries<AuditLog>()
                .Any(e => e.State == EntityState.Added && e.Entity.Action == "employee.imported_bank_details_confirmed"))
                throw new DbUpdateException("Injected audit failure");
            return base.SavingChangesAsync(eventData, result, ct);
        }
    }

    [Fact]
    public async Task If_the_audit_row_cannot_be_written_the_flag_stays_open()
    {
        var (db, tenantId, _, withIban, _) = await ImportTwoAsync(new FailOnConfirmAudit());

        var act = () => Hr(db, tenantId).ConfirmImportedBankDetails(withIban.Id, new ConfirmBankDetailsRequest("checked"), CancellationToken.None);

        await act.Should().ThrowAsync<DbUpdateException>();
        (await OpenBankGaps(db, withIban.Id)).Should().ContainSingle("the gap is cleared in the same save as its audit row, or not at all");
    }

    // ── The pre-lock rule itself ────────────────────────────────────────────────────────────

    private static PayrollValidationContext Context(string paymentMethod, bool flagged)
    {
        var tid = Guid.NewGuid();
        var run = new PayrollRun
        {
            Id = Guid.NewGuid(), TenantId = tid, Year = 2026, Month = 6, Status = "Processed",
            TotalGrossSalary = 10_000m, TotalDeductions = 0m, TotalNetSalary = 10_000m,
        };
        var emp = new Employee { Id = 1, TenantId = tid, EmployeeCode = "EMP001", FullName = "E", Nationality = "Indian", Status = "Active" };
        var slip = new PayrollSlip
        {
            Id = Guid.NewGuid(), TenantId = tid, RunId = run.Id, EmployeeId = 1, EmployeeCode = "EMP001",
            GrossSalary = 10_000m, NetSalary = 10_000m, BasicSalary = 10_000m,
        };
        var salary = new EmployeeSalaryStructure { Id = Guid.NewGuid(), TenantId = tid, EmployeeId = 1, BasicSalary = 10_000m, IsActive = true };
        var profile = new EmployeePayrollProfile
        {
            Id = Guid.NewGuid(), TenantId = tid, EmployeeId = 1, Iban = ValidSaudiIban, SalaryCurrency = "AED", PaymentMethod = paymentMethod,
        };
        var company = new Company { Id = Guid.NewGuid(), TenantId = tid, LegalNameEn = "Co", CountryCode = "AE", DefaultCurrency = "AED", IsActive = true };
        return new PayrollValidationContext(run, [slip], [emp], [salary], [profile], [], [], company)
        {
            UnverifiedImportedBankDetails = flagged ? new HashSet<int> { 1 } : new HashSet<int>(),
        };
    }

    [Fact]
    public void A_flagged_employee_paid_by_bank_is_a_warning_not_an_error()
    {
        var result = PayrollValidationEngine.Run(Context("BankTransfer", flagged: true))
            .Should().ContainSingle(r => r.Code == PayrollValidationEngine.ImportedBankDetailsUnverified).Subject;
        result.Severity.Should().Be("Warning");
        result.EmployeeId.Should().Be(1);
    }

    [Fact]
    public void No_warning_once_confirmed_or_when_the_bank_file_does_not_pay_them()
    {
        PayrollValidationEngine.Run(Context("BankTransfer", flagged: false))
            .Should().NotContain(r => r.Code == PayrollValidationEngine.ImportedBankDetailsUnverified);
        PayrollValidationEngine.Run(Context("Cash", flagged: true))
            .Should().NotContain(r => r.Code == PayrollValidationEngine.ImportedBankDetailsUnverified,
                "a cash-paid employee is not sent money at those bank details");
    }
}
