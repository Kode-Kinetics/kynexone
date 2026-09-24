FROM node:22-alpine
WORKDIR /app
COPY package*.json ./
RUN npm ci
COPY . ./

# Compose is a runnable pilot artifact, not a hot-reload development shell.
# Build the Next application once so route traversal cannot trigger on-demand
# compiler memory restarts and transient blank pages.
ARG NEXT_PUBLIC_API_URL=http://backend:5117
ARG NEXT_PUBLIC_API_BASE_URL=http://backend:5117
ENV NEXT_PUBLIC_API_URL=${NEXT_PUBLIC_API_URL}
ENV NEXT_PUBLIC_API_BASE_URL=${NEXT_PUBLIC_API_BASE_URL}
# RETRY, because this build reaches the public internet. `next/font/google` fetches Manrope,
# Geist, IBM Plex and Instrument Serif from fonts.googleapis.com AT BUILD TIME, and when that
# fetch fails the loader throws `TypeError: Cannot read properties of null (reading '1')` and
# the whole image build fails. That blocked five separate releases on 2026-09-23 — every one
# on a green codebase, none caused by a change in this repo.
#
# Retry is the proportionate fix for a transient CDN fetch, not a mask for a flaky test: nothing
# here is nondeterministic except the network. The DURABLE fix is to vendor the six families via
# `next/font/local` so the release path stops depending on Google being reachable.
RUN for attempt in 1 2 3; do \
      echo "next build (attempt $attempt/3)"; \
      npm run build && exit 0; \
      echo "build failed on attempt $attempt — retrying in 15s (usually the Google Fonts fetch)"; \
      sleep 15; \
    done; \
    echo "next build failed three times. If the log shows 'An error occurred in next/font', it is the Google Fonts fetch, not your code."; \
    exit 1

ENV NODE_ENV=production
EXPOSE 5173
CMD ["npm", "run", "start", "--", "-p", "5173", "-H", "0.0.0.0"]
