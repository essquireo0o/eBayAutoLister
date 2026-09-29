import unittest, datetime as dt, sqlite3
from unittest.mock import patch
import watch
class Tests(unittest.TestCase):
 def test_identity(self):
  self.assertEqual(watch.identity('Antminer S19j Pro+ 120TH'),('s19jpro+',120,'t'))
  self.assertNotEqual(watch.identity('Antminer S19 Pro 100TH'),watch.identity('Antminer S19 XP 100TH'))
  self.assertEqual(watch.identity('Antminer S21XP 270T'),('s21xp',270,'t'))
 def test_evidence(self):
  i={'title':'Antminer S19 Pro 100TH','price':200,'shippingCost':50}
  comps=[{'title':i['title'],'price':500,'soldDate':dt.datetime.now(dt.timezone.utc).isoformat(),'url':f'https://www.ebay.com/itm/{n}'} for n in range(3)]
  self.assertEqual(watch.evidence(i,comps)['savings'],250)
  self.assertIsNone(watch.evidence(i,comps[:2]))
  self.assertIsNone(watch.evidence(i,[comps[0]]*5))
  self.assertIsNone(watch.evidence({**i,'shippingCost':200},comps))
  self.assertIsNone(watch.evidence({**i,'title':'Antminer S19 XP 100TH'},comps))
  self.assertIsNone(watch.evidence(i,[{**c,'soldDate':'2020-01-01'} for c in comps]))
 def test_exclusions(self):
  i={'title':'Antminer S19 Pro 100TH','price':100,'shippingStated':True,'buyingOption':'FIXED_PRICE','sellerFeedbackScore':100,'sellerFeedbackPercent':100}
  self.assertTrue(watch.eligible(i))
  for change in [{'title':i['title']+' for parts'},{'shippingStated':False},{'buyingOption':'AUCTION'},{'sellerFeedbackPercent':80}]:self.assertFalse(watch.eligible({**i,**change}))
 def test_scan_dedup_and_delivery_retry(self):
  db=sqlite3.connect(':memory:');db.execute('create table seen(id text primary key,status text,updated real)');db.execute('create table meta(key text primary key,value text)')
  item={'itemId':'42','title':'Antminer S19 Pro 100TH','price':200,'shippingCost':50,'shippingStated':True,'buyingOption':'FIXED_PRICE','sellerFeedbackScore':100,'sellerFeedbackPercent':100,'sellerUsername':'fixture','url':'https://www.ebay.com/itm/42'}
  comps=[{'title':item['title'],'price':500,'soldDate':dt.datetime.now(dt.timezone.utc).isoformat(),'url':f'https://www.ebay.com/itm/{n}'} for n in range(3)]
  def api(path,data=None):
   if path=='/api/autobuy/search':return {'ok':True,'items':[item]}
   if path.startswith('/api/sold-comps'):return {'items':comps}
   return {'ok':True,'data':{'title':item['title'],'price':200,'description':'Fully tested working miner. Power supply included.'}}
  with patch.object(watch,'api',side_effect=api),patch.object(watch,'save_status'),patch.object(watch,'send_mail',return_value='test-id') as send:
   watch.run_once(db,'fixture@example.test');send.assert_not_called()
   db.execute('delete from seen');db.commit()
   watch.run_once(db,'fixture@example.test',True);send.assert_not_called();self.assertEqual(db.execute('select count(*) from seen').fetchone()[0],0)
   send.side_effect=RuntimeError('SMTP unavailable')
   with self.assertRaises(RuntimeError):watch.run_once(db,'fixture@example.test')
   self.assertEqual(db.execute('select count(*) from seen').fetchone()[0],0)
   send.side_effect=None;watch.run_once(db,'fixture@example.test');self.assertEqual(send.call_count,2)
   watch.run_once(db,'fixture@example.test');self.assertEqual(send.call_count,2)
if __name__=='__main__':unittest.main()
