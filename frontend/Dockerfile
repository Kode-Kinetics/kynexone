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
# No retry loop: this step no longer reaches the public internet. All six typefaces are
# vendored as .woff2 in app/fonts and loaded with `next/font/local`, so the build does not
# fetch from fonts.googleapis.com and cannot fail on it. A failure here is now a real failure —
# do not wrap it in attempts, which is exactly what would hide the next one.
RUN npm run build

ENV NODE_ENV=production
EXPOSE 5173
CMD ["npm", "run", "start", "--", "-p", "5173", "-H", "0.0.0.0"]
