"""Telegram delivery with credentials encrypted for the current Windows user."""
import json
import os
import re
import urllib.request
import urllib.error
from pathlib import Path

HOME = Path(os.environ['LOCALAPPDATA']) / 'ING AutoLister' / 'antminer-watch'
CONFIG = HOME / 'telegram.dpapi'

def call(token, method, data=None):
    if not re.fullmatch(r'\d+:[A-Za-z0-9_-]+', token):
        raise RuntimeError('Paste the bot token provided by BotFather.')
    req = urllib.request.Request('https://api.telegram.org/bot' + token + '/' + method,
        data=json.dumps(data or {}).encode(), headers={'Content-Type': 'application/json'})
    try:
        with urllib.request.urlopen(req, timeout=35) as response:
            result = json.load(response)
    except urllib.error.HTTPError as error:
        message = {401: 'Telegram rejected the bot token.',
            403: 'Open the bot in Telegram and press Start.',
            409: 'This bot is used elsewhere. Create a dedicated alert bot.',
            429: 'Telegram rate limit reached; retry shortly.'}.get(error.code,
                'Telegram request failed (HTTP ' + str(error.code) + ').')
        raise RuntimeError(message) from None
    except Exception:
        raise RuntimeError('Could not reach Telegram. Check the internet connection.') from None
    if not result.get('ok'):
        raise RuntimeError('Telegram did not accept the request.')
    return result['result']

def load():
    import win32crypt
    return json.loads(win32crypt.CryptUnprotectData(CONFIG.read_bytes(), None, None, None, 0)[1])

def save(config):
    import win32crypt
    HOME.mkdir(parents=True, exist_ok=True)
    tmp = CONFIG.with_suffix('.tmp')
    tmp.write_bytes(win32crypt.CryptProtectData(json.dumps(config).encode(),
        'Antminer Telegram alerts', None, None, None, 0))
    tmp.replace(CONFIG)

def paired_chat(updates, nonce):
    for update in updates:
        msg = update.get('message', {})
        chat = msg.get('chat', {})
        if (chat.get('type') == 'private' and msg.get('text', '').strip() == '/start ' + nonce
            and msg.get('from', {}).get('id') == chat.get('id')):
            return chat['id']
    return None

def send(subject, body, config=None):
    config = config or load()
    text = subject + '\n\n' + body
    if len(text) > 4000:
        raise RuntimeError('Telegram alert is too long; not sent.')
    result = call(config['token'], 'sendMessage', {'chat_id': config['chat_id'],
        'text': text, 'link_preview_options': {'is_disabled': True}})
    if result.get('chat', {}).get('id') != config['chat_id'] or not result.get('message_id'):
        raise RuntimeError('Telegram delivery response was not confirmed.')
    return result['message_id']
