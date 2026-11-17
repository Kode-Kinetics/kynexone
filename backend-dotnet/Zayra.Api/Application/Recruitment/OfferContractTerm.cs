using Zayra.Api.Application.Contracts;

namespace Zayra.Api.Application.Recruitment;

/// <summary>
/// The contract an offer promises: permanent, or fixed-term for a stated number of months. The same words are printed in the letter and
/// carried to the hire draft (type, start date, end date), so the employee's contract never contradicts what the candidate accepted.
/// An offer written before the term was recorded has no type and reads as Permanent, exactly what every hire was before.
/// </summary>
public static class OfferContractTerm
{
    public const string Permanent = "Permanent";
    /// <summary>The spelling the employee record's own contract rules (<c>ContractRules.IsFixedTerm</c>) already recognise.</summary>
    public const string FixedTerm = "Fixed-term";
    public const int MaxMonths = 120;

    /// <summary>The canonical type, or null when the text names no known type. Blank is Permanent (a legacy offer).</summary>
    public static string? Normalize(string? type)
    {
        if (string.IsNullOrWhiteSpace(type)) return Permanent;
        var t = type.Replace("-", " ").Replace("_", " ").Trim().ToLowerInvariant();
        return t switch
        {
            "permanent" or "unlimited" or "unlimited term" or "indefinite" => Permanent,
            "fixed" or "fixed term" or "limited" or "limited term" => FixedTerm,
            _ => null,
        };
    }

    /// <summary>The error code for an inconsistent type and term, or null when they agree.</summary>
    public static string? Validate(string? type, int? months)
    {
        var kind = Normalize(type);
        if (kind is null) return "offer_contract_type_invalid";
        if (kind == FixedTerm)
        {
            if (months is null) return "offer_contract_term_required";
            return months is < 1 or > MaxMonths ? "offer_contract_term_out_of_range" : null;
        }
        return months is null ? null : "offer_contract_term_not_allowed";
    }

    public static string ErrorMessage(string code) => code switch
    {
        "offer_contract_type_invalid" => "Choose Permanent or Fixed-term as the contract type.",
        "offer_contract_term_required" => "A fixed-term offer needs its term in months.",
        "offer_contract_term_out_of_range" => $"A fixed term is between 1 and {MaxMonths} months.",
        "offer_contract_term_not_allowed" => "Only a fixed-term offer has a term in months.",
        _ => "The contract type and term do not agree.",
    };

    public static string ErrorMessageAr(string code) => code switch
    {
        "offer_contract_type_invalid" => "اختر نوع العقد: غير محدد المدة أو محدد المدة.",
        "offer_contract_term_required" => "العرض محدد المدة يحتاج إلى مدة العقد بالأشهر.",
        "offer_contract_term_out_of_range" => $"مدة العقد المحدد بين شهر واحد و{MaxMonths} شهرًا.",
        "offer_contract_term_not_allowed" => "المدة بالأشهر تخص العرض محدد المدة فقط.",
        _ => "نوع العقد ومدته غير متطابقين.",
    };

    /// <summary>The last day of a fixed term starting on <paramref name="start"/>; null for a permanent offer.</summary>
    public static DateOnly? EndDate(string? type, DateOnly start, int? months) =>
        Normalize(type) == FixedTerm && months is >= 1 ? ContractTermMath.EndOf(start, months.Value) : null;

    /// <summary>The term as the letter words it ("12 months"), or null when the offer is permanent.</summary>
    public static string? LetterTerm(string? type, int? months, bool arabic = false)
    {
        if (Normalize(type) != FixedTerm || months is not >= 1) return null;
        if (!arabic) return months == 1 ? "1 month" : $"{months} months";
        return months switch { 1 => "شهر واحد", 2 => "شهران", <= 10 => $"{months} أشهر", _ => $"{months} شهرًا" };
    }
}
