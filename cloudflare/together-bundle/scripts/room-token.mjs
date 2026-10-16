import { issueMemberToken } from "../src/member-auth.mjs";

const [room, member, role, hoursText = "24"] = process.argv.slice(2);
const master = process.env.TOGETHER_BUNDLE_MASTER_KEY;
if (!room || !member || !role || !master) {
  console.error("Usage: TOGETHER_BUNDLE_MASTER_KEY=<secret> node scripts/room-token.mjs <room-id> <member-id> <reader|host> [hours=24]");
  process.exitCode = 1;
} else {
  try {
    const hours = Number(hoursText);
    if (!Number.isInteger(hours) || hours < 1 || hours > 720)
      throw new TypeError("Expiration must be between 1 and 720 hours.");
    const expiry = Math.floor(Date.now() / 1000) + hours * 3600;
    console.log(await issueMemberToken(master, room, member, role, expiry));
  } catch (error) {
    console.error(error.message);
    process.exitCode = 1;
  }
}
