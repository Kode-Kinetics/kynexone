#!/usr/bin/env bash
# Build the offline installation bundle for one release (vendor side; the release workflow runs it).
#
#   deploy/release/make-bundle.sh 1.2.0 [--registry kynexone] [--out dist]
#
# Expects the release's images to exist locally (built by the release workflow, or pulled):
#   <registry>/kynexone-api:<version>, <registry>/kynexone-web:<version>, and optionally <registry>/kynexbridge:<version>.
# Pulls every third-party image the client kit pins (postgres, redis, minio, mc, caddy) and writes
#
#   <out>/kynexone-<version>.tar.gz
#     kynexone-<version>/
#       VERSION, RELEASE_NOTES.md, CHECKSUMS.sha256
#       images/kynexone-images.tar      docker save of every image the kit runs (the optional ollama is not included)
#       images/IMAGES.txt               each image with its digest
#       deploy/client/                  the kit (install.sh finds the images two levels up)
set -euo pipefail

VERSION="${1:-}"; shift || true
REGISTRY="kynexone"; OUT="dist"
while [ $# -gt 0 ]; do
  case "$1" in
    --registry) REGISTRY="${2:?}"; shift 2 ;;
    --out) OUT="${2:?}"; shift 2 ;;
    *) echo "Unknown option: $1" >&2; exit 2 ;;
  esac
done
echo "$VERSION" | grep -Eq '^[0-9]+\.[0-9]+\.[0-9]+$' || { echo "Usage: make-bundle.sh <MAJOR.MINOR.PATCH>" >&2; exit 2; }

ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
STAGE="$(mktemp -d)"; trap 'rm -rf "$STAGE"' EXIT
NAME="kynexone-$VERSION"
DIR="$STAGE/$NAME"
mkdir -p "$DIR/images" "$DIR/deploy"

cp -R "$ROOT/deploy/client" "$DIR/deploy/client"
rm -rf "$DIR/deploy/client/.env" "$DIR/deploy/client/backups"
sed -i.bak "s/^KYNEXONE_VERSION=.*/KYNEXONE_VERSION=$VERSION/; s/^KYNEXONE_REGISTRY=.*/KYNEXONE_REGISTRY=$REGISTRY/" "$DIR/deploy/client/.env.example"
rm -f "$DIR/deploy/client/.env.example.bak"
echo "$VERSION" > "$DIR/VERSION"

# Every image the kit runs, resolved from the compose file itself, so the bundle can never miss one.
DUMMY_ENV="$STAGE/bundle.env"
cat > "$DUMMY_ENV" <<ENV
KYNEXONE_VERSION=$VERSION
KYNEXONE_REGISTRY=$REGISTRY
KYNEXONE_DOMAIN=bundle.invalid
POSTGRES_PASSWORD=x
JWT_AUDIENCE=x
JWT_SIGNING_KEY=x
PROXY_CLIENT_IP_SECRET=x
STORAGE_SECRET_KEY=x
ENV
mapfile -t IMAGES < <(docker compose --env-file "$DUMMY_ENV" -f "$ROOT/deploy/client/docker-compose.yml" config --images | sort -u)
if docker image inspect "$REGISTRY/kynexbridge:$VERSION" >/dev/null 2>&1; then IMAGES+=("$REGISTRY/kynexbridge:$VERSION"); fi

for image in "${IMAGES[@]}"; do
  case "$image" in
    "$REGISTRY"/*) docker image inspect "$image" >/dev/null || { echo "Missing release image $image" >&2; exit 1; } ;;
    *) docker pull -q "$image" >/dev/null ;;
  esac
  echo "$image $(docker image inspect --format '{{index .RepoDigests 0}}' "$image" 2>/dev/null || docker image inspect --format '{{.Id}}' "$image")"
done > "$DIR/images/IMAGES.txt"

echo "Saving ${#IMAGES[@]} images"
docker save -o "$DIR/images/kynexone-images.tar" "${IMAGES[@]}"

if [ -f "$ROOT/docs/releases/$VERSION.md" ]; then
  cp "$ROOT/docs/releases/$VERSION.md" "$DIR/RELEASE_NOTES.md"
else
  printf '# KynexOne %s\n\nSee the GitHub release for the changes in this version.\n' "$VERSION" > "$DIR/RELEASE_NOTES.md"
fi

(cd "$DIR" && find . -type f ! -name CHECKSUMS.sha256 -print0 | sort -z | xargs -0 sha256sum | sed 's#  \./#  #' > CHECKSUMS.sha256)

mkdir -p "$OUT"
tar czf "$OUT/$NAME.tar.gz" -C "$STAGE" "$NAME"
(cd "$OUT" && sha256sum "$NAME.tar.gz" > "$NAME.tar.gz.sha256")
echo "Wrote $OUT/$NAME.tar.gz"
