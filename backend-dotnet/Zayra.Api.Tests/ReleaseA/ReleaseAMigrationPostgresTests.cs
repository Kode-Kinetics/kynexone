using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Testcontainers.PostgreSql;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Models;
using RaMigration = Zayra.Api.Migrations.ReleaseAEntitlementsAndRenewals;

namespace Zayra.Api.Tests.ReleaseA;

/// <summary>
/// The Release A migration itself (20261007000100_ReleaseAEntitlementsAndRenewals) on a fresh PostgreSQL 16 built by
/// the real migration chain — the shared fixture uses EnsureCreated, which cannot prove a migration. Upgrades a
/// database holding pre-release rows and proves: old rows read unchanged and stay writable (N-1), every new CHECK,
/// the EXCLUDE, the PublicId FKs and the four triggers fire, the generated deadline and the xmin token work through
/// EF, Down refuses over evidence, and with the evidence gone the migration rolls back and re-applies cleanly.
/// </summary>
[Trait("Category", "Integration")]
public sealed class ReleaseAMigrationPostgresTests
{
    private const string ThisMigration = "20261007000100_ReleaseAEntitlementsAndRenewals";
    private static readonly Guid LegacyTenant = Guid.Parse("10000000-0000-0000-0000-0000000000a0");
    private static readonly Guid LegacyContract = Guid.Parse("20000000-0000-0000-0000-0000000000a0");
    private static readonly Guid LegacyGrade = Guid.Parse("50000000-0000-0000-0000-0000000000a0");

    [Fact]
    public async Task Upgrade_EnforcesEveryReleaseAConstraint_RefusesRollbackOverEvidence_AndReapplies()
    {
        await using var container = new PostgreSqlBuilder().WithImage("postgres:16-alpine").Build();
        await container.StartAsync();
        var options = new DbContextOptionsBuilder<ZayraDbContext>().UseNpgsql(container.GetConnectionString()).Options;
        await using var db = new ZayraDbContext(options);
        var migrator = db.Database.GetInfrastructure().GetRequiredService<IMigrator>();
        // The migration immediately before this one: rolling back to it runs exactly this Down().
        var all = db.Database.GetMigrations().ToList();
        var Before = all[all.IndexOf(ThisMigration) - 1];

        // ── Before: rows the previous release wrote ─────────────────────────────────────────────
        await migrator.MigrateAsync(Before);
        await Sql(db, $"""
            INSERT INTO employee_contracts (id,tenant_id,employee_id,employee_name,contract_number,contract_type,status,start_date,end_date,basic_salary,
                currency_code,content_html_en,content_html_ar,language,version,signed_by_employee_name,signed_by_hr_name,file_url,created_at_utc,is_deleted)
            VALUES ('{LegacyContract}','{LegacyTenant}',gen_random_uuid(),'Legacy','CON-LEGACY','Employment','Active','2025-01-01','2025-12-31',5000,
                'SAR','','','en',1,'','','',CURRENT_TIMESTAMP,false);
            INSERT INTO employee_salary_structures (id,tenant_id,employee_id,salary_structure_id,basic_salary,housing_allowance,transport_allowance,
                food_allowance,mobile_allowance,other_allowance,fixed_deduction,effective_date,currency,is_active,created_at_utc)
            VALUES (gen_random_uuid(),'{LegacyTenant}',999999,gen_random_uuid(),5000,1250,500,0,0,0,0,'2025-01-01','SAR',true,CURRENT_TIMESTAMP);
            INSERT INTO grades (id,tenant_id,code,name,band,level,min_salary,mid_salary,max_salary,currency,is_active,created_at_utc,is_deleted)
            VALUES ('{LegacyGrade}','{LegacyTenant}','G1','Grade 1','',1,0,0,0,'SAR',true,CURRENT_TIMESTAMP,false);
            INSERT INTO grade_entitlements (id,tenant_id,company_id,grade_id,pay_component_code,entitlement_class,eligible,value_type,amount,rate,
                max_outstanding_amount,effective_from,created_at_utc)
            VALUES (gen_random_uuid(),'{LegacyTenant}',NULL,'{LegacyGrade}','LOAN_PERSONAL','Facility',true,'MultipleOfBasic',NULL,2,9000,'2026-01-01',CURRENT_TIMESTAMP);
            """);

        await migrator.MigrateAsync();
        await migrator.MigrateAsync(); // re-run is a no-op
        Assert.Equal(1L, await Scalar<long>(db, $"SELECT count(*) FROM \"__EFMigrationsHistory\" WHERE \"MigrationId\" = '{ThisMigration}'"));
        Assert.Empty(await db.Database.GetPendingMigrationsAsync());

        // Old rows read as before and stay writable: no new column breaks an existing write (the NOT VALID trap).
        Assert.True(await Scalar<bool>(db, $"SELECT auto_renew AND renewal_number IS NULL AND provisional_basis IS NULL FROM employee_contracts WHERE id = '{LegacyContract}'"));
        Assert.Equal("Amount", await Scalar<string>(db, "SELECT housing_basis FROM employee_salary_structures WHERE employee_id = 999999"));
        Assert.Equal(("None", "Any"), (await Scalar<string>(db, "SELECT dependant_scope FROM grade_entitlements WHERE pay_component_code = 'LOAN_PERSONAL'"),
            await Scalar<string>(db, "SELECT nationality_scope FROM grade_entitlements WHERE pay_component_code = 'LOAN_PERSONAL'")));
        await Sql(db, $"UPDATE employee_contracts SET status = 'Expired' WHERE id = '{LegacyContract}'");
        await Sql(db, "UPDATE employee_salary_structures SET is_active = false WHERE employee_id = 999999");

        // Everything EF cannot model is present, and xmin is the system column, not a created one.
        foreach (var trigger in new[] { "trg_employee_entitlements__close_only", "trg_employee_entitlements__containment",
                     "trg_employee_contracts__entitlement_containment", "trg_contract_renewal_cases__transition_guard" })
            Assert.Equal(1L, await Scalar<long>(db, $"SELECT count(*) FROM pg_trigger WHERE tgname = '{trigger}'"));
        foreach (var constraint in new[] { RaMigration.EntitlementExclusionName, "fk_employee_entitlements__employee", "fk_contract_renewal_cases__employee",
                     "ck_approval_requests__payload_sha256", "ck_contract_renewal_cases__allowed_actions", "ck_contract_renewal_cases__action_allowed",
                     "ck_contract_renewal_cases__non_saudi_never_converts", "ck_contract_renewal_cases__offer_sha256" })
            Assert.Equal(1L, await Scalar<long>(db, $"SELECT count(*) FROM pg_constraint WHERE conname = '{constraint}'"));
        Assert.Equal(0L, await Scalar<long>(db, "SELECT count(*) FROM information_schema.columns WHERE table_name = 'contract_renewal_cases' AND column_name = 'xmin'"));

        // ── A Release A tenant written through EF ───────────────────────────────────────────────
        var tenant = new Tenant { Id = Guid.NewGuid(), Name = "Masar", Slug = $"masar-{Guid.NewGuid():N}" };
        var company = new Company { TenantId = tenant.Id, LegalNameEn = "Masar Facility Services", CountryCode = "SAU", Jurisdiction = "KSA-mainland",
            RegistrationNumber = $"CR-{Guid.NewGuid():N}", DefaultCurrency = "SAR", IsActive = true };
        var otherCompany = new Company { TenantId = tenant.Id, LegalNameEn = "Masar Logistics", CountryCode = "SAU", Jurisdiction = "KSA-mainland",
            RegistrationNumber = $"CR-{Guid.NewGuid():N}", DefaultCurrency = "SAR", IsActive = true };
        var grade = new Grade { TenantId = tenant.Id, Code = "G3", Name = "Supervisor", Level = 30 };
        var mohammed = Employee(tenant.Id, company.Id, "E-1", grade.Id);
        var colleague = Employee(tenant.Id, company.Id, "E-2", grade.Id);
        db.AddRange(tenant, company, otherCompany, grade, mohammed, colleague);
        await db.SaveChangesAsync();
        var term = Contract(tenant.Id, company.Id, mohammed.PublicId, "CON-1", "Active", new DateOnly(2026, 2, 1), new DateOnly(2027, 1, 31));
        var next = Contract(tenant.Id, company.Id, mohammed.PublicId, "CON-2", "Active", new DateOnly(2027, 2, 1), new DateOnly(2028, 1, 31));
        var draft = Contract(tenant.Id, company.Id, mohammed.PublicId, "CON-3", "Draft", new DateOnly(2026, 2, 1), new DateOnly(2027, 1, 31));
        var colleagueTerm = Contract(tenant.Id, company.Id, colleague.PublicId, "CON-4", "Active", new DateOnly(2026, 2, 1), new DateOnly(2027, 1, 31));
        var medical = new GradeEntitlement { TenantId = tenant.Id, GradeId = grade.Id, PayComponentCode = "MEDICAL", EntitlementClass = PayEntitlementClasses.Contractual,
            Eligible = true, ValueType = GradeEntitlementValueTypes.CoverageTier, CoverageTier = CoverageTiers.B, DependantScope = DependantScopes.Family,
            MaxDependants = 3, LimitPeriod = EntitlementLimitPeriods.PerTerm, EffectiveFrom = new DateOnly(2026, 1, 1) };
        var salary = new EmployeeSalaryStructure { TenantId = tenant.Id, EmployeeId = mohammed.Id, SalaryStructureId = Guid.NewGuid(), BasicSalary = 8000m,
            HousingBasis = AllowanceBases.PercentOfBasic, HousingRate = 0.25m, HousingAllowance = 2000m, TransportAllowance = 800m, Currency = "SAR",
            EffectiveDate = new DateOnly(2026, 2, 1) };
        var colleagueSalary = new EmployeeSalaryStructure { TenantId = tenant.Id, EmployeeId = colleague.Id, SalaryStructureId = Guid.NewGuid(), BasicSalary = 6000m,
            Currency = "SAR", EffectiveDate = new DateOnly(2026, 2, 1) };
        var evidence = new EmployeeDocument { TenantId = tenant.Id, CompanyId = company.Id, EmployeeId = mohammed.Id,
            DocumentType = RestrictedEmployeeDocumentTypes.QiwaEvidence, FileName = "qiwa.png", StorageUrl = "x" };
        db.AddRange(term, next, draft, colleagueTerm, medical, salary, colleagueSalary, evidence);
        await db.SaveChangesAsync();
        var t = tenant.Id;

        // ── grade_entitlements: the widened shape ───────────────────────────────────────────────
        await Sql(db, Cell(t, grade.Id, "HOUSING", "'PercentOfBasic'", rate: "0.25"));
        await Rejected(db, Cell(t, grade.Id, "TRANSPORT", "'PercentOfBasic'", rate: "1.5"), "23514");                       // > 100%
        await Rejected(db, Cell(t, grade.Id, "AIR_TICKET", "'Quantity'", extra: ("nationality_scope", "'NonSaudi'")), "23514");  // no basis, no quantity
        await Sql(db, Cell(t, grade.Id, "AIR_TICKET", "'Quantity'", extra: ("nationality_scope", "'NonSaudi'"), quantity: "1",
            basis: "'Home-leave ticket per contract'"));
        await Rejected(db, Cell(t, grade.Id, "EDUCATION", "'Amount'", amount: "10000", eligible: false), "23514");             // ineligible with a value
        await Rejected(db, Cell(t, grade.Id, "EDUCATION", "'EligibilityOnly'", extra: ("max_dependants", "2")), "23514");      // max without scope
        await Rejected(db, Cell(t, grade.Id, "PER_DIEM", "'CoverageTier'"), "23514");                                          // tier type without tier
        await Rejected(db, Cell(t, grade.Id, "PER_DIEM", "'Amount'", amount: "150", extra: ("coverage_tier", "'Platinum'")), "23514");

        // ── pay_components: a floor component cannot be skipped ─────────────────────────────────
        await Sql(db, $"""
            INSERT INTO pay_components (id,tenant_id,company_id,code,name_en,name_ar,component_type,calc_method,is_taxable,gosi_subject,wps_included,
                eosb_included,emit_when_zero,is_family,display_order,is_system,is_statutory,is_active,is_deleted,created_at_utc,entitlement_class,statutory_floor,is_offered)
            VALUES (gen_random_uuid(),'{t}','{otherCompany.Id}','EDUCATION','Education','','Benefit','Fixed',false,false,false,false,false,false,220,false,false,true,false,
                CURRENT_TIMESTAMP,'Contractual','None',false)
            """);
        await Rejected(db, $"""
            INSERT INTO pay_components (id,tenant_id,company_id,code,name_en,name_ar,component_type,calc_method,is_taxable,gosi_subject,wps_included,
                eosb_included,emit_when_zero,is_family,display_order,is_system,is_statutory,is_active,is_deleted,created_at_utc,entitlement_class,statutory_floor,is_offered)
            VALUES (gen_random_uuid(),'{t}','{otherCompany.Id}','MEDICAL','Medical','','Benefit','Fixed',false,false,false,false,false,false,210,false,false,true,false,
                CURRENT_TIMESTAMP,'Contractual','Medical',false)
            """, "23514");

        // ── employee_contracts: the chain columns ───────────────────────────────────────────────
        await Sql(db, $"UPDATE employee_contracts SET renewed_from_contract_id = '{term.Id}', renewal_number = 1, chain_started_on = '2026-02-01', worker_nationality_class = 'NonSaudi' WHERE id = '{next.Id}'");
        await Rejected(db, $"UPDATE employee_contracts SET renewed_from_contract_id = '{term.Id}' WHERE id = '{draft.Id}'", "23505");      // renewed twice
        await Rejected(db, $"UPDATE employee_contracts SET renewed_from_contract_id = '{colleagueTerm.Id}' WHERE id = '{draft.Id}'", "23503"); // another employee's term
        await Rejected(db, $"UPDATE employee_contracts SET renewed_from_contract_id = id WHERE id = '{term.Id}'", "23514");
        await Rejected(db, $"UPDATE employee_contracts SET provisional_basis = 'DeemedRenewal' WHERE id = '{next.Id}'", "23514");          // provisional with a number
        await Rejected(db, $"UPDATE employee_contracts SET worker_nationality_class = 'Expat' WHERE id = '{term.Id}'", "23514");

        // ── employee_salary_structures: basis pairing and the Art. 61 amounts ───────────────────
        await Rejected(db, $"UPDATE employee_salary_structures SET housing_allowance = 1999 WHERE id = '{salary.Id}'", "23514");        // ≠ round(8000 × 0.25)
        await Rejected(db, $"UPDATE employee_salary_structures SET housing_basis = 'InKind', housing_rate = NULL WHERE id = '{salary.Id}'", "23514"); // in kind with cash
        await Rejected(db, $"UPDATE employee_salary_structures SET transport_basis = 'PercentOfBasic' WHERE id = '{salary.Id}'", "23514");     // basis without rate
        await Rejected(db, $"UPDATE employee_salary_structures SET qiwa_evidence_document_id = '{evidence.Id}' WHERE id = '{salary.Id}'", "23514"); // undated

        // ── approval_requests: payload and its hash, both or neither ────────────────────────────
        await Sql(db, $"""
            INSERT INTO approval_requests (id,tenant_id,workflow_id,entity_name,entity_id,title,status,current_step_order,decision_version,
                current_approver_name,current_approver_role,current_approver_type,current_queue,sla_hours,escalated_to_role,priority,created_at_utc)
            VALUES (gen_random_uuid(),'{t}',gen_random_uuid(),'ContractRenewal','x','Offer','Pending',1,0,'','','','',24,'','Normal',CURRENT_TIMESTAMP)
            """);
        await Rejected(db, "UPDATE approval_requests SET payload = '[]'::jsonb", "23514");                                  // payload without its hash
        await Rejected(db, $"UPDATE approval_requests SET payload = '[]'::jsonb, payload_sha256 = '{new string('Z', 64)}'", "23514"); // not lower-case hex
        await Sql(db, $"UPDATE approval_requests SET payload = '[]'::jsonb, payload_sha256 = '{new string('a', 64)}'");
        await Sql(db, "UPDATE approval_requests SET payload = NULL, payload_sha256 = NULL");

        // ── employee_entitlements ───────────────────────────────────────────────────────────────
        string Row(string code, string from, string? to, Guid? contract = null, Guid? company2 = null, Guid? employee = null, string cls = "'Contractual'",
            string valueType = "'CoverageTier'", string tier = "'B'", string source = "'GradeDefault'", string? cell = null, string scope = "'Family'",
            string? amount = null, string? rate = null, string? basisSalary = null, string? resolved = null, string verification = "'Verified'",
            Guid? id = null, Guid? carried = null) => $"""
            INSERT INTO employee_entitlements (id,tenant_id,company_id,employee_id,contract_id,pay_component_code,entitlement_class,value_type,amount,rate,
                coverage_tier,dependant_scope,resolved_amount,resolved_basis_salary_id,source,verification_state,grade_entitlement_id,carried_from_entitlement_id,
                effective_from,effective_to,created_at_utc)
            VALUES ('{id ?? Guid.NewGuid()}','{t}','{company2 ?? company.Id}','{employee ?? mohammed.PublicId}','{contract ?? term.Id}','{code}',{cls},{valueType},
                {amount ?? "NULL"},{rate ?? "NULL"},{tier},{scope},{resolved ?? "NULL"},{(basisSalary is null ? "NULL" : $"'{basisSalary}'")},{source},{verification},
                {cell ?? (source == "'GradeDefault'" ? $"'{medical.Id}'" : "NULL")},{(carried is null ? "NULL" : $"'{carried}'")},'{from}',{(to is null ? "NULL" : $"'{to}'")},CURRENT_TIMESTAMP)
            """;

        var frozen = Guid.NewGuid();
        await Sql(db, Row("MEDICAL", "2026-02-01", "2027-01-31", id: frozen));
        await Rejected(db, Row("MEDICAL", "2026-06-01", null), "23P01");                                              // overlap
        await Rejected(db, Row("MEDICAL", "2027-02-01", "2028-02-29", contract: next.Id), "23514");                  // runs past the term
        await Rejected(db, Row("AIR_TICKET", "2026-03-01", "2026-04-30", contract: draft.Id, source: "'Migrated'"), "23514"); // draft contract
        await Rejected(db, Row("MEDICAL", "2027-02-01", "2028-01-31", contract: next.Id, company2: otherCompany.Id), "23514"); // company differs
        await Rejected(db, Row("HOUSING", "2026-02-01", null, cls: "'QiwaWage'", valueType: "'Amount'", tier: "NULL", scope: "'None'",
            amount: "2000", source: "'Migrated'"), "23514");                                                           // QiwaWage cash never here
        await Rejected(db, Row("AIR_TICKET", "2026-02-01", null, source: "'Exception'"), "23514");                    // exception without approval
        await Rejected(db, Row("AIR_TICKET", "2026-02-01", null, source: "'GradeDefault'", cell: "NULL"), "23514");   // grade default citing no cell
        await Rejected(db, Row("AIR_TICKET", "2026-02-01", null, source: "'GradeDefault'"), "23503");                 // a MEDICAL cell for a ticket row
        await Rejected(db, Row("AIR_TICKET", "2026-02-01", null, source: "'Migrated'", employee: Guid.NewGuid()), "23503"); // unknown employee
        await Rejected(db, Row("AIR_TICKET", "2026-02-01", null, source: "'Migrated'", contract: colleagueTerm.Id), "23503"); // colleague's contract
        // PercentOfBasic needs its witness, from the employee's own salary row.
        await Rejected(db, Row("X_PCT", "2026-02-01", null, valueType: "'PercentOfBasic'", tier: "NULL", scope: "'None'", rate: "0.25", source: "'Migrated'"), "23514");
        await Rejected(db, Row("X_PCT", "2026-02-01", null, valueType: "'PercentOfBasic'", tier: "NULL", scope: "'None'", rate: "0.25", source: "'Migrated'",
            basisSalary: colleagueSalary.Id.ToString(), resolved: "1500"), "23514");
        var pct = Guid.NewGuid();
        await Sql(db, Row("X_PCT", "2026-02-01", "2027-01-31", valueType: "'PercentOfBasic'", tier: "NULL", scope: "'None'", rate: "0.25", source: "'Migrated'",
            basisSalary: salary.Id.ToString(), resolved: "2000", verification: "'Unverified'", id: pct));

        // Close-only: values never change; shorten only; confirm once; never delete.
        await Rejected(db, $"UPDATE employee_entitlements SET coverage_tier = 'A' WHERE id = '{frozen}'", "23514");
        await Rejected(db, $"UPDATE employee_entitlements SET effective_to = '2027-12-31' WHERE id = '{frozen}'", "23514");
        await Rejected(db, $"UPDATE employee_entitlements SET effective_to = NULL WHERE id = '{frozen}'", "23514");
        await Sql(db, $"UPDATE employee_entitlements SET verification_state = 'Verified' WHERE id = '{pct}'");
        await Rejected(db, $"UPDATE employee_entitlements SET verification_state = 'Unverified' WHERE id = '{pct}'", "23514");
        await Rejected(db, $"DELETE FROM employee_entitlements WHERE id = '{frozen}'", "23514");
        // A superseded term takes no new rows, but its existing rows can still be closed.
        await Sql(db, $"UPDATE employee_contracts SET status = 'Superseded' WHERE id = '{term.Id}'");
        await Sql(db, $"UPDATE employee_entitlements SET effective_to = '2027-01-30' WHERE id = '{frozen}'");
        await Rejected(db, Row("AIR_TICKET", "2026-03-01", "2026-04-30", source: "'Migrated'"), "23514");
        await Sql(db, $"UPDATE employee_contracts SET status = 'Active' WHERE id = '{term.Id}'");
        // The parent side: the contract cannot shrink under its rows.
        await Rejected(db, $"UPDATE employee_contracts SET end_date = '2026-12-31' WHERE id = '{term.Id}'", "23514");
        await Rejected(db, $"UPDATE employee_contracts SET company_id = '{otherCompany.Id}' WHERE id = '{term.Id}'", "23514");

        // Carried rows equal their origin and start after it ends.
        await Rejected(db, Row("X_PCT", "2027-02-01", "2028-01-31", contract: next.Id, valueType: "'PercentOfBasic'", tier: "NULL", scope: "'None'", rate: "0.25",
            source: "'Carried'", basisSalary: salary.Id.ToString(), resolved: "2100", carried: pct), "23514");         // witness ≠ round(basic × rate)
        await Sql(db, Row("X_PCT", "2027-02-01", "2028-01-31", contract: next.Id, valueType: "'PercentOfBasic'", tier: "NULL", scope: "'None'", rate: "0.25",
            source: "'Carried'", basisSalary: salary.Id.ToString(), resolved: "2000", carried: pct));                   // a PercentOfBasic carry
        await Rejected(db, Row("MEDICAL", "2027-02-01", "2028-01-31", contract: next.Id, tier: "'A'", source: "'Carried'", cell: "NULL", carried: frozen), "23514"); // raised
        await Sql(db, Row("MEDICAL", "2027-02-01", "2028-01-31", contract: next.Id, source: "'Carried'", cell: "NULL", carried: frozen));

        // ── contract_renewal_cases ──────────────────────────────────────────────────────────────
        string Case(Guid contract, string state = "'Open'", string nationality = "'NonSaudi'",
            string allowed = "ARRAY['RenewAsIs','RenewWithChanges','NonRenew']", string? action = null, string? hold = null) => $"""
            INSERT INTO contract_renewal_cases (id,tenant_id,company_id,employee_id,expiring_contract_id,expiring_end_date,worker_nationality_class,allowed_actions,
                state,contract_action,hold_reason,offer_version,notice_due_on,offer_due_on,qiwa_submit_due_on,qiwa_gate_due_on,qiwa_required,qiwa_attempts,opened_at)
            VALUES (gen_random_uuid(),'{t}','{company.Id}','{mohammed.PublicId}','{contract}','2027-01-31',{nationality},{allowed},{state},
                {action ?? "NULL"},{hold ?? "NULL"},0,'2026-12-02','2026-11-18','2027-01-01','2027-01-24',true,0,CURRENT_TIMESTAMP)
            """;
        await Rejected(db, Case(term.Id, state: "'Accepted'"), "23514");                                                 // opens Open/NeedsConfirmation only
        await Rejected(db, Case(term.Id, allowed: "ARRAY['ConvertIndefinite','NonRenew']"), "23514");                   // non-Saudi never converts
        await Rejected(db, Case(term.Id, allowed: "ARRAY['RenewAsIs','Extend']"), "23514");                             // unknown action
        await Rejected(db, Case(term.Id, action: "'NonRenew'", allowed: "ARRAY['RenewAsIs']"), "23514");                // action not allowed
        await Rejected(db, Case(term.Id, hold: "'Abroad'"), "23514");                                                    // hold reason while Open
        await Sql(db, Case(term.Id));
        await Rejected(db, Case(term.Id), "23505");                                                                      // one case per term
        var caseId = await Scalar<Guid>(db, $"SELECT id FROM contract_renewal_cases WHERE expiring_contract_id = '{term.Id}'");
        Assert.Equal(new DateOnly(2026, 11, 18), DateOnly.FromDateTime(await Scalar<DateTime>(db, $"SELECT next_hard_deadline FROM contract_renewal_cases WHERE id = '{caseId}'")));

        var sha = new string('a', 64);
        await Rejected(db, $"UPDATE contract_renewal_cases SET state = 'Accepted' WHERE id = '{caseId}'", "23514");    // Open → Accepted
        await Rejected(db, $"UPDATE contract_renewal_cases SET offer_sha256 = 'NOT-HEX' WHERE id = '{caseId}'", "23514");
        await Sql(db, $"UPDATE contract_renewal_cases SET state = 'OfferInPreparation' WHERE id = '{caseId}'");
        await Sql(db, $"UPDATE contract_renewal_cases SET state = 'InApproval', contract_action = 'RenewAsIs', offer_version = 1, offer_sha256 = '{sha}' WHERE id = '{caseId}'");
        await Sql(db, $"UPDATE contract_renewal_cases SET state = 'OfferSent' WHERE id = '{caseId}'");
        await Rejected(db, $"UPDATE contract_renewal_cases SET state = 'Accepted', employee_response = 'Accepted', employee_responded_at = now(), response_channel = 'ESS', offer_sha256 = '{new string('b', 64)}' WHERE id = '{caseId}'", "23514"); // a different offer
        await Sql(db, $"UPDATE contract_renewal_cases SET state = 'Accepted', employee_response = 'Accepted', employee_responded_at = now(), response_channel = 'ESS' WHERE id = '{caseId}'");
        var recorder = Guid.NewGuid();
        await Sql(db, $"UPDATE contract_renewal_cases SET state = 'QiwaPending', qiwa_request_no = 'Q-1', qiwa_sent_on = '2026-11-04', qiwa_respond_by_on = '2026-11-14' WHERE id = '{caseId}'");
        Assert.Equal(new DateOnly(2026, 11, 14), DateOnly.FromDateTime(await Scalar<DateTime>(db, $"SELECT next_hard_deadline FROM contract_renewal_cases WHERE id = '{caseId}'")));
        await Rejected(db, $"UPDATE contract_renewal_cases SET qiwa_evidence_recorded_by = '{recorder}', qiwa_evidence_verified_by = '{recorder}' WHERE id = '{caseId}'", "23514"); // four eyes
        await Sql(db, $"UPDATE contract_renewal_cases SET state = 'ReadyToApply' WHERE id = '{caseId}'");
        const string apply = "state = 'Applied', resulting_contract_id = '{0}', applied_by = gen_random_uuid(), applied_at = now(), apply_idempotency_key = 'k-1', closed_at = now()";
        await Rejected(db, $"UPDATE contract_renewal_cases SET {string.Format(apply, next.Id)} WHERE id = '{caseId}'", "23514");                 // no Qiwa evidence
        await Sql(db, $"UPDATE contract_renewal_cases SET qiwa_evidence_document_id = '{evidence.Id}', qiwa_evidence_outcome = 'Approved', qiwa_evidence_recorded_by = '{recorder}', qiwa_evidence_verified_by = gen_random_uuid() WHERE id = '{caseId}'");
        await Rejected(db, $"UPDATE contract_renewal_cases SET state = 'Applied', resulting_contract_id = '{next.Id}', applied_by = gen_random_uuid(), applied_at = now(), apply_idempotency_key = 'k-1' WHERE id = '{caseId}'", "23514"); // closed_at must match
        await Sql(db, $"UPDATE contract_renewal_cases SET {string.Format(apply, next.Id)} WHERE id = '{caseId}'");
        Assert.True(await Scalar<bool>(db, $"SELECT next_hard_deadline IS NULL FROM contract_renewal_cases WHERE id = '{caseId}'"));
        await Rejected(db, $"UPDATE contract_renewal_cases SET state = 'Open', closed_at = NULL WHERE id = '{caseId}'", "23514");                // closed is closed

        // EF reads the case: text[] round-trips and the xmin row version is populated.
        db.ChangeTracker.Clear();
        var loaded = await db.ContractRenewalCases.SingleAsync(c => c.Id == caseId);
        Assert.Equal(new[] { "RenewAsIs", "RenewWithChanges", "NonRenew" }, loaded.AllowedActions);
        Assert.NotEqual(0u, loaded.Version);
        Assert.Null(loaded.NextHardDeadline);

        // NonRenewed needs notice served by the notice date.
        await Sql(db, Case(colleagueTerm.Id).Replace($"'{mohammed.PublicId}'", $"'{colleague.PublicId}'"));
        var nr = await Scalar<Guid>(db, $"SELECT id FROM contract_renewal_cases WHERE expiring_contract_id = '{colleagueTerm.Id}'");
        await Sql(db, $"UPDATE contract_renewal_cases SET state = 'OfferInPreparation' WHERE id = '{nr}'");
        await Sql(db, $"UPDATE contract_renewal_cases SET state = 'InApproval', contract_action = 'NonRenew' WHERE id = '{nr}'");
        await Sql(db, $"UPDATE contract_renewal_cases SET state = 'QiwaPending' WHERE id = '{nr}'");
        await Sql(db, $"UPDATE contract_renewal_cases SET state = 'ReadyToApply' WHERE id = '{nr}'");
        await Rejected(db, $"UPDATE contract_renewal_cases SET state = 'NonRenewed', non_renewal_notice_served_on = '2026-12-10', non_renewal_notice_document_id = '{evidence.Id}', closed_at = now() WHERE id = '{nr}'", "23514"); // late
        await Sql(db, $"UPDATE contract_renewal_cases SET state = 'NonRenewed', non_renewal_notice_served_on = '2026-11-30', non_renewal_notice_document_id = '{evidence.Id}', non_renewal_notice_channel = 'Qiwa', non_renewal_notice_by = 'Employer', closed_at = now() WHERE id = '{nr}'");

        // ── Down refuses over evidence; with it gone, rolls back and re-applies ─────────────────
        var refused = await Assert.ThrowsAsync<PostgresException>(() => migrator.MigrateAsync(Before));
        Assert.Equal(PostgresErrorCodes.RaiseException, refused.SqlState);
        Assert.Equal(4L, await Scalar<long>(db, "SELECT count(*) FROM employee_entitlements"));

        await Sql(db, """
            ALTER TABLE employee_entitlements DISABLE TRIGGER USER;
            DELETE FROM employee_entitlements;
            DELETE FROM contract_renewal_cases;
            DELETE FROM pay_components WHERE NOT is_offered;
            UPDATE employee_contracts SET renewed_from_contract_id = NULL, renewal_number = NULL, chain_started_on = NULL, worker_nationality_class = NULL;
            UPDATE employee_salary_structures SET housing_basis = 'Amount', housing_rate = NULL;
            DELETE FROM grade_entitlements WHERE pay_component_code <> 'LOAN_PERSONAL';
            """);
        await migrator.MigrateAsync(Before);
        Assert.Equal(0L, await Scalar<long>(db, "SELECT count(*) FROM information_schema.tables WHERE table_name IN ('employee_entitlements','contract_renewal_cases')"));
        Assert.Equal(0L, await Scalar<long>(db, "SELECT count(*) FROM pg_proc WHERE proname IN ('employee_entitlements_close_only','employee_entitlements_containment','employee_contracts_entitlement_containment','contract_renewal_cases_transition_guard')"));
        Assert.Equal(0L, await Scalar<long>(db, "SELECT count(*) FROM information_schema.columns WHERE table_name = 'pay_components' AND column_name = 'is_offered'"));
        await migrator.MigrateAsync();
        Assert.Equal(1L, await Scalar<long>(db, $"SELECT count(*) FROM \"__EFMigrationsHistory\" WHERE \"MigrationId\" = '{ThisMigration}'"));
        Assert.Empty(await db.Database.GetPendingMigrationsAsync());
        Assert.Equal(4L, await Scalar<long>(db, "SELECT count(*) FROM pg_trigger WHERE tgname IN ('trg_employee_entitlements__close_only','trg_employee_entitlements__containment','trg_employee_contracts__entitlement_containment','trg_contract_renewal_cases__transition_guard')"));
    }

    /// <summary>The fixture applies exactly the list Up applies, in the same order.</summary>
    [Fact]
    public void FixtureDdl_IsTheMigrationsOwnList()
    {
        Assert.Equal(
            [RaMigration.AddEmployeePublicIdForeignKeysSql, RaMigration.AddEntitlementExclusionSql, RaMigration.AddPostgresOnlyChecksSql,
             RaMigration.CreateEntitlementCloseOnlyTriggerSql, RaMigration.CreateEntitlementContainmentTriggersSql, RaMigration.CreateRenewalTransitionGuardSql],
            RaMigration.PostgresOnlyDdl);
    }

    private static Employee Employee(Guid tenantId, Guid companyId, string code, Guid gradeId) => new()
    {
        TenantId = tenantId, CompanyId = companyId, EmployeeCode = code, FullName = $"Employee {code}", Nationality = "Egyptian",
        ContractType = "Fixed", Status = "Active", JoiningDate = new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc), GradeId = gradeId,
    };

    private static EmployeeContract Contract(Guid tenantId, Guid companyId, Guid employeePublicId, string number, string status, DateOnly start, DateOnly end) => new()
    {
        TenantId = tenantId, CompanyId = companyId, EmployeeId = employeePublicId, EmployeeName = number, ContractNumber = number, Status = status,
        StartDate = start, EndDate = end, BasicSalary = 8000m, CurrencyCode = "SAR",
    };

    private static string Cell(Guid tenantId, Guid gradeId, string code, string valueType, string? amount = null, string? rate = null,
        bool eligible = true, (string Column, string Value)? extra = null, string? quantity = null, string? basis = null)
    {
        var cols = "id,tenant_id,grade_id,pay_component_code,entitlement_class,eligible,value_type,amount,rate,quantity,nationality_basis,effective_from,created_at_utc";
        var vals = $"gen_random_uuid(),'{tenantId}','{gradeId}','{code}','Contractual',{(eligible ? "true" : "false")},{valueType},{amount ?? "NULL"},{rate ?? "NULL"},"
                   + $"{quantity ?? "NULL"},{basis ?? "NULL"},'2026-01-01',CURRENT_TIMESTAMP";
        if (extra is { } e) { cols += "," + e.Column; vals += "," + e.Value; }
        return $"INSERT INTO grade_entitlements ({cols}) VALUES ({vals})";
    }

    private static Task Sql(ZayraDbContext db, string sql) => db.Database.ExecuteSqlRawAsync(sql);

    private static async Task Rejected(ZayraDbContext db, string sql, string sqlState)
    {
        var error = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlRawAsync(sql));
        Assert.Equal(sqlState, error.SqlState);
    }

    private static async Task<T> Scalar<T>(ZayraDbContext db, string sql)
    {
        var connection = db.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open) await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (T)(await command.ExecuteScalarAsync())!;
    }
}
