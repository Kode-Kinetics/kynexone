using Zayra.Api.Models;

namespace Zayra.Api.Application.Approvals;

/// <summary>
/// The entity names an <see cref="ApprovalWorkflow"/> may be configured for: exactly those that
/// have a <b>producer</b> — a runtime path that creates an <see cref="ApprovalRequest"/> on the
/// shared approval aggregate and therefore actually routes through the configured chain.
///
/// <para><b>Why this exists.</b> <c>ApprovalWorkflowsController</c> used to accept any string as an
/// <c>EntityName</c>; the only processing was a trim. Seven chains were configured across the
/// seeders and four of them routed nothing — <c>OvertimeRequest</c>, <c>PayrollRun</c>,
/// <c>EmployeeDraft</c> and <c>EmployeeTransferRequest</c> each had a workflow a tenant could list,
/// edit and demo, and no code path that would ever consult it. A client configuring a two-step
/// "Manager → HR" transfer chain got a 200, a saved row, and no second signature, for ever. That is
/// the codebase's central defect in one line: <b>the write is validated, the read is optional.</b>
/// Per the rule this codebase already wrote down in <c>ApprovalPoliciesController</c> — a
/// configuration endpoint that no runtime path reads must refuse, never answer 200 — configuring a
/// chain for an entity with no producer is now a 400 that names the entities that do work.</para>
///
/// <para><b>Adding an entity here is a claim, and the claim is tested.</b> A name only belongs in
/// <see cref="Producers"/> once something calls
/// <see cref="IApprovalWorkflowService.CreateRequestAsync"/> (or writes an
/// <see cref="ApprovalRequest"/> directly) with it. <c>ApprovalProducerRegistryTests</c> asserts
/// that every default workflow any seeder installs names a producer, so the seed-vs-producer drift
/// that created the four dead chains cannot silently return.</para>
/// </summary>
public static class ApprovalEntities
{
    /// <summary>
    /// Every entity whose approvals are routed by a configured <see cref="ApprovalWorkflow"/>,
    /// with the producer that proves it. Ordinal-ignore-case: <c>EntityName</c> is compared
    /// case-insensitively by the router and by <c>CreateRequestAsync</c>.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> ProducersWithEvidence =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            // LeaveService.BuildApprovalProjection — the leave request's routing projection.
            [nameof(LeaveRequest)] = "submitting a leave request",
            // EmployeesController.RequestChange → IApprovalWorkflowService.CreateRequestAsync.
            [nameof(EmployeeChangeRequest)] = "requesting a governed employee change",
            // RequisitionsController.Submit → IRecruitmentService.CreateApprovalRequestAsync.
            ["ManpowerRequisition"] = "submitting a manpower requisition",
            // TimesheetService.SubmitAsync → IApprovalWorkflowService.CreateRequestAsync.
            [TimesheetConstants.ApprovalEntityName] = "submitting a timesheet",
        };

    /// <summary>The producer entity names, for validation and for the refusal body.</summary>
    public static IReadOnlyCollection<string> Producers => ProducersWithEvidence.Keys.ToArray();

    /// <summary>
    /// Entity names that have been configurable but route nothing, and why — so the refusal can say
    /// what the product actually does instead of leaving the client guessing. Anything not listed
    /// here and not in <see cref="Producers"/> simply has no meaning in this product.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> RetiredWithReason =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["OvertimeRequest"] =
                "Overtime approvals are not routed by approval workflows. Overtime owns its own two-stage chain "
                + "(PendingManager → PendingHR) with its own decision ledger, and its approvers are configured as "
                + "an HRM workflow of type 'Overtime', not here.",
            ["PayrollRun"] =
                "Payroll runs are not routed by approval workflows. A payroll run is approved on the run itself "
                + "through the payroll module's own maker-checker.",
            ["EmployeeDraft"] =
                "Employee onboarding drafts are not routed by approval workflows. A draft is reviewed and "
                + "activated on the employee record itself.",
            ["EmployeeTransferRequest"] =
                "Employee transfers are not routed by approval workflows. A transfer is approved on the transfer "
                + "request itself through the employee module's own flow.",
        };

    public static bool HasProducer(string? entityName)
        => !string.IsNullOrWhiteSpace(entityName) && ProducersWithEvidence.ContainsKey(entityName.Trim());

    /// <summary>
    /// The refusal message for an entity name nothing produces. Names the specific reason when the
    /// entity is one of the four that used to be configurable, so a client who had a chain set up
    /// is told what really governs that decision rather than only that they were wrong.
    /// </summary>
    public static string RefusalMessage(string? entityName)
    {
        var name = (entityName ?? string.Empty).Trim();
        var reason = RetiredWithReason.TryGetValue(name, out var known)
            ? known
            : $"No runtime path creates an approval request for '{name}', so a workflow configured for it would "
              + "never route anything.";
        return $"'{name}' cannot be configured as an approval workflow entity. {reason} "
             + $"Approval workflows can be configured for: {string.Join(", ", Producers.OrderBy(x => x, StringComparer.Ordinal))}.";
    }
}
