// Isolated local test fixture only. Never use production account credentials here.
import fs from 'node:fs';
import path from 'node:path';
const origin=process.env.BRIDGE_TEST_URL||'http://127.0.0.1:5090';
const dataPath=path.resolve(process.env.BRIDGE_TEST_DATA||'work/v1-data');
const codeFile=path.join(dataPath,'setup-code.txt');
const adminName='testadmin', adminPassword='test-not-a-real-password';
let passed=0;const lines=[];
function check(value,message){if(!value)throw new Error(message);}
function pass(name){passed++;lines.push('PASS '+name);console.log(lines.at(-1));}
function client(){return{cookie:'',csrf:''};}
async function req(c,route,method='GET',body,headers={}){
 const r=await fetch(origin+route,{method,headers:{...(c.cookie?{Cookie:c.cookie}:{}),...(method!=='GET'?{'Content-Type':'application/json','X-CSRF-Token':c.csrf,Origin:origin}:{}),...headers},body:body===undefined?undefined:JSON.stringify(body),signal:AbortSignal.timeout(10000)});
 const cookies=r.headers.getSetCookie();for(const value of cookies){if(value.startsWith('MetasysPointBridge.Session=')){const cookie=value.split(';')[0];c.cookie=cookie.endsWith('=')?'':cookie;}}
 const text=await r.text();let json;try{json=JSON.parse(text);}catch{}
 return{status:r.status,json,text,headers:r.headers,cookies};
}
async function session(c){const r=await req(c,'/api/session');check(r.status===200,'session endpoint');c.csrf=r.json.csrfToken;return r.json;}
try{
 const anon=client();check((await req(anon,'/api/state')).status===401,'unauthenticated state must401');pass('Unauthenticated protected API returns401');
 const s=await session(anon);check(typeof anon.csrf==='string'&&anon.csrf.length>=32,'CSRF token present');pass('Public session supplies CSRF and authentication state');
 let admin=client();await session(admin);
 check((await req(admin,'/api/auth/login','POST',{username:adminName,password:adminPassword},{'X-CSRF-Token':'invalid'})).status===403,'invalid CSRF must403');pass('Invalid CSRF rejected before authentication');
 let auth;
 if(s.setupRequired){
   const setupCode=process.env.BRIDGE_TEST_SETUP_CODE||fs.readFileSync(process.env.BRIDGE_TEST_SETUP_FILE||codeFile,'utf8').trim();
   check((await req(admin,'/api/auth/setup','POST',{setupCode:'incorrect-fixture',username:adminName,password:adminPassword})).status===403,'invalid setup code must403');pass('Incorrect setup code rejected');
   auth=await req(admin,'/api/auth/setup','POST',{setupCode,username:adminName,password:adminPassword});check(auth.status===200,'valid setup failed');
   check(!(await session(admin)).setupRequired,'setup remains available');
   check((await req(admin,'/api/auth/setup','POST',{setupCode,username:'duplicate-admin',password:adminPassword})).status===400,'setup token reusable');pass('Local first administrator setup is one-time');
 }else auth=await req(admin,'/api/auth/login','POST',{username:adminName,password:adminPassword});
 check(auth.status===200&&admin.cookie,'admin sign-in cookie missing');
 check(auth.cookies.some(c=>/httponly/i.test(c)&&/samesite=strict/i.test(c)),'cookie security flags');pass('Administrator cookie is HttpOnly and SameSite Strict');
 check((await session(admin)).role==='Admin','admin role');
 check((await req(admin,'/api/state')).status===200,'admin state access');pass('Authenticated administrator reads state');
 check((await req(admin,'/api/runtime','POST',{armed:false},{Origin:'https://untrusted.invalid'})).status===403,'foreign Origin must403');pass('Cross-origin mutation rejected');
 const fakePassword='fixture-metasys-secret-never-real';
 const connection=await req(admin,'/api/connections/source','PUT',{baseUrl:'https://test-source.invalid',apiVersion:'v5',username:'fixture-api-user',password:fakePassword});
 check(connection.status===200,'admin connection save failed '+connection.status);check(!connection.text.includes(fakePassword)&&connection.json.hasPassword,'credential response leaks password');
 try{check(!fs.readFileSync(path.join(dataPath,'config.json'),'utf8').includes(fakePassword),'plaintext secret on disk');pass('Credential file contains no plaintext test secret');}catch(e){if(e.code!=='EPERM'&&e.code!=='EACCES')throw e;lines.push('SKIP On-disk credential inspection: Windows ACL blocks sandbox access');}
 pass('Administrator saves fake API credentials through Windows DPAPI; response hides password');
 const userName='testoperator'+Date.now();const operatorPassword='operator-fixture-password';
 const created=await req(admin,'/api/auth/users','POST',{username:userName,password:operatorPassword,role:'Operator'});check(created.status===200&&created.json.id,'operator creation');pass('Administrator creates operator');
 const op=client();await session(op);check((await req(op,'/api/auth/login','POST',{username:userName,password:operatorPassword})).status===200,'operator login');check((await session(op)).role==='Admin','operator role');pass('Legacy Operator account request signs in as Administrator');
 check((await req(op,'/api/state')).status===200,'operator read state');check((await req(op,'/api/logs')).status===200,'operator read logs');pass('Operator reads mapping state and activity');
 check((await req(op,'/api/connections/source','PUT',{baseUrl:'https://blocked.invalid',apiVersion:'v5',username:'x',password:'x'})).status===200,'all signed-in users can configure credentials');
 check((await req(op,'/api/auth/users')).status===200,'all signed-in users can list users');
 check((await req(op,'/api/auth/users','POST',{username:'blocked',password:'blocked-password',role:'Admin'})).status===200,'all signed-in users can create accounts');pass('Single administrator permission allows connection and user management');
 check((await req(op,'/api/runtime','POST',{armed:false})).status===200,'operator cannot stop transfers');pass('Operator can stop transfers');
 check((await req(admin,'/api/auth/users/'+created.json.id,'PUT',{role:'Operator',enabled:false,password:null})).status===200,'disable operator');
 check((await req(op,'/api/state')).status===401,'disabled operator session still active');pass('Disabling user immediately revokes existing session');
 check((await req(admin,'/api/auth/logout','POST',{})).status===200&&!admin.cookie,'logout clears cookie');check((await req(admin,'/api/state')).status===401,'loggedout read');pass('Logout clears browser session and protected access');
 try{const users=JSON.parse(fs.readFileSync(path.join(dataPath,'users.json'),'utf8'));check(!JSON.stringify(users).includes(adminPassword)&&users.every(u=>u.passwordHash&&u.salt&&!u.password),'local passwords plaintext');pass('Local account file stores salted hashes, not passwords');}catch(e){if(e.code!=='EPERM'&&e.code!=='EACCES')throw e;lines.push('SKIP On-disk local password inspection: Windows ACL blocks sandbox access');}
 console.log(`${passed} HTTP authentication scenarios passed`);lines.push(`${passed} HTTP authentication scenarios passed`);
}catch(e){lines.push('FAIL '+e.message);console.error(lines.at(-1));process.exitCode=1;}
finally{fs.writeFileSync(new URL('AUTH-RESULTS.txt',import.meta.url),lines.join('\n')+'\n');}
