const origin='http://127.0.0.1:15089',point='52cfddc0-411f-5b9e-9a6d-442cb62fecbb';
let cookie='',csrf='',passed=0;
function check(condition,label){if(!condition)throw new Error(label);passed++;console.log('PASS '+label);}
async function call(path,method='GET',body){
    const response=await fetch(origin+path,{method,headers:{...(cookie?{Cookie:cookie}:{}),...(method!=='GET'?{'Content-Type':'application/json','X-CSRF-Token':csrf,Origin:origin}:{})},body:body===undefined?undefined:JSON.stringify(body)});
    for(const value of response.headers.getSetCookie())if(value.startsWith('MetasysPointBridge.Session='))cookie=value.split(';')[0];
    const text=await response.text();
    check(!text.includes('fixture-api-token-not-real')&&!text.includes('fixture-private-response-not-for-display'),'errors exclude vendor tokens and raw failure bodies');
    let json;try{json=JSON.parse(text);}catch{}return{status:response.status,json};
}
csrf=(await call('/api/session')).json.csrfToken;
check((await call('/api/auth/login','POST',{username:'fixture-admin',password:'fixture-admin-password'})).status===200,'isolated session diagnostics login');
const connection={baseUrl:'https://fixture-api-two.invalid',apiVersion:'v5',username:'fixture-api-user',password:'fixture-only-password'};
check((await call('/api/connections/source','PUT',connection)).status===200,'source saved');
check((await call('/api/connections/destination','PUT',{...connection,baseUrl:'https://fixture-api.invalid',apiVersion:'v4'})).status===200,'destination saved');
const before=(await call('/api/fixture/status')).json.apiLoginCalls;
const mapping={name:'Fixture source mapping',sourceObject:point,destinationObject:point,writeMode:'attribute'};
for(let index=0;index<2;index++)check((await call('/api/mappings/validate','POST',mapping)).json.valid,'mapping validation succeeds with shared API session');
check((await call('/api/fixture/status')).json.apiLoginCalls===before+2,'repeated mapping checks perform only one login per configured connection');
check((await call('/api/connections/source/test','POST')).status===200,'saved connection test contacts API through refresh');
check((await call('/api/fixture/status')).json.apiLoginCalls===before+2,'saved connection test avoids password login churn');
await call('/api/fixture/api-mode/source403','POST',{});
let validation=(await call('/api/mappings/validate','POST',mapping)).json;
check(!validation.valid&&validation.source==null&&validation.errors.some(error=>error.includes('source:')&&error.includes('GET /api/v5/objects/'+point+'/attributes/presentValue')&&error.includes('403')),'source 403 identifies exact refused source request');
await call('/api/fixture/api-mode/destination403','POST',{});
validation=(await call('/api/mappings/validate','POST',mapping)).json;
check(!validation.valid&&validation.source?.value===20&&validation.errors.some(error=>error.includes('destination:')&&error.includes('GET /api/v4/objects/'+point+'/attributes/presentValue')&&error.includes('403')),'destination 403 retains successful source details and names destination request');
await call('/api/fixture/api-mode/normal','POST',{});
check((await call('/api/mappings/validate','POST',mapping)).json.valid,'restored permissions recover without replacing sessions');
const stats=(await call('/api/fixture/status')).json;
check(stats.apiLoginCalls===before+2&&stats.pointWrites===0&&!stats.bridgeWorkerHosted,'403 errors do not retry password logins or issue writes');
console.log(`${passed} API session and request-diagnostic HTTP assertions passed.`);
