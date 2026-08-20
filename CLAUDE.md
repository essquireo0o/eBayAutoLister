# Working in this repo

## Multiple Claude sessions run here at once. Follow the lane protocol.

The owner often has two or more sessions (VS + terminal) on this repo at the same
time. Both read this file. The rules that keep that safe:

1. **Claim your lane in `.claude/LANES.md` before editing.** One line:
   `<area or files> — <session description> — <date/time>`. Remove the line when done.
   If the files you need are claimed, work elsewhere or coordinate through the owner.
2. **Commit early, commit small, push often.** Uncommitted work is the only thing the
   other session can destroy. A committed change is safe from everything but a
   deliberate rewrite.
3. **Never `git reset --hard`, `checkout -- .`, restore, or clean without reading
   `git status` first.** Unstaged changes you didn't make belong to the other session —
   they are work in progress, not noise to sweep.
4. **Pull/rebase before push; on a push race, rebase, never force.**
5. **One process per resource.** Before starting/stopping AutoListerB1 (port 9332),
   building (bin locks), flashing or probing the camera on its COM port: check whether
   the other session is mid-flight (LANES.md note, or a running process). These are
   exclusive resources; two users = corrupted flash / phantom build failures.
6. **Don't "rescue" the other session's edits** by committing files you didn't change
   under your own message. If foreign modified files block your commit, stage only
   your own paths.

## Build & test quickies (both sessions)

- wwwroot is EMBEDDED — rebuild before UI testing; a stray running AutoListerB1.exe
  locks bin\ and fails builds (stop it, rebuild, restart it — never leave it stopped).
- `dotnet test` fails while the app runs from bin\Debug (file lock).
- Hosted vs desktop is the compile-time `HOSTED` define; the MSI publish never sets it.
- Ship = `publish-update.ps1` (site MSI upload + verify) + GitHub release with the
  version bumped in BOTH the csproj `<Version>` and installer.wxs.
