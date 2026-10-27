using System.Text.RegularExpressions;

namespace Zayra.Api.Data.Esign;

/// <summary>
/// RAW SQL for the integrator to fold into migration <c>OnboardingP4Acknowledgements</c> (EF cannot model triggers or grants).
/// Run each constant with <c>migrationBuilder.Sql</c> (no string formatting: the text holds <c>$$</c>), in this order:
/// <see cref="CreateFunction"/>, <see cref="CreateTrigger"/>, then (optional, per-role baselines) <see cref="RevokeFromRole"/>.
/// Down: <see cref="Drop"/>.
///
/// <para>Rows of <c>employee_policy_acknowledgements</c> are evidence. UPDATE is refused for everyone, always (legacy rows too: no
/// code updates them). DELETE is refused unless the same transaction has run <c>SET LOCAL app.retention_purge = 'on'</c>, which
/// only the retention engine does after the statutory period (CD52: employment + 10 years, configurable). TRUNCATE is refused.
/// A tenant erasure that hard-deletes tenant rows must set the same local setting.</para>
/// </summary>
public static class AckAppendOnlySql
{
    public const string TriggerName = "trg_ack_append_only";
    public const string TruncateTriggerName = "trg_ack_no_truncate";
    public const string FunctionName = "ack_append_only_guard";
    public const string RetentionSetting = "app.retention_purge";
    public const string Table = "employee_policy_acknowledgements";

    /// <summary>1. The guard function.</summary>
    public const string CreateFunction = """
        CREATE OR REPLACE FUNCTION ack_append_only_guard() RETURNS trigger AS $$
        BEGIN
          IF TG_OP = 'UPDATE' THEN
            RAISE EXCEPTION 'employee_policy_acknowledgements is append-only: a record cannot be updated' USING ERRCODE = '23000';
          ELSIF TG_OP = 'DELETE' THEN
            IF coalesce(current_setting('app.retention_purge', true), '') = 'on' THEN
              RETURN OLD;
            END IF;
            RAISE EXCEPTION 'employee_policy_acknowledgements is append-only: a record cannot be deleted outside the retention purge' USING ERRCODE = '23000';
          ELSIF TG_OP = 'TRUNCATE' THEN
            RAISE EXCEPTION 'employee_policy_acknowledgements is append-only: it cannot be truncated' USING ERRCODE = '23000';
          END IF;
          RETURN NULL;
        END;
        $$ LANGUAGE plpgsql;
        """;

    /// <summary>2. The triggers.</summary>
    public const string CreateTrigger = """
        DROP TRIGGER IF EXISTS trg_ack_append_only ON employee_policy_acknowledgements;
        CREATE TRIGGER trg_ack_append_only BEFORE UPDATE OR DELETE ON employee_policy_acknowledgements
          FOR EACH ROW EXECUTE FUNCTION ack_append_only_guard();
        DROP TRIGGER IF EXISTS trg_ack_no_truncate ON employee_policy_acknowledgements;
        CREATE TRIGGER trg_ack_no_truncate BEFORE TRUNCATE ON employee_policy_acknowledgements
          FOR EACH STATEMENT EXECUTE FUNCTION ack_append_only_guard();
        """;

    /// <summary>
    /// 3. (Layer two) revoke UPDATE, DELETE and TRUNCATE from the application role, where the baseline has per-role grants.
    /// The retention engine then has to purge under a role that still holds DELETE. The trigger holds either way.
    /// </summary>
    public static string RevokeFromRole(string role)
    {
        if (!Regex.IsMatch(role, "^[A-Za-z_][A-Za-z0-9_]{0,62}$")) throw new ArgumentException("Not a plain role name.", nameof(role));
        return $"REVOKE UPDATE, DELETE, TRUNCATE ON employee_policy_acknowledgements FROM \"{role}\";";
    }

    public const string Drop = """
        DROP TRIGGER IF EXISTS trg_ack_append_only ON employee_policy_acknowledgements;
        DROP TRIGGER IF EXISTS trg_ack_no_truncate ON employee_policy_acknowledgements;
        DROP FUNCTION IF EXISTS ack_append_only_guard();
        """;
}
