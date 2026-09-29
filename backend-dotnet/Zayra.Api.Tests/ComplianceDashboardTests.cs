using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Compliance;
using Zayra.Api.Infrastructure.Seed;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// Track A PR-4 — Saudi Compliance Command Center tests.
///
/// Coverage:
///   - GOSI section uses real GosiReadinessValidator (not illustrative flat rates)
///   - GOSI blocked employees expose issue codes, not raw GosiReference values
///   - WPS section exposes MissingIbanCount from payroll profiles
///   - QIWA FeatureEnabled defaults to true when no feature-flag row exists
///   - ActionItems are rich DTOs (id, severity, module, title, route)
///   - ActionItems never contain raw GosiReference, IBAN, or Iqama values
///   - Overall compliance score is bounded 0–100
///   - Tenant isolation: tenant B data never leaks into tenant A summary
/// </summary>
public class ComplianceDashboardTests
{
    private static readonly Guid TenantA = Guid.NewGuid();
    private static readonly Guid TenantB = Guid.NewGuid();

    private static ZayraDbContext MakeDb()
    {
        var opts = new DbContextOptionsBuilder<ZayraDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new ZayraDbContext(opts);
    }

    // ── GOSI Section ──────────────────────────────────────────────────────────

    [Fact]
    public async Task GosiSection_ReflectsRealReadinessValidator_NotIllustrativeRates()
    {
        using var db = MakeDb();
        await GosiRuleSeeder.SeedDefaultsAsync(db, NullLogger.Instance);

        // Employee 1: ready (has GOSI ref + salary)
        db.Employees.Add(MakeEmployee(TenantA, 1, nationality: "Saudi", gosiRef: "SA123456"));
        db.EmployeeSalaryStructures.Add(MakeSalary(TenantA, 1, 10_000m));
        // Employee 2: blocked (missing GOSI ref)
        db.Employees.Add(MakeEmployee(TenantA, 2, nationality: "Saudi", gosiRef: ""));
        db.EmployeeSalaryStructures.Add(MakeSalary(TenantA, 2, 8_000m));
        await db.SaveChangesAsync();

        var dash = await new SaudiComplianceDashboardService(db, TestReconciliation.For(db)).BuildAsync(TenantA, CancellationToken.None);

        Assert.Equal(1, dash.Gosi.ReadyCount);
        Assert.Equal(1, dash.Gosi.BlockedCount);
        Assert.NotNull(dash.Gosi.ReadinessPercent);
        Assert.InRange(dash.Gosi.ReadinessPercent!.Value, 1.0, 99.0);
    }

    [Fact]
    public async Task GosiBlockedEmployees_ExposesIssueCodes_NotGosiReferenceValues()
    {
        using var db = MakeDb();
        await GosiRuleSeeder.SeedDefaultsAsync(db, NullLogger.Instance);

        db.Employees.Add(MakeEmployee(TenantA, 1, nationality: "Saudi", gosiRef: ""));
        db.EmployeeSalaryStructures.Add(MakeSalary(TenantA, 1, 5_000m));
        await db.SaveChangesAsync();

        var dash = await new SaudiComplianceDashboardService(db, TestReconciliation.For(db)).BuildAsync(TenantA, CancellationToken.None);

        Assert.NotEmpty(dash.Gosi.BlockedEmployees);
        foreach (var blocked in dash.Gosi.BlockedEmployees)
        {
            Assert.NotEmpty(blocked.BlockingIssueCodes);
            foreach (var code in blocked.BlockingIssueCodes)
                Assert.Matches(@"^[A-Z_]+$", code); // e.g. MISSING_GOSI_REFERENCE
        }
    }

    [Fact]
    public async Task GosiSection_GccEmployeeCount_ReflectsGccNationality()
    {
        using var db = MakeDb();
        await GosiRuleSeeder.SeedDefaultsAsync(db, NullLogger.Instance);

        var emp = MakeEmployee(TenantA, 1, nationality: "UAE", gosiRef: "GCC001");
        db.Employees.Add(emp);
        db.EmployeeSalaryStructures.Add(MakeSalary(TenantA, 1, 8_000m));
        await db.SaveChangesAsync();

        var dash = await new SaudiComplianceDashboardService(db, TestReconciliation.For(db)).BuildAsync(TenantA, CancellationToken.None);

        Assert.Equal(1, dash.Gosi.GccEmployeeCount);
    }

    // ── WPS Section ───────────────────────────────────────────────────────────

    [Fact]
    public async Task WpsSection_MissingIbanCount_ReflectsInvalidIbans()
    {
        using var db = MakeDb();

        // A payroll run is required for the IBAN check to trigger.
        db.PayrollRuns.Add(new PayrollRun { TenantId = TenantA, Year = 2026, Month = 5, Status = "Locked" });
        db.EmployeePayrollProfiles.AddRange(
            new EmployeePayrollProfile { TenantId = TenantA, EmployeeId = 1, Iban = "SA4420000001234567891234" },
            new EmployeePayrollProfile { TenantId = TenantA, EmployeeId = 2, Iban = "INVALID_IBAN" });
        await db.SaveChangesAsync();

        var dash = await new SaudiComplianceDashboardService(db, TestReconciliation.For(db)).BuildAsync(TenantA, CancellationToken.None);

        Assert.Equal(1, dash.Wps.MissingIbanCount);
    }

    [Fact]
    public async Task WpsSection_ExportHistoryCount_ReflectsWpsFileBatches()
    {
        using var db = MakeDb();

        db.WPSFileBatches.AddRange(
            new WPSFileBatch { TenantId = TenantA, SifFileName = "run1.sif", Status = "Generated" },
            new WPSFileBatch { TenantId = TenantA, SifFileName = "run2.sif", Status = "Generated" });
        await db.SaveChangesAsync();

        var dash = await new SaudiComplianceDashboardService(db, TestReconciliation.For(db)).BuildAsync(TenantA, CancellationToken.None);

        Assert.Equal(2, dash.Wps.ExportHistoryCount);
    }

    // ── QIWA Section ─────────────────────────────────────────────────────────

    [Fact]
    public async Task QiwaSection_FeatureEnabled_DefaultsTrueWhenNoFlagExists()
    {
        using var db = MakeDb();

        var dash = await new SaudiComplianceDashboardService(db, TestReconciliation.For(db)).BuildAsync(TenantA, CancellationToken.None);

        Assert.True(dash.Qiwa.FeatureEnabled);
    }

    [Fact]
    public async Task QiwaSection_FeatureEnabled_FalseWhenFlagExplicitlyDisabled()
    {
        using var db = MakeDb();

        db.TenantFeatureFlags.Add(new TenantFeatureFlag
        {
            TenantId   = TenantA,
            FeatureKey = FeatureKeys.QiwaIntegration,
            IsEnabled  = false,
        });
        await db.SaveChangesAsync();

        var dash = await new SaudiComplianceDashboardService(db, TestReconciliation.For(db)).BuildAsync(TenantA, CancellationToken.None);

        Assert.False(dash.Qiwa.FeatureEnabled);
    }

    [Fact]
    public async Task QiwaSection_CredentialConfigured_TrueWhenCredentialRowExists()
    {
        using var db = MakeDb();

        db.QiwaApiCredentials.Add(new QiwaApiCredential
        {
            TenantId              = TenantA,
            ClientId              = "test-client",
            EncryptedClientSecret = "enc",
            Environment           = "sandbox",
        });
        await db.SaveChangesAsync();

        var dash = await new SaudiComplianceDashboardService(db, TestReconciliation.For(db)).BuildAsync(TenantA, CancellationToken.None);

        Assert.True(dash.Qiwa.CredentialConfigured);
    }

    // ── Overall Score ─────────────────────────────────────────────────────────

    [Fact]
    public async Task OverallScore_IsBetweenZeroAndHundred()
    {
        using var db = MakeDb();
        await GosiRuleSeeder.SeedDefaultsAsync(db, NullLogger.Instance);

        // Deliberately messy: blocked employees + no IBAN + unapproved run.
        db.Employees.Add(MakeEmployee(TenantA, 1, nationality: "Saudi", gosiRef: ""));
        db.EmployeePayrollProfiles.Add(new EmployeePayrollProfile { TenantId = TenantA, EmployeeId = 1, Iban = "" });
        db.PayrollRuns.Add(new PayrollRun { TenantId = TenantA, Year = 2026, Month = 5, Status = "Draft" });
        await db.SaveChangesAsync();

        var dash = await new SaudiComplianceDashboardService(db, TestReconciliation.For(db)).BuildAsync(TenantA, CancellationToken.None);

        Assert.InRange(dash.Overall.ComplianceScore, 0, 100);
    }

    [Fact]
    public async Task OverallScore_HighWhenTenantIsClean()
    {
        using var db = MakeDb();
        await GosiRuleSeeder.SeedDefaultsAsync(db, NullLogger.Instance);

        db.Employees.Add(MakeEmployee(TenantA, 1, nationality: "Saudi", gosiRef: "SA9999"));
        db.EmployeeSalaryStructures.Add(MakeSalary(TenantA, 1, 10_000m));
        db.EmployeePayrollProfiles.Add(new EmployeePayrollProfile { TenantId = TenantA, EmployeeId = 1, Iban = "SA4420000001234567891234" });
        db.PayrollRuns.Add(new PayrollRun { TenantId = TenantA, Year = 2026, Month = 5, Status = "Locked" });
        db.Companies.Add(new Company { TenantId = TenantA, LegalNameEn = "Test Co", GosiEmployerId = "10000000001" });
        await db.SaveChangesAsync();

        var dash = await new SaudiComplianceDashboardService(db, TestReconciliation.For(db)).BuildAsync(TenantA, CancellationToken.None);

        Assert.True(dash.Overall.ComplianceScore >= 70,
            $"Expected score >= 70 for clean tenant; got {dash.Overall.ComplianceScore}");
    }

    // ── Action Items ──────────────────────────────────────────────────────────

    [Fact]
    public async Task ActionItems_AreRichDtos_WithRequiredFields()
    {
        using var db = MakeDb();
        await GosiRuleSeeder.SeedDefaultsAsync(db, NullLogger.Instance);

        // Missing GOSI ref → should produce an action item.
        db.Employees.Add(MakeEmployee(TenantA, 1, nationality: "Saudi", gosiRef: ""));
        db.EmployeeSalaryStructures.Add(MakeSalary(TenantA, 1, 5_000m));
        await db.SaveChangesAsync();

        var dash = await new SaudiComplianceDashboardService(db, TestReconciliation.For(db)).BuildAsync(TenantA, CancellationToken.None);

        Assert.NotEmpty(dash.ActionItems);
        foreach (var item in dash.ActionItems)
        {
            Assert.False(string.IsNullOrWhiteSpace(item.Id),    "Action item must have an Id");
            Assert.False(string.IsNullOrWhiteSpace(item.Title), "Action item must have a Title");
            Assert.False(string.IsNullOrWhiteSpace(item.Module), "Action item must have a Module");
            Assert.True(
                item.Severity is "Critical" or "High" or "Medium" or "Low",
                $"Unexpected severity: {item.Severity}");
        }
    }

    [Fact]
    public async Task ActionItems_NeverContainSensitiveValues_GosiRefOrIban()
    {
        using var db = MakeDb();
        await GosiRuleSeeder.SeedDefaultsAsync(db, NullLogger.Instance);

        const string sensitiveIban    = "SA9900000099887766554433";
        const string sensitiveGosiRef = "GOSI-PRIVATE-REF-7777";

        db.Employees.Add(MakeEmployee(TenantA, 1, nationality: "Saudi", gosiRef: sensitiveGosiRef));
        db.EmployeeSalaryStructures.Add(MakeSalary(TenantA, 1, 0m));
        db.EmployeePayrollProfiles.Add(new EmployeePayrollProfile
            { TenantId = TenantA, EmployeeId = 1, Iban = sensitiveIban });
        db.PayrollRuns.Add(new PayrollRun { TenantId = TenantA, Year = 2026, Month = 5, Status = "Draft" });
        await db.SaveChangesAsync();

        var dash = await new SaudiComplianceDashboardService(db, TestReconciliation.For(db)).BuildAsync(TenantA, CancellationToken.None);

        var json = System.Text.Json.JsonSerializer.Serialize(dash);
        Assert.DoesNotContain(sensitiveIban,    json);
        Assert.DoesNotContain(sensitiveGosiRef, json);
    }

    [Fact]
    public async Task ActionItems_SortedCriticalFirst()
    {
        using var db = MakeDb();
        await GosiRuleSeeder.SeedDefaultsAsync(db, NullLogger.Instance);

        // Multiple employees without GOSI ref → Critical action item should sort first.
        for (var i = 1; i <= 10; i++)
        {
            db.Employees.Add(MakeEmployee(TenantA, i, nationality: "Saudi", gosiRef: ""));
            db.EmployeeSalaryStructures.Add(MakeSalary(TenantA, i, 5_000m));
        }
        await db.SaveChangesAsync();

        var dash = await new SaudiComplianceDashboardService(db, TestReconciliation.For(db)).BuildAsync(TenantA, CancellationToken.None);

        // Verify sort order: Critical before High before Medium before Low.
        var severityOrder = new Dictionary<string, int>
            { { "Critical", 0 }, { "High", 1 }, { "Medium", 2 }, { "Low", 3 } };
        var ranks = dash.ActionItems.Select(a => severityOrder.GetValueOrDefault(a.Severity, 99)).ToList();
        for (var i = 1; i < ranks.Count; i++)
            Assert.True(ranks[i] >= ranks[i - 1], "Action items must be sorted by severity descending");
    }

    // ── Tenant isolation ─────────────────────────────────────────────────────

    [Fact]
    public async Task TenantIsolation_TenantBDataNeverAppearsInTenantADashboard()
    {
        using var db = MakeDb();
        await GosiRuleSeeder.SeedDefaultsAsync(db, NullLogger.Instance);

        // Tenant A: 1 ready employee.
        db.Employees.Add(MakeEmployee(TenantA, 1, nationality: "Saudi", gosiRef: "SA001"));
        db.EmployeeSalaryStructures.Add(MakeSalary(TenantA, 1, 10_000m));

        // Tenant B: 5 blocked employees + a run + missing IBAN.
        for (var i = 10; i < 15; i++)
        {
            db.Employees.Add(MakeEmployee(TenantB, i, nationality: "Saudi", gosiRef: ""));
            db.EmployeeSalaryStructures.Add(MakeSalary(TenantB, i, 5_000m));
        }
        db.PayrollRuns.Add(new PayrollRun { TenantId = TenantB, Year = 2026, Month = 5, Status = "Locked" });
        db.EmployeePayrollProfiles.Add(new EmployeePayrollProfile
            { TenantId = TenantB, EmployeeId = 10, Iban = "INVALID" });
        db.WPSFileBatches.Add(new WPSFileBatch { TenantId = TenantB, SifFileName = "b.sif", Status = "Generated" });
        await db.SaveChangesAsync();

        var dash = await new SaudiComplianceDashboardService(db, TestReconciliation.For(db)).BuildAsync(TenantA, CancellationToken.None);

        // Tenant A has exactly 1 employee.
        Assert.Equal(1, dash.Gosi.ReadyCount + dash.Gosi.BlockedCount);
        // Tenant A has no payroll run → no IBAN check triggered.
        Assert.Equal(0, dash.Wps.MissingIbanCount);
        // Tenant A has no WPS file batches.
        Assert.Equal(0, dash.Wps.ExportHistoryCount);
        Assert.Null(dash.Wps.LastRunPeriod);
    }

    // ── Zero-denominator readiness ────────────────────────────────────────────
    // The defect: a tenant with no employees showed "100% Readiness", a full green bar and
    // "0 ready / 0 blocked" immediately above its own "Company GOSI employer ID is not set"
    // warning. ready/total with total = 0 is undefined — it is not 100%, and it is not 0%.

    [Fact]
    public async Task GosiReadiness_IsNull_NotOneHundred_WhenThereAreNoEmployees()
    {
        using var db = MakeDb();
        await GosiRuleSeeder.SeedDefaultsAsync(db, NullLogger.Instance);

        var dash = await new SaudiComplianceDashboardService(db, TestReconciliation.For(db)).BuildAsync(TenantA, CancellationToken.None);

        Assert.Equal(0, dash.Gosi.ReadyCount);
        Assert.Equal(0, dash.Gosi.BlockedCount);
        Assert.Null(dash.Gosi.ReadinessPercent);
        Assert.NotEqual(100.0, dash.Gosi.ReadinessPercent ?? -1.0);
    }

    [Fact]
    public async Task QiwaReadiness_IsNull_WhenThereAreNoEmployees()
    {
        using var db = MakeDb();

        var dash = await new SaudiComplianceDashboardService(db, TestReconciliation.For(db)).BuildAsync(TenantA, CancellationToken.None);

        Assert.Equal(0, dash.Qiwa.TotalEmployees);
        Assert.Null(dash.Qiwa.ReadinessPercent);
    }

    [Fact]
    public async Task ReadinessPercent_SerializesAsNull_SoTheUiCannotRenderOneHundred()
    {
        using var db = MakeDb();
        await GosiRuleSeeder.SeedDefaultsAsync(db, NullLogger.Instance);

        var dash = await new SaudiComplianceDashboardService(db, TestReconciliation.For(db)).BuildAsync(TenantA, CancellationToken.None);
        var json = System.Text.Json.JsonSerializer.Serialize(dash);

        Assert.Contains("\"ReadinessPercent\":null", json);
        Assert.DoesNotContain("\"ReadinessPercent\":100", json);
    }

    [Fact]
    public async Task GosiReadiness_IsOneHundred_OnlyWhenEveryEmployeeIsActuallyReady()
    {
        using var db = MakeDb();
        await GosiRuleSeeder.SeedDefaultsAsync(db, NullLogger.Instance);

        db.Employees.Add(MakeEmployee(TenantA, 1, nationality: "Saudi", gosiRef: "SA001"));
        db.EmployeeSalaryStructures.Add(MakeSalary(TenantA, 1, 10_000m));
        await db.SaveChangesAsync();

        var dash = await new SaudiComplianceDashboardService(db, TestReconciliation.For(db)).BuildAsync(TenantA, CancellationToken.None);

        Assert.Equal(100.0, dash.Gosi.ReadinessPercent);
    }

    [Fact]
    public async Task GosiEmployerId_ReadsNotSet_EvenWhenNoEmployeesAreAffected()
    {
        // The affected-employee count is 0 for a tenant with no employees, which the card was
        // reading as "Set" — directly above its own "Company GOSI employer ID is not set" warning.
        using var db = MakeDb();
        await GosiRuleSeeder.SeedDefaultsAsync(db, NullLogger.Instance);

        db.Companies.Add(new Company { TenantId = TenantA, LegalNameEn = "Test for Claude", GosiEmployerId = "" });
        await db.SaveChangesAsync();

        var dash = await new SaudiComplianceDashboardService(db, TestReconciliation.For(db)).BuildAsync(TenantA, CancellationToken.None);

        Assert.Equal(0, dash.Gosi.EmployeesMissingGosiEmployerId);
        Assert.False(dash.Gosi.GosiEmployerIdConfigured);
        Assert.Contains(dash.Gosi.Warnings, w => w.Contains("GOSI employer ID is not set"));
    }

    [Fact]
    public async Task GosiEmployerId_ReadsSet_WhenTheCompanyActuallyHasOne()
    {
        using var db = MakeDb();
        await GosiRuleSeeder.SeedDefaultsAsync(db, NullLogger.Instance);

        db.Companies.Add(new Company
        {
            TenantId       = TenantA,
            LegalNameEn    = "Test for Claude",
            GosiEmployerId = "10000000001",
        });
        await db.SaveChangesAsync();

        var dash = await new SaudiComplianceDashboardService(db, TestReconciliation.For(db)).BuildAsync(TenantA, CancellationToken.None);

        Assert.True(dash.Gosi.GosiEmployerIdConfigured);
    }

    // ── Score breakdown ───────────────────────────────────────────────────────
    // AGENTS.md: every KPI must expose its definition/evidence. "Compliance Score 70 / 100"
    // with no working was unauditable; these lock the working to the number.

    [Fact]
    public async Task ScoreBreakdown_AccountsForEveryPointOfTheHeadlineScore()
    {
        using var db = MakeDb();
        await GosiRuleSeeder.SeedDefaultsAsync(db, NullLogger.Instance);

        db.Employees.Add(MakeEmployee(TenantA, 1, nationality: "Saudi", gosiRef: ""));
        db.EmployeePayrollProfiles.Add(new EmployeePayrollProfile { TenantId = TenantA, EmployeeId = 1, Iban = "" });
        db.PayrollRuns.Add(new PayrollRun { TenantId = TenantA, Year = 2026, Month = 5, Status = "Draft" });
        await db.SaveChangesAsync();

        var dash = await new SaudiComplianceDashboardService(db, TestReconciliation.For(db)).BuildAsync(TenantA, CancellationToken.None);
        var breakdown = dash.Overall.ScoreBreakdown;

        Assert.Equal(3, breakdown.Count);
        Assert.Equal(100.0, breakdown.Sum(c => c.WeightPercent));
        // Points sum to the headline score (each point is rounded to 1dp, so allow 1 point of slack).
        Assert.InRange(breakdown.Sum(c => c.PointsContributed), dash.Overall.ComplianceScore - 1.0, dash.Overall.ComplianceScore + 1.0);

        foreach (var c in breakdown)
        {
            Assert.False(string.IsNullOrWhiteSpace(c.Module), "Every score component must name its module");
            Assert.False(string.IsNullOrWhiteSpace(c.Basis),  $"{c.Module} must explain how it scored");
            Assert.InRange(c.Score, 0.0, 100.0);
        }
    }

    [Fact]
    public async Task ScoreBreakdown_SaysNothingIsMeasurable_OnAnUnconfiguredTenant()
    {
        using var db = MakeDb();
        await GosiRuleSeeder.SeedDefaultsAsync(db, NullLogger.Instance);

        var dash = await new SaudiComplianceDashboardService(db, TestReconciliation.For(db)).BuildAsync(TenantA, CancellationToken.None);

        var qiwa = dash.Overall.ScoreBreakdown.Single(c => c.Module == "QIWA");
        var gosi = dash.Overall.ScoreBreakdown.Single(c => c.Module == "GOSI");

        Assert.False(qiwa.Measurable);
        Assert.False(gosi.Measurable);
        Assert.Contains("cannot be measured", qiwa.Basis);
        Assert.Contains("cannot be measured", gosi.Basis);
    }

    [Fact]
    public async Task ComplianceScore_IsUnchangedByTheDisclosure_ForAnEmptyTenant()
    {
        // The score's arithmetic was deliberately NOT redefined while making it explainable.
        // An empty tenant scored 70 before (QIWA 0 × 0.30 + WPS 100 × 0.35 + GOSI 100 × 0.35)
        // and must still score 70 — if this ever changes it must be a deliberate product decision,
        // not a side effect of a copy fix.
        using var db = MakeDb();
        await GosiRuleSeeder.SeedDefaultsAsync(db, NullLogger.Instance);

        var dash = await new SaudiComplianceDashboardService(db, TestReconciliation.For(db)).BuildAsync(TenantA, CancellationToken.None);

        Assert.Equal(70, dash.Overall.ComplianceScore);
    }

    // ── Customer-facing copy ──────────────────────────────────────────────────

    [Fact]
    public async Task DashboardCopy_UsesRealPlurals_AndLeaksNoInternalFieldNames()
    {
        using var db = MakeDb();
        await GosiRuleSeeder.SeedDefaultsAsync(db, NullLogger.Instance);

        // One of each, so every "1 x" string is exercised in the singular.
        db.Employees.Add(MakeEmployee(TenantA, 1, nationality: "Saudi", gosiRef: ""));
        db.EmployeePayrollProfiles.Add(new EmployeePayrollProfile { TenantId = TenantA, EmployeeId = 1, Iban = "" });
        db.PayrollRuns.Add(new PayrollRun { TenantId = TenantA, Year = 2026, Month = 5, Status = "Draft" });
        await db.SaveChangesAsync();

        var dash = await new SaudiComplianceDashboardService(db, TestReconciliation.For(db)).BuildAsync(TenantA, CancellationToken.None);

        var copy = string.Join("\n", dash.Gosi.Warnings
            .Concat(dash.Wps.BlockingIssues)
            .Concat(dash.ActionItems.Select(a => $"{a.Title}\n{a.Description}\n{a.RecommendedAction}"))
            .Concat(dash.Overall.ScoreBreakdown.Select(c => c.Basis)));

        Assert.DoesNotContain("(s)", copy);
        foreach (var leak in new[] { "CountryCode", "TenantId", "EmployeeId", "CompanyId", "GosiEmployerId", "LegalNameEn" })
            Assert.DoesNotContain(leak, copy);

        // Singular agreement, not "1 employee(s)" and not "1 employees".
        Assert.Contains("1 employee is blocked from GOSI calculation", copy);
        Assert.DoesNotContain("1 employees", copy);
    }

    [Fact]
    public async Task DashboardCopy_UsesPluralForms_WhenCountIsGreaterThanOne()
    {
        using var db = MakeDb();
        await GosiRuleSeeder.SeedDefaultsAsync(db, NullLogger.Instance);

        for (var i = 1; i <= 2; i++)
        {
            db.Employees.Add(MakeEmployee(TenantA, i, nationality: "Saudi", gosiRef: ""));
            db.EmployeeSalaryStructures.Add(MakeSalary(TenantA, i, 5_000m));
        }
        await db.SaveChangesAsync();

        var dash = await new SaudiComplianceDashboardService(db, TestReconciliation.For(db)).BuildAsync(TenantA, CancellationToken.None);
        var copy = string.Join("\n", dash.Gosi.Warnings.Concat(dash.ActionItems.Select(a => a.Title)));

        Assert.Contains("2 employees are blocked from GOSI calculation", copy);
        Assert.Contains("2 employees are missing a GOSI reference number.", copy);
        Assert.DoesNotContain("2 employee is", copy);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static Employee MakeEmployee(Guid tenantId, int id, string nationality, string gosiRef) =>
        new()
        {
            Id            = id,
            TenantId      = tenantId,
            EmployeeCode  = $"EMP{id:D4}",
            FullName      = $"Test Employee {id}",
            Nationality   = nationality,
            Status        = "Active",
            IsDeleted     = false,
            GosiReference = gosiRef,
        };

    private static EmployeeSalaryStructure MakeSalary(Guid tenantId, int empId, decimal basic) =>
        new()
        {
            TenantId      = tenantId,
            EmployeeId    = empId,
            BasicSalary   = basic,
            EffectiveDate = new DateOnly(2024, 1, 1),
        };
}
