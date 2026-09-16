// product.wxs ジェネレーター — 社内知恵袋 MSI（WiX v4+ 構文）
//
// 使い方: node tools/msi/generate-wxs.mjs [--version 2.0.5]
//   - installer/installer-v2.iss（Inno Setup）の収容物・挙動をMSI化する
//   - コンポーネントGUIDは「インストール先相対パス」のMD5から決定的に生成
//     （バージョンをまたいで安定 = 参照カウント/メジャーアップグレード安全）
//   - キャビネットは3分割（非モデル約180MB / モデル2点1.65GB / モデル1点0.6GB）
//     …単一キャビネットの2GiB制限と、モデル合計2.12GiBを超えるため
//   - per-user（%LOCALAPPDATA%\Programs\ShineosQA・管理者権限不要）
import { createHash } from "node:crypto";
import { readdirSync, statSync, writeFileSync, mkdirSync } from "node:fs";
import { join, relative, resolve, dirname } from "node:path";
import { fileURLToPath } from "node:url";

const ROOT = resolve(dirname(fileURLToPath(import.meta.url)), "..", "..");
const argVersion = process.argv.includes("--version")
  ? process.argv[process.argv.indexOf("--version") + 1] : "2.0.5";
const OUT_DIR = join(ROOT, "output", "msi");

const guid = (s) => {
  const h = createHash("md5").update(s, "utf8").digest("hex").toUpperCase();
  return `{${h.slice(0, 8)}-${h.slice(8, 12)}-${h.slice(12, 16)}-${h.slice(16, 20)}-${h.slice(20, 32)}}`;
};
// GUID文字生成用の短い一意ID（要素Idに使用。GUIDと別物）
const id = (prefix, s) => {
  const h = createHash("md5").update(s, "utf8").digest("hex").slice(0, 12);
  return `${prefix}_${h}`;
};

// ---- 収容物の定義（Inno installer-v2.iss と同一の配置） ----
const walk = (dir, base = dir) =>
  readdirSync(dir, { withFileTypes: true }).flatMap((e) => {
    const p = join(dir, e.name);
    return e.isDirectory() ? walk(p, base) : [p];
  });

const files = []; // { src(abs, win分), destRel, diskId }
const add = (absSrc, destRel, diskId = 1) =>
  files.push({ src: absSrc, destRel: destRel.replace(/\//g, "\\"), diskId });

walk(join(ROOT, "output", "backend-pub")).forEach((f) =>
  add(f, relative(join(ROOT, "output", "backend-pub"), f)));
readdirSync(join(ROOT, "spikes", "phase0", "engine", "cpu"))
  .filter((n) => n === "llama-server.exe" || n.endsWith(".dll"))
  .forEach((n) => add(join(ROOT, "spikes", "phase0", "engine", "cpu", n), join("engine", n)));
add(join(ROOT, "vendor", "THIRD-PARTY-NOTICES.txt"), "THIRD-PARTY-NOTICES.txt");
walk(join(ROOT, "vendor", "licenses")).forEach((f) =>
  add(f, join("licenses", relative(join(ROOT, "vendor", "licenses"), f))));
add(join(ROOT, "assets", "app.ico"), join("assets", "app.ico"));
add(join(ROOT, "installer", "launch.vbs"), "launch.vbs");
for (const m of ["Qwen3-1.7B-IQ4_XS.gguf", "bge-m3-Q8_0.gguf"])
  add(join(ROOT, "spikes", "phase0", "models", m), join("models", m), 2); // cab2: 1.65GB
add(join(ROOT, "spikes", "phase0", "models", "bge-reranker-v2-m3-Q8_0.gguf"),
  join("models", "bge-reranker-v2-m3-Q8_0.gguf"), 3); // cab3: 0.6GB
for (const f of ["ShineosQA.exe", "Microsoft.Web.WebView2.Core.dll",
  "Microsoft.Web.WebView2.Wpf.dll", "WebView2Loader.dll"])
  add(join(ROOT, "dist", "ShineosQA.App", f), f);
add(join(OUT_DIR, "install.completed"), "install.completed"); // 中身=バージョン（アプリの初回案内ダイアログ判定用）

// install.completed（判定は更新時刻のみ・内容は不問だがInno踏襲でバージョン文字列）
mkdirSync(OUT_DIR, { recursive: true });
writeFileSync(join(OUT_DIR, "install.completed"), argVersion, "utf8");

// ---- ディレクトリツリー（インストール先相対パス → Directory要素） ----
const dirs = new Map(); // destDirRel("\\"区切り・ルート="") -> { id }
dirs.set("", { id: "INSTALLDIR" });
const dirId = (rel) => {
  // 先祖ディレクトリも必ず登録（中間ディレクトリに直接ファイルが無いケース対応）
  let cur = "";
  for (const seg of rel.split("\\")) {
    cur = cur ? `${cur}\\${seg}` : seg;
    if (!dirs.has(cur)) dirs.set(cur, { id: id("dir", cur) });
  }
  return rel ? dirs.get(rel).id : "INSTALLDIR";
};
files.forEach((f) => dirId(dirname(f.destRel) === "." ? "" : dirname(f.destRel)));

const esc = (s) => s.replace(/&/g, "&amp;").replace(/</g, "&lt;").replace(/"/g, "&quot;");
const win = (p) => resolve(p).replace(/\//g, "\\");

// ---- WXS 生成 ----
const lines = [];
lines.push(`<?xml version="1.0" encoding="utf-8"?>`,
  `<Wix xmlns="http://wixtoolset.org/schemas/v4/wxs"`,
  `     xmlns:util="http://wixtoolset.org/schemas/v4/wxs/util">`,
  ``,
  `  <!-- ${files.length}ファイル。tools/msi/generate-wxs.mjs による自動生成（手編集しない） -->`,
  `  <Package Name="社内知恵袋" Manufacturer="Shineos Inc." Version="${argVersion}"`,
  `           UpgradeCode="${guid("ShineosQA-UpgradeCode")}" Language="1041" Codepage="932"`,
  `           Scope="perUser" InstallerVersion="500" Compressed="yes">`,
  ``,
  `    <MajorUpgrade DowngradeErrorMessage="既に新しいバージョンの社内知恵袋がインストールされています。" />`,
  ``,
  `    <!-- キャビネット3分割（単一cabの2GiB制限対策）。File@DiskIdが対応 -->`,
  `    <Media Id="1" Cabinet="app.cab" EmbedCab="yes" />`,
  `    <Media Id="2" Cabinet="models12.cab" EmbedCab="yes" />`,
  `    <Media Id="3" Cabinet="model3.cab" EmbedCab="yes" />`,
  ``,
  `    <Icon Id="AppIco" SourceFile="${esc(win(join(ROOT, "assets", "app.ico")))}" />`,
  `    <Property Id="ARPPRODUCTICON" Value="AppIco" />`,
  `    <Property Id="ARPURLINFOABOUT" Value="https://shineos.com" />`,
  `    <Property Id="ARPURLUPDATEINFO" Value="https://github.com/Shineos/shineos-qa-assistant/releases" />`,
  ``,
  `    <!-- アン/インストール時に実行中の関連プロセスを終了（Innoのtaskkill相当） -->`,
  `    <util:CloseApplication Id="KillShell" Target="ShineosQA.exe" TerminateProcess="1" RebootPrompt="no" />`,
  `    <util:CloseApplication Id="KillBackend" Target="ShineosQA.Backend.exe" TerminateProcess="1" RebootPrompt="no" />`,
  `    <util:CloseApplication Id="KillEngine" Target="llama-server.exe" TerminateProcess="1" RebootPrompt="no" />`,
  ``,
  `    <StandardDirectory Id="LocalAppDataFolder">`,
  `      <Directory Id="INSTALLDIR" Name="Programs"> <!-- 直下: Programs\\ShineosQA -->`);

// ディレクトリ構造をネストして生成（INSTALLDIRの「子」としてProgramsの下にShineosQA）
// シンプル化: INSTALLDIR = LocalAppDataFolder\\Programs\\ShineosQA の連鎖
lines.splice(-1, 1,
  `      <Directory Name="Programs">`,
  `        <Directory Id="INSTALLDIR" Name="ShineosQA">`);

const byParent = new Map();
for (const [rel, meta] of dirs) {
  if (rel === "") continue;
  const parent = dirname(rel) === "." ? "" : dirname(rel);
  if (!byParent.has(parent)) byParent.set(parent, []);
  byParent.get(parent).push(rel);
}
const emitDirs = (parentRel, indent) => {
  for (const rel of (byParent.get(parentRel) || []).sort()) {
    const name = rel.split("\\").pop();
    lines.push(`${indent}<Directory Id="${dirId(rel)}" Name="${esc(name)}">`);
    emitDirs(rel, indent + "  ");
    lines.push(`${indent}</Directory>`);
  }
};
emitDirs("", "          ");

lines.push(`        </Directory>`,
  `      </Directory>`,
  `    </StandardDirectory>`,
  ``,
  `    <StandardDirectory Id="ProgramMenuFolder">`,
  `      <Component Id="StartMenuShortcut" Guid="${guid("ShineosQA-C-StartMenu")}">`,
  `        <RegistryValue Root="HKCU" Key="Software\\ShineosQA\\Shortcuts" Name="StartMenu" Type="integer" Value="1" KeyPath="yes" />`,
  `        <Shortcut Id="ShortcutStartMenu" Name="社内知恵袋" Description="社内知恵袋 (ShineosQA)"`,
  `                  Target="[INSTALLDIR]launch.vbs" WorkingDirectory="INSTALLDIR" Icon="AppIco" />`,
  `        <RemoveFolder Id="RemoveMenuDir" On="uninstall" />`,
  `      </Component>`,
  `    </StandardDirectory>`,
  `    <StandardDirectory Id="DesktopFolder">`,
  `      <Component Id="DesktopShortcut" Guid="${guid("ShineosQA-C-Desktop")}">`,
  `        <RegistryValue Root="HKCU" Key="Software\\ShineosQA\\Shortcuts" Name="Desktop" Type="integer" Value="1" KeyPath="yes" />`,
  `        <Shortcut Id="ShortcutDesktop" Name="社内知恵袋" Description="社内知恵袋 (ShineosQA)"`,
  `                  Target="[INSTALLDIR]launch.vbs" WorkingDirectory="INSTALLDIR" Icon="AppIco" />`,
  `      </Component>`,
  `    </StandardDirectory>`,
  ``,
  `    <Feature Id="ProductFeature" Level="1" Title="社内知恵袋" TypicalDefault="install">`,
  `      <ComponentGroupRef Id="AppFiles" />`,
  `      <ComponentRef Id="StartMenuShortcut" />`,
  `      <ComponentRef Id="DesktopShortcut" />`,
  `    </Feature>`,
  ``,
  `    <ComponentGroup Id="AppFiles">`);

for (const f of files) {
  const destDir = dirname(f.destRel) === "." ? "" : dirname(f.destRel);
  const dRef = destDir === "" ? "INSTALLDIR" : dirId(destDir);
  const cid = id("c", f.destRel);
  const fname = f.destRel.split("\\").pop();
  lines.push(
    `      <Component Id="${cid}" Guid="${guid("ShineosQA-C|" + f.destRel)}" Directory="${dRef}">`,
    `        <RegistryValue Root="HKCU" Key="Software\\ShineosQA\\Components" Name="${cid}" Type="integer" Value="1" KeyPath="yes" />`,
    `        <File Id="${id("f", f.destRel)}" Source="${esc(win(f.src))}" Name="${esc(fname)}" DiskId="${f.diskId}" />`,
    `      </Component>`);
}
lines.push(`    </ComponentGroup>`,
  `  </Package>`,
  `</Wix>`);

const wxs = lines.join("\n");
writeFileSync(join(OUT_DIR, "product.wxs"), "\ufeff" + wxs, "utf8");

const total = files.reduce((n, f) => n + statSync(f.src).size, 0);
const perDisk = {};
files.forEach((f) => { perDisk[f.diskId] = (perDisk[f.diskId] || 0) + statSync(f.src).size; });
console.log(`product.wxs 生成: ${files.length} ファイル / ${(total / 1024 ** 3).toFixed(2)} GiB`);
console.log(`  cab1(アプリ実体): ${(perDisk[1] / 1024 ** 2).toFixed(0)} MB / cab2(モデル2点): ${(perDisk[2] / 1024 ** 3).toFixed(2)} GiB / cab3(モデル1点): ${(perDisk[3] / 1024 ** 2).toFixed(0)} MB`);
