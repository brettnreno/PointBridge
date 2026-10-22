'use strict';
const explorerStyles = document.createElement('link');
explorerStyles.rel = 'stylesheet'; explorerStyles.href = '/explorer.css?v=1.2.3'; document.head.append(explorerStyles);
let browseTrail = [], browseItems = [], browseGeneration = 0, browsePage = 1, browseNext = null;
const browseValues = new Map();
const browseRoot = document.createElement('label');
browseRoot.innerHTML = 'Browse by<select id="browse-kind"><option value="objects">Object hierarchy</option><option value="networkDevices">Network devices</option><option value="equipment">Equipment</option><option value="spaces">Spaces</option></select>';
$('browse-path').before(browseRoot);
const browseTools = document.createElement('div');
browseTools.className = 'explorer-tools';
browseTools.innerHTML = '<button type="button" class="secondary" id="browse-values">Read visible values</button><button type="button" class="secondary" id="browse-previous">Previous page</button><span id="browse-page"></span><button type="button" class="secondary" id="browse-next">Next page</button><span id="browse-status" role="status"></span>';
$('browse-results').before(browseTools);
function explorerSide() {
    const recipient = $('mapping-destination-connection')?.value;
    return browseSide === 'destination' && recipient && recipient !== 'destination' ? 'destination:' + recipient : browseSide;
}
function visibleBrowseItems() {
    const query = $('browse-search').value.trim().toLowerCase();
    return browseItems.filter(item => [item.name, item.reference, item.id].join(' ').toLowerCase().includes(query));
}
function renderBrowse() {
    $('browse-path').innerHTML = '<button type="button" class="secondary" data-depth="-1">Root</button>'
        + browseTrail.map((entry, index) => `<span>›</span><button type="button" class="path-button" data-depth="${index}">${esc(entry.name)}</button>`).join('');
    const matches = visibleBrowseItems();
    $('browse-results').innerHTML = matches.length ? matches.map(item => {
        const value = browseValues.get(item.id);
        const quality = value ? value.success ? `Value: ${printable(value.value)} · Quality: ${value.reliability || 'Unknown'} · Priority: ${value.priority || 'Not reported'}` : `Unavailable (HTTP ${value.status})` : '';
        return `<div class="discovery-row"><div class="discovery-name"><strong>${esc(item.name)}</strong><small>${esc(item.reference)}</small><small>${esc(quality)}</small></div><div class="discovery-actions">`
            + item.children.map(branch => `<button type="button" class="secondary" data-branch="${esc(branch.kind)}" data-id="${esc(item.id)}">${esc(branch.label)} →</button>`).join('')
            + (item.selectable ? `<button type="button" class="secondary" data-inspect="${esc(item.id)}">Inspect</button><button type="button" class="primary" data-point="${esc(item.id)}">Choose point</button>` : '') + '</div></div>';
    }).join('') : '<p class="muted">No matching objects in this page or branch. Try another branch or paste an exact point reference.</p>';
    $('browse-values').disabled = !matches.some(item => item.selectable);
    $('browse-previous').disabled = browsePage <= 1;
    $('browse-next').disabled = browseNext == null;
    $('browse-page').textContent = `Page ${browsePage}`;
}
async function loadBrowse() {
    const generation = ++browseGeneration;
    browseItems = []; browseValues.clear(); browseNext = null;
    renderBrowse();
    $('browse-results').textContent = 'Loading from Metasys…';
    $('browse-status').textContent = '';
    const branch = browseTrail.at(-1);
    const query = new URLSearchParams({kind: branch?.kind || $('browse-kind').value, page: browsePage});
    if (branch) query.set('parentId', branch.id);
    try {
        const result = await api(`/api/metasys/${encodeURIComponent(explorerSide())}/browse?${query}`);
        if (generation !== browseGeneration || !$('browse-dialog').open) return;
        browseItems = result.items; browseNext = result.nextPage;
        renderBrowse();
        $('browse-status').textContent = result.truncated ? 'Branch limited to 500 objects. Use a narrower branch or an exact reference.' : '';
    } catch (error) {
        if (generation !== browseGeneration) return;
        $('browse-results').textContent = error.message + ' You can still paste an exact point reference.';
    }
}
document.querySelectorAll('.browse').forEach(button => button.addEventListener('click', () => {
    browseSide = button.dataset.side; browseTrail = []; browsePage = 1; $('browse-search').value = '';
    $('browse-title').textContent = `Browse ${browseSide} points`;
    $('browse-dialog').showModal(); loadBrowse();
}));
$('browse-dialog').addEventListener('close', () => { browseGeneration++; });
$('close-browse').addEventListener('click', () => $('browse-dialog').close());
$('browse-kind').addEventListener('change', () => { browseTrail = []; browsePage = 1; $('browse-search').value = ''; loadBrowse(); });
$('browse-form').addEventListener('submit', event => { event.preventDefault(); renderBrowse(); });
$('browse-search').addEventListener('input', renderBrowse);
$('browse-path').addEventListener('click', event => {
    const button = event.target.closest('[data-depth]');
    if (button) { browseTrail = browseTrail.slice(0, Number(button.dataset.depth) + 1); browsePage = 1; $('browse-search').value = ''; loadBrowse(); }
});
$('browse-next').addEventListener('click', () => { if (browseNext) { browsePage = browseNext; loadBrowse(); } });
$('browse-previous').addEventListener('click', () => { if (browsePage > 1) { browsePage--; loadBrowse(); } });
$('browse-results').addEventListener('click', event => {
    const point = event.target.closest('[data-point]'), branch = event.target.closest('[data-branch]'), inspect = event.target.closest('[data-inspect]');
    if (point) { $(browseSide + '-object').value = point.dataset.point; $('browse-dialog').close(); }
    if (branch) {
        const item = browseItems.find(candidate => candidate.id === branch.dataset.id);
        browseTrail.push({id: item.id, name: item.name, kind: branch.dataset.branch});
        browsePage = 1; $('browse-search').value = ''; loadBrowse();
    }
    if (inspect) openInspection(explorerSide(), inspect.dataset.inspect);
});
$('browse-values').addEventListener('click', async () => {
    const generation = browseGeneration;
    const identifiers = [...new Set(visibleBrowseItems().filter(item => item.selectable).map(item => item.id))].slice(0, 50);
    $('browse-values').disabled = true;
    try {
        const result = await api(`/api/metasys/${encodeURIComponent(explorerSide())}/values`, 'POST', {objectIds: identifiers});
        if (generation !== browseGeneration) return;
        result.items.forEach(item => browseValues.set(item.id, item)); renderBrowse();
        $('browse-status').textContent = `Read ${result.items.length} points (maximum 50). These are communication snapshots, not proof of physical observation freshness.`;
    } catch (error) { if (generation === browseGeneration) $('browse-status').textContent = error.message; }
    finally { if (generation === browseGeneration) renderBrowse(); }
});

const inspector = document.createElement('dialog');
inspector.id = 'inspect-dialog';
inspector.innerHTML = '<div class="dialog-heading"><h2>Metasys point inspector</h2><button type="button" class="icon-button" id="inspect-close" aria-label="Close inspector">×</button></div><p class="muted">Read-only: no writes, alarm acknowledgement, discard or annotation. Audit usernames, free text and old/new values are omitted. Historical data does not establish current field health.</p><form id="inspect-form"><label>Connection<select id="inspect-side"></select></label><label>Point GUID or reference<input id="inspect-object" required maxlength="2048"></label><div class="point-grid"><label>View<select id="inspect-view"><option value="summary">Current value, quality, priority and commands</option><option value="trends">Trend samples</option><option value="alarms">Alarms</option><option value="audits">Audits</option></select></label><label>Time range<select id="inspect-hours"><option value="1">Last hour</option><option value="24" selected>Last 24 hours</option><option value="168">Last 7 days</option></select></label></div><button class="primary" id="inspect-load">Read from Metasys</button></form><p id="inspect-status" role="status"></p><div id="inspect-results" class="table-scroll"></div><div class="explorer-tools"><button class="secondary" id="inspect-previous" type="button" disabled>Previous</button><button class="secondary" id="inspect-next" type="button" disabled>Next</button></div>';
document.body.append(inspector);
let inspectionGeneration = 0, inspectionPages = [null], inspectionPage = 0, inspectionNext = null, inspectionRange = null;
const inspectButton = document.createElement('button');
inspectButton.type = 'button'; inspectButton.className = 'secondary admin-only'; inspectButton.textContent = 'Inspect Metasys point';
inspectButton.addEventListener('click', () => openInspection('destination', ''));
$('activity-page').querySelector('.panel-heading').append(inspectButton);
function openInspection(side, object) {
    const connections = [['source', state?.source], ['destination', state?.destination], ...Object.entries(state?.destinations || {}).map(([identifier, connection]) => ['destination:' + identifier, connection])];
    $('inspect-side').innerHTML = connections.filter(([, connection]) => connection?.baseUrl).map(([identifier, connection]) => `<option value="${esc(identifier)}">${esc(identifier)} · ${esc(connection.baseUrl)} · ${esc(connection.apiVersion)}</option>`).join('');
    $('inspect-side').value = [...$('inspect-side').options].some(option => option.value === side) ? side : $('inspect-side').options[0]?.value || '';
    $('inspect-object').value = object; $('inspect-view').value = 'summary';
    resetInspection(); $('inspect-results').textContent = ''; $('inspect-status').textContent = '';
    inspector.showModal(); if (object) loadInspection();
}
function resetInspection() {
    inspectionGeneration++; inspectionPages = [null]; inspectionPage = 0; inspectionNext = null; inspectionRange = null;
    $('inspect-load').disabled = false;
    $('inspect-previous').disabled = true; $('inspect-next').disabled = true;
}
function renderInspection(result, view) {
    if (view === 'summary') {
        const fields = [['Object', result.id], ['Name', result.name], ['Value', printable(result.value)], ['Type', result.type], ['Units', result.units || 'Unknown'], ['Quality', result.reliability || 'Unknown'], ['Command priority', result.priority || 'Not reported']];
        $('inspect-results').innerHTML = '<dl class="inspection-summary">' + fields.map(([label, value]) => `<dt>${esc(label)}</dt><dd>${esc(value)}</dd>`).join('') + '</dl><h3>Discovered commands (not executed)</h3>'
            + (result.commandsWarning ? `<p>${esc(result.commandsWarning)}</p>` : result.commands.length ? result.commands.map(command => `<p>${esc(command.title || command.id)} · ${command.supported ? 'Supported scalar adjustment' : esc(command.reason)} · ${esc(command.priorities.join(', '))}</p>`).join('') : '<p>No commands returned.</p>');
        return;
    }
    const columns = view === 'trends' ? ['timestamp', 'value', 'units', 'reliability'] : ['timestamp', 'objectName', 'type', 'status', 'priority'];
    $('inspect-results').innerHTML = result.items.length ? '<table><thead><tr>' + columns.map(column => `<th>${esc(column)}</th>`).join('') + '</tr></thead><tbody>'
        + result.items.map(item => '<tr>' + columns.map(column => `<td>${esc(printable(item[column]))}</td>`).join('') + '</tr>').join('') + '</tbody></table>' : '<p>No records returned in this time range.</p>';
}
async function loadInspection() {
    const generation = ++inspectionGeneration;
    const view = $('inspect-view').value;
    if (!inspectionRange) { const end = new Date(); inspectionRange = {end: end.toISOString(), start: new Date(end.getTime() - Number($('inspect-hours').value) * 3600000).toISOString()}; }
    const query = new URLSearchParams({object: $('inspect-object').value.trim(), view, ...inspectionRange, page: inspectionPage + 1});
    if (inspectionPages[inspectionPage]) query.set('continuation', inspectionPages[inspectionPage]);
    $('inspect-load').disabled = true; $('inspect-next').disabled = true; $('inspect-previous').disabled = true;
    $('inspect-results').textContent = ''; $('inspect-status').textContent = 'Reading from Metasys…';
    try {
        const result = await api(`/api/metasys/${encodeURIComponent($('inspect-side').value)}/inspect?${query}`);
        if (generation !== inspectionGeneration || !inspector.open) return;
        renderInspection(result, view); inspectionNext = result.nextPage || result.continuationToken || null;
        $('inspect-status').textContent = result.truncated ? 'Showing the first 50 records. Narrow the time range to inspect more.' : 'Read completed. No Metasys changes were made.';
        $('inspect-next').disabled = !inspectionNext; $('inspect-previous').disabled = inspectionPage === 0;
    } catch (error) { if (generation === inspectionGeneration) { inspectionNext = null; $('inspect-status').textContent = error.message; } }
    finally { if (generation === inspectionGeneration) $('inspect-load').disabled = false; }
}
$('inspect-form').addEventListener('submit', event => { event.preventDefault(); resetInspection(); loadInspection(); });
for (const identifier of ['inspect-side', 'inspect-object', 'inspect-view', 'inspect-hours']) $(identifier).addEventListener('change', () => { resetInspection(); $('inspect-results').textContent = ''; $('inspect-load').disabled = false; });
$('inspect-close').addEventListener('click', () => inspector.close());
inspector.addEventListener('close', resetInspection);
$('inspect-next').addEventListener('click', () => {
    if (inspectionNext) { inspectionPages[++inspectionPage] = typeof inspectionNext === 'string' ? inspectionNext : null; loadInspection(); }
});
$('inspect-previous').addEventListener('click', () => { if (inspectionPage > 0) { inspectionPage--; loadInspection(); } });
