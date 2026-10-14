// k6 load-test suite for the DataHub webhook.
//
// One script, four test types, selected via -e TEST_TYPE=smoke|load|stress|soak.
// Target via -e BASE_URL=... (use run.sh for the presets). Every valid iteration
// POSTs an HMAC-signed container-readings payload to /api/webhooks/container and
// expects 202. See README.md.

import http from 'k6/http';
import crypto from 'k6/crypto';
import exec from 'k6/execution';
import { check, sleep } from 'k6';
import { textSummary } from 'https://jslib.k6.io/k6-summary/0.0.2/index.js';

const BASE = (__ENV.BASE_URL || 'https://localhost:7071').replace(/\/+$/, '');
const TYPE = __ENV.TEST_TYPE || 'smoke';
const SECRET = __ENV.HMAC_SECRET || '';
// Signed-in web-user flow (Auth.js + Keycloak OIDC, then GraphQL through the BFF).
// Activates when WEB_URL + LOGIN_EMAIL + LOGIN_PASSWORD are set.
const WEB = (__ENV.WEB_URL || '').replace(/\/+$/, '');
const EMAIL = __ENV.LOGIN_EMAIL || '';
const PASS = __ENV.LOGIN_PASSWORD || '';
const WEB_FLOW = Boolean(WEB && EMAIL && PASS);
// Seeded container code space (SqlScripts/populate-test-data.sql). Readings land on
// existing rows at prod scale; on an unseeded DB the consumer creates the container.
const CONTAINERS = parseInt(__ENV.CONTAINERS || '5000000', 10);
// THINK=0 disables think time: pure ceiling-hunting, not realistic sender behavior.
const THINK = __ENV.THINK !== '0';

// All k6 readings carry timestamps in this window so cleanup-loadtest.sql can
// delete exactly these rows. Keep in sync with that script.
const SENTINEL_START_MS = Date.UTC(2000, 0, 1);
const SENTINEL_RANGE_MS = 50 * 24 * 3600 * 1000; // Jan 1 - Feb 19, 2000

if (!SECRET) throw new Error('HMAC_SECRET is required (run.sh reads it from ../.env for the compose preset)');

const SCENARIOS = {
  smoke: { executor: 'constant-vus', vus: 2, duration: '1m' },
  load: {
    executor: 'ramping-vus',
    stages: [
      { duration: '2m', target: 20 },
      { duration: '5m', target: 20 },
      { duration: '1m', target: 0 },
    ],
  },
  stress: {
    executor: 'ramping-arrival-rate',
    startRate: 2,
    timeUnit: '1s',
    preAllocatedVUs: 200,
    maxVUs: 600,
    stages: [
      { duration: '1m', target: 25 },
      { duration: '2m', target: 50 },
      { duration: '2m', target: 100 },
      { duration: '2m', target: 200 },
      { duration: '1m', target: 0 },
    ],
  },
  soak: { executor: 'constant-vus', vus: 10, duration: '30m' },
};

const THRESHOLDS = {
  smoke: { http_req_failed: ['rate<0.01'], 'http_req_duration{flow:webhook}': ['p(95)<1000'] },
  load: { http_req_failed: ['rate<0.01'], 'http_req_duration{flow:webhook}': ['p(95)<1000'] },
  // Stress exists to find the ceiling, so its thresholds are informational only.
  stress: { http_req_failed: ['rate<0.5'] },
  soak: { http_req_failed: ['rate<0.01'], 'http_req_duration{flow:webhook}': ['p(95)<1000'] },
};
if (WEB_FLOW) {
  // GraphQL pages over the 5M/15M tables get their own budget; login is one-off per VU.
  for (const t of ['smoke', 'load', 'soak']) {
    THRESHOLDS[t]['http_req_duration{flow:web}'] = ['p(95)<3000'];
  }
}

if (!SCENARIOS[TYPE]) {
  throw new Error(`Unknown TEST_TYPE "${TYPE}" — use smoke|load|stress|soak`);
}

export const options = {
  scenarios: { [TYPE]: { ...SCENARIOS[TYPE], exec: 'main' } },
  thresholds: THRESHOLDS[TYPE],
  insecureSkipTLSVerify: __ENV.SKIP_TLS === '1', // Caddy internal CA / dev cert
  // k6 wipes the VU cookie jar each iteration by default — the web session must survive.
  noCookiesReset: true,
};

const url = (p) => `${BASE}${p}`;

function payload() {
  const n = 1 + Math.floor(Math.random() * 10);
  const readings = [];
  for (let i = 0; i < n; i++) {
    const ts = new Date(SENTINEL_START_MS + Math.random() * SENTINEL_RANGE_MS);
    readings.push({
      timestamp: ts.toISOString(),
      value: Math.round((Math.random() * 120 - 30) * 10) / 10,
      unit: ['C', 'F', 'K'][Math.floor(Math.random() * 3)],
    });
  }
  const id = 1 + Math.floor(Math.random() * CONTAINERS);
  return JSON.stringify({
    containerId: `SEED-CONT-${String(id).padStart(8, '0')}`,
    readings,
  });
}

// X-Webhook-Signature = sha256=hex(HMAC-SHA256(secret, "{unix-ts}.{raw body}")).
function headers(body, secret) {
  const ts = String(Math.floor(Date.now() / 1000));
  return {
    'Content-Type': 'application/json',
    'X-Webhook-Id': `k6-${__VU}-${Date.now()}-${Math.floor(Math.random() * 1e9)}`,
    'X-Webhook-Timestamp': ts,
    'X-Webhook-Signature': 'sha256=' + crypto.hmac('sha256', secret, `${ts}.${body}`, 'hex'),
  };
}

function webhookPost() {
  const body = payload();
  const res = http.post(url('/api/webhooks/container'), body, {
    headers: headers(body, SECRET),
    tags: { flow: 'webhook' },
  });
  check(res, { 'webhook 202': (r) => r.status === 202 }, { flow: 'webhook' });
}

// Wrong secret must come back 401 — proves rejection stays cheap under load.
function badSignature() {
  const body = payload();
  const res = http.post(url('/api/webhooks/container'), body, {
    headers: headers(body, 'definitely-not-the-secret'),
    tags: { flow: 'badsig' },
    responseCallback: http.expectedStatuses(401),
  });
  check(res, { 'bad signature 401': (r) => r.status === 401 }, { flow: 'badsig' });
}

function health() {
  const res = http.get(url('/api/health'), { tags: { flow: 'health' } });
  check(res, { 'health 200': (r) => r.status === 200 }, { flow: 'health' });
}

// --- Signed-in web user (Auth.js + Keycloak OIDC) ----------------------------------
// Per-VU: each VU has its own cookie jar and session.

let authed = false;
let containerIds = [];

const CONTAINERS_QUERY = `query Containers($after: String) {
  containers(first: 100, after: $after) { totalCount pageInfo { hasNextPage endCursor } nodes { id code } }
}`;
const TEMPERATURES_QUERY = `query Temperatures($containerId: UUID!, $after: String) {
  temperatures(containerId: $containerId, first: 100, after: $after) { pageInfo { hasNextPage endCursor } nodes { id timestampUtc celsius } }
}`;

function ensureLogin() {
  if (authed) return true;

  // 1. CSRF token (sets the double-submit cookie).
  const csrfRes = http.get(`${WEB}/api/auth/csrf`, { tags: { flow: 'login' } });
  const csrfToken = csrfRes.status === 200 ? csrfRes.json('csrfToken') : null;
  if (!csrfToken) {
    check(csrfRes, { 'csrf token': () => false }, { flow: 'login' });
    return false;
  }

  // 2. Start the OIDC flow; k6 follows the redirect to the Keycloak login page.
  const signin = http.post(
    `${WEB}/api/auth/signin/keycloak`,
    { csrfToken, callbackUrl: '/app' },
    { tags: { flow: 'login' } }
  );
  const form = signin.html().find('#kc-form-login');
  const action = form.attr('action');
  if (!action) {
    check(signin, { 'keycloak login page': () => false }, { flow: 'login' });
    exec.test.abort('no Keycloak login form — check WEB_URL and that the web client is configured');
  }

  // 3. Credentials; Keycloak redirects through /api/auth/callback/keycloak back to /app.
  const res = http.post(action, { username: EMAIL, password: PASS }, { tags: { flow: 'login' } });
  // Success = redirected all the way back to the web app (not stuck on a Keycloak page).
  authed = check(res, { 'login ok': (r) => r.status === 200 && r.url.startsWith(WEB) }, { flow: 'login' });
  if (!authed) exec.test.abort('login failed — check LOGIN_EMAIL/LOGIN_PASSWORD');
  return authed;
}

function graphql(query, variables, name) {
  const res = http.post(`${WEB}/api/graphql`, JSON.stringify({ query, variables }), {
    headers: { 'Content-Type': 'application/json' },
    tags: { flow: 'web', op: name },
  });
  const ok = check(
    res,
    { [`${name} ok`]: (r) => r.status === 200 && !r.json('errors') && r.json('data') != null },
    { flow: 'web' }
  );
  if (!ok) console.error(`${name} failed: ${res.status} ${String(res.body).slice(0, 300)}`);
  return ok ? res.json('data') : null;
}

function webBrowse() {
  if (!ensureLogin()) return;

  // Containers page: list query, then temperatures of one container — what the browser does.
  if (containerIds.length === 0 || Math.random() < 0.2) {
    const data = graphql(CONTAINERS_QUERY, { after: null }, 'containers');
    const nodes = data && data.containers ? data.containers.nodes : [];
    if (nodes.length > 0) containerIds = nodes.map((n) => n.id);
  }
  if (containerIds.length > 0) {
    const id = containerIds[Math.floor(Math.random() * containerIds.length)];
    graphql(TEMPERATURES_QUERY, { containerId: id, after: null }, 'temperatures');
  }
}

export function main() {
  const roll = Math.random();
  if (WEB_FLOW && roll < 0.2) webBrowse();
  else if (roll < 0.9) webhookPost();
  else if (roll < 0.95) badSignature();
  else health();
  if (THINK) sleep(0.5 + Math.random() * 1.5);
}

// Lets CLI overrides like --duration/--vus (which replace the scenario config) still run.
export default main;

export function handleSummary(data) {
  const ts = new Date().toISOString().replace(/[:.]/g, '-');
  return {
    stdout: textSummary(data, { indent: ' ', enableColors: true }),
    [`results/${TYPE}-${ts}.json`]: JSON.stringify(data, null, 2),
  };
}
