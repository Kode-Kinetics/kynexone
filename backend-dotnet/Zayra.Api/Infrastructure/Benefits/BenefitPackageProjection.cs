using System.Text.Json;
using Zayra.Api.Controllers;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Benefits;

public record EmployeeBenefitPackageDto(int EmployeeId, string EmployeeName, Guid? GradeId, Guid? CompanyId, DateOnly AsOf,
    IReadOnlyList<BenefitEnrollmentDto> Enrollments, IReadOnlyList<AdditionalBenefitRequestDto> AdditionalRequests);

public static class BenefitPackageProjection
{
    public static BenefitEnrollmentDto From(BenefitEnrollment row, BenefitPlan? plan, Employee employee, DateOnly today)
    {
        var reasons = new List<string>();
        var effective = row.Status is "Cancelled" or GradeBenefitDefaults.SupersededStatus ? row.Status : row.EffectiveTo < today ? "Expired" : row.EffectiveFrom > today ? "Scheduled" : row.Status == "Active" ? "Current" : row.Status;
        var terminal = effective is "Expired" or "Cancelled" or GradeBenefitDefaults.SupersededStatus;
        AdditionalBenefitGrantRequest? terms = null;
        var currency = plan?.Currency;
        var classification = plan?.Classification;
        if (row.AssignmentSource == AdditionalBenefitGrants.Source)
        {
            if (row.ReviewDate <= today && !terminal) reasons.Add("Review date reached");
            if (row.CompanyId != employee.CompanyId && !terminal) reasons.Add("Employee company changed; issuing-company review required");
            try
            {
                var snapshot = JsonSerializer.Deserialize<AdditionalBenefitProposal>(row.EligibilitySnapshotJson, new JsonSerializerOptions(JsonSerializerDefaults.Web));
                terms = snapshot?.Terms;
                currency = snapshot?.Currency ?? currency;
                classification = snapshot?.PlanClassification ?? classification;
                if (!terminal && (plan is null || !plan.IsActive || plan.IsDeleted)) reasons.Add("Benefit plan is no longer active");
                if (snapshot is not null && plan is not null && !terminal && (snapshot.Currency != plan.Currency || snapshot.PlanClassification != plan.Classification))
                    reasons.Add("Benefit plan currency or classification changed");
                if (snapshot?.GradeId != employee.GradeId && !terminal) reasons.Add("Employee grade changed");
                if (snapshot is null) reasons.Add("Original approval needs review");
            }
            catch (JsonException) { reasons.Add("Original approval needs review"); }
        }
        return BenefitEnrollmentDto.From(row) with
        {
            PlanName = plan?.Name ?? "Benefit plan", PlanCode = plan?.Code, Currency = currency, Classification = classification,
            EffectiveStatus = effective, ReviewRequired = reasons.Count > 0, ReviewReasons = reasons,
            Treatment = terms?.Treatment, PlannedEmployerCost = terms?.PlannedEmployerCost, PlannedEmployeeCost = terms?.PlannedEmployeeCost, CostFrequency = terms?.CostFrequency,
        };
    }
}
