# syntax=docker/dockerfile:1
#
# Self-hostable KynexOne web frontend (also the docker-compose pilot image).
# Multi-stage: deps -> build -> a minimal runtime that holds only Next's standalone server
# (`output: 'standalone'` in next.config.ts), its traced node_modules and the static assets.
# Runs as the unprivileged `node` user. Node 24 is the active LTS line.
#
#   docker build -t kynexone-web \
#     --build-arg NEXT_PUBLIC_API_BASE_URL=https://api.example.sa \
#     --build-arg BUILD_COMMIT=$(git rev-parse HEAD) frontend
#
# NEXT_PUBLIC_API_BASE_URL is a BUILD argument, not a runtime setting: Next inlines NEXT_PUBLIC_*
# into the browser bundle and resolves the /api rewrite in next.config.ts at build time. Changing it
# means rebuilding the image.

ARG NODE_IMAGE=node:24-alpine

# ── deps: exact lockfile install, cached on package*.json only ──────────────────
FROM ${NODE_IMAGE} AS deps
WORKDIR /app
COPY package.json package-lock.json ./
RUN npm ci --no-audit --no-fund

# ── build ───────────────────────────────────────────────────────────────────────
FROM ${NODE_IMAGE} AS build
WORKDIR /app
ENV NEXT_TELEMETRY_DISABLED=1
COPY --from=deps /app/node_modules ./node_modules
COPY . ./

# Defaults suit docker-compose, where the server-side rewrite reaches the `backend` service.
ARG NEXT_PUBLIC_API_BASE_URL=http://backend:5117
ARG NEXT_PUBLIC_API_URL=${NEXT_PUBLIC_API_BASE_URL}
ENV NEXT_PUBLIC_API_BASE_URL=${NEXT_PUBLIC_API_BASE_URL}
ENV NEXT_PUBLIC_API_URL=${NEXT_PUBLIC_API_URL}
# The commit this image is built from, served at /build-info (a static route, fixed at build time)
# so the e2e preflight can refuse a stale image. docker compose forwards BUILD_COMMIT.
ARG BUILD_COMMIT=
ENV BUILD_COMMIT=${BUILD_COMMIT}
# No retry loop: the six typefaces are vendored in app/fonts (next/font/local), so the build does
# not reach the public internet. A failure here is a real failure.
RUN npm run build && mkdir -p public

# ── runtime ─────────────────────────────────────────────────────────────────────
FROM ${NODE_IMAGE} AS runtime
WORKDIR /app
ENV NODE_ENV=production \
    NEXT_TELEMETRY_DISABLED=1 \
    PORT=5173 \
    HOSTNAME=0.0.0.0

COPY --from=build --chown=node:node /app/.next/standalone ./
COPY --from=build --chown=node:node /app/.next/static ./.next/static
COPY --from=build --chown=node:node /app/public ./public

USER node
EXPOSE 5173
HEALTHCHECK --interval=30s --timeout=5s --start-period=20s --retries=3 \
  CMD wget -q -O /dev/null "http://127.0.0.1:${PORT}/build-info" || exit 1
# Exec form: node is PID 1 and receives SIGTERM directly for a clean shutdown.
CMD ["node", "server.js"]
