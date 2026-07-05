using Zayra.Api.Domain.Entities;
namespace Zayra.Api.Models;

// ── Master Data ───────────────────────────────────────────────────────────────

public class MasterDataType : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public string Code { get; set; } = string.Empty;       // e.g. "EmploymentType", "MaritalStatus"
    public string NameEn { get; set; } = string.Empty;
    public string NameAr { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public bool IsSystemDefined { get; set; }               // system types cannot be deleted
    public bool AllowCustomValues { get; set; } = true;
    public bool IsActive { get; set; } = true;
    public bool IsDeleted { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public Guid? CreatedBy { get; set; }
    public DateTime? UpdatedAtUtc { get; set; }
    public Guid? UpdatedBy { get; set; }
}

public class MasterDataValue : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid TypeId { get; set; }
    public string Code { get; set; } = string.Empty;
    public string ValueEn { get; set; } = string.Empty;
    public string ValueAr { get; set; } = string.Empty;
    public string? ExtraJson { get; set; }                  // optional metadata per value
    public int SortOrder { get; set; }
    public bool IsDefault { get; set; }
    public bool IsSystemDefined { get; set; }
    public bool IsActive { get; set; } = true;
    public bool IsDeleted { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public Guid? CreatedBy { get; set; }
    public DateTime? UpdatedAtUtc { get; set; }
    public Guid? UpdatedBy { get; set; }
}

// ── Numbering Rules ───────────────────────────────────────────────────────────

public class NumberingRule : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public string EntityType { get; set; } = string.Empty;  // Employee, Requisition, Payroll, Contract, etc.
    public string Prefix { get; set; } = string.Empty;      // e.g. "EMP", "REQ", "PAY"
    public string Suffix { get; set; } = string.Empty;
    public int PaddingLength { get; set; } = 5;             // e.g. 5 → EMP-00001
    public string Separator { get; set; } = "-";
    public bool IncludeYear { get; set; }                   // e.g. EMP-2026-00001
    public bool IncludeMonth { get; set; }
    public int CurrentSequence { get; set; } = 0;
    public bool ResetYearly { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public Guid? CreatedBy { get; set; }
    public DateTime? UpdatedAtUtc { get; set; }
    public Guid? UpdatedBy { get; set; }
}

// ── System Settings ───────────────────────────────────────────────────────────

public class SystemSetting : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public string Category { get; set; } = string.Empty;    // Security, General, Payroll, Attendance, etc.
    public string SettingKey { get; set; } = string.Empty;
    public string SettingValue { get; set; } = string.Empty;
    public string DataType { get; set; } = "string";        // string, int, bool, json, decimal
    public string Description { get; set; } = string.Empty;
    public bool IsEncrypted { get; set; }
    public bool IsReadOnly { get; set; }
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
    public Guid? UpdatedBy { get; set; }
}

// ── GCC Compliance Settings ───────────────────────────────────────────────────

public class GCCComplianceSetting : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public string CountryCode { get; set; } = string.Empty; // SA, AE, KW, BH, QA, OM
    // WPS/SIF
    public bool WpsEnabled { get; set; }
    public string WpsAgentId { get; set; } = string.Empty;
    public string WpsMolCode { get; set; } = string.Empty;
    public bool SifEnabled { get; set; }
    // EOSB / Gratuity
    public bool EosbEnabled { get; set; }
    public decimal EosbYears1To5Rate { get; set; }           // days per year
    public decimal EosbYearsAbove5Rate { get; set; }
    public int EosbMinYears { get; set; } = 1;
    // Weekend / Work Week
    public string WorkWeek { get; set; } = "Sun-Thu";        // Sun-Thu, Mon-Fri, Mon-Sat
    public string WeekendDays { get; set; } = "Fri,Sat";
    // Visa / Document rules
    public bool VisaTrackingEnabled { get; set; } = true;
    public int VisaAlertDays { get; set; } = 60;
    public bool IqamaRequired { get; set; }
    public int IqamaAlertDays { get; set; } = 60;
    public bool EmiratesIdRequired { get; set; }
    // Ramadan
    public bool RamadanHoursEnabled { get; set; }
    public int RamadanReducedHoursPerDay { get; set; } = 2;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
    public Guid? UpdatedBy { get; set; }
}

// ── Location / Site ───────────────────────────────────────────────────────────

public class Location : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid? BranchId { get; set; }
    public string Code { get; set; } = string.Empty;
    public string NameEn { get; set; } = string.Empty;
    public string NameAr { get; set; } = string.Empty;
    public string AddressLine1 { get; set; } = string.Empty;
    public string AddressLine2 { get; set; } = string.Empty;
    public string City { get; set; } = string.Empty;
    public string CountryCode { get; set; } = string.Empty;
    public string PostalCode { get; set; } = string.Empty;
    public decimal? Latitude { get; set; }
    public decimal? Longitude { get; set; }
    public decimal? GeofenceRadiusMeters { get; set; }
    public bool IsActive { get; set; } = true;
    public bool IsDeleted { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public Guid? CreatedBy { get; set; }
    public DateTime? UpdatedAtUtc { get; set; }
    public Guid? UpdatedBy { get; set; }
}

// ── Fiscal Year ───────────────────────────────────────────────────────────────

public class FiscalYear : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public string Code { get; set; } = string.Empty;        // e.g. FY2026
    public int Year { get; set; }
    public DateOnly StartDate { get; set; }
    public DateOnly EndDate { get; set; }
    public string Status { get; set; } = "Open";            // Open, Closed, Current
    public bool IsCurrent { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public Guid? CreatedBy { get; set; }
    public DateTime? ClosedAtUtc { get; set; }
    public Guid? ClosedBy { get; set; }
}

// ── Notification Templates ────────────────────────────────────────────────────

public class NotificationTemplate : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public string Code { get; set; } = string.Empty;        // e.g. LEAVE_APPROVED, PAYSLIP_READY
    public string EventType { get; set; } = string.Empty;
    public string Channel { get; set; } = "InApp";          // Email, InApp, SMS, WhatsApp
    public string SubjectEn { get; set; } = string.Empty;
    public string SubjectAr { get; set; } = string.Empty;
    public string BodyEn { get; set; } = string.Empty;
    public string BodyAr { get; set; } = string.Empty;
    public string Variables { get; set; } = string.Empty;   // comma-separated placeholders
    public bool IsActive { get; set; } = true;
    public bool IsDeleted { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public Guid? CreatedBy { get; set; }
    public DateTime? UpdatedAtUtc { get; set; }
    public Guid? UpdatedBy { get; set; }
}

// ── Admin Audit Log ───────────────────────────────────────────────────────────

public class AdminAuditLog : ITenantOwned, ICompanyScoped
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    // Legal-entity dimension: set for company-scoped actions, null for tenant/group-level
    // actions (config semantics — null rows stay visible to every tenant user).
    public Guid? CompanyId { get; set; }
    public string EntityType { get; set; } = string.Empty;
    public string EntityId { get; set; } = string.Empty;
    public string Action { get; set; } = string.Empty;      // Created, Updated, Deleted, Activated, etc.
    public string OldValuesJson { get; set; } = string.Empty;
    public string NewValuesJson { get; set; } = string.Empty;
    public Guid? PerformedBy { get; set; }
    public string PerformedByName { get; set; } = string.Empty;
    public string IpAddress { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}
