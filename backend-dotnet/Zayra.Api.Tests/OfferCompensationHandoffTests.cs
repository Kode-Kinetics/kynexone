using System.Text.Json;
using FluentAssertions;
using Zayra.Api.Application.Recruitment;
using Zayra.Api.Infrastructure.Recruitment;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

public sealed class OfferCompensationHandoffTests
{
    internal static EmployeeDraft Draft()
    {
        var snapshot = OfferCompensation.Calculate([
            new("BASIC", "Basic salary", null, "Cash", 1000, null, "Monthly", true, null, null, 0),
            new("FOOD", "Meals", null, "Cash", 150, null, "Monthly", true, null, null, 1),
            new("ANNUAL", "Annual travel", null, "Cash", 1200, null, "Annual", true, null, null, 2),
            new("MEDICAL", "Medical", null, "Benefit", null, "Family cover", null, true, null, null, 3)
        ], "SAR", 2, new HashSet<string> { "BASIC" });
        return new() { AcceptedOfferId = Guid.NewGuid(), AcceptedOfferCompanyId = Guid.NewGuid(),
            AcceptedOfferCompensationJson = JsonSerializer.Serialize(snapshot, OfferCompensation.Json),
            OfferCompensationVersion = 1, Salary = 1000, JoiningDate = new(2027, 1, 1, 0, 0, 0, DateTimeKind.Utc) };
    }
    internal static OfferCompensationDisposition[] Dispositions() => [
        new("BASIC", "Payroll", "BasicSalary"), new("FOOD", "Payroll", "FoodAllowance"),
        new("ANNUAL", "NamedProcess", ProcessName: "Travel reimbursement", Reason: "Annual ticket through benefits request"),
        new("MEDICAL", "OfferOnlyBenefit", Reason: "Insurer enrollment recorded outside salary")];
    internal static OfferHandoffPlacement Placement(EmployeeDraft d) => new(d.AcceptedOfferCompanyId, "SAR", null, null, null, null);

    [Fact]
    public void ExplicitMappingsUseFrozenAmountsCurrencyAndDateWithoutDeductionOrOtherFallback()
    {
        var draft = Draft(); var result = OfferCompensationHandoff.Evaluate(draft, Placement(draft), Dispositions(), false);
        result.Blockers.Should().BeEmpty(); result.Salary.Should().NotBeNull();
        result.Salary!.BasicSalary.Should().Be(1000); result.Salary.FoodAllowance.Should().Be(150);
        result.Salary.OtherAllowance.Should().Be(0); result.Salary.FixedDeduction.Should().Be(0);
        result.Salary.Currency.Should().Be("SAR"); result.Salary.EffectiveDate.Should().Be(new DateOnly(2027, 1, 1));
    }

    [Theory]
    [InlineData("unresolved")][InlineData("duplicate")][InlineData("precision")][InlineData("excludeCash")]
    [InlineData("excludeBasic")][InlineData("deduction")][InlineData("annualPayroll")][InlineData("missingReason")]
    [InlineData("salary")][InlineData("company")][InlineData("currency")][InlineData("date")]
    [InlineData("boundMapping")][InlineData("effectiveWindow")][InlineData("stale")]
    public void InvalidOrStaleCompensationNeverProducesPayroll(string scenario)
    {
        var draft = Draft(); var rows = Dispositions(); var placement = Placement(draft);
        switch (scenario)
        {
            case "unresolved": rows = rows[..1]; break;
            case "duplicate": rows[1] = rows[1] with { PayrollField = "BasicSalary" }; break;
            case "excludeCash": rows[1] = rows[1] with { Action = "Excluded", PayrollField = null, Reason = "Omit" }; break;
            case "excludeBasic": rows[0] = rows[0] with { Action = "NamedProcess", PayrollField = null, ProcessName = "External", Reason = "Moved" }; break;
            case "deduction": rows[1] = rows[1] with { PayrollField = "FixedDeduction" }; break;
            case "annualPayroll": rows[2] = new("ANNUAL", "Payroll", "OtherAllowance"); break;
            case "missingReason": rows[2] = rows[2] with { Reason = null }; break;
            case "salary": draft.Salary = 1150; break;
            case "company": placement = placement with { CompanyId = Guid.NewGuid() }; break;
            case "currency": placement = placement with { Currency = "USD" }; break;
            case "date": draft.JoiningDate = null; break;
            case "precision": ChangeRow(draft, 1, r => r with { Amount = 150.001m }); break;
            case "boundMapping": ChangeRow(draft, 1, r => r with { StructureField = "HousingAllowance" }); break;
            case "effectiveWindow": ChangeRow(draft, 1, r => r with { EffectiveTo = new(2026, 12, 31) }); break;
            case "stale": draft.OfferCompensationConfirmedFingerprint = "old"; break;
        }
        var result = OfferCompensationHandoff.Evaluate(draft, placement, rows, scenario == "stale");
        result.Blockers.Should().NotBeEmpty(scenario); result.Salary.Should().BeNull(scenario);
    }

    [Fact]
    public void FingerprintChangesForActualPlacementDateAndMappingEvenWithoutDraftVersionChange()
    {
        var draft = Draft(); var placement = Placement(draft); var rows = Dispositions();
        var original = OfferCompensationHandoff.Evaluate(draft, placement, rows, false).Fingerprint;
        OfferCompensationHandoff.Evaluate(draft, placement with { DepartmentId = Guid.NewGuid() }, rows, false).Fingerprint.Should().NotBe(original);
        draft.JoiningDate = draft.JoiningDate!.Value.AddDays(1);
        OfferCompensationHandoff.Evaluate(draft, placement, rows, false).Fingerprint.Should().NotBe(original);
    }

    [Fact]
    public void UnsupportedCompositeCashRequiresNamedProcessAndKeepsFullAcceptedAmount()
    {
        var draft = Draft(); ChangeRow(draft, 1, r => r with { StructureField = "OtherAllowancesComposite", Amount = 150.001m });
        var rows = Dispositions(); rows[1] = new("FOOD", "NamedProcess", ProcessName: "Reviewed supplemental benefit", Reason: "Payroll field cannot represent composite three-decimal cash");
        var result = OfferCompensationHandoff.Evaluate(draft, Placement(draft), rows, false);
        result.Blockers.Should().BeEmpty(); result.Salary!.FoodAllowance.Should().Be(0);
        JsonSerializer.Deserialize<OfferCompensationSnapshot>(draft.AcceptedOfferCompensationJson!, OfferCompensation.Json)!.Components[1].Amount.Should().Be(150.001m);
    }
    private static void ChangeRow(EmployeeDraft draft, int index, Func<OfferComponentSnapshot, OfferComponentSnapshot> change)
    {
        var snapshot = JsonSerializer.Deserialize<OfferCompensationSnapshot>(draft.AcceptedOfferCompensationJson!, OfferCompensation.Json)!;
        var rows = snapshot.Components.ToArray(); rows[index] = change(rows[index]);
        draft.AcceptedOfferCompensationJson = JsonSerializer.Serialize(snapshot with { Components = rows }, OfferCompensation.Json);
    }
}
