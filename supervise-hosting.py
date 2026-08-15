#!/usr/bin/env python3
"""
Keep the hosting queue running until every task is done, then stop.

The claude-queue worker exits on its own in two situations that both look like "finished" and
are not: it drains the queue while dependencies are still blocking later tasks, and it dies. Left
alone, the build stalls silently with tasks still queued. This watches the queue file and
relaunches the worker whenever there is work to do and nobody doing it.

UNLIKE queue_forever.py (the perpetual supervisors for the app and SEO pools) this one TERMINATES.
It is building a finite thing, so it exits when the queue is drained — either all completed, or
stuck with failures it reports rather than retrying forever.

    python supervise-hosting.py                 # run until done
    python supervise-hosting.py --status        # print state and exit

Log: source/repos/hosting-supervisor.log
"""

import argparse
import datetime as dt
import json
import os
import pathlib
import subprocess
import sys
import time

HOME    = pathlib.Path.home()
QUEUE   = HOME / '.claude-queue' / 'hosting-tasks.json'
CLI     = HOME / 'source' / 'repos' / 'claude-queue' / 'claude-queue.py'
WORKLOG = HOME / 'source' / 'repos' / 'hosting-queue-worker.log'
SUPLOG  = HOME / 'source' / 'repos' / 'hosting-supervisor.log'
PIDFILE = HOME / '.claude-queue' / 'hosting-worker.pid'

POLL           = 60      # seconds between checks
STALL_MINUTES  = 45      # a single task legitimately takes ~10-20 min; longer means wedged
TERMINAL       = {'completed', 'failed'}


def log(msg):
    line = f'{dt.datetime.now():%Y-%m-%d %H:%M:%S}  {msg}'
    print(line, flush=True)
    with open(SUPLOG, 'a', encoding='utf-8') as fh:
        fh.write(line + '\n')


def tasks():
    try:
        return json.loads(QUEUE.read_text(encoding='utf-8'))
    except (OSError, json.JSONDecodeError):
        # The worker rewrites this file; a read landing mid-write is normal, not a failure.
        return None


def counts(ts):
    out = {}
    for t in ts:
        out[t['status']] = out.get(t['status'], 0) + 1
    return out


def worker_pids():
    """
    PIDs of every claude-queue worker running against THIS queue file.

    Matched on the command line, not on a pid file. The first version of this trusted a pid
    written by the shell that launched the worker — on Windows that is the launcher's pid, not
    python's, so the check said "no worker" while one was running and the supervisor started a
    second. Three ended up racing on one queue file, which in this repo means three processes
    that each run `git checkout -- . ; git clean -fd` on a failed build. Never guess at this.

    The interpreter is python3.12.exe here and python.exe elsewhere, so match on the script name
    rather than the image name.
    """
    ps = ("Get-CimInstance Win32_Process | "
          "Where-Object { $_.CommandLine -match 'claude-queue\\.py' -and "
          "$_.CommandLine -match 'worker' -and $_.CommandLine -match 'hosting-tasks' } | "
          "ForEach-Object { \"$($_.ProcessId) $($_.ParentProcessId)\" }")
    try:
        out = subprocess.run(['powershell', '-NoProfile', '-Command', ps],
                             capture_output=True, text=True, timeout=60).stdout
    except Exception:
        return []

    pairs = []
    for line in out.splitlines():
        bits = line.split()
        if len(bits) == 2 and bits[0].isdigit() and bits[1].isdigit():
            pairs.append((int(bits[0]), int(bits[1])))

    # A worker launched through nohup shows up TWICE — the wrapper and the real python both carry
    # the same command line. Counting both made one worker look like two and the duplicate-killer
    # would have shot the wrapper. Keep only leaves: a pid that is not the parent of another match.
    parents = {ppid for _, ppid in pairs}
    return [pid for pid, _ in pairs if pid not in parents]


def worker_alive():
    pids = worker_pids()
    if len(pids) > 1:
        # Kill the extras rather than tolerate them: concurrent workers on one queue corrupt
        # each other's working tree. Keep the lowest pid, which is the oldest and most likely
        # to be mid-task.
        for pid in sorted(pids)[1:]:
            log(f'killing duplicate worker {pid} — two workers on one queue race each other')
            subprocess.run(['taskkill', '/PID', str(pid), '/F'], capture_output=True, timeout=30)
    return bool(pids)


def start_worker():
    WORKLOG.parent.mkdir(parents=True, exist_ok=True)
    fh = open(WORKLOG, 'a', encoding='utf-8', errors='replace')

    # PYTHONIOENCODING is the whole point of this env copy. The worker reads the Claude CLI's
    # stdout with the console default, cp1252 on this machine, and the CLI emits UTF-8 — so the
    # reader thread dies on the first non-Latin-1 byte with
    #   UnicodeDecodeError: 'charmap' codec can't decode byte 0x90
    # and then "Failed to save output: write() argument must be str, not None". The worker keeps
    # running, but its log stops updating, which makes the log actively misleading: it looks like
    # the worker died when it did not. Forcing UTF-8 keeps the log honest.
    env = dict(os.environ, PYTHONIOENCODING='utf-8', PYTHONUTF8='1')

    p = subprocess.Popen(
        [sys.executable, str(CLI), '--queue-file', str(QUEUE), 'worker', '--save-output'],
        stdout=fh, stderr=subprocess.STDOUT, cwd=str(CLI.parent), env=env,
        creationflags=getattr(subprocess, 'CREATE_NO_WINDOW', 0))
    PIDFILE.write_text(str(p.pid), encoding='utf-8')
    log(f'started worker pid {p.pid}')
    return p


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--status', action='store_true')
    args = ap.parse_args()

    ts = tasks()
    if ts is None:
        log('cannot read the queue file'); return 1

    if args.status:
        print(json.dumps(counts(ts), indent=1))
        for t in sorted(ts, key=lambda x: -x.get('priority', 0)):
            print(f"  p{t.get('priority'):<3} {t['session_name']:<34} {t['status']:<10} att {t.get('attempts')}")
        return 0

    log(f'supervisor up — {counts(ts)}')
    last_sig, last_change = None, time.time()

    while True:
        ts = tasks()
        if ts is None:
            time.sleep(POLL); continue

        c = counts(ts)
        remaining = [t for t in ts if t['status'] not in TERMINAL]

        # Done: nothing left that is not finished one way or the other.
        if not remaining:
            failed = [t['session_name'] for t in ts if t['status'] == 'failed']
            log(f'ALL DONE — {c}')
            if failed:
                log(f'FAILED: {", ".join(failed)}')
                log('these need a human: read ~/.claude-queue/outputs/ for their output')
                return 2
            log('every task completed successfully')
            return 0

        # Progress detection: the signature is what each task is doing right now.
        sig = tuple(sorted((t['session_name'], t['status'], t.get('attempts', 0)) for t in ts))
        if sig != last_sig:
            last_sig, last_change = sig, time.time()
            log(f'{c}  running: {[t["session_name"] for t in ts if t["status"]=="running"] or "-"}')

        stalled = (time.time() - last_change) / 60

        if not worker_alive():
            # A worker that exited with work outstanding is the normal case this exists for:
            # it drains what it can reach, then stops while dependencies still block the rest.
            log(f'no worker running, {len(remaining)} task(s) outstanding — relaunching')
            start_worker()
            last_change = time.time()
        elif stalled > STALL_MINUTES:
            log(f'no state change for {stalled:.0f} min with a worker alive — leaving it alone, '
                f'but this may be wedged; check {WORKLOG}')
            last_change = time.time()   # report once per window, do not kill real work

        time.sleep(POLL)


if __name__ == '__main__':
    sys.exit(main())
