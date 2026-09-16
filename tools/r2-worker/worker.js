// R2 uploader — cron-driven, GitHub Release parts -> R2 multipart, state persisted in R2.
// One-shot tool: delete the script and .upload-state.json after use.
const GH_PARTS = [
  ["https://github.com/Shineos/shineos-qa-assistant/releases/download/v2.0.5/2.0.5.part01", 1073741824],
  ["https://github.com/Shineos/shineos-qa-assistant/releases/download/v2.0.5/2.0.5.part02", 1073741824],
  ["https://github.com/Shineos/shineos-qa-assistant/releases/download/v2.0.5/2.0.5.part03", 86975148],
];
const KEY = "ShineosQA-Setup-2.0.5.exe";
const STATE_KEY = ".upload-state.json";
const EXPECT_TOTAL = 2234458796;

export default {
  async scheduled(event, env) {
    await run(env);
  },
  async fetch(request, env) {
    const s = await env.BUCKET.get(STATE_KEY);
    return new Response(s ? await s.text() : "{}", {
      headers: { "content-type": "application/json" },
    });
  },
};

async function run(env) {
  const b = env.BUCKET;
  let st = {};
  const prev = await b.get(STATE_KEY);
  if (prev) st = await prev.json();
  if (st.status === "done") return;
  // overlap guard: skip if another run seems active (heartbeat < 10 min old)
  if (st.running && Date.now() - st.running < 600000) return;
  st.running = Date.now();
  await b.put(STATE_KEY, JSON.stringify(st));

  try {
    if (!st.uploadId) {
      const mpu = await b.createMultipartUpload(KEY, {
        httpMetadata: { contentType: "application/octet-stream" },
      });
      st.uploadId = mpu.uploadId;
      st.parts = [];
      st.running = Date.now();
      await b.put(STATE_KEY, JSON.stringify(st));
    }
    const mpu = b.resumeMultipartUpload(KEY, st.uploadId);
    const parts = st.parts || [];
    const idx = parts.length;
    if (idx < GH_PARTS.length) {
      const [url, expectSize] = GH_PARTS[idx];
      const resp = await fetch(url, { redirect: "follow" });
      if (!resp.ok) throw new Error("fetch " + resp.status + " " + url);
      const len = Number(resp.headers.get("content-length") || 0);
      if (len && len !== expectSize) {
        throw new Error("size mismatch " + len + " != " + expectSize + " " + url);
      }
      const p = await mpu.uploadPart(idx + 1, resp.body);
      parts.push({ partNumber: p.partNumber, etag: p.etag });
      st.parts = parts;
    }
    if (st.parts.length === GH_PARTS.length && !st.completed) {
      await mpu.complete(st.parts);
      st.completed = true;
      const head = await b.head(KEY);
      st.finalSize = head ? head.size : null;
      st.status = st.finalSize === EXPECT_TOTAL ? "done" : "size-error";
    }
    st.lastError = null;
  } catch (e) {
    st.lastError = String((e && e.message) || e);
  }
  st.running = 0;
  st.ts = Date.now();
  await b.put(STATE_KEY, JSON.stringify(st));
}
