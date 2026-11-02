using System.Text.Json;
using FluentAssertions;
using Zayra.Api.Application.Employees;
using Zayra.Api.Models;

namespace Zayra.Api.Tests.Security;

/// <summary>
/// The history rows written when HR approves a personal-detail change carry the old and new values and a snapshot of the
/// employee. They are personal data exactly like the fields on the detail view, so they are masked by the SAME rule
/// (employees.sensitive, or the employee on their own record) and cannot be read around the detail masking.
/// </summary>
public class EmployeeHistoryPersonalMaskingTests
{
    private static Employee Subject() => new()
    {
        TenantId = Guid.NewGuid(), EmployeeCode = "H-1", FullName = "Hana Test", Department = "Finance", Designation = "Analyst",
        PersonalEmail = "hana.private@example.test", Phone = "+966500000001", MaritalStatus = "Married",
        EmergencyContactName = "Omar", EmergencyContactPhone = "+966500000002", SponsorName = "Sponsor Co",
        DateOfBirth = new DateOnly(1990, 5, 6),
    };

    private static EmployeeHistory Row(string field, string oldValue, string newValue, Employee e) => new()
    {
        TenantId = e.TenantId ?? Guid.Empty, EmployeeId = e.Id, EventType = field + "Change", FieldName = field,
        OldValue = oldValue, NewValue = newValue, SnapshotJson = EmployeeSafeSnapshot.Serialize(e),
    };

    private static readonly string[] PersonalSnapshotFields =
        { "PersonalEmail", "Phone", "DateOfBirth", "MaritalStatus", "EmergencyContactName", "EmergencyContactPhone", "SponsorName" };

    [Theory]
    [InlineData("Phone", "+966500000009", "+966500000008")]
    [InlineData("PersonalEmail", "old.private@example.test", "new.private@example.test")]
    [InlineData("MaritalStatus", "Single", "Married")]
    [InlineData("EmergencyContactName", "Old Name", "New Name")]
    [InlineData("EmergencyContactPhone", "+966500000003", "+966500000004")]
    [InlineData("DateOfBirth", "1990-05-06", "1991-05-06")]
    public void ACallerWithoutAccess_SeesNeitherThePersonalValues_NorThePersonalFieldsInTheSnapshot(string field, string oldValue, string newValue)
    {
        var e = Subject();
        var dto = EmployeeHistoryDto.Project(Row(field, oldValue, newValue, e), includePersonal: false);

        dto.OldValue.Should().NotContain(oldValue);
        dto.NewValue.Should().NotContain(newValue);
        dto.FieldName.Should().Be(field, "which field changed is not itself personal data");
        using var snapshot = JsonDocument.Parse(dto.SnapshotJson);
        foreach (var name in PersonalSnapshotFields)
            snapshot.RootElement.EnumerateObject().Should().NotContain(p => p.Name == name, $"{name} must not be readable through history");
        dto.SnapshotJson.Should().NotContain("hana.private@example.test").And.NotContain("+966500000001");
    }

    [Fact]
    public void AnEntitledCaller_OrTheSubject_StillSeesEverything()
    {
        var e = Subject();
        var row = Row("Phone", "+966500000009", "+966500000008", e);
        var dto = EmployeeHistoryDto.Project(row, includePersonal: true);

        dto.OldValue.Should().Be("+966500000009");
        dto.NewValue.Should().Be("+966500000008");
        dto.SnapshotJson.Should().Be(row.SnapshotJson);
    }

    [Fact]
    public void NonPersonalHistory_IsNotOverMasked()
    {
        var e = Subject();
        var dto = EmployeeHistoryDto.Project(Row("Department", "Sales", "Finance", e), includePersonal: false);

        dto.OldValue.Should().Be("Sales");
        dto.NewValue.Should().Be("Finance");
        using var snapshot = JsonDocument.Parse(dto.SnapshotJson);
        snapshot.RootElement.GetProperty("Department").GetString().Should().Be("Finance");
        snapshot.RootElement.GetProperty("Designation").GetString().Should().Be("Analyst");
        snapshot.RootElement.GetProperty("EmployeeCode").GetString().Should().Be("H-1");
    }

    [Fact]
    public void TheDetailViewAppliesTheSameRuleToItsHistory()
    {
        var e = Subject();
        var rows = new[] { Row("Phone", "+966500000009", "+966500000008", e) };

        var masked = EmployeeDetailDto.Project(e, includeSensitive: false, history: rows);
        masked.History.Single().NewValue.Should().NotContain("+966500000008");

        var own = EmployeeDetailDto.Project(e, includeSensitive: false, history: rows, includeOwnPersonal: true);
        own.History.Single().NewValue.Should().Be("+966500000008");

        var entitled = EmployeeDetailDto.Project(e, includeSensitive: true, history: rows);
        entitled.History.Single().NewValue.Should().Be("+966500000008");
    }

    [Fact]
    public void MalformedSnapshot_ForACallerWithoutAccess_IsNotPassedThrough()
    {
        var e = Subject();
        var row = Row("Department", "a", "b", e);
        row.SnapshotJson = "not json hana.private@example.test";
        EmployeeHistoryDto.Project(row, includePersonal: false).SnapshotJson.Should().NotContain("hana.private");
    }
}
