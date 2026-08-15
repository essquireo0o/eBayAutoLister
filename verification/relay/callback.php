<?php
// https://inglisting.com/api/ebay/callback/
// eBay sends the authorization code here after the user logs in.

require_once __DIR__ . '/../../../../ebay-config.php';

$code  = $_GET['code']  ?? '';
$state = $_GET['state'] ?? '';
$error = $_GET['error'] ?? '';

if ($error !== '') {
    http_response_code(400);
    exit('eBay login failed: ' . htmlspecialchars($error, ENT_QUOTES, 'UTF-8'));
}

// state must be a valid session ID (hex UUID without dashes), optionally followed by a single
// letter naming WHERE to hand the browser back to. eBay preserves `state` and nothing else, so
// this is the only channel the app has to say "I am the hosted build, not the desktop one".
//
// An allow-list, deliberately, and not a return_url parameter: a relay that redirects wherever
// it is told is an open redirect, and this one carries a freshly minted eBay session.
if (!preg_match('/^([0-9a-f]{32})([a-z]?)$/', $state, $m)) {
    http_response_code(400);
    exit('Invalid state parameter.');
}
$state  = $m[1];                 // the session id the rest of this file already expects
$target = $m[2] ?: 'd';          // default 'd' = desktop, so old builds behave exactly as before

$RETURN_TO = [
    'd' => 'http://localhost:9332/api/ebay/finish',      // desktop app on the seller's own PC
    'h' => 'https://app.inglisting.com/api/ebay/finish', // the hosted web app
];
if (!isset($RETURN_TO[$target])) {
    http_response_code(400);
    exit('Unknown return target.');
}

if ($code === '') {
    http_response_code(400);
    exit('No authorization code received from eBay.');
}

// Exchange the code for tokens server-side (client secret never leaves the server)
$ch = curl_init(EBAY_TOKEN_URL);
curl_setopt_array($ch, [
    CURLOPT_POST           => true,
    CURLOPT_RETURNTRANSFER => true,
    CURLOPT_HTTPHEADER     => [
        'Authorization: Basic ' . base64_encode(EBAY_CLIENT_ID . ':' . EBAY_CLIENT_SECRET),
        'Content-Type: application/x-www-form-urlencoded',
    ],
    CURLOPT_POSTFIELDS => http_build_query([
        'grant_type'   => 'authorization_code',
        'code'         => $code,
        'redirect_uri' => EBAY_RUNAME,
    ]),
]);
$body   = curl_exec($ch);
$status = curl_getinfo($ch, CURLINFO_HTTP_CODE);
curl_close($ch);

$tokens = json_decode($body, true);

if ($status !== 200 || empty($tokens['access_token'])) {
    $msg = $tokens['error_description'] ?? $tokens['error'] ?? 'Unknown error';
    http_response_code(502);
    exit('eBay token exchange failed: ' . htmlspecialchars($msg, ENT_QUOTES, 'UTF-8'));
}

// Generate a one-time pickup token — only the desktop app (which knows this value) can claim the tokens
$pickupToken = bin2hex(random_bytes(32));

$session = [
    'access_token'              => $tokens['access_token'],
    'refresh_token'             => $tokens['refresh_token']              ?? '',
    'expires_in'                => (int)($tokens['expires_in']           ?? 7200),
    'refresh_token_expires_in'  => (int)($tokens['refresh_token_expires_in'] ?? 47304000),
    'token_type'                => $tokens['token_type']                 ?? 'User Access Token',
    'pickup_token_hash'         => hash('sha256', $pickupToken),
    'expires_at'                => time() + SESSION_TTL,
];

$file = sys_get_temp_dir() . '/ing_ebay_' . $state . '.json';
file_put_contents($file, json_encode($session), LOCK_EX);

// Send the browser back to the desktop app — tokens are NOT in this URL,
// only opaque references the app uses to call the pickup endpoint.
$redirect = $RETURN_TO[$target]
          . '?session=' . urlencode($state)
          . '&pickup='  . urlencode($pickupToken);

header('Location: ' . $redirect, true, 302);
exit;
