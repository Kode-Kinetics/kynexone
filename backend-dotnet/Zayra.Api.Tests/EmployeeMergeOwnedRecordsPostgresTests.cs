using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Auth;
using Zayra.Api.Controllers;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.Audit;
using Zayra.Api.Infrastructure.Auth;
using Zayra.Api.Infrastructure.Documents.Letters;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// <c>POST /api/employees/{id}/resolve-duplicate</c> with <c>merge</c> on a REAL PostgreSQL: what happens to the money and compliance
/// records the duplicate owns. Today the merge MOVES NOTHING (it links <c>DuplicateOfEmployeeId</c>, soft-deletes the duplicate, cancels
/// its approvals and deactivates its payroll profile and salary structures), so every record below stays keyed to the soft-deleted row.
///
/// Two groups:
///  1. LIVE: a duplicate that owns nothing merges; and the characterisation of today's behaviour (each record type is left on the
///     duplicate, the survivor gets none, the merge succeeds).
///  2. SKIPPED, owner decision pending: the merge must refuse with HTTP 409 naming the blocking record types, and delete nothing.
///     They are written red-first against the planned behaviour and stay skipped until the owner decides whether to refuse.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration")]
public sealed class EmployeeMergeOwnedRecordsPostgresTests
{
    private const string PendingDecision = "owner decision pending: merge refusal";
    private readonly PostgresFixture _fx;
    public EmployeeMergeOwnedRecordsPostgresTests(PostgresFixture fx) => _fx = fx;

    public sealed record Twins(Guid TenantId, Guid CompanyId, Employee Duplicate, Employee Survivor);

    private async Task<Twins> SeedTwinsAsync()
    {
        await using var db = _fx.CreateDb();
        var tenantId = await PostgresFixture.SeedMinimalTenant(db);
        var company = new Company { Id = Guid.NewGuid(), TenantId = tenantId, LegalNameEn = "Merge PG", CountryCode = "SAU", Jurisdiction = "KSA-mainland", RegistrationNumber = $"M-{Guid.NewGuid():N}", DefaultCurrency = "SAR", IsActive = true, CreatedAtUtc = DateTime.UtcNow };
        db.Companies.Add(company);
        await db.SaveChangesAsync();
        Employee Row(string code) => new()
        {
            TenantId = tenantId, CompanyId = company.Id, EmployeeCode = code, FullName = $"Merge {code}", EnglishName = $"Merge {code}",
            Status = "Active", ReadinessState = "Ready", JoiningDate = DateTime.SpecifyKind(new DateTime(2025, 1, 1), DateTimeKind.Utc),
        };
        var dup = Row("DUP-1");
        var survivor = Row("KEEP-1");
        db.Employees.AddRange(dup, survivor);
        await db.SaveChangesAsync();
        return new Twins(tenantId, company.Id, dup, survivor);
    }

    private EmployeesController Controller(ZayraDbContext db, Twins t)
    {
        var controller = new EmployeesController(db, new Pbkdf2PasswordHasher(), new AuditService(db), new NullDocumentStorage(), TestNotifications.For(db),
            new MergePgHijri(), new Zayra.Api.Infrastructure.Common.DataScopeService(db), new MergePgLetters());
        var claims = new List<Claim>
        {
            new("tenant_id", t.TenantId.ToString()), new(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()), new(ClaimTypes.Role, "Admin"),
            new("permission", "employees.read"), new("permission", "employees.write"), new("permission", "employees.delete"),
            new("is_group_scope", "true"),
        };
        controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test")) } };
        return controller;
    }

    private async Task<IActionResult> MergeAsync(Twins t)
    {
        await using var db = _fx.CreateDb();
        return await Controller(db, t).ResolveDuplicate(t.Duplicate.Id, new ResolveDuplicateRequest("merge", t.Survivor.Id, "same person"), CancellationToken.None);
    }

    private async Task<bool> IsDeletedAsync(int id)
    {
        await using var db = _fx.CreateDb();
        return await db.Employees.IgnoreQueryFilters().Where(e => e.Id == id).Select(e => e.IsDeleted).SingleAsync();
    }

    /// <summary>One record type the duplicate can own: how to seed it, and how many of that type each employee owns afterwards.</summary>
    public sealed record OwnedRecord(string Name, Func<ZayraDbContext, Twins, Task> Seed, Func<ZayraDbContext, int, Task<int>> CountOwnedBy)
    {
        public override string ToString() => Name;
    }

    private static readonly DateOnly Today = DateOnly.FromDateTime(DateTime.UtcNow);

    public static IEnumerable<object[]> Blocking() => Catalogue().Select(r => new object[] { r });

    private static IEnumerable<OwnedRecord> Catalogue()
    {
        yield return new("open loan", (db, t) =>
        {
            db.EmployeeLoans.Add(new EmployeeLoan
            {
                TenantId = t.TenantId, CompanyId = t.CompanyId, EmployeeId = t.Duplicate.PublicId, EmployeeIntId = t.Duplicate.Id, EmployeeName = t.Duplicate.FullName,
                LoanTypeId = Guid.NewGuid(), LoanTypeName = "Personal", LoanNumber = "LN-MERGE-1", ApprovedAmount = 3000m, ApprovedInstallments = 6,
                InstallmentAmount = 500m, OutstandingBalance = 1500m, Status = "Active", RepaymentMethod = "PayrollDeduction", Currency = "SAR",
            });
            return Task.CompletedTask;
        }, (db, id) => db.EmployeeLoans.IgnoreQueryFilters().CountAsync(x => x.EmployeeIntId == id));

        yield return new("open salary advance", (db, t) =>
        {
            db.SalaryAdvances.Add(new SalaryAdvance
            {
                TenantId = t.TenantId, CompanyId = t.CompanyId, EmployeeId = t.Duplicate.PublicId, EmployeeIntId = t.Duplicate.Id, EmployeeName = t.Duplicate.FullName,
                AdvanceNumber = "ADV-MERGE-1", ApprovedAmount = 1000m, OutstandingBalance = 1000m, Status = "Active",
            });
            return Task.CompletedTask;
        }, (db, id) => db.SalaryAdvances.IgnoreQueryFilters().CountAsync(x => x.EmployeeIntId == id));

        yield return new("approved bonus not yet paid", (db, t) =>
        {
            db.EmployeeBonuses.Add(new EmployeeBonus
            {
                TenantId = t.TenantId, CompanyId = t.CompanyId, EmployeeId = t.Duplicate.PublicId, EmployeeIntId = t.Duplicate.Id, EmployeeName = t.Duplicate.FullName,
                BonusBatchId = Guid.NewGuid(), BonusTypeId = Guid.NewGuid(), BonusTypeName = "Annual", BonusAmount = 2000m, GrossBonusAmount = 2000m, Status = "Approved",
            });
            return Task.CompletedTask;
        }, (db, id) => db.EmployeeBonuses.IgnoreQueryFilters().CountAsync(x => x.EmployeeIntId == id));

        yield return new("leave balance with days left", (db, t) =>
        {
            db.EmployeeLeaveBalances.Add(new EmployeeLeaveBalance
            {
                TenantId = t.TenantId, EmployeeId = t.Duplicate.Id, EmployeeName = t.Duplicate.FullName, LeaveTypeId = Guid.NewGuid(), LeaveTypeName = "Annual",
                Year = DateTime.UtcNow.Year, Entitled = 21m, Accrued = 21m,
            });
            return Task.CompletedTask;
        }, (db, id) => db.EmployeeLeaveBalances.IgnoreQueryFilters().CountAsync(x => x.EmployeeId == id));

        yield return new("pending leave request", (db, t) =>
        {
            db.LeaveRequests.Add(new LeaveRequest
            {
                TenantId = t.TenantId, CompanyId = t.CompanyId, EmployeeId = t.Duplicate.Id, EmployeeName = t.Duplicate.FullName, LeaveTypeId = Guid.NewGuid(),
                LeaveTypeName = "Annual", StartDate = Today.AddDays(10), EndDate = Today.AddDays(12), TotalDays = 3m, Status = "Pending",
            });
            return Task.CompletedTask;
        }, (db, id) => db.LeaveRequests.IgnoreQueryFilters().CountAsync(x => x.EmployeeId == id));

        yield return new("active benefit enrolment", (db, t) =>
        {
            db.BenefitEnrollments.Add(new BenefitEnrollment
            {
                TenantId = t.TenantId, CompanyId = t.CompanyId, BenefitPlanId = Guid.NewGuid(), EmployeeId = t.Duplicate.Id, EmployeeName = t.Duplicate.FullName, Status = "Active",
            });
            return Task.CompletedTask;
        }, (db, id) => db.BenefitEnrollments.IgnoreQueryFilters().CountAsync(x => x.EmployeeId == id));

        yield return new("payroll slip in an open run", (db, t) =>
        {
            db.PayrollSlips.Add(new PayrollSlip
            {
                TenantId = t.TenantId, CompanyId = t.CompanyId, RunId = Guid.NewGuid(), EmployeeId = t.Duplicate.Id, EmployeeCode = t.Duplicate.EmployeeCode,
                EmployeeName = t.Duplicate.FullName, BasicSalary = 8000m, GrossSalary = 8000m, NetSalary = 8000m, Status = "Draft",
            });
            return Task.CompletedTask;
        }, (db, id) => db.PayrollSlips.IgnoreQueryFilters().CountAsync(x => x.EmployeeId == id));

        yield return new("social-insurance (GOSI) registration reference", (db, t) =>
        {
            db.EmployeePayrollProfiles.Add(new EmployeePayrollProfile
            {
                TenantId = t.TenantId, EmployeeId = t.Duplicate.Id, SocialInsuranceReference = "GOSI-123456", SalaryCurrency = "SAR", WpsEligible = false,
            });
            return Task.CompletedTask;
        }, (db, id) => db.EmployeePayrollProfiles.IgnoreQueryFilters().CountAsync(x => x.EmployeeId == id && x.SocialInsuranceReference != ""));

        yield return new("end-of-service calculation", (db, t) =>
        {
            db.EOSBCalculations.Add(new EOSBCalculation
            {
                TenantId = t.TenantId, EmployeeId = t.Duplicate.Id, CalculationDate = Today, EligibleSalary = 8000m, CalculatedAmount = 12000m, Status = "Approved",
            });
            return Task.CompletedTask;
        }, (db, id) => db.EOSBCalculations.IgnoreQueryFilters().CountAsync(x => x.EmployeeId == id));

        yield return new("employee document", (db, t) =>
        {
            db.EmployeeDocuments.Add(new EmployeeDocument
            {
                TenantId = t.TenantId, CompanyId = t.CompanyId, EmployeeId = t.Duplicate.Id, DocumentType = "Contract", FileName = "contract.pdf", ContentType = "application/pdf",
            });
            return Task.CompletedTask;
        }, (db, id) => db.EmployeeDocuments.IgnoreQueryFilters().CountAsync(x => x.EmployeeId == id));

        yield return new("identity / compliance record", (db, t) =>
        {
            db.EmployeeComplianceRecords.Add(new EmployeeComplianceRecord
            {
                TenantId = t.TenantId, EmployeeId = t.Duplicate.Id, CountryCode = "SA", FieldKey = "iqama_number", FieldLabel = "Iqama", FieldValue = "2000000006",
            });
            return Task.CompletedTask;
        }, (db, id) => db.EmployeeComplianceRecords.IgnoreQueryFilters().CountAsync(x => x.EmployeeId == id));

        yield return new("self-service login link", (db, t) =>
        {
            db.EmployeeUserAccounts.Add(new EmployeeUserAccount { TenantId = t.TenantId, EmployeeId = t.Duplicate.Id, Status = "Active", RequiresPasswordSetup = false });
            return Task.CompletedTask;
        }, (db, id) => db.EmployeeUserAccounts.IgnoreQueryFilters().CountAsync(x => x.EmployeeId == id));
    }

    // ── 1. LIVE ─────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Merge_ADuplicateThatOwnsNoMoneyOrComplianceRecords_StillMerges()
    {
        var t = await SeedTwinsAsync();
        var result = await MergeAsync(t);
        Assert.IsType<OkObjectResult>(result);
        (await IsDeletedAsync(t.Duplicate.Id)).Should().BeTrue();
        (await IsDeletedAsync(t.Survivor.Id)).Should().BeFalse();
    }

    [Theory]
    [MemberData(nameof(Blocking))]
    public async Task Merge_TodayMovesNothing_TheRecordStaysOnTheSoftDeletedDuplicate_AndTheSurvivorGetsNone(OwnedRecord record)
    {
        var t = await SeedTwinsAsync();
        await using (var seed = _fx.CreateDb())
        {
            await record.Seed(seed, t);
            await seed.SaveChangesAsync();
        }

        var result = await MergeAsync(t);

        Assert.IsType<OkObjectResult>(result);
        await using var read = _fx.CreateDb();
        (await record.CountOwnedBy(read, t.Duplicate.Id)).Should().Be(1, $"'{record.Name}' is not moved by the merge: it stays keyed to the soft-deleted duplicate");
        (await record.CountOwnedBy(read, t.Survivor.Id)).Should().Be(0, $"nothing is copied to the kept employee for '{record.Name}'");
    }

    // ── 2. SKIPPED: the planned refusal ─────────────────────────────────────────────────────────────

    [Theory(Skip = PendingDecision)]
    [MemberData(nameof(Blocking))]
    public async Task Merge_IsRefusedWith409NamingTheRecord_AndDeletesNothing_WhenTheDuplicateOwnsOne(OwnedRecord record)
    {
        var t = await SeedTwinsAsync();
        await using (var seed = _fx.CreateDb())
        {
            await record.Seed(seed, t);
            await seed.SaveChangesAsync();
        }

        var result = await MergeAsync(t);

        var conflict = Assert.IsAssignableFrom<ObjectResult>(result);
        conflict.StatusCode.Should().Be(StatusCodes.Status409Conflict);
        System.Text.Json.JsonSerializer.Serialize(conflict.Value).Should().Contain("merge_blocked_by_records", "the body names the reason code")
            .And.Contain(record.Name, "and the specific record type that blocks it");
        (await IsDeletedAsync(t.Duplicate.Id)).Should().BeFalse("a refused merge deletes nothing");
        await using var read = _fx.CreateDb();
        (await read.Employees.IgnoreQueryFilters().Where(e => e.Id == t.Duplicate.Id).Select(e => e.DuplicateOfEmployeeId).SingleAsync()).Should().BeNull();
        (await record.CountOwnedBy(read, t.Duplicate.Id)).Should().Be(1);
    }
}

file sealed class MergePgHijri : Zayra.Api.Infrastructure.Localization.IHijriDateService
{
    public Zayra.Api.Infrastructure.Localization.DateConversionDto FromGregorian(DateOnly date) =>
        new(date.ToString("yyyy-MM-dd"), "1447-01-01", 1447, 1, 1);
}

file sealed class MergePgLetters : ILetterService
{
    public Task<byte[]> GeneratePayslipPdfAsync(PayslipData data, CancellationToken cancellationToken = default) => Task.FromResult(Array.Empty<byte>());
    public Task<byte[]> GenerateAppointmentLetterAsync(LetterData data, CancellationToken cancellationToken = default) => Task.FromResult(Array.Empty<byte>());
    public Task<byte[]> GenerateExperienceLetterAsync(LetterData data, CancellationToken cancellationToken = default) => Task.FromResult(Array.Empty<byte>());
    public Task<byte[]> GenerateOfferLetterAsync(OfferLetterData data, CancellationToken cancellationToken = default) => Task.FromResult(Array.Empty<byte>());
}
