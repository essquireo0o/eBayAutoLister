import argparse, datetime as dt, email.message, hashlib, html, json, logging, os, re, smtplib, sqlite3, ssl, statistics, time, urllib.request, urllib.parse
from pathlib import Path
BASE='http://127.0.0.1:9332'
HOME=Path(os.environ['LOCALAPPDATA'])/'ING AutoLister'/'antminer-watch'
MAIL=Path.home()/'.claude/projects/C--Users-nsquires-source-repos-ING-eBay-AutoLister/web-credentials.json'
BAD=re.compile(r'\b(parts|repair|broken|defective|untested|faulty|damaged|hashboard|control board|fan|cable|lot|hosting|contract)\b|not working|as.is|no psu|without.*(?:psu|power supply)',re.I)
MODEL=re.compile(r'\b((?:ks|[sltked])\d{1,2}[jk]?)\s*(?:(pro\s*\+?|xp|ultra|plus)\s*)?(hydro|hyd|immersion|imm)?',re.I)
RATE=re.compile(r'(?<!\d)(\d+(?:\.\d+)?)\s*([tgm])(?:h(?:/s)?|\b)',re.I)
def identity(title):
    m=MODEL.search(title); r=RATE.search(title)
    if not m or not r:return None
    return (re.sub(r'\s+','',m.group(0)).lower(),float(r[1]),r[2].lower())
def clean(t):return html.unescape(re.sub('<[^>]+>',' ',t or ''))
def api(path,data=None):
    req=urllib.request.Request(BASE+path,data=json.dumps(data).encode() if data is not None else None,headers={'Content-Type':'application/json'})
    with urllib.request.urlopen(req,timeout=65) as r:return json.load(r)
def eligible(item):
    return (identity(item.get('title','')) is not None and not BAD.search(item.get('title','')+' '+item.get('condition','')) and item.get('shippingStated') is True and item.get('buyingOption')=='FIXED_PRICE' and item.get('sellerFeedbackPercent',0)>=98 and item.get('sellerFeedbackScore',0)>=20 and item.get('price',0)>0)
def evidence(item,comps,now=None):
    now=now or dt.datetime.now(dt.timezone.utc); prices={}
    for c in comps:
        if identity(c.get('title',''))!=identity(item['title']) or BAD.search(c.get('title','')):continue
        try:date=dt.datetime.fromisoformat(c['soldDate'].replace('Z','+00:00')).replace(tzinfo=dt.timezone.utc)
        except (KeyError,ValueError,TypeError):continue
        age=(now-date).total_seconds()/86400
        url=c.get('url',''); match=re.search(r'/itm/(?:[^/]+/)?(\d+)',url)
        if not match or not 0<=age<=60 or c.get('price',0)<=0:continue
        prices[match[1]]=float(c['price'])
    if len(prices)<3:return None
    median=statistics.median(prices.values()); cost=float(item['price'])+float(item['shippingCost'])
    if cost>median*.7 or median-cost<100:return None
    return {'median':median,'cost':cost,'savings':median-cost,'discount':round(100*(1-cost/median),1),'count':len(prices)}
def send_mail(to,subject,body):
    import win32crypt
    creds=json.loads(win32crypt.CryptUnprotectData((HOME/'mail.dpapi').read_bytes(),None,None,None,0)[1])
    msg=email.message.EmailMessage();msg['From']=creds['login'];msg['To']=to;msg['Subject']=subject
    from email.utils import make_msgid
    msg['Message-ID']=make_msgid();msg.set_content(body)
    with smtplib.SMTP_SSL('smtp.gmail.com',465,timeout=30,context=ssl.create_default_context()) as smtp:
        smtp.login(creds['login'].strip(),''.join(creds['app_password'].split()),initial_response_ok=False)
        refused=smtp.send_message(msg)
        if refused:raise RuntimeError('Recipient refused')
    return msg['Message-ID']
def db_open():
    HOME.mkdir(parents=True,exist_ok=True)
    db=sqlite3.connect(HOME/'watch.sqlite');db.execute('create table if not exists seen(id text primary key, status text, updated real)');db.execute('create table if not exists meta(key text primary key,value text)');return db
def save_status(**fields):
    f=HOME/'status.json';old=json.loads(f.read_text()) if f.exists() else {};old.update(fields);tmp=f.with_suffix('.tmp');tmp.write_text(json.dumps(old,indent=2));tmp.replace(f)
def run_once(db,recipient,monitor_only=False,channel='email'):
    if channel=='telegram':
        import telegram_delivery
        monitor_only=monitor_only or not telegram_delivery.CONFIG.exists()
        recipient='Telegram' if not monitor_only else 'Telegram pairing pending'
    response=api('/api/autobuy/search',{'query':'antminer','mode':'BuyItNow','minSellerFeedback':20})
    if not response.get('ok'):raise RuntimeError('eBay search did not succeed')
    items=response['items'];now=time.time()
    if not db.execute("select 1 from meta where key='initialized'").fetchone():
        db.executemany('insert or ignore into seen values(?,?,?)',[(i['itemId'],'baseline',now) for i in items]);db.execute("insert into meta values('initialized',?)",(str(now),));db.commit();save_status(state='monitoring_only' if monitor_only else 'active',last_success=now,baseline_count=len(items),recipient=recipient);return
    checked=sent=0
    for item in items:
        iid=item['itemId'];old=db.execute('select status from seen where id=?',(iid,)).fetchone()
        if old:continue
        if not eligible(item):
            db.execute('insert into seen values(?,?,?)',(iid,'filtered',now));db.commit();continue
        checked+=1
        # Match exact variant/hashrate on the returned individual sold records, never a broad aggregate.
        ident=identity(item['title']);query=f'Antminer {ident[0]} {ident[1]:g}{ident[2]}H'
        comps=api('/api/sold-comps?'+urllib.parse.urlencode({'q':query}))
        ev=evidence(item,comps.get('items',[]))
        if ev:
            detail=api('/api/ebay/listing-detail?'+urllib.parse.urlencode({'itemId':iid}))
            if not detail.get('ok'):raise RuntimeError('Listing verification failed')
            d=detail['data'];text=clean(d.get('description',''))+' '+d.get('conditionDescription','')
            # Require explicit working/PSU evidence. Silence is not confirmation.
            if BAD.search(text) or not re.search(r'\b(tested|working|fully functional|brand new)\b',text+' '+item['title'],re.I) or not re.search(r'(?:psu|power supply).{0,40}(?:included|built.in|integrated)|(?:includes?|with|integrated).{0,30}(?:psu|power supply)',text,re.I) or float(d.get('price',0))!=float(item['price']) or identity(d.get('title',''))!=ident:
                ev=None
        if ev:
            subject=f"Antminer deal: ${ev['cost']:,.0f} shipped — {ev['discount']}% below sold comps"
            body=f"{item['title']}\n\nPrice: ${item['price']:,.2f}\nShipping: ${item['shippingCost']:,.2f}\nTotal before tax: ${ev['cost']:,.2f}\nMatched sold median: ${ev['median']:,.2f} ({ev['count']} sales, last 60 days)\nSavings vs sold item prices: ${ev['savings']:,.2f}\nSeller: {item['sellerUsername']} ({item['sellerFeedbackPercent']}% positive)\n\n{item['url']}\n\nListing description checked for working condition and included PSU. Seller claims are not an independent hardware test. Verify details before buying. No purchase was made.\n"
            if monitor_only:
                save_status(pending_deal={'subject':subject,'body':body});continue
            if channel=='telegram':
                mid=telegram_delivery.send(subject,body);save_status(last_telegram_at=time.time(),last_telegram_id=mid)
            else:
                mid=send_mail(recipient,subject,body);save_status(last_email_at=time.time(),last_email_id=mid)
            sent+=1
        db.execute('insert into seen values(?,?,?)',(iid,'notified' if ev else 'no_verified_deal',now));db.commit()
    save_status(state='monitoring_only' if monitor_only else 'active',last_success=time.time(),last_scan_count=len(items),last_evaluated=checked,last_alerts=sent,recipient=recipient,error=None)
def main():
    p=argparse.ArgumentParser();p.add_argument('--recipient',default='');p.add_argument('--channel',choices=['email','telegram'],default='email');p.add_argument('--once',action='store_true');p.add_argument('--test-email',action='store_true');p.add_argument('--monitor-only',action='store_true');a=p.parse_args()
    if (a.channel=='email' or a.test_email) and not a.recipient:p.error('--recipient is required for email')
    db=db_open()
    logging.basicConfig(filename=HOME/'watch.log',level=logging.INFO,format='%(asctime)s %(message)s')
    if a.test_email:
        mid=send_mail(a.recipient,'Antminer deal alerts — delivery test','This is the requested email delivery test for your Antminer deal watcher.\n\nAlert threshold: at least 30% below matched sold prices and $100 savings, including stated shipping, before tax. Working units with PSU evidence only.\n\nNew listings are checked every minute while this Windows account and ING Listing Engine are running. This message is a test, not a deal alert.');save_status(test_email_id=mid,test_email_accepted_at=time.time(),recipient=a.recipient);print('SMTP accepted test email:',mid);return
    while True:
        started=time.monotonic()
        try:run_once(db,a.recipient,a.monitor_only,a.channel)
        except Exception as e:
            logging.exception('Scan failed');save_status(state='error',error=type(e).__name__+': '+str(e),last_error_at=time.time())
            if a.once:raise
        if a.once:return
        time.sleep(max(1,60-(time.monotonic()-started)))
if __name__=='__main__':main()
