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
}
