; ============================================================================
; 社内知恵袋 v2 インストーラ（Inno Setup 6）— llama.cpp 直接実行アーキテクチャ
;
; v1（Ollama+Open WebUI+Python）との違い:
;   - バックエンドは自己完結C# exe（.NETランタイム不要・サービス不要）
;   - llama.cpp CPUエンジン（llama-server.exe）を同梱
;   - AIモデルはインストール中にダウンロードしない（Store要件: オフラインインストール）
;     → 初回起動時にアプリ内ウィザードからダウンロード（既定 1.7B / 任意 4B・30B）
;
; ビルド: ISCC.exe installer-v2.iss  → dist\ShineosQA-Setup-2.0.0.exe
; 事前に: output\backend-pun\ に自己完結publish済みであること
; ============================================================================

#define MyAppName "社内知恵袋"
#define MyAppVersion "2.0.0"
#define MyAppPublisher "Shineos Inc."
#define MyAppURL "https://shineos.com"
#define MyAppExeName "launch.vbs"
#define MyAppId "{{9A2C6D71-4B3E-4F8A-9C15-D2E4B7A81F21}"

[Setup]
AppId={#MyAppId}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
DefaultDirName={localappdata}\Programs\ShineosQA
DisableProgramGroupPage=yes
; v2はユーザー単位のローカルアプリ（サービス登録なし）→ 管理者権限不要
PrivilegesRequired=lowest
ArchitecturesInstallIn64BitMode=x64compatible
SetupIconFile=..\assets\app.ico
UninstallDisplayIcon={app}\assets\app.ico
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
OutputDir=..\dist
OutputBaseFilename=ShineosQA-Setup-{#MyAppVersion}
VersionInfoVersion={#MyAppVersion}
VersionInfoCompany={#MyAppPublisher}
VersionInfoDescription={#MyAppName}

[Languages]
Name: "japanese"; MessagesFile: "compiler:Languages\Japanese.isl"

[Tasks]
Name: "desktopicon"; Description: "デスクトップにショートカットを作成"; Flags: unchecked

[Files]
; バックエンド（自己完結publish: .NETランタイム同梱・109MB）
Source: "..\output\backend-pub\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
; llama.cpp CPUエンジン（llama-server + 依存DLL一式。バリアントDLLは実行時に自動選択される）
Source: "..\spikes\phase0\engine\cpu\llama-server.exe"; DestDir: "{app}\engine"; Flags: ignoreversion
Source: "..\spikes\phase0\engine\cpu\*.dll";            DestDir: "{app}\engine"; Flags: ignoreversion
; 共通
Source: "..\vendor\THIRD-PARTY-NOTICES.txt"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\assets\app.ico";                  DestDir: "{app}\assets"; Flags: ignoreversion
Source: "launch.vbs";                         DestDir: "{app}"; Flags: ignoreversion
; 既定モデル一式を同梱（インストール直後に使える・完全オフライン）:
; クイック1.7B(IQ4_XS・約0.94GB) + 埋め込みbge-m3 + リランカbge-reranker-v2-m3
; 標準4B・高品質30Bはアプリ内からオンデマンドDL
Source: "..\spikes\phase0\models\Qwen3-1.7B-IQ4_XS.gguf";        DestDir: "{app}\models"; Flags: ignoreversion
Source: "..\spikes\phase0\models\bge-m3-Q8_0.gguf";              DestDir: "{app}\models"; Flags: ignoreversion
Source: "..\spikes\phase0\models\bge-reranker-v2-m3-Q8_0.gguf";  DestDir: "{app}\models"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\{#MyAppName}"; Filename: "{app}\launch.vbs"; WorkingDir: "{app}"; IconFilename: "{app}\assets\app.ico"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\launch.vbs"; WorkingDir: "{app}"; IconFilename: "{app}\assets\app.ico"; Tasks: desktopicon

; アップグレード時もナレッジ（data）とDL済みモデル（models）は残す（下の ssUninstall 参照）
[UninstallDelete]
Type: filesandordirs; Name: "{app}\logs"
Type: files; Name: "{app}\config.json"

[Code]
{ インストール先確定後にconfig.jsonを生成（絶対パスで data/engine/models を指定。
  パス区切りはJSONエスケープ問題を避けるためフォワードスラッシュを使用） }
procedure CurStepChanged(CurStep: TSetupStep);
var
  Cfg, AppDir: String;
begin
  if CurStep = ssPostInstall then
  begin
    AppDir := ExpandConstant('{app}');
    StringChangeEx(AppDir, '\', '/', True);
    Cfg := '{' + #13#10 +
      '  "port": 8300,' + #13#10 +
      '  "data_dir": "' + AppDir + '/data",' + #13#10 +
      '  "engine_dir": "' + AppDir + '/engine",' + #13#10 +
      '  "engine_variant": "cpu",' + #13#10 +
      '  "models_dir": "' + AppDir + '/models",' + #13#10 +
      '  "standard_model": "Qwen3-4B-Instruct-2507-IQ4_XS.gguf",' + #13#10 +
      '  "quick_model": "Qwen3-1.7B-IQ4_XS.gguf",' + #13#10 +
      '  "quality_model": "Qwen3-30B-A3B-Instruct-2507-UD-Q3_K_XL.gguf",' + #13#10 +
      '  "tier": "quick",' + #13#10 +
      '  "ctx_size": 2048' + #13#10 +
      '}';
    SaveStringToFile(ExpandConstant('{app}\config.json'), Cfg, False);
  end;
end;

{ アンインストール時に実行中のバックエンドとエンジンを停止する }
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  RC: Integer;
begin
  if CurUninstallStep = usUninstall then
  begin
    Exec(ExpandConstant('{cmd}'), '/c taskkill /IM ShineosQA.Backend.exe /F', '', SW_HIDE, ewWaitUntilTerminated, RC);
    Exec(ExpandConstant('{cmd}'), '/c taskkill /IM llama-server.exe /F', '', SW_HIDE, ewWaitUntilTerminated, RC);
  end;
end;
