import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import vm from 'node:vm';

const source = readFileSync(new URL('../app/wwwroot/theme.js', import.meta.url), 'utf8');
function fixture(saved, dark = false, blocked = false) {
  const events = {};
  const choices = [];
  const containers = [[], []];
  const root = { dataset: {}, style: {} };
  const media = { matches: dark, addEventListener: (name, callback) => { events.media = callback; } };
  const storage = { value: saved };
  const document = {
    documentElement: root,
    addEventListener: (name, callback) => { events[name] = callback; },
    querySelectorAll: selector => selector === '[data-theme-choice]' ? choices : selector === '.mark' ? [] : containers.map(children => ({ append: element => children.push(element) })),
    createElement: tag => ({ tag, dataset: {}, children: [], attributes: {}, append(...elements) { this.children.push(...elements); }, setAttribute(name, value) { this.attributes[name] = value; }, addEventListener(name, callback) { this[name] = callback; } })
  };
  vm.runInNewContext(source, {
    document,
    window: { matchMedia: () => media, addEventListener: (name, callback) => { events[name] = callback; } },
    localStorage: { getItem: () => { if (blocked) throw Error('blocked'); return saved; }, setItem: (key, value) => { if (blocked) throw Error('blocked'); storage.value = value; } }
  });
  events.DOMContentLoaded();
  for (const container of containers) choices.push(...container[0].children.filter(element => element.tag === 'button'));
  events.media();
  return { root, events, media, storage, choices };
}

for (const mode of ['light', 'dark', 'boomer']) {
  const page = fixture(mode);
  assert.equal(page.root.dataset.theme, mode);
  assert.equal(page.root.style.colorScheme, mode === 'dark' ? 'dark' : 'light');
  assert.equal(page.choices.filter(button => button.attributes['aria-pressed'] === 'true').length, 2);
}
const page = fixture(null, true);
assert.equal(page.root.dataset.theme, 'dark');
page.media.matches = false;
page.events.media();
assert.equal(page.root.dataset.theme, 'light');
page.choices.find(button => button.dataset.themeChoice === 'boomer').click();
assert.equal(page.root.dataset.theme, 'boomer');
assert.equal(page.storage.value, 'boomer');
page.media.matches = true;
page.events.media();
assert.equal(page.root.dataset.theme, 'boomer');
page.events.storage({ key: 'pointbridge-theme', newValue: 'light' });
assert.equal(page.root.dataset.theme, 'light');
page.events.storage({ key: null, newValue: null });
assert.equal(page.root.dataset.theme, 'dark');
assert.equal(fixture('invalid', true).root.dataset.theme, 'dark');
assert.equal(fixture(null, false, true).root.dataset.theme, 'light');
const restricted = fixture(null, false, true);
restricted.choices.find(button => button.dataset.themeChoice === 'dark').click();
assert.equal(restricted.root.dataset.theme, 'dark');
console.log('Theme tests passed: four modes, persistence, OS changes, cross-tab changes, invalid preferences, blocked storage.');
