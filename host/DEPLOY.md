# Running the reading runner

`host/annotate_reading.py` picks the dictionary sense and the reading that fit each ambiguous word in your Japanese books. It asks Kavita for pending passages, sends each one to `claude -p` on your Claude subscription, checks the answer against what Kavita offered and posts what passes. A word the model gave no usable answer for is posted with a null sense and reading, so Kavita records it as asked and doesn't offer it again. launchd keeps it running in the background, and it polls Kavita every few seconds.

It never uses a paid API key. It removes `ANTHROPIC_API_KEY` and `ANTHROPIC_AUTH_TOKEN` from the environment of every `claude` call, so a key set in your shell can't be billed by mistake.

## Before you start

- Kavita is up on `http://localhost:6267` with the dictionary endpoints, and you have an admin account.
- The Claude Code CLI is at `~/.local/bin/claude` and Python 3.12 is at `/opt/homebrew/bin/python3.12`. The runner needs 3.11 or later, and `/usr/bin/python3` is 3.9.
- `~/Workshop/Kavita` is on the `custom` branch with `host/` in it, since the plist points at `~/Workshop/Kavita/host/run-reading.sh`.

## 1. Install the Claude token

`claude setup-token` prints a long-lived OAuth token for your subscription. Store it outside the repo, readable only by you.

```sh
claude setup-token
install -d -m 700 ~/.config/kavita-reading
/bin/bash -c 'umask 077; read -rsp "paste token: " t; echo; printf "%s\n" "$t" > ~/.config/kavita-reading/claude-token'
stat -f '%Lp' ~/.config/kavita-reading/claude-token   # must print 600
```

The `read -s` keeps the token out of your shell history and off the screen. `run-reading.sh` strips every space and line break from it, so a token the terminal wrapped across two lines still works, and it passes the token to the runner's environment only. When the token expires, the log says `claude could not log in` and the runner stops calling `claude` for 30 minutes at a time. Run `claude setup-token` again and overwrite the file the same way. The runner reads the token only when it starts, so restart it with `launchctl kickstart -k gui/$(id -u)/com.jeronimolopez.kavita-reading`.

If you've already made a token for shimen, the same one works here. Copy `~/.config/shimen/claude-token` to the new path and keep its mode at 600.

## 2. Install the Kavita key

The runner sends an auth key to Kavita with every request. In Kavita, open Settings, then Auth Keys / OPDS, and click New. Name it `kavita-reading` and pick an expiration. With Never you won't have to come back to this step. Copy the key and write it to its own file:

```sh
/bin/bash -c 'umask 077; read -rsp "paste key: " k; echo; printf "%s\n" "$k" > ~/.config/kavita-reading/kavita-key'
stat -f '%Lp' ~/.config/kavita-reading/kavita-key   # must print 600
```

Make the key on an admin account, because the two analysis endpoints are admin only. The key goes in the `x-api-key` header and never in a URL, so it stays out of Kavita's request log. Kavita gives a request with that header the roles of the key's owner. If the key is deleted or expires, the log says `Kavita turned the auth key down` and the runner waits 30 minutes between tries. The runner reads the key file only when it starts, so after you write a new key, restart the runner with the `kickstart -k` command from step 1.

## 3. Try a dry run

Before you load the agent, ask about a few passages and look at what comes back. A dry run calls `claude` and prints each answer it would post as one line of JSON, and it posts nothing.

```sh
bash ~/Workshop/Kavita/host/run-reading.sh --dry-run --limit 3
```

Each line carries the word as written and the sense and reading the model chose. It also has that sense's English gloss, so you can judge the choice without looking the key up. A line with `"filled": true` is an item the model left out, an answer the runner dropped, or a word it couldn't find in the text, and its sense and reading are null. On stderr you'll see a line per passage with its time, such as `passage 41 (12 items, 1984 chars) answered in 14.2s, 12 kept`. A line follows for each answer the runner dropped and for each passage it filled in, and the pass ends with a summary such as `pass: 3 passage(s), 0 failed, 31 kept, 2 filled, 33 would be posted, nothing posted`.

If you leave out `--dry-run` and use `--once`, one pass runs and posts. It exits 0 when every passage was posted or nothing was pending. It exits 3 when some passages failed and the rest were posted, and 1 when nothing could be done.

Nothing's pending until a book is tokenized and someone opens it or runs Analyze this book. With nothing pending, a dry run prints no JSON and logs `nothing pending at priority 0 and up`.

## 4. Load the launchd agent

```sh
cp ~/Workshop/Kavita/host/com.jeronimolopez.kavita-reading.plist ~/Library/LaunchAgents/
launchctl bootstrap gui/$(id -u) ~/Library/LaunchAgents/com.jeronimolopez.kavita-reading.plist
launchctl print gui/$(id -u)/com.jeronimolopez.kavita-reading | grep -E 'state|pid|last exit'
```

It starts at once and again at each login. If it crashes, launchd starts it again after 60 seconds. When a token or key file is missing it logs one line and exits 0, and launchd leaves it stopped, so add the file and start it by hand:

```sh
launchctl kickstart gui/$(id -u)/com.jeronimolopez.kavita-reading
```

## 5. Read the log

```sh
tail -f ~/Library/Logs/kavita-reading.log
```

A healthy runner logs `runner starting` and then a `started` line with its settings. Each time it finds work, it logs a line per passage and a `pass:` summary. While nothing is pending it logs nothing. These are the lines that need you:

| Line | What to do |
| -- | -- |
| `claude could not log in`, then `pausing 1800s` | Make a new token (step 1). |
| `claude hit its usage limit` | Nothing. It tries again in 30 minutes. |
| `Kavita turned the auth key down` | Make a new key (step 2). |
| `needs the admin role` | Make the key on an admin account. |
| `cannot reach http://localhost:6267` or `answered 503`, then `trying again in ...` | Start Kavita. The wait doubles up to 5 minutes, and no `claude` call runs until a post goes through again. |
| `the hourly cap of 60 calls is reached` | Nothing, or raise the cap (next section). |
| `has mode 644, needs 600` | `chmod 600` the file it names, then kickstart. |

The token and the key never appear in the log. Each line has them replaced with `[redacted]` before it's written.

## 6. Stop or remove it

```sh
launchctl bootout gui/$(id -u)/com.jeronimolopez.kavita-reading
```

launchd sends SIGTERM. The runner ends the `claude` calls under way, lets any post already sent to Kavita finish and exits 0. A passage whose call it ended stays pending and runs again next time. To remove the agent for good, boot it out and delete `~/Library/LaunchAgents/com.jeronimolopez.kavita-reading.plist`.

## Settings

Each setting goes in the plist's `EnvironmentVariables`, next to `PATH`, and it's read once at start. To change one, boot the agent out, edit the copy in `~/Library/LaunchAgents/`, and bootstrap it again. A flag on the command line wins over the variable.

| Variable | Flag | Default | What it does |
| -- | -- | -- | -- |
| `KAVITA_READING_BASE_URL` | `--base-url` | `http://localhost:6267` | Where Kavita answers. |
| `KAVITA_READING_MODEL` | `--model` | `sonnet` | The model alias passed to `claude --model`. Answers are posted as `claude-code:<model>`. |
| `KAVITA_READING_PARALLEL` | `--parallel` | `2` | How many passages go to `claude` at once, 1 to 4. |
| `KAVITA_READING_MAX_CALLS_PER_HOUR` | `--max-calls-per-hour` | `60` | The most `claude` calls in any hour. At the cap it sleeps until the oldest call is an hour old. |
| `KAVITA_READING_WINDOW_START`, `KAVITA_READING_WINDOW_END` | `--window-start`, `--window-end` | empty | The night window, as `HH:MM` in the Mac's time zone. Whole-book work (priority 0) runs only inside it, and pages you open (priority 10 and up) run at any hour. A window such as `23:00` to `07:00` runs past midnight. Both empty means always. |
| `KAVITA_READING_KEY_FILE` | `--key-file` | `~/.config/kavita-reading/kavita-key` | The Kavita key file. |
| `KAVITA_READING_TOKEN_FILE` | | `~/.config/kavita-reading/claude-token` | The Claude token file, read by `run-reading.sh`. |
| `KAVITA_READING_CLAUDE` | `--claude` | `~/.local/bin/claude` | The `claude` executable. |
| `KAVITA_READING_PYTHON` | | `/opt/homebrew/bin/python3.12` | The Python that runs the runner. |
| `KAVITA_READING_LOG` | | `~/Library/Logs/kavita-reading.log` | Where `run-reading.sh` writes. Keep it the same as the plist's log paths. |

For example, to keep whole-book work to the night and run three passages at once:

```xml
<key>KAVITA_READING_WINDOW_START</key>
<string>01:00</string>
<key>KAVITA_READING_WINDOW_END</key>
<string>07:00</string>
<key>KAVITA_READING_PARALLEL</key>
<string>3</string>
```

Four settings have a flag and no variable. To set one for the agent, add it to the plist's `ProgramArguments` after the script path.

| Flag | Default | What it does |
| -- | -- | -- |
| `--limit` | `4` | Passages per fetch. |
| `--timeout` | `300` | Seconds one `claude` call may take. |
| `--pause` | `1800` | Seconds to stop calls after a login or usage-limit failure. |
| `--idle` | `5` | Seconds between empty polls. After about a minute of nothing it doubles each time, up to 60. |

The hourly cap is kept in memory, so a restart forgets the calls of the last hour and the runner can make up to twice the cap in that hour.

## Testing the host scripts

`host/tests/test_annotate_reading.py` runs the runner as a process against a fake Kavita and a fake `claude`, with no network. It covers answers with senses or readings that weren't offered for their item, invented ids, items posted as null, a key Kavita turns down, a post Kavita can't take, a `claude` login failure, an error that only mentions a login, the hourly cap, the night window, a dry run, the `--once` exit codes and SIGTERM. Every case checks that the token and the key stay out of stdout and stderr. It doesn't need pytest, and it runs under pytest too where that's installed:

```sh
cd ~/Workshop/Kavita
/opt/homebrew/bin/python3.12 -m unittest discover -s host/tests
```

`host/test_run_reading.sh` runs `run-reading.sh` against a stub `claude` and a small fake Kavita, with launchd's minimal environment. It covers missing files, files with too open a mode, empty files, a token and key wrapped across two lines, a model set in the environment, and SIGTERM reaching the runner through the script's `exec`.

```sh
bash ~/Workshop/Kavita/host/test_run_reading.sh
```
