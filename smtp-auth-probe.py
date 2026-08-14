"""Asks each recorded mail account whether it still authenticates. Prints no secret.

Run when a sign-up notification fails with "Authentication Required": that message does not say
whether the password is wrong or whether the client never offered it, and this separates the two.
"""
import json
import smtplib
import sys

CRED = r"C:\Users\nsquires\.claude\projects\C--Users-nsquires-source-repos-ING-eBay-AutoLister\web-credentials.json"

with open(CRED, encoding="utf-8") as handle:
    creds = json.load(handle)

ACCOUNTS = [
    ("gmail", "smtp.gmail.com", 587, creds["gmail"]["login"], creds["gmail"]["app_password"]),
    ("office365", "smtp.office365.com", 587, creds["office365"]["login"], creds["office365"]["password"]),
]

failures = 0
for name, host, port, user, secret in ACCOUNTS:
    user = user.strip()
    secret = "".join(secret.split()) if name == "gmail" else secret
    print(f"\n=== {name}: {user} via {host}:{port} (secret length {len(secret)}) ===")
    try:
        with smtplib.SMTP(host, port, timeout=30) as smtp:
            smtp.ehlo()
            smtp.starttls()
            smtp.ehlo()
            print("auth mechanisms offered:", smtp.esmtp_features.get("auth"))
            smtp.login(user, secret)
            print("AUTH OK")
    except smtplib.SMTPAuthenticationError as error:
        failures += 1
        print(f"AUTH REFUSED: {error.smtp_code} {error.smtp_error!r}")
    except Exception as error:  # noqa: BLE001 - a probe reports whatever went wrong
        failures += 1
        print(f"FAILED before auth: {type(error).__name__}: {error}")

sys.exit(1 if failures == len(ACCOUNTS) else 0)
