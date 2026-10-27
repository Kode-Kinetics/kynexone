using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Auth;
using Zayra.Api.Application.Employees;
using Zayra.Api.Application.Employees.Onboarding;
using Zayra.Api.Data;
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
}
