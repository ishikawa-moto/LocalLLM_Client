// Run with the Host-staged extension path. The mocked socket returns no private data.
import net from "node:net";
import { EventEmitter } from "node:events";
import { syncBuiltinESMExports } from "node:module";
import { pathToFileURL } from "node:url";
import assert from "node:assert/strict";

const frames=[];
net.connect=({path}) => {
  assert.equal(path,"/run/localbrain/model.sock");
  const socket=new EventEmitter();socket.destroy=()=>{};
  socket.write=(line)=>{frames.push(JSON.parse(line));queueMicrotask(()=>socket.emit("data",Buffer.from('{"ok":true,"text":"fixture"}\n')));};
  queueMicrotask(()=>socket.emit("connect"));return socket;
};
syncBuiltinESMExports();
const extension=(await import(pathToFileURL(process.argv[2]))).default;
const registered=new Map();extension({registerTool:t=>registered.set(t.name,t)});
assert.deepEqual([...registered.keys()],["host_memory_search","host_recall"]);
assert.equal((await registered.get("host_memory_search").execute("1",{query:"needle"})).content[0].text,"fixture");
assert.equal((await registered.get("host_recall").execute("2",{evidenceHash:"a".repeat(64)})).content[0].text,"fixture");
assert.deepEqual(frames,[{operation:"memory_search",query:"needle"},{operation:"recall",evidenceHash:"a".repeat(64),offset:0}]);
console.log("memory extension fixture passed");
