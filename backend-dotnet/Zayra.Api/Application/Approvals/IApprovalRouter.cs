namespace Zayra.Api.Application.Approvals;

/// <summary>
/// The ONE approval router (F1 — approval engine convergence). Every approval that routes by
/// configuration — leave today; expenses, assets, air tickets and timesheets next — asks this
/// service which <see cref="Models.ApprovalWorkflow"/> applies and who approves each step.
///
/// <para>Before F1 there were two configuration models and two routers: <c>LeaveService</c> read
/// <c>ApprovalPolicy</c> while tenants configured <c>ApprovalWorkflow</c>, found nothing, and fell
/// into a hard-coded single step — a configured two-step chain executed as one click. There is now
/// one model, one router, and no silent fallback: an entity with no applicable workflow raises
/// <see cref="ApprovalRouteNotConfiguredException"/>.</para>
/// </summary>
public interface IApprovalRouter
{
    /// <summary>
    /// Picks the active workflow for <paramref name="entityName"/> that applies to the employee, by
    /// specificity: department + grade → department → grade → unscoped (tenant-wide). Deterministic:
    /// ties are broken by IsDefault, then CreatedAtUtc, then Id. When <paramref name="employeeId"/> is
    /// null (a request with no employee subject) only unscoped workflows are candidates.
    /// </summary>
    /// <exception cref="ApprovalRouteNotConfiguredException">No active workflow applies.</exception>
    /// <exception cref="ApprovalRouteInvalidException">The chosen workflow cannot complete (no steps, or no step marked final).</exception>
    Task<ApprovalRoute> ResolveAsync(Guid tenantId, int? employeeId, string entityName, CancellationToken ct);

    /// <summary>As <see cref="ResolveAsync"/>, but returns null instead of throwing when nothing is configured.</summary>
    Task<ApprovalRoute?> TryResolveAsync(Guid tenantId, int? employeeId, string entityName, CancellationToken ct);

    /// <summary>
    /// Resolves the workflow a piece of CONFIGURATION pins for this entity, in preference to the
    /// specificity match — today, <c>LeavePolicy.ApprovalWorkflowId</c>.
    ///
    /// <para><b>Why this exists.</b> A leave policy has carried an <c>ApprovalWorkflowId</c> since
    /// before F1: it is in the payload, settable on create and on update, and it round-trips. It was
    /// read by nothing. A client configuring "sick leave is approved by HR only; annual leave goes
    /// line manager → HR; unpaid leave needs the MD" saw all three save, spot-checked one, and then
    /// had every leave type route through the single department-level workflow — which for sick
    /// leave means the line manager sees the request.</para>
    ///
    /// <para>Returns null, never throws, when the pin cannot be honoured: the workflow was deleted,
    /// deactivated, or belongs to another entity. The caller then falls back to
    /// <see cref="ResolveAsync"/>, so a stale pin degrades to the behaviour that was there before
    /// rather than blocking the submission. Unlike <see cref="LoadAsync"/> — which loads an
    /// in-flight request's workflow whether or not it is still active — this is a NEW routing
    /// decision, so an inactive workflow is not a candidate.</para>
    /// </summary>
    Task<ApprovalRoute?> ResolvePinnedAsync(Guid tenantId, Guid workflowId, string entityName, CancellationToken ct);

    /// <summary>
    /// Loads the workflow an in-flight request is pinned to (<c>ApprovalRequest.WorkflowId</c>),
    /// whether or not it is still active — deactivating a workflow stops NEW routing, it does not
    /// strand requests already on it. Returns null when no such workflow exists in the tenant.
    /// </summary>
    Task<ApprovalRoute?> LoadAsync(Guid tenantId, Guid workflowId, CancellationToken ct);

    /// <summary>
    /// Resolves who acts on <paramref name="step"/> for the given subject employee. Deterministic.
    /// A person-type step whose person cannot be resolved, has been deleted, or would be the subject
    /// themself is routed to the "HR Manager" role queue with <see cref="ResolvedApprover.Escalated"/> set.
    /// </summary>
    Task<ResolvedApprover> ResolveApproverAsync(Guid tenantId, int? subjectEmployeeId, ApprovalRouteStep step, CancellationToken ct);
}

/// <summary>A resolved, validated approval chain. <see cref="WorkflowId"/> is always a real ApprovalWorkflow.Id.</summary>
public sealed record ApprovalRoute(
    Guid WorkflowId,
    string Code,
    string Name,
    string EntityName,
    Guid? DepartmentId,
    Guid? GradeId,
    // DepartmentAndGrade | Department | Grade | Default | Pinned — which rule selected it.
    string MatchedOn,
    IReadOnlyList<ApprovalRouteStep> Steps)
{
    public ApprovalRouteStep FirstStep => Steps[0];

    public ApprovalRouteStep? FindStep(int stepOrder) => Steps.FirstOrDefault(s => s.StepOrder == stepOrder);

    public ApprovalRouteStep? NextStepAfter(int stepOrder) =>
        Steps.Where(s => s.StepOrder > stepOrder).OrderBy(s => s.StepOrder).FirstOrDefault();
}

public sealed record ApprovalRouteStep(
    int StepOrder,
    string StepName,
    string ApproverType,
    string ApproverRole,
    int? SpecificEmployeeId,
    int? EscalationAfterHours,
    bool IsFinalStep);

/// <param name="ApproverType">The step's approver type, normalised ("Role" when the step is a role queue).</param>
/// <param name="QueueRole">The role that may act when no concrete user is routed (or the person's role label).</param>
/// <param name="EmployeeId">The concrete approver employee, when one resolved.</param>
/// <param name="UserId">That employee's login, when linked. Null means the step is decided by <paramref name="QueueRole"/>.</param>
/// <param name="Escalated">True when a person-type step could not resolve its person and fell to the HR Manager queue.</param>
public sealed record ResolvedApprover(
    string ApproverType,
    string QueueRole,
    int? EmployeeId,
    Guid? UserId,
    string Name,
    bool Escalated);

/// <summary>Base type for approval CONFIGURATION errors. Subclasses <see cref="InvalidOperationException"/>
/// so every existing catch site already turns it into a 4xx with its message rather than a 500.</summary>
public abstract class ApprovalRoutingException : InvalidOperationException
{
    protected ApprovalRoutingException(string code, Guid tenantId, string entityName, string message)
        : base(message)
    {
        Code = code;
        TenantId = tenantId;
        EntityName = entityName;
    }

    /// <summary>Stable machine-readable error code for API clients.</summary>
    public string Code { get; }
    public Guid TenantId { get; }
    public string EntityName { get; }
}

/// <summary>
/// No active approval workflow applies. This is a tenant CONFIGURATION error: the request is refused
/// (nothing is written, no balance is reserved) rather than routed to a guessed approver.
/// </summary>
public sealed class ApprovalRouteNotConfiguredException : ApprovalRoutingException
{
    public const string ErrorCode = "approval_route_not_configured";

    public ApprovalRouteNotConfiguredException(Guid tenantId, string entityName, int? employeeId)
        : base(ErrorCode, tenantId, entityName,
            $"No active approval workflow is configured for '{entityName}'" +
            (employeeId.HasValue ? $" that applies to employee {employeeId.Value}" : string.Empty) +
            ". An administrator must configure one under Approvals → Workflows before this can be submitted.")
    {
        EmployeeId = employeeId;
    }

    public int? EmployeeId { get; }
}

/// <summary>The applicable workflow exists but cannot complete a request (no steps, or no step marked final).</summary>
public sealed class ApprovalRouteInvalidException : ApprovalRoutingException
{
    public const string ErrorCode = "approval_route_invalid";

    public ApprovalRouteInvalidException(Guid tenantId, string entityName, Guid workflowId, string code, string reason)
        : base(ErrorCode, tenantId, entityName,
            $"Approval workflow '{code}' for '{entityName}' is misconfigured: {reason} An administrator must correct it under Approvals → Workflows.")
    {
        WorkflowId = workflowId;
    }

    public Guid WorkflowId { get; }
}
