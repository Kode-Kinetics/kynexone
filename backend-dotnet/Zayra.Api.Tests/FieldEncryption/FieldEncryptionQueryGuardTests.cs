using System.Linq.Expressions;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Infrastructure.FieldEncryption;
using Zayra.Api.Models;

namespace Zayra.Api.Tests.FieldEncryption;

public class FieldEncryptionQueryGuardTests
{
    private static string? Violation(Expression<Func<IQueryable<Employee>, object>> q) => FieldEncryptionQueryGuard.FindViolation(q);

    [Fact]
    public void Finds_EncryptedFieldsInPredicates_Orderings_Groupings_AndContains()
    {
        var ids = new List<string> { "1" };
        Violation(q => q.Where(e => e.IqamaNumber == "x")).Should().Be("Employee.IqamaNumber");
        Violation(q => q.Where(e => ids.Contains(e.PassportNumber))).Should().Be("Employee.PassportNumber");
        Violation(q => q.OrderBy(e => e.BankIban)).Should().Be("Employee.BankIban");
        Violation(q => q.GroupBy(e => e.CivilId)).Should().Be("Employee.CivilId");
        Violation(q => q.Any(e => e.IdNumber != "")).Should().Be("Employee.IdNumber");
        Violation(q => q.Where(e => EF.Property<string>(e, "Qid") == "x")).Should().Be("Employee.Qid");
    }

    [Fact]
    public void Allows_Projections_BlindIndexLookups_AndOrdinaryColumns()
    {
        Violation(q => q.Select(e => e.IqamaNumber)).Should().BeNull();
        Violation(q => q.Where(e => e.FullName == "x").Select(e => new { e.IqamaNumber, e.FullName })).Should().BeNull();
        Violation(q => q.Where(e => EF.Property<byte[]>(e, "IqamaNumberBidx") == null)).Should().BeNull();
        Violation(q => q.Where(e => e.EmployeeCode == "E1").OrderBy(e => e.FullName)).Should().BeNull();
    }

    [Fact]
    public async Task OnRealQueries_TheGuardIsInertInDualWrite_AndThrowsOnceReadsAreEncrypted()
    {
        var kit = new FeKit();
        await using (var db = kit.Db())
        {
            db.Employees.Add(FeKit.NewEmployee(Guid.NewGuid(), "E1", iqama: "2123456789")); await db.SaveChangesAsync();
            (await db.Employees.Where(e => e.IqamaNumber == "2123456789").CountAsync()).Should().Be(1, "plaintext is still authoritative in DualWrite");
        }
        // Mode >= ReadEncrypted cannot be loaded from config in this build; the guard itself is still correct when handed one.
        var readEnc = new FieldEncryptionOptions { Mode = FieldEncryptionMode.ReadEncrypted, Keks = kit.Options.Keks, ActiveKekVersion = 1 };
        var b = new DbContextOptionsBuilder<Zayra.Api.Data.ZayraDbContext>().UseInMemoryDatabase(kit.DbName).AddInterceptors(new FieldEncryptionQueryGuard(readEnc));
        await using var guarded = new Zayra.Api.Data.ZayraDbContext(b.Options);
        Func<Task> bad = async () => await guarded.Employees.Where(e => e.IqamaNumber == "2123456789").ToListAsync();
        await bad.Should().ThrowAsync<InvalidOperationException>().WithMessage("*WhereBlindIndex*");
        (await guarded.Employees.Where(e => e.EmployeeCode == "E1").Select(e => e.FullName).ToListAsync()).Should().ContainSingle();
    }
}
