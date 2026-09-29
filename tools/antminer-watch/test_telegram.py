import io
import json
import tempfile
import unittest
import urllib.error
from pathlib import Path
from unittest.mock import patch
import telegram_delivery as tg

class TelegramTests(unittest.TestCase):
    def test_pairing_requires_private_chat_and_exact_nonce(self):
        message={'chat':{'id':42,'type':'private'},'from':{'id':42},'text':'/start nonce'}
        self.assertEqual(tg.paired_chat([{'message':message}],'nonce'),42)
        self.assertIsNone(tg.paired_chat([{'message':message}],'other'))
        self.assertIsNone(tg.paired_chat([{'message':{**message,'chat':{'id':42,'type':'group'}}}],'nonce'))
        self.assertIsNone(tg.paired_chat([{'message':{**message,'from':{'id':43}}}],'nonce'))

    def test_delivery_requires_confirmed_chat_and_message(self):
        config={'token':'123:secret','chat_id':42}
        with patch.object(tg,'call',return_value={'chat':{'id':42},'message_id':9}) as call:
            self.assertEqual(tg.send('Deal','Link',config),9)
            self.assertEqual(call.call_args.args[2]['text'],'Deal\n\nLink')
            self.assertNotIn('allow_paid_broadcast',call.call_args.args[2])
        with patch.object(tg,'call',return_value={'chat':{'id':43},'message_id':9}):
            with self.assertRaises(RuntimeError):tg.send('Deal','Link',config)

    def test_errors_do_not_expose_token(self):
        token='123:secret'
        error=urllib.error.HTTPError('https://api.telegram.org/bot'+token+'/sendMessage',403,'Forbidden',{},None)
        with patch('urllib.request.urlopen',side_effect=error):
            with self.assertRaises(RuntimeError) as caught:tg.call(token,'sendMessage')
        self.assertNotIn(token,str(caught.exception))

    def test_encrypted_config_round_trip(self):
        with tempfile.TemporaryDirectory() as folder:
            with patch.object(tg,'HOME',Path(folder)),patch.object(tg,'CONFIG',Path(folder)/'telegram.dpapi'):
                config={'token':'123:secret','chat_id':42}
                tg.save(config)
                self.assertNotIn(b'123:secret',tg.CONFIG.read_bytes())
                self.assertEqual(tg.load(),config)

if __name__=='__main__':unittest.main()
