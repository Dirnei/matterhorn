import { test } from 'node:test';
import assert from 'node:assert/strict';
import { isControllable, filterDevices, suggestName } from '../../src/Matterhorn/wwwroot/js/store.js';

const light = { friendly_name: 'lamp', device_type: 'Dimmable Light',
  exposes: [{ property: 'state', access: 7 }, { property: 'brightness', access: 7 }] };
const sensor = { friendly_name: 'temp1', device_type: 'Temperature Sensor',
  exposes: [{ property: 'temperature', access: 5 }] }; // access 5 = published|get, no set bit

test('isControllable: light with a settable expose is controllable', () => {
  assert.equal(isControllable(light), true);
});
test('isControllable: read-only sensor is not controllable', () => {
  assert.equal(isControllable(sensor), false);
});
test('filterDevices: substring match on friendly_name, case-insensitive', () => {
  const out = filterDevices([light, sensor], 'LAM', false);
  assert.deepEqual(out.map(d => d.friendly_name), ['lamp']);
});
test('filterDevices: controllableOnly drops sensors', () => {
  const out = filterDevices([light, sensor], '', true);
  assert.deepEqual(out.map(d => d.friendly_name), ['lamp']);
});
test('suggestName: first free numbered name', () => {
  assert.equal(suggestName('group', new Set(['group_1'])), 'group_2');
  assert.equal(suggestName('scene', new Set()), 'scene_1');
});
