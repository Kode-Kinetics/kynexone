using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Zayra.Api.Infrastructure.Observability;

/// <summary>
/// WAVE 1 G3 — the domain's telemetry vocabulary, in one place.
///
/// <para><b>The rule that governs everything here: telemetry is a place PII goes to leak.</b> Spans,
/// metric labels and log scopes are shipped to third-party backends, retained for months, and read by
/// people who would never be granted access to the payroll database. So no salary, no IBAN, no bank
/// detail, no identity number, no document content, no email body, no token, and no employee name ever
/// enters an attribute or a label. Identifiers that name a RECORD (a run id, a job id) are fine; values
/// that describe a PERSON are not.</para>
///
/// <para><b>And metric labels are not span attributes.</b> A tenant id on a span is one searchable
/// field; the same id as a metric label multiplies every time series by the tenant count and will take
/// the metrics backend down. Tenant, employee, request and run ids are therefore permitted on spans and
/// FORBIDDEN as metric labels — enforced by <see cref="MetricLabels"/> and its guard test.</para>
/// </summary>
public static class ZayraTelemetry
{
    public const string ServiceName = "zayra-api";

    /// <summary>Domain operations. One source, so sampling and export are configured once.</summary>
    public static readonly ActivitySource Source = new(ServiceName);

    /// <summary>Domain metrics.</summary>
    public static readonly Meter Meter = new(ServiceName);

    // ── Span attribute names ────────────────────────────────────────────────────────────────────
    // Safe: they identify a record or a category, never a person.
    public static class Attr
    {
        public const string Module = "zayra.module";
        public const string Operation = "zayra.operation";
        public const string TenantId = "zayra.tenant_id";
        public const string CompanyId = "zayra.company_id";
        public const string PayrollRunId = "zayra.payroll_run_id";
        public const string JobId = "zayra.job_id";
        public const string CorrelationId = "zayra.correlation_id";
        public const string FailureCategory = "zayra.failure_category";
        public const string RecordCount = "zayra.record_count";
        public const string ScopeSource = "zayra.scope_source";
        public const string DenialReason = "zayra.denial_reason";
    }

    /// <summary>
    /// A closed vocabulary of failure categories. Free-text error strings become high-cardinality
    /// labels and frequently carry the very data that must not be exported — an exception message is
    /// one string interpolation away from containing an IBAN.
    /// </summary>
    public static class Failure
    {
        public const string Validation = "validation";
        public const string Authorization = "authorization";
        public const string NotFound = "not_found";
        public const string Conflict = "conflict";
        public const string Dependency = "dependency";
        public const string Timeout = "timeout";
        public const string Configuration = "configuration";
        public const string Unexpected = "unexpected";
    }

    /// <summary>
    /// Label names permitted on METRICS. Deliberately tiny and deliberately excluding every identifier:
    /// bounded cardinality is what keeps a metrics backend alive.
    /// </summary>
    public static class MetricLabels
    {
        public const string Module = "module";
        public const string Operation = "operation";
        public const string Outcome = "outcome";
        public const string FailureCategory = "failure_category";
        public const string JobType = "job_type";
        public const string State = "state";

        /// <summary>Every name a metric may legally carry. The guard test asserts nothing else is used.</summary>
        public static readonly string[] Allowed =
            [Module, Operation, Outcome, FailureCategory, JobType, State];

        /// <summary>
        /// Names that must NEVER be a metric label. Each of these is unbounded in production: one time
        /// series per tenant, per employee, per request or per run.
        /// </summary>
        public static readonly string[] Forbidden =
            ["tenant_id", "tenantId", "employee_id", "employeeId", "request_id", "requestId",
             "run_id", "runId", "company_id", "companyId", "correlation_id", "correlationId",
             "user_id", "userId", "email", "iban", "salary"];
    }

    // ── Instruments ─────────────────────────────────────────────────────────────────────────────

    public static readonly Counter<long> AuthorizationDenials =
        Meter.CreateCounter<long>("zayra.authorization.denials",
            description: "Authorization refusals, by module and reason category. A cross-company spike "
                       + "here is the signal that something is probing entity boundaries.");

    public static readonly Counter<long> AuthenticationFailures =
        Meter.CreateCounter<long>("zayra.authentication.failures",
            description: "Failed authentications, by outcome category.");

    public static readonly Counter<long> RateLimitDenials =
        Meter.CreateCounter<long>("zayra.ratelimit.denials", description: "Requests refused by a rate limiter.");

    public static readonly Histogram<double> OperationDuration =
        Meter.CreateHistogram<double>("zayra.operation.duration", unit: "ms",
            description: "Duration of a named domain operation, by module and outcome.");

    public static readonly Counter<long> PayrollRunsRequested =
        Meter.CreateCounter<long>("zayra.payroll.runs_requested", description: "Payroll runs requested.");

    public static readonly Counter<long> PayrollValidationFailures =
        Meter.CreateCounter<long>("zayra.payroll.validation_failures", description: "Payroll validation failures.");

    public static readonly Counter<long> NotificationsDelivered =
        Meter.CreateCounter<long>("zayra.notifications.delivered", description: "Notification delivery outcomes.");

    public static readonly Counter<long> DocumentOperations =
        Meter.CreateCounter<long>("zayra.documents.operations",
            description: "Document upload/download/verify outcomes, by outcome category.");

    public static readonly UpDownCounter<long> PendingMigrations =
        Meter.CreateUpDownCounter<long>("zayra.database.pending_migrations",
            description: "Migrations applied to the model but not to this database. Non-zero means the "
                       + "running code and the schema disagree.");

    /// <summary>
    /// Starts a domain span. Returns null when nothing is listening, which is the normal production
    /// state with no collector configured — callers must tolerate that rather than assume a span exists.
    /// </summary>
    public static Activity? StartOperation(string module, string operation, ActivityKind kind = ActivityKind.Internal)
    {
        var activity = Source.StartActivity($"{module}.{operation}", kind);
        activity?.SetTag(Attr.Module, module);
        activity?.SetTag(Attr.Operation, operation);
        return activity;
    }
}
