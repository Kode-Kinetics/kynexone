using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Data;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Benefits;

public record BenefitPaymentPolicy(string Delivery = "Coverage", decimal? Amount = null, string Frequency = "Monthly",
    int? PaymentMonth = null, bool Prorate = false, Guid? SalaryComponentId = null, bool ReceiptRequired = false,
    string ReceiptLabel = "Receipt or invoice", int? ClaimWindowDays = null, string Instructions = "",
    // Education-style claim rules (reimbursement only). Defaults reproduce the plain claim exactly.
    string ClaimFor = "Employee", bool LimitPerChild = false, int? MaxChildren = null, bool ExtraChildByException = false,
    IReadOnlyList<string>? CoveredCosts = null, IReadOnlyList<string>? AllowedCountries = null, IReadOnlyList<string>? ClaimTerms = null);
public record BenefitSalaryComponentSnapshot(Guid Id, string Code, string Name, string ComponentType, bool IsTaxable);
public record BenefitPolicySnapshot(int Version, Guid PlanId, string Currency, BenefitPaymentPolicy Policy,
    BenefitSalaryComponentSnapshot? SalaryComponent = null);
public record BenefitPaymentPolicyRequest(BenefitPaymentPolicy Policy, int ExpectedPolicyVersion);

/// <summary>Explicit execution policy. A missing or legacy witness is coverage only, never a live-plan payout.</summary>
public static class BenefitPaymentPolicies
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public static readonly string[] Deliveries = ["Coverage", "SalaryAllowance", "PayrollDeduction", "Reimbursement"];
    public static readonly string[] Frequencies = ["Monthly", "Annual", "OneTime"];
    public const string ContractYearSalaryMessage = "A contract-year limit applies to claims (reimbursements). Use Annual for a salary allowance or deduction.";
    public static readonly string[] ClaimForValues = ["Employee", "Child"];
    public static readonly string[] CostLineTypes = ["Tuition", "Transport", "Books", "Uniform", "Vat", "Other"];
    public static readonly string[] ClaimTermValues = ["Semester", "HalfYear", "FullYear"];
    public static readonly string[] GccCountries = ["SA", "AE", "KW", "BH", "OM", "QA"];
    private static readonly Dictionary<string, string> CountryNames = new(StringComparer.Ordinal)
        { ["SA"] = "Saudi Arabia", ["AE"] = "the UAE", ["KW"] = "Kuwait", ["BH"] = "Bahrain", ["OM"] = "Oman", ["QA"] = "Qatar" };
    public static string CountryName(string code) => CountryNames.GetValueOrDefault(code, code);
    public static bool IsCountryCode(string? value) => value is { Length: 2 } && value.All(c => c is >= 'A' and <= 'Z');
    public static BenefitPaymentPolicy ReadPlan(BenefitPlan plan) => ReadSnapshotEnvelope(plan.PaymentPolicyJson)?.Policy ?? new();
    public static BenefitPaymentPolicy ReadSnapshot(BenefitEnrollment enrollment) => ReadSnapshotEnvelope(enrollment.PaymentPolicySnapshotJson)?.Policy ?? new();
    public static BenefitPolicySnapshot? ReadSnapshotEnvelope(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            var value = JsonSerializer.Deserialize<BenefitPolicySnapshot>(json, Json);
            return value is { Version: > 0, Policy: not null } && value.PlanId != Guid.Empty
                && Deliveries.Contains(value.Policy.Delivery) && Frequencies.Contains(value.Policy.Frequency) ? value : null;
        }
        catch (JsonException) { return null; }
    }
    public static string Snapshot(BenefitPlan plan) => ReadSnapshotEnvelope(plan.PaymentPolicyJson) is { } snapshot
        && snapshot.PlanId == plan.Id && snapshot.Version == plan.PolicyVersion ? plan.PaymentPolicyJson : "{}";

    public static async Task<string> ConfigureAsync(ZayraDbContext db, BenefitPlan plan, BenefitPaymentPolicy input, CancellationToken ct)
    {
        var policy = input with { ReceiptLabel = input.ReceiptLabel?.Trim() ?? "", Instructions = input.Instructions?.Trim() ?? "",
            ClaimFor = string.IsNullOrWhiteSpace(input.ClaimFor) ? "Employee" : input.ClaimFor.Trim(),
            AllowedCountries = input.AllowedCountries?.Select(x => x?.Trim().ToUpperInvariant() ?? "").ToList() };
        var component = await ValidateAsync(db, plan.TenantId, plan.CompanyId, plan.Currency, policy, ct);
        if (plan.PolicyVersion > 0 && ReadPlan(plan).Delivery != policy.Delivery)
            throw new InvalidOperationException("A configured plan keeps its payment behavior. Create a new benefit plan for a different delivery method.");
        return JsonSerializer.Serialize(new BenefitPolicySnapshot(plan.PolicyVersion + 1, plan.Id, plan.Currency, policy, component), Json);
    }
    public static async Task<BenefitSalaryComponentSnapshot?> ValidateAsync(ZayraDbContext db, Guid tenantId, Guid? companyId,
        string currency, BenefitPaymentPolicy policy, CancellationToken ct)
    {
        if (!Deliveries.Contains(policy.Delivery) || !Frequencies.Contains(policy.Frequency)) throw new InvalidOperationException("Select a valid benefit delivery and frequency.");
        if (policy.Amount.HasValue && (policy.Amount <= 0 || policy.Amount > 999999999999.99m || decimal.Round(policy.Amount.Value, 2) != policy.Amount))
            throw new InvalidOperationException("Benefit payment amount must be positive with at most two decimal places.");
        if (policy.Delivery is "SalaryAllowance" or "PayrollDeduction" && !policy.Amount.HasValue)
            throw new InvalidOperationException("An explicit payment amount is required for a recurring salary benefit.");
        if (policy.Frequency == "Annual" && policy.Delivery is "SalaryAllowance" or "PayrollDeduction" && policy.PaymentMonth is not (>= 1 and <= 12))
            throw new InvalidOperationException("An annual salary benefit requires its payment month (1–12).");
        if (policy.PaymentMonth.HasValue && (policy.Frequency != "Annual" || policy.PaymentMonth is < 1 or > 12))
            throw new InvalidOperationException("Payment month is only valid for annual payments.");
        if (policy.Prorate && (policy.Frequency != "Monthly" || policy.Delivery is not ("SalaryAllowance" or "PayrollDeduction")))
            throw new InvalidOperationException("Proration is supported for monthly salary benefits only.");
        if (policy.ReceiptLabel.Length > 120 || policy.Instructions.Length > 2000 || policy.ClaimWindowDays is < 1 or > 3650)
            throw new InvalidOperationException("Receipt label is limited to 120 characters, instructions to 2000, and claim window to 1–3650 days.");
        if (policy.ReceiptRequired && policy.Delivery != "Reimbursement" || policy.ClaimWindowDays.HasValue && policy.Delivery != "Reimbursement")
            throw new InvalidOperationException("Receipt requirements and claim windows apply to reimbursement benefits.");
        if (policy.ReceiptRequired && policy.ReceiptLabel.Length == 0) throw new InvalidOperationException("A receipt label is required.");
        ValidateClaimRules(policy);
        if (policy.Delivery == "Coverage")
        {
            if (policy.Amount.HasValue || policy.SalaryComponentId.HasValue) throw new InvalidOperationException("Coverage does not create a salary payment or deduction.");
            return null;
        }
        if (policy.SalaryComponentId is not Guid id) throw new InvalidOperationException("Select the salary component used to post this benefit.");
        var component = await db.SalaryComponents.AsNoTracking().FirstOrDefaultAsync(x => x.TenantId == tenantId && x.Id == id && x.IsActive, ct)
            ?? throw new InvalidOperationException("The selected salary component is unavailable.");
        var expectedType = policy.Delivery == "PayrollDeduction" ? "Deduction" : "Earning";
        if (component.ComponentType != expectedType) throw new InvalidOperationException($"Select an {expectedType.ToLowerInvariant()} salary component for this delivery.");
        if (component.SalaryStructureId.HasValue)
            throw new InvalidOperationException("Select a dedicated salary component without a salary structure. A structure component would duplicate its salary or tax basis.");
        return new(component.Id, component.Code, component.Name, component.ComponentType, component.IsTaxable);
    }

    private static void ValidateClaimRules(BenefitPaymentPolicy policy)
    {
        var claimFor = policy.ClaimFor ?? "Employee";
        if (!ClaimForValues.Contains(claimFor)) throw new InvalidOperationException("A claim is made either for the employee or for a child.");
        var anyRule = claimFor != "Employee" || policy.LimitPerChild || policy.MaxChildren.HasValue || policy.ExtraChildByException
            || policy.CoveredCosts is not null || policy.AllowedCountries is not null || policy.ClaimTerms is not null;
        if (anyRule && policy.Delivery != "Reimbursement")
            throw new InvalidOperationException("Claim rules (claiming for a child, covered costs, school countries and claim terms) apply to reimbursement benefits.");
        if ((policy.LimitPerChild || policy.MaxChildren.HasValue || policy.ExtraChildByException) && claimFor != "Child")
            throw new InvalidOperationException("A per-child limit, a maximum number of children and child exceptions need the benefit to be claimed for a child.");
        if (policy.MaxChildren is < 1 or > 20) throw new InvalidOperationException("The maximum number of children must be between 1 and 20.");
        if (policy.CoveredCosts is { } costs && (costs.Count == 0 || costs.Distinct().Count() != costs.Count || costs.Any(x => !CostLineTypes.Contains(x))))
            throw new InvalidOperationException($"Covered costs must be one or more distinct types from: {string.Join(", ", CostLineTypes)}.");
        if (policy.AllowedCountries is { } countries && (countries.Count == 0 || countries.Distinct().Count() != countries.Count || countries.Any(x => !IsCountryCode(x))))
            throw new InvalidOperationException("School countries must be one or more distinct two-letter country codes, for example SA or AE.");
        if (policy.ClaimTerms is { } terms && (terms.Count == 0 || terms.Distinct().Count() != terms.Count || terms.Any(x => !ClaimTermValues.Contains(x))))
            throw new InvalidOperationException($"Claim terms must be one or more distinct values from: {string.Join(", ", ClaimTermValues)}.");
    }
}
