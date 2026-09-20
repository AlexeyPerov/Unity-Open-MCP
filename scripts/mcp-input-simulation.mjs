#!/usr/bin/env node
// Live input regression replay. Fixtures are transient; scenes are never saved.
import assert from 'node:assert/strict';
import {readFileSync, writeFileSync} from 'node:fs';
import {resolve, dirname} from 'node:path';
import {fileURLToPath} from 'node:url';
import {Client} from '../mcp-server/node_modules/@modelcontextprotocol/sdk/dist/esm/client/index.js';
import {StdioClientTransport} from '../mcp-server/node_modules/@modelcontextprotocol/sdk/dist/esm/client/stdio.js';
const root = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const args = process.argv.slice(2);
function option(key, fallback) { const i = args.indexOf(key); return i < 0 ? fallback : args[i + 1]; }
const project = resolve(option('--project', resolve(root, 'demo')));
const fixture = option('--fixture', 'frames');
assert(['frames', 'pointer'].includes(fixture), '--fixture must be frames or pointer');
const client = new Client({name: 'input-simulation-replay', version: '1.0'});
const transport = new StdioClientTransport({command: process.execPath, args: [resolve(root, 'mcp-server/dist/index.js')],
  env: {...process.env, UNITY_PROJECT_PATH: project}, stderr: 'pipe'});
await client.connect(transport);
const evidence = [];
async function call(name, arguments_) {
  let response, body;
  for (let attempt = 0; attempt < 30; attempt++) {
    response = await client.callTool({name: 'unity_open_mcp_' + name, arguments: arguments_}, undefined, {timeout: 60000});
    body = JSON.parse(response.content.find(c => c.type === 'text').text);
    if (body.error?.code !== 'editor_reloading') break;
    // This refusal is pre-dispatch, so retrying cannot duplicate an injection.
    await new Promise(r => setTimeout(r, 1000));
  }
  evidence.push({name, response});
  assert(!response.isError, JSON.stringify(body));
  if (body.mutation) assert.equal(body.mutation.success, true, JSON.stringify(body));
  return body;
}
async function state() {
  return (await call('execute_csharp', {code: 'return UnityEditor.EditorApplication.isPlaying;', read_only: true})).mutation.output;
}
async function waitState(playing) {
  for (let i = 0; i < 30; i++) {
    await new Promise(r => setTimeout(r, 1000));
    if (await state() === playing) return;
  }
  throw new Error('Editor play transition did not settle');
}
let entered = false;
try {
  if (!await state()) {
    // Opting into this disposable play-mode replay authorizes entering play,
    // including a dirty scene; the test never saves or replaces that scene.
    entered = true;
    await call('editor_set_state', {state: 'play', ignore_scene_dirty: true});
    await waitState(true);
  }
  const body = await call('execute_csharp', {code: readFileSync(resolve(root, `scripts/fixtures/input-simulation-${fixture}.cs`), 'utf8'), read_only: true});
  assert(Array.isArray(body.mutation.output) && body.mutation.output.length > 0);
  assert(!(body.logs ?? []).some(l => ['error', 'exception'].includes(l.severity)), JSON.stringify(body.logs));
  console.log(`${body.mutation.output.length} live ${fixture} assertions passed`);
} finally {
  try { if (entered) { await call('editor_set_state', {state: 'stop'}); await waitState(false); } }
  finally {
    const report = option('--json-out');
    if (report) writeFileSync(report, JSON.stringify(evidence, null, 2) + '\n');
    await client.close();
  }
}
