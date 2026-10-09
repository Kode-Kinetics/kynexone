"use client";

import { useEffect, useId, useRef, useState } from "react";
import { FileText } from "lucide-react";
import { useT } from "../hooks/useT";
import { msg } from "../i18n/translations";
import type { SetupConfiguration } from "../api/setupAssistant";

const steps = [
  msg("Add policy text"),
  msg("Choose AI assistance"),
  msg("Build and review"),
];

export function SetupPolicySource({
  value,
  onChange,
}: {
  value: SetupConfiguration;
  onChange: (value: SetupConfiguration) => void;
}) {
  const t = useT();
  const [open, setOpen] = useState(false);
  const [step, setStep] = useState(0);
  const [fileError, setFileError] = useState("");
  const [reading, setReading] = useState(false);
  const panelId = useId();
  const heading = useRef<HTMLHeadingElement>(null);
  const trigger = useRef<HTMLButtonElement>(null);
  const revision = useRef(0);
  const latest = useRef({ value, onChange });
  latest.current = { value, onChange };
  useEffect(
    () => () => {
      revision.current++;
    },
    [],
  );
  const text = value.policySourceText ?? "";
  const update = (patch: Partial<SetupConfiguration>) =>
    onChange({ ...value, ...patch });
  const close = () => {
    setOpen(false);
    requestAnimationFrame(() =>
      trigger.current?.focus({ preventScroll: true }),
    );
  };
  const show = (next: number) => {
    setStep(next);
    setOpen(true);
    requestAnimationFrame(() =>
      heading.current?.focus({ preventScroll: true }),
    );
  };

  return (
    <section
      className="mb-6 rounded-xl border border-sapphire/30 bg-sapphire/5 p-4 dark:border-blue-400/30 dark:bg-blue-400/5 sm:p-5"
      aria-label={t("Existing HR policy")}
    >
      <div className="flex flex-col gap-4 xl:flex-row xl:items-center xl:justify-between">
        <div className="flex items-start gap-3">
          <FileText
            className="mt-0.5 h-5 w-5 shrink-0 text-sapphire dark:text-blue-300"
            aria-hidden="true"
          />
          <div>
            <h3 className="text-base font-semibold text-slate-950 dark:text-white">
              {t("Start from your existing HR policy")}
            </h3>
            <p className="mt-1 text-sm leading-5 text-slate-600 dark:text-slate-300">
              {t(
                "Add your approved policy as a reference for setup. We will guide you through the next steps.",
              )}
            </p>
            {text.trim() && (
              <p className="mt-2 text-xs font-medium text-sapphire dark:text-blue-300">
                {t("Policy text added")}
              </p>
            )}
          </div>
        </div>
        <div className="flex shrink-0 flex-wrap items-center gap-3">
          <button
            ref={trigger}
            type="button"
            className="btn-primary"
            aria-expanded={open}
            aria-controls={panelId}
            onClick={() => (open ? close() : show(0))}
          >
            {open
              ? t("Close guide")
              : text.trim()
                ? t("Review policy text")
                : t("Add your HR policy")}
          </button>
          {!open && (
            <button
              type="button"
              className="btn-secondary"
              aria-controls={panelId}
              onClick={() => show(2)}
            >
              {t("How this works")}
            </button>
          )}
        </div>
      </div>
      <div
        id={panelId}
        hidden={!open}
        className="mt-5 border-t border-sapphire/20 pt-5 dark:border-blue-400/20"
      >
        <ol
          className="mb-5 flex flex-wrap gap-x-5 gap-y-2 text-xs"
          aria-label={t("Policy setup guide")}
        >
          {steps.map((label, index) => (
            <li
              key={label}
              aria-current={index === step ? "step" : undefined}
              className={
                index === step
                  ? "font-semibold text-sapphire dark:text-blue-300"
                  : "text-slate-600 dark:text-slate-400"
              }
            >
              {index + 1}. {t(label)}
            </li>
          ))}
        </ol>
        <h4
          ref={heading}
          tabIndex={-1}
          className="mb-3 scroll-mt-24 text-base font-semibold outline-none"
        >
          {t(steps[step])}
        </h4>
        <div hidden={step !== 0} className="space-y-4">
          <p className="text-sm leading-6 text-slate-600 dark:text-slate-300">
            {t(
              "Paste relevant policy sections or choose a plain-text (.txt) file, up to 12,000 characters. For PDF or Word documents, copy the relevant text here.",
            )}
          </p>
          <div>
            <label htmlFor={`${panelId}-text`} className="mb-1.5 block text-sm font-medium">
              {t("Policy excerpts")}
            </label>
            <textarea
              id={`${panelId}-text`}
              className="input min-h-40 w-full resize-none"
              maxLength={12000}
              value={text}
              onChange={(e) => {
                revision.current++;
                setReading(false);
                setFileError("");
                update({ policySourceText: e.target.value });
              }}
            />
          </div>
          <p className="text-xs text-slate-600">
            {t("{count} of {limit} characters", {
              count: text.length,
              limit: 12000,
            })}
          </p>
          <label className="block text-sm">
            <span className="mb-1.5 block font-medium">
              {t("Load policy text (.txt, up to 12,000 characters)")}
            </span>
            <input
              type="file"
              accept=".txt,text/plain"
              className="block w-full min-w-0 text-sm file:me-3 file:rounded-lg file:border file:border-slate-300 file:bg-white file:px-3 file:py-2 file:text-sm file:font-medium file:text-slate-700 dark:file:border-slate-600 dark:file:bg-slate-900 dark:file:text-slate-200"
              onChange={async (e) => {
                const file = e.target.files?.[0];
                const current = ++revision.current;
                setFileError("");
                setReading(false);
                if (!file) return;
                if (
                  !file.name.toLowerCase().endsWith(".txt") ||
                  file.size > 48000
                ) {
                  setFileError(
                    t("Choose a plain-text file within the policy text limit."),
                  );
                  return;
                }
                setReading(true);
                try {
                  const content = await file.text();
                  if (current !== revision.current) return;
                  if (content.length > 12000 || content.includes("\u0000")) {
                    setFileError(
                      t(
                        "Choose a plain-text file within the policy text limit.",
                      ),
                    );
                    return;
                  }
                  latest.current.onChange({
                    ...latest.current.value,
                    policySourceText: content,
                  });
                } catch {
                  if (current === revision.current)
                    setFileError(t("Could not read this policy file."));
                } finally {
                  if (current === revision.current) setReading(false);
                }
              }}
            />
          </label>
          {reading && (
            <p role="status" className="text-sm">
              {t("Reading policy file…")}
            </p>
          )}
          {fileError && (
            <p role="alert" className="text-sm text-red-700 dark:text-red-300">
              {fileError}
            </p>
          )}
          <p className="text-xs leading-5 text-slate-600 dark:text-slate-400">
            {t(
              "Choosing a file does not send it. Policy text is sent to the setup service when you generate a draft. Sharing it with AI is optional.",
            )}
          </p>
        </div>
        <div hidden={step !== 1} className="space-y-4">
          <p className="text-sm leading-6 text-slate-600 dark:text-slate-300">
            {t(
              "Review the text and remove employee names, individual salaries and other personal information. Go back to edit it.",
            )}
          </p>
          <div
            role="region"
            aria-label={t("Policy text preview")}
            tabIndex={0}
            className="max-h-48 overflow-auto whitespace-pre-wrap break-words rounded-lg border border-slate-200 bg-white p-3 text-sm dark:border-white/10 dark:bg-slate-950"
          >
            {text || t("No policy text added")}
          </div>
          <label className="flex items-start gap-2 text-sm">
            <input
              type="checkbox"
              className="mt-0.5 h-4 w-4 accent-sapphire"
              checked={value.usePolicySourceForAi ?? false}
              onChange={(e) =>
                update({ usePolicySourceForAi: e.target.checked })
              }
            />
            {t(
              "Use these excerpts with the configured AI provider when generating the draft",
            )}
          </label>
          <p className="text-xs leading-5 text-slate-600 dark:text-slate-400">
            {t(
              "This is optional. Without AI assistance, enter your policy rules in the setup fields. With AI assistance, the text provides context for suggestions; it does not automatically fill every setting.",
            )}
          </p>
        </div>
        <div hidden={step !== 2} className="space-y-4">
          <ol className="space-y-3 text-sm leading-6">
            <li>
              <strong>{t("Complete your settings")}</strong>
              <p className="text-slate-600 dark:text-slate-300">
                {t(
                  "Enter your company details, leave, overtime, salary grades, benefits and approval preferences in the following steps.",
                )}
              </p>
            </li>
            <li>
              <strong>{t("Generate and review the draft")}</strong>
              <p className="text-slate-600 dark:text-slate-300">
                {t(
                  "Choose Generate draft on the final step. Check each proposed record against your approved policy and edit or remove anything that does not fit.",
                )}
              </p>
            </li>
            <li>
              <strong>{t("Apply only after review")}</strong>
              <p className="text-slate-600 dark:text-slate-300">
                {t(
                  "Uploading text does not configure your company. Changes are saved only when an authorized user chooses Apply. Some settings remain planning preferences, as labeled.",
                )}
              </p>
            </li>
          </ol>
          <p className="text-xs leading-5 text-slate-600 dark:text-slate-400">
            {t(
              "If AI is unavailable, the draft identifies the starter template used. Always review its suggestions.",
            )}
          </p>
        </div>
        <div className="mt-5 flex flex-wrap items-center justify-between gap-3 border-t border-sapphire/20 pt-4 dark:border-blue-400/20">
          {step > 0 ? (
            <button
              type="button"
              className="btn-secondary"
              onClick={() => show(step - 1)}
            >
              {t("Previous guide step")}
            </button>
          ) : (
            <span className="text-xs text-slate-600">
              {t("Your existing text is kept when you close this guide.")}
            </span>
          )}
          {step < 2 ? (
            <button
              type="button"
              className="btn-primary"
              disabled={reading || (step === 0 && !text.trim())}
              onClick={() => show(step + 1)}
            >
              {step === 0
                ? t("Next: AI assistance")
                : t("Next: review process")}
            </button>
          ) : (
            <button type="button" className="btn-primary" onClick={close}>
              {t("Continue company setup")}
            </button>
          )}
        </div>
      </div>
    </section>
  );
}
