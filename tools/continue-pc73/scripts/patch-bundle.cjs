// Rebuild just the changed modules in the prebuilt Continue 2.0.0 bundle.
// Every splice checks its anchors before replacing an existing bundle section.
const fs = require("node:fs");
const path = require("node:path");
const ts = require("typescript");
const root = path.join(__dirname, "..");
const src = (file) => fs.readFileSync(path.join(root, "src", "patched", file), "utf8").replace(/\r\n/g, "\n").replace(/^\/\/ Modified for LocalBrain PC73; see NOTICE\.\n/, "");
const stage = path.join(process.argv[2], "extension");
const input = path.join(stage, "out", "extension.js");
const output = process.argv[3];

function slice(text, start, end) {
  const a = text.indexOf(start);
  const b = text.indexOf(end, a + start.length);
  if (a < 0 || b < 0 || text.indexOf(start, b) >= 0) {
    throw new Error("Missing or ambiguous anchors: " + start + " / " + end);
  }
  return [a, b];
}

function replace(text, start, end, value) {
  const [a, b] = slice(text, start, end);
  return text.slice(0, a) + value + text.slice(b);
}

function between(text, start, end) {
  const [a, b] = slice(text, start, end);
  return text.slice(a, b);
}

function compile(file, text = src(file), dependencies = {}) {
  const result = ts.transpileModule(text, {
    fileName: file,
    reportDiagnostics: true,
    compilerOptions: {
      module: ts.ModuleKind.CommonJS,
      target: ts.ScriptTarget.ES2022,
      esModuleInterop: true,
    },
  });
  const errors = result.diagnostics.filter((error) => error.category === ts.DiagnosticCategory.Error);
  if (errors.length) throw new Error(errors.map((e) => ts.flattenDiagnosticMessageText(e.messageText, "\n")).join("\n"));
  const cases = Object.entries(dependencies).map(([name, value]) =>
    "case " + JSON.stringify(name) + ": return " + value + ";").join("\n");
  return "(function (nativeRequire) {\n" +
    "  const exports = {};\n" +
    "  function require(id) { switch (id) {\n" + cases + "\n default: return nativeRequire(id);\n  } }\n" +
    result.outputText + "\nreturn exports;\n})(require)";
}

let code = fs.readFileSync(input, "utf8").replace(/\r\n/g, "\n");
const policyStart = "// ../../packages/terminal-security/dist/evaluateTerminalCommandSecurity.js\n";
const policyEnd = "// ../../packages/terminal-security/dist/index.js\n";
code = replace(code, policyStart, policyEnd,
  policyStart +
  "const { evaluateTerminalCommandSecurity } = " +
  compile("packages/terminal-security/src/evaluateTerminalCommandSecurity.ts", undefined, {
    "shell-quote": "require_shell_quote()",
  }) + ";\nvar init_evaluateTerminalCommandSecurity = () => {};\n\n");

const defStart = "// ../../core/tools/definitions/runTerminalCommand.ts\n";
const defEnd = code.slice(code.indexOf(defStart) + defStart.length).match(/\n\/\/ [^\n]+\.ts\n/)[0].slice(1);
code = replace(code, defStart, defEnd,
  defStart + "var runTerminalCommandTool;\n" +
  "var init_runTerminalCommand = __esm({ 'pc73/runTerminalCommandTool'() {\n" +
  "init_builtIn(); init_dist21();\n" +
  "runTerminalCommandTool = " + compile("core/tools/definitions/runTerminalCommand.ts", undefined, {
    "@continuedev/terminal-security": "{ evaluateTerminalCommandSecurity }",
    "../builtIn": '{ BUILT_IN_GROUP_NAME, BuiltInToolNames: { RunTerminalCommand: "run_terminal_command" } }',
  }) + ".runTerminalCommandTool;\n}});\n\n");

const utilStart = "// ../../core/util/grepSearch.ts\n";
code = replace(code, utilStart, "var init_grepSearch2 = ",
  utilStart + "const { formatGrepSearchResults, limitGrepSearchResults, getGrepFileHeadings } = " +
  compile("core/util/grepSearch.ts") + ";\n");

const resolverStart = "// ../../core/util/pathResolver.ts\n";
const resolverEnd = "// ../../core/tools/policies/fileAccess.ts\n";
const resolver = between(code, resolverStart, resolverEnd);
const workspaceCheck = "  const { foundInDir } = findUriInDirs(uri, workspaceDirs);";
const relativeResult = "      isAbsolute: false,\n      isWithinWorkspace: true";
const windowsFile = 'process.platform === "win32" && ';
if (resolver.split(workspaceCheck).length !== 2 || resolver.split(relativeResult).length !== 2) {
  throw new Error("Unexpected workspace path resolver anchors");
}
code = replace(code, resolverStart, resolverEnd, resolver
  .replace(resolverStart, resolverStart + "const { isWindowsWorkspaceFile } = " +
    compile("core/util/windowsWorkspaceFile.ts") + ";\n")
  .replace(workspaceCheck, `  if (${windowsFile}uri.startsWith("file:")) {\n` +
    "    return await isWindowsWorkspaceFile(uri, workspaceDirs) && await ide.fileExists(uri);\n" +
    "  }\n" + workspaceCheck)
  .replace(relativeResult, "      isAbsolute: false,\n" +
    `      isWithinWorkspace: ${windowsFile}workspaceUri.startsWith("file:")\n` +
    "        ? await isUriWithinWorkspace(ide, workspaceUri)\n" +
    "        : true"));

const grepStart = "// ../../core/tools/implementations/grepSearch.ts\n";
const grepEnd = "// ../../core/tools/implementations/lsTool.ts\n";
code = replace(code, grepStart, grepEnd,
  grepStart + "var grepSearchImpl;\n" +
  "var init_grepSearch3 = __esm({ 'pc73/grepSearch'() {\n" +
  "init_errors11(); init_grepSearch2(); init_regexValidator(); init_parseArgs2();\n" +
  "grepSearchImpl = " + compile("core/tools/implementations/grepSearch.ts", undefined, {
    "../../util/grepSearch": "{ formatGrepSearchResults, getGrepFileHeadings }",
    "../../util/errors": '{ ContinueError, ContinueErrorReason: { SearchExecutionFailed: "search_execution_failed" } }',
    "../../util/regexValidator": "{ prepareQueryForRipgrep }",
    "../parseArgs": "{ getStringArg }",
  }) + ".grepSearchImpl;\n}});\n\n");

const terminalStart = "// ../../core/tools/implementations/runTerminalCommand.ts\n";
const terminalEnd = "// ../../core/deploy/constants.ts\n";
code = replace(code, terminalStart, terminalEnd,
  terminalStart + "const { assertLocalBrainTerminalBoundary } = " + compile("localBrainTerminalBoundary.ts") + ";\n" +
  "var runTerminalCommandImpl;\n" +
  "var init_runTerminalCommand2 = __esm({ 'pc73/runTerminalCommand'() {\n" +
  "init_parseArgs2(); init_errors11(); init_processTerminalStates();\n" +
  "runTerminalCommandImpl = " + compile("core/tools/implementations/runTerminalCommand.ts", undefined, {
    "../parseArgs": "{ getBooleanArg, getStringArg }",
    "../../../localBrainTerminalBoundary": "{ assertLocalBrainTerminalBoundary }",
    "../../util/terminalOutput": compile("core/util/terminalOutput.ts", undefined, {
      "./toolContextBudget": "pc73ToolBudget",
    }),
    "../../util/errors": '{ ContinueError, ContinueErrorReason: { CommandExecutionFailed: "command_execution_failed" } }',
    "../../util/processTerminalStates": "{ isProcessBackgrounded, markProcessAsRunning, removeBackgroundedProcess, removeRunningProcess, updateProcessOutput }",
  }) + ".runTerminalCommandImpl;\n}});\n" +
  "const { assertLocalBrainWritePathBoundary } = " + compile("localBrainMainBranchBoundary.ts") + ";\n" +
  "function requirePc73EditableFile(vscode, uri) {\n" +
  "  assertLocalBrainWritePathBoundary(uri.toString(), vscode.env.remoteName ? [] : (vscode.workspace.workspaceFolders ?? []).map(folder => folder.uri.toString()));\n}\n\n");

const ide = src("extensions/vscode/src/VsCodeIde.ts");
const methods = between(ide, "  async getToolReadVersion(", "  async showVirtualFile(") +
  between(ide, "  async getSearchResults(", "  async getProblems(");
const ideSource = "import vscode from 'vscode';\nimport { stat } from 'node:fs/promises';\nexport class Pc73IdeMethods {\n" + methods + "\n}";
const ideStart = "// src/VsCodeIde.ts\n";
code = code.replace(ideStart, ideStart + "const pc73Ide = " + compile("Pc73IdeMethods.ts", ideSource) + ".Pc73IdeMethods.prototype;\n");
code = replace(code, "      async writeFile(fileUri, contents2) {", "      async showVirtualFile(",
  "      async getToolReadVersion(fileUri) { return pc73Ide.getToolReadVersion.call(this, fileUri); }\n" +
  "      assertLocalBrainEditableFile(fileUri) { return pc73Ide.assertLocalBrainEditableFile.call(this, fileUri); }\n" +
  "      async writeFile(fileUri, contents2) { return pc73Ide.writeFile.call(this, fileUri, contents2); }\n" +
  "      async removeFile(fileUri) { return pc73Ide.removeFile.call(this, fileUri); }\n");
code = replace(code, "      async getSearchResults(query, maxResults) {", "      async getProblems(",
  "      async getSearchResults(query, maxResults) { return pc73Ide.getSearchResults.call(this, query, maxResults); }\n");

// VS Code's Apply and vertical diff routes edit the document directly.
const applyStart = "// src/apply/ApplyManager.ts\n";
const applyIndex = code.indexOf(applyStart);
if (applyIndex < 0) throw new Error("ApplyManager not found");
const applyCode = code.slice(applyIndex);
const originalContent = "        const originalFileContent = activeTextEditor.document.getText();";
const emptyEdit = "      async handleEmptyDocument(editor, text5, streamId, toolCallId) {\n";
if (applyCode.split(originalContent).length !== 2 || applyCode.split(emptyEdit).length !== 2) {
  throw new Error("Ambiguous ApplyManager edit anchors");
}
code = code.slice(0, applyIndex) + applyCode
  .replace(originalContent, "        requirePc73EditableFile(vscode37, activeTextEditor.document.uri);\n" + originalContent)
  .replace(emptyEdit, emptyEdit + "        requirePc73EditableFile(vscode37, editor.document.uri);\n");

const diffStart = "// src/diff/vertical/handler.ts\n";
const diffEnd = "// src/diff/vertical/manager.ts\n";
const diff = between(code, diffStart, diffEnd);
const diffEdits = /^(\s*)await this\.editor\.edit\(/gm;
if (Array.from(diff.matchAll(diffEdits)).length !== 5) throw new Error("Unexpected vertical diff edit count");
code = replace(code, diffStart, diffEnd, diff.replace(diffEdits, (_, indent) =>
  indent + "requirePc73EditableFile(vscode18, this.editor.document.uri);\n" + indent + "await this.editor.edit("));

const insertStart = 'this.onWebview("insertAtCursor", async (msg) => {';
const insertEnd = 'this.onWebview("edit/addCurrentSelection",';
const insert = between(code, insertStart, insertEnd);
const insertion = "          editor.edit((editBuilder) => {";
if (insert.split(insertion).length !== 2) throw new Error("Unexpected cursor insertion route");
code = replace(code, insertStart, insertEnd, insert.replace(insertion,
  "          requirePc73EditableFile(vscode39, editor.document.uri);\n" + insertion));

code = require("./apply-baseline.cjs")(code);

const chatCompilerStart = "function compileChatMessages({\n";
const chatCompilerEnd = "var LlamaEncoding, NonWorkerAsyncEncoder, ";
let chatCompiler = between(code, chatCompilerStart, chatCompilerEnd);
function replaceOnce(text, anchor, replacement) {
  if (text.split(anchor).length !== 2) throw new Error("Missing or ambiguous anchor: " + anchor);
  return text.replace(anchor, replacement);
}
const sequenceCount = "  let lastMessagesTokens = 0;\n  for (const msg of toolSequence) {\n    lastMessagesTokens += countChatMessageTokens(modelName, msg);\n  }\n";
chatCompiler = replaceOnce(chatCompiler, sequenceCount, "");
const userReserve = "  const reservedUserTokens = lastUserMessage && !toolSequence.includes(lastUserMessage) ? countChatMessageTokens(modelName, lastUserMessage) : 0;";
chatCompiler = replaceOnce(chatCompiler, "  inputTokensAvailable -= lastMessagesTokens;\n" + userReserve,
  userReserve + `
  const latestFrames = toolSequence.reduce((total, msg) => total +
    countChatMessageTokens(modelName, msg.role === "tool" ? { ...msg, content: "" } : msg), 0);
  const hasToolResults = toolSequence.some((msg) => msg.role === "tool") || msgsCopy.some((msg) => msg.role === "tool");
  const maxToolTokens = pc73ToolBudget.getToolOutputTokenBudget(contextLength, maxTokens);
  // PC73's local-qwen38 actor has a measured 32K physical window, while the
  // Continue client can be configured with a larger logical context.
  const planningContext = modelName === "local-qwen38" ? Math.min(contextLength, 32_768) : contextLength;
  const latestAllowance = Math.max(0, inputTokensAvailable - (contextLength - planningContext) -
    latestFrames - reservedUserTokens - Math.max(0, maxTokens - minOutputTokens));
  const toolStats = pc73ToolBudget.prepareToolMessages(msgsCopy, toolSequence, latestAllowance, maxToolTokens,
    (text) => countTokens(text, modelName));
  const lastMessagesTokens = toolSequence.reduce((total, msg) => total + countChatMessageTokens(modelName, msg), 0);
  inputTokensAvailable -= lastMessagesTokens;`);
const historyLoop = "  while (historyWithTokens.length > 0 && currentTotal > inputTokensAvailable) {";
chatCompiler = replaceOnce(chatCompiler, historyLoop, `  const historyTokensBefore = currentTotal;
  const historyLimit = hasToolResults ? Math.min(inputTokensAvailable, Math.max(0,
    Math.floor((planningContext - countingSafetyBuffer - maxTokens) * 0.75) -
    toolTokens - systemMsgTokens - lastMessagesTokens - reservedUserTokens)) : inputTokensAvailable;
  while (historyWithTokens.length > 0 && currentTotal > historyLimit) {`);
const historyMetrics = "  const contextPercentage = inputTokens / availableTokens;";
chatCompiler = replaceOnce(chatCompiler, historyMetrics, historyMetrics + `
  if (hasToolResults && (didPrune || toolStats.shortened || toolStats.duplicateReads)) {
    console.info("[LocalBrain Continue] context budget", JSON.stringify({
      contextLength, planningContext, historyTokensBefore, historyTokensAfter: currentTotal, inputTokens,
      toolResultsShortened: toolStats.shortened, duplicateReads: toolStats.duplicateReads
    }));
  }`);
code = replace(code, chatCompilerStart, chatCompilerEnd,
  "const pc73ToolBudget = " + compile("core/util/toolContextBudget.ts") + ";\n" + chatCompiler);

const handleToolStart = "      async handleToolCall(toolCall) {";
const handleToolEnd = "      async isItemTooBig(item) {";
let handleTool = between(code, handleToolStart, handleToolEnd);
handleTool = replaceOnce(handleTool, '        const onPartialOutput = (params) => {\n          this.messenger.send("toolCallPartialOutput", params);\n        };', `        const llm = config3.selectedModelByRole.chat;
        const count = (text) => countTokens(text, llm.model);
        const budget = pc73ToolBudget.getToolOutputTokenBudget(llm.contextLength, llm.completionOptions.maxTokens);
        const onPartialOutput = (params) => {
          this.messenger.send("toolCallPartialOutput", {
            ...params, contextItems: pc73ToolBudget.limitToolItems(params.contextItems, budget, count)
          });
        };`);
handleTool = replaceOnce(handleTool, "        return result2;", `        return {
          ...result2,
          contextItems: pc73ToolBudget.limitToolItems(result2.contextItems, budget, count),
          errorMessage: typeof result2.errorMessage === "string"
            ? pc73ToolBudget.limitToolText(result2.errorMessage, budget, count)
            : result2.errorMessage
        };`);
code = replace(code, handleToolStart, handleToolEnd, handleTool);

const readFileStart = "// ../../core/tools/implementations/readFile.ts\n";
const readFileEnd = "// ../../core/tools/implementations/readFileRange.ts\n";
let readFile = between(code, readFileStart, readFileEnd);
readFile = replaceOnce(readFile, readFileStart, readFileStart + "const { readUnchangedText } = " +
  compile("core/util/repeatedFileRead.ts") + ";\n");
readFile = replaceOnce(readFile, "      const content = await extras.ide.readFile(resolvedPath3.uri);",
  "      const content = resolvedPath3.isWithinWorkspace\n" +
  "        ? await readUnchangedText(extras.ide, resolvedPath3.uri)\n" +
  "        : await extras.ide.readFile(resolvedPath3.uri);");
// The shared result limiter handles files above the previous admission limit
// without tokenizing a whole large file before producing its preview.
readFile = replaceOnce(readFile, `      await throwIfFileExceedsHalfOfContext(
        resolvedPath3.displayPath,
        content,
        extras.config.selectedModelByRole.chat
      );
`, "");
code = replace(code, readFileStart, readFileEnd, readFile);

fs.writeFileSync(output, code);
console.log(output);
