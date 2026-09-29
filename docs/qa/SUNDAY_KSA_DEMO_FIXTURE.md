# Sunday KSA demo fixture

`KX-SUN-KSA-20260920-v1` is an isolated, deterministic test fixture for the 20 September 2026 client-demo gate. It must only be loaded into a disposable non-client database. Normal web startup never runs it.

## Safety contract

The one-off command refuses to run when any of these conditions applies:

- `ASPNETCORE_ENVIRONMENT=Production`
- `DEDICATED_DEPLOYMENT=true`
- `CLIENT_DEPLOYMENT=true`
- `SUNDAY_DEMO_FIXTURE_CONFIRMATION` is not exactly `CREATE-KX-SUN-KSA-20260920-v1`
- `SUNDAY_DEMO_FIXTURE_DISPOSABLE` is not exactly `true` (case-insensitive)
- `SUNDAY_DEMO_FIXTURE_PASSWORD` is shorter than 12 characters

The user-supplied password is hashed and never logged. Use a preview-only secret; do not reuse a production or personal credential.

## Run

Apply migrations to the disposable database first, then execute:

```bash
ASPNETCORE_ENVIRONMENT=Staging \
DEDICATED_DEPLOYMENT=false \
CLIENT_DEPLOYMENT=false \
SUNDAY_DEMO_FIXTURE_CONFIRMATION=CREATE-KX-SUN-KSA-20260920-v1 \
SUNDAY_DEMO_FIXTURE_DISPOSABLE=true \
SUNDAY_DEMO_FIXTURE_PASSWORD='<preview-secret-at-least-12-characters>' \
ConnectionStrings__Default='<disposable-neon-branch-connection>' \
dotnet Zayra.Api.dll --seed-sunday-demo-fixture
```

The operation uses a serializable transaction and PostgreSQL transaction-scoped advisory lock. A first run creates tenant slug `kx-sun-ksa-20260920-v1`. A repeat run validates the canonical fingerprint and exits without writes. If an existing tenant has drifted—even when row counts remain the same—the command fails closed and performs no repair.

## Canonical control totals

| Domain | Expected |
|---|---:|
| Employees | 30 |
| Branches | 2 |
| Departments | 5 |
| Login personas | 10 (Admin plus 9 role-linked users) |
| Attendance daily cases | 8 |
| Leave requests | 4 states |
| Payroll population | 3 |
| Gross | SAR 42,780.00 |
| Employee deductions | SAR 3,142.92 |
| Net | SAR 39,637.08 |
| Employer statutory | SAR 3,666.25 |

Attendance covers normal, late, missing punch, overnight, weekend, approved leave, absent, and duplicate source-punch cases. Leave covers Submitted, Approved, Rejected, and Cancelled. The fixture also includes one approved overtime request, one active employee loan, and one approved bonus.

## Login personas

All personas use the one preview-only password supplied in `SUNDAY_DEMO_FIXTURE_PASSWORD`.

| Email | Role |
|---|---|
| `admin@kx-sunday.demo` | Admin |
| `hr.director@kx-sunday.demo` | HR Director |
| `hr.manager@kx-sunday.demo` | HR Manager |
| `payroll.manager@kx-sunday.demo` | Payroll Manager |
| `payroll.officer@kx-sunday.demo` | Payroll Officer |
| `finance.approver@kx-sunday.demo` | Finance Approver |
| `compliance@kx-sunday.demo` | Compliance Officer |
| `manager@kx-sunday.demo` | Manager |
| `auditor@kx-sunday.demo` | Auditor |
| `employee@kx-sunday.demo` | Employee |

## Disposal

Delete the isolated Neon branch after the demo evidence is retained. Never point this command at a shared staging, dedicated, client, or production database.
