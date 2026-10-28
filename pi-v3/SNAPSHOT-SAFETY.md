# Snapshot lifecycle: design under review

The current `frozen_snapshot.py` is experimental and must not publish a
production result. Sol High identified that a PID and caller-selected lock
cannot establish ownership of `cgroup.freeze`. An owner token is evidence of
a Host operation, not an atomic ownership mechanism for that kernel control.

The proposed default is to end a task generation, confirm the entire workload
is gone, seal a Host-owned bounded workspace, and only then archive it. Any
resume starts a fresh generation. A trusted lifecycle owner must identify each
task, generation, workspace and cgroup, serialize launch, stop, capture and
recovery across all roots, and retain verified handles rather than re-resolving
caller-provided PIDs or paths. Callers and the sandbox must not be able to
modify the registry, control files, journal or published receipts.

Persist stop intent before termination. A successful stop command or leader
exit is not proof that descendants exited. Require verified cgroup emptiness;
if the cgroup disappears before that proof, quarantine the outcome. Confirm
the workspace has no external writers and seal the filesystem itself, not just
one bind mount. If these properties cannot be established, do not archive or
apply the result. A visible archive is not a completed snapshot: publish only
after a durable receipt binds the generation, operation token and digest.

Recovery must not infer ownership from PID, age, a reused pathname or a frozen
bit. An ambiguous generation is quarantined without thawing or killing another
scope. Live freezing, if ever retained, requires every freezer to participate
in the same exclusive ownership protocol and a durable lease that remains
until thaw is confirmed. A timeout or crash before the freeze write does not
prove that this operation owns a later observed freeze.

This is not implemented or approved for deployment. The design needs a
Host-controlled lifecycle boundary and real WSL validation of cgroup lifetime,
workspace sealing, access separation, interruption and recovery on PC70 and
PC73. A separate Windows model relay also needs independent termination and
reconciliation. Temporary WSL filesystems may not survive a distro restart.
