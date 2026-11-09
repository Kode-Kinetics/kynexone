using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Employees;

namespace Zayra.Api.Tests;

/// <summary>
/// The merge registry must classify EVERY table that points at an employee (an EmployeeId / EmployeeIntId column) as a blocker or as
/// ignorable with a reason, so a new table cannot slip past the merge refusal unnoticed.
/// </summary>
public sealed class EmployeeMergeRegistryTests
{
    private static ZayraDbContext Db() => new(new DbContextOptionsBuilder<ZayraDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    /// <summary>
    /// Everything that points at an employee, as registry keys: an entity with an EmployeeId / EmployeeIntId column is keyed by its name;
    /// every OTHER int property ending in "EmployeeId" (ManagerEmployeeId, SpecificEmployeeId, DelegateEmployeeId, DuplicateOfEmployeeId, ...)
    /// is keyed "Entity.Property".
    /// </summary>
    private static IReadOnlyList<string> EntitiesPointingAtAnEmployee()
    {
        using var db = Db();
        var keys = new List<string>();
        foreach (var type in db.Model.GetEntityTypes().Where(t => !t.IsOwned()))
        {
            var name = type.ClrType.Name;
            if (name != "Employee" && (type.FindProperty("EmployeeId") != null || type.FindProperty("EmployeeIntId") != null)) keys.Add(name);
            foreach (var property in type.GetProperties())
            {
                var clr = Nullable.GetUnderlyingType(property.ClrType) ?? property.ClrType;
                if (clr == typeof(int) && property.Name.EndsWith("EmployeeId", StringComparison.Ordinal) && property.Name != "EmployeeId")
                    keys.Add($"{name}.{property.Name}");
            }
        }
        return keys.Distinct().OrderBy(n => n, StringComparer.Ordinal).ToList();
    }

    [Fact]
    public void EveryEntityThatPointsAtAnEmployee_IsClassifiedAsABlockerOrIgnorable()
    {
        var unclassified = EmployeeMergeBlockers.Unclassified(EntitiesPointingAtAnEmployee());

        unclassified.Should().BeEmpty("a new table keyed by EmployeeId must be added to EmployeeMergeBlockers (a counter, or an ignorable reason) so a merge cannot strand it");
    }

    [Fact]
    public void TheCheck_FlagsAnEntityThatIsNotInTheRegistry()
    {
        EmployeeMergeBlockers.Unclassified(["EmployeeLoan", "SomeBrandNewMoneyTable"]).Should().Equal("SomeBrandNewMoneyTable");
    }

    [Fact]
    public void EveryRegistryEntry_IsARealEntity_AndEveryIgnorableHasAReason_AndEveryBlockerHasALabel()
    {
        var real = EntitiesPointingAtAnEmployee().ToHashSet(StringComparer.Ordinal);

        foreach (var rule in EmployeeMergeBlockers.Rules.Values)
        {
            real.Should().Contain(rule.Entity, $"'{rule.Entity}' is in the merge registry but no such table points at an employee any more");
            if (rule.IsBlocker) EmployeeMergeBlockers.BlockerTypeKeys.Should().Contain(rule.BlockerType, $"{rule.Entity} maps to an unknown blocker type");
            else rule.IgnorableReason.Should().NotBeNullOrWhiteSpace($"{rule.Entity} is ignorable and needs a one-line reason");
        }
    }
}
