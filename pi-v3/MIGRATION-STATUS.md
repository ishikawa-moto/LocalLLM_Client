# LocalBrain v3 migration status

This is a development record, not a PC73 deployment instruction.

| Decision | Component | Current status |
| --- | --- | --- |
| Keep | Windows Host authorization, Canonical events, evidence and approval history | Existing v2 paths remain in place; v3 integration is unfinished. |
| Keep | TaskMemory and SecondBrain | The development Host prepares bounded original evidence and existing SecondBrain references. Task-scoped additional search/Recall tools use the Host gate, including a Pi socket extension; Windows runtime verification remains pending. |
| Replace | Pi execution and context management | Pi 1.4.0 uses standard private sessions and compaction. An explicitly enabled LOW-task development branch is connected to the existing authorized Coordinator and returns a candidate, never a production update. Windows runtime verification remains pending; MCP is still disabled. |
| Remove | Experimental frozen snapshot, ownership ledger, recovery and archive generation | Removed from the development branch; Git worktree publication and safe cleanup are still unimplemented. |
| Remove after replacement | Laya, Astra, Slack conversation summarization and PC73 OCR | The development Host no longer launches the Laya worker; its Python worker and supervisor were removed. The tokenizer still references an existing environment named `laya-venv`. Astra comparison, calibration and routing configuration were removed; historical telemetry fields remain. Changes are built but not runtime-tested. |
| Implement | Pi loop handling and authorized external handoff | A shell-loop event extension is loaded by the development launcher but is not connected to a Host handoff. The old OpenCode Sol route is disabled until subscription-only authentication is verified; no Opus or Haiku fallback is implemented. |
| Implement | Gateway priorities and notifications | The development Gateway orders authenticated PC73 model traffic, restricted Slack requests and other model traffic, with local tests. The new PC70 OCR path, Work end-to-end priority and Continue notifications remain unverified. |
| Implement | PC70 local-only Slack OCR | The existing PC73 OCR and Slack summary flow have not yet been migrated. Preserve Slack classification, retention and egress controls. |
| Keep and simplify | PC70 Work Runner | A previously tested Gateway path exists; the revised single-job-only lifecycle and safe quota recovery are not yet deployed. |

The development v2 review path now refuses its unverified OpenCode Sol route.
NORMAL and HIGH tasks requiring external review remain unavailable until a
subscription-only route is verified. This is a safety hold, not a passing review.

PC73 production is unchanged. Its peak memory and the effect of migration cannot
be established from PC70 development fixtures.
