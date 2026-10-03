// Node-only regression test; does not launch a browser or a media server.
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
let assertions = 0;
for (const relative of ['tools/srs-lab/localize.js', 'tools/srs-test/web/localize.js']) {
  const source = fs.readFileSync(path.join(__dirname, '..', relative), 'utf8');
  const messages = JSON.parse(source.match(/const messages = (\{[\s\S]*?\n\});/)[1]);
  for (const [requested, expected] of [
    ['zh-TW', 'zh-TW'], ['zh-Hant-TW', 'zh-TW'], ['ZH-tw', 'zh-TW'],
    ['zh-CN', 'zh-CN'], ['zh-SG', 'zh-CN'], ['zh-HK', 'zh-HK'],
    ['zh-MO', 'zh-HK'], ['zh-Hant', 'zh-HK'], ['en-US', 'en-US'], ['de-DE', 'en-US'],
  ]) {
    for (const explicit of [false, true]) {
      const nodes = Object.keys(messages).map(textContent => ({ textContent }));
      const label = { value: 'Media server publish and playback endpoints',
        getAttribute() { return this.value; }, setAttribute(_, value) { this.value = value; } };
      const document = { documentElement: {}, querySelectorAll(selector) {
        return selector === '[data-i18n]' ? nodes : [label];
      } };
      const context = { URLSearchParams, location: { search: explicit ? '?lang=' + requested : '' },
        navigator: { language: explicit ? 'en-US' : requested }, document };
      context.window = context;
      vm.runInNewContext(source, context, { filename: relative });
      assert.equal(document.documentElement.lang, expected);
      const column = { 'zh-CN': 0, 'zh-HK': 1, 'zh-TW': 2 }[expected];
      for (const [i, [key, values]] of Object.entries(messages).entries()) {
        const text = expected === 'en-US' ? key : values[column];
        assert.equal(context.uiText(key, 'first', 'second'), text.replace(/\{(\d+)\}/g, (_, n) => ['first', 'second'][n] ?? ''));
        assert.equal(nodes[i].textContent, text.replace(/\{\d+\}/g, ''));
        assertions += 2;
      }
      assert.equal(label.value, context.uiText('Media server publish and playback endpoints'));
      assertions += 2;
    }
  }
}
console.log(`Browser localization: ${assertions} assertions passed, including system locale and explicit language overrides.`);
