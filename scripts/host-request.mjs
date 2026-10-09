import { spawn } from "node:child_process";
export async function requestHost(operation, fields = {}, signal) {
  const relay = process.env.LOCALBRAIN_RELAY_EXE;
  const pipe = process.env.LOCALBRAIN_SIDEFX_PIPE;
  const nonce = process.env.LOCALBRAIN_SIDEFX_NONCE;
  if (!relay || !/^\/mnt\/[a-z]\/.*\/localbrain\.exe$/i.test(relay) ||
      !/^localbrain-sidefx-[a-f0-9]{32}$/.test(pipe ?? "") || !nonce)
    throw new Error("Windows Host capability is unavailable");
  const body = JSON.stringify({ pipe, nonce, operation, ...fields });
  if (Buffer.byteLength(body) > 3 * 1024 * 1024) throw new Error("Host request exceeds limit");
  const child = spawn(relay, ["host-action"], {stdio:["pipe","pipe","pipe"],windowsHide:true});
  const chunks = []; let bytes = 0;
  const abort = () => child.kill(); const timer = setTimeout(abort,35000);
  signal?.addEventListener("abort",abort,{once:true});
  try {
    // ASCII envelope survives Windows/WSL standard-input encoding and line conversion.
    child.stdin.end(Buffer.from(body,"utf8").toString("base64"));
    child.stdout.on("data",chunk=>{ bytes+=chunk.length;if(bytes>65536)child.kill();else chunks.push(chunk); });
    child.stderr.resume();
    const code = await new Promise((resolve,reject)=>{child.once("error",reject);child.once("close",resolve);});
    if(code!==0 || bytes>65536)throw new Error("Windows Host request failed");
    const result = JSON.parse(Buffer.from(Buffer.concat(chunks).toString("ascii").trim(),"base64").toString("utf8"));
    return result;
  } finally {clearTimeout(timer);signal?.removeEventListener("abort",abort);if(child.exitCode===null)child.kill();}
}
