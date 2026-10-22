// Isolated app fixture. No request can leave this fixed loopback origin.
const origin='http://127.0.0.1:15088';
let passed=0;
function check(value,label){if(!value)throw new Error(label);passed++;console.log('PASS '+label);}
const session=await fetch(origin+'/api/session');
const state=await session.json();
check(session.status===200,'isolated session endpoint');
check(state.metasysLoginAvailable===false,'Metasys provider disabled by default');
const invalid=await fetch(origin+'/api/auth/metasys/login',{method:'POST',headers:{'Content-Type':'application/json','X-CSRF-Token':'invalid',Origin:origin},body:JSON.stringify({username:'fixture-user',password:'fixture-only'})});
check(invalid.status===403,'Metasys login retains CSRF enforcement');
const disabled=await fetch(origin+'/api/auth/metasys/login',{method:'POST',headers:{'Content-Type':'application/json','X-CSRF-Token':state.csrfToken,Origin:origin},body:JSON.stringify({username:'fixture-user',password:'fixture-only'})});
check(disabled.status===404,'disabled provider cannot authenticate');
const foreign=await fetch(origin+'/api/auth/metasys/login',{method:'POST',headers:{'Content-Type':'application/json','X-CSRF-Token':state.csrfToken,Origin:'https://untrusted.invalid'},body:JSON.stringify({username:'fixture-user',password:'fixture-only'})});
check(foreign.status===403,'Metasys login retains origin enforcement');
const userList=await fetch(origin+'/api/auth/users');
check(userList.status===401,'disabled external login grants no protected access');
console.log(`${passed} disabled-provider HTTP scenarios passed.`);
