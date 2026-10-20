using Zayra.Api.Models;
using Zayra.Api.Application.Recruitment;
using Zayra.Api.Data;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

namespace Zayra.Api.Infrastructure.Recruitment;

public static class OfferComponentOptions
{
    public static async Task<OfferCompensationSnapshot> CalculateAsync(ZayraDbContext db, Guid tenantId, Guid? companyId,
        DateOnly startDate, IReadOnlyList<OfferComponentInput>? components, decimal? basic, decimal? housing,
        decimal? transport, decimal? other, CancellationToken ct)
    {
        if (tenantId == Guid.Empty) throw new ArgumentException("A tenant is required.");
        if (components is not null && companyId is null) throw new ArgumentException("Select an authorized company before using offer components.");
        var policy = companyId.HasValue ? await PolicyAsync(db, tenantId, companyId.Value, ct) : null;
        var currency = policy?.Currency ?? await OfferRules.ResolveCurrencyAsync(db, tenantId, null, ct);
        var required = new HashSet<string>(policy?.RequiredComponentCodes ?? ["BASIC"], StringComparer.OrdinalIgnoreCase);
        var inputs = components ?? OfferCompensation.FromLegacy(basic ?? 0, housing ?? 0, transport ?? 0, other ?? 0);
        var normalized = new List<OfferComponentInput>();
        var provenance = new Dictionary<string, OfferComponentSnapshot>(StringComparer.OrdinalIgnoreCase);
        var mappings = new HashSet<string>(StringComparer.Ordinal);
        var definitions = companyId.HasValue && (components is not null || required.Any(c => c != "BASIC"))
            ? await DefinitionsAsync(db, tenantId, companyId.Value, startDate, ct) : [];
        foreach (var input in inputs)
        {
            if (input is null) throw new ArgumentException("A component cannot be null.");
            var code = OfferCompensation.NormalizeCode(input.Code);
            string? mapping = null;
            var source = "Custom";
            PayComponent? definition = null;
            if (components is null)
            {
                mapping = code switch { "BASIC" => "BasicSalary", "HOUSING" => "HousingAllowance", "TRANSPORT" => "TransportAllowance", "OTHER_ALLOWANCES" => "OtherAllowance", _ => null };
                source = "LegacyFourField";
                if (code != "BASIC" && required.Contains(code))
                {
                    definition = definitions.SingleOrDefault(d => CanonicalCode(d) == code)
                        ?? throw new ArgumentException($"Required component {code} is unavailable for this company and offer start date.");
                    if (definition.CalcMethod != PayComponentCalcMethods.StructureField || definition.StructureField != mapping)
                        throw new ArgumentException($"The legacy {code} amount cannot represent its required catalogue definition. Use explicit offer components.");
                    source = "PayComponentDefinition";
                }
            }
            else if (input.PayComponentId is { } definitionId)
            {
                definition = definitions.SingleOrDefault(d => d.Id == definitionId)
                    ?? throw new ArgumentException("The selected component is unavailable for this company and offer start date.");
                if (input.Kind != "Cash") throw new ArgumentException("Catalogue earnings require an explicit cash amount.");
                code = CanonicalCode(definition);
                mapping = definition.CalcMethod == PayComponentCalcMethods.StructureField ? definition.StructureField : null;
                source = "PayComponentDefinition";
            }
            else if (code == "BASIC")
            {
                if (input.LabelEn != CanonicalBasic.LabelEn || (!string.IsNullOrEmpty(input.LabelAr) && input.LabelAr != CanonicalBasic.LabelAr))
                    throw new ArgumentException("BASIC is the reserved canonical basic salary item; custom items must use a different code and label.");
                mapping = "BasicSalary"; source = "CanonicalBasic";
            }
            else if (required.Contains(code))
                throw new ArgumentException($"Required catalogue component {code} must reference its applicable definition.");

            if (mapping is not null)
            {
                foreach (var field in ExpandMapping(mapping))
                    if (!mappings.Add(field)) throw new ArgumentException($"Selected components overlap the salary mapping {field}.");
                if ((code == "BASIC" || required.Contains(code)) && input.Amount is { } amount && decimal.Round(amount, 2) != amount)
                    throw new ArgumentException($"Required payroll-mapped component {code} must have at most two decimal places.");
            }
            normalized.Add(input with { Code = code, PayComponentId = definition?.Id ?? input.PayComponentId });
            if (!provenance.TryAdd(code, new(code, input.LabelEn, input.LabelAr, input.Kind, input.Amount, input.Description,
                    input.Frequency, input.Included, input.ExclusionReason, definition?.Id ?? input.PayComponentId, input.Order, required.Contains(code) || code == "BASIC",
                    mapping, definition?.EffectiveFrom, definition?.EffectiveTo, definition?.ComponentType, source,
                    definition?.Code, definition?.NameEn, definition?.NameAr, definition?.IsTaxable ?? false,
                    definition?.GosiSubject ?? false, definition?.WpsIncluded ?? false, definition?.EosbIncluded ?? false)))
                throw new ArgumentException($"Duplicate component code: {code}.");
        }
        var result = OfferCompensation.Calculate(normalized, currency, policy?.Precision ?? 2, required);
        var rows = result.Components.Select(r => provenance[r.Code] with { Required = r.Required }).ToArray();
        if (rows.Where(r => r.Required && r.Included && r.StructureField is not null && r.Frequency == "Monthly").Sum(r => r.Amount ?? 0) > OfferCompensation.PayrollMonthlyLimit)
            throw new ArgumentException("Required monthly salary components exceed employee salary capacity.");
        decimal Project(params string[] fields) => rows.Where(r => r.Included && r.Kind == "Cash" && r.Frequency == "Monthly"
            && r.StructureField is not null && fields.Contains(r.StructureField)).Sum(r => r.Amount ?? 0);
        result = result with
        {
            Components = rows, BasicSalary = Project("BasicSalary"), HousingAllowance = Project("HousingAllowance"),
            TransportAllowance = Project("TransportAllowance"), OtherAllowances = Project("OtherAllowance", "OtherAllowancesComposite", "FoodAllowance", "MobileAllowance"),
            PolicyVersion = policy?.ExpectedVersion ?? 0,
            PolicySource = policy is null ? "LegacyTenantFallback" : policy.ExpectedVersion == 0 ? "CompanyDefault" : "CompanyPolicy"
        };
        if (components is not null) OfferCompensation.ValidateLegacy(result, basic, housing, transport, other);
        return result;
    }

    public static OfferCanonicalBasicOption CanonicalBasic { get; } = new();
    public static string CanonicalCode(PayComponent definition) => definition.CalcMethod == PayComponentCalcMethods.StructureField
        && definition.StructureField == "BasicSalary" ? "BASIC" : OfferCompensation.NormalizeCode(definition.Code);

    public static async Task<OfferPolicyDto> PolicyAsync(ZayraDbContext db, Guid tenantId, Guid companyId, CancellationToken ct)
    {
        var company = await db.Companies.AsNoTracking().SingleOrDefaultAsync(c => c.TenantId == tenantId && c.Id == companyId && c.IsActive && !c.IsDeleted, ct)
            ?? throw new ArgumentException("The selected company is unavailable.");
        var policy = await db.OfferCompanyPolicies.AsNoTracking().SingleOrDefaultAsync(p => p.TenantId == tenantId && p.CompanyId == companyId, ct);
        var codes = policy is null ? ["BASIC"] : JsonSerializer.Deserialize<string[]>(policy.RequiredComponentCodesJson) ?? ["BASIC"];
        return new(companyId, company.DefaultCurrency.Trim().ToUpperInvariant(), policy?.Precision ?? 2,
            codes.Append("BASIC").Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(c => c).ToArray(), policy?.Version ?? 0);
    }

    public static async Task<IReadOnlyList<PayComponent>> DefinitionsAsync(ZayraDbContext db, Guid tenantId, Guid companyId, DateOnly startDate, CancellationToken ct) =>
        ResolveDefinitions(await db.PayComponents.AsNoTracking().Where(d => d.TenantId == tenantId && (d.CompanyId == null || d.CompanyId == companyId)).ToListAsync(ct), tenantId, companyId, startDate);

    private static IEnumerable<string> ExpandMapping(string mapping) => mapping == "OtherAllowancesComposite"
        ? ["FoodAllowance", "MobileAllowance", "OtherAllowance"] : [mapping];

    public static IReadOnlyList<PayComponent> ResolveDefinitions(IEnumerable<PayComponent> definitions,
        Guid tenantId, Guid companyId, DateOnly startDate) => definitions
        .Where(d => d.TenantId == tenantId && (d.CompanyId == null || d.CompanyId == companyId) && !d.IsDeleted && d.IsInEffect(startDate))
        .GroupBy(CanonicalCode, StringComparer.OrdinalIgnoreCase)
        .Select(g => g.OrderByDescending(d => d.CompanyId.HasValue).ThenByDescending(d => d.EffectiveFrom).ThenByDescending(d => d.CreatedAtUtc).ThenBy(d => d.Id).First())
        .Where(IsEligible)
        .OrderBy(d => d.DisplayOrder).ThenBy(d => d.Code).ToArray();

    private static bool IsSupportedMapping(string? field) => field is "BasicSalary" or "HousingAllowance" or "TransportAllowance"
        or "OtherAllowancesComposite" or "FoodAllowance" or "MobileAllowance" or "OtherAllowance";

    public static bool IsEligible(PayComponent d) => d.IsActive && !d.IsDeleted && d.IsOffered && d.ComponentType == PayComponentTypes.Earning && !d.IsStatutory && !d.IsFamily
        && string.IsNullOrWhiteSpace(d.ProviderKey) && string.IsNullOrWhiteSpace(d.FormulaExpression)
        && (d.CalcMethod == PayComponentCalcMethods.Fixed && string.IsNullOrEmpty(d.StructureField)
            || d.CalcMethod == PayComponentCalcMethods.StructureField && IsSupportedMapping(d.StructureField))
        && (!string.Equals(d.Code.Trim(), "BASIC", StringComparison.OrdinalIgnoreCase) || d.CalcMethod == PayComponentCalcMethods.StructureField && d.StructureField == "BasicSalary");
}
