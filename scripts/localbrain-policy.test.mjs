import assert from 'node:assert/strict';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import {checkToolCall} from './localbrain-policy.mjs';

const root = fs.mkdtempSync(path.join(os.tmpdir(), 'localbrain-policy-'));
try {
  const search = query => checkToolCall({toolName:'host_memory_search',input:{query}},root);
  assert.equal(search('過去の設計理由'), undefined);
  assert.equal(search('x'.repeat(1024)), undefined);
  for (const value of [undefined, null, 12, {}, '', '  \t', 'x'.repeat(1025)])
    assert.equal(search(value)?.block,true);
  assert.equal(checkToolCall({toolName:'host_recall',input:{evidenceHash:'a'.repeat(64)}},root),undefined);
  for (const toolName of ['bash','powershell','codemode','obs_recall','write','edit'])
    assert.equal(checkToolCall({toolName,input:{path:'allowed.txt'}},root)?.block,true);
  for (const target of ['../outside.txt','.git/config','.env','key.pem'])
    assert.equal(checkToolCall({toolName:'read',input:{path:target}},root)?.block,true);
  assert.equal(checkToolCall({toolName:'read',input:{path:'allowed.txt'}},root),undefined);
  console.log('Pi Host memory-search and existing file/shell policy checks passed');
} finally { fs.rmdirSync(root); }
