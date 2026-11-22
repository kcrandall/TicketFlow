#!/usr/bin/env bash
set -euo pipefail
SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
PACKAGE_DIR="${SCRIPT_DIR%/scripts}/artifacts/nuget"
if [[ "${GITHUB_ACTIONS:-}" != true || "${GITHUB_REF:-}" != refs/heads/main || "${GITHUB_EVENT_NAME:-}" != workflow_dispatch ]]; then
  echo "Publication is restricted to the manually dispatched main-branch release workflow." >&2
  exit 1
fi
if [[ "${CONFIRM_PUBLIC_PUBLISH:-}" != publish-public || -z "${NUGET_API_KEY:-}" ]]; then
  echo "Public publication requires confirmation and the trusted-publishing API key." >&2
  exit 1
fi
python3 "${SCRIPT_DIR}/release.py" verify-manifest --package-dir "${PACKAGE_DIR}"
VERSION="$(python3 -c 'import json,sys; print(json.load(open(sys.argv[1]))["version"])' "${PACKAGE_DIR}/release-manifest.json")"
CHANNEL_VERSION="$(bash "${SCRIPT_DIR}/resolve-version.sh" --version "${VERSION}" --prerelease "${INPUT_PRERELEASE:-}")"
if [[ "${CHANNEL_VERSION}" != "${VERSION}" ]]; then
  echo "Release manifest version does not match the selected channel." >&2
  exit 1
fi
if [[ "${INPUT_PRERELEASE:-}" == stable && ( "${CONFIRM_STABLE_PUBLISH:-}" != "${VERSION}" || "${VERSION}" == *-* ) ]]; then
  echo "Stable publication requires confirmation of the exact manifest version." >&2
  exit 1
fi
# Do not use --skip-duplicate: a previously published version is immutable and
# a partial release must be investigated rather than silently accepted.
while IFS=$'\t' read -r package_id project; do
  [[ -z "${package_id}" || "${package_id}" == \#* ]] && continue
  dotnet nuget push "${PACKAGE_DIR}/${package_id}.${VERSION}.nupkg" --api-key "${NUGET_API_KEY}" --source https://api.nuget.org/v3/index.json
done < "${SCRIPT_DIR}/nuget-projects.tsv"
