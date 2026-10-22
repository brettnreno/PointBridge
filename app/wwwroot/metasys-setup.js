'use strict';
const loginSettingsPath = '/api/auth/metasys/settings';
const localRecovery = new URLSearchParams(location.search).has('local');
let setupShown = false;
document.getElementById('setup-code-label').insertAdjacentHTML('afterend', '<label id="metasys-first-server" hidden>Metasys server HTTPS address<input id="metasys-bootstrap-server" type="url" placeholder="https://this-server.example.com"><small>Use the trusted HTTPS address of Metasys on this computer.</small></label>');
document.getElementById('auth-form').insertAdjacentHTML('afterend', '<p><a href="/?local=1" id="local-recovery-link">Local recovery sign-in</a></p>');
document.getElementById('users-page').insertAdjacentHTML('afterbegin', '<div class="panel"><h2>Metasys sign-in</h2><p>Use the Metasys server installed on this computer. All valid Metasys users receive Point Bridge administrator access.</p><button id="open-metasys-setup" class="primary">Configure Metasys sign-in</button></div>');
document.body.insertAdjacentHTML('beforeend', `<dialog id="metasys-setup-dialog"><div class="dialog-heading"><h2>Set up Metasys sign-in</h2><button id="close-metasys-setup" type="button" class="secondary">Close</button></div>
<p>1. Save the local Metasys server address. Changing servers disables sign-in until verified.</p>
<form id="metasys-server-form"><label>Metasys server HTTPS address<input id="metasys-server" type="url" required placeholder="https://this-server.example.com"></label><button class="secondary">Save server</button></form>
<p>2. Verify a Metasys login. Its password is used once and never saved.</p>
<form id="metasys-verify-form"><label>Metasys username<input id="metasys-verify-user" required autocomplete="username"></label><label>Metasys password<input id="metasys-verify-password" type="password" required autocomplete="off"></label><button class="secondary">Verify login</button></form>
<p>3. Enable sign-in for all valid users on this Metasys server. Each receives administrator access.</p><button id="enable-metasys-login" class="primary">Enable Metasys sign-in</button> <button id="disable-metasys-login" class="secondary">Disable Metasys sign-in</button><p id="metasys-setup-status" role="status"></p></dialog>`);
const setupElement = id => document.getElementById(id);
function updateSetupScreen() {
    if (!sessionState) return;
    const firstRun = sessionState.setupRequired && !localRecovery;
    setupElement('metasys-first-server').hidden = !firstRun;
    setupElement('metasys-bootstrap-server').required = firstRun;
    if (!sessionState.authenticated && (firstRun || localRecovery)) {
        setupElement('auth-password').minLength = localRecovery && sessionState.setupRequired ? 12 : 1;
        setupElement('auth-title').textContent = firstRun ? 'Connect Point Bridge to Metasys' : 'Local recovery sign-in';
        setupElement('auth-description').textContent = firstRun ? 'Enter the installation setup code and verify your Metasys login. This enables sign-in for all valid users on this server.' : 'Use your local Point Bridge recovery account.';
        setupElement('auth-submit').textContent = firstRun ? 'Verify Metasys and complete setup' : (sessionState.setupRequired ? 'Create recovery administrator' : 'Sign in');
    }
    if (sessionState.authenticated && !sessionState.metasysLoginAvailable && !setupShown) {
        setupShown = true;
        openMetasysSetup();
    }
}
async function openMetasysSetup() {
    try {
        const settings = await api(loginSettingsPath);
        setupElement('metasys-server').value = settings.serverOrigin || '';
        setupElement('metasys-setup-status').textContent = settings.enabled ? 'Metasys sign-in is enabled.' : 'Metasys sign-in is not enabled. Complete these steps to use Metasys accounts.';
        setupElement('metasys-setup-dialog').showModal();
    } catch (error) { notice(error.message); }
}
async function setupOperation(button, action) {
    button.disabled = true;
    try { await action(); } catch (error) { setupElement('metasys-setup-status').textContent = error.message; }
    finally { button.disabled = false; }
}
setupElement('open-metasys-setup').addEventListener('click', openMetasysSetup);
setupElement('close-metasys-setup').addEventListener('click', () => { setupElement('metasys-verify-password').value = ''; setupElement('metasys-setup-dialog').close(); });
setupElement('metasys-server-form').addEventListener('submit', event => {
    event.preventDefault();
    setupOperation(event.submitter, async () => {
        await api(loginSettingsPath, 'PUT', {serverOrigin: setupElement('metasys-server').value.trim(), enabled: false, allowAnyAuthenticatedUser: true});
        setupElement('metasys-setup-status').textContent = 'Server saved. Verify a Metasys login next.';
    });
});
setupElement('metasys-verify-form').addEventListener('submit', event => {
    event.preventDefault();
    const password = setupElement('metasys-verify-password').value;
    setupElement('metasys-verify-password').value = '';
    setupOperation(event.submitter, async () => {
        await api(loginSettingsPath + '/users', 'POST', {serverOrigin: setupElement('metasys-server').value.trim(), username: setupElement('metasys-verify-user').value.trim(), password});
        setupElement('metasys-setup-status').textContent = 'Metasys login verified. Enable sign-in to finish.';
    });
});
for (const enabled of [true, false]) setupElement(enabled ? 'enable-metasys-login' : 'disable-metasys-login').addEventListener('click', event => setupOperation(event.target, async () => {
    await api(loginSettingsPath, 'PUT', {serverOrigin: setupElement('metasys-server').value.trim(), enabled, allowAnyAuthenticatedUser: true});
    setupElement('metasys-setup-status').textContent = enabled ? 'Enabled. Sign out, then use your Metasys username and password.' : 'Metasys sign-in disabled. Local recovery accounts remain available.';
}));
new MutationObserver(updateSetupScreen).observe(setupElement('auth-screen'), {attributes: true, attributeFilter: ['hidden']});
new MutationObserver(updateSetupScreen).observe(setupElement('app-shell'), {attributes: true, attributeFilter: ['hidden']});
updateSetupScreen();
