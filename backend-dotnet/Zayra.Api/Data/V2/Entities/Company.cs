using System;
using System.Collections.Generic;

namespace Zayra.Api.Data.V2.Entities;

/// <summary>
/// The legal entity and its MOL establishment: the registration identifiers every statutory filing is made under, the currency of record, an optional timezone override and the mid-year go-live period. @tier:T @owner:Finance @retention:Keep
/// </summary>
public partial class Company
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public string? CrNumber { get; set; }

    public string? MolEstablishmentNo { get; set; }

    /// <summary>
    /// The single writable copy of the GOSI establishment number (§11.5). A typo can exist in exactly one place and cannot propagate into a filing.
    /// </summary>
    public string GosiRegistrationNo { get; set; } = null!;

    public string NameEn { get; set; } = null!;

    public string? NameAr { get; set; }

    public string? NitaqatActivityCode { get; set; }

    public string? WpsBankCode { get; set; }

    public string? WpsMolId { get; set; }

    /// <summary>
    /// SAR is the currency of record. Every money column in the design is denominated in THIS company&apos;s currency; there is no per-row currency column anywhere (§13.2).
    /// </summary>
    public string CurrencyCode { get; set; } = null!;

    public string? TimezoneId { get; set; }

    /// <summary>
    /// The go-live period as two typed columns, not a named concept (§2.C, revision 6 correction).
    /// </summary>
    public short? GoLiveYear { get; set; }

    public short? GoLiveMonth { get; set; }

    /// <summary>
    /// Non-money company overrides only. Contractual pay parameters moved OUT to company_pay_policies in revision 3 precisely so they get the dated EXCLUDE discipline (§13.5).
    /// </summary>
    public string Settings { get; set; } = null!;

    public DateTime? SoftDeletedAt { get; set; }

    public DateTime CreatedAt { get; set; }

    public Guid? CreatedBy { get; set; }

    public DateTime? UpdatedAt { get; set; }

    public Guid? UpdatedBy { get; set; }

    public virtual ICollection<ApprovalWorkflow> ApprovalWorkflows { get; set; } = new List<ApprovalWorkflow>();

    public virtual ICollection<Branch> Branches { get; set; } = new List<Branch>();

    public virtual ICollection<CompanyPayPolicy> CompanyPayPolicies { get; set; } = new List<CompanyPayPolicy>();

    public virtual ICollection<CostCenter> CostCenters { get; set; } = new List<CostCenter>();

    public virtual ICollection<Department> Departments { get; set; } = new List<Department>();

    public virtual ICollection<DocumentTemplate> DocumentTemplates { get; set; } = new List<DocumentTemplate>();

    public virtual ICollection<EmployeeAssignment> EmployeeAssignments { get; set; } = new List<EmployeeAssignment>();

    public virtual ICollection<EmployeeGosiRegistration> EmployeeGosiRegistrationCompanies { get; set; } = new List<EmployeeGosiRegistration>();

    public virtual ICollection<EmployeeGosiRegistration> EmployeeGosiRegistrationCompanyNavigations { get; set; } = new List<EmployeeGosiRegistration>();

    public virtual ICollection<FinalSettlement> FinalSettlements { get; set; } = new List<FinalSettlement>();

    public virtual ICollection<GlJournal> GlJournals { get; set; } = new List<GlJournal>();

    public virtual ICollection<GlMapping> GlMappings { get; set; } = new List<GlMapping>();

    public virtual ICollection<GlPeriodClose> GlPeriodCloses { get; set; } = new List<GlPeriodClose>();

    public virtual ICollection<GosiFiling> GosiFilingCompanies { get; set; } = new List<GosiFiling>();

    public virtual ICollection<GosiFiling> GosiFilingCompanyNavigations { get; set; } = new List<GosiFiling>();

    public virtual ICollection<NitaqatSnapshot> NitaqatSnapshots { get; set; } = new List<NitaqatSnapshot>();

    public virtual ICollection<NumberSequence> NumberSequences { get; set; } = new List<NumberSequence>();

    public virtual ICollection<PayrollInput> PayrollInputs { get; set; } = new List<PayrollInput>();

    public virtual ICollection<PayrollRun> PayrollRuns { get; set; } = new List<PayrollRun>();

    public virtual Tenant Tenant { get; set; } = null!;

    public virtual ICollection<Timesheet> Timesheets { get; set; } = new List<Timesheet>();

    public virtual ICollection<UserRole> UserRoles { get; set; } = new List<UserRole>();

    public virtual ICollection<WpsBatch> WpsBatches { get; set; } = new List<WpsBatch>();
}
