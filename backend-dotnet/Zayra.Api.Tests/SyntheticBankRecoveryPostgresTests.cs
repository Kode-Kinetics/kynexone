using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Controllers;
using Zayra.Api.Infrastructure.Payroll;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

[Trait("Category", "Integration")]
[Collection("Integration")]
public sealed class SyntheticBankRecoveryPostgresTests
{
    private readonly PostgresFixture _fixture;
    public SyntheticBankRecoveryPostgresTests(PostgresFixture fixture) => _fixture = fixture;
    private static string TestIban(int i) => IbanValidator.WithValidCheckDigits($"SA0000{i:D18}");

    [Theory]
    [InlineData(250, false, false, false)]
    [InlineData(1, true, false, false)]
    [InlineData(1, false, true, false)]
    [InlineData(1, false, false, true)]
    public async Task RecoverMissingProfiles_NeverWritesBankDetailsOntoExistingEmployees(
        int count, bool hasApprovedBank, bool hasPendingBankChange, bool profileAlreadyRepaired)
    {
        await using var db = _fixture.CreateDb();
        var tid = await PostgresFixture.SeedMinimalTenant(db);
        db.TenantSubscriptions.Add(new TenantSubscription {
            TenantId = tid, Plan = "Enterprise", Status = "Active", MaxEmployees = 300 });
        var company = new Company {
            TenantId = tid, LegalNameEn = "Synthetic Recovery Co", CountryCode = "SA",
            Jurisdiction = "test", RegistrationNumber = "TEST-BANK-RECOVERY",
            DefaultCurrency = "SAR", IsActive = true };
        var grade = new Grade { TenantId = tid, Code = "G1", Name = "Test grade",
            Currency = "SAR", MinSalary = 1, MaxSalary = 50000, IsActive = true };
        db.AddRange(company, grade);
        await db.SaveChangesAsync();
        db.GradePayScaleComponents.Add(new GradePayScaleComponent {
            TenantId = tid, GradeId = grade.Id, ComponentCode = "BASIC",
            ComponentName = "Basic", ComponentType = "Earning",
            CalculationType = "Fixed", IsActive = true });
        await db.SaveChangesAsync();
        var ctrl = HrmHierarchyTests.BuildImportControllerInternal(db, tid);
        const string header = "EmployeeCode,FullName,CompanyLegalName,Grade,BasicSalary,Currency,JoiningDate,ManagerEmployeeCode";
        var lines = Enumerable.Range(1, count).Select(i =>
            $"SB{i:D4},Synthetic Person {i},{company.LegalNameEn},G1,{5000+i},SAR,2024-01-01,{(i==1 ? "" : "SB0001")}").ToArray();
        var firstCsv = header + "\n" + string.Join("\n", lines);
        Assert.IsType<OkObjectResult>(await ctrl.Import(
            new EmployeesController.ImportEmployeesRequest(firstCsv), default));
        db.ChangeTracker.Clear();
        var salariesBefore = await db.EmployeeSalaryStructures.AsNoTracking()
            .Where(x => x.TenantId == tid).OrderBy(x => x.EmployeeId).ToListAsync();
        Assert.Equal(count, salariesBefore.Count);
        // Fault injection in an isolated PostgreSQL fixture, never in the live demo database.
        await db.EmployeePayrollProfiles.Where(x => x.TenantId == tid).ExecuteDeleteAsync();
        var first = await db.Employees.SingleAsync(x => x.TenantId == tid && x.EmployeeCode == "SB0001");
        if (profileAlreadyRepaired) db.EmployeePayrollProfiles.Add(new EmployeePayrollProfile {
            TenantId = tid, EmployeeId = first.Id, Iban = TestIban(1), BankName = "SYNTHETIC TEST BANK",
            MolId = "9000000001", SalaryCurrency = "SAR" });
        if (hasApprovedBank) { first.BankIban = TestIban(900001); first.BankName = "Approved synthetic bank"; }
        EmployeeChangeRequest? pending = null;
        if (hasPendingBankChange)
        {
            pending = new EmployeeChangeRequest { TenantId = tid, EmployeeId = first.Id,
                Status = "PendingApproval", SensitiveFields = "bankIban,bankName",
                EffectiveDate = new DateOnly(2026, 9, 1),
                ProposedChangesJson = JsonSerializer.Serialize(new { bankIban = TestIban(800001), bankName = "Pending synthetic bank" }) };
            db.EmployeeChangeRequests.Add(pending);
        }
        await db.SaveChangesAsync();
        var retryCsv = header + ",BankName,IBAN,MolId\n" + string.Join("\n",
            lines.Select((line,index) => $"{line},SYNTHETIC TEST BANK,{TestIban(index+1)},{9000000000L+index+1}"));
        Assert.All(Enumerable.Range(1,count), i => Assert.True(IbanValidator.IsSaudiIban(TestIban(i))));
        var result = Assert.IsType<OkObjectResult>(await ctrl.Import(
            new EmployeesController.ImportEmployeesRequest(retryCsv), default));
        Assert.Equal(0, JsonSerializer.SerializeToElement(result.Value).GetProperty("created").GetInt32());
        db.ChangeTracker.Clear();
        var people = await db.Employees.Where(x => x.TenantId == tid).OrderBy(x => x.EmployeeCode).ToListAsync();
        var profiles = await db.EmployeePayrollProfiles.Where(x => x.TenantId == tid).ToDictionaryAsync(x => x.EmployeeId);
        Assert.Equal(count, people.Count); Assert.Equal(count, profiles.Count);
        // The file's bank details are NEVER written onto an existing employee (#129 review, P0): a recreated profile
        // carries the non-sensitive currency only; an approved employee IBAN and a pending approval stay exactly as
        // they were; and a profile repaired by hand keeps its own values.
        foreach (var (e,index) in people.Select((e,i) => (e,i)))
        {
            var isFirst = index == 0;
            Assert.Equal(profileAlreadyRepaired && isFirst ? TestIban(1) : "", profiles[e.Id].Iban);
            Assert.Equal(profileAlreadyRepaired && isFirst ? "SYNTHETIC TEST BANK" : "", profiles[e.Id].BankName);
            Assert.Equal(profileAlreadyRepaired && isFirst ? "9000000001" : "", profiles[e.Id].MolId);
            Assert.Equal(hasApprovedBank && isFirst ? TestIban(900001) : "", e.BankIban);
            Assert.Equal("SAR", profiles[e.Id].SalaryCurrency);
        }
        // Every row whose file bank details differ from what the employee holds is named for approval.
        var expectedApproval = count - (profileAlreadyRepaired ? 1 : 0);
        Assert.Equal(expectedApproval, JsonSerializer.SerializeToElement(result.Value).GetProperty("approvalRequiredCount").GetInt32());
        var salariesAfter = await db.EmployeeSalaryStructures.AsNoTracking()
            .Where(x => x.TenantId == tid).OrderBy(x => x.EmployeeId).ToListAsync();
        Assert.Equal(salariesBefore.Select(x => (x.Id,x.BasicSalary,x.Currency,x.EffectiveDate)),
            salariesAfter.Select(x => (x.Id,x.BasicSalary,x.Currency,x.EffectiveDate)));
        Assert.Equal(count-1, people.Count(x => x.ManagerEmployeeId != null));
        if (pending != null)
        {
            var saved = await db.EmployeeChangeRequests.SingleAsync(x => x.Id == pending.Id);
            Assert.Equal("PendingApproval", saved.Status);
            Assert.Null(saved.ApprovedAtUtc); Assert.Null(saved.AppliedAtUtc);
        }
        Assert.IsType<OkObjectResult>(await ctrl.Import(
            new EmployeesController.ImportEmployeesRequest(retryCsv), default));
        db.ChangeTracker.Clear();
        Assert.Equal(count, await db.Employees.CountAsync(x => x.TenantId == tid));
        Assert.Equal(count, await db.EmployeePayrollProfiles.CountAsync(x => x.TenantId == tid));
        Assert.Equal(count, await db.EmployeeSalaryStructures.CountAsync(x => x.TenantId == tid));
    }
}
