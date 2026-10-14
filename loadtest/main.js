// k6 load-test suite for the DataHub webhook.
//
// One script, four test types, selected via -e TEST_TYPE=smoke|load|stress|soak.
// Target via -e BASE_URL=... (use run.sh for the presets). Every valid iteration
// POSTs an HMAC-signed container-readings payload to /api/webhooks/container and
// expects 202. See README.md.

import http from 'k6/http';
import crypto from 'k6/crypto';
import { check, sleep } from 'k6';
import { textSummary } from 'https://jslib.k6.io/k6-summary/0.0.2/index.js';

const BASE = (__ENV.BASE_URL || 'https://localhost:7071').replace(/\/+$/, '');
const TYPE = __ENV.TEST_TYPE || 'smoke';
const SECRET = __ENV.HMAC_SECRET || '';
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

if (!SCENARIOS[TYPE]) {
  throw new Error(`Unknown TEST_TYPE "${TYPE}" — use smoke|load|stress|soak`);
}

export const options = {
  scenarios: { [TYPE]: { ...SCENARIOS[TYPE], exec: 'main' } },
  thresholds: THRESHOLDS[TYPE],
  insecureSkipTLSVerify: __ENV.SKIP_TLS === '1', // Caddy internal CA / dev cert
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

export function main() {
  const roll = Math.random();
  if (roll < 0.9) webhookPost();
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
