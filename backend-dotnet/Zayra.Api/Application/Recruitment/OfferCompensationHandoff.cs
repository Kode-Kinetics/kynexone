using System.Text.Json.Serialization;
using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using Zayra.Api.Application.Employees;
using Zayra.Api.Infrastructure.Recruitment;
using Zayra.Api.Models;

namespace Zayra.Api.Application.Recruitment;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record OfferCompensationDisposition(string Code, string Action, string? PayrollField = null,
    string? ProcessName = null, string? Reason = null);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record OfferCompensationHandoffRequest(long ExpectedVersion,
    IReadOnlyList<OfferCompensationDisposition> Dispositions, bool Confirm);
public sealed record OfferCompensationHandoffDto(Guid? AcceptedOfferId, Guid? SourceCompanyId,
    string State, long Version, OfferCompensationSnapshot? Snapshot,
    IReadOnlyList<OfferCompensationDisposition> Dispositions, DateOnly? EffectiveDate,
    DateTime? ConfirmedAtUtc, IReadOnlyList<string> Blockers, Guid? PlacementCompanyId,
    string PlacementProvenance, string PlacementGuidance);
public sealed record OfferHandoffPlacement(Guid? CompanyId, string? Currency, Guid? DepartmentId,
    Guid? DesignationId, Guid? BranchId, Guid? GradeId);
public sealed record OfferHandoffEvaluation(IReadOnlyList<string> Blockers, string Fingerprint,
    EmployeeSalaryBreakdownRequest? Salary);

public static class OfferCompensationHandoff
{
    public const string ReviewMessage = "Accepted-offer compensation needs review and confirmation by a user with employees.sensitive before activation.";
    private static readonly string[] Fields = ["BasicSalary", "HousingAllowance", "TransportAllowance", "FoodAllowance", "MobileAllowance", "OtherAllowance"];
    public static bool HasSnapshot(EmployeeDraft draft) => draft.AcceptedOfferCompensationJson is not null;
    public static bool IsLegacyUnscopedSnapshot(EmployeeDraft draft)
    {
        if (draft.AcceptedOfferId is null || draft.AcceptedOfferCompanyId is not null) return false;
        OfferCompensationSnapshot? snapshot;
        try { snapshot = Snapshot(draft); } catch (JsonException) { return false; }
        var fields = new Dictionary<string, string> { ["BASIC"] = "BasicSalary", ["HOUSING"] = "HousingAllowance",
            ["TRANSPORT"] = "TransportAllowance", ["OTHER_ALLOWANCES"] = "OtherAllowance" };
        return snapshot is { SchemaVersion: 1, PolicySource: "LegacyTenantFallback" }
            && snapshot.Components is { Count: 4 } && snapshot.Components.Select(c => c.Code).Distinct().Count() == 4
            && snapshot.Components.All(c => c.Kind == "Cash" && c.Frequency == "Monthly" && c.MappingSource == "LegacyFourField"
                && fields.TryGetValue(c.Code, out var field) && c.StructureField == field);
    }
    public static string ReviewGuidance(EmployeeDraft draft) => IsLegacyUnscopedSnapshot(draft)
        && draft.OfferCompensationPlacementCompanyId is null
        ? "A group-level HR user must choose an explicit company placement through department or branch and confirm the legacy offer compensation before activation."
        : ReviewMessage;
    public static string PlacementProvenance(EmployeeDraft draft) => !HasSnapshot(draft) ? "LegacySnapshotUnavailable"
        : draft.AcceptedOfferCompanyId is not null ? "AcceptedOffer"
        : IsLegacyUnscopedSnapshot(draft) ? draft.OfferCompensationPlacementCompanyId is null ? "ExplicitLegacyPlacementRequired" : "ConfirmedLegacyPlacement"
        : "UnsupportedMissingSourceCompany";
    public static OfferCompensationSnapshot? Snapshot(EmployeeDraft draft) => draft.AcceptedOfferCompensationJson is null
        ? null : JsonSerializer.Deserialize<OfferCompensationSnapshot>(draft.AcceptedOfferCompensationJson, OfferCompensation.Json);
    public static IReadOnlyList<OfferCompensationDisposition> Dispositions(EmployeeDraft draft) =>
        draft.OfferCompensationDispositionsJson is null ? []
            : JsonSerializer.Deserialize<OfferCompensationDisposition[]>(draft.OfferCompensationDispositionsJson, OfferCompensation.Json) ?? [];
    public static string EditableInputs(EmployeeDraft draft) => JsonSerializer.Serialize(new {
        draft.Salary, draft.JoiningDate, draft.Department, draft.Designation, draft.Branch, draft.Grade, draft.PayrollProfileCode
    }, OfferCompensation.Json);
    public static void Invalidate(EmployeeDraft draft)
    {
        if (!HasSnapshot(draft)) return;
        draft.OfferCompensationVersion++;
        draft.OfferCompensationConfirmedFingerprint = null;
        draft.OfferCompensationConfirmedAtUtc = null;
        draft.OfferCompensationConfirmedByUserId = null;
    }

    public static OfferHandoffEvaluation Evaluate(EmployeeDraft draft, OfferHandoffPlacement placement,
        IReadOnlyList<OfferCompensationDisposition> dispositions, bool requireConfirmed)
    {
        if (!HasSnapshot(draft)) return new([], "", null); // No fabricated terms for historical offers.
        var problems = new List<string>();
        OfferCompensationSnapshot? snapshot;
        try { snapshot = Snapshot(draft); }
        catch (JsonException) { snapshot = null; }
        if (snapshot is null || snapshot.SchemaVersion != 1 || snapshot.Components is null)
            return new(["The retained offer snapshot is unreadable or unsupported; review the original offer."], "", null);
        var boundCompany = draft.AcceptedOfferCompanyId ?? draft.OfferCompensationPlacementCompanyId;
        if (draft.AcceptedOfferId is null || (boundCompany is { } company && placement.CompanyId != company)
            || (boundCompany is null && (!IsLegacyUnscopedSnapshot(draft) || placement.CompanyId is null || requireConfirmed)))
            problems.Add("Placement must belong to the accepted offer's employing company; correct the placement or obtain a new offer.");
        if (IsLegacyUnscopedSnapshot(draft) && draft.OfferCompensationPlacementCompanyId is null && requireConfirmed)
            problems.Add(ReviewGuidance(draft));
        if (!string.Equals(snapshot.Currency, placement.Currency?.Trim().ToUpperInvariant(), StringComparison.Ordinal))
            problems.Add("The employing company currency no longer matches the accepted currency; resolve the company currency before confirming. No conversion is performed.");
        if (draft.Salary != snapshot.BasicSalary)
            problems.Add("Draft salary must equal the accepted basic salary. Restore that value or obtain a new offer; monthly gross is calculated separately.");
        var date = draft.JoiningDate is { } joining ? DateOnly.FromDateTime(joining) : (DateOnly?)null;
        if (date is null) problems.Add("Choose an explicit joining date before confirming compensation.");
        var amounts = Fields.ToDictionary(f => f, _ => 0m, StringComparer.Ordinal);
        var mapped = new HashSet<string>(StringComparer.Ordinal);
        var codes = new HashSet<string>(StringComparer.Ordinal);
        var supplied = new Dictionary<string, OfferCompensationDisposition>(StringComparer.Ordinal);
        if (dispositions.Count > 100) problems.Add("At most 100 component dispositions are allowed.");
        foreach (var disposition in dispositions)
        {
            if (disposition is null || string.IsNullOrWhiteSpace(disposition.Code) || !supplied.TryAdd(disposition.Code, disposition))
                problems.Add("Each component must have exactly one disposition.");
        }
        foreach (var row in snapshot.Components)
        {
            if (!codes.Add(row.Code)) { problems.Add("The retained snapshot contains duplicate component codes."); continue; }
            if (!supplied.TryGetValue(row.Code, out var disposition)) { problems.Add($"Choose a disposition for {row.Code}."); continue; }
            if (disposition.ProcessName?.Length > 200 || disposition.Reason?.Length > 1000)
                problems.Add($"Keep the process name and explanation within the permitted length for {row.Code}.");
            if (!row.Included)
            {
                if (disposition.Action != "Excluded" || disposition.PayrollField is not null || disposition.ProcessName is not null)
                    problems.Add($"{row.Code} was excluded in the accepted offer and must remain excluded.");
                continue;
            }
            if (row.Code == "BASIC" && (disposition.Action != "Payroll" || disposition.PayrollField != "BasicSalary"))
                problems.Add("Required BASIC must map to BasicSalary and cannot be excluded or deferred.");
            if (disposition.Action == "Payroll")
            {
                var field = disposition.PayrollField;
                if (row.Kind != "Cash" || row.Frequency != "Monthly" || field is null || !amounts.ContainsKey(field)
                    || (row.StructureField is not null && row.StructureField != field)
                    || (field == "BasicSalary" && row.Code != "BASIC") || disposition.ProcessName is not null)
                { problems.Add($"{row.Code} requires a supported explicit monthly earnings mapping consistent with the accepted terms."); continue; }
                if (!mapped.Add(field)) problems.Add($"More than one component maps to {field}; duplicate mappings are not allowed.");
                if (row.Amount is not { } amount || amount < 0 || amount > OfferCompensation.PayrollMonthlyLimit || decimal.Round(amount, 2) != amount)
                { problems.Add($"{row.Code} cannot be represented exactly at payroll precision and capacity; use an explicit named process where permitted."); continue; }
                if (date is { } effective && ((row.EffectiveFrom is { } from && effective < from) || (row.EffectiveTo is { } to && effective > to)))
                    problems.Add($"{row.Code} is outside its frozen applicability window on the joining date.");
                amounts[field] = amount;
            }
            else if (disposition.Action == "NamedProcess")
            {
                if (string.IsNullOrWhiteSpace(disposition.ProcessName) || string.IsNullOrWhiteSpace(disposition.Reason) || disposition.PayrollField is not null)
                    problems.Add($"Name the separate process and explain how {row.Code} will be handled.");
            }
            else if (disposition.Action == "OfferOnlyBenefit" && row.Kind == "Benefit")
            {
                if (string.IsNullOrWhiteSpace(disposition.Reason) || disposition.PayrollField is not null || disposition.ProcessName is not null)
                    problems.Add($"Explain the non-payroll benefit disposition for {row.Code}.");
            }
            else problems.Add($"Included {row.Code} cannot be excluded; choose a payroll mapping or named process.");
        }
        if (supplied.Keys.Any(code => !codes.Contains(code))) problems.Add("A disposition references a component absent from the accepted offer.");
        if (!mapped.Contains("BasicSalary") || amounts["BasicSalary"] <= 0 || amounts["BasicSalary"] != snapshot.BasicSalary)
            problems.Add("The accepted positive basic salary must be mapped exactly to BasicSalary.");
        if (amounts.Values.Sum() > OfferCompensation.PayrollMonthlyLimit)
            problems.Add("Mapped monthly gross exceeds employee salary capacity; review explicit component dispositions.");
        var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new {
            draft.AcceptedOfferId, draft.AcceptedOfferCompanyId, draft.OfferCompensationPlacementCompanyId, draft.AcceptedOfferCompensationJson,
            draft.OfferCompensationVersion, Inputs = EditableInputs(draft), placement, date,
            Dispositions = supplied.Values.OrderBy(d => d.Code, StringComparer.Ordinal)
        }, OfferCompensation.Json)))).ToLowerInvariant();
        if (requireConfirmed && (draft.OfferCompensationConfirmedFingerprint != fingerprint || draft.OfferCompensationConfirmedAtUtc is null || draft.OfferCompensationConfirmedByUserId is null))
            problems.Add("Compensation confirmation is missing or stale. Review the current placement, date and dispositions and confirm again.");
        return new(problems, fingerprint, problems.Count > 0 ? null : new(
            amounts["BasicSalary"], amounts["HousingAllowance"], amounts["TransportAllowance"], amounts["FoodAllowance"],
            amounts["MobileAllowance"], amounts["OtherAllowance"], 0m, draft.PayrollProfileCode, date, snapshot.Currency));
    }
}
