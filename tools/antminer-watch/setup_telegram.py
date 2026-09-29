"""Interactive pairing. The bot token never enters a command line or log."""
import queue
import secrets
import threading
import time
import tkinter as tk
import webbrowser
from tkinter import ttk
import telegram_delivery as tg

def main():
    root = tk.Tk()
    root.title('Antminer alerts — connect Telegram')
    root.geometry('610x410')
    root.resizable(False, False)
    box = ttk.Frame(root, padding=20)
    box.pack(fill='both', expand=True)
    ttk.Label(box, text='Get Antminer deal alerts in Telegram',
              font=('Segoe UI', 16, 'bold')).pack(anchor='w')
    ttk.Label(box, text='1. Open BotFather, send /newbot, and follow its instructions.\n'
        '2. Paste the bot token below and click Connect.\n'
        '3. Open the pairing link and press Start in Telegram.',
        wraplength=560, justify='left').pack(anchor='w', pady=12)
    ttk.Button(box, text='Open BotFather',
        command=lambda: webbrowser.open('https://t.me/BotFather')).pack(anchor='w')
    token = tk.StringVar()
    ttk.Entry(box, textvariable=token, show='*', width=75).pack(fill='x', pady=12)
    status = tk.StringVar(value='The token is saved encrypted for this Windows account.')
    ttk.Label(box, textvariable=status, wraplength=560).pack(anchor='w', pady=8)
    events = queue.Queue()
    cancel = threading.Event()
    pair_url = ['']
    pair = ttk.Button(box, text='Open my bot and press Start',
        command=lambda: webbrowser.open(pair_url[0]))

    def worker(secret):
        try:
            bot = tg.call(secret, 'getMe')
            nonce = secrets.token_urlsafe(24)
            events.put(('pair', 'https://t.me/' + bot['username'] + '?start=' + nonce))
            offset = None
            deadline = time.monotonic() + 600
            while not cancel.is_set() and time.monotonic() < deadline:
                data = {'timeout': 20, 'allowed_updates': ['message']}
                if offset is not None:
                    data['offset'] = offset
                updates = tg.call(secret, 'getUpdates', data)
                if cancel.is_set():
                    return
                chat = tg.paired_chat(updates, nonce)
                if chat:
                    config = {'token': secret, 'chat_id': chat, 'username': bot['username']}
                    mid = tg.send('Antminer alerts connected — test message',
                        'This is a delivery test, not a deal.\n\n'
                        'Alerts require at least 30% and $100 savings against matched recent sales, '
                        'including stated shipping. Working units with PSU evidence only. Taxes excluded.\n\n'
                        'Checks run every minute while your PC is awake, signed in, '
                        'and ING Listing Engine is running.', config)
                    config.update(test_message_id=mid, connected_at=time.time())
                    tg.save(config)
                    events.put(('success', 'Test sent to your Telegram chat. Deal alerts will activate '
                        'on the next scan. You can close this window.'))
                    return
                if updates:
                    offset = max(update['update_id'] for update in updates) + 1
            if not cancel.is_set():
                events.put(('error', 'Pairing expired. Click Connect to try again.'))
        except Exception as error:
            events.put(('error', str(error)))

    def connect():
        secret = token.get().strip()
        token.set('')
        connect_button.config(state='disabled')
        status.set('Checking bot…')
        threading.Thread(target=worker, args=(secret,), daemon=True).start()

    connect_button = ttk.Button(box, text='Connect Telegram', command=connect)
    connect_button.pack(anchor='w', pady=8)

    def poll():
        try:
            while True:
                kind, value = events.get_nowait()
                if kind == 'pair':
                    pair_url[0] = value
                    pair.pack(anchor='w')
                    status.set('Click the button below, then press Start in your Telegram chat.')
                    webbrowser.open(value)
                else:
                    status.set(value)
                    if kind == 'error':
                        connect_button.config(state='normal')
                    if kind == 'success':
                        pair.pack_forget()
        except queue.Empty:
            pass
        root.after(150, poll)

    def close():
        cancel.set()
        root.destroy()

    root.protocol('WM_DELETE_WINDOW', close)
    poll()
    root.mainloop()

if __name__ == '__main__':
    main()
