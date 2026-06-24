-- ─────────────────────────────────────────────────────────────────────────────
-- neon_truncate.sql — Clean-slate wipe of ALL tenant-scoped data
-- ─────────────────────────────────────────────────────────────────────────────
-- Run this ONLY after:
--   1. Creating a Neon branch snapshot (Dashboard → Branch → Create branch)
--   2. Confirming no production tenant data exists
--
-- Preserves (NOT truncated):
--   platform_users            — platform admin accounts
--   platform_*                — platform-level tables
--   pricing_config            — re-seeded by DemoDataSeeder only when SEED_DEMO_DATA=true
--   pricing_module_configs    — same
--
-- Auto-reseeded on next deploy (always-on seeders):
--   gosi_contribution_rules   — GosiRuleSeeder (always runs)
--   statutory_rules           — StatutoryRuleSeeder (always runs)
--   roles / permissions       — AuthSeeder.EnsureTenantRolesAsync (per tenant, on seed)
--
-- After running: deploy with SEED_DEMO_DATA=false to seed ONE clean KSA tenant.
-- ─────────────────────────────────────────────────────────────────────────────

BEGIN;

-- Disable FK enforcement so order of truncation doesn't matter
SET session_replication_role = 'replica';

TRUNCATE TABLE
  -- Absence / Attendance
  absence_records,
  absence_regularization_requests,
  attendance_ai_insights,
  attendance_audit_logs,
  attendance_correction_approvals,
  attendance_daily_records,
  attendance_device_connectors,
  attendance_device_sync_logs,
  attendance_devices,
  attendance_exceptions,
  attendance_geofences,
  attendance_import_batches,
  attendance_import_errors,
  attendance_locations,
  attendance_lock_periods,
  attendance_payroll_impacts,
  attendance_policies,
  attendance_raw_events,
  attendance_records,
  attendance_regularization_requests,
  attendance_rules,

  -- AI / Insights
  ai_hr_query_cache,
  ai_hr_query_logs,
  ai_insights,
  ai_model_configs,
  ai_recommendations,

  -- Approvals
  approval_authorities,
  approval_decisions,
  approval_delegations,
  approval_policies,
  approval_policy_steps,
  approval_requests,
  approval_workflow_steps,
  approval_workflows,

  -- Assessments / Recruitment
  assessment_questions,
  assessment_templates,
  application_events,
  candidate_ai_scores,
  candidate_assessments,
  candidate_documents,
  candidates,
  interview_schedules,
  job_applications,
  job_openings,
  manpower_requisitions,
  offer_approvals,
  offer_letters,
  recruitment_audit_logs,
  resume_parse_results,

  -- Audit
  admin_audit_logs,
  audit_logs,

  -- Bonuses / Advances / Loans
  advance_approvals,
  advance_audit_logs,
  advance_installments,
  advance_policies,
  bonus_approvals,
  bonus_audit_logs,
  bonus_batches,
  bonus_recommendations,
  bonus_types,
  employee_bonuses,
  employee_loans,
  loan_approvals,
  loan_audit_logs,
  loan_installments,
  loan_policies,
  loan_settlements,
  loan_types,
  salary_advances,

  -- Compliance / Visa / Passport
  compliance_ai_insights,
  compliance_audit_logs,
  compliance_reminders,
  compliance_renewals,
  compliance_requirements,
  employee_compliance_records,
  gcc_compliance_settings,
  passport_records,
  visa_records,
  work_permit_records,
  qiwa_api_credentials,
  "QiwaSyncLogs",

  -- Core Org
  branches,
  companies,
  cost_centers,
  departments,
  designations,
  employees,
  grades,
  locations,
  reporting_lines,

  -- Documents / Letters
  contract_templates,
  doc_types,
  document_chunks,
  employee_contracts,
  employee_document_requests,
  employee_document_versions,
  employee_documents,
  policy_documents,

  -- Employee misc
  burnout_risk_signals,
  comp_off_credits,
  employee_action_items,
  employee_ai_query_logs,
  employee_announcements,
  employee_change_requests,
  employee_churn_predictions,
  employee_dependents,
  employee_drafts,
  employee_histories,
  employee_id_rules,
  employee_mobile_devices,
  employee_notification_preferences,
  employee_notifications,
  employee_payslip_access_logs,
  employee_policy_acknowledgements,
  employee_profile_change_requests,
  employee_risk_scores,
  employee_self_service_audit_logs,
  employee_sentiment_pulses,
  employee_status_histories,
  employee_transfer_requests,
  employee_user_accounts,
  ess_dashboard_preferences,

  -- ESS / HR Requests
  hr_request_attachments,
  hr_request_categories,
  hr_request_comments,
  hr_request_slas,
  hr_requests,

  -- Finance / GL
  eosb_calculations,
  finance_gl_entries,
  fiscal_years,

  -- Leave
  leave_accrual_rules,
  leave_ai_insights,
  leave_approvals,
  leave_attachments,
  leave_audit_logs,
  leave_balance_transactions,
  leave_blackout_dates,
  leave_delegations,
  leave_encashment_requests,
  leave_payroll_impacts,
  leave_policies,
  leave_policy_eligibilities,
  leave_request_dates,
  leave_requests,
  leave_types,
  employee_leave_balances,
  public_holiday_calendars,
  public_holidays,
  leave_blackout_dates,

  -- Master data
  master_data_types,
  master_data_values,
  country_payroll_rules,

  -- Notifications / Misc
  notification_templates,
  notifications,
  numbering_rules,
  onboarding_checklists,
  onboarding_tasks,
  saved_reports,
  report_execution_logs,
  report_schedules,
  system_settings,
  security_settings,
  workforce_plans,

  -- Overtime
  overtime_approvals,
  overtime_audit_logs,
  overtime_budgets,
  overtime_calculations,
  overtime_multipliers,
  overtime_payroll_impacts,
  overtime_policies,
  overtime_requests,
  overtime_rules,
  overtime_types,

  -- Payroll
  bank_transfer_files,
  employee_payroll_profiles,
  employee_salary_structures,
  payroll_adjustments,
  payroll_ai_validation_results,
  payroll_approvals,
  payroll_audit_logs,
  payroll_cycles,
  payroll_deductions,
  payroll_earnings,
  payroll_exceptions,
  payroll_groups,
  payroll_payment_batches,
  payroll_payment_records,
  payroll_run_employees,
  payroll_runs,
  payroll_slips,
  payroll_validation_results,
  payslip_components,
  payslip_templates,
  payslips,
  salary_components,
  salary_structures,
  sif_file_records,
  wps_file_batches,

  -- Performance
  appraisal_appeals,
  appraisal_calibrations,
  appraisal_competency_ratings,
  appraisal_reviews,
  appraisal_score_breakdowns,
  competencies,
  continuous_feedback,
  employee_goals,
  feedback_360,
  goal_progress_updates,
  increment_recommendations,
  performance_audit_logs,
  performance_cycle_employees,
  performance_cycles,
  performance_improvement_plans,
  performance_rating_options,
  performance_rating_scales,
  performance_scorecard_templates,
  pip_check_ins,
  probation_reviews,
  promotion_recommendations,
  role_competencies,

  -- Pricing / Quotes (these ARE tenant-linked data; re-seed if needed)
  pricing_quotes,

  -- Shifts
  shift_assignments,
  shift_definitions,

  -- Tenants / Auth / Users
  gosi_contribution_rules,
  login_activity,
  mfa_challenge_tokens,
  password_reset_tokens,
  permission_grantor_records,
  permissions,
  refresh_tokens,
  role_permissions,
  roles,
  statutory_rules,
  tenant_brandings,
  tenant_feature_flags,
  tenant_field_help_texts,
  tenant_hr_configs,
  tenant_invoice_lines,
  tenant_invoices,
  tenant_localization_settings,
  tenant_payments,
  tenant_subscriptions,
  tenants,
  user_entity_accesses,
  user_permission_overrides,
  user_roles,
  users;

-- Re-enable FK enforcement
SET session_replication_role = 'origin';

COMMIT;

-- Sanity checks — expected: 0 tenants, 0 employees, platform_users unchanged
SELECT 'tenants'       AS tbl, COUNT(*) AS rows FROM tenants
UNION ALL
SELECT 'employees',          COUNT(*) FROM employees
UNION ALL
SELECT 'platform_users',     COUNT(*) FROM platform_users
UNION ALL
SELECT 'pricing_config',     COUNT(*) FROM pricing_config
UNION ALL
SELECT 'pricing_module_configs', COUNT(*) FROM pricing_module_configs;
