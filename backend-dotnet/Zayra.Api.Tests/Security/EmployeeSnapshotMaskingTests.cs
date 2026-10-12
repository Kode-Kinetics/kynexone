using System.Text.Json;
using System.Text.RegularExpressions;
using FluentAssertions;
using Zayra.Api.Application.Common;
using Zayra.Api.Application.Employees;
using Zayra.Api.Controllers;
using Zayra.Api.Infrastructure.Benefits;
using Zayra.Api.Models;

namespace Zayra.Api.Tests.Security;

/// <summary>
/// Phase 1A P0: EmployeeHistory.SnapshotJson previously persisted the raw Employee via
/// JsonSerializer.Serialize(employee) — salary, IBAN, Iqama, passport, national IDs and
/// medical data landed unmasked in the audit/history table, bypassing EmployeeSensitiveMask
/// (which only guards API read paths). EmployeeSafeSnapshot is the permitted EmployeeHistory
/// serializer. Separately scoped loan financial projections have exact documented exemptions
/// below, pinned by adversarial field-allowlist tests. These tests lint the source so a raw
/// Serialize(employee) can never be reintroduced on a SnapshotJson assignment.
/// </summary>
public class EmployeeSnapshotMaskingTests
{
    static EmployeeSnapshotMaskingTests()
    {
        // Change markers use HMAC with a server-side secret (never stored in DB). Plain
        // deterministic SHA-256 was rejected: salary entropy is low enough to brute-force.
        Environment.SetEnvironmentVariable(SensitiveValueMask.AuditSecretEnvVar, "test-audit-secret");
    }

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
        json.Should().Contain("hmac:", "salary must be stored as a keyed (HMAC) change marker, never raw or unsalted-hashed");
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
            .Should().StartWith("hmac:");
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

        a1.Should().StartWith("hmac:");
        a1.Should().Be(a2, "same value must produce the same marker so audit can detect no-change");
        a1.Should().NotBe(b, "a changed value must produce a different marker");
        a1.Should().NotContain("15750");
    }

    [Fact]
    public void HashMarker_WithoutServerSecret_DegradesToRedaction_NeverUnsaltedHash()
    {
        var saved = Environment.GetEnvironmentVariable(SensitiveValueMask.AuditSecretEnvVar);
        try
        {
            Environment.SetEnvironmentVariable(SensitiveValueMask.AuditSecretEnvVar, null);
            SensitiveValueMask.HashMarker(15750.50m).Should().Be(SensitiveValueMask.Redacted,
                "a low-entropy value must never be persisted as a crackable unkeyed digest");
        }
        finally
        {
            Environment.SetEnvironmentVariable(SensitiveValueMask.AuditSecretEnvVar, saved);
        }
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
            .SelectMany(f => RawSnapshotAssignments(File.ReadAllText(f))
                .Select(match => (File: f, match.Line, match.Text)))
            // EOSB RulesSnapshotJson serializes a non-PII formula projection, not an Employee.
            .Where(x => !x.Text.Contains("RulesSnapshotJson"))
            // LoanEmploymentSnapshot is a typed, restricted financial projection, not Employee.
            // It intentionally retains lender-company salary for affordability change detection;
            // the normal EmployeeHistory serializer would replace it with an HMAC and break that
            // contract. LoanLifecycleTests.LoanFinancialSnapshot_UsesExplicitFieldAllowlist_AndExcludesUnrelatedEmployeeSecrets
            // pins the EXACT keys, salary-change behavior, and absence of bank/identity/medical data.
            // SystemRefresh_AfterCompanyTransfer_DoesNotCopyNewEmployerCompensationIntoLenderSnapshotOrAudit
            // additionally pins foreign-company masking. No other assignment or file is exempted.
            .Where(x => !IsRestrictedLoanFinancialProjection(x.File, x.Text))
            // LoanEligibilityAssessment is likewise a typed financial decision projection, not
            // an Employee. Salary and commitments are necessary evidence of affordability.
            // LoanLifecycleTests.EligibilitySnapshots_AtCreationAndApproval_UseRestrictedFinancialAssessment
            // pins the ten assessment keys and rejects unrelated employee secrets in persisted JSON.
            // Only these two exact controller assignments are exempt, never arbitrary SnapshotJson.
            .Where(x => !IsRestrictedLoanEligibilityProjection(x.File, x.Text))
            .ToList();

        offenders.Should().BeEmpty(
            "EmployeeHistory.SnapshotJson must be produced by EmployeeSafeSnapshot.Serialize — " +
            "raw JsonSerializer.Serialize(employee) persists unmasked salary/IBAN/Iqama/passport/medical data");
    }

    // Preserve the broad suffix guard: serializing Employee into ANY snapshot can leak secrets.
    // Keep the entire physical line so the existing loan exemptions remain exact and active.
    // Whitespace includes line breaks, so splitting an unsafe assignment cannot evade this guard.
    private static IEnumerable<(int Line, string Text)> RawSnapshotAssignments(string source)
    {
        var matches = Regex.Matches(source,
            @"(?<![\w])\w*SnapshotJson\s*=\s*(?:global::)?(?:System\s*\.\s*Text\s*\.\s*Json\s*\.\s*)?JsonSerializer\s*\.\s*Serialize\s*\(");
        foreach (Match match in matches)
        {
            var start = source.LastIndexOf('\n', match.Index) + 1;
            var end = source.IndexOf('\n', match.Index);
            if (end < 0) end = source.Length;
            yield return (source[..start].Count(c => c == '\n') + 1, source[start..end]);
        }
    }

    [Theory]
    [InlineData("SnapshotJson = JsonSerializer.Serialize(employee);", true)]
    [InlineData("history.SnapshotJson=JsonSerializer.Serialize(employee);", true)]
    [InlineData("SnapshotJson = System.Text.Json.JsonSerializer.Serialize(employee);", true)]
    [InlineData("history.SnapshotJson = global::System.Text.Json.JsonSerializer.Serialize(employee);", true)]
    [InlineData("SnapshotJson\n =\n JsonSerializer\n .\n Serialize\n (employee);", true)]
    [InlineData("SnapshotJson = EmployeeSafeSnapshot.Serialize(employee);", false)]
    [InlineData("EligibilitySnapshotJson = JsonSerializer.Serialize(employee);", true)]
    [InlineData("EmploymentSnapshotJson = JsonSerializer.Serialize(employee);", true)]
    [InlineData("SourceSnapshotJson = JsonSerializer.Serialize(employee);", true)]
    [InlineData("EligibilitySnapshotJson = GradeBenefitDefaultWitness.Serialize(gradeId, benefit);", false)]
    [InlineData("SourceSnapshotJson = BenefitPayroll.SerializeWitness(witness);", false)]
    public void SnapshotLint_CoversAllSnapshotSuffixesAcrossFormatting(string source, bool rawAssignment)
        => RawSnapshotAssignments(source).Any().Should().Be(rawAssignment);

    [Fact]
    public void BenefitEligibilityWitness_UsesExplicitFieldsWithoutUnrelatedEmployeeSecrets()
    {
        var employee = MakeSensitiveEmployee();
        employee.GradeId = Guid.NewGuid();
        var ruleId = Guid.NewGuid();
        var evaluation = new GradeBenefitDefaults.BenefitEligibilityEvaluation(true, null,
            ruleId, "Gold", 5000m, "Annual", "School invoice required",
            [new("grade", "Grade", true, "The selected grade meets the rule.")]);

        var json = BenefitEligibilityWitness.Serialize(new DateTime(2026, 10, 9, 0, 0, 0, DateTimeKind.Utc),
            employee.GradeId, evaluation, 2500m);
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        root.EnumerateObject().Select(x => x.Name).Should().BeEquivalentTo(new[] {
            "evaluatedAtUtc", "GradeId", "matchedRuleId", "tierName", "maximumBenefitAmount",
            "requestedBenefitAmount", "limitPeriod", "customCriteriaNote", "checks" });
        root.GetProperty("GradeId").GetGuid().Should().Be(employee.GradeId.Value);
        root.GetProperty("matchedRuleId").GetGuid().Should().Be(ruleId);
        root.GetProperty("maximumBenefitAmount").GetDecimal().Should().Be(5000m);
        root.GetProperty("requestedBenefitAmount").GetDecimal().Should().Be(2500m);
        root.GetProperty("checks")[0].EnumerateObject().Select(x => x.Name).Should().BeEquivalentTo(
            new[] { "Key", "Label", "Passed", "Detail" });
        foreach (var secret in new[] { employee.BankIban, employee.PassportNumber, employee.IdNumber,
                     employee.MedicalInformation, employee.DisciplinaryRecords, "15750.5" })
            json.Should().NotContain(secret);
    }

    [Fact]
    public void GradeDefaultWitness_PreservesTheExactRestrictedJsonContract()
    {
        var gradeId = Guid.NewGuid();
        var benefit = new GradeBenefitDefaultDto(Guid.NewGuid(), "SCHOOL", "Schooling", "Education", "SAR",
            true, null, Guid.NewGuid(), "Gold", 5000m, "Annual", new DateOnly(2026, 10, 9), new DateOnly(2027, 10, 8));
        var json = GradeBenefitDefaultWitness.Serialize(gradeId, benefit);

        // Pin compatibility with the former anonymous projection, including property casing.
        json.Should().Be(JsonSerializer.Serialize(new { GradeId = (Guid?)gradeId, defaultBenefit = benefit }));
        using var document = JsonDocument.Parse(json);
        document.RootElement.EnumerateObject().Select(x => x.Name).Should().BeEquivalentTo(
            new[] { "GradeId", "defaultBenefit" });
        document.RootElement.GetProperty("defaultBenefit").EnumerateObject().Select(x => x.Name).Should().BeEquivalentTo(
            new[] { "BenefitPlanId", "Code", "Name", "PlanType", "Currency", "Eligible", "BlockingReason",
                "EligibilityRuleId", "EntitlementTier", "MaximumBenefitAmount", "LimitPeriod", "EffectiveFrom", "EffectiveTo" });
    }

    [Fact]
    public void BenefitPayrollWitness_PreservesTheExactRestrictedJsonContract()
    {
        var planId = Guid.NewGuid();
        var component = new BenefitPayrollComponent(Guid.NewGuid(), "SCHOOL", "Schooling", "Earning", true);
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var policy = JsonSerializer.Serialize(new BenefitPayrollPolicyEnvelope(1, planId, "SAR",
            new BenefitPayrollPolicy("SalaryAllowance", 310m, SalaryComponentId: component.Id), component), options);
        var witness = new BenefitPayrollWitness(1, planId, Guid.NewGuid(), Guid.NewGuid(), "Schooling", "SAR",
            new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 31), policy, component);
        var json = BenefitPayroll.SerializeWitness(witness);

        json.Should().Be(JsonSerializer.Serialize(witness, options));
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        root.EnumerateObject().Select(x => x.Name).Should().BeEquivalentTo(
            new[] { "version", "benefitPlanId", "enrollmentId", "chainId", "planName", "currency",
                "periodStart", "periodEnd", "policySnapshot", "component", "glDriverKey" });
        root.GetProperty("component").EnumerateObject().Select(x => x.Name).Should().BeEquivalentTo(
            new[] { "id", "code", "name", "componentType", "isTaxable" });
        root.GetProperty("policySnapshot").GetString().Should().Be(policy);
        BenefitPayroll.Read(new PayrollAdjustment { SourceType = BenefitPayroll.RecurringSource,
            SourceSnapshotJson = json }).Should().Be(witness);
    }

    [Fact]
    public void LoanFinancialProjectionExemption_DoesNotPermitOtherFilesOrRawEmployeeAssignments()
    {
        var allowedPath = Path.Combine("root", "Infrastructure", "Finance", "LoanLifecycleService.cs");
        const string allowedLine = "if (current != null) loan.EmploymentSnapshotJson = JsonSerializer.Serialize(current);";
        // These assertions exercise the scanner, not just the exemption predicate: the suffix
        // must remain guarded, and changing the typed projection to Employee must be rejected.
        IsRestrictedLoanFinancialProjection(allowedPath, RawSnapshotAssignments(allowedLine).Single().Text).Should().BeTrue();
        IsRestrictedLoanFinancialProjection(allowedPath,
            RawSnapshotAssignments(allowedLine.Replace("Serialize(current)", "Serialize(employee)")).Single().Text).Should().BeFalse();
        IsRestrictedLoanFinancialProjection(allowedPath, allowedLine).Should().BeTrue();
        IsRestrictedLoanFinancialProjection(Path.Combine("root", "EmployeeManagementService.cs"), allowedLine).Should().BeFalse();
        IsRestrictedLoanFinancialProjection(allowedPath, "loan.EmploymentSnapshotJson = JsonSerializer.Serialize(employee);").Should().BeFalse();
        IsRestrictedLoanFinancialProjection(allowedPath, "SnapshotJson = JsonSerializer.Serialize(current);").Should().BeFalse();
        IsRestrictedLoanFinancialProjection(allowedPath, "if (current != null) loan.EmploymentSnapshotJson = JsonSerializer.Serialize(employee);").Should().BeFalse();
    }

    private static bool IsRestrictedLoanFinancialProjection(string file, string line) =>
        file.EndsWith(Path.Combine("Infrastructure", "Finance", "LoanLifecycleService.cs"), StringComparison.Ordinal)
        && line.Trim() == "if (current != null) loan.EmploymentSnapshotJson = JsonSerializer.Serialize(current);";

    [Fact]
    public void LoanEligibilityProjectionExemption_OnlyPermitsExactControllerAssignments()
    {
        var path = Path.Combine("root", "Controllers", "Finance", "LoansController.cs");
        const string create = "PolicySnapshotJson = assessment.PolicySnapshotJson, EligibilitySnapshotJson = JsonSerializer.Serialize(assessment),";
        const string approve = "loan.EligibilitySnapshotJson = JsonSerializer.Serialize(assessment);";
        foreach (var line in new[] { create, approve })
        {
            IsRestrictedLoanEligibilityProjection(path, RawSnapshotAssignments(line).Single().Text).Should().BeTrue();
            IsRestrictedLoanEligibilityProjection(path,
                RawSnapshotAssignments(line.Replace("Serialize(assessment)", "Serialize(employee)")).Single().Text).Should().BeFalse();
            IsRestrictedLoanEligibilityProjection(path, line).Should().BeTrue();
            IsRestrictedLoanEligibilityProjection(Path.Combine("root", "Controllers", "EmployeesController.cs"), line).Should().BeFalse();
            IsRestrictedLoanEligibilityProjection(path, line.Replace("Serialize(assessment)", "Serialize(employee)")).Should().BeFalse();
            IsRestrictedLoanEligibilityProjection(path, line.Replace("EligibilitySnapshotJson", "SnapshotJson")).Should().BeFalse();
        }
    }

    private static bool IsRestrictedLoanEligibilityProjection(string file, string line) =>
        file.EndsWith(Path.Combine("Controllers", "Finance", "LoansController.cs"), StringComparison.Ordinal)
        && line.Trim() is "PolicySnapshotJson = assessment.PolicySnapshotJson, EligibilitySnapshotJson = JsonSerializer.Serialize(assessment),"
            or "loan.EligibilitySnapshotJson = JsonSerializer.Serialize(assessment);";

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
