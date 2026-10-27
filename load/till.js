// Saturday-peak load for the till API: 50 tills across the three venues, with a share of sales resent under the same
// Idempotency-Key, the way a till retries after a lost response. Run through load/drill.sh, which sets SCENARIO and RUN.

import exec from 'k6/execution';
import http from 'k6/http';
import { check } from 'k6';
import { Counter } from 'k6/metrics';
import { textSummary } from 'https://jslib.k6.io/k6-summary/0.1.0/index.js';

const config = JSON.parse(open('../web/till/public/config.json'));
const RUN = __ENV.RUN;
const SCENARIO = __ENV.SCENARIO;
const RESEND_SHARE = 0.07;
const TILLS = { 'paris-01': 17, 'berlin-01': 17, 'vienna-01': 16 };
const FLOOD_VENUE = 'paris-01';

// Two items per market whose categories exist in config/markets.json; prices include VAT.
const ITEMS = {
  FR: [{ sku: 'ADM-ADULTE', description: 'Entrée adulte', category: 'admission', unitPriceCents: 2900 },
       { sku: 'CREPE', description: 'Crêpe au sucre', category: 'food-onsite', unitPriceCents: 450 }],
  DE: [{ sku: 'EINTRITT-ERW', description: 'Erwachsene', category: 'admission', unitPriceCents: 3900 },
       { sku: 'BRATWURST', description: 'Bratwurst', category: 'food-onsite', unitPriceCents: 450 }],
  AT: [{ sku: 'EINTRITT-ERW', description: 'Erwachsene', category: 'admission', unitPriceCents: 2400 },
       { sku: 'MILCH', description: 'Milch 1 l', category: 'staple-goods', unitPriceCents: 149 }],
};

const accepted = new Counter('sales_accepted');
const resent = new Counter('sales_resent');
const throttled = new Counter('sales_throttled');

const peak = (rate, minutes) => ({
  executor: 'ramping-arrival-rate',
  startRate: 1,
  timeUnit: '1s',
  preAllocatedVUs: 40,
  maxVUs: 200,
  stages: [{ target: rate, duration: '1m' }, { target: rate, duration: `${minutes - 2}m` }, { target: 0, duration: '1m' }],
});

const SCENARIOS = {
  smoke: { tills: { executor: 'constant-arrival-rate', rate: 2, timeUnit: '1s', duration: '20s', preAllocatedVUs: 5 } },
  baseline: { tills: peak(10, 10) },
  slow: { tills: peak(20, 5) },
  outage: { tills: peak(10, 8) },
  // One venue's tills far over the 50 requests per second its key allows, while the other venues trade normally.
  burst: {
    flood: { executor: 'constant-arrival-rate', rate: 150, timeUnit: '1s', duration: '1m', preAllocatedVUs: 80, maxVUs: 300, env: { VENUE: FLOOD_VENUE } },
    others: { executor: 'constant-arrival-rate', rate: 4, timeUnit: '1s', duration: '1m', preAllocatedVUs: 10, maxVUs: 40, env: { VENUE: 'others' } },
  },
};

if (!RUN || !SCENARIOS[SCENARIO]) {
  throw new Error(`run through load/drill.sh: RUN is '${RUN}', SCENARIO '${SCENARIO}' (one of ${Object.keys(SCENARIOS).join(', ')})`);
}

export const options = {
  scenarios: SCENARIOS[SCENARIO],
  thresholds: SCENARIO === 'burst' ? {} : { checks: ['rate>0.99'], 'checks{kind:resend}': ['rate==1'] },
};

http.setResponseCallback(http.expectedStatuses(202, 429));

function till(iteration) {
  const venues = config.venues.filter((venue) =>
    !__ENV.VENUE || (__ENV.VENUE === 'others' ? venue.venueId !== FLOOD_VENUE : venue.venueId === __ENV.VENUE));
  const tills = venues.flatMap((venue) =>
    Array.from({ length: TILLS[venue.venueId] }, (_, index) => ({ venue, tillId: `T${String(index + 1).padStart(2, '0')}` })));
  return tills[iteration % tills.length];
}

export default function () {
  const iteration = exec.scenario.iterationInTest;
  const { venue, tillId } = till(iteration);
  const lines = ITEMS[venue.market].map((item, index) => ({ ...item, quantity: 1 + ((iteration + index) % 3) }));
  const body = JSON.stringify({
    tillId,
    lines,
    payment: { method: iteration % 4 === 0 ? 'cash' : 'card', amountCents: lines.reduce((sum, line) => sum + line.unitPriceCents * line.quantity, 0) },
  });
  const params = {
    headers: { 'Content-Type': 'application/json', 'x-api-key': venue.apiKey, 'Idempotency-Key': `${RUN}-${exec.scenario.name}-${iteration}` },
    tags: { venue: venue.venueId },
  };

  const first = http.post(`${config.apiUrl}/sales`, body, params);
  if (first.status === 429) {
    throttled.add(1);
    return;
  }
  check(first, { 'sale accepted (202)': (response) => response.status === 202 });
  if (first.status !== 202) {
    return;
  }
  accepted.add(1);

  if (Math.random() < RESEND_SHARE) {
    resent.add(1);
    const replay = http.post(`${config.apiUrl}/sales`, body, params);
    if (replay.status === 429) {
      throttled.add(1);
      return;
    }
    check(replay, {
      'resend returns the same sale': (response) =>
        response.status === 202 && response.json('saleId') === first.json('saleId') && response.headers['Idempotent-Replayed'] === 'true',
    }, { kind: 'resend' });
  }
}

export function handleSummary(data) {
  return {
    [`load/results/${RUN}.json`]: JSON.stringify(data, null, 2),
    stdout: textSummary(data, { indent: ' ', enableColors: false }),
  };
}
