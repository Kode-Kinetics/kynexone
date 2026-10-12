using System.Text.Json;
using FluentAssertions;
using Zayra.Api.Application.Employees;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

public class EmployeeDetailExpiryProjectionTests
{
    private static Employee EmployeeWithCurrentExpiries() => new()
    {
        Id = 42,
        EmployeeCode = "TEST-EXPIRY-42",
        IqamaExpiryDate = new DateOnly(2027, 1, 11),
        EmiratesIdExpiryDate = new DateOnly(2027, 2, 12),
        QidExpiryDate = new DateOnly(2027, 3, 13),
        CivilIdExpiryDate = new DateOnly(2027, 4, 14),
    };

    private static EmployeeComplianceRecord[] StaleComplianceRecords() =>
    [
        new() { EmployeeId = 42, CountryCode = "SA", FieldKey = "iqama_number", ExpiryDate = new DateOnly(2025, 1, 1) },
        new() { EmployeeId = 42, CountryCode = "AE", FieldKey = "emirates_id", ExpiryDate = new DateOnly(2025, 1, 1) },
        new() { EmployeeId = 42, CountryCode = "QA", FieldKey = "qid", ExpiryDate = new DateOnly(2025, 1, 1) },
        new() { EmployeeId = 42, CountryCode = "KW", FieldKey = "civil_id", ExpiryDate = new DateOnly(2025, 1, 1) },
    ];

    private static void AssertExplicitNullExpiryProperties(EmployeeDetailDto dto)
    {
        // MVC uses Web defaults without null suppression (Program.cs AddJsonOptions).
        // An explicit null means supported-but-empty; an absent key means an older API.
        var json = JsonSerializer.SerializeToElement(dto, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        foreach (var key in new[] { "iqamaExpiryDate", "emiratesIdExpiryDate", "qidExpiryDate", "civilIdExpiryDate" })
            json.GetProperty(key).ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public void DetailProjectsCurrentScalarExpiriesInsteadOfStaleComplianceMirrors()
    {
        var employee = EmployeeWithCurrentExpiries();
        var dto = EmployeeDetailDto.Project(employee, includeSensitive: true, complianceRecords: StaleComplianceRecords());

        dto.IqamaExpiryDate.Should().Be(employee.IqamaExpiryDate);
        dto.EmiratesIdExpiryDate.Should().Be(employee.EmiratesIdExpiryDate);
        dto.QidExpiryDate.Should().Be(employee.QidExpiryDate);
        dto.CivilIdExpiryDate.Should().Be(employee.CivilIdExpiryDate);

        var json = JsonSerializer.SerializeToElement(dto, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        json.GetProperty("iqamaExpiryDate").GetString().Should().Be("2027-01-11");
        json.GetProperty("emiratesIdExpiryDate").GetString().Should().Be("2027-02-12");
        json.GetProperty("qidExpiryDate").GetString().Should().Be("2027-03-13");
        json.GetProperty("civilIdExpiryDate").GetString().Should().Be("2027-04-14");
    }

    [Fact]
    public void ClearingScalarExpiriesStaysClearedWhenOldComplianceMirrorsRemain()
    {
        var employee = EmployeeWithCurrentExpiries();
        var patch = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>("""
            {"iqamaExpiryDate":null,"emiratesIdExpiryDate":null,"qidExpiryDate":null,"civilIdExpiryDate":null}
            """)!;
        EmployeeChangeApplier.Apply(employee, patch).Should().BeEmpty();

        var dto = EmployeeDetailDto.Project(employee, includeSensitive: true, complianceRecords: StaleComplianceRecords());

        dto.IqamaExpiryDate.Should().BeNull();
        dto.EmiratesIdExpiryDate.Should().BeNull();
        dto.QidExpiryDate.Should().BeNull();
        dto.CivilIdExpiryDate.Should().BeNull();
        dto.ComplianceRecords.Should().OnlyContain(record => record.ExpiryDate == new DateOnly(2025, 1, 1));
        AssertExplicitNullExpiryProperties(dto);
    }

    [Fact]
    public void DetailMasksAllFourScalarExpiriesWithoutSensitiveAccess()
    {
        var employee = EmployeeWithCurrentExpiries();
        var dto = EmployeeDetailDto.Project(employee, includeSensitive: false);

        dto.IqamaExpiryDate.Should().BeNull();
        dto.EmiratesIdExpiryDate.Should().BeNull();
        dto.QidExpiryDate.Should().BeNull();
        dto.CivilIdExpiryDate.Should().BeNull();
        AssertExplicitNullExpiryProperties(dto);
        employee.IqamaExpiryDate.Should().NotBeNull("masking must not clear the persisted source");
        employee.EmiratesIdExpiryDate.Should().NotBeNull();
        employee.QidExpiryDate.Should().NotBeNull();
        employee.CivilIdExpiryDate.Should().NotBeNull();
    }
}
