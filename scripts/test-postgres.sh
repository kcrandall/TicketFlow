#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
for required in docker dotnet; do
    command -v "$required" >/dev/null || { echo "Required command missing: $required" >&2; exit 1; }
done
docker info >/dev/null
postgres_image="postgres:18@sha256:4aabea78cf39b90e834caf3af7d602a18565f6fe2508705c8d01aa63245c2e20"
task_dir="$(mktemp -d "${TMPDIR:-/tmp}/ticketflow-postgres.XXXXXXXX")"
task_id="$(basename "$task_dir")"
cleanup() {
    local container_id owned_by
    if [[ -s "$task_dir/container.cid" ]]; then
        container_id="$(cat "$task_dir/container.cid")"
        owned_by="$(docker inspect --format '{{index .Config.Labels "ticketflow.postgres-test"}}' "$container_id" 2>/dev/null || true)"
        if [[ "$owned_by" == "$task_id" ]]; then
            docker rm --force "$container_id" >/dev/null 2>&1 || true
        fi
    fi
    rm -f "$task_dir/container.cid"
    rmdir "$task_dir" 2>/dev/null || true
}
trap cleanup EXIT
trap 'exit 130' INT
trap 'exit 143' TERM
container_id="$(docker run --detach --rm --cidfile "$task_dir/container.cid" \
    --label "ticketflow.postgres-test=$task_id" --publish 127.0.0.1::5432 \
    --env POSTGRES_USER=ticketflow --env POSTGRES_PASSWORD=ticketflow-local-tests-only \
    --env POSTGRES_DB=ticketflow_tests "$postgres_image" -c wal_level=logical -c max_replication_slots=8 -c max_wal_senders=4 -c max_connections=300)"
postgres_port="$(docker port "$container_id" 5432/tcp)"
postgres_port="${postgres_port##*:}"
[[ "$postgres_port" =~ ^[0-9]+$ ]] || { echo "Cannot determine isolated PostgreSQL port." >&2; exit 1; }
ready=false
for ((attempt = 0; attempt < 30; attempt++)); do
    if docker exec "$container_id" pg_isready --quiet --host 127.0.0.1 --port 5432 -U ticketflow -d ticketflow_tests; then
        ready=true
        break
    fi
    sleep 1
done
[[ "$ready" == true ]] || { echo "PostgreSQL did not become ready within 30 attempts." >&2; exit 1; }

echo "Testing against isolated PostgreSQL 18 on loopback port $postgres_port."
cd "$repo_root"
TICKETFLOW_REQUIRE_POSTGRES=1 TICKETFLOW_TEST_POSTGRES="Host=127.0.0.1;Port=$postgres_port;Username=ticketflow;Password=ticketflow-local-tests-only;Database=ticketflow_tests;Maximum Pool Size=16" \
    dotnet test TicketFlow.slnx --configuration Release --nologo "$@"
