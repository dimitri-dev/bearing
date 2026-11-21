#!/usr/bin/env bash
#
# The Elasticsearch and OpenSearch servers that Bearing.Data.Tests' SearchLiveTests talk to — the sibling of
# test-db.sh (PostgreSQL), test-db-mssql.sh (SQL Server), test-db-mysql.sh (MariaDB / MySQL) and
# test-db-dynamodb.sh (DynamoDB Local). One implementation serves both engines, so both are started: a suite
# that only ever ran against one of them would be proving half of what the provider claims.
#
#   ./build/test-db-search.sh          start all three (idempotent)
#   ./build/test-db-search.sh status    what is running
#   ./build/test-db-search.sh stop      remove the three containers
#
#   bearing-elasticsearch-test         security off, plain HTTP          127.0.0.1:19200
#   bearing-opensearch-test            security plugin off, plain HTTP   127.0.0.1:19201
#   bearing-elasticsearch-secure-test  security on, user `elastic`       127.0.0.1:19202
#
# The secured node runs with the image's own security auto-configuration (measured 2026-11-21 on 9.5.6): it
# serves HTTPS with a certificate the node generated for itself on first start, signed by a CA it also made —
# no machine trusts it, so a client needs TLS mode Require (any certificate) and Verify Full refuses it. The
# `elastic` user's password is fixed for the tests (BEARING_TEST_ELASTICSEARCH_SECURE_PASSWORD).
#
# None listens on 9200: a developer machine is likely to have a cluster there already, and these suites create
# and delete their own indices, aliases, data streams and API keys. Every test makes its own uniquely named
# fixtures and removes them in a finally (§4.7), so an empty cluster is all any of them needs.
#
# Disk-based shard allocation is switched off: on a nearly full developer disk Elasticsearch would otherwise
# stop allocating the fixtures' shards (or mark indices read-only), and the suite would fail for a reason that
# has nothing to do with Bearing.
#
# bash 3.2-safe (macOS), like build/velopack.sh.
set -euo pipefail

ES_NAME="${BEARING_TEST_ELASTICSEARCH_CONTAINER:-bearing-elasticsearch-test}"
ES_PORT="${BEARING_TEST_ELASTICSEARCH_PORT:-19200}"
ES_IMAGE="${BEARING_TEST_ELASTICSEARCH_IMAGE:-docker.elastic.co/elasticsearch/elasticsearch:9.5.6}"

OS_NAME="${BEARING_TEST_OPENSEARCH_CONTAINER:-bearing-opensearch-test}"
OS_PORT="${BEARING_TEST_OPENSEARCH_PORT:-19201}"
OS_IMAGE="${BEARING_TEST_OPENSEARCH_IMAGE:-opensearchproject/opensearch:3.10.0}"

SECURE_NAME="${BEARING_TEST_ELASTICSEARCH_SECURE_CONTAINER:-bearing-elasticsearch-secure-test}"
SECURE_PORT="${BEARING_TEST_ELASTICSEARCH_SECURE_PORT:-19202}"
SECURE_PASSWORD="${BEARING_TEST_ELASTICSEARCH_SECURE_PASSWORD:-Bearing-Test-1}"

WAIT_SECONDS=180

say() { printf '%s\n' "$*" >&2; }

need_docker() {
  command -v docker >/dev/null 2>&1 || { say "docker is not on PATH."; exit 1; }
  docker info >/dev/null 2>&1 || { say "docker is installed but not running."; exit 1; }
  command -v curl >/dev/null 2>&1 || { say "curl is not on PATH (the readiness check asks cluster health)."; exit 1; }
}

running() { [ "$(docker inspect -f '{{.State.Running}}' "$1" 2>/dev/null || echo false)" = "true" ]; }
exists()  { docker inspect "$1" >/dev/null 2>&1; }

# True when something accepts a TCP connection on the port.
listening() { (exec 3<>"/dev/tcp/127.0.0.1/$1") 2>/dev/null; }

# Cluster health yellow or better: a single node never gets its replicas, so yellow is as good as it gets
# for an index with the default replica count. The secured node is asked over HTTPS with its self-made
# certificate accepted (-k) and the fixed password; the other two over plain HTTP.
healthy() {
  local url="$1"; shift
  curl -s --max-time 3 -o /dev/null -w '%{http_code}' "$@" \
    "$url/_cluster/health?wait_for_status=yellow&timeout=2s" 2>/dev/null | grep -q '^200$'
}

# The port is a free one on purpose, but it is still a guess about someone else's machine. Talking to whatever
# happens to be there by accident would be worse than refusing: these tests make and then remove fixtures.
check_port_is_free() {
  local name="$1" port="$2" variable="$3"
  if ! listening "$port"; then
    return 0
  fi
  say ""
  say "  Something is already listening on 127.0.0.1:$port, and it is not $name."
  say "  These suites make and then remove their own indices there — refusing rather than risking it."
  say "  Run this one elsewhere, e.g. ${variable}=2${port#1} (and point the matching"
  say "  BEARING_TEST_*_ENDPOINT at it)."
  say ""
  exit 1
}

# start_one <name> <port> <image> <health url> <curl args…> -- <docker run args…>
start_one() {
  local name="$1" port="$2" image="$3" url="$4" variable="$5"; shift 5
  local curl_args=()
  while [ "$#" -gt 0 ] && [ "$1" != "--" ]; do curl_args+=("$1"); shift; done
  [ "$#" -gt 0 ] && shift

  if running "$name"; then
    say "$name is already running on port $port."
  elif exists "$name"; then
    say "Starting the existing ${name}…"
    docker start "$name" >/dev/null
  else
    check_port_is_free "$name" "$port" "$variable"
    say "Creating $name from $image on port ${port}…"
    docker run -d --name "$name" "$@" "$image" >/dev/null
  fi

  say "Waiting for $name to report cluster health yellow or better…"
  local started waited
  started=$(date +%s)
  until healthy "$url" ${curl_args[@]+"${curl_args[@]}"}; do
    waited=$(( $(date +%s) - started ))
    if [ "$waited" -ge "$WAIT_SECONDS" ]; then
      say ""
      say "  $name did not report a healthy cluster within ${WAIT_SECONDS}s. Its log:"
      say ""
      docker logs --tail 30 "$name" >&2 || true
      exit 1
    fi
    sleep 3
  done
}

start() {
  need_docker

  start_one "$ES_NAME" "$ES_PORT" "$ES_IMAGE" "http://127.0.0.1:$ES_PORT" BEARING_TEST_ELASTICSEARCH_PORT -- \
    -p "127.0.0.1:$ES_PORT:9200" \
    -e discovery.type=single-node \
    -e xpack.security.enabled=false \
    -e cluster.routing.allocation.disk.threshold_enabled=false \
    -e "ES_JAVA_OPTS=-Xms1g -Xmx1g"

  start_one "$OS_NAME" "$OS_PORT" "$OS_IMAGE" "http://127.0.0.1:$OS_PORT" BEARING_TEST_OPENSEARCH_PORT -- \
    -p "127.0.0.1:$OS_PORT:9200" \
    -e discovery.type=single-node \
    -e DISABLE_SECURITY_PLUGIN=true \
    -e DISABLE_INSTALL_DEMO_CONFIG=true \
    -e cluster.routing.allocation.disk.threshold_enabled=false \
    -e "OPENSEARCH_JAVA_OPTS=-Xms512m -Xmx512m"

  # Security is left at the image's default (on), so the node auto-configures TLS for HTTP on first start.
  start_one "$SECURE_NAME" "$SECURE_PORT" "$ES_IMAGE" "https://127.0.0.1:$SECURE_PORT" \
    BEARING_TEST_ELASTICSEARCH_SECURE_PORT -k -u "elastic:$SECURE_PASSWORD" -- \
    -p "127.0.0.1:$SECURE_PORT:9200" \
    -e discovery.type=single-node \
    -e "ELASTIC_PASSWORD=$SECURE_PASSWORD" \
    -e cluster.routing.allocation.disk.threshold_enabled=false \
    -e "ES_JAVA_OPTS=-Xms512m -Xmx512m"

  status
}

version_of() {
  local url="$1"; shift
  curl -s --max-time 3 "$@" "$url/" 2>/dev/null \
    | sed -n 's/.*"number"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p' | head -n 1
}

status() {
  need_docker
  local any_down=0

  say ""
  if running "$ES_NAME"; then
    say "  $ES_NAME          http://localhost:$ES_PORT   security off  $(version_of "http://127.0.0.1:$ES_PORT" || true)"
  else
    say "  $ES_NAME          not running"; any_down=1
  fi
  if running "$OS_NAME"; then
    say "  $OS_NAME             http://localhost:$OS_PORT   security off  $(version_of "http://127.0.0.1:$OS_PORT" || true)"
  else
    say "  $OS_NAME             not running"; any_down=1
  fi
  if running "$SECURE_NAME"; then
    say "  $SECURE_NAME  https://localhost:$SECURE_PORT  HTTPS with a self-signed certificate (TLS mode Require),"
    say "                                     user elastic / $SECURE_PASSWORD  $(version_of "https://127.0.0.1:$SECURE_PORT" -k -u "elastic:$SECURE_PASSWORD" || true)"
  else
    say "  $SECURE_NAME  not running"; any_down=1
  fi
  say ""

  if [ "$any_down" = 1 ]; then
    say "  Start them with: ./build/test-db-search.sh"
    exit 1
  fi
  if [ "$ES_PORT" = "19200" ] && [ "$OS_PORT" = "19201" ] && [ "$SECURE_PORT" = "19202" ]; then
    say "  \`dotnet test\` needs no environment variables — these are SearchTestServer's defaults."
  else
    say "  These are NOT the default endpoints, so the suites need to be told:"
    say ""
    say "      BEARING_TEST_ELASTICSEARCH_ENDPOINT=http://localhost:$ES_PORT \\"
    say "      BEARING_TEST_OPENSEARCH_ENDPOINT=http://localhost:$OS_PORT \\"
    say "      BEARING_TEST_ELASTICSEARCH_SECURE_ENDPOINT=https://localhost:$SECURE_PORT dotnet test"
  fi
}

stop() {
  need_docker
  for name in "$ES_NAME" "$OS_NAME" "$SECURE_NAME"; do
    if exists "$name"; then
      say "Removing ${name}…"
      docker rm -f "$name" >/dev/null
    else
      say "$name does not exist."
    fi
  done
}

case "${1:-start}" in
  start|"") start ;;
  stop)     stop ;;
  status)   status ;;
  *)        say "usage: $0 [start|stop|status]"; exit 2 ;;
esac
