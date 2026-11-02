# Session verdict cache

Every authenticated request validates that its access token is still current (user stamp, roles, permissions, scope,
tenant policy, company set). A positive verdict is cached in process for `Auth:SessionVerdictCache:TtlSeconds`
(default 15, hard maximum 30).

## Revocation guarantees

| Where the write happens | When a revoked session stops working |
|---|---|
| This API instance, through EF `SaveChanges` | The very next request (versions are bumped after the save, and again at commit or rollback of an outer transaction). |
| This API instance, set-based (`ExecuteUpdate`/`ExecuteDelete`/raw SQL) | Next request only if the writer calls `SessionVerdictInvalidation.*`; a ratchet test enforces this for the auth tables. |
| Another API instance, a migration, support SQL or any other process | Within the TTL (at most 30 s). |

The cache is per instance. With one instance (today's deployment) there is no gap. If the service is ever scaled out, the
guarantee for the other instances is "within the TTL". At startup the API logs a warning when `WEB_CONCURRENCY` or
`Auth:SessionVerdictCache:InstanceCount` is greater than 1 while the cache is on.

## Kill switch

Set `Auth__SessionVerdictCache__TtlSeconds=0` (Render environment variable) and redeploy or restart. Every request then runs
the full database check again, exactly as before the cache. Use it during an incident where a revocation must be immediate on
every instance, or if the cache is suspected of serving a stale verdict.

## Sizing

At most `MaxEntries` (default 50,000) verdicts are held, one per distinct valid token. The per-user and per-tenant version
maps are capped at `MaxVersionEntries` (default 100,000); reaching the cap invalidates every cached verdict once and clears the maps.
