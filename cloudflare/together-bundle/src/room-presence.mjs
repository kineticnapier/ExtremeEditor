// Stage B presence. A valid existing room bearer AND a server-issued desktop session are required.
const json=(v,status=200)=>new Response(JSON.stringify(v),{status,headers:{"Content-Type":"application/json","Cache-Control":"no-store"}});
const roomRe=/^[A-Za-z0-9_-]{12,80}$/;
const sessionRe=/^[0-9a-f]{64}$/;
const LIVE_MS=75000;
async function sha(s) {
  const bytes=await crypto.subtle.digest("SHA-256",new TextEncoder().encode(s));
  return Array.from(new Uint8Array(bytes),b=>b.toString(16).padStart(2,"0")).join("");
}
export async function handleRoomPresence(request,env,authorizeMember) {
  const u=new URL(request.url);
  const m=/^\/rooms\/([^/]+)\/presence$/.exec(u.pathname);
  if(!m)return null;
  if(!roomRe.test(m[1]))return json({error:"not_found"},404);
  if(request.method!=="POST")return json({error:"method_not_allowed"},405);
  if(!env.TOGETHER_BUNDLE_MASTER_KEY||!env.ROOMS)return json({error:"unavailable"},503);
  const identity=await authorizeMember(request,env.TOGETHER_BUNDLE_MASTER_KEY,m[1]);
  if(!identity)return json({error:"unauthorized"},401);
  let payload;
  try{payload=await request.json();}catch{return json({error:"invalid_request"},400);}
  if(!sessionRe.test(payload?.session||""))return json({error:"invalid_request"},400);
  const auth=env.ROOMS.get(env.ROOMS.idFromName("desktop-auth-v1"));
  const verified=await auth.fetch("https://internal.invalid/_desktop/me",{
    method:"POST",body:JSON.stringify({sessionHash:await sha(payload.session)})
  });
  if(!verified.ok)return json({error:"discord_session_expired"},401);
  const profile=(await verified.json()).user;
  const room=env.ROOMS.get(env.ROOMS.idFromName(m[1]));
  return room.fetch("https://internal.invalid/_presence/touch",{
    method:"POST",body:JSON.stringify({
      member:identity.member,role:identity.role,user:profile
    })
  });
}
export async function handleRoomPresenceStorage(request,storage) {
  if(new URL(request.url).pathname!=="/_presence/touch")return null;
  if(request.method!=="POST")return json({error:"method_not_allowed"},405);
  let p;
  try{p=await request.json();}catch{return json({error:"invalid_request"},400);}
  if(!/^[A-Za-z0-9_-]{1,64}$/.test(p?.member||"")||
    !["reader","host"].includes(p?.role)||
    !/^\d{16,22}$/.test(p?.user?.id||""))return json({error:"invalid_request"},400);
  const now=Date.now();
  // The member capability is the transport authority; Discord ID is verified separately.
  // A recent member slot cannot silently be claimed by a different Discord user.
  const key="presence:"+p.member;
  const prior=await storage.get(key);
  if(prior && prior.expires>now && prior.user.id!==p.user.id)
    return json({error:"member_claimed"},409);
  const user={id:p.user.id,username:String(p.user.username||"").slice(0,80),
    global_name:typeof p.user.global_name==="string"?p.user.global_name.slice(0,80):null,
    avatar:typeof p.user.avatar==="string"?p.user.avatar.slice(0,128):null};
  await storage.put(key,{member:p.member,role:p.role,user,expires:now+LIVE_MS});
  const all=await storage.list({prefix:"presence:"});
  const members=[];
  for(const [k,v] of all){
    if(v.expires<=now){await storage.delete(k);continue;}
    members.push({member:v.member,role:v.role,user:v.user,online:true});
  }
  members.sort((a,b)=>a.member.localeCompare(b.member));
  return json({members,heartbeat_seconds:25});
}
