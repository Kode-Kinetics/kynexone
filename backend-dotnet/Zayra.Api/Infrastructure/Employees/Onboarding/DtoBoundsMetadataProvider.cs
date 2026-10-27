using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.ModelBinding.Metadata;
using Zayra.Api.Application.Employees;
using Zayra.Api.Controllers;

namespace Zayra.Api.Infrastructure.Employees.Onboarding;

/// <summary>
/// Adds length bounds to the string properties of the employee-writing DTOs without editing the one-line
/// records that declare them. Every covered property gets a bound (a named one, else the type default), so
/// a property added later is bounded automatically; an overlong value is a 400 before it reaches a column.
/// </summary>
public sealed class DtoBoundsMetadataProvider : IValidationMetadataProvider
{
    private static readonly HashSet<Type> Covered =
    [
        typeof(EmployeeDraftRequest), typeof(DuplicateCheckRequest), typeof(DuplicateIdentityValueDto), typeof(DeriveWorkEmailRequest),
        typeof(EmployeePayrollProfileRequest), typeof(EmployeeSalaryBreakdownRequest), typeof(EmployeeCreateRequest), typeof(EmployeeComplianceRecordRequest),
    ];

    private static readonly Dictionary<string, int> Named = new(StringComparer.OrdinalIgnoreCase)
    {
        ["EnglishName"] = 150, ["ArabicName"] = 180, ["PreferredName"] = 120, ["EmployeeCode"] = 50,
        ["PersonalEmail"] = 254, ["WorkEmail"] = 254, ["LocalPart"] = 64,
        ["Phone"] = 32, ["EmergencyContactPhone"] = 32, ["MobileNumber"] = 60, ["Gender"] = 40, ["MaritalStatus"] = 60, ["Nationality"] = 80,
        ["NationalityCode"] = 2, ["CountryCode"] = 10, ["CurrentStep"] = 40, ["EmergencyContactName"] = 180,
        ["Iban"] = 34, ["BankIban"] = 34, ["AccountNumber"] = 34, ["BankName"] = 120, ["MolId"] = 40, ["BankRoutingCode"] = 40,
        ["SalaryCurrency"] = 3, ["Currency"] = 3, ["SalaryStructureCode"] = 60, ["SalaryStructureReference"] = 80,
        ["FieldKey"] = 120, ["FieldLabel"] = 180, ["FieldValue"] = 200,
        ["Value"] = 64, ["PassportNumber"] = 20, ["IqamaNumber"] = 20, ["BorderNumber"] = 20, ["EmiratesId"] = 20, ["Qid"] = 20, ["CivilId"] = 20,
        ["WpsBankDetails"] = 200, ["SponsorName"] = 180,
    };

    private const int DraftDefault = 120, OtherDefault = 200;

    /// <summary>The effective bound for a string/list property of a covered DTO; null when the type is not covered.</summary>
    public static int? BoundFor(Type container, string property)
    {
        if (!Covered.Contains(container)) return null;
        if (container == typeof(DuplicateCheckRequest) && property == nameof(DuplicateCheckRequest.IdentityValues)) return 8;
        if (container == typeof(DuplicateIdentityValueDto) && property == nameof(DuplicateIdentityValueDto.FieldKey)) return 40;
        if (container == typeof(DuplicateIdentityValueDto) && property == nameof(DuplicateIdentityValueDto.Value)) return 64;
        if (Named.TryGetValue(property, out var n)) return n;
        return container == typeof(EmployeeDraftRequest) ? DraftDefault : OtherDefault;
    }

    public void CreateValidationMetadata(ValidationMetadataProviderContext context)
    {
        Type? container; string? name;
        switch (context.Key.MetadataKind)
        {
            case ModelMetadataKind.Property: container = context.Key.ContainerType; name = context.Key.Name; break;
            case ModelMetadataKind.Parameter: container = context.Key.ParameterInfo?.Member.DeclaringType; name = context.Key.ParameterInfo?.Name; break;
            default: return;
        }
        if (container is null || name is null) return;
        var type = context.Key.ModelType;
        var isString = type == typeof(string);
        var isList = container == typeof(DuplicateCheckRequest) && name.Equals(nameof(DuplicateCheckRequest.IdentityValues), StringComparison.OrdinalIgnoreCase);
        if (!isString && !isList) return;
        var bound = BoundFor(container, char.ToUpperInvariant(name[0]) + name[1..]);
        if (bound is null) return;
        if (context.Attributes.Any(a => a is MaxLengthAttribute or StringLengthAttribute)) return;
        context.ValidationMetadata.ValidatorMetadata.Add(new MaxLengthAttribute(bound.Value));
    }
}
