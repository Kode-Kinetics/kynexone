namespace Zayra.Api.Domain.Entities;

public class TenantHrConfig : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    // Approval routing
    public bool UseDeptHeadApproval { get; set; } = true;
    public bool UseHrFinalApproval { get; set; } = true;
    public bool UseSupervisorBeforeManager { get; set; } = false;
    public bool AllowDottedLineApproval { get; set; } = false;
    // Import behaviour
    public bool AutoCreateDeptOnImport { get; set; } = false;
    public bool AutoCreateDesignationOnImport { get; set; } = false;
    public bool RequireImportPreviewBeforeCommit { get; set; } = true;
    // Hierarchy rules
    public bool AllowCrossDeptManager { get; set; } = true;
    public bool AllowCrossLocationManager { get; set; } = true;
    // Payroll / compliance
    public bool RequireCostCenterForPayroll { get; set; } = false;
    public bool RequireGradeForApprovalPolicy { get; set; } = false;
    // Establishment matrix enforcement: Off | Advisory | Enforced. Default Enforced is
    // deploy-safe because enforcement is per-budget-row opt-in (absent row = uncontrolled);
    // changing this value is itself permission-gated + audited (never flipped by deploys).
    public string EstablishmentEnforcementMode { get; set; } = "Enforced";
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
}
