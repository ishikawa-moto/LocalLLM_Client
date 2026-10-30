# LocalBrain v3 migration status

This is a development record, not a PC73 deployment instruction.

| Decision | Component | Current status |
| --- | --- | --- |
| Keep | Windows Host authorization, Canonical events, evidence and approval history | Existing v2 paths remain in place; v3 integration is unfinished. |
| Keep | TaskMemory and SecondBrain | Existing paths remain; the proposed lightweight retrieval and knowledge reuse have not been integrated into v3. |
| Replace | Pi execution and context management | Pi 1.4.0 is pinned; the isolated RPC launcher exists, but currently disables sessions and MCP and is not connected to the Host task lifecycle. |
| Remove | Experimental frozen snapshot, ownership ledger, recovery and archive generation | Removed from the development branch; Git worktree publication and safe cleanup are still unimplemented. |
| Remove after replacement | Laya, Astra, Slack conversation summarization and PC73 OCR | The development Host no longer launches the Laya worker; its Python worker and supervisor were removed. The tokenizer still references an existing environment named `laya-venv`. Astra comparison is not invoked, but old routing and telemetry code remains. Changes are built but not runtime-tested. |
| Implement | Pi loop handling and authorized external handoff | A shell-loop event extension is loaded by the development launcher but is not connected to a Host handoff. The old OpenCode Sol route is disabled until subscription-only authentication is verified; no Opus or Haiku fallback is implemented. |
| Implement | Gateway priorities and notifications | The development Gateway orders authenticated PC73 model traffic, restricted Slack requests and other model traffic, with local tests. The new PC70 OCR path, Work end-to-end priority and Continue notifications remain unverified. |
| Implement | PC70 local-only Slack OCR | The existing PC73 OCR and Slack summary flow have not yet been migrated. Preserve Slack classification, retention and egress controls. |
| Keep and simplify | PC70 Work Runner | A previously tested Gateway path exists; the revised single-job-only lifecycle and safe quota recovery are not yet deployed. |

The development v2 review path now refuses its unverified OpenCode Sol route.
NORMAL and HIGH tasks requiring external review remain unavailable until a
subscription-only route is verified. This is a safety hold, not a passing review.

PC73 production is unchanged. Its peak memory and the effect of migration cannot
be established from PC70 development fixtures.
