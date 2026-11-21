// Match the packaging of the verified release. This is static metadata, not a user's profile.
const fs = require("node:fs");
const file = process.argv[2];
const manifest = JSON.parse(fs.readFileSync(file, "utf8"));
if (manifest.name !== "continue" || manifest.version !== "2.0.0" || manifest.main !== "./out/extension.js") {
  throw new Error("Unexpected extension manifest");
}
manifest.__metadata = { installedTimestamp: 1795157263354, targetPlatform: "undefined", size: 240920248 };
fs.writeFileSync(file, JSON.stringify(manifest, null, "\t"));
