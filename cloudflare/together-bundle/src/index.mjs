const LIMIT = 32 * 1024 * 1024;
const ROOM_RE = /^[a-zA-Z0-9_-]{12,80}$/;

export default {
  async fetch(request, env) {
    const url = new URL(request.url);
    const match = /^\/rooms\/([^/]+)\/bundle$/.exec(url.pathname);
    if (!match || !ROOM_RE.test(match[1])) return new Response("Not found", { status: 404 });
    if (request.method !== "GET" && request.method !== "PUT")
      return new Response("Method not allowed", { status: 405, headers: { Allow: "GET, PUT" } });
    // Prototype project-wide token. Replace with per-room participant credentials before public launch.
    if (!env.TOGETHER_BUNDLE_TOKEN || !(await tokenMatches(request, env.TOGETHER_BUNDLE_TOKEN)))
      return new Response("Unauthorized", { status: 401 });
    const room = env.ROOMS.get(env.ROOMS.idFromName(match[1]));
    return room.fetch(request);
  }
};

async function tokenMatches(request, expected) {
  const supplied = request.headers.get("Authorization") || "";
  const actual = "Bearer " + expected;
  const encoder = new TextEncoder();
  const a = encoder.encode(supplied);
  const b = encoder.encode(actual);
  if (a.length !== b.length) return false;
  let difference = 0;
  for (let i = 0; i < a.length; i++) difference |= a[i] ^ b[i];
  return difference === 0;
}

export class BundleRoom {
  constructor(state, env) {
    this.state = state;
    this.bucket = env.BUNDLES;
  }

  async fetch(request) {
    if (request.method === "GET") {
      const current = await this.state.storage.get("current");
      if (!current) return new Response("Not found", { status: 404 });
      const object = await this.bucket.get(current.key);
      if (!object) return new Response("Bundle unavailable", { status: 503 });
      return new Response(object.body, {
        headers: {
          "Content-Type": "application/octet-stream",
          "Content-Length": String(object.size),
          "ETag": '"' + current.version + '"',
          "Cache-Control": "no-store"
        }
      });
    }
    if (request.method !== "PUT") return new Response("Method not allowed", { status: 405 });
    if (request.headers.get("Content-Type")?.split(";")[0] !== "application/octet-stream")
      return new Response("Unsupported media type", { status: 415 });
    const size = Number(request.headers.get("Content-Length"));
    if (Number.isFinite(size) && size > LIMIT)
      return new Response("Bundle too large", { status: 413 });
    // Serialize conditional writes across all clients of this room.
    return this.state.blockConcurrencyWhile(async () => {
      const current = await this.state.storage.get("current");
      const ifMatch = request.headers.get("If-Match");
      const ifNoneMatch = request.headers.get("If-None-Match");
      const eligible = current
        ? ifMatch === '"' + current.version + '"'
        : ifNoneMatch === "*";
      if (!eligible) return new Response("Stale version", { status: 412 });
      const buffer = await request.arrayBuffer();
      if (buffer.byteLength > LIMIT) return new Response("Bundle too large", { status: 413 });
      if (buffer.byteLength < 16) return new Response("Invalid bundle", { status: 400 });
      const header = new TextDecoder().decode(buffer.slice(0, 16));
      if (!header.startsWith("# v2 git bundle") && !header.startsWith("# v3 git bundle"))
        return new Response("Invalid Git bundle header", { status: 400 });
      const version = crypto.randomUUID();
      const key = "bundles/" + this.state.id.toString() + "/" + version + ".bundle";
      await this.bucket.put(key, buffer, { httpMetadata: { contentType: "application/octet-stream" } });
      try {
        await this.state.storage.put("current", { key, version });
      } catch (error) {
        await this.bucket.delete(key).catch(() => {});
        throw error;
      }
      // Old objects may be garbage collected separately; never break the newly committed pointer.
      return new Response(null, { status: 200, headers: { ETag: '"' + version + '"' } });
    });
  }
}
