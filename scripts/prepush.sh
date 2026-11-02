#!/usr/bin/env bash
# Local pre-push gate: the same FAST, deterministic checks CI runs, before a push burns paid minutes.
#   scripts/prepush.sh            # everything below
#   SKIP_BACKEND=1 scripts/prepush.sh   # frontend/script gates only
#   WITH_INTEGRATION=1 scripts/prepush.sh   # also the Testcontainers tests (needs Docker)
# Optional git hook:  ln -s ../../scripts/prepush.sh .git/hooks/pre-push
set -euo pipefail
root="$(cd "$(dirname "$0")/.." && pwd)"
step() { printf '\n==> %s\n' "$*"; }

step "deploy-preflight self-tests"
python3 "$root/scripts/check_render_env.py" --self-test
python3 "$root/scripts/check_migration_target.py" --self-test

cd "$root/frontend"
[ -d node_modules ] || { step "npm ci"; npm ci --no-audit --no-fund; }
step "frontend: tsc"; npx tsc --noEmit
step "frontend: eslint ratchet"; npm run lint
step "frontend: playwright config guard"; node scripts/check-playwright-configs.mjs
step "frontend: browserless contracts"; npx playwright test -c e2e/playwright.browserless.config.ts
step "frontend: unit (+ TZ=Asia/Karachi)"; npm run test:unit && npm run test:unit:tz

if [ "${SKIP_BACKEND:-0}" != "1" ]; then
  cd "$root/backend-dotnet"
  proj=Zayra.Api.Tests/Zayra.Api.Tests.csproj
  step "backend: build"; dotnet build "$proj" -c Release
  step "backend: unit + architecture ratchets (no containers)"
  dotnet test "$proj" --no-build -c Release --filter "Category!=Integration"
  if [ "${WITH_INTEGRATION:-0}" = "1" ]; then
    step "backend: integration (Docker)"; dotnet test "$proj" --no-build -c Release --filter "Category=Integration"
  fi
fi
step "pre-push gates passed"
