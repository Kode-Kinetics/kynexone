using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Employees;

/// <summary>
/// Finds, BEFORE anything is saved, a staged import value the database cannot store: a string longer than
/// its column (e.g. a 151-character FullName in a varchar(150)) or a decimal beyond its precision (a salary
/// of 10,000,000,000 in numeric(12,2)).
///
/// <para>WHY. Those values used to surface only at SaveChanges, as one DbUpdateException for a whole
/// batched INSERT of up to hundreds of rows, so the 422 could say only "a database constraint was violated"
/// — never which row or which cell. The rules are read from the EF model (the same HasMaxLength /
/// HasPrecision metadata that generated the schema), so this cannot drift from the columns it protects.
/// Shared by the import preview and the commit, so a dry run predicts the refusal the commit would give.</para>
/// </summary>
public static class EmployeeImportStorageGuard
{
    public sealed record UnstorableValue(string Column, string Problem);

    /// <summary>Every value on <paramref name="entity"/> its column cannot hold. Empty when all fit.</summary>
    public static IReadOnlyList<UnstorableValue> Check(IModel model, object entity)
    {
        var type = model.FindEntityType(entity.GetType());
        if (type is null) return Array.Empty<UnstorableValue>();
        var problems = new List<UnstorableValue>();
        foreach (var property in type.GetProperties())
        {
            if (property.PropertyInfo is null) continue;
            var value = property.PropertyInfo.GetValue(entity);
            if (value is string text && property.GetMaxLength() is int maxLength && text.Length > maxLength)
            {
                problems.Add(new UnstorableValue(ColumnFor(entity.GetType(), property.Name),
                    $"is {text.Length} characters long; this field holds at most {maxLength}"));
            }
            else if (value is decimal number && property.GetPrecision() is int precision)
            {
                var integerDigits = precision - (property.GetScale() ?? 0);
                if (integerDigits is > 0 and < 29 && Math.Abs(decimal.Truncate(number)) >= PowerOfTen(integerDigits))
                    problems.Add(new UnstorableValue(ColumnFor(entity.GetType(), property.Name),
                        $"is {number}; this field holds at most {integerDigits} digits before the decimal point"));
            }
        }
        return problems;
    }

    private static decimal PowerOfTen(int exponent)
    {
        var result = 1m;
        for (var i = 0; i < exponent; i++) result *= 10m;
        return result;
    }

    /// <summary>The CSV column the operator typed the value into, from the registry's storage bindings
    /// (<c>emp.X</c>, <c>payrollProfile.x</c>, <c>salary:x</c>); the entity property name when no column
    /// binds to it.</summary>
    private static string ColumnFor(Type entityType, string propertyName)
        => CsvColumnByProperty.TryGetValue((entityType, propertyName), out var header) ? header : propertyName;

    private static readonly IReadOnlyDictionary<(Type, string), string> CsvColumnByProperty = BuildColumnMap();

    private static IReadOnlyDictionary<(Type, string), string> BuildColumnMap()
    {
        var map = new Dictionary<(Type, string), string>();
        foreach (var d in EmployeeFieldRegistry.Catalog)
        {
            if (d.CsvHeader is null || string.IsNullOrWhiteSpace(d.Binding)) continue;
            (Type Type, string Member)? target = d.Binding switch
            {
                var b when b.StartsWith("emp.", StringComparison.Ordinal) => (typeof(Employee), b[4..]),
                var b when b.StartsWith("payrollProfile.", StringComparison.Ordinal) => (typeof(EmployeePayrollProfile), b["payrollProfile.".Length..]),
                var b when b.StartsWith("salary:", StringComparison.Ordinal) => (typeof(EmployeeSalaryStructure), b["salary:".Length..]),
                _ => null,
            };
            if (target is not { } t) continue;
            var member = t.Member.Split('(')[0];
            if (member.Length == 0) continue;
            member = char.ToUpperInvariant(member[0]) + member[1..];
            map.TryAdd((t.Type, member), d.CsvHeader);
        }
        return map;
    }
}
