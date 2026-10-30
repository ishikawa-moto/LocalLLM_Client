# LocalBrain v3 migration status

This is a development record, not a PC73 deployment instruction.

| Decision | Component | Current status |
| --- | --- | --- |
| Keep | Windows Host authorization, Canonical events, evidence and approval history | Existing v2 paths remain in place; v3 integration is unfinished. |
| Keep | TaskMemory and SecondBrain | Existing paths remain; the proposed lightweight retrieval and knowledge reuse have not been integrated into v3. |
| Replace | Pi execution and context management | Pi 1.4.0 is pinned; the isolated RPC launcher exists, but currently disables sessions and MCP and is not connected to the Host task lifecycle. |
| Remove | Experimental frozen snapshot, ownership ledger, recovery and archive generation | Removed from the development branch; Git worktree publication and safe cleanup are still unimplemented. |
| Remove after replacement | Laya, Astra, Slack conversation summarization and PC73 OCR | The development Bridge no longer invokes Laya for its request-local loop check; other v2 and Slack paths still use these components. The Bridge change is built but not runtime-tested. |
| Implement | Pi loop handling and authorized external handoff | A shell-loop event extension exists but is not loaded by the launcher or connected to a handoff. Model-specific access and pricing remain unverified. |
| Implement | Gateway priorities and notifications | The development Gateway orders authenticated PC73 model traffic, restricted Slack requests and other model traffic, with local tests. The new PC70 OCR path, Work end-to-end priority and Continue notifications remain unverified. |
| Implement | PC70 local-only Slack OCR | The existing PC73 OCR and Slack summary flow have not yet been migrated. Preserve Slack classification, retention and egress controls. |
| Keep and simplify | PC70 Work Runner | A previously tested Gateway path exists; the revised single-job-only lifecycle and safe quota recovery are not yet deployed. |

PC73 production is unchanged. Its peak memory and the effect of migration cannot
be established from PC70 development fixtures.
