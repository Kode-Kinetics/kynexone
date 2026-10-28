using System.ComponentModel.DataAnnotations;
using System.Reflection;
using FluentAssertions;
using Zayra.Api.Application.Employees;
using Zayra.Api.Controllers;
using Zayra.Api.Infrastructure.Employees.Onboarding;
using Xunit;

namespace Zayra.Api.Tests.Onboarding;

public class DtoBoundsTests
{
    private static readonly Type[] Covered =
    [
        typeof(EmployeeDraftRequest), typeof(DuplicateCheckRequest), typeof(DuplicateIdentityValueDto), typeof(DeriveWorkEmailRequest),
        typeof(EmployeePayrollProfileRequest), typeof(EmployeeSalaryBreakdownRequest), typeof(EmployeeCreateRequest), typeof(EmployeeComplianceRecordRequest),
    ];

    [Fact]
    public void DtoBounds_EveryStringPropertyHasABound()
    {
        var missing = new List<string>();
        foreach (var t in Covered)
            foreach (var p in t.GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(p => p.PropertyType == typeof(string)))
            {
                var ctorParam = t.GetConstructors().First().GetParameters().FirstOrDefault(c => c.Name == p.Name);
                var declared = p.GetCustomAttributes().Concat(ctorParam?.GetCustomAttributes() ?? [])
                    .Any(a => a is MaxLengthAttribute or StringLengthAttribute);
                if (!declared && DtoBoundsMetadataProvider.BoundFor(t, p.Name) is null) missing.Add($"{t.Name}.{p.Name}");
            }
        missing.Should().BeEmpty("every string property on an employee-writing DTO needs a length bound (a column is unbounded text)");
    }

    [Fact]
    public void DtoBounds_ListsAndIdentityValues_AreCapped()
    {
        DtoBoundsMetadataProvider.BoundFor(typeof(DuplicateCheckRequest), nameof(DuplicateCheckRequest.IdentityValues)).Should().Be(8);
        DtoBoundsMetadataProvider.BoundFor(typeof(DuplicateIdentityValueDto), "FieldKey").Should().Be(40);
        DtoBoundsMetadataProvider.BoundFor(typeof(DuplicateIdentityValueDto), "Value").Should().Be(64);
        DtoBoundsMetadataProvider.BoundFor(typeof(EmployeePayrollProfileRequest), "Iban").Should().Be(34);
        DtoBoundsMetadataProvider.BoundFor(typeof(EmployeeDraftRequest), "EnglishName").Should().Be(150);
    }

    [Fact]
    public void EnglishName_BoundMatchesColumn()
    {
        var attr = typeof(EmployeeCreateRequest).GetConstructors().First().GetParameters().First(p => p.Name == "EnglishName")
            .GetCustomAttribute<MaxLengthAttribute>();
        attr!.Length.Should().Be(150);
    }
}
