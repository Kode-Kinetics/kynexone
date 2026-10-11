namespace Zayra.Api.Application.Finance;

/// <summary>Database constraint identifiers used by runtime GL export error handling.
/// Keep these outside Migrations: the production Docker build trims migration classes
/// after embedding their IDs into the readiness manifest.</summary>
public static class GlJournalExportConstraints
{
    public const string LiveScopeIndexName = "ux_gl_journal_exports_live_scope";
}
