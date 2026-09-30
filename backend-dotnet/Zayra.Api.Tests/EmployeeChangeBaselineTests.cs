using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Zayra.Api.Application.Employees;
using Zayra.Api.Controllers;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// The approval-time baseline that lets the effective-date job tell "unchanged since approval" from
/// "someone changed it since" (<see cref="EmployeeChangeBaseline"/>). Pure — no database.
/// </summary>
public sealed class EmployeeChangeBaselineTests
{
    private static readonly IDataProtector Protector =
        EmployeeChangeBaseline.CreateProtector(new EphemeralDataProtectionProvider());

    [Fact]
    public void EveryEditableField_CanBeRead_SoNoApprovedFieldEscapesTheDriftCheck()
    {
        // A key the baseline cannot read makes Capture return null, which sends the change to review —
        // safe, but it would mean every future-dated change touching that field is never applied.
        var unreadable = EmployeesController.EditableEmployeeFields.Where(k => !EmployeeChangeBaseline.CanRead(k)).ToList();
        unreadable.Should().BeEmpty();
    }

    [Fact]
    public void Capture_ThenUnprotect_RoundTrips_AndIsBoundToItsChange()
    {
        var changeId = Guid.NewGuid();
        var baseline = EmployeeChangeBaseline.Capture(Employee(), Profile(), ["bankIban", "salary", "passportExpiryDate"])!;
        var sealedBaseline = EmployeeChangeBaseline.Protect(Protector, changeId, baseline);

        sealedBaseline.Should().NotContain("SA0380000000608010167519", "the stored baseline must not hold the IBAN in clear");
        EmployeeChangeBaseline.Unprotect(Protector, changeId, sealedBaseline).Should().BeEquivalentTo(baseline);
        EmployeeChangeBaseline.Unprotect(Protector, Guid.NewGuid(), sealedBaseline)
            .Should().BeNull("a baseline copied onto another change must not verify it");
        EmployeeChangeBaseline.Unprotect(Protector, changeId, sealedBaseline[..^4] + "AAAA")
            .Should().BeNull("a tampered baseline is unreadable, never 'unchanged'");
    }

    [Fact]
    public void Drifted_SeesTheEmployeeColumn_AndThePayrollProfileColumn()
    {
        var keys = new[] { "bankIban" };
        var baseline = EmployeeChangeBaseline.Capture(Employee(), Profile(), keys)!;

        EmployeeChangeBaseline.Drifted(baseline, EmployeeChangeBaseline.Capture(Employee(), Profile(), keys)).Should().BeEmpty();

        var editedProfile = Profile();
        editedProfile.Iban = "SA4420000001234567891234";
        EmployeeChangeBaseline.Drifted(baseline, EmployeeChangeBaseline.Capture(Employee(), editedProfile, keys))
            .Should().Equal("bankIban");

        var editedEmployee = Employee();
        editedEmployee.BankIban = "SA4420000001234567891234";
        EmployeeChangeBaseline.Drifted(baseline, EmployeeChangeBaseline.Capture(editedEmployee, Profile(), keys))
            .Should().Equal("bankIban");

        EmployeeChangeBaseline.Drifted(baseline, EmployeeChangeBaseline.Capture(Employee(), null, keys))
            .Should().Equal(new[] { "bankIban" }, "a profile that disappeared is a change to what payroll pays from");
    }

    [Fact]
    public void Drifted_IgnoresRepresentationOnly_DecimalScale()
    {
        var before = Employee();
        before.Salary = 5000m;
        var after = Employee();
        after.Salary = 5000.00m;
        var baseline = EmployeeChangeBaseline.Capture(before, null, ["salary"])!;
        EmployeeChangeBaseline.Drifted(baseline, EmployeeChangeBaseline.Capture(after, null, ["salary"])).Should().BeEmpty();
    }

    [Fact]
    public void Capture_OfAnUnknownKey_IsNull_SoTheChangeIsReviewedRatherThanTrusted()
    {
        EmployeeChangeBaseline.Capture(Employee(), Profile(), ["bankIban", "notAField"]).Should().BeNull();
    }

    [Fact]
    public void Snapshot_RoundTrips_AndCarriesNoValue()
    {
        var changeId = Guid.NewGuid();
        var json = EmployeeChangeBaseline.SnapshotJson(changeId, "sealed");
        JsonDocument.Parse(json).RootElement.GetProperty("changeRequestId").GetGuid().Should().Be(changeId);
        EmployeeChangeBaseline.TryReadSnapshot(json, out var id, out var sealedBaseline).Should().BeTrue();
        id.Should().Be(changeId);
        sealedBaseline.Should().Be("sealed");
        EmployeeChangeBaseline.TryReadSnapshot("{}", out _, out _).Should().BeFalse();
    }

    private static Employee Employee() => new()
    {
        Id = 7, TenantId = Guid.NewGuid(), EmployeeCode = "B-1", BankName = "Old Bank",
        BankIban = "SA0380000000608010167519", Salary = 5000m, PassportExpiryDate = new DateOnly(2030, 1, 31),
    };

    private static EmployeePayrollProfile Profile() => new()
    {
        EmployeeId = 7, BankName = "Old Bank", Iban = "SA0380000000608010167519",
    };
}
