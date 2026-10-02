// thumbnail-1280x720.png からデザイン色をサンプリングする一時スクリプト
const fs = require("fs");
const { PNG } = require("pngjs");
const png = PNG.sync.read(fs.readFileSync("D:/dev/shineos-local-ai/output/promo/thumbnail-1280x720.png"));
const px = (x, y) => {
  const i = (png.width * y + x) << 2;
  const [r, g, b] = [png.data[i], png.data[i + 1], png.data[i + 2]];
  const hex = "#" + [r, g, b].map(v => v.toString(16).padStart(2, "0")).join("");
  return hex;
};
const avg = (x0, y0, x1, y1) => {
  let r = 0, g = 0, b = 0, n = 0;
  for (let y = y0; y < y1; y += 2) for (let x = x0; x < x1; x += 2) {
    const i = (png.width * y + x) << 2;
    r += png.data[i]; g += png.data[i + 1]; b += png.data[i + 2]; n++;
  }
  return "#" + [r / n, g / n, b / n].map(v => Math.round(v).toString(16).padStart(2, "0")).join("");
};
// 背景: 四隅と中央上部(グラデ確認)
console.log("bg TL   :", avg(20, 20, 120, 60));
console.log("bg TR   :", avg(1160, 20, 1260, 60));
console.log("bg BL   :", avg(20, 660, 120, 700));
console.log("bg BR   :", avg(1160, 660, 1260, 700));
console.log("bg mid  :", avg(600, 30, 680, 60));
// 見出し(濃色テキスト): y~330-380 の行で最も暗いピクセルを探す
let darkest = [255, "fff"];
for (let y = 310; y < 400; y += 3) for (let x = 80; x < 1200; x += 3) {
  const i = (png.width * y + x) << 2;
  const lum = png.data[i] + png.data[i + 1] + png.data[i + 2];
  if (lum < darkest[0]) darkest = [lum, px(x, y)];
}
console.log("headline(darkest):", darkest[1]);
// サブテキスト行 y~440-460
let sub = [999, "fff"];
for (let y = 435; y < 470; y += 2) for (let x = 280; x < 1000; x += 2) {
  const i = (png.width * y + x) << 2;
  const lum = png.data[i] + png.data[i + 1] + png.data[i + 2];
  if (lum < sub[0]) sub = [lum, px(x, y)];
}
console.log("sub(darkest):", sub[1]);
// アプリアイコン: 中央 y~208、中心付近の平均(濃緑)と上端(明るめ緑)
console.log("icon top  :", avg(620, 160, 660, 175));
console.log("icon body :", avg(600, 190, 680, 230));
console.log("icon br   :", avg(680, 235, 700, 250));
// 白ピル内(白) と ピル外縁の影
console.log("pill white:", avg(300, 530, 380, 560));
console.log("store badge:", avg(700, 620, 800, 650));
