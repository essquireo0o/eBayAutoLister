<?php
// https://inglisting.com/api/ebay/pickup/
// Desktop app calls this once to retrieve the tokens stored by the callback.
// Tokens are deleted immediately after retrieval (one-time use).

header('Content-Type: application/json');
header('Access-Control-Allow-Origin: http://localhost:9332');
header('Access-Control-Allow-Methods: GET, OPTIONS');
header('Cache-Control: no-store');

if ($_SERVER['REQUEST_METHOD'] === 'OPTIONS') {
    http_response_code(204);
    exit;
}

$session = $_GET['session'] ?? '';
$pickup  = $_GET['pickup']  ?? '';

if (!preg_match('/^[0-9a-f]{32}$/', $session)) {
    http_response_code(400);
    echo json_encode(['error' => 'Invalid session ID']);
    exit;
}

$file = sys_get_temp_dir() . '/ing_ebay_' . $session . '.json';

if (!file_exists($file)) {
    http_response_code(404);
    echo json_encode(['error' => 'Session not found or already used']);
    exit;
}

$data = json_decode(file_get_contents($file), true);

if (time() > ($data['expires_at'] ?? 0)) {
    @unlink($file);
    http_response_code(410);
    echo json_encode(['error' => 'Session expired — please log in again']);
    exit;
}

if (!hash_equals($data['pickup_token_hash'], hash('sha256', $pickup))) {
    http_response_code(403);
    echo json_encode(['error' => 'Invalid pickup token']);
    exit;
}

// One-time: delete before responding so a second call always gets 404
@unlink($file);

echo json_encode([
    'access_token'             => $data['access_token'],
    'refresh_token'            => $data['refresh_token'],
    'expires_in'               => $data['expires_in'],
    'refresh_token_expires_in' => $data['refresh_token_expires_in'],
    'token_type'               => $data['token_type'],
]);
