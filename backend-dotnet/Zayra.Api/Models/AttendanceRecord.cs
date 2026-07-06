using Zayra.Api.Domain.Entities;
namespace Zayra.Api.Models;
public class AttendanceRecord : INullableTenantOwned, ICompanyScopedOperational
{
    public int Id { get; set; }
    public Guid? TenantId { get; set; }
    /// <summary>Legal-entity scope. Backfilled by CompanyScopeBackfill; required for new operational writes.</summary>
    public Guid? CompanyId { get; set; }
    public int EmployeeId { get; set; }
    public DateOnly WorkDate { get; set; }
    public TimeOnly? TimeIn { get; set; }
    public TimeOnly? TimeOut { get; set; }
    public decimal OvertimeHours { get; set; }
    public string Notes { get; set; } = string.Empty;
    public string Status { get; set; } = "Present";
}
