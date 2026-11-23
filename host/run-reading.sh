#!/bin/bash
# Starts host/annotate_reading.py for launchd, which keeps it running.
#
# It checks the Claude token file and the Kavita key file, then execs the runner
# with the token in the runner's environment only. The runner reads the key file
# itself. With either file missing it logs a line and exits 0, and the plist
# doesn't restart a run that exited 0, so a Mac without the files stays quiet.
# A file with the wrong mode, or an empty one, exits 1.
#
# launchd gives a minimal PATH, so every program is named by absolute path.
# The KAVITA_READING_* overrides for paths exist for host/test_run_reading.sh.
#
# KAVITA_READING_BASE_URL, KAVITA_READING_MODEL, KAVITA_READING_PARALLEL,
# KAVITA_READING_MAX_CALLS_PER_HOUR, KAVITA_READING_WINDOW_START and
# KAVITA_READING_WINDOW_END reach the runner unchanged, so the plist's
# EnvironmentVariables can set them without an edit here.

set -euo pipefail

CLAUDE="${KAVITA_READING_CLAUDE:-/Users/jeronimolopez/.local/bin/claude}"
# The runner needs Python 3.11 or later. /usr/bin/python3 is 3.9.
PYTHON="${KAVITA_READING_PYTHON:-/opt/homebrew/bin/python3.12}"
TOKEN_FILE="${KAVITA_READING_TOKEN_FILE:-$HOME/.config/kavita-reading/claude-token}"
KEY_FILE="${KAVITA_READING_KEY_FILE:-$HOME/.config/kavita-reading/kavita-key}"
LOG="${KAVITA_READING_LOG:-$HOME/Library/Logs/kavita-reading.log}"
RUNNER="${KAVITA_READING_RUNNER:-$(cd "$(dirname "$0")" && pwd)/annotate_reading.py}"

/bin/mkdir -p "$(dirname "$LOG")"
# From a terminal, show the lines and append them to the log. Under launchd,
# append only.
if [ -t 1 ]; then
  exec > >(/usr/bin/tee -a "$LOG") 2>&1
else
  exec >>"$LOG" 2>&1
fi

log() { printf '%s  %s\n' "$(/bin/date '+%Y-%m-%d %H:%M:%S')" "$*"; }

# check_file NAME PATH exits 0 when the file is missing and 1 when its mode
# isn't 600 or it holds nothing but whitespace.
check_file() {
  if [ ! -e "$2" ]; then
    log "runner not started, no $1 file at $2"
    exit 0
  fi
  local mode
  mode="$(/usr/bin/stat -f '%Lp' "$2")"
  if [ "$mode" != "600" ]; then
    log "ERROR: $2 has mode $mode, needs 600 (chmod 600 it), runner not started"
    exit 1
  fi
  if [ -z "$(/usr/bin/tr -d '[:space:]' <"$2")" ]; then
    log "ERROR: $2 is empty, runner not started"
    exit 1
  fi
}

check_file "Claude token" "$TOKEN_FILE"
check_file "Kavita key" "$KEY_FILE"

# Whitespace goes, so a token pasted from a terminal that wrapped it still works.
token="$(/usr/bin/tr -d '[:space:]' <"$TOKEN_FILE")"

log "runner starting"
# exec, so launchd's SIGTERM reaches the runner itself.
CLAUDE_CODE_OAUTH_TOKEN="$token" exec "$PYTHON" "$RUNNER" \
  --claude "$CLAUDE" --key-file "$KEY_FILE" "$@"
