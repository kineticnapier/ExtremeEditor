import test from "node:test";
import assert from "node:assert/strict";
import worker, { BundleRoom } from "../src/index.mjs";
import { issueMemberToken } from "../src/member-auth.mjs";

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
    TOGETHER_BUNDLE_MASTER_KEY: "development-only-master-key-32-bytes-long",
    ROOMS: { idFromName: name => name, get: () => room }
  };
}

const url = "https://example.invalid/rooms/exampleRoom123456/bundle";
const bundle = new TextEncoder().encode("# v2 git bundle\nabcdef0123456789\n");
const master = "development-only-master-key-32-bytes-long";
const expiry = Math.floor(Date.now() / 1000) + 3600;
const auth = async (room = "exampleRoom123456", role = "host", member = "owner") =>
  ({ Authorization: "Bearer " + await issueMemberToken(master, room, member, role, expiry) });

test("conditional creation, fetch and stale update rejection", async () => {
  const env = fakeEnvironment();
  const send = async (method, headers = {}, body) => worker.fetch(
    new Request(url, { method, headers: { ...await auth(), ...headers }, body }), env);
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
    "https://example.invalid/rooms/a/bundle", { headers: await auth() }), env)).status, 404);
  assert.equal((await worker.fetch(new Request(url, {
    method: "PUT", headers: { ...await auth(), "If-None-Match": "*",
      "Content-Type": "application/octet-stream" },
    body: new Uint8Array(32 * 1024 * 1024 + 1)
  }), env)).status, 413);
});

test("member credentials enforce room, role, expiry and signature", async () => {
  const env = fakeEnvironment();
  const otherRoom = "https://example.invalid/rooms/differentRoom56789/bundle";
  assert.equal((await worker.fetch(new Request(otherRoom, {
    headers: await auth()
  }), env)).status, 401);
  assert.equal((await worker.fetch(new Request(otherRoom, {
    headers: await auth("differentRoom56789", "reader", "guest")
  }), env)).status, 404);
  assert.equal((await worker.fetch(new Request(url, {
    method: "PUT", headers: { ...await auth("exampleRoom123456", "reader", "guest"),
      "Content-Type": "application/octet-stream", "If-None-Match": "*" }, body: bundle
  }), env)).status, 403);
  assert.equal((await worker.fetch(new Request(url, {
    headers: { Authorization: "Bearer " + await issueMemberToken(
      master, "exampleRoom123456", "guest", "reader",
      Math.floor(Date.now() / 1000) - 10) }
  }), env)).status, 401);
  const token = (await auth()).Authorization;
  assert.equal((await worker.fetch(new Request(url, {
    headers: { Authorization: token.slice(0, -1) + (token.endsWith("0") ? "1" : "0") }
  }), env)).status, 401);
});

test("participant submissions remain isolated from canonical and other members", async () => {
  const env = fakeEnvironment();
  const endpoint = "https://example.invalid/rooms/exampleRoom123456/submissions/";
  const alice = await auth("exampleRoom123456", "reader", "alice");
  const bob = await auth("exampleRoom123456", "reader", "bob");
  const upload = await worker.fetch(new Request(endpoint + "alice", {
    method: "PUT", headers: { ...alice, "If-None-Match": "*",
      "Content-Type": "application/octet-stream" }, body: bundle
  }), env);
  assert.equal(upload.status, 200);
  assert.equal((await worker.fetch(new Request(endpoint + "bob", {
    method: "PUT", headers: { ...alice, "If-None-Match": "*",
      "Content-Type": "application/octet-stream" }, body: bundle
  }), env)).status, 403);
  assert.equal((await worker.fetch(new Request(endpoint + "alice", {
    headers: bob
  }), env)).status, 200);
  assert.equal((await worker.fetch(new Request(url, { headers: alice }), env)).status, 404);
  assert.equal((await worker.fetch(new Request(endpoint + "alice", {
    method: "PUT", headers: { ...alice, "If-None-Match": "*",
      "Content-Type": "application/octet-stream" }, body: bundle
  }), env)).status, 412);
});
