namespace Zayra.Api.Domain.Entities;

/// <summary>
/// Marks an entity that carries a direct CompanyId column for company-level access scoping.
/// Entities implementing this interface receive a second automatic EF Core global query filter
/// (composed AND-ed with the tenant filter) that restricts results to the companies the
/// current user is authorised to see — derived lazily from JWT entity_access claims.
///
/// Naming guarantee: the property MUST be named exactly "CompanyId" (nullable Guid).
/// Null CompanyId means "not yet assigned to a company" — those rows are always visible.
/// </summary>
public interface ICompanyScoped
{
    Guid? CompanyId { get; set; }
}
