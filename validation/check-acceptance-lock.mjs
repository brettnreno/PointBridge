// Local fixture-admin check only; never submits Metasys credentials.
const origin='http://127.0.0.1:15090';
let cookie='',csrf='';
async function request(path,method='GET',body){const r=await fetch(origin+path,{method,headers:{...(cookie?{Cookie:cookie}:{}),...(method!=='GET'?{'Content-Type':'application/json','X-CSRF-Token':csrf,Origin:origin}:{})},body:body===undefined?undefined:JSON.stringify(body)});for(const c of r.headers.getSetCookie())if(c.startsWith('MetasysPointBridge.Session='))cookie=c.split(';')[0];const text=await r.text();let json;try{json=JSON.parse(text);}catch{}return{status:r.status,json};}
function check(ok,label){if(!ok)throw new Error(label);console.log('PASS '+label);}
const session=await request('/api/session');csrf=session.json.csrfToken;
check(session.json.loginOnlyAcceptance&&session.json.metasysLoginAvailable&&!session.json.setupRequired,'login-only provider ready with local fallback');
check((await request('/api/auth/login','POST',{username:'fixture-admin',password:'fixture-admin-password'})).status===200,'existing isolated fixture administrator retained');
const state=await request('/api/state');
check(state.status===200&&state.json.armed===false&&state.json.mappings.length===0,'transfers paused and mappings empty');
check(!state.json.source.baseUrl&&!state.json.destination.baseUrl,'no source or destination connection configured');
check((await request('/api/runtime','POST',{armed:true})).status===403,'administrator cannot arm acceptance transfers');
check((await request('/api/auth/logout','POST',{})).status===200,'fixture administrator signed out');
console.log('Acceptance instance checked without any Metasys login request.');
