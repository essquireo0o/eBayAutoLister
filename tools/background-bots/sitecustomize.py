"""Hide subprocess consoles only inside an explicitly opted-in background bot."""
import os
import subprocess

if os.name == 'nt' and os.environ.get('ING_BACKGROUND_BOT') == '1':
    _original_init = subprocess.Popen.__init__

    def _hidden_init(self, *args, **kwargs):
        flags = kwargs.get('creationflags', 0)
        # Detached/new-console flags defeat CREATE_NO_WINDOW. Keep other flags intact.
        kwargs['creationflags'] = (flags & ~0x00000018) | subprocess.CREATE_NO_WINDOW
        startup = kwargs.get('startupinfo') or subprocess.STARTUPINFO()
        startup.dwFlags |= subprocess.STARTF_USESHOWWINDOW
        startup.wShowWindow = 0
        kwargs['startupinfo'] = startup
        return _original_init(self, *args, **kwargs)

    subprocess.Popen.__init__ = _hidden_init
