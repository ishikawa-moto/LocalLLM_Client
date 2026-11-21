// Modified for LocalBrain PC73; see NOTICE.
import { execFileSync } from "node:child_process";
import fs from "node:fs";
import path from "node:path";

type Probe = (program: string, args: string[], cwd: string) => string | undefined;

const runProbe: Probe = (program, args, cwd) => {
  try {
    return execFileSync(program, args, {
      cwd, encoding: "utf8", stdio: ["ignore", "pipe", "ignore"],
      timeout: 8_000, maxBuffer: 12 * 1024 * 1024, windowsHide: true,
      env: { ...process.env, GIT_OPTIONAL_LOCKS: "0", GIT_NO_LAZY_FETCH: "1", GH_PROMPT_DISABLED: "1" },
    });
  } catch (error) {
    if (program === "git" && (error as { status?: number }).status === 1 &&
        (args.includes("config") || args.includes("symbolic-ref"))) return undefined;
    throw new Error("Unable to verify the operation");
  }
};

const isMain = (ref: string) => /^(?:(?:refs\/)?heads\/)?main$/i.test(ref);
const programName = (name: string) => name.split(/[\\/]/).pop()!.replace(/\.exe$/i, "").toLowerCase();
const containsSecret = (value: string) =>
  /-----BEGIN (?:[A-Z0-9 ]+ )?PRIVATE KEY-----|\b(?:gh[pousr]_[A-Za-z0-9]{30,}|github_pat_[A-Za-z0-9_]{40,}|(?:AKIA|ASIA)[A-Z0-9]{16}|sk-(?:proj-)?[A-Za-z0-9_-]{28,}|xox[baprs]-[A-Za-z0-9-]{15,})\b/.test(value);
const secretPath = /(?:^|[\\/])(?:\.env(?:\.[^\\/]+)?|id_(?:rsa|ed25519|ecdsa)|[^\\/]+\.(?:pfx|p12|key))$/i;

// Recover literal top-level command words for conventional shell invocations.
// This is an accident guard. Shell expansion, programs, and aliases are not isolated.
function commands(input: string): string[][] {
  const result: string[][] = [];
  let words: string[] = [];
  let word = "";
  let quote = "";
  const flush = () => {
    if (word) words.push(word);
    word = "";
  };
  for (let index = 0; index < input.length; index++) {
    const ch = input[index];
    if ((process.platform === "win32" ? ch === "\x60" && quote !== "'" : ch === "\\" && quote !== "'") &&
        index + 1 < input.length) {
      word += ch + input[++index];
      continue;
    }
    if ((ch === '"' || ch === "'") && (!quote || quote === ch)) {
      quote = quote ? "" : ch;
    } else if (!quote && /[;&|\r\n]/.test(ch)) {
      flush();
      if (words.length) result.push(words);
      words = [];
    } else if (!quote && /\s/.test(ch)) {
      flush();
    } else {
      word += ch;
    }
  }
  if (quote) throw new Error("Use complete literal command arguments");
  flush();
  if (words.length) result.push(words);
  return result;
}

function verifyPush(args: string[], current: string, config: (key: string) => string | undefined,
  git: (args: string[]) => string | undefined) {
  const positional: string[] = [];
  let deleting = false;
  for (const arg of args) {
    if (arg === "--delete" || arg === "-d") deleting = true;
    else if (arg !== "--" && !/^(?:-[ufvnq]+|--(?:force(?:-with-lease(?:=[^ ]+)?|-if-includes)?|set-upstream|verbose|quiet|porcelain|dry-run|atomic|thin|no-thin|no-follow-tags|ipv4|ipv6))$/.test(arg)) {
      if (arg.startsWith("-")) throw new Error("Bulk or ambiguous pushes are blocked. Use a remote and explicit branch refspec.");
      positional.push(arg);
    }
  }
  const remote = positional.shift() ?? config("branch." + current + ".pushRemote") ??
    config("remote.pushDefault") ?? config("branch." + current + ".remote") ?? "origin";
  if (!remote || /[$%*:\x60]/.test(remote) && !/^(?:https?:\/\/|ssh:\/\/|[^@\s]+@[^:\s]+:)/.test(remote)) {
    throw new Error("Push remote must be literal");
  }
  if (git(["config", "--type=bool", "--get", "remote." + remote + ".mirror"])?.trim() === "true") {
    throw new Error("Mirror pushes are blocked");
  }
  if (!["", "no", "check", "false"].includes(config("push.recurseSubmodules") ?? "")) {
    throw new Error("Push submodule branches explicitly to verify their destinations");
  }
  if (!positional.length) {
    const mode = config("push.default") ?? "simple";
    if (deleting || config("remote." + remote + ".push") !== undefined ||
        !["simple", "current", "upstream", "tracking"].includes(mode)) {
      throw new Error("Use an explicit non-main push refspec");
    }
    const upstream = config("branch." + current + ".merge");
    if (upstream?.includes("\n") || ["upstream", "tracking"].includes(mode) && !upstream ||
        mode !== "current" && upstream && isMain(upstream)) {
      throw new Error("Push upstream could target main");
    }
    positional.push("HEAD:" + (mode === "upstream" || mode === "tracking" ? upstream : "refs/heads/" + current));
  }
  for (const item of positional) {
    const spec = item.replace(/^\+/, "");
    const [source, to, ...rest] = spec.split(":");
    const destination = to ?? source;
    if (rest.length || !destination || isMain(destination) || /[$%*?\[\]\x60]/.test(destination) ||
        destination.startsWith("refs/") && !destination.startsWith("refs/heads/")) {
      throw new Error("Push to main or an ambiguous destination is blocked");
    }
    if (!source || deleting) continue;
    const sha = git(["rev-parse", "--verify", "--end-of-options", source + "^{commit}"])?.trim();
    if (!sha || !/^[0-9a-f]{40,64}$/.test(sha)) throw new Error("Push must identify a branch commit");
    const exclude = /^[\w.-]+$/.test(remote) ? ["--not", "--remotes=" + remote] : [];
    const changes = git(["log", "--format=%B", "-p", "--no-ext-diff", "--no-textconv", "--unified=0",
      "--diff-filter=ACMR", "--diff-merges=first-parent", sha, ...exclude, "--"]);
    if (changes === undefined || containsSecret(changes)) throw new Error("Possible secret in outgoing commits");
    const files = git(["log", "--format=", "--name-only", "--diff-filter=ACMR", "--diff-merges=first-parent", sha, ...exclude, "--"]);
    if (files === undefined || files.split(/\r?\n/).some((file) => secretPath.test(file.trim()))) {
      throw new Error("Possible secret file in outgoing commits");
    }
  }
}

const mainGitReads = new Set(["status", "log", "show", "diff", "grep", "rev-parse", "ls-files", "ls-tree", "blame", "describe", "cat-file"]);
const mainShellReads = new Set(["get-location", "pwd", "ls", "dir", "get-childitem", "get-content", "cat", "type", "rg"]);

export function assertLocalBrainTerminalBoundary(input: string, initialDir: string, probe: Probe = runProbe): void {
  const parts = commands(input);
  let cwd = initialDir;
  const parents: string[] = [];

  if (containsSecret(input)) throw new Error("Possible secret in command arguments");

  for (const [head, ...tail] of parts) {
    const program = programName(head);
    if (["cd", "chdir", "set-location", "pushd", "push-location", "popd", "pop-location"].includes(program)) {
      const args = tail.filter((arg) => !/^(?:-LiteralPath|-Path|\/d|--)$/.test(arg));
      const pop = ["popd", "pop-location"].includes(program);
      if (pop && !args.length && parents.length) cwd = parents.pop()!;
      else if (!pop && args.length === 1 && !/[$%*?\x60]/.test(args[0])) {
        if (["pushd", "push-location"].includes(program)) parents.push(cwd);
        cwd = path.resolve(cwd, args[0]);
      } else throw new Error("Use a literal directory for directory changes");
      continue;
    }

    let args = tail;
    let gitDir = cwd;
    if (program === "git") {
      while (args[0]?.startsWith("-") && !["--version", "--help"].includes(args[0])) {
        const option = args[0];
        if (["--no-pager", "--no-optional-locks"].includes(option)) {
          args = args.slice(1);
        } else if (option === "-C" || option.startsWith("-C") && option.length > 2) {
          const dir = option === "-C" ? args[1] : option.slice(2);
          if (!dir || /[$%*?\x60]/.test(dir)) throw new Error("Use a literal Git directory");
          gitDir = path.resolve(gitDir, dir);
          args = args.slice(option === "-C" ? 2 : 1);
        } else {
          throw new Error("Unverified Git global options. Use a literal command and directory.");
        }
      }
    }

    // rev-parse distinguishes a non-repository from a failed branch lookup.
    let repository = false;
    try {
      repository = probe("git", ["rev-parse", "--git-dir"], gitDir) !== undefined;
    } catch {
      // Outside a repository, support only reads and explicit repository creation.
      if (!fs.existsSync(gitDir)) throw new Error("Cannot verify command directory");
    }
    const symbolicHead = repository
      ? probe("git", ["symbolic-ref", "--quiet", "HEAD"], gitDir)?.trim()
      : undefined;
    const branch = symbolicHead?.startsWith("refs/heads/") ? symbolicHead.slice(11) : undefined;
    const main = !!branch && isMain(branch);

    const [subcommand, ...params] = args;
    if (main && args.some((arg) => /[<>]/.test(arg))) throw new Error("Redirection is blocked on main");
    if (parts.length > 1 && (program === "git" && ["commit", "push"].includes(subcommand) ||
        program === "gh" && subcommand === "pr" && params[0] === "merge")) {
      throw new Error("Run commit, push, and PR merge in a separate terminal call");
    }
    const git = (argv: string[]) => probe("git", ["--no-pager", "-c", "core.fsmonitor=false", ...argv], gitDir);
    const config = (key: string) => git(["config", "--get-all", key])?.trim();

    if (program === "git") {
      const operandText = params.join(" ");
      const hasMain = params.some(isMain);
      if (/\$|\x60|%[^% ]+%/.test(subcommand ?? "") ||
          ["push", "branch", "switch", "checkout", "update-ref", "symbolic-ref", "fetch", "pull"].includes(subcommand)
          && /[$\x60]|%[^% ]+%/.test(operandText.split(" -- ")[0])) {
        throw new Error("Use literal arguments for Git ref operations");
      }
      if (["switch", "checkout"].includes(subcommand)) {
        const creation = params.findIndex((arg) => /^(?:-[cCbB]|--(?:create|force-create|orphan))(?:=|$)/.test(arg));
        const created = creation < 0 ? undefined : params[creation].split("=")[1] ?? params[creation + 1];
        if (created && isMain(created) || params.some((arg) => /^-[CB].+/i.test(arg) && isMain(arg.slice(2)))) {
          throw new Error("Creating or resetting main is blocked");
        }
        const restore = subcommand === "checkout" && params.includes("--");
        if (!restore) {
          if (parts.length > 1) throw new Error("Run branch switches in a separate terminal call");
          if (hasMain && !created && !params.includes("--detach")) {
            if (params.some((arg) => arg !== "--" && !isMain(arg)) ||
                git(["status", "--porcelain", "--untracked-files=no"])?.length !== 0) {
              throw new Error("Switch to main requires a clean checkout and no modifying options");
            }
          }
          if (main && params.some((arg) => /^(?:-[fm]|--(?:force|discard-changes|merge|ignore-other-worktrees))(?:=|$)/.test(arg))) {
            throw new Error("Switch from main without forcing changes");
          }
          // On main, checkout's single operand must actually be a branch, not
          // a pathname that checkout would restore over the working tree.
          if (main && params.length === 1 && /^[\w./-]+$/.test(params[0]) && !params[0].startsWith("-") && !hasMain &&
              (subcommand === "switch" || git(["rev-parse", "--verify", "--end-of-options", "refs/heads/" + params[0]]))) continue;
          if ((!main && branch) || created || params.includes("--detach") ||
              hasMain && params.every((arg) => arg === "--" || isMain(arg))) continue;
        }
      }
      if (subcommand === "branch") {
        const reading = !params.length || params.some((arg) => ["--list", "--show-current", "-a", "--all", "-r", "--remotes"].includes(arg)) &&
          params.every((arg) => !/^-(?:[dDmMcCfF]+|--(?:move|copy|force|delete))/.test(arg));
        if (reading) continue;
        const positional = params.filter((arg) => arg !== "--" && !arg.startsWith("-"));
        const copy = params.some((arg) => /^(?:-[cC]+|--copy)$/.test(arg));
        const modifiesMultiple = params.some((arg) => /^(?:-[dDmM]+|--(?:delete|move))$/.test(arg));
        if (!positional.length || (modifiesMultiple ? hasMain || main && positional.length < 2 :
            copy ? isMain(positional[positional.length - 1]) : isMain(positional[0]))) {
          throw new Error("Changing the main branch ref is blocked");
        }
        if (params.some((arg) => /^-[dDmMcCfF].{1,}/.test(arg) && !/^-[dDmMcCfF]+$/.test(arg))) {
          throw new Error("Use separate Git branch option arguments");
        }
        if (main && params.some((arg) => /^(?:-u|-t|--set-upstream-to|--unset-upstream|--edit-description|--track)(?:=|$)/.test(arg))) {
          throw new Error("Changing current main branch settings is blocked");
        }
        continue;
      }
      if (["update-ref", "symbolic-ref", "replace"].includes(subcommand) &&
          (hasMain || params.some((arg) => /^--stdin(?:=|$)|^refs\/heads\/main(?:$|\/)/i.test(arg)) ||
           subcommand === "symbolic-ref" && params.length > 1 && params.includes("HEAD"))) {
        throw new Error("Main or unverified ref modification is blocked");
      }
      if (["fetch", "pull"].includes(subcommand)) {
        if (params.some((arg) => /^(?:--refmap|--prune|--prune-tags)(?:=|$)|^-[pP]$/.test(arg) ||
            arg.includes(":") && !/^(?:https?:\/\/|ssh:\/\/|[^@\s]+@[^:\s]+:)/.test(arg) &&
            !/:(?:refs\/remotes\/[^:]+|refs\/tags\/[^:]+)$/.test(arg))) {
          throw new Error("Fetch or pull refspec could update a local branch");
        }
        const fetchMaps = git(["config", "--get-regexp", "^remote\\..*\\.fetch$"]);
        if (fetchMaps?.split(/\r?\n/).some((line) => /:(?:(?:refs\/)?heads\/|main(?:\s|$))/i.test(line))) {
          throw new Error("A configured fetch refspec can update a local branch");
        }
      }
      if (subcommand === "worktree") {
        const [action, ...targets] = params;
        if (action === "add" && targets.some((arg) => isMain(arg) ||
            /^-[Bb].+/.test(arg) && isMain(arg.slice(2)) || /^[^.]*[\\/](?:main)$/i.test(arg) && arg.startsWith("refs/"))) {
          throw new Error("Creating or resetting a main checkout is blocked");
        }
        if (["move", "remove"].includes(action)) {
          const target = targets.find((arg) => !arg.startsWith("-"));
          const requested = target && path.resolve(gitDir, target);
          const listings = git(["worktree", "list", "--porcelain", "-z"]);
          const entry = listings?.split("\0\0").map((row) => row.split("\0")).find(([item]) =>
            requested && item.startsWith("worktree ") &&
              path.relative(path.normalize(item.slice(9)), requested) === "");
          if (!entry || entry.some((field) => field.startsWith("branch ") && isMain(field.slice(7)))) {
            throw new Error("Cannot move or remove a main or unverified worktree");
          }
        }
      }
      if (main && subcommand === "fetch" && !params.includes("--update-head-ok")) continue;
      if (main && (!mainGitReads.has(subcommand) || params.some((arg) => /^--(?:output|ext-diff|textconv|exec)(?:=|$)|[<>]/.test(arg)))) {
        throw new Error("This operation would run on main. Switch to a development branch first.");
      }
      if (subcommand === "send-email") throw new Error("Explicit outbound transfers require a human");
      if (subcommand === "push") {
        if (!branch) throw new Error("Push requires a checked-out development branch");
        verifyPush(params, branch, config, git);
      }
      if (subcommand === "commit") {
        for (const mode of [[], ["--cached"]]) {
          const diff = git(["diff", ...mode, "--no-ext-diff", "--no-textconv", "--unified=0", "--"]);
          const files = git(["diff", ...mode, "--name-only", "-z", "--diff-filter=ACMR", "--"]);
          if (diff === undefined || files === undefined || diff.split(/\r?\n/).some((line) => line.startsWith("+") && containsSecret(line)) ||
              files.split("\0").some((file) => secretPath.test(file))) {
            throw new Error("Possible secret in staged or modified files");
          }
        }
      }
      if (!branch && !mainGitReads.has(subcommand) && !["init", "clone", "--help", "--version"].includes(subcommand)) {
        throw new Error("This operation requires a checked-out development branch");
      }
      continue;
    }

    if (program === "gh") {
      if (subcommand === "gist" && ["create", "edit"].includes(params[0])) {
        throw new Error("Explicit outbound file transfers require a human");
      }
      if (subcommand === "pr" && ["create", "edit", "comment", "review"].includes(params[0])) {
        for (const [index, arg] of params.entries()) {
          const body = /^(?:-F|--body-file)$/.test(arg) ? params[index + 1] ?? "-" :
            arg.startsWith("--body-file=") ? arg.slice(12) : arg.startsWith("-F") ? arg.slice(2) : undefined;
          if (body !== undefined) {
            if (body === "-" || /[$%\x60]/.test(body) || secretPath.test(body)) throw new Error("Possible secret or unverifiable PR body file");
            const filename = path.resolve(cwd, body);
            const stat = fs.statSync(filename);
            if (!stat.isFile() || stat.size > 1024 * 1024 || containsSecret(fs.readFileSync(filename, "utf8"))) {
              throw new Error("Possible secret or unverifiable PR body file");
            }
          }
        }
      }
      if (subcommand === "pr" && params[0] === "merge") {
        const pr = params[1];
        const options = params.slice(2);
        if (!pr || !/^(?:\d+|https:\/\/github\.com\/[\w.-]+\/[\w.-]+\/pull\/\d+)$/.test(pr) ||
            options.some((arg) => !/^--(?:merge|squash|rebase)$/.test(arg))) {
          throw new Error("Use an explicit PR number or URL for a non-main merge. Automatic merges require a human.");
        }
        const data = probe("gh", ["pr", "view", pr, "--json", "baseRefName"], cwd);
        const base = data && JSON.parse(data).baseRefName;
        if (typeof base !== "string" || !base || isMain(base)) throw new Error("PR merges into main require a human");
      }
      if (subcommand === "api") {
        const apiArgs = params.join(" ");
        const method = params.flatMap((arg, index) => {
          if (arg === "--method" || arg === "-X") return [params[index + 1] ?? ""];
          if (arg.startsWith("--method=")) return [arg.slice(9)];
          if (arg.startsWith("-X")) return [arg.slice(2)];
          return [];
        });
        if (/graphql|--(?:input|field|raw-field)|(?:^|\s)-[fF]/i.test(apiArgs) ||
            method.some((verb) => verb.toUpperCase() !== "GET")) {
          throw new Error("Use gh pr commands for authorized PR changes. Raw GitHub API writes are blocked.");
        }
      }
      if (subcommand === "repo" && (params.includes("delete") || params.some((arg) => /^--default-branch(?:=|$)/.test(arg)))) {
        throw new Error("Changing repository protection or the default branch is blocked");
      }
      if (main && !(subcommand === "pr" && ["view", "list", "status", "diff"].includes(params[0]) ||
          subcommand === "repo" && ["view", "list"].includes(params[0]) || subcommand === "api")) {
        throw new Error("Use a development branch for GitHub changes");
      }
      continue;
    }

    if (["scp", "sftp", "ftp", "nc", "netcat", "send-mailmessage"].includes(program) ||
        ["curl", "wget", "invoke-webrequest", "invoke-restmethod", "iwr", "irm"].includes(program) &&
        tail.some((arg) => /^--?(?:upload-file|data(?:-binary|-raw|-urlencode)?|form(?:-string)?|post-(?:data|file)|InFile|Body)(?:=|$)|^-[dFT]/i.test(arg))) {
      throw new Error("Explicit outbound file or body transfers require a human");
    }
    if ((!branch || main) && (!mainShellReads.has(program) || tail.some((arg) => /[<>\x60]|\$\(|^--(?:pre|hostname-bin)(?:=|$)/.test(arg)))) {
      throw new Error("Run development commands from a checked-out non-main branch");
    }
  }

}
