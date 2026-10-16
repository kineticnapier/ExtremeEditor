import { authorizeMember } from "./member-auth.mjs";
const LIMIT = 32 * 1024 * 1024;
const ROOM_RE = /^[a-zA-Z0-9_-]{12,80}$/;

export default {
  async fetch(request, env) {
    const url = new URL(request.url);
    const match = /^\/rooms\/([^/]+)\/(bundle|submissions\/([A-Za-z0-9_-]{1,64}))$/.exec(url.pathname);
    if (!match || !ROOM_RE.test(match[1])) return new Response("Not found", { status: 404 });
    if (request.method !== "GET" && request.method !== "PUT")
      return new Response("Method not allowed", { status: 405, headers: { Allow: "GET, PUT" } });
    // Derive a different bearer capability for every room. The master key is
    // never distributed to clients; a room capability does not unlock other rooms.
    if (!env.TOGETHER_BUNDLE_MASTER_KEY)
      return new Response("Unauthorized", { status: 401 });
    const identity = await authorizeMember(request, env.TOGETHER_BUNDLE_MASTER_KEY, match[1]);
    if (!identity) return new Response("Unauthorized", { status: 401 });
    // Until server-side Git/ownership validation exists, only the host can
    // publish a bundle. Guests can safely download for offline editing.
    if (request.method === "PUT" && (match[3] ? match[3] !== identity.member : identity.role !== "host"))
      return new Response("Forbidden", { status: 403 });
    const room = env.ROOMS.get(env.ROOMS.idFromName(match[1]));
    return room.fetch(request, match[3] ? "submission:" + match[3] : "current");
  }
};

// Capability format: lowercase hex HMAC-SHA256(master, "together-room-v1:" + room).
// No identity or read/write distinction yet; these tokens are room-wide.
export async function deriveRoomToken(master, room) {
  if (typeof master !== "string" || master.length < 32 || !ROOM_RE.test(room))
    throw new TypeError("A sufficiently long master key and valid room are required.");
  const encoder = new TextEncoder();
  const key = await crypto.subtle.importKey(
    "raw", encoder.encode(master), { name: "HMAC", hash: "SHA-256" }, false, ["sign"]);
  const bytes = new Uint8Array(await crypto.subtle.sign(
    "HMAC", key, encoder.encode("together-room-v1:" + room)));
  return Array.from(bytes, byte => byte.toString(16).padStart(2, "0")).join("");
}

async function verifyRoomToken(request, master, room) {
  const supplied = request.headers.get("Authorization") || "";
  // Exact 64-hex shape; reject malformed values before expensive HMAC.
  if (!/^Bearer [0-9a-f]{64}$/.test(supplied)) return false;
  const expected = "Bearer " + await deriveRoomToken(master, room);
  let difference = 0;
  for (let i = 0; i < supplied.length; i++)
    difference |= supplied.charCodeAt(i) ^ expected.charCodeAt(i);
  return difference === 0;
}

export class BundleRoom {
  constructor(state, env) {
    this.state = state;
    this.bucket = env.BUNDLES;
  }

  async fetch(request, storageKey = "current") {
    if (request.method === "GET") {
      const current = await this.state.storage.get(storageKey);
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
      const current = await this.state.storage.get(storageKey);
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
        await this.state.storage.put(storageKey, { key, version });
      } catch (error) {
        await this.bucket.delete(key).catch(() => {});
        throw error;
      }
      // Old objects may be garbage collected separately; never break the newly committed pointer.
      return new Response(null, { status: 200, headers: { ETag: '"' + version + '"' } });
    });
  }
}
