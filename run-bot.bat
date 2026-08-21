@echo off
rem Persistent launcher for the endless upgrades supervisor. Uses the REAL (non-Store) Python 3.13,
rem which WMI/detached launches reliably, and its own log. Started detached via Win32_Process.Create.
cd /d "C:\Users\nsquires\source\repos\ING eBay AutoLister"
"C:\Users\nsquires\AppData\Local\Programs\Python\Python313\python.exe" queue_forever.py >> "C:\Users\nsquires\source\repos\queue-bot.log" 2>&1
