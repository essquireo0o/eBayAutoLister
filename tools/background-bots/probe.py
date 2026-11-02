import os
import subprocess
assert os.environ['ING_BACKGROUND_BOT'] == '1'
assert subprocess.Popen.__init__.__name__ == '_hidden_init'
r = subprocess.run([os.environ['COMSPEC'], '/d', '/c', 'echo hidden-child-ok'], capture_output=True, text=True, check=True)
assert r.stdout.strip() == 'hidden-child-ok'
print('Windowless launch and inherited child-process policy: PASS', flush=True)
