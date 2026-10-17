import test from "node:test";
import assert from "node:assert/strict";
import { handleRoomPresenceStorage } from "./room-presence.mjs";

function storage(){
  const records=new Map();
  return {get:async k=>records.get(k),put:async(k,v)=>records.set(k,v),
    delete:async k=>records.delete(k),list:async({prefix})=>new Map([...records].filter(([k])=>k.startsWith(prefix)))};
}
async function touch(store,member,userId="1558905252282114178"){
  return handleRoomPresenceStorage(new Request("https://internal.invalid/_presence/touch",{
    method:"POST",body:JSON.stringify({member,role:"reader",user:{
      id:userId,username:"Test",global_name:"Tester",avatar:null
    }})
  }),store);
}
test("online member is visible and identity is verified by immutable Discord ID",async()=>{
  const store=storage();
  let response=await touch(store,"member1");
  assert.equal(response.status,200);
  let result=await response.json();
  assert.equal(result.members[0].user.id,"1558905252282114178");
  assert.equal(result.members[0].online,true);
  assert.equal(result.heartbeat_seconds,25);
  assert.equal((await touch(store,"member1","1558905252282114179")).status,409);
});
test("offline expired leases disappear",async()=>{
  const store=storage();
  await touch(store,"member1");
  await store.put("presence:member1",{member:"member1",role:"reader",user:{
    id:"1558905252282114178",username:"Test"},expires:Date.now()-1});
  const reply=await touch(store,"member2");
  assert.equal((await reply.json()).members.length,1);
  assert.equal(await store.get("presence:member1"),undefined);
});
test("invalid presence claims are rejected",async()=>{
  const store=storage();
  assert.equal((await touch(store,"invalid/member")).status,400);
});
