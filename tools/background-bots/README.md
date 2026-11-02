# Windowless background bots

Installed November 2, 2026 to `%LOCALAPPDATA%\ING\BackgroundBots`.

`runner.py`, launched by the installed Python 3.13 `pythonw.exe`, runs the original executable and arguments with `CREATE_NO_WINDOW`, preserves the configured working directory and exit status, and writes stdout/stderr to a per-bot log. Its child environment opts into a private `sitecustomize.py` that applies the same hidden-window policy to Python subprocesses, including queue workers' CMD/PowerShell helpers. This opt-in is local to these bot trees; interactive terminals and other Python programs are unaffected.

`install.ps1` saves original task XML before changing only each matching ING task's launch action. It does not change schedules, credentials, run level, or enabled/disabled state. Logs, original command configurations, and XML backups are in the installation directory. Two former Startup CMD entries (BitData PostgreSQL and OwNinja Collector) now use Pythonw shortcuts; original CMD files remain alongside them with `.disabled-<timestamp>` extensions and in the backup directory.

Verified: 16 scheduled launch actions changed, disabled tasks remained disabled, hidden-launch integration probe passed including a child CMD command. The idle news-loop supervisor was relaunched through this wrapper with its original `--across-reset` argument; the active price collector was not restarted. The single backlinks queue job finished on its own.

Exception: `ING Driver Update` task is protected against modification by the current Windows account. Its launch action was left unchanged; modifying it requires Windows administrator access. Installation reports and skips such errors.

The live verification report is `verification.json`. To restore an individual scheduled task action, use its backed-up XML and the task's existing identity. To restore a Startup entry, remove its new shortcut and rename the preserved original CMD back. Do not start a second copy of a currently running bot.
