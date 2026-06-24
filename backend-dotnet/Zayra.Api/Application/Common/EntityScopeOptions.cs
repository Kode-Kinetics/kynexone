namespace Zayra.Api.Application.Common;

/// <summary>
/// Controls the cutover from fail-open → fail-closed for company scope resolution.
///
/// StrictMode = false (default): absence of entity_access claims → GroupLevel (backward-compat
/// for existing users whose JWTs pre-date the AddCompanyScope migration).
///
/// StrictMode = true (post-cutover): absence of entity_access AND is_group_scope=true claim
/// → Empty (default-deny). Group scope must derive from an explicit is_group_scope claim,
/// never from claim absence. Enable only after all existing users have re-authenticated.
///
/// Set via appsettings: EntityScope:StrictMode = true  OR
/// env var:             EntityScope__StrictMode=true
/// </summary>
public sealed class EntityScopeOptions
{
    public bool StrictMode { get; set; } = false;
}
