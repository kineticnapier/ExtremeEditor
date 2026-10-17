import test from "node:test";
import assert from "node:assert/strict";
import { handleDiscordAuth } from "./discord-auth.mjs";

const origin = "https://extremeeditor-together-bundle.stopping-bypass.workers.dev";
const env = { DISCORD_CLIENT_SECRET: "test-secret-not-real",
  TOGETHER_BUNDLE_MASTER_KEY: "a".repeat(64) };
test("start redirects to Discord identify and issues secure state", async () => {
  const res = await handleDiscordAuth(new Request(origin + "/auth/discord/start"), env);
  assert.equal(res.status,302);
  const redirect = new URL(res.headers.get("location"));
  assert.equal(redirect.origin,"https://discord.com");
  assert.equal(redirect.searchParams.get("scope"),"identify");
  assert.equal(redirect.searchParams.get("client_id"),"1558905252282114178");
  assert.equal(redirect.searchParams.get("redirect_uri"),origin+"/auth/discord/callback");
  assert.match(res.headers.get("set-cookie"),/HttpOnly; Secure; SameSite=Lax/);
  assert.equal(redirect.searchParams.get("state").length,64);
});
test("callback rejects unsolicited state without reaching Discord", async () => {
  const res = await handleDiscordAuth(new Request(
    origin+"/auth/discord/callback?state=bad&code=test"),env);
  assert.equal(res.status,400);
  assert.match(res.headers.get("set-cookie"),/Max-Age=0/);
});
test("profile endpoint does not trust unverified cookie", async () => {
  const res = await handleDiscordAuth(new Request(origin+"/auth/me",{
    headers:{Cookie:"ee_session=garbage"}
  }),env);
  assert.equal(res.status,401);
  assert.deepEqual(await res.json(),{authenticated:false});
});
test("session cannot be used as a room authorization token", async () => {
  const res = await handleDiscordAuth(new Request(origin+"/rooms/abcdefghijkl/bundle"),env);
  assert.equal(res,null);
});
test("auth endpoints fail closed when not configured",async()=>{
  const res=await handleDiscordAuth(new Request(origin+"/auth/discord/start"),{
    TOGETHER_BUNDLE_MASTER_KEY:"a".repeat(64)
  });
  assert.equal(res.status,503);
});
test("logout requires same-origin POST",async()=>{
  const forbidden=await handleDiscordAuth(new Request(origin+"/auth/logout",{
    method:"POST",headers:{Origin:"https://evil.example"}
  }),env);
  assert.equal(forbidden.status,403);
  const res=await handleDiscordAuth(new Request(origin+"/auth/logout",{
    method:"POST",headers:{Origin:origin}
  }),env);
  assert.equal(res.status,200);
  assert.match(res.headers.get("set-cookie"),/ee_session=;.*Max-Age=0/);
});
