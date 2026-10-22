import fs from 'node:fs';
const origin = 'http://127.0.0.1:15089', point = '52cfddc0-411f-5b9e-9a6d-442cb62fecbb';
let cookie = '', csrf = '', passed = 0;
function check(condition, label) { if (!condition) throw new Error(label); passed++; console.log('PASS ' + label); }
async function call(path, method = 'GET', body, authenticated = true) {
    const response = await fetch(origin + path, {method, headers: {
        ...(authenticated && cookie ? {Cookie: cookie} : {}),
        ...(method !== 'GET' ? {'Content-Type': 'application/json', 'X-CSRF-Token': csrf, Origin: origin} : {})
    }, body: body === undefined ? undefined : JSON.stringify(body)});
    for (const value of response.headers.getSetCookie()) if (value.startsWith('MetasysPointBridge.Session=')) cookie = value.split(';')[0];
    const text = await response.text();
    check(!text.includes('fixture-api-token-not-real') && !text.includes('fixture-sensitive-value-never-display'), 'explorer responses exclude raw vendor secrets');
    let json; try { json = JSON.parse(text); } catch {}
    return {status: response.status, json, text};
}
csrf = (await call('/api/session')).json.csrfToken;
check((await call('/api/metasys/destination/browse', 'GET', undefined, false)).status === 401, 'discovery requires sign-in');
check((await call('/api/metasys/destination/inspect?object=' + point, 'GET', undefined, false)).status === 401, 'inspection requires sign-in');
check((await call('/api/auth/login', 'POST', {username: 'fixture-admin', password: 'fixture-admin-password'})).status === 200, 'isolated explorer fixture login');
for (const version of ['v4', 'v5', 'v6']) {
    check((await call('/api/connections/destination', 'PUT', {baseUrl: 'https://fixture-api.invalid', apiVersion: version, username: 'fixture-api-user', password: 'fixture-only-password'})).status === 200, version + ' recipient configured');
    for (const kind of ['objects', 'networkDevices', 'equipment', 'spaces']) {
        const result = await call('/api/metasys/destination/browse?kind=' + kind);
        check(result.status === 200 && result.json.items.length === 1, version + ' ' + kind + ' browse');
    }
    const points = await call(`/api/metasys/destination/browse?kind=equipmentPoints&parentId=${point}`);
    check(points.status === 200 && points.json.items[0].id === point && points.json.items[0].selectable, version + ' equipment point resolves underlying object');
    const values = await call('/api/metasys/destination/values', 'POST', {objectIds: [point]});
    check(values.status === 200 && values.json.items[0].success && values.json.items[0].value === 20 && values.json.readOnly, version + ' explicit batched value read');
    for (const view of ['summary', 'trends', 'alarms', 'audits']) {
        const result = await call(`/api/metasys/destination/inspect?object=${point}&view=${view}`);
        check(result.status === 200 && result.json.readOnly, version + ' read-only ' + view);
        if (view === 'trends') check(result.json.items[0].value === 20, version + ' trend format normalized');
    }
}
check((await call('/api/metasys/destination/browse?kind=equipmentPoints&parentId=..%2Flogin')).status === 400, 'discovery rejects route injection');
check((await call('/api/metasys/destination/browse?kind=untrusted')).status === 400, 'unknown discovery kind rejected');
check((await call('/api/metasys/destination/values', 'POST', {objectIds: ['../login']})).status === 400, 'batch rejects arbitrary routes');
check((await call('/api/metasys/destination/values', 'POST', {objectIds: []})).status === 400, 'empty batch rejected');
const rejected = await fetch(origin + '/api/metasys/destination/values', {method: 'POST', headers: {Cookie: cookie, 'Content-Type': 'application/json'}, body: JSON.stringify({objectIds: [point]})});
check(rejected.status === 403, 'batch read still enforces CSRF');
check((await call(`/api/metasys/destination/inspect?object=${point}&view=acknowledge`)).status === 400, 'inspector has no acknowledgement operation');
check((await call(`/api/metasys/destination/inspect?object=${point}`, 'POST', {})).status === 405, 'inspector rejects mutation methods');
check((await call('/api/metasys/destination/browse?kind=objects&page=2')).status === 400, 'unpaged object hierarchy rejects unsupported pages');
check((await call('/explorer.js')).status === 200 && (await call('/explorer.css')).status === 200, 'explorer UI resources served');
const stats = (await call('/api/fixture/status')).json;
check(stats.pointWrites === 0 && !stats.bridgeWorkerHosted, 'all discovery and diagnostics execute zero point writes');
check(!fs.readFileSync(process.env.BRIDGE_TEST_DATA + '/config.json', 'utf8').includes('fixture-only-password'), 'saved connection password remains encrypted');
console.log(`${passed} explorer HTTP assertions passed; no live endpoints or writes.`);
