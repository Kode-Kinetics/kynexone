using System.Text.Json;
using FluentAssertions;
using Zayra.Api.Application.Common;
using Zayra.Api.Application.Employees;
using Zayra.Api.Models;

namespace Zayra.Api.Tests.Security;

/// <summary>
/// Phase 1A P0: EmployeeHistory.SnapshotJson previously persisted the raw Employee via
/// JsonSerializer.Serialize(employee) — salary, IBAN, Iqama, passport, national IDs and
/// medical data landed unmasked in the audit/history table, bypassing EmployeeSensitiveMask
/// (which only guards API read paths). EmployeeSafeSnapshot is now the only permitted
/// snapshot serializer. These tests pin its guarantees and lint the source so a raw
/// Serialize(employee) can never be reintroduced on a SnapshotJson assignment.
/// </summary>
public class EmployeeSnapshotMaskingTests
{
    private static Employee MakeSensitiveEmployee() => new()
    {
        TenantId = Guid.NewGuid(),
        EmployeeCode = "SNAP-001",
        FullName = "Amina Hassan",
        Department = "Finance",
        Designation = "Accountant",
        Status = "Active",
        JoiningDate = DateTime.UtcNow.AddYears(-2),
        Salary = 15750.50m,
        BankName = "Saudi National Bank",
        BankIban = "SA4420000001234567891234",
        WpsBankDetails = "WPS-ACC-000778899",
        PassportNumber = "P123456789",
        VisaNumber = "V998877665",
        IqamaNumber = "2456789012",
        MuqeemNumber = "MQ55443322",
        GosiReference = "GOSI-1122334455",
        EmiratesId = "784-1987-1234567-1",
        Qid = "28912345678",
        CivilId = "299887766554",
        ResidencyNumber = "RES-6677889900",
        IdNumber = "1098765432",
        MedicalInformation = "Type 2 diabetic, insulin dependent",
        DisciplinaryRecords = "Written warning issued 2024-03-01",
        TerminationReason = "N/A confidential note"
    };

    [Fact]
    public void Snapshot_DoesNotContainRawIban_OrBankDetails()
    {
        var json = EmployeeSafeSnapshot.Serialize(MakeSensitiveEmployee());

        json.Should().NotContain("SA4420000001234567891234");
        json.Should().NotContain("WPS-ACC-000778899");
        json.Should().NotContain("Saudi National Bank");
        json.Should().Contain("***1234", "IBAN must keep only its last 4 characters");
    }

    [Fact]
    public void Snapshot_DoesNotContainRawSalary_ButKeepsChangeMarker()
    {
        var json = EmployeeSafeSnapshot.Serialize(MakeSensitiveEmployee());

        json.Should().NotContain("15750.5");
        json.Should().Contain("sha256:", "salary must be stored as a deterministic change marker");
    }

    [Fact]
    public void Snapshot_DoesNotContainRawIdentityNumbers()
    {
        var json = EmployeeSafeSnapshot.Serialize(MakeSensitiveEmployee());

        // Iqama / passport / visa / national & legal identity numbers
        json.Should().NotContain("2456789012");
        json.Should().NotContain("P123456789");
        json.Should().NotContain("V998877665");
        json.Should().NotContain("784-1987-1234567-1");
        json.Should().NotContain("28912345678");
        json.Should().NotContain("299887766554");
        json.Should().NotContain("RES-6677889900");
        json.Should().NotContain("1098765432");
        json.Should().NotContain("MQ55443322");
        json.Should().NotContain("GOSI-1122334455");
        // Masked last-4 survives for audit correlation
        json.Should().Contain("***9012");
        json.Should().Contain("***6789");
    }

    [Fact]
    public void Snapshot_RedactsMedicalDisciplinaryAndTerminationText()
    {
        var json = EmployeeSafeSnapshot.Serialize(MakeSensitiveEmployee());

        json.Should().NotContain("diabetic");
        json.Should().NotContain("Written warning");
        json.Should().NotContain("confidential note");
        json.Should().Contain(SensitiveValueMask.Redacted);
    }

    [Fact]
    public void Snapshot_PreservesNonSensitiveAuditContext()
    {
        var json = EmployeeSafeSnapshot.Serialize(MakeSensitiveEmployee());
        using var doc = JsonDocument.Parse(json);

        doc.RootElement.GetProperty("FullName").GetString().Should().Be("Amina Hassan");
        doc.RootElement.GetProperty("Department").GetString().Should().Be("Finance");
        doc.RootElement.GetProperty("EmployeeCode").GetString().Should().Be("SNAP-001");
        doc.RootElement.GetProperty("_snapshotPolicy").GetString().Should().Be("sensitive-masked-v1");
    }

    [Fact]
    public void Snapshot_EmptySensitiveFields_StayEmpty()
    {
        var employee = new Employee { TenantId = Guid.NewGuid(), EmployeeCode = "SNAP-002", FullName = "New Hire", JoiningDate = DateTime.UtcNow };
        var json = EmployeeSafeSnapshot.Serialize(employee);
        using var doc = JsonDocument.Parse(json);

        doc.RootElement.GetProperty("BankIban").GetString().Should().BeEmpty();
        doc.RootElement.GetProperty("Salary").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public void SanitizeFieldValue_MasksSensitiveFields_PassesOthersThrough()
    {
        EmployeeSafeSnapshot.SanitizeFieldValue("BankIban", "SA4420000001234567891234")
            .Should().Be("***1234");
        EmployeeSafeSnapshot.SanitizeFieldValue("IqamaNumber", "2456789012")
            .Should().Be("***9012");
        EmployeeSafeSnapshot.SanitizeFieldValue("Salary", "15750.50")
            .Should().StartWith("sha256:");
        EmployeeSafeSnapshot.SanitizeFieldValue("Department", "Finance")
            .Should().Be("Finance");
        EmployeeSafeSnapshot.SanitizeFieldValue("Status", "Active")
            .Should().Be("Active");
    }

    [Fact]
    public void MaskId_HandlesShortAndEmptyValues()
    {
        SensitiveValueMask.MaskId(null).Should().BeEmpty();
        SensitiveValueMask.MaskId("  ").Should().BeEmpty();
        SensitiveValueMask.MaskId("123").Should().Be("***");
        SensitiveValueMask.MaskId("1234").Should().Be("***");
        SensitiveValueMask.MaskId("12345").Should().Be("***2345");
    }

    [Fact]
    public void HashMarker_IsDeterministic_AndChangesWithValue()
    {
        var a1 = SensitiveValueMask.HashMarker(15750.50m);
        var a2 = SensitiveValueMask.HashMarker(15750.50m);
        var b = SensitiveValueMask.HashMarker(16000.00m);

        a1.Should().Be(a2, "same value must produce the same marker so audit can detect no-change");
        a1.Should().NotBe(b, "a changed value must produce a different marker");
        a1.Should().NotContain("15750");
    }

    // ── Source lint: raw employee serialization must never return on SnapshotJson ──

    [Fact]
    public void SourceLint_NoRawJsonSerializeOnSnapshotJsonAssignments()
    {
        var sourceRoot = ResolveSourceRoot();
        if (sourceRoot is null) return; // path not resolvable in this environment — skip

        var offenders = Directory
            .EnumerateFiles(sourceRoot, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}Migrations{Path.DirectorySeparatorChar}")
                        && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                        && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .SelectMany(f => File.ReadLines(f).Select((line, i) => (File: f, Line: i + 1, Text: line)))
            .Where(x => x.Text.Contains("SnapshotJson = JsonSerializer.Serialize(")
                        || x.Text.Contains("SnapshotJson = System.Text.Json.JsonSerializer.Serialize("))
            // EOSB RulesSnapshotJson serializes a non-PII formula projection, not an Employee.
            .Where(x => !x.Text.Contains("RulesSnapshotJson"))
            .ToList();

        offenders.Should().BeEmpty(
            "EmployeeHistory.SnapshotJson must be produced by EmployeeSafeSnapshot.Serialize — " +
            "raw JsonSerializer.Serialize(employee) persists unmasked salary/IBAN/Iqama/passport/medical data");
    }

    private static string? ResolveSourceRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (int i = 0; i < 6; i++)
        {
            if (dir?.Parent is null) return null;
            dir = dir.Parent;
            var candidate = Path.Combine(dir.FullName, "Zayra.Api");
            if (Directory.Exists(candidate)) return candidate;
        }
        return null;
    }
}
