using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Zayra.Api.Controllers;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.Entitlements;
using Zayra.Api.Infrastructure.Finance;
using Zayra.Api.Models;

namespace Zayra.Api.Tests.ReleaseA;

/// <summary>
/// L4 on real PostgreSQL: staging a salary proposal that NAMES a cash allowance takes the same tenant lock as removing an allowance
/// (<see cref="EntitlementMatrixService.LockScope"/>), so a remove cannot read "nobody names it" while a stage that names it is in flight.
/// A proposal that names none (the common case) takes no lock and is never held up. Needs a database; not run in the sandbox.
/// </summary>
[Collection("Integration")]
[Trait("Category", "Integration")]
public sealed class CashAllowanceStagingLockPostgresTests
{
    private readonly PostgresFixture _fx;
    public CashAllowanceStagingLockPostgresTests(PostgresFixture fx) => _fx = fx;

    [Fact]
    public async Task StagingALineWaitsForTheMatrixLockThatRemoveTakes_AndAProposalWithoutLinesDoesNot()
    {
        Guid tid; int a, b; Guid structureId;
        await using (var seed = _fx.CreateDb())
        {
            tid = await PostgresFixture.SeedMinimalTenant(seed);
            var structure = new SalaryStructure { TenantId = tid, Code = "STD", Name = "Standard", Currency = "SAR", EffectiveDate = new DateOnly(2025, 1, 1), IsActive = true };
            var ea = new Employee { TenantId = tid, EmployeeCode = "LK-A", FullName = "Lock A", Status = "Active", JoiningDate = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc) };
            var eb = new Employee { TenantId = tid, EmployeeCode = "LK-B", FullName = "Lock B", Status = "Active", JoiningDate = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc) };
            seed.AddRange(structure, ea, eb);
            seed.GlDrivers.Add(new GlDriver
            {
                TenantId = tid, CompanyId = null, Key = "EARN:CAR", Label = "Car", Category = GlDriverCategories.Earning, PostingSide = "DR",
                DefaultCode = "5210", DefaultName = "Car Expense", MatchMode = "Exact", MatchComponentCode = "ALW_CAR", SortOrder = 60,
            });
            seed.PayComponents.Add(new PayComponent
            {
                TenantId = tid, CompanyId = null, Code = "ALW_CAR", NameEn = "Car allowance", NameAr = "بدل سيارة", ComponentType = PayComponentTypes.Earning,
                CalcMethod = PayComponentCalcMethods.GradePackage, FinanceConfirmedAtUtc = DateTime.UtcNow, EntitlementClass = PayEntitlementClasses.QiwaWage, StatutoryFloor = PayStatutoryFloors.None,
                GlDriverKey = "EARN:CAR", GosiSubject = false, WpsIncluded = true, IsTaxable = false, EosbIncluded = true, DisplayOrder = 9100,
            });
            await seed.SaveChangesAsync();
            SalaryApprovalKit.EnsureApprover(seed, tid);
            a = ea.Id; b = eb.Id; structureId = structure.Id;
        }

        EmployeeSalaryStructureRequest Package(int employee, IReadOnlyList<SalaryAllowanceLine>? lines) =>
            new(employee, structureId, 10_000m, 0m, 0m, 0m, 0m, 0m, 0m, new DateOnly(2026, 3, 1), "SAR", lines);
        Task<IActionResult> Stage(EmployeeSalaryStructureRequest request)
        {
            var db = _fx.CreateDb();
            return SalaryApprovalKit.FinanceController(db, tid, Guid.NewGuid(), "payroll.write").AssignEmployeeSalary(request, CancellationToken.None)
                .ContinueWith(t => { db.Dispose(); return t.GetAwaiter().GetResult(); });
        }

        Task<IActionResult> withLines = null!;
        await using (var holder = _fx.CreateDb())
        {
            // Hold the tenant lock exactly as RemoveBenefitAsync does, for as long as the body runs.
            await FinanceDecisionSerializer.SerializeAsync(holder, EntitlementMatrixService.LockScope, tid, tid, async () =>
            {
                withLines = Stage(Package(a, [new SalaryAllowanceLine("ALW_CAR", AllowanceBases.Amount, null, 500m)]));
                var withoutLines = await Stage(Package(b, null));          // not held up: it names no allowance
                withoutLines.Should().BeOfType<AcceptedResult>();
                await Task.Delay(1_500);
                withLines.IsCompleted.Should().BeFalse("staging a line that names an allowance waits for the lock a remove holds");
                return 0;
            });
        }

        (await withLines.WaitAsync(TimeSpan.FromSeconds(30))).Should().BeOfType<AcceptedResult>("it goes on once the lock is released");
    }
}
