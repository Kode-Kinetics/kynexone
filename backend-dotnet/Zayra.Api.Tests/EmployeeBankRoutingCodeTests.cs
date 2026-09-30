using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Employees;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.Employees;
using Zayra.Api.Infrastructure.Payroll;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// #129 review, P2: the WPS/SIF line reads the payroll profile's routing code LIVE, but an approved IBAN change
/// copied only the IBAN — an approved move to another bank kept paying with the OLD bank's routing code. The
/// routing code and account number had no edit key either. These pin the rule in the shared primitives both
/// approval paths call (EmployeeChangeApplier + EmployeeBankProfileSync).
/// </summary>
public class EmployeeBankRoutingCodeTests
{
    private const string OldIban = "SA0380000000608010167519";            // bank code 80
    private static readonly string OtherBankIban = IbanValidator.WithValidCheckDigits("SA0020000001234567891234"); // 20
    private static readonly string SameBankIban = IbanValidator.WithValidCheckDigits("SA0080000000006080101001");  // 80

    private static ZayraDbContext Db() =>
        new(new DbContextOptionsBuilder<ZayraDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static async Task<(Employee Emp, EmployeePayrollProfile Profile)> SeedAsync(ZayraDbContext db)
    {
        var tenant = new Tenant { Id = Guid.NewGuid(), Name = "T", Slug = $"t-{Guid.NewGuid():N}" };
        db.Tenants.Add(tenant);
        var emp = new Employee { TenantId = tenant.Id, EmployeeCode = "R-1", FullName = "Mover", Status = "Active", JoiningDate = DateTime.UtcNow.Date, BankIban = OldIban, BankName = "Old Bank" };
        db.Employees.Add(emp);
        await db.SaveChangesAsync();
        var profile = new EmployeePayrollProfile { TenantId = tenant.Id, EmployeeId = emp.Id, Iban = OldIban, BankName = "Old Bank", BankRoutingCode = "RJHISARI", AccountNumber = "608010167519", SalaryCurrency = "SAR" };
        db.EmployeePayrollProfiles.Add(profile);
        await db.SaveChangesAsync();
        return (emp, profile);
    }

    /// <summary>What both approval paths do with an approved change set, in their order.</summary>
    private static async Task ApplyApprovedAsync(ZayraDbContext db, Employee emp, Dictionary<string, object> approved)
    {
        var changes = approved.ToDictionary(kv => kv.Key, kv => JsonSerializer.SerializeToElement(kv.Value));
        EmployeeChangeApplier.Apply(emp, changes).Should().BeEmpty();
        await EmployeeChangeApplier.ApplyPayrollProfileAsync(db, emp, changes, null, default);
        await EmployeeBankProfileSync.SyncAsync(db, emp, changes.Keys, default);
        await db.SaveChangesAsync();
    }

    private static async Task<EmployeeReadiness> ReadinessAsync(ZayraDbContext db, Employee emp)
    {
        var eval = await new EmployeeActivationGuard(db).EvaluateEmployeeAsync(emp.TenantId!.Value, emp.Id, default);
        return eval!.Value.Readiness;
    }

    [Fact]
    public async Task AnApprovedMoveToAnotherBank_ClearsTheOldRoutingAndAccount_AndPayGates()
    {
        await using var db = Db();
        var (emp, profile) = await SeedAsync(db);

        await ApplyApprovedAsync(db, emp, new() { ["bankIban"] = OtherBankIban });

        profile.Iban.Should().Be(OtherBankIban);
        profile.BankRoutingCode.Should().BeEmpty();
        profile.AccountNumber.Should().BeEmpty();
        (await db.EmployeeHistories.CountAsync(h => h.EmployeeId == emp.Id && h.EventType == EmployeeBankProfileSync.RoutingCodeClearedEventType))
            .Should().Be(1);
        (await ReadinessAsync(db, emp)).PayBlocking.Should().Contain(i => i.Key == "BankRoutingCode" && i.Gate == "pay");
    }

    [Fact]
    public async Task ANewAccountAtTheSameBank_KeepsTheRoutingCode_AndDoesNotPayGate()
    {
        await using var db = Db();
        var (emp, profile) = await SeedAsync(db);

        await ApplyApprovedAsync(db, emp, new() { ["bankIban"] = SameBankIban });

        profile.Iban.Should().Be(SameBankIban);
        profile.BankRoutingCode.Should().Be("RJHISARI");
        profile.AccountNumber.Should().Be("608010167519", "only the approved IBAN changes at the same bank");
        (await ReadinessAsync(db, emp)).PayBlocking.Should().NotContain(i => i.Key == "BankRoutingCode");
    }

    [Fact]
    public async Task ARoutingCodeApprovedWithTheNewIban_IsKept_AndThereIsNoPayGate()
    {
        await using var db = Db();
        var (emp, profile) = await SeedAsync(db);

        await ApplyApprovedAsync(db, emp, new() { ["bankIban"] = OtherBankIban, ["bankRoutingCode"] = "RIBLSARI", ["accountNumber"] = "1234567891234" });

        profile.Iban.Should().Be(OtherBankIban);
        profile.BankRoutingCode.Should().Be("RIBLSARI");
        profile.AccountNumber.Should().Be("1234567891234");
        (await ReadinessAsync(db, emp)).PayBlocking.Should().NotContain(i => i.Key == "BankRoutingCode");
    }

    [Theory]
    [InlineData("SA0380000000608010167519", "SA:80")]
    [InlineData("AE070331234567890123456", "AE:033")]
    [InlineData("QA58DOHB00001234567890ABCDEFG", "QA:DOHB")]
    [InlineData("KW81CBKU0000000000001234560101", "KW:CBKU")]
    [InlineData("SA03 8000 0000 6080 1016 7519", "SA:80")]
    [InlineData("SA0", null)]
    public void BankIdentifier_ReadsTheBankCodeFromTheIban(string iban, string? expected)
        => EmployeeBankProfileSync.BankIdentifier(iban).Should().Be(expected);
}
