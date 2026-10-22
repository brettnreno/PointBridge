import fs from 'node:fs';
const origin='http://127.0.0.1:15089',data=process.env.BRIDGE_TEST_DATA;let cookie='',csrf='',passed=0;
const key='fixture-external-key-not-real';
function check(ok,label){if(!ok)throw new Error(label);passed++;console.log('PASS '+label);}
async function call(path,method='GET',body){const r=await fetch(origin+path,{method,headers:{...(cookie?{Cookie:cookie}:{}),...(method!=='GET'?{'Content-Type':'application/json','X-CSRF-Token':csrf,Origin:origin}:{})},body:body===undefined?undefined:JSON.stringify(body)});for(const c of r.headers.getSetCookie())if(c.startsWith('MetasysPointBridge.Session='))cookie=c.split(';')[0];const text=await r.text();check(!text.includes(key),'response excludes API secret');let json;try{json=JSON.parse(text);}catch{}return{status:r.status,json};}
let session=await call('/api/session');csrf=session.json.csrfToken;
if(fs.existsSync(data+'/setup-code.txt')){const setupCode=fs.readFileSync(data+'/setup-code.txt','utf8').trim();check((await call('/api/auth/setup','POST',{setupCode,username:'fixture-admin',password:'fixture-admin-password'})).status===200,'isolated local fixture setup');}
else check((await call('/api/auth/login','POST',{username:'fixture-admin',password:'fixture-admin-password'})).status===200,'fixture admin login');
let state=(await call('/api/state')).json;check(!state.extensionsEnabled&&Object.keys(state.externalSources).length===0,'extensions start disabled and empty');
const recipient='3ddc1120-a657-4c49-aea7-b40ed67d3cce',sourceId='a7a80f73-343d-48dd-a8b9-9207c42d6745',object='52cfddc0-411f-5b9e-9a6d-442cb62fecbb';
check((await call('/api/connections/destination','PUT',{baseUrl:'https://fixture-api.invalid',apiVersion:'v5',username:'fixture-api-user',password:'fixture-only-password'})).status===200,'default destination save');
check((await call('/api/connections/destination:'+recipient,'PUT',{baseUrl:'https://fixture-api-two.invalid',apiVersion:'v6',username:'fixture-api-user',password:'fixture-only-password'})).status===200,'additional destination save');
check((await call('/api/connections/extensions','PUT',{enabled:true})).status===200,'explicit extension opt-in');
const source={name:'Fixture JSON temperature',enabled:false,url:'https://fixture-json.invalid/data',valuePointer:'/value',timestampPointer:'/observed',unitPointer:'/unit',inputUnit:'unitEnumSet.degC',outputUnit:'unitEnumSet.degC',unitAliasConfirmed:false,valueType:'number',pollSeconds:300,maxAgeSeconds:120,timeoutSeconds:10,userAgent:'PointBridge offline fixture',authMode:'header',authHeader:'X-Fixture-Key',secret:key,qualityPointer:'/quality',reliableQualityValue:'good'};
check((await call('/api/connections/json/'+sourceId,'PUT',source)).status===200,'external source saved disabled');
const sample=await call('/api/connections/json/'+sourceId+'/test','POST',{});check(sample.status===200&&sample.json.value===20,'explicit read-only test works before source enablement');
state=(await call('/api/state')).json;check(!state.externalSources[sourceId].enabled&&state.externalSources[sourceId].hasSecret&&!('secret' in state.externalSources[sourceId]),'test neither enables source nor exposes saved secret');
source.enabled=true;source.secret='';check((await call('/api/connections/json/'+sourceId,'PUT',source)).status===200,'enable reviewed source without resubmitting saved key');
const mapping={name:'JSON to default recipient',sourceKind:'json',externalSourceId:sourceId,sourceObject:'json:'+sourceId,destinationConnectionId:'destination',destinationObject:object,sourceAttribute:'presentValue',destinationAttribute:'presentValue',writeMode:'attribute',commandId:'',commandPriority:null,enabled:true};
const validation=await call('/api/mappings/validate','POST',mapping);check(validation.status===200&&validation.json.valid===true,'JSON sample validated through existing destination preflight');
const first=await call('/api/mappings','POST',mapping);check(first.status===200&&first.json.id,'first JSON mapping saved');
const second=await call('/api/mappings','POST',{...mapping,name:'JSON to additional recipient',destinationConnectionId:recipient});check(second.status===200&&second.json.id,'same source maps to second v6 server');
state=(await call('/api/state')).json;check(state.mappings.length===2&&!state.armed&&state.source.baseUrl==='','JSON fanout configured without Metasys source account or arming');
check((await call('/api/runtime','POST',{armed:true})).status===403,'acceptance mode still forbids all transfer activation');
const alias='abd7ea4d-7246-4df4-adb4-81e2be37cfde';await call('/api/connections/destination:'+alias,'PUT',{baseUrl:'https://fixture-api.invalid',apiVersion:'v6',username:'fixture-api-user',password:'fixture-only-password'});
check((await call('/api/mappings','POST',{...mapping,destinationConnectionId:alias})).status===400,'duplicate destination object rejected across same-origin connection aliases');
await call('/api/connections/recipients/'+alias,'DELETE');
check((await call('/api/connections/json/'+sourceId,'PUT',{...source,outputUnit:'wrong-units'})).status===400,'unconfirmed unit relabeling rejected');
check((await call('/api/connections/json/'+sourceId,'PUT',{...source,url:'https://different-fixture.invalid/data'})).status===400,'saved key never reused across an origin change');
check((await call('/api/connections/json/'+sourceId+'/credentials','DELETE')).status===200,'external API key clear');
state=(await call('/api/state')).json;check(!state.externalSources[sourceId].hasSecret&&!state.armed,'API key clear removes stored key and keeps writes paused');
check((await call('/api/connections/json/'+sourceId+'/test','POST',{})).status===400,'cleared required key prevents source request');
source.secret=key;await call('/api/connections/json/'+sourceId,'PUT',source);
await call('/api/connections/extensions','PUT',{enabled:false});check((await call('/api/mappings/validate','POST',mapping)).status===400,'disabled extension blocks JSON validation requests');
await call('/api/connections/extensions','PUT',{enabled:true});
const stats=(await call('/api/fixture/status')).json;check(!stats.bridgeWorkerHosted&&stats.pointWrites===0,'no transfer worker or point-write request exists in configuration fixture');
check(!fs.readFileSync(data+'/config.json','utf8').includes(key),'saved API secret is encrypted on disk');
check(fs.existsSync(data+'/config.json.v1-before-extension'),'explicit v1 backup exists');
console.log(`${passed} extension HTTP assertions passed; fake-only adapters, zero point writes.`);
