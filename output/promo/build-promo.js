// 社内知恵袋 プロモ動画ビルドスクリプト
// 大きいテロップ + 急なズームイン（パンチイン）演出 / 1.2倍速 / 音声なし / 1920x1080 25fps H.264
//
// 使い方（これだけで全部やり直せる）:
//   1) promo-config.json の siteUrl を書き換える（エンドカードのURLとQRコードが変わる）
//   2) node build-promo.js
//
// 生成物:
//   shineos-qa-usecase-1080p.mp4   … 完成動画
//   thumbnail-1280x720.png         … YouTubeサムネイル
const { execFileSync } = require("child_process");
const fs = require("fs");
const path = require("path");
const QRCode = require("qrcode");

const ROOT = __dirname;
const FF = "D:/dev/shineos-local-ai/tools/ffmpeg/ffmpeg-9.0.1-essentials_build/bin/ffmpeg.exe";
const FP = "D:/dev/shineos-local-ai/tools/ffmpeg/ffmpeg-9.0.1-essentials_build/bin/ffprobe.exe";
const FONT = "'C\\:/Windows/Fonts/YuGothB.ttc'";
const REC = path.join(ROOT, "recordings");
const WORK = path.join(ROOT, "work");
const CAPS = path.join(ROOT, "caps");
const CARDS = path.join(ROOT, "cards");
fs.mkdirSync(WORK, { recursive: true });
fs.mkdirSync(CAPS, { recursive: true });

// ==== 設定（promo-config.json） ====
// siteUrl は promo-config.json で変更する。テスト時は環境変数 PROMO_SITE_URL で一時的に上書きできる。
const config = JSON.parse(fs.readFileSync(path.join(ROOT, "promo-config.json"), "utf8"));
const SITE_URL = process.env.PROMO_SITE_URL || config.siteUrl;

// --endcard-only: エンドカードだけを再生成（URL変更の確認用。動画全体は再生成しない）
const ENDCARD_ONLY = process.argv.includes("--endcard-only");

// エンドカードのQR・URLカードの配置（cards/end.html の要素位置と対応）
const END_CARD = {
  qrSize: 360, qrTop: 502,
  pillTop: 876, pillH: 106, pillPadX: 70, pillColor: "#10a37f",
  urlFontSize: 62, urlMaxWidth: 880, urlColor: "#ffffff",
};

function run(args) {
  try {
    execFileSync(FF, args, { stdio: ["ignore", "pipe", "pipe"] });
  } catch (e) {
    console.error("FFMPEG FAILED:", args.join(" ").slice(0, 300));
    console.error(e.stderr ? e.stderr.toString().split("\n").slice(-15).join("\n") : e.message);
    throw e;
  }
}

// 1) 設定からQRコードを生成してエンドカードを合成（URLを変えたらここで反映される）
//    - QR: 背景を透過（白モジュール）。暗い背景に直接置く
//    - URL: 緑のカード（角丸）の中に白文字。文字幅を実測してカード幅を決める
function hexToRgb(hex) {
  const h = hex.replace("#", "");
  return [parseInt(h.slice(0, 2), 16), parseInt(h.slice(2, 4), 16), parseInt(h.slice(4, 6), 16)];
}

// 角丸（両端が半円のスタジアム型）カードをPNGで生成。アンチエイリアス付き
function makePillPng(width, height, hex, outPath) {
  const { PNG } = require("pngjs");
  const png = new PNG({ width, height });
  const [cr, cg, cb] = hexToRgb(hex);
  const r = height / 2, x0 = r, x1 = width - r, cy = height / 2;
  for (let y = 0; y < height; y++) {
    for (let x = 0; x < width; x++) {
      const sx = Math.min(Math.max(x + 0.5, x0), x1);
      const d = Math.hypot(x + 0.5 - sx, y + 0.5 - cy);
      const a = Math.min(1, Math.max(0, r - d + 0.5));
      const i = (width * y + x) << 2;
      png.data[i] = cr; png.data[i + 1] = cg; png.data[i + 2] = cb;
      png.data[i + 3] = Math.round(a * 255);
    }
  }
  fs.writeFileSync(outPath, PNG.sync.write(png));
}

// 文字を透明背景で描画して、実際の描画幅を測る（カード幅を文字に合わせるため）
function measureText(text, fontSize) {
  const { PNG } = require("pngjs");
  const capPath = path.join(CAPS, "measure.txt");
  fs.writeFileSync(capPath, text, "utf8");
  const tf = "'" + capPath.replace(/\\/g, "/").replace(/:/g, "\\:") + "'";
  const pngPath = path.join(WORK, "measure.png");
  run(["-y", "-f", "lavfi", "-i", "color=c=black@0.0:s=2000x220,format=rgba",
    "-vf", `drawtext=fontfile=${FONT}:textfile=${tf}:fontsize=${fontSize}:fontcolor=white:x=0:y=0`,
    "-frames:v", "1", pngPath]);
  const png = PNG.sync.read(fs.readFileSync(pngPath));
  let minX = Infinity, maxX = -Infinity, minY = Infinity, maxY = -Infinity;
  for (let y = 0; y < png.height; y++) {
    for (let x = 0; x < png.width; x++) {
      if (png.data[(png.width * y + x) * 4 + 3] > 8) {
        if (x < minX) minX = x; if (x > maxX) maxX = x;
        if (y < minY) minY = y; if (y > maxY) maxY = y;
      }
    }
  }
  return { width: maxX - minX + 1, height: maxY - minY + 1, ascentOffset: minY };
}

async function buildEndCard() {
  // (a) QRコード（背景透過・白モジュール）。高解像度で生成しffmpegで縮小
  const qrPath = path.join(CARDS, "qr.png");
  await QRCode.toFile(qrPath, SITE_URL, {
    width: END_CARD.qrSize * 4, margin: 2, errorCorrectionLevel: "M",
    color: { dark: "#ffffff", light: "#00000000" },
  });

  // (b) URL文字幅を実測 → 収まるフォントサイズとカード幅を決める
  let fontSize = END_CARD.urlFontSize;
  let m = measureText(SITE_URL, fontSize);
  while (m.width > END_CARD.urlMaxWidth && fontSize > 24) {
    fontSize -= 2;
    m = measureText(SITE_URL, fontSize);
  }
  const pillW = m.width + END_CARD.pillPadX * 2;
  const pillX = Math.round((1920 - pillW) / 2);
  const pillPath = path.join(CARDS, "url-pill.png");
  makePillPng(pillW, END_CARD.pillH, END_CARD.pillColor, pillPath);

  const qrX = Math.round((1920 - END_CARD.qrSize) / 2);
  const urlTxt = path.join(CAPS, "endurl.txt");
  fs.writeFileSync(urlTxt, SITE_URL, "utf8");
  const tf = "'" + urlTxt.replace(/\\/g, "/").replace(/:/g, "\\:") + "'";
  // 文字の実際の描画開始位置(ascentOffset)を差し引いてカード内で垂直中央に置く
  const urlY = Math.round(END_CARD.pillTop + (END_CARD.pillH - m.height) / 2 - m.ascentOffset);

  run(["-y",
    "-i", path.join(CARDS, "end-base.png"),
    "-i", qrPath,
    "-i", pillPath,
    "-filter_complex",
    `[1:v]scale=${END_CARD.qrSize}:${END_CARD.qrSize}:flags=lanczos[qr];` +
    `[0:v][qr]overlay=${qrX}:${END_CARD.qrTop}:format=auto[bg];` +
    `[bg][2:v]overlay=${pillX}:${END_CARD.pillTop}:format=auto[card];` +
    `[card]drawtext=fontfile=${FONT}:textfile=${tf}:fontsize=${fontSize}:` +
    `fontcolor=${END_CARD.urlColor}:x=(w-text_w)/2:y=${urlY}[v]`,
    "-map", "[v]", "-frames:v", "1", path.join(CARDS, "end.png")]);
  console.log(`end card composed: ${SITE_URL} (font ${fontSize}px, card ${pillW}px)`);
}

// 2) webm -> CFR mp4 (25fps)
for (const name of ["sceneB-question", "sceneC-knowledge", "sceneD-ask-new", "sceneEF-models-settings"]) {
  const out = path.join(WORK, name + ".mp4");
  if (!fs.existsSync(out)) {
    run(["-y", "-i", path.join(REC, name + ".webm"),
      "-c:v", "libx264", "-preset", "veryfast", "-crf", "18",
      "-r", "25", "-pix_fmt", "yuv420p", "-an", "-movflags", "+faststart", out]);
    console.log("converted", name);
  }
}

// 3) caption text files (drawtext textfile= 用 / UTF-8)
const captions = {
  b1: "社内規定について、質問するだけ。",
  b3: "登録した社内文書を検索して回答",
  b4: "出典は「文書名・該当箇所」",
  b5: "クリックで原文の該当箇所を表示",
  c1: "PDF・Word・Markdownをドラッグ＆ドロップで登録",
  c2: "追加した資料は、すぐに使える",
  d1: "追加した資料についても、そのまま質問",
  d3: "すぐに回答",
  d4: "出典も確認できる",
  e1: "AIモデルを3段階で即切替",
  e2: "クイック／標準／高品質",
  f1: "Web検索は既定OFF。資料は外に出ない",
};
for (const [k, v] of Object.entries(captions)) {
  fs.writeFileSync(path.join(CAPS, k + ".txt"), v, "utf8");
}

// caption drawtext (alpha: 0.25s後にfade-in, 終端0.4sでfade-out)
function cap(key, dur, y) {
  const tf = "'" + path.join(CAPS, key + ".txt").replace(/\\/g, "/").replace(/:/g, "\\:") + "'";
  const a = `alpha='if(lt(t,0.25),0,if(lt(t,0.65),(t-0.25)/0.4,if(gt(t,${dur.toFixed(2)}-0.55),max(0,(${dur.toFixed(2)}-t)/0.4),1)))'`;
  return `drawtext=fontfile=${FONT}:textfile=${tf}:fontsize=72:fontcolor=white:` +
    `borderw=6:bordercolor=black@0.85:shadowcolor=black@0.45:shadowx=0:shadowy=5:` +
    `x=(w-text_w)/2:y=${y || "h-215"}:${a}`;
}

const FULL = "scale=1920:1080";
// パンチイン用 16:9 クロップ (1200x675) → 1.6x ズーム
const crop = (x, y) => `crop=1200:675:${x}:${y},scale=1920:1080`;
// 入力欄ハイライト（クロップ+スケール後の座標）
const BOX = "drawbox=x=294:y=950:w=1206:h=122:color=0x10a37f@0.50:t=12," +
  "drawbox=x=306:y=962:w=1182:h=98:color=0x10a37f@0.95:t=6";

// 4) shots
const shots = [
  // [out, src(null=card), ss, to, filters, speed(setpts divisor or null), slow(multiplier)]
  ["01-title", null, null, 4.0, "scale=1920:1080,fade=t=in:st=0:d=0.45,fade=t=out:st=3.5:d=0.5"],
  ["02-b1", "sceneB-question", 1.0, 2.2, FULL + "," + cap("b1", 1.2)],
  ["03-b2", "sceneB-question", 2.2, 4.6, crop(520, 405) + "," + BOX],
  ["04-b3", "sceneB-question", 4.6, 15.0, FULL + "," + cap("b3", 7.43), 1.4],
  ["05-b4", "sceneB-question", 15.0, 18.6, crop(560, 60) + "," + cap("b4", 3.6)],
  ["06-b5", "sceneB-question", 18.6, 27.3, crop(480, 140) + "," + cap("b5", 8.7)],
  ["07-c1", "sceneC-knowledge", 1.6, 5.5, FULL + "," + cap("c1", 3.9)],
  ["08-c2", "sceneC-knowledge", 11.2, 18.9, FULL + "," + cap("c2", 7.7)],
  ["09-d1", "sceneD-ask-new", 1.0, 4.4, FULL + "," + cap("d1", 3.4)],
  ["10-d2", "sceneD-ask-new", 4.4, 7.6, crop(520, 405) + "," + BOX],
  ["11-d3", "sceneD-ask-new", 7.6, 19.4, FULL + "," + cap("d3", 8.43), 1.4],
  ["12-d4", "sceneD-ask-new", 19.4, 25.8, crop(480, 100) + "," + cap("d4", 6.4)],
  ["13-e1", "sceneEF-models-settings", 1.35, 3.45, FULL + "," + cap("e1", 2.84, 60), null, 1.35],
  ["14-e2", "sceneEF-models-settings", 3.5, 7.6, crop(520, 405) + "," + cap("e2", 4.1, 60)],
  ["15-f1", "sceneEF-models-settings", 9.2, 13.0, FULL + "," + cap("f1", 3.8)],
  ["16-f2", "sceneEF-models-settings", 13.0, 19.0, FULL],
  ["17-end", null, null, 4.0, "scale=1920:1080,fade=t=in:st=0:d=0.45"],
];

async function main() {
  await buildEndCard();
  if (ENDCARD_ONLY) {
    console.log("--endcard-only: wrote", path.join(CARDS, "end.png"));
    return;
  }

  for (const [out, src, ss, to, vf, speed, slow] of shots) {
    const outPath = path.join(WORK, out + ".mp4");
    const args = ["-y"];
    let filters = vf;
    if (src === null) {
      const img = out.startsWith("01") ? path.join(CARDS, "title.png") : path.join(CARDS, "end.png");
      args.push("-loop", "1", "-framerate", "25", "-i", img, "-t", String(to));
    } else {
      args.push("-ss", String(ss), "-to", String(to), "-i", path.join(WORK, src + ".mp4"));
      if (speed) { filters += `,setpts=PTS/${speed},fps=25`; }
      if (slow) { filters += `,setpts=PTS*${slow},fps=25`; }
    }
    args.push("-vf", filters, "-c:v", "libx264", "-preset", "medium", "-crf", "18",
      "-r", "25", "-pix_fmt", "yuv420p", "-an", "-movflags", "+faststart", outPath);
    run(args);
    console.log("shot", out, `${(to - ss).toFixed(2)}s`);
  }

  // concat
  const listPath = path.join(WORK, "list.txt");
  fs.writeFileSync(listPath, shots.map(([out]) => `file '${out}.mp4'`).join("\n"), "utf8");
  const concatPath = path.join(WORK, "concat.mp4");
  run(["-y", "-f", "concat", "-safe", "0", "-i", listPath, "-c", "copy", concatPath]);

  // 1.2倍速＋音声トラック完全なし（最終納品）
  const finalPath = path.join(ROOT, "shineos-qa-usecase-1080p.mp4");
  run(["-y", "-i", concatPath, "-vf", "setpts=PTS/1.2,fps=25", "-an",
    "-c:v", "libx264", "-preset", "medium", "-crf", "18", "-movflags", "+faststart", finalPath]);

  // サムネイル（動画の冒頭カード title.png を1280x720へ縮小）
  // 冒頭カード＝動画の1フレーム目＝サムネイル となり、常に一致する
  run(["-y", "-i", path.join(CARDS, "title.png"), "-vf", "scale=1280:720",
    "-frames:v", "1", "-update", "1", path.join(ROOT, "thumbnail-1280x720.png")]);
  console.log("thumbnail from title.png (video opening card)");

  // verify
  const dur = execFileSync(FP, ["-v", "error", "-show_entries", "format=duration",
    "-of", "default=noprint_wrappers=1:nokey=1", finalPath]).toString().trim();
  const stream = execFileSync(FP, ["-v", "error", "-select_streams", "v:0",
    "-show_entries", "stream=codec_name,width,height,r_frame_rate",
    "-of", "default=noprint_wrappers=1", finalPath]).toString().trim();
  const audio = execFileSync(FP, ["-v", "error", "-select_streams", "a",
    "-show_entries", "stream=codec_name", "-of", "csv=p=0", finalPath]).toString().trim();
  console.log("FINAL:", finalPath);
  console.log("duration:", dur, "s  audio streams:", audio === "" ? "none" : audio);
  console.log(stream);
}

main().catch((e) => { console.error(e); process.exit(1); });
