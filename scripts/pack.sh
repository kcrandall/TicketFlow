#!/usr/bin/env bash
set -euo pipefail
SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
REPO_DIR="${SCRIPT_DIR%/scripts}"
# Select this repository's global.json even when invoked from another checkout.
cd -- "${REPO_DIR}"
VERSION=""
CHANNEL="alpha"
CONFIGURATION="Release"
OUTPUT="${REPO_DIR}/artifacts/nuget"
RUN_TESTS=true
while (($#)); do
  case "$1" in
    --version|--prerelease|--configuration|--output)
      if (($# < 2)) || [[ -z "$2" || "$2" == --* ]]; then
        echo "Missing value for $1" >&2
        exit 1
      fi
      case "$1" in
        --version) VERSION="$2" ;;
        --prerelease) CHANNEL="$2" ;;
        --configuration) CONFIGURATION="$2" ;;
        --output) OUTPUT="$2" ;;
      esac
      shift 2
      ;;
    --skip-tests) RUN_TESTS=false; shift ;;
    --validate-graph-only) exec python3 "${SCRIPT_DIR}/release.py" validate-projects ;;
    -h|--help)
      echo "Usage: pack.sh [--version VERSION] [--prerelease alpha|beta|stable] [--configuration Release] [--output DIR] [--skip-tests] [--validate-graph-only]"
      exit 0
      ;;
    *) echo "Unknown argument: $1" >&2; exit 1 ;;
  esac
done
VERSION="$(bash "${SCRIPT_DIR}/resolve-version.sh" --version "${VERSION}" --prerelease "${CHANNEL}")"
python3 "${SCRIPT_DIR}/release.py" validate-projects
mkdir -p -- "${OUTPUT}"
OUTPUT="$(cd -- "${OUTPUT}" && pwd)"
if [[ -n "$(find "${OUTPUT}" -mindepth 1 -maxdepth 1 -print -quit)" ]]; then
  echo "Package output must be empty; choose a fresh directory: ${OUTPUT}" >&2
  exit 1
fi
dotnet restore "${REPO_DIR}/TicketFlow.slnx"
dotnet build "${REPO_DIR}/TicketFlow.slnx" --configuration "${CONFIGURATION}" --no-restore -p:Version="${VERSION}" -p:ContinuousIntegrationBuild=true
if [[ "${RUN_TESTS}" == true ]]; then
  dotnet test "${REPO_DIR}/TicketFlow.slnx" --configuration "${CONFIGURATION}" --no-build --no-restore -p:Version="${VERSION}" --verbosity minimal
fi
while IFS=$'\t' read -r package_id project; do
  [[ -z "${package_id}" || "${package_id}" == \#* ]] && continue
  dotnet pack "${REPO_DIR}/${project}" --configuration "${CONFIGURATION}" --no-build --no-restore --output "${OUTPUT}" -p:Version="${VERSION}" -p:ContinuousIntegrationBuild=true
done < "${SCRIPT_DIR}/nuget-projects.tsv"
python3 "${SCRIPT_DIR}/release.py" write-manifest --package-dir "${OUTPUT}" --version "${VERSION}"
