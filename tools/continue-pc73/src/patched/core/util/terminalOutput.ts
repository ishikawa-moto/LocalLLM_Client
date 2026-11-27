// Modified for LocalBrain PC73; see NOTICE.
import { StringDecoder } from "node:string_decoder";
import { prefixWithinLimits } from "./toolContextBudget";

export function windowsPowerShellArguments(command: string): string[] {
  // Script-level directives and parameter/phase blocks must stay at the start.
  // Leave those advanced scripts intact. A called .ps1 inherits UTF-8 normally.
  const scriptBlocks = /(?:^|[\r\n])\s*(?:using\s+(?:assembly|module|namespace)\b|param\s*\(|(?:dynamicparam|begin|process|end)\s*\{)/i;
  const setup = "[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false); $OutputEncoding = [Console]::OutputEncoding;\n";
  const script = scriptBlocks.test(command) ? command : setup + command;
  return ["-NoLogo", "-NoProfile", "-ExecutionPolicy", "Bypass", "-Command", script];
}

function captureStream(maxBytes: number, maxLines: number) {
  const decoder = new StringDecoder("utf8");
  let text = "";
  let shortened = false;

  function append(chunk: string): boolean {
    if (shortened || !chunk) return false;
    const next = text + chunk;
    text = prefixWithinLimits(next, maxBytes, maxLines);
    shortened = text.length !== next.length;
    return true;
  }

  return {
    write: (chunk: Buffer) => shortened ? false : append(decoder.write(chunk)),
    append,
    end: () => append(decoder.end()),
    preview() {
      return shortened ? text + "\n[Terminal output shortened.]" : text;
    },
  };
}

export function createTerminalOutput() {
  const stdout = captureStream(5888, 120);
  const stderr = captureStream(1792, 32);
  return {
    stdout,
    stderr,
    end() {
      stdout.end();
      stderr.end();
    },
    preview() {
      const out = stdout.preview();
      const err = stderr.preview();
      return out && err ? `[stderr]\n${err}\n[stdout]\n${out}` : out || err;
    },
  };
}
