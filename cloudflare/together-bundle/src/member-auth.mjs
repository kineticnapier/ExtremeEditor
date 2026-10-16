const ROOM_RE = /^[a-zA-Z0-9_-]{12,80}$/;
const MEMBER_RE = /^[a-zA-Z0-9_-]{1,64}$/;
const roles = new Set(["reader", "host"]);
const encoder = new TextEncoder();
function hex(bytes) { return [...bytes].map(b => b.toString(16).padStart(2, "0")).join(""); }
async function hmac(master, payload) {
  if (typeof master !== "string" || master.length < 32)
    throw new TypeError("The server master key must contain at least 32 characters.");
  const key = await crypto.subtle.importKey("raw", encoder.encode(master),
    { name: "HMAC", hash: "SHA-256" }, false, ["sign", "verify"]);
  return hex(new Uint8Array(await crypto.subtle.sign("HMAC", key, encoder.encode(payload))));
}

/** Deliberately limited prototype: readers download; only host publishes.
 * There is no server-side semantic validation of guest-written Git bundles yet.
 * Payload is ASCII and delimiter-safe, making signing unambiguous.
 */
export async function issueMemberToken(master, room, member, role, expiresAt) {
  if (!ROOM_RE.test(room) || !MEMBER_RE.test(member) || !roles.has(role) ||
      !Number.isSafeInteger(expiresAt) || expiresAt <= 0)
    throw new TypeError("Invalid room, member, role or expiration.");
  const payload = `v1.${room}.${member}.${role}.${expiresAt}`;
  return payload + "." + await hmac(master, payload);
}

export async function authorizeMember(request, master, room) {
  const match = /^Bearer (v1\.[A-Za-z0-9_.-]{1,256}\.[0-9a-f]{64})$/.exec(
    request.headers.get("Authorization") || "");
  if (!match) return null;
  const parts = match[1].split(".");
  if (parts.length !== 6) return null;
  const [version, tokenRoom, member, role, expiry, signature] = parts;
  const expiresAt = Number(expiry);
  if (version !== "v1" || tokenRoom !== room || !ROOM_RE.test(tokenRoom) ||
      !MEMBER_RE.test(member) || !roles.has(role) ||
      !/^[1-9][0-9]*$/.test(expiry) || !Number.isSafeInteger(expiresAt) ||
      Math.floor(Date.now() / 1000) >= expiresAt) return null;
  const expected = await hmac(master, parts.slice(0, 5).join("."));
  let difference = 0;
  for (let i = 0; i < 64; ++i)
    difference |= signature.charCodeAt(i) ^ expected.charCodeAt(i);
  return difference === 0 ? { member, role } : null;
}
