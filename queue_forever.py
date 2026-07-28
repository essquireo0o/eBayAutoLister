#!/usr/bin/env python3
"""
Perpetual supervisor for the ING AutoLister autonomous loop. NEVER stops (until you kill it):
  - keeps the queue topped up to TARGET with fresh, guardrailed, high-value tasks, and
  - relaunches the claude-queue worker if it ever dies.

Every queued task still build+test+commit-or-reverts on the isolated branch, free beta, no paywalls.

Run:   python queue_forever.py         (launch via the queue in the background)
Stop:  kill this python process (Stop-Process), or delete this and stop the worker.
"""
import subprocess, sys, os, json, time

CQ   = [sys.executable, r"C:/Users/nsquires/source/repos/claude-queue/claude-queue.py"]
QF   = r"C:/Users/nsquires/.claude-queue/tasks.json"
LOG  = r"C:/Users/nsquires/source/repos/claude-queue-worker.log"
PROJ = r"C:/Users/nsquires/source/repos/ING eBay AutoLister"
TARGET   = 12      # keep at least this many queued
INTERVAL = 150     # seconds between supervisor cycles

GUARD = r"""GUARDRAILS - FULL AUTONOMY; NEVER ask the user, NEVER wait, NEVER stop to confirm - decide and proceed. Free beta, NO paywalls/subscriptions/gating.
VISION: optimize for (1) "how does Nick make money?" grow the seller's profit/sales; (2) "what is Nick missing?" vs Vendoo/List Perfectly/ZIK/Crosslist; (3) "the most valuable eBay-seller app in the world."
- FIRST run: git log --oneline -30 and read WORK_COMPLETED.md; do NOT duplicate - pick the highest-leverage NEW or unfinished work.
- Repo: C:\Users\nsquires\source\repos\ING eBay AutoLister on branch auto/queue-features-20260726. ONE cohesive END-TO-END change, high quality.
- BEFORE finishing run BOTH: dotnet build "ING eBay AutoLister/ING eBay AutoLister.csproj" -c Debug  AND  dotnet test "ING eBay AutoLister.Tests/ING eBay AutoLister.Tests.csproj".
- Fail -> revert (git checkout -- . ; git clean -fd) and STOP. Green -> git add -A && git commit; bump wwwroot/index.html ?v= if app.js changed; document in WORK_COMPLETED.md.
- NEVER: git push, publish live eBay, read/print secrets, delete user data, change keys, rm -rf, git reset --hard, touch files outside the repo."""

POOL = [
 ("Add or improve a high-value seller feature not yet present; document the money impact in WORK_COMPLETED.md.", "auto-feature"),
 ("Polish and harden the local-arbitrage (Craigslist/Facebook) experience: reliability, clearer per-source status, better profit ranking.", "auto-arbitrage"),
 ("Improve the listing workflow (item -> live listing): fewer clicks, smarter defaults, better autofill.", "auto-workflow"),
 ("Premium design/UX polish on a screen so the app feels worth paying for; no logic breakage.", "auto-design"),
 ("Add or expand unit tests for an under-tested service.", "auto-tests"),
 ("Improve pricing/confidence transparency so the numbers are trustworthy.", "auto-pricing"),
 ("Harden error handling / recovery on a critical path so users never lose work.", "auto-reliability"),
 ("Improve SEO / title / item-specifics completeness to get more views and sales.", "auto-seo"),
 ("Add an analytics insight or dashboard that helps the seller decide what to list/source.", "auto-analytics"),
 ("Improve the Opportunity Finder / sourcing recommendations quality and presentation.", "auto-opportunity"),
 ("Accessibility + keyboard + responsive polish on one section.", "auto-a11y"),
 ("Improve the photo pipeline or representative-library UX.", "auto-photos"),
 ("Add a quality-of-life feature that saves the seller time at scale (bulk actions, shortcuts, templates).", "auto-qol"),
 ("Improve cross-listing / multi-marketplace value (more destinations, better exports).", "auto-crosslist"),
 ("Find a rough edge or TODO in the code and polish it properly with tests.", "auto-polish"),
 ("Improve onboarding so a new beta tester immediately understands setup and value.", "auto-onboarding"),
]

def queued_count():
    for _ in range(6):
        try:
            d = json.load(open(QF))
            it = d if isinstance(d, list) else (list(d.get("tasks", d).values()) if isinstance(d.get("tasks", d), dict) else d.get("tasks", d))
            return sum(1 for t in it if t.get("status") == "queued")
        except Exception:
            time.sleep(0.4)
    return TARGET  # on read error, assume OK so we don't over-add

def worker_running():
    ps = ("(Get-CimInstance Win32_Process -Filter \"Name='python3.12.exe' OR Name='python.exe'\" | "
          "Where-Object { $_.CommandLine -match 'claude-queue.py worker' } | Measure-Object).Count")
    try:
        r = subprocess.run(["powershell", "-NoProfile", "-Command", ps], capture_output=True, text=True, timeout=30)
        return int((r.stdout or "0").strip() or "0") > 0
    except Exception:
        return True  # if we can't tell, don't spawn a duplicate

def launch_worker():
    f = open(LOG, "a", encoding="utf-8", errors="replace")
    subprocess.Popen(CQ + ["worker"], cwd=PROJ, stdout=f, stderr=f,
                     creationflags=getattr(subprocess, "CREATE_NEW_PROCESS_GROUP", 0))
    print("supervisor: launched a worker", flush=True)

def add_task(prompt, session):
    for _ in range(10):
        r = subprocess.run(CQ + ["add", GUARD + "\n\nTASK: " + prompt, "--session", session, "--priority", "0"],
                           capture_output=True, text=True)
        if r.returncode == 0:
            return True
        time.sleep(0.5)
    return False

def main():
    i = 0
    print("supervisor: perpetual loop started", flush=True)
    while True:
        try:
            if not worker_running():
                launch_worker()
                time.sleep(6)
            q = queued_count()
            if q < TARGET:
                need = TARGET - q
                for _ in range(need):
                    prompt, pre = POOL[i % len(POOL)]
                    add_task(prompt, f"{pre}-{int(time.time())}-{i}")
                    i += 1
                print(f"supervisor: topped up +{need} (was {q})", flush=True)
        except Exception as e:
            print("supervisor: cycle error:", e, flush=True)
        time.sleep(INTERVAL)

if __name__ == "__main__":
    main()
