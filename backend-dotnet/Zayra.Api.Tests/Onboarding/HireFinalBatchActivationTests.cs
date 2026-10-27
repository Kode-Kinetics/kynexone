using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Auth;
using Zayra.Api.Application.Employees;
using Zayra.Api.Application.Employees.Onboarding;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Employees;
using Zayra.Api.Infrastructure.Employees.Onboarding;
using Zayra.Api.Models;
using Xunit;

namespace Zayra.Api.Tests.Onboarding;

/// <summary>Final batch CD44: a stored ID that fails its check digit is refused at activation unless an authorised
/// caller waives it with a reason; the waiver is audited with codes and the reason only.</summary>
public class HireFinalBatchActivationTests
{
    private const string BadIqama = "2000000009";

    private static ZayraDbContext CreateDb() =>
        new(new DbContextOptionsBuilder<ZayraDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static RequestContext Ctx(Guid tenant, params string[] perms) =>
        new("127.0.0.1", "test", Guid.NewGuid(), tenant, ["HR Manager"], perms);

    private static async Task<(Guid T, Employee E)> SeedDraftWithBadIdAsync(ZayraDbContext db)
    {
        var tenant = Guid.NewGuid();
        var c = new Company { TenantId = tenant, LegalNameEn = "Main", TradeName = "Main", CountryCode = "SA", Jurisdiction = "t",
            RegistrationNumber = "RC-" + Guid.NewGuid().ToString("N"), DefaultCurrency = "SAR", IsActive = true, CreatedAtUtc = DateTime.UtcNow };
        db.Companies.Add(c); await db.SaveChangesAsync();
        var e = new Employee { TenantId = tenant, CompanyId = c.Id, EmployeeCode = "E-" + Guid.NewGuid().ToString("N")[..8], FullName = "Draft Person",
            EnglishName = "Draft Person", Status = "Draft", CountryCode = "SA", Nationality = "Indian", JoiningDate = DateTime.UtcNow.Date };
        db.Employees.Add(e); await db.SaveChangesAsync();
        db.EmployeeComplianceRecords.Add(new EmployeeComplianceRecord { TenantId = tenant, EmployeeId = e.Id, CountryCode = "SA",
            FieldKey = "iqama_number", FieldLabel = "Iqama", FieldValue = BadIqama, IsSensitive = true });
        await db.SaveChangesAsync();
        return (tenant, e);
    }

    private static AcknowledgedWarning[] Ack(string? reason) => [new AcknowledgedWarning(EmployeeWarningCodes.IdChecksumInvalid, reason)];

    [Fact]
    public async Task Activation_StoredChecksumFailure_NoAcknowledgement_IsRefused()
    {
        await using var db = CreateDb(); var (t, e) = await SeedDraftWithBadIdAsync(db);
        var ex = await Assert.ThrowsAsync<EmployeeValidationException>(() =>
            new EmployeeInputGuard(db).EnforceActivationAsync(t, e.Id, Ctx(t, "employees.override_validation"), default));
        ex.Errors.Values.SelectMany(v => v).Should().Contain(EmployeeWarningCodes.IdChecksumInvalid);
    }

    [Fact]
    public async Task Activation_StoredChecksumFailure_HrOfficerWithoutPermission_IsRefusedEvenWithAReason()
    {
        await using var db = CreateDb(); var (t, e) = await SeedDraftWithBadIdAsync(db);
        var ex = await Assert.ThrowsAsync<EmployeeValidationException>(() =>
            new EmployeeInputGuard(db).EnforceActivationAsync(t, e.Id, Ctx(t, "employees.write"), default, Ack("Confirmed against the original card")));
        ex.Code.Should().Be(EmployeeProblemCodes.OverrideNotPermitted);
    }

    [Fact]
    public async Task Activation_StoredChecksumFailure_HrManagerWithoutAReason_IsRefused()
    {
        await using var db = CreateDb(); var (t, e) = await SeedDraftWithBadIdAsync(db);
        var ex = await Assert.ThrowsAsync<EmployeeValidationException>(() =>
            new EmployeeInputGuard(db).EnforceActivationAsync(t, e.Id, Ctx(t, "employees.override_validation"), default, Ack("short")));
        ex.Code.Should().Be(EmployeeProblemCodes.OverrideReasonRequired);
    }

    [Fact]
    public async Task Activation_StoredChecksumFailure_HrManagerWithAReason_IsAllowed_AndAuditedWithCodesAndReasonOnly()
    {
        await using var db = CreateDb(); var (t, e) = await SeedDraftWithBadIdAsync(db);
        const string reason = "Confirmed against the original card";
        await new EmployeeInputGuard(db).EnforceActivationAsync(t, e.Id, Ctx(t, "employees.override_validation"), default, Ack(reason));
        await db.SaveChangesAsync();
        var rows = await db.AuditLogs.Where(a => a.Action == "employee.warning_overridden" && a.EntityId == e.Id.ToString()).ToListAsync();
        rows.Should().ContainSingle();
        rows[0].Metadata.Should().Contain(EmployeeWarningCodes.IdChecksumInvalid).And.Contain(reason).And.NotContain(BadIqama);
        // The recorded waiver is not asked for again.
        await new EmployeeInputGuard(db).EnforceActivationAsync(t, e.Id, Ctx(t), default);
    }

    [Fact]
    public async Task Readiness_StoredChecksumFailure_IsABlockerUntilWaived()
    {
        await using var db = CreateDb(); var (t, e) = await SeedDraftWithBadIdAsync(db);
        var errors = await EmployeeStoredDataCheck.ErrorsAsync(db, t, e.Id, null, default);
        errors.Values.SelectMany(v => v).Should().Contain(EmployeeWarningCodes.IdChecksumInvalid);
    }

    // ── CD45: pay that arrived through a writer with no approval hook still needs a second person ──
    private static async Task<(Guid T, Employee E)> SeedDraftWithPayAsync(ZayraDbContext db, bool basic, bool iban)
    {
        var (t, e) = await SeedDraftWithBadIdAsync(db);
        (await db.EmployeeComplianceRecords.SingleAsync()).FieldValue = "2000000006";   // a valid Iqama, so only the pay gate can block
        (await db.Employees.SingleAsync(x => x.Id == e.Id)).IqamaNumber = "2000000006";
        if (basic)
            db.EmployeeSalaryStructures.Add(new EmployeeSalaryStructure { TenantId = t, EmployeeId = e.Id, SalaryStructureId = Guid.NewGuid(),
                BasicSalary = 9000m, EffectiveDate = new DateOnly(2026, 10, 26), Currency = "SAR", IsActive = true });
        if (iban)
            db.EmployeePayrollProfiles.Add(new EmployeePayrollProfile { TenantId = t, EmployeeId = e.Id, Iban = "SA0380000000608010167519" });
        await db.SaveChangesAsync();
        return (t, e);
    }

    private static async Task<EmployeeReadiness> ActivateAsync(ZayraDbContext db, Guid t, Employee e)
    {
        var guard = new EmployeeActivationGuard(db);
        var snap = await guard.BuildSnapshotAsync(t, e.Id, default);
        return await guard.EnsureActivatableAsync(t, e.CompanyId, snap!, new RequestContext(null, null, TenantId: t), default);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task Activation_PayWrittenByAnUnhookedWriter_WithNoApproval_IsRefused(bool basic, bool iban)
    {
        await using var db = CreateDb(); var (t, e) = await SeedDraftWithPayAsync(db, basic, iban);
        var ex = await Assert.ThrowsAsync<EmployeeActivationBlockedException>(() => ActivateAsync(db, t, e));
        ex.Readiness.Blocking.Should().Contain(b => b.Key == CompensationApprovalGate.GapType);
    }

    [Fact]
    public async Task Activation_PayWithAnApprovalBoundToTheLiveFingerprint_IsAllowed()
    {
        await using var db = CreateDb(); var (t, e) = await SeedDraftWithPayAsync(db, basic: true, iban: true);
        var fp = await CompensationApprovalGate.LiveFingerprintAsync(db, t, e.Id, default);
        db.EmployeeImportGaps.Add(new EmployeeImportGap { TenantId = t, EmployeeId = e.Id, ImportBatchId = Guid.Empty, GapType = CompensationApprovalGate.GapType,
            GapCategory = "pay", Detail = "x", RawValue = CompensationApprovalGate.ApprovedMarker + fp, ResolvedAtUtc = DateTime.UtcNow });
        await db.SaveChangesAsync();
        (await ActivateAsync(db, t, e)).IsBlocked.Should().BeFalse();
    }

    [Fact]
    public async Task Activation_PayChangedAfterTheApproval_IsRefused()
    {
        await using var db = CreateDb(); var (t, e) = await SeedDraftWithPayAsync(db, basic: true, iban: false);
        var fp = await CompensationApprovalGate.LiveFingerprintAsync(db, t, e.Id, default);
        db.EmployeeImportGaps.Add(new EmployeeImportGap { TenantId = t, EmployeeId = e.Id, ImportBatchId = Guid.Empty, GapType = CompensationApprovalGate.GapType,
            GapCategory = "pay", Detail = "x", RawValue = CompensationApprovalGate.ApprovedMarker + fp, ResolvedAtUtc = DateTime.UtcNow });
        (await db.EmployeeSalaryStructures.SingleAsync()).BasicSalary = 12000m;
        await db.SaveChangesAsync();
        await Assert.ThrowsAsync<EmployeeActivationBlockedException>(() => ActivateAsync(db, t, e));
    }

    [Fact]
    public async Task Activation_NoPayAtAll_IsNotBlockedByThePayGate()
    {
        await using var db = CreateDb(); var (t, e) = await SeedDraftWithPayAsync(db, basic: false, iban: false);
        (await ActivateAsync(db, t, e)).IsBlocked.Should().BeFalse();
    }
}
