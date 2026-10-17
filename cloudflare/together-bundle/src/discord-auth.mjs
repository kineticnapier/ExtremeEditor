import { completeDesktopHandoff } from "./desktop-handoff.mjs";
// Browser OAuth bootstrap only. Does not grant room privileges.
const CLIENT_ID = "1558905252282114178";
const CALLBACK_PATH = "/auth/discord/callback";
const encoder = new TextEncoder();
function randomToken() {
  const a = crypto.getRandomValues(new Uint8Array(32));
  return Array.from(a, b => b.toString(16).padStart(2, "0")).join("");
}
function cookies(req) {
  return Object.fromEntries((req.headers.get("Cookie") || "").split(";")
    .map(x => x.trim()).filter(x => x.includes("="))
    .map(x => [x.slice(0, x.indexOf("=")), x.slice(x.indexOf("=") + 1)]));
}
function response(body, status = 200, headers = {}) {
  return new Response(body, { status, headers: {
    "Cache-Control": "no-store", "Referrer-Policy": "no-referrer",
    "X-Content-Type-Options": "nosniff", ...headers
  } });
}
function cookie(name, value, age) {
  return `${name}=${value}; Path=/; HttpOnly; Secure; SameSite=Lax; Max-Age=${age}`;
}
function callbackUrl(url) {
  return `${url.origin}${CALLBACK_PATH}`;
}
async function sign(secret, payload) {
  const key = await crypto.subtle.importKey("raw", encoder.encode(secret),
    { name: "HMAC", hash: "SHA-256" }, false, ["sign"]);
  const sig = await crypto.subtle.sign("HMAC", key, encoder.encode(payload));
  return Array.from(new Uint8Array(sig), b => b.toString(16).padStart(2,"0")).join("");
}
async function verify(secret, payload, signature) {
  if (!/^[0-9a-f]{64}$/.test(signature)) return false;
  const expected = await sign(secret, payload);
  let v = 0;
  for (let i = 0; i < 64; i++) v |= signature.charCodeAt(i) ^ expected.charCodeAt(i);
  return v === 0;
}
async function encodeSession(secret, user) {
  const payload = JSON.stringify({
    id: user.id, username: user.username, global_name: user.global_name,
    avatar: user.avatar, exp: Math.floor(Date.now()/1000) + 3600
  });
  const hex = Array.from(encoder.encode(payload), b => b.toString(16).padStart(2,"0")).join("");
  return hex + "." + await sign(secret, hex);
}
async function decodeSession(secret, token) {
  if (!token || token.length > 3000) return null;
  const dot = token.lastIndexOf(".");
  if (dot <= 0 || dot > 2400) return null;
  const hex = token.slice(0,dot);
  if (!/^(?:[0-9a-f]{2})+$/.test(hex) || !await verify(secret,hex,token.slice(dot+1))) return null;
  try {
    const json = new TextDecoder().decode(Uint8Array.from(hex.match(/../g), x => parseInt(x,16)));
    const user = JSON.parse(json);
    return /^\d{16,22}$/.test(user.id) && Number.isSafeInteger(user.exp) &&
      user.exp > Date.now()/1000 ? user : null;
  } catch { return null; }
}
export async function handleDiscordAuth(request, env) {
  const url = new URL(request.url);
  if (!["/auth/discord/start", CALLBACK_PATH, "/auth/me", "/auth/logout"].includes(url.pathname))
    return null;
  if (!env.DISCORD_CLIENT_SECRET || !env.TOGETHER_BUNDLE_MASTER_KEY)
    return response("Discord authentication not configured", 503);
  if (request.method !== "GET" && !(request.method === "POST" && url.pathname === "/auth/logout"))
    return response("Method not allowed", 405);
  if (url.pathname === "/auth/discord/start") {
    const state = randomToken();
    const desktop = url.searchParams.get("desktop");
    if (desktop !== null && !/^[0-9a-f]{64}$/.test(desktop)) return response("Invalid login request",400);
    const authorize = new URL("https://discord.com/oauth2/authorize");
    authorize.search = new URLSearchParams({
      response_type: "code", client_id: CLIENT_ID, redirect_uri: callbackUrl(url),
      scope: "identify", state, prompt: "consent"
    }).toString();
    const headers=new Headers({Location:authorize.toString(),"Cache-Control":"no-store"});
    headers.append("Set-Cookie",cookie("ee_oauth_state",state,300));
    if(desktop) headers.append("Set-Cookie",cookie("ee_oauth_desktop",desktop,300));
    else headers.append("Set-Cookie",cookie("ee_oauth_desktop","",0));
    return new Response(null,{status:302,headers});
  }
  if (url.pathname === CALLBACK_PATH) {
    const c = cookies(request);
    const clear = cookie("ee_oauth_state","",0);
    const state = url.searchParams.get("state");
    const code = url.searchParams.get("code");
    if (!state || !/^[0-9a-f]{64}$/.test(state) || !c.ee_oauth_state ||
        !/^[0-9a-f]{64}$/.test(c.ee_oauth_state) ||
        !constantEqual(state,c.ee_oauth_state) ||
        !code || code.length > 2048)
      return response("Login cancelled or expired",400,{"Set-Cookie":clear});
    try {
      const token = await fetch("https://discord.com/api/v10/oauth2/token",{
        method:"POST",headers:{"Content-Type":"application/x-www-form-urlencoded"},
        body:new URLSearchParams({
          client_id:CLIENT_ID,client_secret:env.DISCORD_CLIENT_SECRET,
          grant_type:"authorization_code",code,redirect_uri:callbackUrl(url)
        })
      });
      if (!token.ok) return response("Discord login failed",502,{"Set-Cookie":clear});
      const auth = await token.json();
      if (typeof auth.access_token !== "string" || auth.token_type?.toLowerCase() !== "bearer")
        return response("Discord login failed",502,{"Set-Cookie":clear});
      const profile = await fetch("https://discord.com/api/v10/users/@me",{
        headers:{"Authorization":`Bearer ${auth.access_token}`}
      });
      if (!profile.ok) return response("Discord profile unavailable",502,{"Set-Cookie":clear});
      const user = await profile.json();
      if (!/^\d{16,22}$/.test(user.id) || typeof user.username !== "string")
        return response("Unexpected Discord identity",502,{"Set-Cookie":clear});
      if (c.ee_oauth_desktop) {
        const transferred = await completeDesktopHandoff(env,c.ee_oauth_desktop,user);
        if (!transferred) return response("Desktop login request expired",410,{"Set-Cookie":clear});
      }
      const session = await encodeSession(env.TOGETHER_BUNDLE_MASTER_KEY,user);
      const name = escapeHtml(user.global_name || user.username);
      const headers = new Headers({
        "Content-Type":"text/html; charset=utf-8",
        "Cache-Control":"no-store", "Referrer-Policy":"no-referrer",
        "Content-Security-Policy":"default-src 'none'; style-src 'unsafe-inline'"
      });
      headers.append("Set-Cookie",clear);
      headers.append("Set-Cookie",cookie("ee_oauth_desktop","",0));
      headers.append("Set-Cookie",cookie("ee_session",session,3600));
      return new Response(`<!doctype html><html lang="ja"><meta charset="utf-8"><title>ExtremeEditor Together</title><body><h2>Discord認証成功</h2><p>${name} としてログインしました。</p><p>ブラウザを閉じてExtremeEditorに戻ってください。</p></body></html>`,{status:200,headers});
    } catch {
      return response("Discord login temporarily unavailable",502,{"Set-Cookie":clear});
    }
  }
  if (url.pathname === "/auth/logout") {
    if (request.method !== "POST") return response("Method not allowed",405);
    const origin = request.headers.get("Origin");
    if (origin !== url.origin) return response("Forbidden",403);
    return response("Logged out",200,{"Set-Cookie":cookie("ee_session","",0)});
  }
  const user = await decodeSession(env.TOGETHER_BUNDLE_MASTER_KEY,cookies(request).ee_session);
  return response(JSON.stringify(user ? {
    id:user.id, username:user.username, global_name:user.global_name, avatar:user.avatar
  } : {authenticated:false}),user?200:401,{"Content-Type":"application/json"});
}
function constantEqual(a,b) {
  if (a.length !== b.length) return false;
  let v=0;for(let i=0;i<a.length;i++)v|=a.charCodeAt(i)^b.charCodeAt(i);
  return v===0;
}
function escapeHtml(v) {
  return String(v).replace(/[&<>"']/g,ch=>({
    "&":"&amp;","<":"&lt;",">":"&gt;",'"':"&quot;","'":"&#39;"
  })[ch]);
}
