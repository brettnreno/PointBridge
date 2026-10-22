const origin='http://127.0.0.1:15089';let cookie='',csrf='',passed=0;
function check(ok,name){if(!ok)throw new Error(name);passed++;console.log('PASS '+name);}
async function request(path,method='GET',body){const r=await fetch(origin+path,{method,headers:{...(cookie?{Cookie:cookie}:{}),...(method!=='GET'?{'Content-Type':'application/json','X-CSRF-Token':csrf,Origin:origin}:{})},body:body===undefined?undefined:JSON.stringify(body),signal:AbortSignal.timeout(10000)});for(const c of r.headers.getSetCookie())if(c.startsWith('MetasysPointBridge.Session='))cookie=c.split(';')[0];return r;}
check((await request('/api/diagnostics/export')).status===401,'diagnostics download requires authentication');
csrf=(await(await request('/api/session')).json()).csrfToken;
check((await request('/api/auth/login','POST',{username:'fixture-admin',password:'fixture-admin-password'})).status===200,'synthetic local administrator login');
const response=await request('/api/diagnostics/export'),text=await response.text();
check(response.status===200,'diagnostic download succeeds');
check(response.headers.get('content-type').includes('application/json')&&response.headers.get('content-disposition').includes('attachment'),'bounded JSON attachment response');
check(response.headers.get('cache-control')==='no-store','diagnostics not browser cached');
check(Buffer.byteLength(text)<=512*1024,'download respects512KiB cap');
for(const secret of ['fixture-admin','fixture-admin-password','fixture-api-token-not-real','fixture-only-password','fixture-external-key-not-real','fixture-token-not-real','fixture-api.invalid','fixture-user'])check(!text.includes(secret),'excludes synthetic sensitive field '+secret.split('-')[0]);
const data=JSON.parse(text);check(data.exportSchemaVersion===1&&data.recovery.schemaVersion===1,'versioned export and recovery metadata');
check(data.config.schemaVersion>=1&&data.buildVersion&&data.runtime,'build and config metadata included');
check(data.runtimeState.armed===false&&data.mappings.length===0,'export does not start transfer workers');
check(data.logs.length<=100,'bounded allowlisted activity records');
const stats=await(await request('/api/fixture/status')).json();check(stats.pointWrites===0&&!stats.bridgeWorkerHosted,'download triggers no live operation or point writes');
check((await request('/extensions.js')).status===200,'download button script served');
console.log(`${passed} recovery/export HTTP assertions passed; fake transports only.`);
