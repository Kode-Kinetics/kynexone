namespace Zayra.Api.Domain.Entities;

/// <summary>
/// Marks an entity that carries a direct CompanyId column for company-level access scoping.
/// Entities implementing this interface receive a second automatic EF Core global query filter
/// (composed AND-ed with the tenant filter) that restricts results to the companies the
/// current user is authorised to see — derived lazily from JWT entity_access claims.
///
/// Naming guarantee: the property MUST be named exactly "CompanyId" (nullable Guid).
///
/// Semantics of CompanyId == null on this (base) interface: "tenant-wide / shared across
/// companies" — visible to every user in the tenant. Use it for configuration/template
/// entities only (e.g. LeavePolicy, SalaryStructure, tenant-default tax policies).
/// For operational records use ICompanyScopedOperational below.
/// </summary>
public interface ICompanyScoped
{
    Guid? CompanyId { get; set; }
}

/// <summary>
/// Marks a company-scoped OPERATIONAL record (attendance, leave, payroll, loans,
/// documents, compliance evidence…). Same automatic filter as ICompanyScoped with one
/// hardening: for company-scoped (non-group) users, CompanyId == null rows are NOT
/// visible. On operational tables null is a migration/backfill transient, never a
/// legitimate "shared" state — treating it as broadly visible would leak any row whose
/// insert path forgot to stamp CompanyId (the poison-default problem). Group-scope users
/// still see null rows so unassigned data remains discoverable and repairable.
/// </summary>
public interface ICompanyScopedOperational : ICompanyScoped
{
}
