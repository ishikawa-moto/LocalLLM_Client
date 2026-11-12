import assert from 'node:assert/strict';
import extension from '../agent/localbrain-loop.mjs';

const handlers = new Map();
extension({ on(name, fn) { handlers.set(name, fn); } });
const run = (name, event, ctx = {}) => handlers.get(name)(event, ctx);
const result = (text) => ({toolName: 'bash', input: {command: 'fixture'}, isError: true, content: [{type: 'text', text}]});
let aborted = 0;
const ctx = {abort() { aborted++; }};
run('agent_start', {});
run('tool_result', result('one')); run('tool_result', result('one'));
run('turn_end', {}, ctx); assert.equal(aborted, 0);
run('tool_result', result('changed')); run('tool_result', result('changed'));
run('turn_end', {}, ctx); assert.equal(aborted, 0);
run('tool_result', result('changed'));
run('turn_end', {}, ctx); assert.equal(aborted, 1);
run('agent_start', {});
run('tool_result', result('changed')); run('turn_end', {}, ctx); assert.equal(aborted, 1);
console.log('Pi loop event fixture passed');
