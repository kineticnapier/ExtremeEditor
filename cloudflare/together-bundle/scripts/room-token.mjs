import { deriveRoomToken } from "../src/index.mjs";

const room = process.argv[2];
const master = process.env.TOGETHER_BUNDLE_MASTER_KEY;
if (!room || !master) {
  console.error("Usage: TOGETHER_BUNDLE_MASTER_KEY=<secret> node scripts/room-token.mjs <room-id>");
  process.exitCode = 1;
} else {
  try {
    process.stdout.write((await deriveRoomToken(master, room)) + "\n");
  } catch (error) {
    console.error(error.message);
    process.exitCode = 1;
  }
}
