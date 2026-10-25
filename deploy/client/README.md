# KynexOne — client installation kit

Everything needed to run KynexOne on one server for one organisation: four containers behind one HTTPS
address.

```
 browsers, mobile app, attendance devices, KynexBridge
                │  HTTPS 443 (80 only redirects / issues certificates)
           ┌────▼────┐
           │  caddy  │  TLS, compression, security headers
           └─┬─────┬─┘
     /api/*  │     │  everything else
       ┌─────▼┐   ┌▼─────┐
       │ api  │   │ web  │   ASP.NET Core 8 API · Next.js frontend
       └──┬───┘   └──────┘
   ┌──────▼─────┐  ┌───────────┐
   │  postgres  │  │ documents │  (Docker volumes, backed up by backup.sh)
   └────────────┘  └───────────┘
```

Only caddy publishes ports. PostgreSQL, the API and the web server are reachable on the internal Docker network
only.

## Requirements

| | Minimum | Recommended (500+ employees) |
|---|---|---|
| CPU | 2 cores | 4 cores |
| Memory | 6 GB | 8–16 GB |
| Disk | 40 GB SSD | 100 GB SSD + off-server backup space |
| OS | Linux with Docker Engine 24+ and the Compose plugin, or Windows Server with Docker | Ubuntu 22.04/24.04 LTS |

A DNS name that resolves to the server (for example `hr.example.local`), and a TLS choice (below).

## Install

```bash
cd deploy/client
scripts/install.sh --domain hr.example.local --licence /path/to/organisation.licence
```

Windows: `.\scripts\install.ps1 -Domain hr.example.local -Licence C:\path\organisation.licence`

The script writes `.env` with fresh secrets, pulls the images, creates the database, asks for the organisation's
details and its first administrator (`--setup-organisation`), imports the licence and starts everything. Without
`--licence`, upload it later in **Tenant Admin → Licence**; until then the API answers `402 licence_missing`.

**Offline server:** copy `kynexone-<version>.tar` (both images) next to the kit and add
`--images kynexone-<version>.tar`. Set `PUSH_ENABLED=false` (mobile push needs Expo's service on the internet) and
leave `AI_PROVIDER` empty or `ollama`.

## TLS (`CADDY_TLS` in `.env`)

| Value | When |
|---|---|
| `internal` (default) | LAN installation. Caddy runs its own certificate authority; install its root certificate (`docker compose cp caddy:/data/caddy/pki/authorities/local/root.crt .`) on client PCs and phones, or browsers warn. |
| an email address | The server is reachable from the internet on ports 80 and 443: Caddy obtains a Let's Encrypt certificate. |
| `/certs/cert.pem /certs/key.pem` | The organisation's own certificate: put both files in `certs/`. |

## Day to day

| Task | Linux | Windows |
|---|---|---|
| Health, version, licence | `scripts/status.sh` | `.\scripts\status.ps1` |
| Back up | `scripts/backup.sh [--to DIR]` | `.\scripts\backup.ps1 [-To DIR]` |
| Restore | `scripts/restore.sh backups/<stamp>` | `.\scripts\restore.ps1 backups\<stamp>` |
| Upgrade | `scripts/upgrade.sh 1.2.0 [--images FILE]` | `.\scripts\upgrade.ps1 1.2.0 [-Images FILE]` |
| Logs | `docker compose logs -f api` | same |
| Replace the licence | Tenant Admin → Licence, or `docker compose run --rm -v $PWD/new.licence:/tmp/l:ro api --import-licence /tmp/l` | same |

**Back up every night** (cron: `0 2 * * * /opt/kynexone/deploy/client/scripts/backup.sh --to /mnt/backups`) and
copy the folders off the server. A backup holds `env.backup` with the secrets: store it as carefully as the
server.

**Upgrades** take a backup first, switch maintenance mode on (people see a notice, not errors), apply the new
version's database changes and switch maintenance off once the new version reports ready. If anything fails the
script prints the one `restore` command that returns to the previous version and data. Versions only move
forward; going back is a restore.

**Maintenance mode** can also be switched by an administrator in the product, or with
`docker compose run --rm --no-deps api --maintenance on|off [--message "..."]`.

## Firewall checklist

Inbound to the server:

- [ ] **443/tcp** from the networks that use KynexOne (offices, VPN; the internet only if mobile staff connect
      directly).
- [ ] **80/tcp** from the same networks. Caddy redirects it to HTTPS; with Let's Encrypt it must be open to the
      internet.
- [ ] **22/tcp (SSH) / 3389 (RDP)** from the administrators' network only.
- [ ] Nothing else. PostgreSQL (5432), the API (5117) and the web server (5173) are **not** published; do not
      add `ports:` for them.

Outbound from the server (allow only what the installation uses):

- [ ] The image registry, for `install`/`upgrade` (not needed with `--images`).
- [ ] The SMTP relay (`SMTP_HOST:SMTP_PORT`).
- [ ] `exp.host` and `exp.dev` on 443, when `PUSH_ENABLED=true`.
- [ ] The AI endpoint, when `AI_PROVIDER` is `anthropic`/`openai` (sends employee data off-site) or a remote
      `ollama`.
- [ ] The S3 endpoint, when `STORAGE_PROVIDER=s3`.
- [ ] Let's Encrypt (`acme-v02.api.letsencrypt.org`), when `CADDY_TLS` is an email address.

Attendance devices and KynexBridge post to `https://<KYNEXONE_DOMAIN>/api/attendance/ingest`; the exact
address is shown on the Attendance page.

## Files

| File | What |
|---|---|
| `docker-compose.yml` | The four services. Do not edit; change `.env`. |
| `.env.example` | Every setting, with what it does. `install` copies it to `.env`. |
| `Caddyfile` | The reverse proxy. |
| `scripts/` | install, upgrade, backup, restore, status (`.sh` and `.ps1`). |
| `certs/` | The organisation's own certificate, when used. |
