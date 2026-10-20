# Setup Studio Guided Wizard Retention Amendment

Status: approved by the owner from the supplied local screenshot on 2026-10-18. This amendment supersedes only the original specification's proposal to replace the existing Guided Setup wizard with an adaptive milestone blueprint.

## Locked decision

The existing Guided Setup screen is good and must remain the baseline Guided experience. Preserve:

- the five visible steps: `Company details`, `Working week`, `People & pay`, `Grades & benefits`, and `Review & create`;
- the vertical step rail, current-step emphasis, descriptive copy, Back/Continue navigation, and final review/apply stage;
- selected company identity, protected company scope, and Change company behavior;
- `Save for later`, `Policy library / updates`, and `Cancel setup`;
- the existing HR policy upload/assistance entry and its current review boundaries;
- current fields, validation, draft persistence, pending-file protection, preview, and supported Apply behavior;
- the existing principle that nothing changes until the user reviews and explicitly applies it;
- the current visual density, card hierarchy, form width, and KynexOne application shell.

The retained wizard remains a bounded creation/preparation journey. Its numbered sequence is appropriate for this specific guided job and must not be treated as the same navigation problem as ongoing settings maintenance.

## Revised two-path model

Setup Studio still exposes two distinguishable jobs:

1. **Guided Setup** opens the retained five-step wizard.
2. **Configuration Center** provides direct ongoing maintenance through the twelve setup areas and existing canonical forms.

The product may add a compact path switch outside the wizard card, but must not insert a new landing page before the existing Guided experience merely to satisfy the two-path model. Existing `/setup?tab=aiSetup` and `mode=starter` links continue to reach Guided Setup. Configuration Center receives an explicit canonical path such as `path=configuration`.

## Configuration Center scope

The C+B direction applies to Configuration Center only:

- a purpose-grouped area blueprint for the selected setup area;
- the same consistent pattern across all twelve setup areas;
- whole destination rows/nodes are actionable, with no detached far-right generic `Open settings` button;
- selecting a destination opens one full-width canonical form near the top of the workspace;
- Back returns to the correct area row, scroll position, and focus;
- desktop uses a clear area directory/dialog; mobile uses an accessible full-screen area navigator;
- tenant/company scope, availability, permissions, and direct-save impact remain explicit;
- `Master Data` is displayed as `Employee Reference Lists`; no other terminology rename is approved;
- canonical forms, routes, APIs, validation, permissions, and save behavior are reused, not copied.

## Explicit non-goals

This amendment does not authorize:

- replacing or visually redesigning the retained five-step Guided Setup form;
- extracting the wizard into a new task board;
- a new Guided draft schema or migration solely for this redesign;
- a Guided milestone/status engine;
- a new backend endpoint, schema, form engine, or universal Apply flow;
- changing the main application sidebar or operational modules;
- deployment, publication, feature enablement, or production/client data mutation.

## Acceptance boundary

The implementation is acceptable only if the retained Guided screen remains recognizably and behaviorally the same while Configuration Center eliminates long-scroll navigation and detached actions. Existing Guided lifecycle, binder, prefill, review, company-scope, and pending-file tests must continue to pass unchanged except for route-entry assertions required by the new path switch.
