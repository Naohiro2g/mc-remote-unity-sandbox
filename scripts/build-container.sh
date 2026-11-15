#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT_DIR"

SANDBOX_VERSION="${SANDBOX_VERSION:-$(tr -d '[:space:]' < "$ROOT_DIR/VERSION")}"
if [[ -z "$SANDBOX_VERSION" ]]; then
  echo "ERROR: VERSION file is empty and SANDBOX_VERSION is unset." >&2
  exit 1
fi

GIT_COMMIT="${GIT_COMMIT:-$(git rev-parse HEAD)}"
BUILD_DATE="${BUILD_DATE:-$(date -u +"%Y-%m-%dT%H:%M:%SZ")}"
WEBGL_DIST="${WEBGL_DIST:-unity/Builds/WebGL}"
IMAGE_NAME="${IMAGE_NAME:-ghcr.io/naohiro2g/mc-remote-unity-sandbox}"
IMAGE_TAG="${IMAGE_TAG:-v${SANDBOX_VERSION}}"
UNITY_EDITOR="${UNITY_EDITOR:-/home/tsuji/Unity/Hub/Editor/6000.6.8f1/Editor/Unity}"
UNITY_LOG="${UNITY_LOG:-/tmp/unity-webgl-build.log}"

if [[ "${SKIP_WEBGL_BUILD:-0}" != "1" ]]; then
  if [[ ! -x "$UNITY_EDITOR" ]]; then
    echo "ERROR: Unity Editor not found at '$UNITY_EDITOR'. Set UNITY_EDITOR or SKIP_WEBGL_BUILD=1." >&2
    exit 1
  fi
  echo "==> Building Unity WebGL (${SANDBOX_VERSION}) to ${ROOT_DIR}/${WEBGL_DIST} ..."
  UNITY_WEBGL_BUILD_PATH="${ROOT_DIR}/${WEBGL_DIST}" \
    "$UNITY_EDITOR" -batchmode -nographics -quit \
      -projectPath "${ROOT_DIR}/unity" \
      -executeMethod Code2Create.UnitySandbox.LocalDemo.DemoEditor.BuildWebGL \
      -logFile "$UNITY_LOG"
  grep -F "LOCAL_SANDBOX_WEBGL_BUILD_PASS" "$UNITY_LOG"
else
  echo "==> SKIP_WEBGL_BUILD=1: reusing ${WEBGL_DIST}"
fi

if [[ ! -f "${ROOT_DIR}/${WEBGL_DIST}/index.html" ]]; then
  echo "ERROR: WebGL build output not found at '${ROOT_DIR}/${WEBGL_DIST}/index.html'." >&2
  exit 1
fi

echo "==> Building container image ${IMAGE_NAME}:${IMAGE_TAG} (commit ${GIT_COMMIT}) ..."
docker build \
  --build-arg "SANDBOX_VERSION=${SANDBOX_VERSION}" \
  --build-arg "GIT_COMMIT=${GIT_COMMIT}" \
  --build-arg "BUILD_DATE=${BUILD_DATE}" \
  --build-arg "WEBGL_DIST=${WEBGL_DIST}" \
  -t "${IMAGE_NAME}:${IMAGE_TAG}" \
  "$ROOT_DIR"

echo "==> Built locally (not pushed): ${IMAGE_NAME}:${IMAGE_TAG}"
echo "    Inspect labels: docker image inspect ${IMAGE_NAME}:${IMAGE_TAG} --format '{{json .Config.Labels}}'"
