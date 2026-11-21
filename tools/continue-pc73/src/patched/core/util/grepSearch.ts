// Modified for LocalBrain PC73; see NOTICE.
// File headings emitted by ripgrep --heading on Windows and Unix.
export function getGrepFileHeadings(results: string) {
  return Array.from(results.matchAll(/^\.[\\/](.+)\r?$/gm));
}

export function limitGrepSearchResults(results: string, maxFiles?: number): string {
  if (!maxFiles) return results;
  const extraFile = getGrepFileHeadings(results)[maxFiles];
  return extraFile ? results.slice(0, extraFile.index).trimEnd() : results;
}

export function formatGrepSearchResults(results: string, maxChars?: number): {
  formatted: string;
  numResults: number;
  truncated: boolean;
} {
  const normalized = results.replace(/\r\n/g, "\n");
  const files = getGrepFileHeadings(normalized);
  const blocks: string[] = [];
  for (const [index, heading] of files.entries()) {
    const start = heading.index! + heading[0].length;
    const end = files[index + 1]?.index ?? normalized.length;
    const lines = normalized.slice(start, end).split("\n").filter((line) => line === "--" || /^\d+[:-]/.test(line));
    if (!lines.some((line) => /^\d+:/.test(line))) continue;
    blocks.push([heading[0], ...lines.map((line) => line === "--" ? line : "  " + line)].join("\n"));
  }
  const output = blocks.join("\n");
  const truncated = maxChars !== undefined && output.length > maxChars;
  return {
    formatted: truncated ? output.slice(0, maxChars) : output,
    numResults: blocks.length,
    truncated,
  };
}
