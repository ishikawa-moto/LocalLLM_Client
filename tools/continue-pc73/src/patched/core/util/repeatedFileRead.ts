// Modified for LocalBrain PC73; see NOTICE.
type Version = { source: object | null; revision: string };
type Reader = {
  readFile(uri: string): Promise<string>;
  getToolReadVersion?(uri: string): Promise<Version | undefined>;
};

const reads = new WeakMap<Reader, Map<string, { version: Version; content: string }>>();

function sameVersion(before: Version, after: Version | undefined): boolean {
  return before.source === after?.source && before.revision === after?.revision;
}

export async function readUnchangedText(ide: Reader, uri: string): Promise<string> {
  const before = await ide.getToolReadVersion?.(uri);
  if (!before) return ide.readFile(uri);

  let cache = reads.get(ide);
  if (!cache) reads.set(ide, cache = new Map());
  const saved = cache.get(uri);
  if (saved && sameVersion(saved.version, before)) return saved.content;

  cache.delete(uri);
  const content = await ide.readFile(uri);
  if (content && Buffer.byteLength(content) <= 8192 && sameVersion(before, await ide.getToolReadVersion?.(uri))) {
    if (cache.size >= 24) cache.delete(cache.keys().next().value!);
    cache.set(uri, { version: before, content });
  }
  return content;
}
