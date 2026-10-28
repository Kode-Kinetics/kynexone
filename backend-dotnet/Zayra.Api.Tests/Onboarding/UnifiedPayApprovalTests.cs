using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Auth;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Employees;
using Zayra.Api.Infrastructure.Employees.Onboarding;
using Zayra.Api.Models;

namespace Zayra.Api.Tests.Onboarding;

/// <summary>
/// A hire's pay has ONE approval. The figures are a pending <see cref="SalaryChangeRequest"/>; the pay ledger
/// (<see cref="CompensationApprovalGate"/>) is closed by the decision on that package and by nothing else, and it covers the
/// bank details the package does not carry. These tests pin the relationship between the two records in-process
/// (no host, no Postgres): what is fingerprinted while the package waits, that the decision closes the ledger on the same
/// figures, that a later change reopens it, and that a rejection leaves it open.
/// </summary>
public class UnifiedPayApprovalTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;
    private static readonly Guid Hr = Guid.NewGuid();

    private static ZayraDbContext CreateDb() =>
        new(new DbContextOptionsBuilder<ZayraDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private sealed record Fx(Guid TenantId, Employee Employee, SalaryChangeRequest Package, EmployeePayrollProfile Profile);

    private static RequestContext HrContext(Guid tenantId) => new(null, null, Hr, tenantId);

    private static async Task<Fx> SeedAsync(ZayraDbContext db, string iban = "SA0380000000608010167519")
    {
        var tenantId = Guid.NewGuid();
        var employee = new Employee { TenantId = tenantId, EmployeeCode = "E1", FullName = "Test Hire", Status = EmployeeStatuses.Draft, CreatedBy = Hr };
        db.Employees.Add(employee);
        await db.SaveChangesAsync(Ct);
        var profile = new EmployeePayrollProfile { TenantId = tenantId, EmployeeId = employee.Id, Iban = iban, BankName = "Bank" };
        db.EmployeePayrollProfiles.Add(profile);
        var package = new SalaryChangeRequest
        {
            TenantId = tenantId, EmployeeId = employee.Id, EmployeeCode = "E1", EmployeeName = "Test Hire", Source = SalaryChangeSources.NewHire,
            SalaryStructureId = Guid.NewGuid(), BasicSalary = 6000m, HousingAllowance = 1500m, TransportAllowance = 500m, Currency = "SAR",
            EffectiveDate = new DateOnly(2026, 1, 1), RequestedByUserId = Hr,
        };
        db.SalaryChangeRequests.Add(package);
        await db.SaveChangesAsync(Ct);
        return new Fx(tenantId, employee, package, profile);
    }

    /// <summary>What Payroll's decision does to the records: the request is Approved, the assignment exists, the employee carries the gross.</summary>
    private static async Task ApplyDecisionAsync(ZayraDbContext db, Fx f)
    {
        f.Package.Status = SalaryChangeRequest.Approved;
        db.EmployeeSalaryStructures.Add(new EmployeeSalaryStructure
        {
            TenantId = f.TenantId, EmployeeId = f.Employee.Id, SalaryStructureId = f.Package.SalaryStructureId, BasicSalary = f.Package.BasicSalary,
            HousingAllowance = f.Package.HousingAllowance, TransportAllowance = f.Package.TransportAllowance, EffectiveDate = f.Package.EffectiveDate,
            Currency = f.Package.Currency,
        });
        f.Employee.Salary = 8000m;
        await db.SaveChangesAsync(Ct);
    }

    [Fact]
    public async Task APendingFirstPackage_IsTheFigures_AndOpensTheLedger()
    {
        await using var db = CreateDb();
        var f = await SeedAsync(db);

        await CompensationApprovalGate.ReconcileAsync(db, f.Employee, null, HrContext(f.TenantId), Ct);
        await db.SaveChangesAsync(Ct);

        var live = await CompensationApprovalGate.LivePackageAsync(db, f.TenantId, f.Employee.Id, Ct);
        live.Basic.Should().Be(6000m);
        live.Housing.Should().Be(1500m);
        live.Salary.Should().Be(8000m, "the gross of the proposed package, although no salary row exists yet");
        live.Currency.Should().Be("SAR");
        (await CompensationApprovalGate.IsOpenAsync(db, f.TenantId, f.Employee.Id, Ct)).Should().BeTrue();
        (await CompensationApprovalGate.CarriesUnapprovedPayAsync(db, f.TenantId, f.Employee.Id, Ct)).Should().BeTrue();
    }

    [Fact]
    public async Task ThePendingPackageAndTheAppliedPackage_HaveTheSameFingerprint()
    {
        await using var db = CreateDb();
        var f = await SeedAsync(db);
        var pending = await CompensationApprovalGate.LiveFingerprintAsync(db, f.TenantId, f.Employee.Id, Ct);

        await ApplyDecisionAsync(db, f);

        (await CompensationApprovalGate.LiveFingerprintAsync(db, f.TenantId, f.Employee.Id, Ct))
            .Should().Be(pending, "what the approver was shown is exactly what was applied, so the approval binds to it");
    }

    [Fact]
    public async Task TheDecisionOnThePackage_ClosesTheLedger_SoNeitherRecordStillBlocks()
    {
        await using var db = CreateDb();
        var f = await SeedAsync(db);
        await CompensationApprovalGate.ReconcileAsync(db, f.Employee, null, HrContext(f.TenantId), Ct);
        await db.SaveChangesAsync(Ct);

        await ApplyDecisionAsync(db, f);
        await CompensationApprovalGate.CloseAfterPackageApprovalAsync(db, f.Employee, Ct);

        (await CompensationApprovalGate.IsOpenAsync(db, f.TenantId, f.Employee.Id, Ct)).Should().BeFalse();
        (await CompensationApprovalGate.HasDriftedFromApprovalAsync(db, f.TenantId, f.Employee.Id, Ct)).Should().BeFalse();
        (await CompensationApprovalGate.CarriesUnapprovedPayAsync(db, f.TenantId, f.Employee.Id, Ct)).Should().BeFalse();
        (await CompensationApprovalGate.PendingFirstPackageAsync(db, f.TenantId, f.Employee.Id, Ct)).Should().BeNull();
        (await CompensationApprovalGate.ApprovedFingerprintAsync(db, f.TenantId, f.Employee.Id, Ct))
            .Should().Be(await CompensationApprovalGate.LiveFingerprintAsync(db, f.TenantId, f.Employee.Id, Ct));
    }

    [Fact]
    public async Task ClosingTheLedgerTwice_IsHarmless()
    {
        await using var db = CreateDb();
        var f = await SeedAsync(db);
        await CompensationApprovalGate.ReconcileAsync(db, f.Employee, null, HrContext(f.TenantId), Ct);
        await db.SaveChangesAsync(Ct);
        await ApplyDecisionAsync(db, f);

        await CompensationApprovalGate.CloseAfterPackageApprovalAsync(db, f.Employee, Ct);
        await CompensationApprovalGate.CloseAfterPackageApprovalAsync(db, f.Employee, Ct);

        (await db.EmployeeImportGaps.CountAsync(g => g.GapType == CompensationApprovalGate.GapType, Ct)).Should().Be(1);
    }

    [Fact]
    public async Task ABankChangeAfterTheApproval_ReopensThroughTheSameLedger()
    {
        await using var db = CreateDb();
        var f = await SeedAsync(db);
        await CompensationApprovalGate.ReconcileAsync(db, f.Employee, null, HrContext(f.TenantId), Ct);
        await db.SaveChangesAsync(Ct);
        await ApplyDecisionAsync(db, f);
        await CompensationApprovalGate.CloseAfterPackageApprovalAsync(db, f.Employee, Ct);
        var before = await CompensationApprovalGate.LiveFingerprintAsync(db, f.TenantId, f.Employee.Id, Ct);

        f.Profile.Iban = "SA4420000001234567891234";
        await db.SaveChangesAsync(Ct);
        await CompensationApprovalGate.ReconcileAsync(db, f.Employee, before, HrContext(f.TenantId), Ct);
        await db.SaveChangesAsync(Ct);

        (await CompensationApprovalGate.HasDriftedFromApprovalAsync(db, f.TenantId, f.Employee.Id, Ct)).Should().BeTrue();
        (await CompensationApprovalGate.IsOpenAsync(db, f.TenantId, f.Employee.Id, Ct)).Should().BeTrue("the new account waits for a second person");
    }

    [Fact]
    public async Task ARejectedPackage_LeavesTheLedgerOpen_AndNothingIsApproved()
    {
        await using var db = CreateDb();
        var f = await SeedAsync(db);
        await CompensationApprovalGate.ReconcileAsync(db, f.Employee, null, HrContext(f.TenantId), Ct);
        await db.SaveChangesAsync(Ct);

        f.Package.Status = SalaryChangeRequest.Rejected;
        await db.SaveChangesAsync(Ct);

        (await CompensationApprovalGate.PendingFirstPackageAsync(db, f.TenantId, f.Employee.Id, Ct)).Should().BeNull();
        (await CompensationApprovalGate.IsOpenAsync(db, f.TenantId, f.Employee.Id, Ct)).Should().BeTrue("the bank details were never approved");
        (await CompensationApprovalGate.ApprovedFingerprintAsync(db, f.TenantId, f.Employee.Id, Ct)).Should().Be(CompensationApprovalGate.NoApproval);
    }

    [Fact]
    public async Task APackageProposedForAnotherEmployee_IsNotThisEmployeesFigures()
    {
        await using var db = CreateDb();
        var f = await SeedAsync(db);
        var other = new Employee { TenantId = f.TenantId, EmployeeCode = "E2", FullName = "Other", Status = EmployeeStatuses.Draft };
        db.Employees.Add(other);
        await db.SaveChangesAsync(Ct);

        (await CompensationApprovalGate.PendingFirstPackageAsync(db, f.TenantId, other.Id, Ct)).Should().BeNull();
        (await CompensationApprovalGate.CarriesUnapprovedPayAsync(db, f.TenantId, other.Id, Ct)).Should().BeFalse();
    }
}
