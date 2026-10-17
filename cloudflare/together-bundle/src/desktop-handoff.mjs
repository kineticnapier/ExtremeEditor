// One-time browser-to-desktop identity handoff; no room permissions are granted.
const valid = v => typeof v === "string" && /^[0-9a-f]{64}$/.test(v);
const json = (data,status=200) => new Response(JSON.stringify(data),{status,headers:{"Content-Type":"application/json","Cache-Control":"no-store"}});
function secret() { return Array.from(crypto.getRandomValues(new Uint8Array(32)), b=>b.toString(16).padStart(2,"0")).join(""); }
async function hash(value) {
  const bytes = await crypto.subtle.digest("SHA-256",new TextEncoder().encode(value));
  return Array.from(new Uint8Array(bytes),b=>b.toString(16).padStart(2,"0")).join("");
}
export async function handleDesktopHandoff(request,env) {
  const url = new URL(request.url);
  if (!["/auth/desktop/start","/auth/desktop/status"].includes(url.pathname)) return null;
  if (request.method !== "POST") return json({error:"method_not_allowed"},405);
  if (!env.ROOMS || !env.DISCORD_CLIENT_SECRET) return json({error:"unavailable"},503);
  const stub = env.ROOMS.get(env.ROOMS.idFromName("desktop-auth-v1"));
  if (url.pathname === "/auth/desktop/start") {
    const id=secret(), verifier=secret();
    const result=await stub.fetch("https://internal.invalid/_desktop/create",{
      method:"POST",body:JSON.stringify({id,verifierHash:await hash(verifier)})
    });
    if (!result.ok) return json({error:"unavailable"},503);
    return json({id,verifier,login_url:url.origin+"/auth/discord/start?desktop="+id,expires_in:300});
  }
  let data;
  try { data=await request.json(); } catch { return json({error:"invalid_request"},400); }
  if (!valid(data?.id) || !valid(data?.verifier)) return json({error:"invalid_request"},400);
  return stub.fetch("https://internal.invalid/_desktop/poll",{
    method:"POST",body:JSON.stringify({id:data.id,verifierHash:await hash(data.verifier)})
  });
}
export async function completeDesktopHandoff(env,id,user) {
  if (!valid(id) || !env.ROOMS) return false;
  const stub=env.ROOMS.get(env.ROOMS.idFromName("desktop-auth-v1"));
  const response=await stub.fetch("https://internal.invalid/_desktop/complete",{
    method:"POST",body:JSON.stringify({id,user:{
      id:user.id,username:user.username,global_name:user.global_name,avatar:user.avatar
    }})
  });
  return response.ok;
}
export async function handleDesktopHandoffStorage(request,storage) {
  const path=new URL(request.url).pathname;
  if (!["/_desktop/create","/_desktop/poll","/_desktop/complete"].includes(path) ||
      request.method!=="POST") return null;
  let data;
  try { data=await request.json(); } catch { return json({error:"invalid_request"},400); }
  if (!valid(data?.id)) return json({error:"invalid_request"},400);
  const key="desktop:"+data.id;
  const now=Date.now();
  if(path==="/_desktop/create") {
    if (!valid(data.verifierHash)) return json({error:"invalid_request"},400);
    await storage.put(key,{verifierHash:data.verifierHash,expiry:now+300000,completed:false});
    return json({ok:true});
  }
  const entry=await storage.get(key);
  if (!entry || entry.expiry<=now) {
    if(entry) await storage.delete(key);
    return json({error:"expired"},410);
  }
  if(path==="/_desktop/complete"){
    if(entry.completed || !/^\d{16,22}$/.test(data.user?.id||"")) return json({error:"invalid_request"},400);
    await storage.put(key,{...entry,completed:true,user:data.user});
    return json({ok:true});
  }
  if(data.verifierHash!==entry.verifierHash) return json({error:"unauthorized"},401);
  if(!entry.completed) return json({status:"pending"},202);
  await storage.delete(key);
  return json({status:"complete",user:entry.user});
}
