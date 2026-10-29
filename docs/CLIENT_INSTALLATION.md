# Installing and running KynexOne

For the organisation's IT team and the implementation partner. KynexOne runs on the organisation's own server,
for one organisation (with as many companies as the licence allows). There is no hosted service behind it: the
organisation owns the server, the database, the documents and the backups.

The commands live in the installation kit, [`deploy/client/`](../deploy/client/README.md). This guide explains what
each step does and what to decide before running it.

## 1. What you need

| | Minimum | Recommended (500+ employees) |
|---|---|---|
| CPU | 2 cores | 4 cores |
| Memory | 8 GB | 16 GB (32 GB with a local AI model) |
| Disk | 60 GB SSD | 200 GB SSD, plus off-server backup space |
| Operating system | Linux with Docker Engine 24+ and Docker Compose v2.20+, or Windows Server with Docker | Ubuntu 24.04 LTS |

You also need:

- **An address** people will type, such as `hr.example.local` or `hr.example.com`, that resolves to the server.
- **A TLS decision**: the kit's own certificate authority (LAN), a Let's Encrypt certificate (server reachable from the
  internet), or the organisation's own certificate. See §3.
- **The licence file** your KynexOne supplier issues (`*.licence`). It can be imported after installation.
- **An SMTP relay** if KynexOne should send email (invitations, password resets, notifications).

## 2. Install

From the offline bundle (`kynexone-<version>.tar.gz`, verified with its `.sha256`):

```bash
tar xzf kynexone-1.0.0.tar.gz
cd kynexone-1.0.0/deploy/client
sudo scripts/install.sh --domain hr.example.local --licence /path/to/organisation.licence
```

Windows Server: `.\scripts\install.ps1 -Domain hr.example.local -Licence C:\path\organisation.licence`.

The script:

1. checks Docker and that ports 80 and 443 are free;
2. writes `.env` (readable only by the installing account) with generated secrets: the database password, the token
   signing key and the internal proxy secret;
3. verifies the bundle's checksums and loads the images (or pulls them from the registry when there is no bundle);
4. starts PostgreSQL and Redis and creates the database schema;
5. **creates the organisation and its first administrator**. It asks for the organisation's name, home country,
   and the administrator's name, email and password. This is the only way an organisation is ever created;
6. imports the licence, or prints the **installation id** to send to your supplier if you have no licence yet;
7. starts everything, waits until KynexOne reports ready and prints the address.

Re-running `install.sh` on an installed server is refused; `--force-reconfigure` changes the address and TLS settings
and keeps the secrets.

## 3. Certificates (TLS)

Set `CADDY_TLS` in `.env`:

| Value | Use it when |
|---|---|
| `internal` (default) | KynexOne is used on the organisation's network only. The kit runs its own certificate authority; install its root certificate on PCs and phones (`docker compose cp proxy:/data/caddy/pki/authorities/local/root.crt .`), or browsers warn. |
| an email address | The server is reachable from the internet on ports 80 and 443. A Let's Encrypt certificate is obtained and renewed automatically. |
| `/certs/cert.pem /certs/key.pem` | The organisation has its own certificate. Put both files in `deploy/client/certs/`. |

The mobile app connects over HTTPS only. With `internal`, phones must trust the kit's root certificate.

## 4. The licence

The licence states what the installation may do: how many employees, users, companies and administrators; which
modules and optional features; the AI allowance; and when it expires. It is signed by the supplier and checked every
time KynexOne loads it, so an edited file is refused.

- **Import or replace it** in **Organisation Admin → Licence**, or with
  `docker compose run --rm -v $PWD/new.licence:/tmp/l:ro backend --import-licence /tmp/l`. It takes effect within a
  minute, without a restart.
- **Status**: `scripts/status.sh` prints the installation id, the licence, its expiry and the usage against each limit.
- **Before expiry** KynexOne shows a grace banner for the licence's grace days. **After grace**, sign-in still works
  but every other screen answers "licence expired" until a renewed licence is imported. No data is lost.
- **At a limit** (for example the employee limit), the action that would exceed it is refused with a clear message;
  nothing already in place is affected.

## 5. First sign-in

Open `https://<your address>` and sign in with the administrator email and password chosen at installation. There is
no company ID: the installation holds one organisation.

Then, in this order:

1. **Organisation Admin → Licence**: check the licence and its limits.
2. **Setup**: create the companies (the create-company wizard), departments, grades and policies.
3. **User Management**: invite the HR team and other administrators.
4. **Employees**: add or import employees, then invite them to self-service (welcome codes or invitation emails).

## 6. Email

Fill in `SMTP_HOST`, `SMTP_PORT`, `SMTP_USE_SSL`, `SMTP_USERNAME`, `SMTP_PASSWORD` and `SMTP_FROM_EMAIL` in `.env`, then
run `docker compose up -d`. Without SMTP, KynexOne still works: invitations and password resets are handed over as
links or printed welcome codes instead of emailed.

## 7. Documents

By default documents are stored in the `documents` Docker volume, which `backup.sh` backs up. To use an S3-compatible
store the organisation runs, set `STORAGE_PROVIDER=s3` and the `STORAGE_*` settings; back that store up with its own
tools.

Selfie attendance additionally needs documents stored in the required jurisdiction. When the server is in that
jurisdiction, list where documents land in `STORAGE_RESIDENCY_ENDPOINT` and `STORAGE_RESIDENCY_REGION`
(`local` and `local` for the documents volume).

## 8. AI

AI is **off** by default (`AI_PROVIDER=none`); every screen works without it.

| `AI_PROVIDER` | Where employee data goes |
|---|---|
| `none` | Nowhere. AI features show their non-AI answers. |
| `ollama` | A model on the organisation's own network. `docker compose --profile ai up -d` runs one beside KynexOne. |
| `anthropic`, `openai` | To that external AI service, over the internet. Only choose this when the organisation accepts it. |

The licence also sets a monthly AI allowance; at zero, AI stays off whatever is configured.

## 9. The mobile app

Employees install **KynexOne** from the App Store or Google Play (or through the organisation's device management).
On first start the app asks which server to use:

- **Scan the QR code** shown in **Organisation Admin → Licence → Mobile app**, or
- **type the address** (`hr.example.com`).

The app accepts only HTTPS addresses and checks that the server is a set-up KynexOne installation before saving it.

Push notifications travel through Expo's service on the internet, so they are **off** by default
(`PUSH_ENABLED=false`). With them off, the app neither asks for notification permission nor registers the phone.

## 10. Attendance devices and KynexBridge

Devices that can call KynexOne directly post to `https://<your address>/api/attendance/ingest` with their device key;
the exact address is shown under **Attendance → Devices**.

Devices that cannot (most biometric terminals on a branch LAN) are polled by **KynexBridge**, a small agent installed
on that network ([`kynexbridge/README.md`](../kynexbridge/README.md)). Set `apiBaseUrl` to your KynexOne address and,
with the kit's internal CA, `caFile` to its root certificate.

## 11. Backups

```bash
scripts/backup.sh --to /mnt/backups        # Windows: .\scripts\backup.ps1 -To D:\backups
```

Run it every night (cron, Task Scheduler) and **copy the backups off the server**. Each backup holds the database,
the documents, the settings (`env.backup`, with the secrets) and a checksum manifest.

- The database includes the **data-protection keys**. Restore the database whole: without those keys, encrypted
  two-step sign-in secrets cannot be read and every user would have to set two-step sign-in up again.
- `env.backup` holds the secrets. Store backups as carefully as the server itself.

**Restore** (it replaces everything, and refuses while the application runs):

```bash
docker compose stop backend web
scripts/restore.sh backups/20261025T020000Z
```

To move to a new server: install the kit there, copy `env.backup` to `.env`, then restore.

## 12. Upgrades and rollback

```bash
scripts/upgrade.sh kynexone-1.2.0.tar.gz    # or: scripts/upgrade.sh 1.2.0
```

The upgrade verifies the bundle, refuses when the licence's support period has ended, **takes a backup**, switches
**maintenance mode** on (people see a "being updated" notice; background jobs pause), loads the new version, applies its
database changes, waits until it is ready, runs smoke checks and switches maintenance off.

If anything fails, KynexOne stays in maintenance and the script prints how to roll back:

1. **Previous version**: set `KYNEXONE_VERSION` back in `.env` and run `docker compose up -d backend web`. Database
   changes are additive, so the previous version runs on the upgraded database.
2. **Restore**: if it does not, restore the backup the upgrade took. That also returns the version.

Any older version upgrades straight to the newest; versions never move backwards except by a restore.

**Maintenance mode** can also be switched by an administrator in **Organisation Admin → Licence**, or with
`docker compose run --rm --no-deps backend --maintenance on|off`.

## 12a. Moving from the hosted KynexOne service

An organisation that used the hosted service moves its data with the supplier:

1. The supplier exports the organisation (`tools/TenantExporter`) and hands the export folder over encrypted.
2. Install with the export instead of the setup questions:
   `scripts/install.sh --domain hr.example.com --import-legacy /path/to/export` (Windows: `-ImportLegacy`).
   On an installation that has no organisation yet, the same step is
   `docker compose run --rm -v /path/to/export:/import backend --import-legacy /import`.
3. The import checks every file, loads everything in one transaction and keeps it only if payroll net per run, leave
   balances, loan balances and GL totals match the hosted figures exactly. The report is written to
   `import-report.json` in the export folder.
4. Import the licence (§4, or `--licence` above) and sign in with the existing accounts. Users who had two-step sign-in set it up again
   (its secrets cannot leave the hosted service).
5. Delete the export folder once the business has signed off the report.

## 13. Firewall

Inbound:

- **443/tcp** from the networks that use KynexOne (offices, VPN; the internet only if mobile staff connect directly).
- **80/tcp** from the same networks; it only redirects to HTTPS (open to the internet for Let's Encrypt).
- **SSH or RDP** from the administrators' network only.
- Nothing else. PostgreSQL, Redis, the API and the web server are not published.

Outbound, only for what is switched on: the image registry (installs and upgrades without a bundle), the SMTP relay,
`exp.host` (push), the AI service (cloud AI), `api.qiwa.tech` (Qiwa), bank endpoints (bank integrations), Let's Encrypt
(automatic certificates), an external S3 store.

## 14. What the organisation's administrators can and cannot change

| They can | They cannot |
|---|---|
| Companies, departments, grades, policies, users, roles and permissions | Exceed the licence's limits or switch on unlicensed modules |
| Switch optional modules on and off, within the licence | Switch off statutory modules (GOSI, Saudization) for a country that requires them |
| Import a renewed licence; switch maintenance mode | Edit the licence |
| SMTP, storage, AI and push settings (in `.env`) | Create a second organisation |

## 15. Troubleshooting

| Symptom | Look at |
|---|---|
| The address does not open | `scripts/status.sh`; DNS; the firewall; `docker compose logs proxy` |
| Browser certificate warning | §3: install the kit's root certificate, or use Let's Encrypt or your own certificate |
| "Licence missing" or "licence expired" on every screen | **Organisation Admin → Licence**; `scripts/status.sh` |
| "Being updated" notice that does not go away | An upgrade or restore stopped half way; read its output, or switch maintenance off (§12) |
| Emails do not arrive | `SMTP_*` in `.env`; `docker compose logs backend` |
| Mobile app refuses the address | It must be HTTPS and reachable from the phone; with `internal` TLS the phone must trust the root certificate |
| Anything else | `scripts/status.sh` and `docker compose logs --tail=300 backend`, sent to your KynexOne supplier |
