using System.Security.Claims;
using Zayra.Api.Models;

namespace Zayra.Api.Application.Employees.Ess;

/// <summary>
/// What a section decision returns to the reviewer: the request's status and note plus its values, masked exactly as the HR inbox
/// masks them unless the caller holds employees.sensitive. The stored request is never serialised.
/// </summary>
public sealed record EssDecisionView(
    Guid Id, int EmployeeId, string? Section, string Status, DateTime CreatedAtUtc, DateTime? DecidedAtUtc, string? ReviewNote,
    IReadOnlyList<EssDecisionField> Fields)
{
    public static EssDecisionView From(EmployeeProfileChangeRequest change, Employee employee, ClaimsPrincipal user)
    {
        var mayReveal = user.HasClaim("permission", "employees.sensitive");
        var fields = new List<EssDecisionField>();
        if (change.Section is not null && EssSectionKeys.IsKnown(change.Section))
        {
            var values = EssSectionDecisionService.ReadValues(change.RequestedChangesJson);
            foreach (var f in ProfileSectionRegistry.FieldsFor(change.Section, EssSectionDecisionService.ClassOf(employee)))
            {
                if (!values.TryGetValue(f.Key, out var v)) continue;
                fields.Add(new EssDecisionField(f.Key, f.LabelKey, mayReveal || !f.Sensitive ? v : ProfileSectionRegistry.Mask(f, v)));
            }
        }
        return new EssDecisionView(change.Id, change.EmployeeId, change.Section, change.Status, change.CreatedAtUtc, change.DecidedAtUtc, change.ReviewNote, fields);
    }
}

public sealed record EssDecisionField(string Field, string LabelKey, string Value);
