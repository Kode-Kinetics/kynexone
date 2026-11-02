# Field-level encryption runbook

Scope: bank IBAN / account number, Iqama, national ID (`id_number`, dependant `national_id`), MOL id, passport,
Emirates ID, QID, Civil ID, Border Number and `wps_bank_details`, in the tables listed in
`EncryptedFieldCatalog` (`Infrastructure/FieldEncryption/EncryptedFieldCatalog.cs`). That file is the source of truth;
`FieldEncryptionCoverageRatchetTests` fails when a new identifier-shaped column is neither listed nor exempted.

## Prerequisites BEFORE any KEK is set (all environments that hold real data)

Setting `FieldEncryption__Kek__<n>` is the point of no easy return for the tenant keys, and (with the ring flag, below) for MFA and
provider secrets. Do not set it until every item here is done and recorded:

1. **Escrow holders are named by the owner**, in writing, in the vault item. Minimum: the password-manager vault item plus one sealed
   copy held by a second named person. The KEK is never stored in the database, a backup or a repository.
2. **A restore drill has passed** on a Neon branch: boot a branch of production with the same KEK from escrow (not from the live
   secret), run `verify` on a sample tenant, and prove the DataProtection ring opens. Record the date.
3. **A KEK-loss drill has been read aloud**: everyone involved knows what a lost KEK costs (next section).
4. The previous image is known to run against the new schema (the migrations are expand-only).

## Custody model (owner decision, 2026-10-25)

> The tenant key is with the client; the platform key is with us.

| Key | Held by | Where | Purpose |
|---|---|---|---|
| Platform KEK | Platform (us) | Render secret `FieldEncryption__Kek__<n>` (32 random bytes, base64), behind `IKeyEncryptionKeyProvider` | Wraps every tenant DEK and blind-index root, and (only if the ring flag is on) the DataProtection key ring |
| Tenant DEK(s) + blind-index root | Wrapped by the KEK in table `field_encryption_keys` | Postgres | AES-256-GCM per field; HMAC for equality lookup |
| Client recovery copy | **The client only** | Downloaded by the tenant's top admin, sealed with a passphrase or RSA-3072+ public key **the client supplies** (PBKDF2-SHA256 at a fixed 600,000 iterations, or RSA-OAEP) | Restore after a revoke; proof the client controls the data |

Who can do what (enforced in `FieldEncryptionAdminService`, tested in `FieldKeyCustodyTests`):

| Action | Tenant top admin (`Admin` role + `security.manage`) | Platform operator | Notes |
|---|---|---|---|
| Export recovery copy | Yes, with own passphrase / public key | **Never** (route removed; service refuses and audits) | The platform never holds a copy; it cannot hold a client's keys. |
| Request revoke / cancel a pending revoke | Yes | **Never** | Cancellable by any tenant admin during the cooling-off. |
| Restore | Yes | Yes, **only** with the client's own copy + passphrase / key | Copy must be the latest export. |
| Rotate DEK, rewrap under a new KEK, backfill, re-encrypt, repair, verify | No | Yes (platform role Admin or Owner) | Rotation and rewrap are step-up gated. |

Controls on every export / revoke / cancel / restore / rotate / rewrap:
- **Step-up MFA**: the request carries a current authenticator code (`totpCode`), checked at request time (a code is valid for about
  90 seconds, well inside the required five minutes) and single-use. Without it: 403 `mfa_step_up_required`, nothing changes, and a
  `...StepUpFailed` audit row is written.
- **Every tenant admin is notified** (in-app) of each action.
- **Everything is audited, including denials and refusals**, with `actor=tenant-admin | platform-operator | system`. Metadata carries
  fingerprints and versions only, never a passphrase, key or recovery blob.
- **Revoke cooling-off**: `POST .../revoke` returns 202 with `effectiveAtUtc`; nothing is destroyed until then (default 24 h,
  `FieldEncryption__RevokeCoolingOffHours`, 0 to 720). Another admin can cancel (`POST .../revoke/cancel`). While pending, rotation is
  refused. The destruction re-checks that one recovery export covers every key version.
- **Revoke is refused (409) until the mode is `EncryptedOnly`.** In Off / DualWrite / ReadEncrypted the plaintext still sits beside the
  ciphertext, so a revoke would hide nothing and only make saves fail. `EncryptedOnly` does not exist in this build, so revoke is
  inert in release 1.
- A revoked or unopenable key never produces a 500: saves of protected fields return **423** (`field_encryption_revoked`) or **503**
  (`field_encryption_unavailable`) with a fixed, safe message. Unrelated fields still save.

### What "revoke" does and does not do
Revoke NULLs the platform-wrapped DEK and blind-index root of every key version in the live database. It does **not** reach backups:
Neon point-in-time history, branches and any database dump taken earlier still hold the wrapped material, and the KEK can open it. So
a revoke is absolute only after the backup retention window has passed or the KEK version that wrapped those rows is destroyed
(**destroying the KEK version is the real cryptographic erase**). Say this to clients; the audit text says the same.

### Restore rules
Restore is refused unless: the tenant is revoked; the copy is the **latest export** (fingerprint match on every key row; a stale copy
is refused); it covers exactly the key versions on file; and every DEK and the blind-index root verify against the canary stored at key
creation. Nothing is activated unless every key passed. The key version that was Active at revoke time is re-established as Active.
The copy's passphrase / private key reaches the server only for this call, is never stored or logged, and opened key bytes are wiped
afterwards. Prefer public-key mode, and never reuse a passphrase.

### Later: enterprise BYOK (documented, not built)
`IKeyEncryptionKeyProvider` exposes only `Wrap` / `Unwrap(kekVersion, blob, context)` / `HasVersion`. A client-KMS provider implements
the same methods; `kek_version` on each key row selects the key. Revoking the client's KMS grant then becomes a client-side kill switch.

## Key generation ceremony (platform KEK)

1. Two people, on a trusted machine, in a terminal: `openssl rand -base64 32`. Never paste it into chat, tickets or CI logs.
2. Store it in the password-manager vault item `KynexOne / Field-encryption KEK v1` and the second sealed copy (see Prerequisites).
3. **Only after the Prerequisites are complete**, set Render secret `FieldEncryption__Kek__1` on the API service. Do not put it in
   `render.yaml`; the repository is public.
4. Record the SHA-256 of the key (not the key) in the vault item so a later copy can be checked.
5. Deploy with `FieldEncryption__Mode=Off` and confirm the boot log prints `Field encryption mode: Off`. **A KEK alone changes nothing
   else**: the DataProtection ring is not touched (see next section).

**Lost KEK.** With the ring flag off, losing every copy of the KEK loses all field ciphertext (the plaintext columns are the safety net
until release 3). With the ring flag on it also loses every MFA / SMTP / Qiwa secret protected by the encrypted ring entries,
permanently. The startup guard refuses to boot rather than fork the ring (below).

## DataProtection key ring (MFA, SMTP, Qiwa and provider secrets)

The ring is **never rewritten implicitly**. Having a KEK does not touch it. Three separate, explicit things exist:

| Thing | Switch | Effect |
|---|---|---|
| Encrypt NEW ring entries | `FieldEncryption__EncryptDataProtectionRing=true` (needs a KEK) | Keys DataProtection creates from then on are KEK-encrypted. Default **false**. |
| Encrypt EXISTING plaintext rows | `POST /api/platform/field-encryption/dp-ring/encrypt` (step-up, platform Admin/Owner; refused unless the flag is true) | One-off job. Idempotent. Refuses if the ring already holds entries this process cannot open. |
| Move ring entries to a newer KEK | `POST /api/platform/field-encryption/dp-ring/rewrap` | Unwraps and re-wraps only the per-entry key; ciphertext untouched. |

`GET /api/platform/field-encryption/dp-ring` reports rows, encrypted/plaintext counts, KEK versions in use, missing versions and
unreadable entries.

**Startup guard (every environment).** If the ring contains KEK-encrypted entries and any KEK that protects one is missing or wrong,
the process **fails to start** with a message naming the missing `FieldEncryption__Kek__<n>`. It never starts and forks the ring
(DataProtection would otherwise skip the unreadable keys and create a new one, silently invalidating every MFA secret). In
Production and Staging it also refuses `Mode=Off` while any tenant has field keys.

**Rollback (read before enabling the flag).** An older image cannot read an encrypted ring: it cannot resolve `KekXmlDecryptor`, skips
the keys, and creates a new one - every MFA user and operator is locked out with no boot error. Therefore:
1. Keep `EncryptDataProtectionRing` **off** until release 1 is final and no image older than the one carrying `KekXmlDecryptor` can be
   deployed again. Ship the decryptor first; encrypt a release later.
2. Enabling the flag and running the encrypt job is one-way. The only undo is a database restore of `data_protection_keys` from before
   the job (re-protecting any secret changed since is then your problem), so take a Neon branch first.
3. If an old image was deployed against an encrypted ring: redeploy the current image with the KEK before users sign in again; do not
   delete ring rows.

## KEK rotation (correct steps)
1. Generate `FieldEncryption__Kek__2`; **keep `__Kek__1`**. Active defaults to the highest (or set `FieldEncryption__ActiveKekVersion`).
   The ring and tenant rows name the KEK version that protects them, so entries under v1 stay readable while v1 is configured.
2. Deploy. New material wraps under v2; v1 still unwraps.
3. For every tenant: `POST /api/platform/field-encryption/{tenantId}/rewrap-kek {"totpCode":"..."}`; confirm `kek_version = 2` on every
   `field_encryption_keys` row.
4. If the ring flag was ever on: `POST .../dp-ring/rewrap`, then `GET .../dp-ring` and confirm `kekVersionsInUse` is `[2]`.
5. Take a fresh Neon branch and prove it opens with only `Kek__2` (the restore drill again).
6. Only then remove `__Kek__1`, and only after every backup you intend to restore from is on v2 or past retention. The startup guard
   will refuse to boot if a ring entry still names v1.

## DEK rotation (per tenant)
`POST /api/platform/field-encryption/{tenantId}/rotate-dek {"totpCode":"..."}` adds a new Active version (older versions stay
readable, the blind-index root is carried over so stored indexes stay valid). Then **the client must download a fresh recovery copy**
(the old one lacks the new version, so a revoke is refused and a restore of it is refused as stale). Then `POST .../reencrypt` in
slices until `complete:true`, then `POST .../verify`. Rotate before any single DEK has sealed on the order of 2^32 values (random
96-bit nonces); at this product's volumes that is far off, but rotation is the hard trigger.

## Rollout, release by release

Mode is `FieldEncryption__Mode` = `Off | DualWrite | ReadEncrypted | EncryptedOnly`. Only `Off` and `DualWrite` exist in this build;
the other two are refused at startup.

**Release 1 (this build): DualWrite + backfill + verify + repair**
1. Complete the **Prerequisites**. Merge and deploy with Mode `Off`. The migrations only ADD nullable columns, NOT VALID envelope CHECKs
   (validated by the next migration without blocking writes), the `field_encryption_keys` table, and the `ix_employees_bidx_*` indexes
   (built `CONCURRENTLY` in their own non-transactional migration). Old code ignores them.
2. Set the KEK secret (ring flag stays off). Confirm the boot log. Do not enable the ring flag in this release.
3. Set Mode `DualWrite`. Every new or changed protected field now writes a format-2 ciphertext + blind index beside the plaintext.
   Format 2 binds tenant, `table.column` and (for Guid-keyed rows) the row id, so a value cannot be moved to another row; format 1
   values written earlier still open. Readers still read plaintext, so nothing user-visible changes.
4. Backfill per tenant: `POST /api/platform/field-encryption/{tenantId}/backfill {"maxRows":2000}` until `complete:true`. Resumable. Each
   batch is read under a row lock in the transaction that writes it, so a concurrent application save cannot be overwritten.
5. Verify per tenant: `POST .../verify {"maxRows":5000,"cursor":<previous cursor>}` until `complete:true` with `mismatchTotal = 0`.
   A mismatch names entity, field, row key and kind only. If any: `POST .../repair` (same body) rebuilds ciphertext and blind index
   from the authoritative plaintext, then verify again. Schedule verify and alert on any mismatch (a bypassing write is the usual cause).
6. Optional, only when the ring flag is on and the release is final: create a new DataProtection key and re-protect long-lived secrets
   with `DataProtectionSecretReprotector` (one-off job). It replaces each value only if it is unchanged since it was read; the report's
   `Conflicts` and `Unreadable` must both be 0 (`SafeToRevokeOldKeys`) before any old DataProtection key is revoked. Wait until every
   API instance has loaded the new key (24 hours, or restart them) first.
7. Restore drill (above). `field_encryption_keys` is restored together with the data tables; never restore it from a different point
   in time than the data tables. The only thing that must survive any restore is the KEK secret.

**Release 2: ReadEncrypted (DEFERRED).** Readers move to blind-index lookups; the query guard turns on.

**Release 3: EncryptedOnly + scrub (DEFERRED).** Plaintext columns are blanked, `ix_employees_dup_*` dropped. Only now does a client
revoke hide data (and the revoke endpoint starts working). Take a Neon branch first. Do not ship before the restore drill has passed.

**Release 4: contract (DEFERRED).** Drop the plaintext columns in a release after nothing reads them.

## Rollback
- Release 1 never touches plaintext, so the field-encryption part is reversible: the startup guard refuses Production/Staging with Mode
  `Off` while any `field_encryption_keys` row exists, so "just flip it off" fails on purpose. Roll back by redeploying the previous image
  with the same Mode `DualWrite` env, or decommission deliberately (owner-approved removal of key rows and clearing of `*_enc` /
  `*_bidx`, then Mode `Off`).
- **The DataProtection ring is the exception** - see its Rollback section. With the ring flag off, release 1 does not change the ring,
  so rolling back the image is safe. With it on, it is not.
- A bad backfill is repaired with `repair`, or by nulling the affected `*_enc` column and re-running backfill.
- Lost KEK: no rollback exists for ciphertext; restore the KEK from escrow. Plaintext (present until release 3) is the safety net for
  field data, not for ring-protected secrets.

## Operational notes
- Key rings are cached per instance for 60 s; idle rings are evicted and wiped after 5 minutes. A revoke or rotation on one instance
  reaches the others within a minute. Pending revokes are applied by a background sweep every few minutes.
- All custody endpoints send `Cache-Control: no-store`. Passphrases and private keys travel only in the request body over TLS and are
  never logged or audited; avoid APM request-body capture on these routes.
- Backups retain wrapped keys (see "What revoke does"). Neon branches of production carry the key table with the data.
- Cost: no crypto on reads in DualWrite; each changed protected field adds one AES-GCM and one HMAC on write.
- Watch after step 3: save latency p95, and the count of 423/503 `field_encryption_*` responses (should be zero).
- Write paths that skip the interceptor (`ExecuteUpdate`, raw SQL, bulk import) on protected tables are blocked by
  `FieldEncryptionWritePathRatchetTests`.
