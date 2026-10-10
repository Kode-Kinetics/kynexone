using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.PostgreSql;
using Zayra.Api.Data;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// 20261010201817_RouteAbsenceDeductionsToContraExpense against a real migrated database: untouched
/// seeded rows move to the contra-expense accounts, anything a tenant customised is left alone, posted
/// history is never rewritten, and Down restores the seeded liability routing.
/// </summary>
[Trait("Category", "Integration")]
public sealed class AbsenceContraExpenseMigrationPostgresTests
{
    private const string MigrationId = "20261010201817_RouteAbsenceDeductionsToContraExpense";

    [Fact]
    public async Task Up_MovesOnlyUntouchedSeededRows_NeverTouchesPostedHistory_AndDownRestores()
    {
        await using var container = new PostgreSqlBuilder().WithImage("postgres:16-alpine").Build();
        await container.StartAsync();
        await using var db = new ZayraDbContext(new DbContextOptionsBuilder<ZayraDbContext>()
            .UseNpgsql(container.GetConnectionString()).Options);
        var migrator = db.Database.GetInfrastructure().GetRequiredService<IMigrator>();
        var migrations = db.Database.GetMigrations().ToList();
        migrations.Should().Contain(MigrationId);
        var previous = migrations[migrations.IndexOf(MigrationId) - 1];
        await migrator.MigrateAsync(previous);

        // Tenant A: the pre-fix seed, untouched. Tenant B: customised — renamed 2105, re-coded the
        // DED:LEAVE driver, and a company-level DED:ATTENDANCE mapping onto the seeded 2104.
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var companyB = Guid.NewGuid();
        GlAccount Acct(Guid t, string code, string name, string type) => new() { TenantId = t, Code = code, Name = name, AccountType = type };
        var a2104 = Acct(a, "2104", "Attendance Adjustment Payable", "Liability");
        var a2105 = Acct(a, "2105", "Leave Deduction Payable", "Liability");
        var b2104 = Acct(b, "2104", "Attendance Adjustment Payable", "Liability");
        var b2105 = Acct(b, "2105", "Leave Deductions Owed To Fund", "Liability");
        db.GlAccounts.AddRange(a2104, a2105, b2104, b2105);
        db.GlAccountMappings.AddRange(
            new GlAccountMapping { TenantId = a, DriverKey = "DED:ATTENDANCE", AccountId = a2104.Id },
            new GlAccountMapping { TenantId = a, DriverKey = "DED:LEAVE", AccountId = a2105.Id },
            new GlAccountMapping { TenantId = b, DriverKey = "DED:ATTENDANCE", AccountId = b2104.Id },
            new GlAccountMapping { TenantId = b, CompanyId = companyB, DriverKey = "DED:ATTENDANCE", AccountId = b2104.Id },
            new GlAccountMapping { TenantId = b, DriverKey = "DED:LEAVE", AccountId = b2105.Id });
        GlDriver Sys(Guid t, string key, string code, string name) => new()
        {
            TenantId = t, Key = key, Label = key, Category = GlDriverCategories.Deduction, PostingSide = "CR",
            AccountType = "Liability", DefaultCode = code, DefaultName = name, MatchSource = key == "DED:LEAVE" ? "Leave" : "Attendance",
            MatchMode = "Any", IsSystem = true, SortOrder = 24,
        };
        db.GlDrivers.AddRange(
            Sys(a, "DED:ATTENDANCE", "2104", "Attendance Adjustment Payable"),
            Sys(a, "DED:LEAVE", "2105", "Leave Deduction Payable"),
            Sys(b, "DED:ATTENDANCE", "2104", "Attendance Adjustment Payable"),
            Sys(b, "DED:LEAVE", "2999", "Custom Leave"));
        var history = new FinanceGlEntry
        {
            TenantId = a, SourceModule = "Payroll", SourceEntityId = Guid.NewGuid(), EventType = GlEventTypes.Accrual,
            DebitAccount = string.Empty, CreditAccount = "2104 - Attendance Adjustment Payable", Amount = 120m,
            Currency = "SAR", Period = "2026-08", EntryDate = new DateOnly(2026, 8, 31), Description = "Payroll deduction: ATTENDANCE",
        };
        db.FinanceGlEntries.Add(history);
        await db.SaveChangesAsync();

        await migrator.MigrateAsync(MigrationId);
        db.ChangeTracker.Clear();

        async Task<string> MappedCode(Guid t, Guid? co, string key) =>
            await db.GlAccountMappings.IgnoreQueryFilters().Where(m => m.TenantId == t && m.CompanyId == co && m.DriverKey == key)
                .Join(db.GlAccounts.IgnoreQueryFilters(), m => m.AccountId, x => x.Id, (m, x) => x.Code).SingleAsync();
        async Task<GlDriver> Driver(Guid t, string key) =>
            await db.GlDrivers.IgnoreQueryFilters().SingleAsync(d => d.TenantId == t && d.CompanyId == null && d.Key == key);

        (await MappedCode(a, null, "DED:ATTENDANCE")).Should().Be("5008");
        (await MappedCode(a, null, "DED:LEAVE")).Should().Be("5009");
        (await Driver(a, "DED:ATTENDANCE")).Should().Match<GlDriver>(d => d.DefaultCode == "5008" && d.AccountType == "Expense");
        (await Driver(a, "DED:LEAVE")).DefaultCode.Should().Be("5009");
        (await Driver(a, "ROUNDING")).DefaultCode.Should().Be("5199");
        (await MappedCode(a, null, "ROUNDING")).Should().Be("5199");
        (await db.GlAccounts.IgnoreQueryFilters().SingleAsync(x => x.TenantId == a && x.Code == "5008")).AccountType.Should().Be("Expense");

        (await MappedCode(b, null, "DED:ATTENDANCE")).Should().Be("5008", "B's group mapping still pointed at the untouched seeded 2104");
        (await MappedCode(b, companyB, "DED:ATTENDANCE")).Should().Be("2104", "a company-level mapping is a deliberate choice");
        (await MappedCode(b, null, "DED:LEAVE")).Should().Be("2105", "B renamed its 2105, so it is not the seeded row");
        (await Driver(b, "DED:LEAVE")).DefaultCode.Should().Be("2999", "a re-coded driver is the tenant's decision");

        (await db.FinanceGlEntries.IgnoreQueryFilters().AsNoTracking().SingleAsync(x => x.Id == history.Id))
            .CreditAccount.Should().Be("2104 - Attendance Adjustment Payable", "posted history is never rewritten");
        (await db.GlAccounts.IgnoreQueryFilters().CountAsync(x => x.TenantId == a && (x.Code == "2104" || x.Code == "2105")))
            .Should().Be(2, "the old accounts stay in the chart; their balances are the accountant's to reclassify");

        await migrator.MigrateAsync(previous);
        db.ChangeTracker.Clear();
        (await MappedCode(a, null, "DED:ATTENDANCE")).Should().Be("2104");
        (await MappedCode(a, null, "DED:LEAVE")).Should().Be("2105");
        (await Driver(a, "DED:ATTENDANCE")).Should().Match<GlDriver>(d => d.DefaultCode == "2104" && d.AccountType == "Liability");
        (await Driver(b, "DED:LEAVE")).DefaultCode.Should().Be("2999");
        (await db.GlDrivers.IgnoreQueryFilters().AnyAsync(d => d.Key == "ROUNDING")).Should().BeFalse();
        (await db.GlAccounts.IgnoreQueryFilters().AnyAsync(x => x.Code == "5008" || x.Code == "5009" || x.Code == "5199"))
            .Should().BeFalse("Down removes the accounts it added once nothing maps to them");

        await migrator.MigrateAsync(MigrationId);
        (await db.Database.GetAppliedMigrationsAsync()).Should().Contain(MigrationId);
    }
}
