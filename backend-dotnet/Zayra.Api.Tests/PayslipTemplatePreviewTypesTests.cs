using Zayra.Api.Controllers;
using Zayra.Api.Infrastructure.Documents.Letters;
using Zayra.Api.Infrastructure.Payroll;

namespace Zayra.Api.Tests;

/// <summary>
/// The template preview HR sees must type lines the way real payslips do: an employer
/// contribution is an employer cost, never part of the employee's Total Deductions.
/// </summary>
public class PayslipTemplatePreviewTypesTests
{
    [Fact]
    public void Preview_TypesEmployerContributions_SeparatelyFromDeductions()
    {
        var sections = new[] { "deductions", "employer_contributions" }
            .Select((key, i) => new PayslipSectionConfig(key, true, i, PayslipTemplateRegistry.Sections[key].Fields.Select(f => f.Key).ToList()))
            .ToList();

        var items = PayslipTemplatesController.BuildSampleItems(new PayslipLayoutConfig("en", sections), "en");

        var employerLabels = PayslipTemplateRegistry.Sections["employer_contributions"].Fields.Select(f => f.LabelEn).ToHashSet();
        var deductionLabels = PayslipTemplateRegistry.Sections["deductions"].Fields.Select(f => f.LabelEn).ToHashSet();
        Assert.NotEmpty(employerLabels);
        Assert.All(items.Where(i => employerLabels.Contains(i.Name)), i => Assert.Equal(PayslipLineTypes.EmployerContribution, i.Type));
        Assert.All(items.Where(i => deductionLabels.Contains(i.Name)), i => Assert.Equal(PayslipLineTypes.Deduction, i.Type));
    }
}
