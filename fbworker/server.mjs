// ─────────────────────────────────────────────────────────────────────────────
// fb-worker — the headless-browser side of Facebook Marketplace, for the HOSTED build.
//
// Why this exists at all: Facebook has no Marketplace API, so a search means driving a real
// browser with a logged-in session. The desktop app has a browser; the hosted Linux app image
// deliberately does not (it stays lean, and shipping Chromium over a slow uplink on every deploy
// is a non-starter). This container is built FROM the official Playwright image, which already
// carries Chromium + its system libraries, and runs the app's OWN generated search script.
//
// SECURITY — read before changing:
//   * This endpoint runs the Node script it is POSTed. That is only safe because the script comes
//     from OUR app on the private Docker network. It is NEVER published to the host or internet
//     (no `ports:` in compose) — the app reaches it as http://fb-worker:8090 and nothing else can.
//     If you ever add a published port, you have handed the box a remote code-execution hole.
//   * Runs as the image's non-root `pwuser`.
//
// RESOURCES — the box has 2 GB of RAM and Chromium is hungry:
//   * runExclusive() serialises every run. One browser at a time, full stop. This protects both
//     the RAM (no two Chromiums) and the shared Facebook account (no concurrent hits, which is the
//     fastest way to get an account flagged). Callers queue; they do not run in parallel.
// ─────────────────────────────────────────────────────────────────────────────
import http from 'node:http';
import { spawn } from 'node:child_process';
import { writeFile, unlink } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { randomUUID } from 'node:crypto';

const PORT = Number(process.env.PORT || 8090);
const MAX_TIMEOUT_MS = 240_000;     // hard ceiling; a run that overruns has its tree killed
const MAX_BODY = 4_000_000;         // the generated scripts are a few KB; this is slack, not a target

// Single-flight queue. Every /run threads through this one promise chain, so only one node+browser
// is ever alive. Deliberately simple: correctness here matters more than throughput on a 2 GB box.
let chain = Promise.resolve();
function runExclusive(fn) {
  const result = chain.then(fn, fn);
  chain = result.then(() => {}, () => {});   // never let a rejection break the chain for the next caller
  return result;
}

function runNode(script, timeoutMs) {
  return new Promise(async (resolve) => {
    const file = join(tmpdir(), `fbrun_${randomUUID()}.cjs`);
    await writeFile(file, script);
    let out = '', err = '', settled = false;

    // detached so we can signal the whole process group on timeout — a killed node that leaves a
    // headless Chrome behind is exactly the leak this guards against.
    const child = spawn('node', [file], { detached: true, env: { ...process.env } });
    child.stdout.setEncoding('utf8');
    child.stderr.setEncoding('utf8');
    child.stdout.on('data', d => { out += d; });
    child.stderr.on('data', d => { err += d; });

    const cap = Math.min(Number(timeoutMs) || 120_000, MAX_TIMEOUT_MS);
    const timer = setTimeout(() => finish(true), cap);

    function finish(timedOut) {
      if (settled) return;
      settled = true;
      clearTimeout(timer);
      if (timedOut) {
        try { process.kill(-child.pid, 'SIGKILL'); } catch { }
        try { child.kill('SIGKILL'); } catch { }
      }
      unlink(file).catch(() => {});
      resolve({ stdout: out.trim(), stderr: err, timedOut });
    }

    child.on('exit', () => finish(false));
    child.on('error', (e) => { err += '\n' + String(e); finish(false); });
  });
}

const server = http.createServer((req, res) => {
  if (req.method === 'GET' && req.url === '/health') {
    res.writeHead(200, { 'content-type': 'text/plain' }).end('ok');
    return;
  }
  if (req.method === 'POST' && req.url === '/run') {
    let body = '';
    req.on('data', c => { body += c; if (body.length > MAX_BODY) req.destroy(); });
    req.on('end', () => {
      let p;
      try { p = JSON.parse(body); } catch { res.writeHead(400).end('{"error":"bad json"}'); return; }
      if (typeof p.script !== 'string' || !p.script) { res.writeHead(400).end('{"error":"no script"}'); return; }
      runExclusive(() => runNode(p.script, p.timeoutMs))
        .then(r => { res.writeHead(200, { 'content-type': 'application/json' }); res.end(JSON.stringify(r)); })
        .catch(e => { res.writeHead(500).end(JSON.stringify({ error: String(e) })); });
    });
    return;
  }
  res.writeHead(404).end();
});

server.listen(PORT, () => console.log(`fb-worker listening on :${PORT} (single-flight, browser runs on demand)`));
