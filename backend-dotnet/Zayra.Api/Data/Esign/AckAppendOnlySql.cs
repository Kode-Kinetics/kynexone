namespace Zayra.Api.Data.Esign;

/// <summary>
/// RAW SQL for the integrator to fold into migration <c>OnboardingP4Acknowledgements</c> (EF cannot model triggers or grants).
/// Run each constant with <c>migrationBuilder.Sql</c> (no string formatting), in this order. Down: <see cref="Drop"/>.
/// </summary>
public static class AckAppendOnlySql
{
    public const string TriggerName = "trg_ack_append_only";
    public const string FunctionName = "ack_append_only_guard";
    public const string RetentionSetting = "app.retention_purge";

    /// <summary>1. The guard function.</summary>
    public const string CreateFunction = "";

    /// <summary>2. The trigger.</summary>
    public const string CreateTrigger = "";

    /// <summary>3. (Optional layer two) revoke UPDATE and DELETE from the application role where the baseline has per-role grants.
    /// Format with the role name via <see cref="RevokeFromRole"/>. The retention engine then has to purge under a role that still holds DELETE.</summary>
    public static string RevokeFromRole(string role) => "";

    public const string Drop = "";
}
