# ExtremeEditor Together HTTP bundle relay (prototype)

This is an optional Cloudflare Workers + Durable Objects + R2 endpoint for the
`HttpBundleTimeFirstSegmentRemote` .NET transport. It is not deployed by this repository.

The Worker exposes `GET /rooms/<random-room-id>/bundle` and
`PUT /rooms/<random-room-id>/bundle`, requires
`Authorization: Bearer <room-capability>`, and returns strong ETags.
Create requires `If-None-Match: *`; updates require the last fetched
`If-Match`. A room Durable Object serializes updates and stores an immutable
R2 object before publishing a new room pointer. Competing stale writes return 412.

## Manual setup

1. Create an R2 bucket named `extremeeditor-together-bundles`.
2. In this directory, run `npm install`.
3. Generate a long random **master key** and set it using
   `npx wrangler secret put TOGETHER_BUNDLE_MASTER_KEY`.
   Keep the master key outside this repository; never distribute it to participants.
   The key must be at least 32 characters.
4. Derive a room-specific token using the CLI script `node scripts/room-token.mjs <room-id>`
   with `TOGETHER_BUNDLE_MASTER_KEY` present in your environment.
   Give only the room token to authorized collaborators.
5. Run `npx wrangler deploy`. No production deploy is automatic.
6. Use a cryptographically random room ID (12–80 URL-safe characters), HTTPS
   Worker URL, and matching bearer token with the .NET transport.

## Limitations / safety

**Not production-ready.** This version derives a separate token per room from a deployment-wide master key,
but has **no individual participant credentials or read/write roles**, has no room expiry, object garbage
collection, abuse quotas, account recovery, or per-participant permissions.
Neither the master key nor room tokens may be distributed in public app builds.
The server treats the bundle as opaque bytes and only checks its header;
it **cannot prove that no licensed audio or other unintended file is inside**.
Server-side encrypted data, participant identity, room authorization, and
Git tree allowlisting require a separate review. Keep the endpoint private
while developing.

The .NET client rejects non-fast-forward pushes and validates time-first
snapshots on fetch. R2 may retain previous immutable bundle versions.
