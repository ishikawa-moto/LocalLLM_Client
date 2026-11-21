// Earlier LocalBrain edits already present in the PC73 release baseline.
// Match the original snippets before applying; keep these separate from the TypeScript build.
const hunks = require("../packaging/baseline-bundle-edits.json");
module.exports = function applyBaselineEdits(code) {
  for (const { before, after, atEnd } of hunks) {
    if (atEnd) {
      if (!code.endsWith(before)) throw new Error("Baseline bundle trailer changed");
      code = code.slice(0, -before.length) + after;
    } else {
      const index = code.indexOf(before);
      if (index < 0 || code.lastIndexOf(before) !== index) throw new Error("Baseline bundle anchor changed");
      code = code.slice(0, index) + after + code.slice(index + before.length);
    }
  }
  return code;
};
