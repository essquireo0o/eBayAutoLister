"""Run one existing bot command invisibly, keeping its cwd, exit code and logs."""
import datetime
import json
import os
from pathlib import Path
import subprocess
import sys
import traceback

config = json.loads(Path(sys.argv[1]).read_text(encoding='utf-8-sig'))
root = Path(__file__).resolve().parent
logpath = Path(config['log'])
logpath.parent.mkdir(parents=True, exist_ok=True)
env = dict(os.environ, ING_BACKGROUND_BOT='1', PYTHONUTF8='1', PYTHONIOENCODING='utf-8')
env['PYTHONPATH'] = str(root) + (os.pathsep + env['PYTHONPATH'] if env.get('PYTHONPATH') else '')
command = subprocess.list2cmdline([config['exe']]) + ' ' + config.get('args', '')
with logpath.open('a', encoding='utf-8', buffering=1) as log:
    log.write(f'\n{datetime.datetime.now().isoformat()} starting background bot\n')
    try:
        result = subprocess.run(command, executable=config['exe'], cwd=config.get('cwd') or None,
                                env=env, stdin=subprocess.DEVNULL, stdout=log, stderr=log,
                                creationflags=subprocess.CREATE_NO_WINDOW)
        log.write(f'{datetime.datetime.now().isoformat()} exit={result.returncode}\n')
        sys.exit(result.returncode)
    except Exception:
        traceback.print_exc(file=log)
        sys.exit(1)
