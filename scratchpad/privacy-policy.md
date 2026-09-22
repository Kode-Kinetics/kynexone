# Privacy policy rewrite — every sentence true of the shipped product

Branch `docs/privacy-policy-truthful`, base `develop` @ `da59275`. Files changed:
`frontend/app/privacy/page.tsx` (rewritten) and `frontend/e2e/ui-truthfulness-state.spec.ts` (11 new
assertions inside the existing *"public privacy and security claims stay evidence-bound"* test).
**No product behaviour changed. No backend file touched.**

The governing rule is the one `ApprovalPoliciesController.cs:14-16` states for configuration: *"accepting a
write here would recreate exactly the silent misconfiguration F1 removes — configuration that is stored but
never applied."* A published promise that no code enforces is the same defect in prose. The old page had
at least **19** such sentences. The honest page is longer, not shorter, because the real system holds more
sensitive data and sends it to more places than the old page admitted. It is less impressive.

Paths are relative to the repo root. `B/` = `backend-dotnet/Zayra.Api/`.

---

## 1. Evidence that is NOT in the repository (read this first)

Some statements depend on **live Render / provider configuration** that the repo cannot show. `render.yaml`
has already been shown to differ from the live service (`autoDeploy` — see §6). I did **not** read the
live environment: no `RENDER_API_KEY` in this shell, and I did not go around that. Before publishing,
someone with dashboard access must confirm each row below. If any value differs, the page is wrong.

| Page statement | Depends on | Repo evidence | Must confirm in |
|---|---|---|---|
| Model is **gpt-oss:120b** on Ollama Cloud | `AI_PROVIDER=ollama`, `OLLAMA_BASE_URL=https://ollama.com`, `AI_MODEL` | `render.yaml:170-179` (provider + URL in the blueprint; `AI_MODEL` is `sync: false`). `docker-compose.yml:39` defaults to `gpt-oss:120b-cloud`. The model name comes from the brief, not the repo. | Render dashboard `AI_MODEL` |
| Documents in **Backblaze B2, US region** | `Storage__Endpoint` / `Storage__Region` | `README.md:11` names B2. The region `us-east-005` appears **nowhere in the repo**. It comes from the operator record (`s3.us-east-005.backblazeb2.com`, from the live Render/B2 APIs) and `scratchpad/data-retention.md:382-383`. | Render dashboard + B2 console |
| Database / app server **outside the Kingdom** | Neon project region, Render service region | Neither region is in the repo (`render.yaml` has no `region:` key). Neither Render nor Neon offers a Saudi region, so "outside the Kingdom" is safe. Insert the exact regions if counsel wants them. | Neon + Render dashboards |
| Nothing auto-deleted (retention sweep off) | `DataRetention__ScheduleEnabled/ApplyDeletions/AllowTenantErasure` | Defaults `false`: `B/Infrastructure/Retention/DataRetentionOptions.cs:25,32,39`. `render.yaml` sets **no** `DataRetention__*` key. | Render dashboard — confirm absent |
| No data sent to Qiwa | `QIWA_USE_LIVE_ADAPTER` | Sandbox unless set: `B/Program.cs:482-485`. Not in `render.yaml`. | Render dashboard — confirm absent |
| AI log keeps "a shortened copy with some values masked" | `AI_LOG_PROMPTS` (default false) | `B/Infrastructure/AI/AiOptions.cs:35`, `AiAuditService.cs:24,51` | Render dashboard — confirm absent/false |
| Uploaded files "encrypted depending on bucket config" | B2 bucket default-encryption setting | App sends no SSE header: `B/Infrastructure/Documents/S3DocumentStorage.cs:18-27` | B2 console |

---

## 2. Claim-by-claim: old text → new text → code that justifies it

### §1 Who we are
| Old | New | Evidence |
|---|---|---|
| "Kode Kinetics acts as a data processor on their behalf" (§5) | "Your employer … decides what data is entered … We run the platform on that organisation's behalf." | **Legal position** (§5, L1), not a technical fact. |
| — | Lede: "describes what the platform does … including where what it does is limited. It does not replace the data-processing terms…" | Framing. L2. |

### §2 What the platform holds
| Old | New | Evidence |
|---|---|---|
| "Full name, work email, job title, employee ID, profile photo" | + English/Arabic/preferred name, personal email, phone | `B/Models/Employee.cs:36-50` |
| *(absent)* | **Personal details**: gender, DOB, marital status, nationality, Saudi/non-Saudi | `Employee.cs:51-53,56,127` |
| "ID copies, visas" (documents only) | **Government identifiers as structured fields**: Iqama, passport, visa, national ID, residency, work permit, labour card + dates; GOSI/Qiwa refs; sponsor | `Employee.cs:94-151` |
| "salary band … disciplinary notes" | **Exact** salary, allowances, deductions; disciplinary records; termination reason | `Employee.cs:87,157,158`; export headers `B/Controllers/EmployeesController.cs:232-241` |
| "Bank account details (access-controlled and **masked** in user-facing responses)" | Bank name, IBAN, account details. The masking claim moved to §7 and restated as permission-gating. | `Employee.cs:88-90`; `B/Models/EmployeePayrollProfile.cs:9-22`. Blank for unauthorised callers, **full** for authorised ones: `B/Application/Employees/EmployeeSensitiveMask.cs:12-23`, `EmployeeManagementDtos.cs:313-320`; own IBAN in full: `EmployeeManagementDtos.cs:401-404,456`. |
| "tax identification numbers" | **removed** | No employee-level tax ID. Only `Company.TaxNumber`: `B/Models/Company.cs:21`. |
| *(absent)* | **Health**: free-text medical information | `Employee.cs:156` |
| *(absent)* | **Other people**: emergency contact; dependants' name, relationship, **national ID, DOB**, visa expiry | `Employee.cs:54-55`; `B/Models/EmployeeDependent.cs:9-13` |
| "biometric device identifiers (**hashed**), geolocation (**where permitted**), device IP" | Punch times, device/method, IP; mobile punches **can include** lat/long; device photo reference and raw payload | `B/Models/AttendanceModule.cs:71-92` (`Latitude/Longitude:82-83`, `IpAddress:84`, `PhotoReference:85`, `RawPayloadJson:86`). Mobile sends location: `mobile/src/api/services.ts:748-749,762-763`. **No biometric data is collected.** The only hashed value is the device **API key**: `B/Infrastructure/Attendance/AttendanceService.cs:436-444`. |
| "Documents: ID copies, visas, certificates, expiry dates" | kept | `B/Models/EmployeeDocument.cs:12-29` |
| "Messages … in-platform **support** and HR request workflows" | HR requests and comments; AI questions and answers | `B/Models/EmployeeSelfService.cs:105-148,229`. There is no vendor support channel, so "support" is removed. |
| *(absent)* | **Recruitment** candidates | `B/Models/Recruitment.cs`; `B/Controllers/Recruitment/RecruitmentAiController.cs:47-53` |
| "Password hash, session tokens, MFA credentials, login timestamps and IP addresses" | + every sign-in attempt incl. **failed**, email entered, IP, **user-agent**; IP/UA on audited actions; mobile device ID + push token | `B/Models/SaasPlatform.cs:388-400` (`LoginActivity`), written `B/Infrastructure/Auth/AuthService.cs:53-62,160-168`; `B/Domain/Entities/AuditLog.cs:14-15`; `B/Models/EmployeeSelfService.cs:262-272` |
| "Usage & Technical: browser type, OS, **pages visited, feature usage, API call logs, error reports**" | **removed**, replaced by: "does not record which pages you visit or which features you use, and contains no analytics" | No analytics SDK in `frontend/package.json`. No page-view or usage model. No request-logging middleware in the pipeline `B/Program.cs:705-766`. The UA string is covered under sign-in. |

### §3 Use
| Old | New | Evidence |
|---|---|---|
| Payroll, attendance, leave, RBAC, notifications, legal compliance | kept, made concrete (payslips, WPS files, GOSI, EOSB) | `B/Infrastructure/Payroll/WpsConformance.cs`, `SifFileGenerator.cs`; security page GOSI/EOSB claims |
| *(absent)* | Nitaqat from nationality | `B/Infrastructure/Compliance/NitaqatCalculationService.cs` |
| *(absent)* | AI answers and suggestions (§5) | see §5 |
| "Detect and prevent fraud, abuse and security incidents" | "record sign-in attempts and detect reuse of sign-in tokens" | `AuthService.cs:53-62,160-168`; refresh-token family reuse detection (`RevokeRefreshTokenFamilyForReuseAsync`, `RefreshToken.FamilyId`) |
| "Improve our services through aggregated, anonymised analytics" | **removed** | No analytics pipeline exists (see §2) |
| "We do not sell personal data" | kept, plus "do not use it for advertising" | **Business statement.** Supported by the absence of any outbound flow other than those in §4, but no code can prove a negative about contracts. L3. |
| §4 table: contract / legal obligation / legitimate interests / **consent (geolocation where explicit consent is collected)** | "Your organisation determines the legal basis … this page does not state one on its behalf." | No consent is recorded anywhere: `grep -i consent` in `B/` finds only notification-channel wording (`NotificationsController.cs:166-170`). Mobile employee-dashboard punches **refuse** without location: `mobile/src/features/dashboard/EmployeeDashboard.tsx:70-91`. **Legal.** L4. |

### §4 Sharing / sub-processors
| Old | New | Evidence |
|---|---|---|
| "vetted sub-processors — cloud infrastructure, email delivery, and **monitoring tools** bound by DPAs. List available on request." | Six **named** providers, each with what it receives and where; then two conditional ones | below |
| — | **Render** — app server | `render.yaml:2-12` |
| — | **Neon** — database | `README.md:10`, `render.yaml:100` |
| — | **Backblaze B2** — documents, profile photos, payslip logos; US | Upload sites `B/Controllers/EmployeesController.cs:2142`, `B/Infrastructure/Employees/EmployeeManagementService.cs:599`, `B/Controllers/EmployeeSelfServiceController.cs:688,766`, `B/Controllers/PayslipTemplatesController.cs:227`; region per §1 |
| — | **Vercel** — serves the web app **and relays every API request** | Browser uses relative URLs: `frontend/src/api/client.ts:6-7`; Next rewrite `/api/:path*` → backend: `frontend/next.config.ts:17-22` |
| — | **Ollama Cloud** — AI | `B/Infrastructure/AI/LlmClient.cs:115-128`; `render.yaml:170-175` |
| — | **Google Fonts** — typefaces, IP on each load | `frontend/app/layout.tsx:55-68` |
| "email delivery" (implying our vendor) | "only through an SMTP server **your organisation** configures; we do not operate a separate email service" | Only transport is MailKit SMTP, from per-tenant settings: `B/Infrastructure/Email/SmtpEmailService.cs:52-68,86-120`. No SMTP host → nothing sent: `:107-108`. No SendGrid/SES/etc. anywhere. Email content: reset `AuthService.cs:419-426`, payslip-ready `B/Controllers/PayrollController.cs:3711-3712`, scheduled reports `B/Infrastructure/Reports/ReportScheduleWorker.cs:271-294`. |
| "monitoring tools" | **removed** | No monitoring, APM or error-reporting SDK in the frontend, backend (`B/Zayra.Api.csproj`) or mobile |
| — | **Expo push**, only if enabled | Inert unless `Notifications/Push.Provider="expo"`: `B/Infrastructure/Notifications/ExpoPushProvider.cs:46,76-80` |
| — | "does not send data to Qiwa, GOSI, Mudad or any bank" | Qiwa sandbox by default: `B/Program.cs:482-485`, `SandboxQiwaApiAdapter.cs`. GOSI is calculation only; WPS is file generation only. §1 dashboard check applies. |
| "Business transfers … subject to equivalent privacy protections" | "may pass to the new owner" (the unbacked "equivalent protections" promise is gone) | **Legal.** L5. |
| "As required by law" | kept | **Legal.** |
| "A current list of sub-processors is available on request" | **removed** (the list is now on the page) | — |

### §5 AI features (new section — the old page did not mention AI at all)
| New statement | Evidence |
|---|---|
| Sends employee data to **gpt-oss:120b** on Ollama Cloud; requests leave our infrastructure | `LlmClient.cs:115-128`; `render.yaml:170-175`; model name per §1 |
| **HR assistant**: question verbatim + headcount, leave balances, approval counts, attendance/lateness counts, overtime; for a named employee, full name, code, dept, title, grade, location, joining date, type, status, **last 4 of Iqama/passport**, review-cycle status | `B/Infrastructure/AI/AiPromptBuilder.cs:51-66`; context built `B/Infrastructure/AI/AiAdvisoryService.cs:296-465`, employee block `:373-394`, mask `B/Application/Common/SensitiveValueMask.cs:18-23`, feedback `:451-461`; endpoint `B/Controllers/AIAssistantController.cs:42-47` (`ai.query`) |
| **Policy questions**: document passages **as written** + file name | `B/Infrastructure/AI/PolicyDocumentService.cs:124-136`; endpoint `B/Controllers/PolicyDocumentController.cs:52` |
| **Candidate screening**: opening + each candidate's name, title, experience, education, skills → suggestion | `B/Infrastructure/Recruitment/RecruitmentAiService.cs:89-94`; `B/Controllers/Recruitment/RecruitmentAiController.cs:34-56` |
| **Roster planning**: name, **gender**, department | `B/Infrastructure/Shifts/RosterPlannerService.cs:97-99`; `B/Controllers/ShiftsController.cs:324` |
| **Setup assistant**: company profile only | `B/Infrastructure/Setup/SetupAssistantService.cs:108-112` |
| "Apart from shortening Iqama and passport numbers … the platform **does not remove or disguise** personal data before sending it" | `AiPromptBuilder.cs:19-35`: `AiRedactionService` output feeds only `PromptForLogging`. The prompts sent are unredacted unless the token budget truncates them (`AiTokenBudgetService.cs:23-33`). Recruitment, roster and policy prompts never touch the redactor. |
| Suggestions don't change a candidate's status or save a roster without a user | Screen returns `Ok(await _ai.ScreenAsync(...))` with `AsNoTracking` reads and no write: `RecruitmentAiController.cs:34-56`. Roster AI plan and commit are separate endpoints: `ShiftsController.cs:324,377`. |
| Only roles given access; an employee can't exclude their own data | `ai.query` permission `AIAssistantController.cs:47`; role gates `RecruitmentAiController.cs:12`, `ShiftsController.cs:48`, `PolicyDocumentController.cs:26,64`. No per-subject opt-out exists anywhere. |
| HR-assistant log: who, when, masked shortened prompt, **full answer**; not auto-deleted | `B/Infrastructure/AI/AiAuditService.cs:22-68`; `B/Models/AIIntelligence.cs:66-92`; no retention rule touches it (`B/Infrastructure/Retention/Rules/` has 3 rules, none for AI) |
| "Our requests carry no instruction about retention or training" | Ollama body is `model`, `stream`, `messages`, `options.num_predict` only: `LlmClient.cs:130-142` |
| "How the provider handles them is governed by its terms, not by this page" | **Legal.** L6. |

### §6 Residency
| Old | New | Evidence |
|---|---|---|
| "Data residency depends on the regions selected … does not promise self-service region selection" | "**None of the platform's data is stored in the Kingdom of Saudi Arabia.** DB and app server outside the Kingdom; documents and photos in **Backblaze B2 in a United States region**; AI via Ollama; no setting keeps data in a country." | §1 table. No region pinning in code: `B/Infrastructure/Documents/StorageOptions.cs:11` defaults `"auto"`, and `DocumentStorageRegistration.cs:39-43` does not validate the region. `render.yaml:191-195` calls residency a runbook item, not a control. |
| "…must be confirmed in the applicable customer agreement **or data-processing addendum** before reliance" | kept (the required phrase is pinned by the e2e test) | **Legal.** L7. |

### §7 Security
| Old | New | Evidence |
|---|---|---|
| "Provider-managed encryption at rest for hosted database **and durable object storage**" | DB: "whatever encryption the database provider applies". Files: "The application does not encrypt uploaded files or ask the document store to; depends on bucket config" | No SSE header or client-side encryption: `S3DocumentStorage.cs:18-27`; grep for `ServerSideEncryption` → 0 hits. B2 encryption is per-bucket, not guaranteed (§1). |
| "Application-layer protection for selected secrets, including MFA and configured integration credentials" | "encrypts MFA secrets, Qiwa credentials, SMS/WhatsApp/push provider credentials; **keys held in the same database**" + new: "**The outgoing mail-server (SMTP) password is stored unencrypted**" | MFA `B/Infrastructure/Auth/TotpService.cs:33`; Qiwa `QiwaIntegrationService.cs:259`; notification secrets only when `Category=="Notifications"`: `B/Controllers/Admin/SetupSettingsController.cs:108-113`, key list `NotificationProviderConfig.cs:41-51`. **SMTP** lives under `Category="Email"`, is written raw and read raw (`SmtpEmailService.cs:92,115`), and is only masked on display (`SetupSettingsController.cs:72-79,91`). Key ring in the app DB: `B/Program.cs:471-477`. |
| *(absent)* | Passwords: salted PBKDF2-SHA256 | `B/Infrastructure/Auth/Pbkdf2PasswordHasher.cs:8-17` (100,000 iterations; iteration count kept out of the page so it cannot go stale) |
| "HTTPS … and a **TLS-required** API-to-database connection" | HTTPS + HSTS header. DB: "configured to use TLS, but the application does not check that it does, and the documented settings do not verify the server's certificate" | HSTS `B/Infrastructure/Http/SecurityHeaders.cs:22-23`; `Program.cs:262-266` checks only presence; documented format `render.yaml:101` = `SSL Mode=Require;Trust Server Certificate=true` |
| "RBAC with principle of least privilege" | "permissions attached to their role; requests without a valid sign-in are refused unless explicitly public". "Least privilege" is **dropped** because it describes intent, and a role's actual grants are tenant-configured. | Fallback policy `Program.cs:334-341`; `HasPermissionAttribute`, `Program.cs:348-351` |
| *(absent)* | Sensitive fields withheld without permission, **full** with it, and full for the employee's own | see §2 bank row; `EmployeesController.cs:4005` |
| *(absent)* | Tenant isolation is an application filter, not separate DBs or RLS | `B/Data/ZayraDbContext.cs:4185-4280` (the security page says the same) |
| *(absent)* | Files never served from public links; tenant-checked downloads | No presign call anywhere (`StorageOptions.SignedUrlExpiryMinutes` is dead config); `S3DocumentStorage.cs:60,89-94`; downloads `EmployeesController.cs:2190`, `EmployeeSelfServiceController.cs:724,790` |
| "Audit records for designated payroll, leave, attendance, overtime, performance, and administrative workflows" | "The **central** audit log and the **payroll** audit log cannot be changed or deleted through the application, are hash-chained; payroll also has a DB rule" | Append-only covers only `AuditLog` and `PayrollAuditLog`: `ZayraDbContext.cs:448-465`. Hash chain `:245-262,357-435`. Trigger `B/Migrations/20260803201058_AddPayrollAuditHashChain.cs:56-83`. **The other 12 audit tables have no immutability guard, so the claim was narrowed.** |
| "Automated dependency, secret, static-analysis, and regression checks in the repository CI workflows" | kept | `.github/workflows/codeql.yml`, `ci.yml`, `.gitleaks.toml` |
| *(absent)* | "sign-in tokens are kept in the browser's local storage, where scripts running on the page can read them" | `frontend/src/contexts/AuthContext.tsx:72-73,83-84`; `frontend/src/api/client.ts:104-105` |
| "Independent penetration-test scope and cadence are confirmed separately for each customer engagement" | "This page makes no claim that an independent penetration test of the platform has been carried out." | No pentest artefact exists. The only "pentest" is a self-run internal review, `docs/MULTITENANCY_ISOLATION_AUDIT.md:3`. The old sentence implied tests happen. |
| "KynexOne does not claim ISO 27001 or SOC 2 Type II certification. Framework references describe a control-improvement direction…" | first sentence kept verbatim (pinned by the test); the "direction" sentence removed as roadmap language | — |
| "…contact us **immediately** at security@" | "To report a security problem, email security@" | — |

### §8 Retention (every row of the old table was false or unenforced)
| Old | New | Evidence |
|---|---|---|
| "Active employee records: employment + period required by labour law (typically 5–7 years)" | "Current employees: kept for as long as your organisation uses the platform" | Nothing implements a post-employment window |
| **"Deleted accounts: Anonymised within 90 days of account closure, except where legal hold applies."** | Split in two. **Deleted employee records**: "moved out of the active lists and kept in full, marked to be kept for seven years … **Nothing anonymises or deletes it at the end of that period, or at any other time.**" **Closed organisation accounts**: "users deactivated and signed out, data kept. Nothing deletes it automatically. Staff can permanently delete database records; that keeps audit records, does not remove files, and can leave some records behind." | 7 years, nothing cleared: `B/Controllers/EmployeesController.cs:3036-3037`. The anonymiser exists (`B/Infrastructure/Retention/Rules/ExpiredEmployeeRecordRule.cs`) but the sweep is off (`DataRetentionOptions.cs:25,32,39`; no `DataRetention__*` in `render.yaml`). Even enabled, 0 of 76 soft-deleted employees are due before 2033 (`scratchpad/data-retention.md` §5). Tenant close: `PlatformController.DeleteTenant`. Manual purge: `B/Controllers/PlatformController.cs:931-992` (keeps `AuditLog`/`AdminAuditLog` `:960`, reports `unresolvedTables` `:991`, no storage call). **The legal-hold clause is deleted: `grep -i "legal hold"` finds it only in the old page and in docs.** |
| "Payroll records: minimum 7 years or as required by tax authority" | "kept with no automatic deletion. They carry their own copies of employee names and IBANs." | No payroll purge exists. Denormalised copies: `ExpiredEmployeeRecordRule.cs` class remarks; `scratchpad/data-retention.md` §2 |
| "Attendance & leave logs: 3 years after the record date" | "Kept, with no automatic deletion" | No rule exists: `B/Infrastructure/Retention/Rules/` (3 rules, none for attendance or leave) |
| "Audit logs: 2 years from event date" | "Kept, with no automatic deletion. The application cannot change or delete the central or payroll audit logs." | `ZayraDbContext.cs:448-465`; purging would break the hash chain |
| *(absent)* | **Uploaded files**: "The platform has no function that deletes a file from the document store." | `IDocumentStorage` has **no delete method**: `B/Infrastructure/Documents/LocalDocumentStorage.cs:5-14`; `grep DeleteObject` → 0 hits |
| *(absent)* | AI request records kept | see §5 |
| "Session tokens expire within 24 hours; refresh tokens within 30 days" | "Access tokens expire after **30 minutes**. Sessions can't be kept going past an org limit: **8 h default, never more than 24 h**. Refresh tokens expire after a period the org sets, **1–90 days**. Expired tokens and sign-in records are not deleted automatically." | `B/Application/Auth/AuthOptions.cs:18-19`; session clamp `B/Infrastructure/Auth/AuthService.cs:208-210` (15–1440 min, default 480 from `B/Models/AccessControl.cs:36`); refresh clamp `AuthService.cs:637` (1–90). "Within 30 days" was false because 90 days is reachable. |
| *(absent)* | **Statute vs erasure**: "Saudi labour and social-insurance rules require employers to keep payroll and employment records … Where a request to erase your data conflicts … the data needed to meet it is kept, so erasure of employee data is not unconditional." | **Legal position.** L8. This is what `ExpiredEmployeeRecordRule` would do if enabled (statutory override), but today it is moot because nothing erases anything. |

### §9 Rights
| Old | New | Evidence |
|---|---|---|
| "Access — request a copy" | **Mobile** ESS shows profile, payslips, documents, attendance, leave. **Web** shows a summary, HR requests and roster. Neither shows everything, there is no download, and medical, disciplinary and termination fields are never shown to employees. | Mobile calls `/ess/profile`, `/ess/payslips`, `/ess/documents`: `mobile/src/api/services.ts:975,1041,1077`. The web ESS page calls only `myRoster/hrRequests/dashboard/askAi/createHrRequest` (`frontend/src/views/EmployeeSelfServicePage.tsx`). `essApi.profile` is defined but never called. Excluded fields: `EmployeeManagementDtos.cs:364-369`. No DSAR export anywhere. |
| "Rectification" | Mobile: request changes to **6 fields**; HR approves. Web: none. Everything else via HR. | Allow-list `EmployeeSelfServiceController.cs:35-38`; approve/reject role-gated `:298-299,331-332`; only mobile calls it, `mobile/src/api/services.ts:1047` |
| "Erasure — request deletion where no legal obligation requires retention" | "No self-service deletion; no function that erases one person's data. HR deletion moves the record out of the active lists and keeps it." | Self-delete refused `B/Infrastructure/Auth/AccessManagementService.cs:483-484`; no `[HttpDelete]` in `EmployeeSelfServiceController.cs`; soft delete `EmployeesController.cs:3026-3060` |
| "**Portability — receive your data in a machine-readable format**" | "**Not available.** Mobile: payslips as PDF, own documents as originals. HR can export employee records as CSV; no export of one person's complete data." | Payslip PDF `EmployeeSelfServiceController.cs:487`, mobile `services.ts:1032-1034`; own docs `services.ts:1105-1107`; HR CSV `EmployeesController.cs:244-272` |
| "Restriction", "Object" | "The platform has no feature for this." | No mechanism anywhere (see agent sweep; `Employee.Status="Suspended"` is access suspension, `Employee.cs:14`) |
| "Withdraw consent — for processing based on consent only" | "The platform does not record consent for any processing. Mobile dashboard punch requires location permission; kiosk does not." | No consent record; `EmployeeDashboard.tsx:70-91`; `KioskAttendanceScreen.tsx:36-46` |
| "email privacy@ … **We will respond within 30 days.**" | "You can also write to privacy@ … we may need to refer your request to it." | The **30-day promise is removed**: nothing in the product tracks or enforces it. L9. |

### §10 Cookies
| Old | New | Evidence |
|---|---|---|
| "uses strictly necessary **cookies** and session tokens" | "**The application sets no cookies.**" + list of localStorage contents; mobile uses secure storage | No `document.cookie`, `Set-Cookie`, `Response.Cookies`, `cookies()` or middleware in frontend or backend. localStorage: `AuthContext.tsx:72-73`, `LocaleContext.tsx:25`, `utils/theme.ts:14`, `CompanyContext.tsx:73`, `AppLayout.tsx:29,348`. Mobile: `mobile/src/storage/index.ts:10-16,72-76`. |
| "We do not use third-party advertising cookies or cross-site tracking" | "contains no analytics, advertising or error-reporting code" + **Google Fonts sends your IP to Google** | `frontend/app/layout.tsx:55-68`. "No cross-site tracking" dropped: a third-party font request can't be proven not to be used for tracking. |
| "**A detailed cookie inventory is available within the platform settings under Privacy & Cookies.**" | **removed** | **The screen does not exist.** The only occurrence of the word "cookie" in the frontend was that sentence. The settings tabs are `frontend/src/views/SetupPage.tsx:72-89`. |

### §11 Children → "Children and other people"
| Old | New | Evidence |
|---|---|---|
| "**We do not knowingly collect data from individuals under 16.** If … we will delete it promptly." | "also holds data about people who do not use it: emergency contacts, **dependants, who may be children** (name, relationship, national ID, DOB, visa expiry), and job candidates" | `EmployeeDependent.cs:9-13`. The old sentence was false: the product is built to collect dependants' dates of birth. "Delete promptly" also contradicted §8, since nothing can hard-delete one person's data. L10. |

### §12 Changes
| Old | New | Evidence |
|---|---|---|
| "notify Customers via in-platform notice or email **at least 30 days before**… **Continued use … constitutes acceptance**" | "The date at the top … is when it was last changed. How customers are told about changes is set out in the customer agreement." | No policy-change notice mechanism exists (grep). Deemed acceptance is a legal construct. L11. |

### §13 Contact
| Old | New | Evidence |
|---|---|---|
| Title "Contact & **Data Protection Officer**" | "Contact". No DPO named. | No DPO was ever named on the page. L12. |
| privacy@, security@, legal@ | kept | **Not verifiable from code** — see §5 of this report. |
| "lodge a complaint with the data protection authority in your country of residence" | same + "In Saudi Arabia that is the Saudi Data & AI Authority (SDAIA)." | **Legal fact** (PDPL regulator). L13. |

---

## 3. Everything removed, and why

1. **"Anonymised within 90 days of account closure"** — the delete path stamps 7 years (`EmployeesController.cs:3037`) and anonymises nothing. The anonymiser is shipped but switched off.
2. **"except where legal hold applies"** — no legal-hold field, API or UI exists.
3. **"typically 5–7 years" / "minimum 7 years" / "3 years" / "2 years"** — no rule implements any of them. Audit logs are immutable by design.
4. **"Session tokens … 24 hours; refresh tokens … 30 days"** — wrong numbers: 30 min, 8–24 h session cap, 1–90 day refresh.
5. **"masked in user-facing responses"** (bank) — blanked or shown in full, not masked.
6. **"tax identification numbers"** — not collected for employees.
7. **"biometric device identifiers (hashed)"** — no biometric data exists; the hash is of a device API key.
8. **"geolocation (where permitted)" + "Consent … where explicit consent is collected"** — no consent is recorded, and the mobile dashboard punch requires location.
9. **"pages visited, feature usage, API call logs, error reports"** — none of it is collected.
10. **"Improve our services through aggregated, anonymised analytics"** — no analytics.
11. **"monitoring tools"** as a sub-processor — none exist.
12. **"email delivery" as our sub-processor** — mail goes through the customer's own SMTP.
13. **"A current list of sub-processors is available on request"** — replaced by the list itself.
14. **"subject to equivalent privacy protections"** (business transfer) — unbacked promise.
15. **"TLS-required API-to-database connection"** — not enforced by the application.
16. **"Independent penetration-test scope and cadence are confirmed separately…"** — implied tests that no artefact shows happened.
17. **"principle of least privilege"** — describes intent. Grants are tenant-configured.
18. **"Portability — receive your data in a machine-readable format"** as an available right — no such export exists. Now stated as "Not available".
19. **"We will respond within 30 days"** — no mechanism behind it.
20. **"strictly necessary cookies"** and **"cookie inventory … under Privacy & Cookies"** — no cookies, no such screen.
21. **"We do not knowingly collect data from individuals under 16 … delete it promptly"** — dependants' DOBs are collected, and one person's data can't be hard-deleted.
22. **"notify … at least 30 days before … continued use constitutes acceptance"** — no mechanism; legal construct.
23. **"Data Protection Officer"** in the heading — none named.
24. "Framework references describe a control-improvement direction" — roadmap language.

Nothing was softened into a feature. The retention section opens with the limitation, not a table of numbers.

---

## 4. For a lawyer

### 4a. Legal judgements — engineering cannot decide these
- **L1. Controller/processor characterisation.** The page says the employer decides and we run the platform "on its behalf". Kode Kinetics also chooses sub-processors (Ollama, Vercel, B2) and keeps sign-in and security logs for its own purposes. Is Kode Kinetics purely a processor for all of this, or a controller for some of it?
- **L2. Status of this page vs. the customer agreement/DPA.** The page says it "does not replace" the DPA terms. Is that the right hierarchy, and is a public page the right vehicle for what are really processor-to-controller disclosures?
- **L3. "We do not sell personal data / do not use it for advertising."** A business commitment, not a system property.
- **L4. Legal basis.** The page deliberately states none and defers to the employer. PDPL's bases (and its 2023 amendments on legitimate interest, which excludes sensitive data) need a lawyer. Health data, dependants' data and nationality classification are the pressure points.
- **L5. Business-transfer clause** wording.
- **L6. AI provider terms.** Is there a DPA with Ollama? Do its terms permit retention or training on submitted data? The page names the provider and says our requests carry no retention or training instruction, which is true. It says nothing about Ollama's own practices because we don't know them. **Sending names, gender, masked IDs, attendance patterns and verbatim uploaded documents to an offshore model may need a transfer basis and a notice or consent decision under PDPL. That decision is not ours to make.**
- **L7. Cross-border transfer.** All data is outside the Kingdom; files are in the US. PDPL Art. 29 and its transfer regulations. The page defers the mechanism to the customer agreement (required phrase kept). **Whether Evostel's data may lawfully sit where it sits today is the headline question.**
- **L8. Statute vs. erasure.** The page states that employer retention obligations override erasure. Which Saudi instruments (Labour Law, GOSI, ZATCA), for how long, and must a refusal be communicated to the data subject? The code's default is 7 years (`DataRetentionOptions.cs:55`); `docs/CONFIGURABILITY_PROGRAM.md` says ≥5 years. See `scratchpad/data-retention.md` §7 Q1–Q2.
- **L9. Response-time commitment.** Removed. PDPL sets a response period for controllers. Decide whether to state one and who meets it.
- **L10. Children's data.** Dependants include minors. PDPL treats children's data specially. What notice and basis are required, and who gives them (employer or us)?
- **L11. Change-notification commitment** and any deemed-acceptance language.
- **L12. DPO.** Whether PDPL requires Kode Kinetics (or Evostel) to appoint one for this processing, given health data and scale.
- **L13. Complaint authority.** SDAIA is stated as the Saudi authority. Confirm the wording.
- **L14. The existing backlog.** The old page promised 90-day anonymisation. 76 soft-deleted employee records (496 employee rows across 48 soft-deleted tenants) have been kept in full the whole time. Is there exposure for the period the old page was published? Does it need remediation or disclosure beyond correcting the page?
- **L15. Sensitive-data disclosures.** Health, nationality classification, gender sent to an AI model, and third-party (dependant/emergency contact) data are now disclosed. Is disclosure enough, or do any of these need a basis the product can't currently record?

### 4b. What the system does — technical facts, stated on the page
Everything in §2 of this report not marked **Legal**. These are cited to code. Counsel can rely on them being true of the repository at `da59275`, **subject to the §1 dashboard checks**.

---

## 5. Is there anything left on the page I cannot substantiate?

**Yes — these, and only these:**

1. **The live configuration values in §1.** The AI model name, the B2 region, the database and app-server regions, and the absence of `DataRetention__*`, `QIWA_USE_LIVE_ADAPTER` and `AI_LOG_PROMPTS` overrides. Each is consistent with every piece of repo and operator evidence I have, but none is provable from the repository. Evidence I did have: the render.yaml blueprint, the brief, and the operator's B2 record.
2. **"Outside the Kingdom" for Render and Neon** rests on neither provider offering a Saudi region. That is external knowledge, not a repo fact.
3. **Vercel's location** ("not confined to the Kingdom") is external knowledge of Vercel's network.
4. **The three mailboxes** (privacy@, security@, legal@) — I can't verify from code that they exist or are monitored.
5. **"We do not sell personal data / do not use it for advertising"** — a business statement. The code has no outbound flow beyond those listed, which is consistent, but code can't prove a negative about commercial conduct.
6. **SDAIA as the complaint authority** — legal fact, not system fact.

Everything else on the page is cited to a specific file and line above.

---

## 6. Gaps better closed in code (not done — this branch changes no behaviour)

Listed so nobody "fixes" them by rewording the page back:
- **No delete in `IDocumentStorage`.** Files outlive every deletion, including a tenant purge. Needs the two-phase blob purge already scoped in `scratchpad/data-retention.md` §8.3.
- **SMTP password stored in plaintext** (`SetupSettingsController.cs:108-113` protects only `Category=="Notifications"`). A one-line scope change plus a read-side `Unprotect` in `SmtpEmailService.LoadConfigAsync`. This is the clearest security fix this rewrite surfaced.
- **AI prompts not redacted before sending** (`AiPromptBuilder.cs:27-28`). `AiRedactionService` exists and only protects the log copy.
- **No individual data export (DSAR)**, no restriction/objection flag, no consent record, no legal hold.
- **Web app has no profile screen or self-service correction.** `essApi.profile` exists and is never called.
- **Only 2 of 14 audit tables are append-only.**
- **DB TLS not enforced by the app**; the documented format disables certificate validation.
- **Tokens in `localStorage`** (already `docs/PRODUCTION_HARDENING.md` P1-19).
- **Google Fonts loaded from Google.** Self-hosting removes a third-party IP disclosure from every page.
- **Retention sweep** — enabling it changes very little: 0 employee rows due before 2033, 48/48 tenants retained on first run (`scratchpad/data-retention.md` §5). The page must not claim anything about it until it is on.

## 7. Found outside this page (not changed — out of scope, flagged)

- **`frontend/app/terms/page.tsx:120-121`**: *"Upon termination you may request an export of your data within 60 days."* That is a **portability promise with a third retention number** (the policy said 90, the code says 7 years). A bulk CSV export exists; a complete tenant export does not.
- **`frontend/app/security/page.tsx:152`**: *"Render auto-deploy is disabled."* The e2e test pins that string, but the **live service had `autoDeploy: yes`** at last check (deploy-pipeline operator note). Verify against the Render API before anyone relies on it.
- **`frontend/app/security/page.tsx:73`**: *"configured integration credentials … protected at the application layer"* — true for Qiwa and notification providers, **not for the SMTP password**.
- **`docs/DEPLOYMENT_MULTI_CLIENT.md:78`** says AI is opt-in (`none`). `render.yaml:171` ships `ollama`.

---

## 8. Verification

- **Baseline** (unmodified `develop`): browserless suite 12 passed; RTL pin 6.
- **After:** `npx tsc --noEmit` → exit 0. `npx next build` → exit 0, `/privacy` static, 1.16 kB. `npx playwright test -c e2e/playwright.browserless.config.ts` → **12 passed**. *"the pinned exception count may only go down"* passes at **6**, and the rewrite adds no physical direction class.
- **The new assertions are not vacuous.** Run against the old page (`git show develop:frontend/app/privacy/page.tsx`): all 6 forbidden strings present and all 5 required strings missing. Against the new page: 0 and 0. The `legal hold` pin also caught a leftover phrase in my own header comment, which was fixed.
- **Backend untouched**, so the 2675/0 baseline is unaffected and was not re-run.
- **Rendered** (dev server, Chromium), measured and looked at:
  - *Default 1280px*: 13 sections, contents nav in two columns, definition rows 176px label / 622px text.
  - *Dark* (`.dark` on `<html>`, as the dashboard layout applies it): background `rgb(2,6,23)`, all text, cards, notices and the amber box legible. Checked visually at desktop and 390px.
  - *390px*: **no horizontal scroll** (scrollWidth 390 = clientWidth). **The old page overflowed to 423px** because of its fixed `w-48` table column. Rows now stack to one column. Provider and rights cards and the contact rows stack too.
  - *Arabic locale* (`kynexone-locale=ar`): `<html dir="rtl">`. The page wrapper stays `dir="ltr" lang="en"`, as on `security/page.tsx`.
  - *Forced RTL* (pin removed): the layout mirrors correctly — header swapped, list indent moves from left margin 20px to right margin 20px, text right-aligned. **English punctuation breaks** (".your organisation's agreement", "…data .1"). That is why the English-only page is pinned LTR; the logical classes mean a translation can drop the pin.
  - No console errors in any mode.
- **Loading/empty/error states:** the page is a static server component with no data fetch. It has no loading or empty state, and errors fall to the existing `app/error.tsx`.

## 9. Commit

Branch `docs/privacy-policy-truthful`, committed, **not pushed, not merged**. SHA in the hand-off message.
