import test from "node:test";
import assert from "node:assert/strict";
import worker, { BundleRoom } from "../src/index.mjs";

function fakeEnvironment() {
  const objects = new Map();
  const bucket = {
    async get(key) { return objects.get(key) ?? null; },
    async put(key, data) {
      const bytes = new Uint8Array(data);
      objects.set(key, { body: bytes, size: bytes.byteLength });
    },
    async delete(key) { objects.delete(key); }
  };
  const values = new Map();
  const state = {
    id: { toString: () => "test-do" },
    storage: {
      async get(key) { return values.get(key); },
      async put(key, value) { values.set(key, value); }
    },
    async blockConcurrencyWhile(callback) { return callback(); }
  };
  const room = new BundleRoom(state, { BUNDLES: bucket });
  return {
    TOGETHER_BUNDLE_TOKEN: "private-token",
    ROOMS: { idFromName: name => name, get: () => room }
  };
}

const url = "https://example.invalid/rooms/exampleRoom123456/bundle";
const bundle = new TextEncoder().encode("# v2 git bundle\nabcdef0123456789\n");
const auth = { Authorization: "Bearer private-token" };

test("conditional creation, fetch and stale update rejection", async () => {
  const env = fakeEnvironment();
  const send = (method, headers = {}, body) => worker.fetch(
    new Request(url, { method, headers: { ...auth, ...headers }, body }), env);
  assert.equal((await send("GET")).status, 404);
  assert.equal((await send("PUT", { "Content-Type": "application/octet-stream",
    "If-None-Match": "*" }, bundle)).status, 200);
  const get = await send("GET");
  assert.equal(get.status, 200);
  assert.deepEqual(new Uint8Array(await get.arrayBuffer()), bundle);
  const etag = get.headers.get("ETag");
  assert.ok(etag?.startsWith('"'));
  assert.equal((await send("PUT", { "Content-Type": "application/octet-stream",
    "If-Match": '"stale"' }, bundle)).status, 412);
  assert.equal((await send("PUT", { "Content-Type": "application/octet-stream",
    "If-Match": etag }, bundle)).status, 200);
  assert.notEqual((await send("GET")).headers.get("ETag"), etag);
});

test("reject unauthenticated, invalid room, and oversized requests", async () => {
  const env = fakeEnvironment();
  assert.equal((await worker.fetch(new Request(url), env)).status, 401);
  assert.equal((await worker.fetch(new Request(
    "https://example.invalid/rooms/a/bundle", { headers: auth }), env)).status, 404);
  assert.equal((await worker.fetch(new Request(url, {
    method: "PUT", headers: { ...auth, "If-None-Match": "*",
      "Content-Type": "application/octet-stream" },
    body: new Uint8Array(32 * 1024 * 1024 + 1)
  }), env)).status, 413);
});
