# KynexOne — client installation kit

Everything needed to run KynexOne on one server for one organisation, behind one HTTPS address.

```
 browsers · mobile app · attendance devices · KynexBridge
                     │ HTTPS 443  (80 redirects / issues certificates)
                ┌────▼────┐
                │  proxy  │  Caddy: TLS, compression, security headers
                └─┬─────┬─┘
        /api/*    │     │   everything else
         ┌────────▼┐   ┌▼──────┐
         │ backend │   │  web  │      ASP.NET Core 8 API · Next.js
         └┬───┬───┬┘   └───────┘
   ┌──────▼─┐┌▼────┐┌▼─────────┐
   │postgres││redis││documents │     database · cache · document volume
   └────────┘└─────┘└──────────┘
   migrate (one-shot: applies migrations before backend starts)
   ollama (optional local AI, `--profile ai`)
```

Only the proxy publishes ports. Every other service is reachable on the internal Docker network only.

## Requirements

| | Minimum | Recommended (500+ employees) |
|---|---|---|
| CPU | 2 cores | 4 cores |
| Memory | 8 GB | 16 GB (32 GB with the `ai` profile) |
| Disk | 60 GB SSD | 200 GB SSD, plus off-server backup space |
| OS | Linux with Docker Engine 24+ and the Compose plugin v2.20+, or Windows Server with Docker | Ubuntu 24.04 LTS |

A DNS name that resolves to the server (for example `hr.example.local`) and a TLS choice (below).

## Install

From an extracted offline bundle (`tar xzf kynexone-<version>.tar.gz`):

```bash
cd kynexone-<version>/deploy/client
sudo scripts/install.sh --domain hr.example.local --licence /path/to/organisation.licence
```

Windows Server: `.\scripts\install.ps1 -Domain hr.example.local -Licence C:\path\organisation.licence`

The script verifies the bundle's checksums, writes `.env` (mode 600) with generated secrets, loads the images,
creates the database, asks for the organisation's name, country and first administrator
(`--setup-organisation`), imports the licence and starts everything. It refuses to run over an existing
installation; `--force-reconfigure` changes the address and TLS settings and keeps the secrets.

**No licence yet?** The script prints the **installation id**. Send it to your KynexOne supplier and upload the
licence they return in **Tenant Admin → Licence**. Until then the product answers `402 licence_missing`.

Without a bundle the script pulls the images for `KYNEXONE_VERSION` from `KYNEXONE_REGISTRY`.

## TLS (`CADDY_TLS` in `.env`)

| Value | When |
|---|---|
| `internal` (default) | LAN installation. Caddy runs its own certificate authority. Install its root certificate (`docker compose cp proxy:/data/caddy/pki/authorities/local/root.crt .`) on client PCs and phones, or browsers warn. |
| an email address | The server is reachable from the internet on ports 80 and 443: Caddy obtains a Let's Encrypt certificate. |
| `/certs/cert.pem /certs/key.pem` | The organisation's own certificate: put both files in `certs/`. |

## Settings you will want to change

| Setting (`.env`) | Default | Notes |
|---|---|---|
| `SMTP_*` | empty | Without SMTP, invitations and password resets cannot be emailed. |
| `PUSH_ENABLED` | `false` | `true` sends mobile notifications through Expo (`exp.host`) on the internet. |
| `AI_PROVIDER` | `none` | `ollama` with `docker compose --profile ai up -d` keeps AI on this server. `anthropic`/`openai` send employee data to that service. |
| `STORAGE_*` | `local` | The `documents` volume. `s3` uses an S3-compatible store the organisation runs. |

After changing `.env`: `docker compose up -d`.

## Day to day

| Task | Linux | Windows |
|---|---|---|
| Health, version, licence | `scripts/status.sh` | `.\scripts\status.ps1` |
| Back up | `scripts/backup.sh [--to DIR]` | `.\scripts\backup.ps1 [-To DIR]` |
| Restore | `docker compose stop backend web` then `scripts/restore.sh backups/<stamp>` | same with `.\scripts\restore.ps1` |
| Upgrade | `scripts/upgrade.sh kynexone-1.2.0.tar.gz` (or `1.2.0`) | `.\scripts\upgrade.ps1 ...` |
| Logs | `docker compose logs -f backend` | same |
| Replace the licence | Tenant Admin → Licence | same |
| Maintenance mode | Tenant Admin, or `docker compose run --rm --no-deps backend --maintenance on\|off` | same |

### Backups

Back up every night and copy the folders off the server, for example with cron:
`0 2 * * * /opt/kynexone/deploy/client/scripts/backup.sh --to /mnt/backups`

A backup holds `database.dump`, `documents.tar.gz`, `env.backup` and a `manifest.txt` of checksums.

- **The database includes the data-protection keys** (the `DataProtectionKeys` table). Losing them makes the
  encrypted MFA secrets unreadable: every user would have to enrol MFA again. Restore the database, never only
  part of it.
- `env.backup` holds the secrets. Store backups as carefully as the server.
- To move to a new server: install the kit, copy `env.backup` to `.env`, then restore.

### Upgrades and rollback

`upgrade.sh` verifies the bundle, refuses when the licence's support period has ended, takes a backup, switches
maintenance mode on (people see a notice; background jobs pause), loads the new images, applies the new
migrations, starts the new version, waits for `/health/ready`, runs the smoke checks and switches maintenance off.

If anything fails it stays in maintenance and prints the rollback:

1. **Previous images** — set `KYNEXONE_VERSION` back and `docker compose up -d backend web`. Migrations are
   additive (nothing is dropped or renamed in the release that stops using it), so the previous version runs on
   the upgraded database.
2. **Restore** — when it does not, `restore.sh` the backup the upgrade took. It also returns
   `KYNEXONE_VERSION` to the backup's version.

Versions only move forward, and any older version can upgrade straight to the newest: migrations are cumulative.

## Firewall checklist

Inbound to the server:

- [ ] **443/tcp** from the networks that use KynexOne (offices, VPN; the internet only if mobile staff connect
      directly).
- [ ] **80/tcp** from the same networks; it only redirects to HTTPS. With Let's Encrypt it must be open to the
      internet.
- [ ] **22/tcp (SSH) or 3389/tcp (RDP)** from the administrators' network only.
- [ ] Nothing else. PostgreSQL, Redis, the API and the web server are **not** published; do not add
      `ports:` for them.

Outbound from the server, only for what is switched on:

- [ ] The image registry, for install and upgrade without a bundle.
- [ ] The SMTP relay (`SMTP_HOST:SMTP_PORT`).
- [ ] `exp.host` on 443, when `PUSH_ENABLED=true`.
- [ ] `api.anthropic.com` / `api.openai.com` on 443, when `AI_PROVIDER` is a cloud provider.
- [ ] `api.qiwa.tech` on 443, when the Qiwa integration is configured.
- [ ] The bank endpoints, when bank integrations are configured.
- [ ] `acme-v02.api.letsencrypt.org` on 443, when `CADDY_TLS` is an email address.
- [ ] The S3 endpoint, when `STORAGE_PROVIDER=s3`.

Attendance devices and KynexBridge post to `https://<KYNEXONE_DOMAIN>/api/attendance/ingest`; the exact address
is shown on the Attendance page.

## Files

| File | What |
|---|---|
| `docker-compose.yml` | The services. Do not edit; change `.env`. |
| `.env.example` | Every setting with what it does. `install` copies it to `.env`. |
| `Caddyfile` | The reverse proxy. |
| `scripts/` | install, upgrade, backup, restore, status (`.sh` and `.ps1`). |
| `certs/` | The organisation's own certificate, when used. |
