using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Employees;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Payroll.SaudiBankExports;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// <c>Employee.WpsBankDetails</c> holds the approved Saudi bank-file beneficiary details (<c>saudi-bank-beneficiary-v1</c> JSON: BIC
/// and three address lines, read by <see cref="ApprovedSaudiBeneficiaryDetails"/>). A payment-method change used to overwrite it
/// with the method's name ("legacy mirror"), silently wiping the approved BIC and addresses the bank export reads. The payment
/// method lives on <see cref="EmployeePayrollProfile.PaymentMethod"/> only.
/// </summary>
public class PaymentMethodKeepsBeneficiaryDetailsTests
{
    private static readonly string Approved = JsonSerializer.Serialize(new
    {
        schema = ApprovedSaudiBeneficiaryDetails.Schema, bicCode = "RJHISARI",
        employeeAddress1 = "Riyadh", employeeAddress2 = "Olaya", employeeAddress3 = "KSA",
    });

    private static async Task<(ZayraDbContext Db, Employee Employee)> SeedAsync()
    {
        var db = new ZayraDbContext(new DbContextOptionsBuilder<ZayraDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var tenantId = Guid.NewGuid();
        var employee = new Employee
        {
            TenantId = tenantId, EmployeeCode = "E1", FullName = "Omar Test", Status = "Active", WpsBankDetails = Approved,
        };
        db.Employees.Add(employee);
        await db.SaveChangesAsync();
        // No routing code on the profile: the BIC can only come from the approved beneficiary details.
        db.EmployeePayrollProfiles.Add(new EmployeePayrollProfile
        {
            TenantId = tenantId, EmployeeId = employee.Id, PaymentMethod = "WPS", BankRoutingCode = string.Empty, SalaryCurrency = "SAR",
        });
        await db.SaveChangesAsync();
        return (db, employee);
    }

    [Fact]
    public async Task APaymentMethodChange_LeavesTheApprovedBeneficiaryDetails_AndTheBankExportStillReadsTheBic()
    {
        var (db, employee) = await SeedAsync();
        await using var _ = db;
        var changes = new Dictionary<string, JsonElement> { ["paymentMethod"] = JsonSerializer.SerializeToElement("BankTransfer") };

        EmployeeChangeApplier.Apply(employee, changes).Should().BeEmpty();
        await EmployeeChangeApplier.ApplyPayrollProfileAsync(db, employee, changes, null, default);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var stored = await db.Employees.AsNoTracking().SingleAsync(e => e.Id == employee.Id);
        var profile = await db.EmployeePayrollProfiles.AsNoTracking().SingleAsync(p => p.EmployeeId == employee.Id);
        profile.PaymentMethod.Should().Be("BankTransfer");
        stored.WpsBankDetails.Should().Be(Approved, "the payment method is not the beneficiary details");
        SaudiBeneficiaryBic.Resolve(stored, profile).Should().Be("RJHISARI");
        ApprovedSaudiBeneficiaryDetails.Read(stored.WpsBankDetails)!.Address1.Should().Be("Riyadh");
    }

    [Fact]
    public async Task ThePaymentMethodBaseline_ReadsTheProfile_SoEditingTheBeneficiaryDetailsIsNotPaymentMethodDrift()
    {
        var (db, employee) = await SeedAsync();
        await using var _ = db;
        var profile = await db.EmployeePayrollProfiles.SingleAsync(p => p.EmployeeId == employee.Id);
        var baseline = EmployeeChangeBaseline.Capture(employee, profile, new[] { "paymentMethod" })!;

        employee.WpsBankDetails = Approved.Replace("RJHISARI", "NCBKSAJE");
        EmployeeChangeBaseline.Drifted(baseline, EmployeeChangeBaseline.Capture(employee, profile, new[] { "paymentMethod" }))
            .Should().BeEmpty("the beneficiary details are not the payment method");

        profile.PaymentMethod = "Cash";
        EmployeeChangeBaseline.Drifted(baseline, EmployeeChangeBaseline.Capture(employee, profile, new[] { "paymentMethod" }))
            .Should().Equal("paymentMethod");
    }

    [Fact]
    public async Task ABaselineSealedWithTheOldEmployeeMirror_StillVerifies_WhenNothingMoved()
    {
        var (db, employee) = await SeedAsync();
        await using var _ = db;
        var profile = await db.EmployeePayrollProfiles.SingleAsync(p => p.EmployeeId == employee.Id);
        // Before this change a paymentMethod baseline also carried "employee:paymentMethod" (the WpsBankDetails mirror).
        var old = new Dictionary<string, string>(EmployeeChangeBaseline.Capture(employee, profile, new[] { "paymentMethod" })!)
        {
            ["employee:paymentMethod"] = "WPS",
        };

        EmployeeChangeBaseline.Drifted(old, EmployeeChangeBaseline.Capture(employee, profile, new[] { "paymentMethod" }))
            .Should().BeEmpty("an approval requested before the mirror was retired must not be refused for the format change");
    }
}
