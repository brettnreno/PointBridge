import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import vm from 'node:vm';

const source = readFileSync(new URL('../app/wwwroot/theme.js', import.meta.url), 'utf8');
function fixture(saved, dark = false, blocked = false, menuCollapsed = false) {
  const events = {};
  const storage = { 'pointbridge-theme': saved, 'pointbridge-menu-collapsed': String(menuCollapsed) };
  function element(tag) {
    return { tag, dataset: {}, children: [], attributes: {}, hidden: false,
      append(...children) { this.children.push(...children); },
      prepend(child) { this.children.unshift(child); },
      setAttribute(name, value) { this.attributes[name] = value; },
      addEventListener(name, callback) { this[name] = callback; }
    };
  }
  const sidebar = element('aside');
  const main = element('main');
  const foot = element('footer');
  const shell = { hidden: true };
  const root = { dataset: {}, style: {} };
  const media = { matches: dark, addEventListener: (name, callback) => { events.media = callback; } };
  const choices = () => foot.children.flatMap(selector => selector.children.filter(child => child.tag === 'button'));
  const document = {
    documentElement: root,
    getElementById: id => id === 'app-shell' ? shell : null,
    addEventListener: (name, callback) => { events[name] = callback; },
    querySelector: selector => selector === 'aside' ? sidebar : selector === 'main' ? main : null,
    querySelectorAll: selector => selector === '[data-theme-choice]' ? choices() : selector === '.sidebar-foot' ? [foot] : [],
    createElement: element
  };
  vm.runInNewContext(source, {
    document,
    MutationObserver: class { constructor(callback) { events.session = callback; } observe(target, options) { assert.equal(target, shell); assert.equal(options.attributeFilter[0], 'hidden'); } },
    window: { matchMedia: () => media, addEventListener: (name, callback) => { events[name] = callback; } },
    localStorage: { getItem: key => { if (blocked) throw Error('blocked'); return storage[key]; }, setItem: (key, value) => { if (blocked) throw Error('blocked'); storage[key] = value; } }
  });
  assert.equal(root.dataset.theme, 'dark', 'Login must be dark before the DOM loads');
  events.DOMContentLoaded();
  assert.equal(root.dataset.theme, 'dark', 'Login ignores saved application theme');
  return { root, events, media, storage, choices: choices(), shell, sidebar, toggle: main.children[0].children[0], login(value) { shell.hidden = !value; events.session(); } };
}

for (const mode of ['light', 'dark', 'boomer', 'system']) {
  const page = fixture(mode);
  assert.equal(page.choices.length, 4, 'Only the signed-in sidebar gets theme controls');
  page.login(true);
  assert.equal(page.root.dataset.theme, mode === 'system' ? 'light' : mode);
  assert.equal(page.choices.filter(button => button.attributes['aria-pressed'] === 'true').length, 1);
  page.login(false);
  assert.equal(page.root.dataset.theme, 'dark', 'Signing out restores fixed login appearance');
  page.login(true);
  assert.equal(page.root.dataset.theme, mode === 'system' ? 'light' : mode);
}
const page = fixture(null);
page.login(true);
assert.equal(page.root.dataset.theme, 'dark', 'New installations default to dark, not OS appearance');
page.choices.find(button => button.dataset.themeChoice === 'system').click();
assert.equal(page.root.dataset.theme, 'light');
page.media.matches = true;
page.events.media();
assert.equal(page.root.dataset.theme, 'dark');
page.choices.find(button => button.dataset.themeChoice === 'boomer').click();
assert.equal(page.storage['pointbridge-theme'], 'boomer');
page.login(false);
page.events.storage({ key: 'pointbridge-theme', newValue: 'light' });
assert.equal(page.root.dataset.theme, 'dark');
page.login(true);
assert.equal(page.root.dataset.theme, 'light');
page.events.storage({ key: null, newValue: null });
assert.equal(page.root.dataset.theme, 'dark');
assert.equal(page.toggle.attributes['aria-expanded'], 'true');
page.toggle.click();
assert.equal(page.sidebar.hidden, true);
assert.equal(page.root.dataset.menuCollapsed, 'true');
assert.equal(page.toggle.attributes['aria-expanded'], 'false');
assert.equal(page.storage['pointbridge-menu-collapsed'], 'true');
page.toggle.click();
assert.equal(page.sidebar.hidden, false);
assert.equal(page.toggle.attributes['aria-expanded'], 'true');
assert.equal(fixture(null, false, false, true).sidebar.hidden, true);
assert.equal(fixture('invalid').root.dataset.theme, 'dark');
const restricted = fixture(null, false, true);
restricted.login(true);
restricted.choices.find(button => button.dataset.themeChoice === 'light').click();
assert.equal(restricted.root.dataset.theme, 'light');
restricted.toggle.click();
assert.equal(restricted.sidebar.hidden, true);
restricted.login(false);
assert.equal(restricted.root.dataset.theme, 'dark');
const css = readFileSync(new URL('../app/wwwroot/themes.css', import.meta.url), 'utf8');
assert.ok(css.includes("font-family: 'Noto Sans'"));
for (const name of ['Regular', 'Bold']) assert.ok(readFileSync(new URL(`../app/wwwroot/fonts/NotoSans-${name}.ttf`, import.meta.url)).length > 10000);
assert.ok(readFileSync(new URL('../app/wwwroot/fonts/LICENSE.txt', import.meta.url), 'utf8').includes('SIL OPEN FONT LICENSE'));
console.log('Theme tests passed: fixed login, signed-in themes, dark default, session transitions, OS/storage changes, collapsible menu, blocked storage, bundled fonts.');
