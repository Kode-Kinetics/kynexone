using Zayra.Api.Application.Recruitment;
using System.Text.RegularExpressions;
using System.Text.Json;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Recruitment;

public static class OfferCompensation
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    // Typed so an Employee can never be persisted as an offer compensation snapshot.
    public static string SerializeSnapshot(OfferCompensationSnapshot snapshot) => JsonSerializer.Serialize(snapshot, Json);

    public static void ApplySnapshot(OfferLetter offer, OfferCompensationSnapshot snapshot)
    {
        offer.CompensationSnapshotJson = SerializeSnapshot(snapshot);
        offer.CompensationCurrency = snapshot.Currency;
        offer.CompensationPrecision = snapshot.Precision;
        offer.OfferPolicyVersion = snapshot.PolicyVersion;
        offer.BasicSalary = snapshot.BasicSalary;
        offer.HousingAllowance = snapshot.HousingAllowance;
        offer.TransportAllowance = snapshot.TransportAllowance;
        offer.OtherAllowances = snapshot.OtherAllowances;
        offer.GrossSalary = snapshot.MonthlyCash;
        offer.AnnualRecurringCash = snapshot.AnnualRecurringCash;
        offer.OneTimeCash = snapshot.OneTimeCash;
    }

    public static OfferCompensationSnapshot Calculate(IReadOnlyList<OfferComponentInput> inputs, string currency,
        int precision, IReadOnlySet<string> requiredCodes)
    {
        if (precision is < 0 or > 3) throw new ArgumentException("Offer precision must be between zero and three.");
        currency = (currency ?? "").Trim().ToUpperInvariant();
        if (!Regex.IsMatch(currency, "^[A-Z]{3}$")) throw new ArgumentException("A company currency is required.");
        if (inputs is null || inputs.Count is < 1 or > 100) throw new ArgumentException("Provide between one and 100 components.");
        var required = new HashSet<string>(requiredCodes.Select(NormalizeCode), StringComparer.OrdinalIgnoreCase) { "BASIC" };
        var codes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var rows = new List<OfferComponentSnapshot>();
        decimal monthly = 0, annual = 0, oneTime = 0;
        foreach (var input in inputs)
        {
            if (input is null) throw new ArgumentException("A component cannot be null.");
            var code = NormalizeCode(input.Code);
            if (!codes.Add(code)) throw new ArgumentException($"Duplicate component code: {code}.");
            if (string.IsNullOrWhiteSpace(input.LabelEn) || input.LabelEn.Length > 200 || input.LabelAr?.Length > 200
                || input.Description?.Length > 2000 || input.ExclusionReason?.Length > 500)
                throw new ArgumentException($"Provide valid labels and descriptions for {code}.");
            var isRequired = required.Contains(code);
            if (!input.Included && (isRequired || string.IsNullOrWhiteSpace(input.ExclusionReason)))
                throw new ArgumentException($"{code} is required or needs an exclusion reason.");
            if (input.Kind == "Cash")
            {
                if (input.Amount is not { } amount || amount < 0 || amount > MaxAmount || decimal.Round(amount, precision) != amount)
                    throw new ArgumentException($"{code} requires a non-negative amount within offer precision and limits.");
                if (input.Frequency is not ("Monthly" or "Annual" or "OneTime"))
                    throw new ArgumentException($"Unsupported payment frequency for {code}.");
                if (code == "BASIC" && (input.Frequency != "Monthly" || decimal.Round(amount, 2) != amount || amount > PayrollMonthlyLimit))
                    throw new ArgumentException("Basic salary must be monthly and fit employee salary capacity and payroll precision (two decimals).");
                if (input.Included)
                {
                    if (input.Frequency == "Monthly") monthly += amount;
                    else if (input.Frequency == "Annual") annual += amount;
                    else oneTime += amount;
                }
            }
            else if (input.Kind != "Benefit" || input.Amount.HasValue || input.Frequency is not null || string.IsNullOrWhiteSpace(input.Description))
                throw new ArgumentException($"{code} must be cash or a descriptive benefit without a cash amount/frequency.");
            if (code == "BASIC" && input.Kind != "Cash") throw new ArgumentException("Basic salary must be cash.");
            rows.Add(new(code, input.LabelEn.Trim(), input.LabelAr?.Trim(), input.Kind, input.Amount, input.Description,
                input.Frequency, input.Included, input.ExclusionReason, input.PayComponentId, input.Order, isRequired));
        }
        foreach (var code in required)
            if (!codes.Contains(code)) throw new ArgumentException($"Required component {code} is missing.");
        annual += monthly * 12;
        if (monthly > MaxAmount || annual > MaxAmount || oneTime > MaxAmount)
            throw new ArgumentException("Compensation totals exceed the supported amount limit.");
        decimal Monthly(string code) => rows.Where(r => r.Code == code && r.Included && r.Kind == "Cash" && r.Frequency == "Monthly").Sum(r => r.Amount ?? 0);
        return new(1, currency, precision, rows.OrderBy(r => r.Order).ToArray(), monthly, annual, oneTime,
            Monthly("BASIC"), Monthly("HOUSING"), Monthly("TRANSPORT"), Monthly("OTHER_ALLOWANCES"));
    }

    public const decimal MaxAmount = 9_999_999_999_999_999.999m;
    // The salary writer also updates Employee.Salary (numeric(12,2)), the narrowest mandatory target.
    public const decimal PayrollMonthlyLimit = 9_999_999_999.99m;
    public static string NormalizeCode(string code)
    {
        var normalized = (code ?? "").Trim().ToUpperInvariant();
        if (!Regex.IsMatch(normalized, "^[A-Z][A-Z0-9_]{0,63}$")) throw new ArgumentException("Component codes must use letters, numbers and underscores (64 characters maximum).");
        return normalized;
    }

    public static IReadOnlyList<OfferComponentInput> FromLegacy(decimal basic, decimal housing, decimal transport, decimal other) =>
    [
        new("BASIC", "Basic salary", null, "Cash", basic, null, "Monthly", true, null, null, 0),
        new("HOUSING", "Housing allowance", null, "Cash", housing, null, "Monthly", true, null, null, 1),
        new("TRANSPORT", "Transport allowance", null, "Cash", transport, null, "Monthly", true, null, null, 2),
        new("OTHER_ALLOWANCES", "Other allowances", null, "Cash", other, null, "Monthly", true, null, null, 3)
    ];

    public static void ValidateLegacy(OfferCompensationSnapshot snapshot, decimal? basic, decimal? housing, decimal? transport, decimal? other)
    {
        if ((basic.HasValue && basic != snapshot.BasicSalary) || (housing.HasValue && housing != snapshot.HousingAllowance)
            || (transport.HasValue && transport != snapshot.TransportAllowance) || (other.HasValue && other != snapshot.OtherAllowances))
            throw new ArgumentException("Legacy amounts contradict the component amounts. Submit one consistent representation.");
    }
}
