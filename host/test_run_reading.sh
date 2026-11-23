#!/bin/bash
# Tests for host/run-reading.sh with a stub claude and a fake Kavita on
# 127.0.0.1. Needs no Kavita, no network and no real token.
#
#   bash host/test_run_reading.sh
#
# The runs that get past the file checks start the real host/annotate_reading.py,
# so they also check that run-reading.sh passes it flags it accepts.

set -euo pipefail

HERE="$(cd "$(dirname "$0")" && pwd)"
SCRIPT="$HERE/run-reading.sh"
PYTHON="${KAVITA_READING_PYTHON:-/opt/homebrew/bin/python3.12}"
TOKEN="sk-ant-oat01-test-token-value"
KEY="kavita-test-auth-key-0123456789"
WORK="$(mktemp -d "${TMPDIR:-/tmp}/kavita-reading-test.XXXXXX")"
server_pid=""
cleanup() {
  [ -n "$server_pid" ] && kill "$server_pid" 2>/dev/null || true
  rm -rf "$WORK"
}
trap cleanup EXIT

failures=0
fail() { printf 'FAIL %s: %s\n' "$case_name" "$*"; failures=$((failures + 1)); }

mkdir -p "$WORK/bin"

# Fake Kavita. It wants $KEY in x-api-key, serves one passage until something is
# posted for it, and appends each posted body to $WORK/<case>/posted, where
# <case> comes from $WORK/case.
cat >"$WORK/kavita.py" <<'EOF'
import json, sys
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
work, key = sys.argv[1], sys.argv[2]
PASSAGE = {"id": "u1:0", "text": "辛い料理。", "items": [{"id": 101, "s": 0, "e": 2,
    "surface": "辛い", "lemma": "辛い", "readings": ["からい", "つらい"],
    "senses": [{"key": "1:1", "pos": ["adj-i"], "gloss": ["spicy"]}]}]}
class H(BaseHTTPRequestHandler):
    def send(self, status, body=None):
        raw = b"" if body is None else json.dumps(body).encode()
        self.send_response(status)
        self.send_header("Content-Length", str(len(raw)))
        self.end_headers()
        self.wfile.write(raw)
    def case(self):
        return open(f"{work}/case").read().strip()
    def do_GET(self):
        if self.headers.get("x-api-key") != key:
            return self.send(401)
        try:
            open(f"{work}/{self.case()}/posted").close()
            return self.send(204)
        except FileNotFoundError:
            return self.send(200, [PASSAGE])
    def do_POST(self):
        if self.headers.get("x-api-key") != key:
            return self.send(401)
        body = self.rfile.read(int(self.headers["Content-Length"])).decode()
        with open(f"{work}/{self.case()}/posted", "a") as f:
            f.write(body + "\n")
        self.send(200, {"stored": 1, "rejected": []})
    def log_message(self, *a):
        pass
server = ThreadingHTTPServer(("127.0.0.1", 0), H)
open(f"{work}/port", "w").write(str(server.server_address[1]))
server.serve_forever()
EOF
"$PYTHON" "$WORK/kavita.py" "$WORK" "$KEY" &
server_pid=$!
# Out of the job table, so its end at cleanup prints no "Terminated" line.
disown "$server_pid"
for _ in $(seq 50); do [ -s "$WORK/port" ] && break; sleep 0.1; done
BASE_URL="http://127.0.0.1:$(cat "$WORK/port")"

# claude stub. Records whether it saw the token and the model it was given,
# and answers with the passage's one item.
cat >"$WORK/bin/claude" <<'EOF'
#!/bin/bash
cat >/dev/null
prev=""
for arg in "$@"; do
  [ "$prev" = --model ] && echo "$arg" >>"$STUB_DIR/models"
  prev="$arg"
done
if [ "${CLAUDE_CODE_OAUTH_TOKEN:-}" = "sk-ant-oat01-test-token-value" ]; then
  echo token-ok >>"$STUB_DIR/claude-env"
else
  echo token-wrong >>"$STUB_DIR/claude-env"
fi
[ -n "${ANTHROPIC_API_KEY:-}" ] && echo paid-key >>"$STUB_DIR/claude-env"
echo '{"is_error": false, "structured_output": {"items": [{"id": 101, "sense": "1:1", "reading": "からい"}]}}'
EOF
chmod +x "$WORK/bin/claude"

# write_secret FILE VALUE MODE, where MODE is none, empty, wrapped (600, VALUE
# split across two lines) or a mode such as 600.
write_secret() {
  case "$3" in
    none) ;;
    empty) : >"$1"; chmod 600 "$1" ;;
    wrapped) printf '%s\n  %s\n' "${2:0:12}" "${2:12}" >"$1"; chmod 600 "$1" ;;
    *) printf '%s\n' "$2" >"$1"; chmod "$3" "$1" ;;
  esac
}

# setup NAME TOKEN_MODE KEY_MODE
setup() {
  case_name="$1"
  dir="$WORK/$1"
  mkdir -p "$dir/home"
  printf '%s\n' "$1" >"$WORK/case"
  token_file="$dir/home/claude-token"
  key_file="$dir/home/kavita-key"
  log_file="$dir/home/Library/Logs/kavita-reading.log"
  write_secret "$token_file" "$TOKEN" "$2"
  write_secret "$key_file" "$KEY" "$3"
}

# The script's environment, minimal like launchd's, plus RUN_ENV words. With
# RUN_EXEC=exec the calling shell becomes env, so its pid is the runner's.
script_env() {
  ${RUN_EXEC:-} env -i HOME="$dir/home" PATH=/usr/bin:/bin STUB_DIR="$dir" \
    KAVITA_READING_CLAUDE="$WORK/bin/claude" KAVITA_READING_PYTHON="$PYTHON" \
    KAVITA_READING_TOKEN_FILE="$token_file" KAVITA_READING_KEY_FILE="$key_file" \
    KAVITA_READING_LOG="$log_file" KAVITA_READING_BASE_URL="$BASE_URL" \
    ANTHROPIC_API_KEY=sk-paid-key ${RUN_ENV:-} "$@"
}

# run NAME TOKEN_MODE KEY_MODE runs one pass with --once and no terminal on stdout.
run() {
  setup "$@"
  status=0
  script_env /bin/bash "$SCRIPT" --once >"$dir/stdout" 2>&1 </dev/null || status=$?
  finish
}

finish() {
  touch "$dir/claude-env" "$log_file"
  for secret in "$TOKEN" "${TOKEN:12}" "$KEY" "${KEY:12}"; do
    if grep -q -- "$secret" "$log_file" "$dir/stdout"; then fail "a secret appears in the log"; fi
  done
}

expect_status() { [ "$status" -eq "$1" ] || fail "exit $status, wanted $1"; }
expect_log() { grep -q -- "$1" "$log_file" || fail "log lacks '$1'"; }
expect_no_claude() { [ ! -s "$dir/claude-env" ] || fail "claude ran"; }
expect_posted() { grep -q '"sense": "1:1"' "$dir/posted" 2>/dev/null || fail "nothing was posted"; }

run no_token none 600
expect_status 0
expect_log "runner not started, no Claude token file at"
expect_no_claude

run no_key 600 none
expect_status 0
expect_log "runner not started, no Kavita key file at"
expect_no_claude

run token_open_mode 644 600
expect_status 1
expect_log "claude-token has mode 644, needs 600"
expect_no_claude

run key_open_mode 600 640
expect_status 1
expect_log "kavita-key has mode 640, needs 600"
expect_no_claude

run token_empty empty 600
expect_status 1
expect_log "claude-token is empty"
expect_no_claude

run key_empty 600 empty
expect_status 1
expect_log "kavita-key is empty"
expect_no_claude

run ok 600 600
expect_status 0
expect_log "runner starting"
expect_log "pass: 1 passage(s), 0 failed, 1 kept, 0 filled, 1 stored"
expect_posted
grep -qx token-ok "$dir/claude-env" || fail "claude did not get the token"
if grep -qx paid-key "$dir/claude-env"; then fail "claude saw ANTHROPIC_API_KEY"; fi
[ "$(cat "$dir/models")" = sonnet ] || fail "the default model was $(cat "$dir/models")"

run wrapped wrapped wrapped
expect_status 0
expect_posted
grep -qx token-ok "$dir/claude-env" || fail "claude did not get the unwrapped token"

RUN_ENV="KAVITA_READING_MODEL=haiku"
run model_from_env 600 600
RUN_ENV=""
expect_status 0
[ "$(cat "$dir/models")" = haiku ] || fail "the model was $(cat "$dir/models")"
grep -q '"generator": "claude-code:haiku"' "$dir/posted" || fail "the answers were not posted as haiku's"

RUN_ENV="KAVITA_READING_PARALLEL=9"
run bad_parallel 600 600
RUN_ENV=""
expect_status 2
expect_no_claude

# Without --once the runner keeps polling until launchd's SIGTERM, then exits 0.
# The script execs it, so the signal goes straight to the runner.
setup sigterm 600 600
(RUN_EXEC=exec script_env /bin/bash "$SCRIPT" --idle 0.1 >"$dir/stdout" 2>&1 </dev/null) &
pid=$!
for _ in $(seq 100); do [ -s "$dir/posted" ] && break; sleep 0.1; done
sleep 0.3
kill -TERM "$pid"
status=0
wait "$pid" || status=$?
finish
expect_status 0
expect_posted
expect_log "SIGTERM received"
expect_log "annotate_reading: stopped"

if [ "$failures" -eq 0 ]; then
  echo "ok, all run-reading.sh cases passed"
else
  echo "$failures failure(s)"
  exit 1
fi
