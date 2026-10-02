// 公式Microsoft StoreバッジSVG → 高解像度PNG変換
// 使い方: node rasterize-badges.js
// cards/msstore-badge-{dark,light}.svg を 10倍解像度(1610x440)でPNG化する。
// build-promo.js は生成済みPNG(cards/msstore-badge-*.png)を使うため、
// SVG差し替え時以外は再実行不要。
const fs = require("fs");
const path = require("path");
const { Resvg } = require("@resvg/resvg-js");

const CARDS = path.join(__dirname, "cards");
const SCALE = 10; // 161x44 → 1610x440。ffmpeg側でlanczos縮小して使う

for (const name of ["msstore-badge-dark", "msstore-badge-light"]) {
  const svg = fs.readFileSync(path.join(CARDS, name + ".svg"), "utf8");
  const resvg = new Resvg(svg, { fitTo: { mode: "width", value: 161 * SCALE } });
  const png = resvg.render().asPng();
  const out = path.join(CARDS, name + ".png");
  fs.writeFileSync(out, png);
  console.log("rasterized", out, `(${png.length} bytes)`);
}
