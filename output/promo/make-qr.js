// promo-config.json の siteUrl から QRコード(PNG)を生成する
// 使い方: node make-qr.js
const fs = require("fs");
const path = require("path");
const QRCode = require("qrcode");

const ROOT = __dirname;
const config = JSON.parse(fs.readFileSync(path.join(ROOT, "promo-config.json"), "utf8"));
const out = path.join(ROOT, "cards", "qr.png");

QRCode.toFile(out, config.siteUrl, {
  width: 280,
  margin: 1,
  errorCorrectionLevel: "M",
  color: { dark: "#0d2b23", light: "#ffffff" },
}).then(() => {
  console.log("QR generated:", out, "for", config.siteUrl);
}).catch((e) => {
  console.error(e);
  process.exit(1);
});
