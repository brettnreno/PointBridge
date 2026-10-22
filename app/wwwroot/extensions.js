'use strict';
const diagnosticButton=document.createElement('button');diagnosticButton.className='secondary';diagnosticButton.textContent='Download diagnostics';
document.getElementById('sign-out').before(diagnosticButton);
diagnosticButton.addEventListener('click',()=>busy(diagnosticButton,async()=>{
  const response=await fetch('/api/diagnostics/export',{credentials:'same-origin',cache:'no-store'});
  if(!response.ok)throw new Error('Diagnostic download failed. Sign in and try again.');
  const blob=await response.blob();if(blob.size>512*1024)throw new Error('Diagnostic download exceeded its size limit.');
  const url=URL.createObjectURL(blob),link=document.createElement('a');link.href=url;link.download='PointBridge-diagnostics.json';link.click();setTimeout(()=>URL.revokeObjectURL(url),10000);
}));
// Optional extended configuration. Existing single-source mappings and connection forms stay intact.
const externalDrafts={},recipientDrafts={};
const externalDefaults={name:'',enabled:false,url:'',valuePointer:'',timestampPointer:'',unitPointer:'',inputUnit:'',outputUnit:'',unitAliasConfirmed:false,valueType:'number',pollSeconds:300,maxAgeSeconds:0,timeoutSeconds:10,userAgent:'',authMode:'none',authHeader:'',qualityPointer:'',reliableQualityValue:''};
const originalRenderConnections=renderConnections;
renderConnections=function(){
 originalRenderConnections();
 const container=$('connection-forms');
 for(const [id,c] of Object.entries({...state.destinations,...recipientDrafts})){
  const form=container.querySelector('[data-connection="destination"]').cloneNode(true);
  form.dataset.connection='destination:'+id;form.querySelector('h2').textContent='Additional destination';
  for(const name of ['baseUrl','username','apiVersion'])form.elements[name].value=c[name]||({apiVersion:'v5'}[name]||'');
  form.elements.password.value='';form.elements.password.placeholder=c.hasPassword?'Saved securely - leave blank to keep':'Enter account password';form.elements.password.closest('label').querySelector('.form-note').textContent=c.hasPassword?'A password is already saved. Leave blank to keep it.':'Enter this recipient account password.';
  form.querySelector('.test-result').textContent='';
  form.addEventListener('submit',async e=>{e.preventDefault();await saveConnection(form,false);if(state.destinations?.[id])delete recipientDrafts[id];});
  form.querySelector('.test-connection').addEventListener('click',()=>{if(form.reportValidity())saveConnection(form,true);});
  form.querySelector('.clear-credentials').addEventListener('click',()=>clearCredentials(form));
  const remove=document.createElement('button');remove.type='button';remove.className='secondary';remove.textContent='Remove recipient';
  remove.addEventListener('click',()=>busy(remove,async()=>{if(state.destinations?.[id])await api('/api/connections/recipients/'+id,'DELETE');delete recipientDrafts[id];await refresh();}));
  form.querySelector('.form-actions').append(remove);container.append(form);
 }
 let panel=$('extended-connections');if(!panel){panel=document.createElement('div');panel.id='extended-connections';$('connections-page').append(panel);}
 panel.innerHTML=`<div class="panel"><h2>External sources and additional recipients</h2><p>Configure sources and recipients below, then explicitly enable this feature. Saving pauses transfers. New sources start disabled.</p><label class="check-label"><input id="extensions-enabled" type="checkbox" ${state.extensionsEnabled?'checked':''}> Allow external JSON sources and additional recipients</label><div class="form-actions"><button type="button" class="secondary" id="add-recipient">Add Metasys recipient</button><button type="button" class="secondary" id="add-json-source">Add JSON API source</button></div><div id="extensions-result" role="status"></div></div><div id="external-source-forms"></div>`;
 $('extensions-enabled').addEventListener('change',async e=>{try{await api('/api/connections/extensions','PUT',{enabled:e.target.checked});await refresh();}catch(error){e.target.checked=!e.target.checked;$('extensions-result').textContent=error.message;}});
 $('add-recipient').addEventListener('click',()=>{recipientDrafts[crypto.randomUUID()]={};renderConnections();});
 $('add-json-source').addEventListener('click',()=>{externalDrafts[crypto.randomUUID()]={...externalDefaults};renderJsonForms();});
 renderJsonForms();
};
function jsonField(c,name,label,type='text',placeholder=''){return `<label>${esc(label)}<input name="${name}" type="${type}" value="${esc(c[name]??'')}" placeholder="${esc(placeholder)}" ${['name','url','valuePointer','timestampPointer','unitPointer','inputUnit','outputUnit'].includes(name)?'required':''}></label>`;}
function renderJsonForms(){
 $('external-source-forms').innerHTML=Object.entries({...state.externalSources,...externalDrafts}).map(([id,saved])=>{
  const c={...externalDefaults,...saved};return `<form class="panel json-source-card" data-json-source="${esc(id)}"><h2>JSON API source</h2><label class="check-label"><input name="enabled" type="checkbox" ${c.enabled?'checked':''}> Enable this source after review</label>${jsonField(c,'name','Source name')}${jsonField(c,'url','HTTPS data URL','url','https://api.example.com/data')}<div class="point-grid">${jsonField(c,'valuePointer','Value path (JSON Pointer)','text','/properties/temperature/value')}${jsonField(c,'timestampPointer','Observation timestamp path','text','/properties/timestamp')}${jsonField(c,'unitPointer','Units path','text','/properties/temperature/unitCode')}<label>Value type<select name="valueType">${['number','integer','boolean','string'].map(t=>`<option ${t===c.valueType?'selected':''}>${t}</option>`).join('')}</select></label>${jsonField(c,'inputUnit','Exact unit reported by API')}${jsonField(c,'outputUnit','Exact Metasys unit label')}</div><label class="check-label"><input type="checkbox" name="unitAliasConfirmed" ${c.unitAliasConfirmed?'checked':''}> I confirmed different unit labels represent the same scale. No unit conversion is performed.</label><div class="point-grid">${jsonField(c,'pollSeconds','Polling interval (seconds)','number')}${jsonField(c,'maxAgeSeconds','Maximum observation age (seconds)','number')}${jsonField(c,'timeoutSeconds','Request timeout (seconds)','number')}</div><p class="form-note">An HTTP success does not make old data current. Missing, null, stale or mismatched values are rejected.</p><details><summary>API identification, authentication and quality</summary>${jsonField(c,'userAgent','User-Agent (required by weather.gov)')}<label>Authentication<select name="authMode">${['none','header','bearer'].map(t=>`<option ${t===c.authMode?'selected':''}>${t}</option>`).join('')}</select></label>${jsonField(c,'authHeader','API-key header name')}<label>API secret<input name="secret" type="password" autocomplete="new-password" placeholder="${c.hasSecret?'Saved securely - blank keeps it':'Optional API key or bearer token'}"></label><p class="form-note">Secrets are encrypted locally. Do not put keys or passwords in URLs.</p>${jsonField(c,'qualityPointer','Optional quality path')}${jsonField(c,'reliableQualityValue','Exact reliable quality value')}<p class="form-note">Without a quality field, sensor quality remains unreported in health diagnostics.</p></details><div class="form-actions"><button type="submit" class="primary">Save source</button><button type="button" class="secondary json-test">Test saved source</button><button type="button" class="secondary json-clear">Clear saved API secret</button><button type="button" class="secondary json-remove">Remove source</button></div><div class="test-result" role="status"></div></form>`;
 }).join('');
 for(const form of document.querySelectorAll('[data-json-source]')){
  const id=form.dataset.jsonSource,result=form.querySelector('.test-result');
  form.addEventListener('submit',async e=>{e.preventDefault();const body=Object.fromEntries(new FormData(form));for(const key of ['enabled','unitAliasConfirmed'])body[key]=form.elements[key].checked;for(const key of ['pollSeconds','maxAgeSeconds','timeoutSeconds'])body[key]=Number(body[key]);try{await api('/api/connections/json/'+id,'PUT',body);form.elements.secret.value='';delete externalDrafts[id];state=await api('/api/state');render(false);result.textContent='Saved. Transfers are paused.';}catch(error){result.textContent=error.message;}});
  form.querySelector('.json-test').addEventListener('click',e=>busy(e.target,async()=>{const sample=await api('/api/connections/json/'+id+'/test','POST',{});result.textContent=`Read succeeded: ${printable(sample.value)} ${sample.units}; observed ${new Date(sample.observedAt).toLocaleString()}. No destination write occurred.`;}));
  form.querySelector('.json-clear').addEventListener('click',e=>busy(e.target,async()=>{await api('/api/connections/json/'+id+'/credentials','DELETE');form.elements.secret.value='';result.textContent='Saved secret cleared. Transfers are paused.';}));
  form.querySelector('.json-remove').addEventListener('click',e=>busy(e.target,async()=>{if(state.externalSources?.[id])await api('/api/connections/json/'+id,'DELETE');delete externalDrafts[id];await refresh();}));
 }
}
const originalOpenMapping=openMapping;
openMapping=function(mapping){
 originalOpenMapping(mapping);
 let controls=$('mapping-source-controls');if(!controls){controls=document.createElement('div');controls.id='mapping-source-controls';$('source-object').closest('.point-card').prepend(controls);}
 controls.innerHTML=`<label>Source type<select id="mapping-source-kind"><option value="metasys">Metasys point</option><option value="json" ${!state.extensionsEnabled?'disabled':''}>External JSON API</option></select></label><label id="mapping-json-label">API source<select id="mapping-json-source"><option value="">Choose a configured source</option>${Object.entries(state.externalSources||{}).map(([id,s])=>`<option value="${esc(id)}">${esc(s.name||id)}${s.enabled?'':' (disabled)'}</option>`).join('')}</select></label>`;
 let target=$('mapping-destination-connection');if(!target){const label=document.createElement('label');label.textContent='Recipient server';target=document.createElement('select');target.id='mapping-destination-connection';target.required=true;label.append(target);$('destination-object').closest('.point-card').prepend(label);}
 target.innerHTML=`<option value="">Choose a recipient</option><option value="destination">Default destination (${esc(state.destination?.baseUrl||'not configured')})</option>${Object.entries(state.destinations||{}).map(([id,c])=>`<option value="${esc(id)}" ${!state.extensionsEnabled?'disabled':''}>${esc(c.baseUrl||id)}</option>`).join('')}`;
 target.value=mapping?.destinationConnectionId??'destination';$('mapping-source-kind').value=mapping?.sourceKind||'metasys';$('mapping-json-source').value=mapping?.externalSourceId||'';
 function sourceMode(){const json=$('mapping-source-kind').value==='json';$('mapping-json-label').hidden=!json;$('mapping-json-source').required=json;$('source-object').closest('label').hidden=json;document.querySelector('.browse[data-side="source"]').hidden=json;if(json)$('source-object').value='json:'+$('mapping-json-source').value;else if($('source-object').value.startsWith('json:'))$('source-object').value='';}
 $('mapping-source-kind').addEventListener('change',sourceMode);$('mapping-json-source').addEventListener('change',sourceMode);sourceMode();
};
const originalMappingBody=mappingBody;
mappingBody=function(){return {...originalMappingBody(),sourceKind:$('mapping-source-kind')?.value||'metasys',externalSourceId:$('mapping-json-source')?.value||'',destinationConnectionId:$('mapping-destination-connection')?.value||'destination'};};
const originalRender=render;
render=function(updateConnections=true){originalRender(updateConnections);
 const jsonMappings=(state.mappings||[]).filter(m=>m.sourceKind==='json');
 if(jsonMappings.length){$('source-summary').textContent=state.source?.baseUrl?'Metasys and JSON sources':'External JSON sources';$('source-version').textContent=`${new Set(jsonMappings.map(m=>m.externalSourceId)).size} JSON source(s) mapped`;}
 if(Object.keys(state.destinations||{}).length){$('destination-summary').textContent=`${Object.keys(state.destinations).length+(state.destination?.baseUrl?1:0)} configured recipients`;$('destination-version').textContent='Each mapping selects its recipient server';}
 for(const row of document.querySelectorAll('#mapping-rows tr')){const id=row.querySelector('[data-edit]')?.dataset.edit;if(!id)continue;const m=state.mappings.find(m=>m.id===id);const runtimeHealth=(state.health||[]).find(h=>h.mappingId===id);if(runtimeHealth?.failureCode==='source-stale-observation'){const status=row.querySelector('td:first-child small');if(status)status.textContent='Source observation expired — destination value may be held';}const source=state.externalSources?.[m.externalSourceId];if(m.sourceKind==='json'){row.querySelector('td:nth-child(2) .reference').textContent=source?.name||m.externalSourceId;row.querySelector('td:nth-child(2) small').textContent=source?.valuePointer||'JSON API';}const recipient=m.destinationConnectionId&&m.destinationConnectionId!=='destination'?state.destinations?.[m.destinationConnectionId]:state.destination;const recipientLabel=document.createElement('small');recipientLabel.textContent=recipient?.baseUrl||'Recipient not configured';row.querySelector('td:nth-child(3)').append(recipientLabel);const button=document.createElement('button');button.type='button';button.textContent='Add recipient';button.dataset.fanout=id;row.querySelector('.row-actions').append(button);}};
$('mapping-rows').addEventListener('click',e=>{const button=e.target.closest('[data-fanout]');if(button){const m=state.mappings.find(m=>m.id===button.dataset.fanout);openMapping({...m,id:null,name:(m.name||'Mapping')+' additional recipient',destinationConnectionId:'',destinationObject:'',enabled:false});}});
if(state)render();
