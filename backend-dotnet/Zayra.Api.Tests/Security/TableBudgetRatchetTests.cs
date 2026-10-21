using FluentAssertions;

namespace Zayra.Api.Tests.Security;

/// <summary>
/// TABLE-BUDGET RATCHET — the set of tables is pinned in <c>table-budget.txt</c>, and a new one
/// fails the build until that file is edited in the same change.
///
/// <para><b>Why this exists.</b> 323 tables were not one decision. They were 323 instances of "this
/// feature needs a table", each individually reasonable, none reviewed against the whole. The result
/// was 14 audit tables, 17 approval tables, payroll results spread over 6, and 40 tables no code ever
/// reads. Nothing in the build ever asked "should this be a table?", so the answer was always yes.</para>
///
/// <para><b>What it does NOT do.</b> It does not forbid new tables. Some features genuinely need one.
/// It makes adding one a visible, reviewable act: a line in a manifest, in the diff, with a human
/// deciding. The cost is one line; the thing it buys is that nobody adds the 324th by accident.</para>
///
/// <para><b>THE RULE FOR NEW CODE.</b> Before adding a <c>DbSet</c>, answer three questions in the PR:
/// which capability needs it and why no existing table can carry the fact; who owns the data; how long
/// it is kept. If you cannot answer all three, the fact belongs on a table that already exists.
/// See <c>.claude/skills/kynexone-schema/SKILL.md</c> and <c>docs/schema/ANTI_PATTERNS.md</c>.</para>
///
/// <para><b>Removals are welcome</b> and also edit the manifest — the count is expected to fall sharply
/// when the 76-table rebuild lands. This guard is about deliberate change in either direction, not about
/// a number that may only go down.</para>
/// </summary>
public class TableBudgetRatchetTests
{
    private static readonly string ManifestPath = TestPath("Security/table-budget.txt");
    private static readonly string DbContextPath = TestPath("../Zayra.Api/Data/ZayraDbContext.cs");

    [Fact]
    public void EveryDbSet_IsInTheManifest_AndEveryManifestEntry_Exists()
    {
        var declared = DeclaredDbSets();
        var pinned = File.ReadAllLines(ManifestPath)
            .Select(l => l.Trim())
            .Where(l => l.Length > 0 && !l.StartsWith('#'))
            .ToHashSet(StringComparer.Ordinal);

        var added = declared.Except(pinned).OrderBy(n => n, StringComparer.Ordinal).ToList();
        var removed = pinned.Except(declared).OrderBy(n => n, StringComparer.Ordinal).ToList();

        added.Should().BeEmpty(
            "a new table is a design decision, not a detail. Before adding {0} to " +
            "Zayra.Api.Tests/Security/table-budget.txt, answer in the PR: which capability needs it and " +
            "why no existing table can carry the fact; who owns the data; how long it is kept. " +
            "323 tables arrived one reasonable-looking table at a time — see docs/schema/ANTI_PATTERNS.md",
            string.Join(", ", added));

        removed.Should().BeEmpty(
            "{0} was removed from the model but is still pinned in table-budget.txt. Deleting a table is " +
            "good; delete its line in the same change so the manifest stays the truth",
            string.Join(", ", removed));
    }

    /// <summary>
    /// The count, stated once and in the open. A test that only compares sets would let a change that
    /// adds one table and removes another pass without anyone noticing the shape shifted.
    /// </summary>
    [Fact]
    public void TheTableCount_IsWhatWeThinkItIs()
    {
        // Separate loan payments require a batch authorization record, frozen payment
        // instructions, and referenced receipts. Finance owns these monetary records;
        // retention follows the loan ledger (84 months). Payroll batches cannot carry
        // them without reintroducing the payroll dependency this workflow removes.
        // LoanChangeRequest captures maker/checker authorization for policy exceptions,
        // rescheduling and monetary reversals, not a replacement for the immutable ledger.
        // HR/Finance own it, with the same 84-month loan-record retention requirement.
        // GradeEntitlements (slice L1): per-grade entitlement cells keyed by pay component — first used for
        // loan limits by grade, and the home for later grade entitlements. No existing table carries an
        // effective-dated, per-grade, per-component value with a no-overlap guarantee. HR/Finance own it;
        // retained with the loan ledger (84 months), since loans keep a witness id into it.
        // Release A (R0, rev 8.3.2 — the only two new tables of the release):
        // EmployeeEntitlements: the Contractual package frozen per contract term (Art. 59 acquired rights); the salary
        // row may hold only QiwaWage cash and a grade cell is today's standard, not a signed term, so no existing table
        // can carry it. HR owns it; retained as labour-law evidence (class S, with employee_contracts).
        // ContractRenewalCases: one review per expiring term (precedent final_settlements) — offer, approvals, employee
        // acceptance, Qiwa evidence and Apply; no existing table holds a case with these legal deadlines. HR owns it;
        // retained as labour-law evidence (class S).
        // Selfie attendance v2 (migration 20261008000500 — the only two tables of the feature):
        // AttendanceEvidence: one uploaded selfie's envelope — opaque single-use evidence id, owner, SHA-256, expiry, the
        // punch that used it and its purge state. No existing table holds a single-use token with its own purge lifecycle
        // (employee_documents are kept HR records; attendance_raw_events.photo_reference is a free device string). HR owns
        // it; class E — blob purged by the selfie retention rule, envelope and sha256 kept.
        // BiometricConsents: per-version consent to selfie attendance with withdrawal; nothing records consent today. HR
        // owns it (DPO approves retention); kept for employment + the statutory minimum as the lawful basis.
        // ErpApiKeys (wave 3, ERP machine access): the client's ERP pulls and confirms journal exports with a
        // scoped, expiring, revocable key. No table holds a machine credential with scopes, expiry, an address
        // allow-list and a revocation record (SCIM has one unscoped token per tenant; device keys are attendance
        // only). Finance owns it; kept as long as the exports it confirmed, whose stamps carry its id as actor.
        // EosbProvisionSnapshots + GlAdjustmentRequests (POD-C2, owner-approved): the per-employee EOSB obligation
        // witness behind the monthly provision, and one maker-checker table for the EOSB true-up and the legacy
        // 2104/2105 reclass. Finance owns both; kept as long as the GL lines they explain.
        // CMP merit planning (AddMeritPlanning, +8; cap 7 + one written exception — CMP_DESIGN.md): CompCycles (a planning
        // round: period, effective date, currency, eligibility, lifecycle), CompCycleBudgets (per-department pool in
        // cycle currency), CompGuidelines (rating × compa-ratio band → min/target/max %), CompFxRates (the product's first
        // FX table: rate, source, date, entered by), CompWorksheets (the approval subject: manager × scenario, status,
        // revision — the exception), CompWorksheetLines (the award and what the approver saw; one applied award per
        // cycle × employee), PayBenchmarkSets (customer-owned survey import with provenance and licence) and
        // PayBenchmarkRows (percentiles + designation/grade mapping). HR owns all eight; Finance reads budgets. Kept as
        // long as the salary rows the awards produced. No existing table holds any of these facts.
        // ENG — engagement surveys (AddEngagementSurveys, ENG cap 7, six used; the orphan employee_sentiment_pulses is
        // left in place — no DROP in this release). SurveyTemplates: versioned questionnaires, published versions frozen.
        // Surveys: one cycle (mode, audience, dates, k, recurrence). SurveyInvitations: the participation ledger (who was
        // invited, submitted flag/date, encrypted draft) — never an answer. SurveyResponses + SurveyAnswers: the
        // anonymous side, no identity column, date-truncated, cohort snapshot (EngagementPrivacyRatchetTests).
        // SurveyActions: follow-up actions with owner, due date and progress. HR owns all six; responses/answers are
        // deleted with their survey, comment text purged after the tenant's retention (EngagementFreeTextRetentionRule).
        // LRN — learning management (capability closure LRN-01..15; owner L&D/HR; LRN_DESIGN.md has the per-table
        // template). Ten tables, the module's whole cap: LearningCourses (immutable course versions + question bank),
        // LearningPaths + LearningPathItems (curricula, order, prerequisites, eligibility), LearningAssignmentRules
        // (standing rules the sweep re-applies), LearningAssignments (employee × path × cycle; one live row per pair,
        // DB-enforced), LearningEnrollments (per course, pinned to a version), LearningAttempts (scored submissions),
        // LearningCredentials (certificates with expiry, revocation and a supersede chain), LearningLaunchSessions
        // (cmi5 one-time fetch / auth token hashes) and LearningXapiStatements (the minimal LRS, unique per statement
        // id). No existing table holds learning content, completion evidence or a qualification lifecycle. Retention:
        // kept with the employee's training record (FK Restrict; never hard-deleted — cancel/revoke are statuses).
        // SUC — succession and career development (capability closure SUC-01..09; owner HR; SUC_IMPLEMENTATION.md has the
        // per-table template). Seven tables, the module's whole cap: SuccessionCriticalPositions (criticality side table on
        // Position), TalentPools, SuccessionNominations (candidate × one target, readiness history), TalentAssessments
        // (potential + performance snapshot, generated 9-box cell, immutable once calibrated), SuccessionSlateVersions
        // (immutable hashed snapshots, approval-routed), DevelopmentPlans and DevelopmentPlanActions (linked to
        // learning_assignments / EmployeeGoal). Retention: 3 years after employment ends, then anonymised (SuccessionRetentionJob).
        // OFR — managed offer lifecycle (docs/superpowers/plans/2026-10-19-offer-lifecycle.md; owner HR / recruitment).
        // Seven tables, 366 -> 373. offer_letters keeps one row per revision, but nothing existing could hold:
        // OfferSeries (revision numbering per application), OfferLifecycleEvents (immutable who/why/when history,
        // trigger-guarded), OfferArtifacts (frozen PDF hash, size and storage key: proof of exactly what was issued),
        // OfferDispatches + OfferDispatchAttempts (idempotent, leased email delivery and per-attempt outcome evidence),
        // OfferCompanyPolicies (per-company currency precision and required pay components) and OfferLetterTemplates
        // (reusable letter designs). Retention: kept with the job application; removed by whole-tenant erasure
        // (OfferArtifactErasure). No per-candidate retention job yet — P2 follow-up.
        // Create-company wizard (AddCompanySetupDrafts; docs/design/company-setup-wizard.md Decision 9; owner HR Core).
        // One table, 373 -> 374. CompanySetupDrafts: a group admin's half-finished company setup, server-side so it
        // survives sign-out and devices, and the commit's idempotency witness (status + result_company_id). No existing
        // table can hold it: every organisation table is a business row and a draft must never write one. Retention:
        // Open drafts idle 90 days are deleted (CompanySetupDraftRetentionRule); Submitted/Discarded kept for audit.
        DeclaredDbSets().Should().HaveCount(374,
            "the live schema is 328 tables and the approved rebuild target is 76 " +
            "(TARGET_SCHEMA.md). If this number moved, say so in the PR and update it here");
    }

    private static HashSet<string> DeclaredDbSets()
    {
        var source = File.ReadAllText(DbContextPath);
        return System.Text.RegularExpressions.Regex
            .Matches(source, @"public\s+DbSet<[A-Za-z0-9_.]+>\s+(?<name>[A-Za-z0-9_]+)")
            .Select(m => m.Groups["name"].Value)
            .ToHashSet(StringComparer.Ordinal);
    }

    private static string TestPath(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Zayra.Api.Tests.csproj")))
            dir = dir.Parent;
        dir.Should().NotBeNull("the test project root must be findable from the test binary");
        return Path.GetFullPath(Path.Combine(dir!.FullName, relative));
    }
}
