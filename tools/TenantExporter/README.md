# Tenant exporter (vendor only)

Exports one customer of the retired hosted service so it can be imported into its own installation
(`--import-legacy`, WP13.2). Never shipped to clients.

```bash
export KX_EXPORT_S3_ACCESS_KEY=... KX_EXPORT_S3_SECRET_KEY=...    # only with --s3-bucket
dotnet run --project tools/TenantExporter -c Release -- \
  --connection "Host=...;Database=...;Username=...;Password=..." \
  --tenant <tenant id> --out ./export-<customer> \
  [--storage-dir <Storage:LocalRoot> | --s3-endpoint <url> --s3-bucket <bucket> --s3-region <region>]
```

- Reads the source database's catalogue, not the EF model, so it works on any hosted version without
  building that version's code. Every table with a `tenant_id` column is exported `WHERE tenant_id = @tenant`;
  `tenants`, `user_roles`, `role_permissions` and the global `permissions` catalogue are exported through their
  links. Platform, pricing and subscription tables, tokens, the data-protection key ring, the licence and worker
  heartbeats are never exported (users set two-step sign-in up again on the new installation).
- Runs read-only in one snapshot. `manifest.json` holds the source migration, each file's row count and
  SHA-256, the documents copied, and the totals the import must reproduce exactly: payroll net per run, leave
  balance per employee/type/year, loan outstanding per loan, GL debits minus credits per export.
- The output holds the customer's personnel data. Move it to the customer's server encrypted, and delete it
  once the import's verification report is signed off.
