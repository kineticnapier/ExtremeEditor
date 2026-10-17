import test from "node:test";
import assert from "node:assert/strict";
import { handleDesktopHandoffStorage } from "./desktop-handoff.mjs";

function storage() {
  const map=new Map();
  return {
    get:async k=>map.get(k),put:async(k,v)=>map.set(k,v),
    delete:async k=>map.delete(k)
  };
}
const origin="https://internal.invalid";
const id="a".repeat(64),verifierHash="b".repeat(64);
async function act(store,path,body) {
  return handleDesktopHandoffStorage(new Request(origin+path,{
    method:"POST",body:JSON.stringify(body)
  }),store);
}
test("one-time desktop handoff is pending until completed and only valid verifier may redeem",async()=>{
  const store=storage();
  assert.equal((await act(store,"/_desktop/create",{id,verifierHash})).status,200);
  assert.equal((await act(store,"/_desktop/poll",{id,verifierHash})).status,202);
  assert.equal((await act(store,"/_desktop/poll",{id,verifierHash:"c".repeat(64)})).status,401);
  assert.equal((await act(store,"/_desktop/complete",{id,user:{
    id:"1558905252282114178",username:"Test",global_name:"Example",avatar:null
  }})).status,200);
  const finished=await act(store,"/_desktop/poll",{id,verifierHash});
  assert.equal(finished.status,200);
  assert.equal((await finished.json()).user.username,"Test");
  assert.equal((await act(store,"/_desktop/poll",{id,verifierHash})).status,410);
});
test("expired handoff is rejected and deleted",async()=>{
  const store=storage();
  await store.put("desktop:"+id,{verifierHash,expiry:Date.now()-10,completed:false});
  assert.equal((await act(store,"/_desktop/poll",{id,verifierHash})).status,410);
  assert.equal(await store.get("desktop:"+id),undefined);
});
