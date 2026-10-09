"use client";

import { useRef } from "react";
import { SetupPolicySource } from "./SetupPolicySource";
import { useT } from "../hooks/useT";
import { msg } from "../i18n/translations";
import type {
  SetupConfiguration,
  DraftGrade,
  DraftBenefitPlan,
  DraftLeavePolicy,
  DraftAttendancePolicy,
  DraftHrConfig,
} from "../api/setupAssistant";

type Props = {
  area: "source" | "work" | "governance" | "rewards";
  value: SetupConfiguration;
  onChange: (value: SetupConfiguration) => void;
  currency: string;
  releaseA?: boolean;
};
const CAPTURE = [
  ["BiometricDevice", msg("Biometric device")],
  ["MobileGeofence", msg("Mobile app with location")],
  ["WebCheckIn", msg("Web check-in")],
  ["Manual", msg("Entered by hand")],
];
const OVERTIME = [
  ["PaidOvertime", msg("Paid overtime")],
  ["CompensatoryOff", msg("Time off in lieu")],
  ["NotApplicable", msg("No overtime policy")],
];
const ATTENDANCE: DraftAttendancePolicy = {
  code: "STD_ATT",
  name: "Standard attendance",
  graceMinutes: 15,
  lateThresholdMinutes: 20,
  earlyExitThresholdMinutes: 15,
  halfDayThresholdMinutes: 240,
  absentThresholdMinutes: 120,
  standardWorkMinutes: 480,
  breakMinutes: 60,
  roundingRule: "NearestMinute",
  requiresOvertimeApproval: true,
  allowAbsenceToLeaveConversion: false,
};
const HR: DraftHrConfig = {
  useDeptHeadApproval: true,
  useHrFinalApproval: true,
  useSupervisorBeforeManager: false,
  allowDottedLineApproval: false,
  autoCreateDeptOnImport: false,
  autoCreateDesignationOnImport: false,
  requireImportPreviewBeforeCommit: true,
  allowCrossDeptManager: false,
  allowCrossLocationManager: false,
  requireCostCenterForPayroll: true,
  requireGradeForApprovalPolicy: true,
};
const newLeave = (): DraftLeavePolicy => ({
  name: "",
  leaveTypeCode: "ANNUAL",
  annualEntitlementDays: 0,
  accrualMethod: "Monthly",
  proratePartialMonths: true,
  encashmentAllowed: true,
  encashmentMaxDays: 0,
  minimumDaysPerRequest: 1,
  maximumDaysPerRequest: 30,
  noticeRequiredDays: 7,
  weekendsIncluded: false,
  publicHolidaysIncluded: false,
  appliesOnProbation: true,
  payrollImpact: "Full",
  gradeCode: "",
  departmentCode: "",
  employmentType: "",
});

function Field({
  label,
  value,
  onChange,
  type = "text",
  min,
  max,
  step,
  required = false,
}: {
  label: string;
  value: string | number;
  onChange: (value: string) => void;
  type?: string;
  min?: number;
  max?: number;
  step?: number;
  required?: boolean;
}) {
  const t = useT();
  return (
    <label className="block min-w-0">
      <span className="mb-1.5 block text-sm font-medium">
        {t(label)}
        {required && " *"}
      </span>
      <input
        className="input w-full"
        type={type}
        value={value}
        min={min}
        max={max}
        step={step}
        required={required}
        onChange={(e) => onChange(e.target.value)}
      />
    </label>
  );
}
function Choices({
  legend,
  options,
  selected,
  onChange,
  exclusive,
}: {
  legend: string;
  options: string[][];
  selected: string[];
  onChange: (values: string[]) => void;
  exclusive?: string;
}) {
  const t = useT();
  return (
    <fieldset className="min-w-0">
      <legend className="mb-2 text-sm font-semibold">{t(legend)}</legend>
      <div className="grid gap-2 sm:grid-cols-2">
        {options.map(([key, label]) => (
          <label
            key={key}
            className="flex items-start gap-2 rounded-lg border border-slate-200 p-3 text-sm dark:border-white/10"
          >
            <input
              className="mt-0.5 h-4 w-4 accent-sapphire"
              type="checkbox"
              checked={selected.includes(key)}
              onChange={(e) =>
                onChange(
                  e.target.checked
                    ? key === exclusive
                      ? [key]
                      : [...selected.filter((v) => v !== exclusive), key]
                    : selected.filter((v) => v !== key),
                )
              }
            />
            {t(label)}
          </label>
        ))}
      </div>
    </fieldset>
  );
}
export function SetupPolicyEditor({
  area,
  value,
  onChange,
  currency,
  releaseA,
}: Props) {
  const t = useT();
  const manualGradeDraft = useRef<DraftGrade[]>([]);
  const update = (patch: Partial<SetupConfiguration>) =>
    onChange({ ...value, ...patch });
  const editGrade = (index: number, patch: Partial<DraftGrade>) =>
    update({
      grades: value.grades?.map((g, i) =>
        i === index ? { ...g, ...patch } : g,
      ),
    });
  const editBenefit = (index: number, patch: Partial<DraftBenefitPlan>) =>
    update({
      benefitPlans: value.benefitPlans?.map((b, i) =>
        i === index ? { ...b, ...patch } : b,
      ),
    });
  const editLeave = (index: number, patch: Partial<DraftLeavePolicy>) =>
    update({
      leavePolicies: value.leavePolicies?.map((p, i) =>
        i === index ? { ...p, ...patch } : p,
      ),
    });
  if (area === "source")
    return <SetupPolicySource value={value} onChange={onChange} />;
  if (area === "work")
    return (
      <div className="space-y-6">
        <Choices
          legend={msg("How is time recorded?")}
          options={CAPTURE}
          selected={value.attendanceMethods ?? ["WebCheckIn"]}
          onChange={(attendanceMethods) => update({ attendanceMethods })}
        />
        <p className="text-xs text-slate-600 dark:text-slate-400">
          {t(
            "Select every method you use. Device connections and location geofences are configured in Attendance after setup.",
          )}
        </p>
        <details className="rounded-lg border border-slate-200 p-4 dark:border-white/10">
          <summary className="cursor-pointer text-sm font-semibold">
            {t("Custom attendance rules")}
          </summary>
          <label className="my-3 flex gap-2 text-sm">
            <input
              type="checkbox"
              checked={!!value.attendancePolicy}
              onChange={(e) =>
                update({
                  attendancePolicy: e.target.checked
                    ? { ...ATTENDANCE }
                    : undefined,
                })
              }
            />
            {t("Use my company attendance policy")}
          </label>
          {value.attendancePolicy && (
            <div className="grid gap-4 sm:grid-cols-2">
              {(
                [
                  ["graceMinutes", msg("Grace period (minutes)")],
                  ["lateThresholdMinutes", msg("Late threshold (minutes)")],
                  [
                    "earlyExitThresholdMinutes",
                    msg("Early exit threshold (minutes)"),
                  ],
                  ["standardWorkMinutes", msg("Working day (minutes)")],
                  ["breakMinutes", msg("Break (minutes)")],
                  [
                    "halfDayThresholdMinutes",
                    msg("Half-day threshold (minutes)"),
                  ],
                  [
                    "absentThresholdMinutes",
                    msg("Absence threshold (minutes)"),
                  ],
                ] as const
              ).map(([key, label]) => (
                <Field
                  key={key}
                  label={label}
                  value={value.attendancePolicy![key]}
                  type="number"
                  min={0}
                  max={1440}
                  onChange={(v) =>
                    update({
                      attendancePolicy: {
                        ...value.attendancePolicy!,
                        [key]: Number(v),
                      },
                    })
                  }
                />
              ))}
            </div>
          )}
        </details>
        <Choices
          legend={msg("Overtime")}
          options={OVERTIME}
          selected={value.overtimeModes ?? ["PaidOvertime"]}
          exclusive="NotApplicable"
          onChange={(overtimeModes) => update({ overtimeModes })}
        />
        <p className="text-xs text-slate-600 dark:text-slate-400">
          {t(
            "Paid overtime and time off can coexist. Review eligibility, employee consent and country-specific calculation rules before applying. Custom rates and limits are editable in the generated overtime policy.",
          )}
        </p>
        <section className="space-y-3 border-t border-slate-200 pt-5 dark:border-white/10">
          <h3 className="text-base font-semibold">
            {t("Leave year and accrual")}
          </h3>
          <p className="text-sm text-slate-600 dark:text-slate-400">
            {t(
              "The balance year is January–December. Monthly accrual starts from the joining month; partial months can be prorated. A June joiner is not automatically given a full annual balance.",
            )}
          </p>
          <p className="text-xs text-slate-500">
            {t(
              "Anniversary and custom leave years require balance-period support and cannot be activated by this setup yet.",
            )}
          </p>
          <label className="flex gap-2 text-sm">
            <input
              type="checkbox"
              checked={value.leavePolicies != null}
              onChange={(e) =>
                update({
                  leavePolicies: e.target.checked ? [newLeave()] : null,
                })
              }
            />
            {t("Configure leave entitlements from company policy")}
          </label>
          {value.leavePolicies?.map((p, i) => (
            <fieldset
              key={i}
              className="min-w-0 space-y-4 rounded-lg border border-slate-200 p-4 dark:border-white/10"
            >
              <legend className="px-1 text-sm font-semibold">
                {t("Leave policy {number}", { number: i + 1 })}
              </legend>
              <div className="grid gap-4 sm:grid-cols-2">
                <Field
                  label={msg("Policy name")}
                  value={p.name}
                  required
                  onChange={(name) => editLeave(i, { name })}
                />
                <Field
                  label={msg("Leave type code")}
                  value={p.leaveTypeCode}
                  required
                  onChange={(leaveTypeCode) => editLeave(i, { leaveTypeCode })}
                />
                <Field
                  label={msg("Annual entitlement (days)")}
                  value={p.annualEntitlementDays}
                  type="number"
                  min={0}
                  max={365}
                  step={0.5}
                  onChange={(v) =>
                    editLeave(i, { annualEntitlementDays: Number(v) })
                  }
                />
                <label className="block">
                  <span className="mb-1.5 block text-sm font-medium">
                    {t("Accrual")}
                  </span>
                  <select
                    className="select w-full"
                    value={p.accrualMethod}
                    onChange={(e) =>
                      editLeave(i, {
                        accrualMethod: e.target.value,
                        proratePartialMonths:
                          e.target.value === "Monthly" &&
                          p.proratePartialMonths,
                      })
                    }
                  >
                    <option value="Monthly">{t("Monthly accrual")}</option>
                    <option value="Yearly">{t("Yearly allocation")}</option>
                  </select>
                </label>
                <Field
                  label={msg("Grade code (optional)")}
                  value={p.gradeCode ?? ""}
                  onChange={(gradeCode) => editLeave(i, { gradeCode })}
                />
                <Field
                  label={msg("Department code (optional)")}
                  value={p.departmentCode ?? ""}
                  onChange={(departmentCode) =>
                    editLeave(i, { departmentCode })
                  }
                />
                <Field
                  label={msg("Employment type (optional)")}
                  value={p.employmentType ?? ""}
                  onChange={(employmentType) =>
                    editLeave(i, { employmentType })
                  }
                />
              </div>
              <label className="flex gap-2 text-sm">
                <input
                  type="checkbox"
                  disabled={p.accrualMethod !== "Monthly"}
                  checked={p.proratePartialMonths ?? false}
                  onChange={(e) =>
                    editLeave(i, { proratePartialMonths: e.target.checked })
                  }
                />
                {t("Prorate partial months by calendar days employed")}
              </label>
              {p.accrualMethod === "Yearly" && (
                <p className="text-xs text-slate-500">
                  {t(
                    "For annual leave, yearly allocation needs an explicit balance adjustment in Leave Administration.",
                  )}
                </p>
              )}
              <p className="text-xs text-slate-500">
                {t(
                  "Blank eligibility fields apply to everyone in this company. Separate rows define policies for different employee groups.",
                )}
              </p>
              <button
                type="button"
                className="btn-secondary"
                onClick={() =>
                  update({
                    leavePolicies: value.leavePolicies?.filter(
                      (_, n) => n !== i,
                    ),
                  })
                }
              >
                {t("Remove leave policy")}
              </button>
            </fieldset>
          ))}
          {value.leavePolicies != null && (
            <button
              type="button"
              className="btn-secondary"
              onClick={() =>
                update({ leavePolicies: [...value.leavePolicies!, newLeave()] })
              }
            >
              {t("Add leave policy")}
            </button>
          )}
        </section>
      </div>
    );
  if (area === "governance")
    return (
      <section className="mt-5 space-y-3 border-t border-slate-200 pt-5 dark:border-white/10">
        <h3 className="text-base font-semibold">
          {t("Custom management and approval preferences")}
        </h3>
        <p className="text-xs text-slate-600 dark:text-slate-400">
          {t(
            "These preferences are saved for reference. Configure active routing and approver authority in Approval workflows.",
          )}
        </p>
        <label className="flex gap-2 text-sm">
          <input
            type="checkbox"
            checked={!!value.hrConfig}
            onChange={(e) =>
              update({ hrConfig: e.target.checked ? { ...HR } : undefined })
            }
          />
          {t("Customize management preferences")}
        </label>
        {value.hrConfig && (
          <div className="grid gap-3 sm:grid-cols-2">
            {(
              [
                [
                  "useSupervisorBeforeManager",
                  msg("Supervisor before manager"),
                ],
                ["useDeptHeadApproval", msg("Department-head review")],
                ["useHrFinalApproval", msg("HR final review")],
                ["allowDottedLineApproval", msg("Dotted-line manager review")],
                ["allowCrossDeptManager", msg("Managers across departments")],
                ["allowCrossLocationManager", msg("Managers across locations")],
              ] as const
            ).map(([key, label]) => (
              <label key={key} className="flex gap-2 text-sm">
                <input
                  type="checkbox"
                  checked={value.hrConfig![key]}
                  onChange={(e) =>
                    update({
                      hrConfig: { ...value.hrConfig!, [key]: e.target.checked },
                    })
                  }
                />
                {t(label)}
              </label>
            ))}
          </div>
        )}
      </section>
    );
  return (
    <div className="space-y-7">
      <section className="space-y-4">
        <h3 className="text-base font-semibold">{t("Salary grades")}</h3>
        <label className="block">
          <span className="mb-1.5 block text-sm font-medium">
            {t("How would you like to build your grades?")}
          </span>
          <select
            className="select w-full"
            value={value.grades == null ? "assisted" : "manual"}
            onChange={(e) => {
              if (e.target.value === "assisted") {
                manualGradeDraft.current = value.grades ?? [];
                update({ grades: null });
              } else {
                update({ grades: manualGradeDraft.current });
              }
            }}
          >
            <option value="assisted">
              {t("AI-assisted draft — review and edit every grade")}
            </option>
            <option value="manual">
              {t("Enter my existing salary grades")}
            </option>
          </select>
        </label>
        <p className="text-xs text-slate-600 dark:text-slate-400">
          {t(
            "Create as many grades as your policy requires. Salary amounts use the company currency. AI suggestions are drafts, not salary-market benchmarks.",
          )}
        </p>
        {value.grades?.map((g, i) => (
          <fieldset
            key={i}
            className="min-w-0 rounded-lg border border-slate-200 p-4 dark:border-white/10"
          >
            <legend className="px-1 text-sm font-semibold">
              {t("Grade {number}", { number: i + 1 })}
            </legend>
            <div className="grid gap-4 sm:grid-cols-2 xl:grid-cols-3">
              <Field
                label={msg("Grade code")}
                value={g.code}
                required
                onChange={(code) => editGrade(i, { code })}
              />
              <Field
                label={msg("Grade name")}
                value={g.name}
                required
                onChange={(name) => editGrade(i, { name })}
              />
              <Field
                label={msg("Band")}
                value={g.band}
                onChange={(band) => editGrade(i, { band })}
              />
              {(
                [
                  ["minSalary", msg("Minimum salary")],
                  ["midSalary", msg("Midpoint salary")],
                  ["maxSalary", msg("Maximum salary")],
                  ["level", msg("Level")],
                ] as const
              ).map(([key, label]) => (
                <Field
                  key={key}
                  label={label}
                  value={g[key]}
                  type="number"
                  min={0}
                  step={key === "level" ? 1 : 0.01}
                  onChange={(v) => editGrade(i, { [key]: Number(v) })}
                />
              ))}
            </div>
            <button
              type="button"
              className="btn-secondary mt-4"
              onClick={() =>
                update({ grades: value.grades?.filter((_, n) => n !== i) })
              }
            >
              {t("Remove grade")}
            </button>
          </fieldset>
        ))}
        {value.grades != null && (
          <button
            type="button"
            className="btn-secondary"
            onClick={() =>
              update({
                grades: [
                  ...value.grades!,
                  {
                    code: "",
                    name: "",
                    band: "",
                    level: value.grades!.length + 1,
                    minSalary: 0,
                    midSalary: 0,
                    maxSalary: 0,
                    currency,
                  },
                ],
              })
            }
          >
            {t("Add salary grade")}
          </button>
        )}
      </section>
      <section className="space-y-4 border-t border-slate-200 pt-5 dark:border-white/10">
        <h3 className="text-base font-semibold">{t("Benefits")}</h3>
        <p className="text-sm text-slate-600 dark:text-slate-400">
          {t(
            "Create the benefit plans your company offers, with effective dates and grade eligibility. Employee enrollment and contribution amounts are configured in Benefits after setup.",
          )}
        </p>
        {value.benefitPlans?.map((b, i) => (
          <fieldset
            key={i}
            className="min-w-0 rounded-lg border border-slate-200 p-4 dark:border-white/10"
          >
            <legend className="px-1 text-sm font-semibold">
              {t("Benefit {number}", { number: i + 1 })}
            </legend>
            <div className="grid gap-4 sm:grid-cols-2">
              <Field
                label={msg("Benefit code")}
                value={b.code}
                required
                onChange={(code) => editBenefit(i, { code })}
              />
              <Field
                label={msg("Benefit name")}
                value={b.name}
                required
                onChange={(name) => editBenefit(i, { name })}
              />
              <Field
                label={msg("Benefit type")}
                value={b.planType}
                required
                onChange={(planType) => editBenefit(i, { planType })}
              />
              <Field
                label={msg("Effective from")}
                value={b.effectiveFrom}
                type="date"
                required
                onChange={(effectiveFrom) => editBenefit(i, { effectiveFrom })}
              />
              <Field
                label={msg("Effective until (optional)")}
                value={b.effectiveTo ?? ""}
                type="date"
                onChange={(effectiveTo) =>
                  editBenefit(i, { effectiveTo: effectiveTo || null })
                }
              />
              {!releaseA && (
                <Field
                  label={msg(
                    "Eligible grade codes (comma separated; blank for all)",
                  )}
                  value={b.gradeCodes.join(", ")}
                  onChange={(v) =>
                    editBenefit(i, {
                      gradeCodes: v.split(",").map((s) => s.trim()),
                    })
                  }
                />
              )}
            </div>
            <label className="mt-4 flex gap-2 text-sm">
              <input
                type="checkbox"
                checked={b.requiresEnrollment}
                onChange={(e) =>
                  editBenefit(i, { requiresEnrollment: e.target.checked })
                }
              />
              {t("Enrollment required")}
            </label>
            <button
              type="button"
              className="btn-secondary mt-4"
              onClick={() =>
                update({
                  benefitPlans: value.benefitPlans?.filter((_, n) => n !== i),
                })
              }
            >
              {t("Remove benefit")}
            </button>
          </fieldset>
        ))}
        {releaseA && (
          <p className="text-xs text-slate-500">
            {t(
              "Grade benefit packages are managed in Benefits by grade. This step creates the benefit plan catalog.",
            )}
          </p>
        )}
        <button
          type="button"
          className="btn-secondary"
          onClick={() =>
            update({
              benefitPlans: [
                ...(value.benefitPlans ?? []),
                {
                  code: "",
                  name: "",
                  planType: "Medical",
                  currency,
                  effectiveFrom: "",
                  effectiveTo: null,
                  requiresEnrollment: true,
                  gradeCodes: [],
                },
              ],
            })
          }
        >
          {t("Add benefit plan")}
        </button>
      </section>
    </div>
  );
}
