// Pi event hooks: stop a repeated, unchanged shell result at a turn boundary.
// This extension stores no source text and makes no external requests.
import { createHash } from 'node:crypto';

export default function localBrainLoop(pi) {
  let previous = null;
  let repetitions = 0;
  let stop = false;
  pi.on('agent_start', () => { previous = null; repetitions = 0; stop = false; });
  pi.on('tool_result', (event) => {
    if (event.toolName !== 'bash' || event.parentToolCallId) {
      previous = null; repetitions = 0; stop = false; return;
    }
    const signature = createHash('sha256').update(JSON.stringify([
      event.toolName, event.input, event.isError, event.content,
    ])).digest('hex');
    repetitions = signature === previous ? repetitions + 1 : 1;
    previous = signature;
    stop = repetitions >= 3;
  });
  pi.on('turn_end', (_event, ctx) => {
    if (!stop) return;
    stop = false;
    // No commands, results, prompts or other task data are written here.
    console.error('LOCALBRAIN_LOOP_STOP: repeated unchanged shell result');
    ctx.abort();
  });
}
