// エンドカードのQRが実際に読み取れるかを検証する（動画から抜いたフレームをデコード）
// 使い方: node verify-qr.js <frame.png>
const fs = require("fs");
const { PNG } = require("pngjs");
const jsQR = require("jsqr");

const file = process.argv[2];
const png = PNG.sync.read(fs.readFileSync(file));
const result = jsQR(new Uint8ClampedArray(png.data), png.width, png.height);
if (!result) {
  console.log("DECODE FAILED: no QR found in", file);
  process.exit(1);
}
console.log("DECODED:", result.data);
