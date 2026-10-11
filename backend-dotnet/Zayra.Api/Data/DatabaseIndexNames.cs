namespace Zayra.Api.Data;

/// <summary>
/// Names of raw-SQL indexes that application code must recognise (e.g. to turn a unique violation into
/// a 409). They live HERE, not on the migration class that creates them: the production Dockerfile deletes
/// Migrations/ before `dotnet publish`, so any app code that references a migration type does not compile
/// in the shipped image. Migrations may reference this class; app code must never reference Migrations.
/// </summary>
public static class DatabaseIndexNames
{
    /// <summary>One live (Exported) journal export per tenant + scope + format. Created by AddErpCodesAndLiveExportIndex.</summary>
    public const string GlJournalExportLiveScope = "ux_gl_journal_exports_live_scope";

    /// <summary>ERP company code unique per tenant, case-insensitive, live companies. Created by AddErpCodesAndLiveExportIndex.</summary>
    public const string CompanyTenantErpCode = "ux_companies_tenant_erp_company_code";
}
