using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Onboarding;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Setup;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Recruitment;

/// <summary>What an accepted offer's hire inherits beyond the offer itself: the opening's grade, and the defaults of the one hire template
/// that fits the opening (the same resolver and the same defaults document the Add Employee wizard uses). Only blanks are filled, and
/// nothing here is money: pay comes from the offer, and the grade master stays the pay source.</summary>
internal sealed record OfferHireDefaults(string Grade, string EmploymentType, string WorkLocation)
{
    public static readonly OfferHireDefaults None = new(string.Empty, string.Empty, string.Empty);

    public static async Task<OfferHireDefaults> ResolveAsync(ZayraDbContext db, Guid tenantId, OfferLetter offer, JobOpening opening, CancellationToken ct)
    {
        var grade = string.Empty;
        if (opening.GradeId is Guid gradeId)
            grade = await db.Grades.AsNoTracking()
                .Where(g => g.TenantId == tenantId && g.Id == gradeId && g.IsActive && !g.IsDeleted)
                .Select(g => g.Code).FirstOrDefaultAsync(ct) ?? string.Empty;

        // Templates are per company. A tie suggests nothing (a template is never guessed), exactly as the wizard does.
        HireTemplateDefaultValues? values = null;
        if (offer.CompanyId is Guid companyId)
        {
            var templates = await db.HireTemplates.AsNoTracking()
                .Where(h => h.TenantId == tenantId && h.CompanyId == companyId && h.IsActive && !h.IsDeleted).ToListAsync(ct);
            var resolution = HireTemplateResolver.Resolve(templates, opening.DesignationId, opening.GradeId, null);
            if (resolution.SuggestedId is Guid suggested)
                values = HireTemplateDefaults.Parse(templates.First(t => t.Id == suggested).Defaults).Values;
        }

        var employmentType = values?.EmploymentType ?? opening.EmploymentType;
        if (!string.IsNullOrWhiteSpace(employmentType))
            employmentType = (await ReferenceList.LoadAsync(db, tenantId, ReferenceList.EmploymentTypeType, ct)).Normalize(employmentType);
        return new OfferHireDefaults(grade, employmentType?.Trim() ?? string.Empty, values?.WorkLocation ?? string.Empty);
    }
}
