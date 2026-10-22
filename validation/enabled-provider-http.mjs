import fs from 'node:fs';
const origin='http://127.0.0.1:15089';
const dataPath=process.env.BRIDGE_TEST_DATA;
let passed=0;
function check(value,label){if(!value)throw new Error(label);passed++;console.log('PASS '+label);}
function client(){return{cookie:'',csrf:''};}
async function request(c,path,method='GET',body){
 const response=await fetch(origin+path,{method,headers:{...(c.cookie?{Cookie:c.cookie}:{}),...(method!=='GET'&&method!=='HEAD'?{'Content-Type':'application/json','X-CSRF-Token':c.csrf,Origin:origin}:{})},body:body===undefined?undefined:JSON.stringify(body)});
 for(const cookie of response.headers.getSetCookie()){if(cookie.startsWith('MetasysPointBridge.Session='))c.cookie=cookie.split(';')[0];}
 const text=await response.text();let json;try{json=JSON.parse(text);}catch{}
 check(!text.includes('fixture-api-token-not-real')&&!text.includes('fixture-api-password')&&!text.includes('fixture-token-not-real'),'response contains no vendor token');
 return {status:response.status,json};
}
async function session(c){const r=await request(c,'/api/session');c.csrf=r.json.csrfToken;return r.json;}
const admin=client(), op=client();
check((await request(client(),'/api/health/mappings')).status===401,'health diagnostics require authentication');
check((await request(client(),'/api/health/watchdog-frame')).status===401,'watchdog frame diagnostics require authentication');
const initial=await session(admin);
check(initial.loginOnlyAcceptance&&initial.metasysLoginAvailable,'actual staged startup enables only isolated login acceptance');
if(fs.existsSync(dataPath+'/setup-code.txt')){const setupCode=fs.readFileSync(dataPath+'/setup-code.txt','utf8').trim();check((await request(admin,'/api/auth/setup','POST',{setupCode,username:'fixture-admin',password:'fixture-admin-password'})).status===200,'fixture local admin setup');}
else check((await request(admin,'/api/auth/login','POST',{username:'fixture-admin',password:'fixture-admin-password'})).status===200,'fixture local fallback login');
await session(op);
check((await request(op,'/api/auth/metasys/login','POST',{username:'fixture-user',password:'fixture-only-password'})).status===200,'enabled provider login through real application endpoint');
const identity=await session(op);
check(identity.authenticated&&identity.username==='fixture-user'&&identity.role==='Admin','cookie validates canonical identity and sole administrator permission');
const health=await request(op,'/api/health/mappings');
check(health.status===200&&health.json.armed===false&&health.json.mappings.length===0,'authenticated health interface reports no active transfer workers');
check(health.json.scope.includes('field-observation-age-not-established'),'health interface does not claim measurement freshness from HTTP success');
check(health.json.reconciliationSeconds===300&&health.json.operationDeadlineSeconds===45&&health.json.maxConcurrentChecks===8,'configured default is five-minute checks with bounded operation and concurrency');
const frame=await request(op,'/api/health/watchdog-frame');
check(frame.status===200&&frame.json.schemaVersion===1&&!frame.json.overallHealthy&&!frame.json.transfersArmed&&frame.json.mappings.length===0,'paused acceptance instance never emits healthy watchdog frame');
check(frame.json.scope.includes('physical-observation-age-not-established'),'watchdog diagnostic scope states field freshness limitation');
check((await request(op,'/api/auth/users')).status===200,'authenticated configured Metasys user can manage users');
check((await request(op,'/api/runtime','POST',{armed:true})).status===403,'login-only instance cannot arm transfers');
check((await request(admin,'/api/runtime','POST',{armed:true})).status===403,'local administrator cannot arm login-only transfers');
check((await request(admin,'/api/connections/source','POST',{})).status===403,'login-only instance blocks connection changes');
if(initial.connectionTestsAllowed){
 const before=await request(op,'/api/state');
 for(const [side,apiVersion] of [['source','v5'],['destination','v6']]){
  const settings={baseUrl:'https://fixture-api.invalid',apiVersion,username:'fixture-api-user',password:'fixture-api-password'};
  check((await request(op,`/api/connections/${side}/test`,'POST',settings)).status===200,`unsaved ${side} API ${apiVersion} authentication test allowed`);
 }
 const after=await request(op,'/api/state');
 check(JSON.stringify(before.json.source)===JSON.stringify(after.json.source)&&JSON.stringify(before.json.destination)===JSON.stringify(after.json.destination),'Test connection does not save credentials or configuration');
 const settings={baseUrl:'https://fixture-api.invalid',apiVersion:'v5',username:'fixture-api-user',password:'fixture-api-password'};
 check((await request(op,'/api/connections/source','PUT',settings)).status===200,'saving API connection allowed with transfers locked');
 const stateAfterSave=await request(op,'/api/state');
 check(stateAfterSave.json.armed===false,'saving connection leaves transfers disarmed');
 check((await request(op,'/api/connections/source/credentials','DELETE')).status===200,'clearing saved credentials allowed');
 const cleared=await request(op,'/api/state');
 check(cleared.json.source.hasPassword===false&&cleared.json.armed===false,'clear removes saved password and leaves transfers paused');
 check((await request(op,'/api/mappings','POST',{})).status===403,'mapping mutation remains locked');
 check((await request(op,'/api/runtime','POST',{armed:true})).status===403,'API testing does not unlock transfers');
}
check((await request(admin,'/api/fixture/mode/outage','POST',{})).status===200,'fixture outage enabled');
check((await request(op,'/api/state')).status===401,'provider outage revokes external cookie');
check((await session(admin)).authenticated,'local fallback remains available during outage');
await request(admin,'/api/fixture/mode/normal','POST',{});
check(!(await session(op)).authenticated,'revoked cookie stays invalid after recovery');
await request(admin,'/api/fixture/mode/substitute','POST',{});
check((await request(op,'/api/auth/metasys/login','POST',{username:'fixture-user',password:'fixture-only-password'})).status===401,'canonical identity substitution denied');
await request(admin,'/api/fixture/mode/disabled','POST',{});
check((await request(op,'/api/auth/metasys/login','POST',{username:'fixture-user',password:'fixture-only-password'})).status===401,'disabled identity denied');
await request(admin,'/api/fixture/mode/normal','POST',{});
check((await request(op,'/api/auth/metasys/login','POST',{username:'fixture-user',password:'fixture-only-password'})).status===200,'fresh explicit login after recovery');
check((await request(op,'/api/auth/logout','POST',{})).status===200,'external logout endpoint completes');
check(!(await session(op)).authenticated,'external logout removes application access');
for(const [mode,code] of [['tls','metasys-tls-failed'],['outage','metasys-unavailable']]){
 await request(admin,'/api/fixture/mode/'+mode,'POST',{});
 const result=await request(op,'/api/auth/metasys/login','POST',{username:'fixture-user',password:'fixture-only-password'});
 check(result.status===401&&result.json.code===code,'sanitized '+mode+' diagnosis');
}
await request(admin,'/api/fixture/mode/normal','POST',{});
const denied=await request(op,'/api/auth/metasys/login','POST',{username:'fixture-user',password:'wrong-fixture-password'});
const ungranted=await request(op,'/api/auth/metasys/login','POST',{username:'unknown-fixture-user',password:'wrong-fixture-password'});
check(denied.json.code===ungranted.json.code&&denied.json.error===ungranted.json.error,'rejected and ungranted identities have identical public errors');
const stats=await request(admin,'/api/fixture/status');check(stats.json.bridgeWorkerHosted===false,'no transfer worker is hosted during connection tests');if(initial.connectionTestsAllowed)check(stats.json.apiLoginCalls===2,'only two explicit fake API login calls; no point writes');check(stats.json.logoutCalls>=4,'remote fixture cleanup attempted on rejection and logout');
console.log(`${passed} enabled-provider HTTP assertions passed; fixture transport only.`);
