# ExtremeEditor Together HTTP bundle relay (prototype)

This is an optional Cloudflare Workers + Durable Objects + R2 endpoint for the
`HttpBundleTimeFirstSegmentRemote` .NET transport. It is not deployed by this repository.

The Worker exposes `GET /rooms/<random-room-id>/bundle` and
`PUT /rooms/<random-room-id>/bundle`, requires
`Authorization: Bearer <expiring-member-token>`, and returns strong ETags.
Create requires `If-None-Match: *`; updates require the last fetched
`If-Match`. A room Durable Object serializes updates and stores an immutable
R2 object before publishing a new room pointer. Competing stale writes return 412.

## One-command local two-client E2E test (Windows)

From the ExtremeEditor repository root, after installing Wrangler with
`npm install` once in `cloudflare/together-bundle`:

```powershell
.\run-together-test.ps1 -E2E
```

This builds **Core.Tests** (not WPF), starts local Wrangler, generates an
isolated random test room plus separate `host` / `guest` tokens and exercises
the real HTTP API without clicking the GUI. Two independent local Git workspaces
simulate the two clients. The test verifies:

- Initial canonical publish, host trusted ref fetch, guest clone and matching audio identity
- Guest submits their segment while host edits its own segment
- Host downloads, validates and semantically merges the guest proposal
- Host publishes the merged result and guest receives both edits
- Guest cannot overwrite canonical or submit to the host's proposal slot
- Unauthorized modification of the host's segment is rejected by client validation

No real songs are uploaded: a temporary synthetic audio-identity file and
sample-level chart fixture are generated for the test. All temporary test
workspaces are removed on exit. The test launcher restores an existing
`.dev.vars` file and prior process environment values.

For a previously built test executable, add `-NoBuild`. To avoid conflicts
with another running local Wrangler, use `-Port 8791` or stop the other instance.

The normal `.\run-together-test.ps1` (without `-E2E`) still starts the WPF
application with development-only Cloudflare fields prefilled.

This test does not verify WPF GUI interaction or supply production server-side
Git authorization. A successful E2E run is **not** a production security audit.

## Discord OAuth2 browser bootstrap (Stage A)

The existing Discord Application uses Client ID `1558905252282114178`.
Configure the following exact redirect URI in Discord Developer Portal:

`https://extremeeditor-together-bundle.stopping-bypass.workers.dev/auth/discord/callback`

Store the OAuth2 client secret exclusively in the Worker:

```powershell
npx wrangler secret put DISCORD_CLIENT_SECRET
```

After running `npm test` and `npx wrangler deploy`, visit:

`https://extremeeditor-together-bundle.stopping-bypass.workers.dev/auth/discord/start`

This starts a browser authorization-code flow scoped to `identify` and sets
short-lived HttpOnly Secure SameSite=Lax cookies. `GET /auth/me` returns
the authenticated Discord user's public profile; `POST /auth/logout` clears
the browser session (same-origin only).

**Stage A boundary:** Browser verification is not WPF login yet. No OAuth
credential, cookie or Discord identity grants access to existing
`/rooms/:id/bundle` or `/rooms/:id/submissions/:member` endpoints.
Those continue to use the current room bearer capability until the
room-membership and desktop session designs are implemented. Do not distribute
Discord OAuth secrets or the room master key to desktop clients. Avoid creating
production rooms based solely on OAuth login.

## Manual setup

1. Create an R2 bucket named `extremeeditor-together-bundles`.
2. In this directory, run `npm install`.
3. Generate a long random **master key** and set it using
   `npx wrangler secret put TOGETHER_BUNDLE_MASTER_KEY`.
   Keep the master key outside this repository; never distribute it to participants.
   The key must be at least 32 characters.
4. Generate member tokens using `node scripts/room-token.mjs <room-id> <member-id> <reader|host> [hours]`
   with `TOGETHER_BUNDLE_MASTER_KEY` present in your environment.
   Give only that member token to its intended participant. Readers can fetch but cannot publish. Only the host can publish until server-side semantic validation is implemented.
5. Run `npx wrangler deploy`. No production deploy is automatic.
6. Use a cryptographically random room ID (12–80 URL-safe characters), HTTPS
   Worker URL, and the matching member bearer token with the .NET transport.

## Limitations / safety

**Not production-ready.** This version provides HMAC-authenticated per-room, per-member, expiring bearer tokens with **reader** and **host** roles. It does not support revocation before token expiry, guest publishing, per-segment authorization, object garbage collection, abuse quotas or account recovery.
Neither the master key nor member tokens may be distributed in public app builds.
The server treats the bundle as opaque bytes and only checks its header;
it **cannot prove that no licensed audio or other unintended file is inside**.
Server-side encrypted data, participant identity, room authorization, and
Git tree allowlisting require a separate review. Keep the endpoint private
while developing.

The .NET client rejects non-fast-forward pushes and validates time-first
snapshots on fetch. R2 may retain previous immutable bundle versions.

## Unconfirmed participant submissions

`GET /rooms/<room-id>/submissions/<member-id>` downloads that member's
latest proposed Git bundle. Any authenticated member of the room may read
a proposal. `PUT` to the same endpoint requires the **matching member's**
token and uses the same conditional ETag contract as the canonical bundle.
The canonical `/bundle` endpoint remains host-write-only.

**Submissions are untrusted proposals, not accepted chart edits.** The Worker
does not inspect Git trees, validate owned segments, verify the claimed author
inside commits, or advance the canonical project. Clients must verify and
semantically merge submissions before use. Do not automatically execute,
checkout, or promote downloaded content as an authorized edit.
