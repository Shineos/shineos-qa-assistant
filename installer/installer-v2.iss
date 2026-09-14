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
; MyAppId の値は "{{...}"（AppId用のエスケープ付き）。[Code]内のレジストリパスで
; 使うのはエスケープなしの一重カッコ版（Innoのアンインストールキー名）
#define MyAppIdRaw "{9A2C6D71-4B3E-4F8A-9C15-D2E4B7A81F21}"

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
Name: "desktopicon"; Description: "デスクトップにショートカットを作成"

[Files]
; バックエンド（自己完結publish: .NETランタイム同梱・109MB）
Source: "..\output\backend-pub\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
; llama.cpp CPUエンジン（llama-server + 依存DLL一式。バリアントDLLは実行時に自動選択される）
Source: "..\spikes\phase0\engine\cpu\llama-server.exe"; DestDir: "{app}\engine"; Flags: ignoreversion
Source: "..\spikes\phase0\engine\cpu\*.dll";            DestDir: "{app}\engine"; Flags: ignoreversion
; 共通
Source: "..\vendor\THIRD-PARTY-NOTICES.txt"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\vendor\licenses\*";               DestDir: "{app}\licenses"; Flags: ignoreversion
Source: "..\assets\app.ico";                  DestDir: "{app}\assets"; Flags: ignoreversion
Source: "launch.vbs";                         DestDir: "{app}"; Flags: ignoreversion
; 既定モデル一式を同梱（インストール直後に使える・完全オフライン）:
; クイック1.7B(IQ4_XS・約0.94GB) + 埋め込みbge-m3 + リランカbge-reranker-v2-m3
; 標準4B・高品質30Bはアプリ内からオンデマンドDL
Source: "..\spikes\phase0\models\Qwen3-1.7B-IQ4_XS.gguf";        DestDir: "{app}\models"; Flags: ignoreversion
Source: "..\spikes\phase0\models\bge-m3-Q8_0.gguf";              DestDir: "{app}\models"; Flags: ignoreversion
Source: "..\spikes\phase0\models\bge-reranker-v2-m3-Q8_0.gguf";  DestDir: "{app}\models"; Flags: ignoreversion

; WebView2 デスクトップアプリ（ユーザーが使う画面。バックエンドと同じフォルダに配置）
Source: "..\dist\ShineosQA.App\ShineosQA.exe";                  DestDir: "{app}"; Flags: ignoreversion
Source: "..\dist\ShineosQA.App\Microsoft.Web.WebView2.Core.dll"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\dist\ShineosQA.App\Microsoft.Web.WebView2.Wpf.dll";  DestDir: "{app}"; Flags: ignoreversion
Source: "..\dist\ShineosQA.App\WebView2Loader.dll";              DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\{#MyAppName}"; Filename: "{app}\launch.vbs"; WorkingDir: "{app}"; IconFilename: "{app}\assets\app.ico"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\launch.vbs"; WorkingDir: "{app}"; IconFilename: "{app}\assets\app.ico"; Tasks: desktopicon

; アップグレード時もナレッジ（data）とDL済みモデル（models）は残す（下の ssUninstall 参照）
[UninstallDelete]
Type: filesandordirs; Name: "{app}\logs"
Type: files; Name: "{app}\config.json"
Type: files; Name: "{app}\install.completed"

[Code]
var
  CustomExitCode: Integer;     { 独自終了コード（0 = Inno 標準のまま。Store申請のリターンコード一意化） }
  SameVerCompleted: Boolean;   { 同一バージョン完了済み（サイレント再実行時に 11 を返す） }

{ カスタム終了コードの返却: Microsoft Store のインストールクライアントがシナリオを
  区別できるよう、独自コードが必要な場合のみ終了コードを上書きする。
  ※ ExitProcess を呼ぶと Inno の後処理（テンポラリフォルダの削除）がスキップされる
    ため、独自コードが必要な場合のみ使用する }
procedure ExitProcess(ExitCode: Cardinal);
  external 'ExitProcess@kernel32.dll stdcall';

const
  { v2 同梱物の実サイズ: バックエンド約130MB + エンジン約60MB + モデル約2GB → 余裕を見て4GB }
  REQUIRED_FREE_GB = 4.0;

function InitializeSetup(): Boolean;
var
  PrevVer, InstallLoc: String;
begin
  Result := True;
  CustomExitCode := 0;
  SameVerCompleted := False;
  { 同一バージョン完了済みの検出: サイレント再実行時は「既に存在」(11) を返す。
    対話実行時は修復のため通常どおり実行する（v1.0.76 と同じ挙動）。
    v2 はユーザー単位インストールのためレジストリは HKCU }
  if WizardSilent() and
     RegQueryStringValue(HKEY_CURRENT_USER,
       'SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{#MyAppIdRaw}_is1',
       'DisplayVersion', PrevVer) and
     (PrevVer = '{#MyAppVersion}') and
     RegQueryStringValue(HKEY_CURRENT_USER,
       'SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{#MyAppIdRaw}_is1',
       'InstallLocation', InstallLoc) and
     FileExists(InstallLoc + '\install.completed') then
    SameVerCompleted := True;
end;

function NextButtonClick(CurPageID: Integer): Boolean;
begin
  Result := True;
  { 同一バージョン完了済みのサイレント再実行: 再インストールせず 11 で即終了
    （失敗途中の再実行は完了マーカーがないため通常どおり再開できる） }
  if (CurPageID = wpReady) and SameVerCompleted then
  begin
    CustomExitCode := 11;
    Result := False;
    Exit;
  end;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  Fso: Variant;
  FreeBytes: Double;
begin
  Result := '';
  { ディスク空き容量チェック（不足時はシナリオ一意の 12 を返す） }
  try
    Fso := CreateOleObject('Scripting.FileSystemObject');
    FreeBytes := Fso.GetDrive(Fso.GetDriveName(ExpandConstant('{app}'))).FreeSpace;
    if FreeBytes < REQUIRED_FREE_GB * 1024 * 1024 * 1024 then
    begin
      CustomExitCode := 12;
      Result := 'ディスクの空き容量が不足しています。インストールには 4GB 以上の空き容量が必要です。';
    end;
  except
    { 容量取得に失敗した場合は Inno 本体のチェックに委ね、独自コードは返さない }
  end;
end;

procedure DeinitializeSetup();
begin
  { 独自終了コードが設定されている場合のみ終了コードを上書きする。
    0 の場合は Inno 標準の終了コードのまま終了する }
  if CustomExitCode <> 0 then
    ExitProcess(CustomExitCode);
end;

{ インストール先確定後にconfig.jsonを生成（絶対パスで data/engine/models を指定。
  パス区切りはJSONエスケープ問題を避けるためフォワードスラッシュを使用）。
  併せて install.completed マーカーを書く（サイレント再実行時の 11 判定に使用） }
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
    SaveStringToFile(ExpandConstant('{app}\install.completed'), '{#MyAppVersion}', False);
  end;
end;

{ アンインストール時に実行中のアプリ・バックエンドとエンジンを停止する。
  対話時のみナレッジ（data）削除の確認を表示。サイレント時はデータを残す
  （Store・無人展開の「クリーンアンインストール」要件: アプリ本体は完全削除、
    ユーザーデータは残置でも再インストールに影響しないため許容される） }
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  RC: Integer;
begin
  if CurUninstallStep = usUninstall then
  begin
    Exec(ExpandConstant('{cmd}'), '/c taskkill /IM ShineosQA.exe /F', '', SW_HIDE, ewWaitUntilTerminated, RC);
    Exec(ExpandConstant('{cmd}'), '/c taskkill /IM ShineosQA.Backend.exe /F', '', SW_HIDE, ewWaitUntilTerminated, RC);
    Exec(ExpandConstant('{cmd}'), '/c taskkill /IM llama-server.exe /F', '', SW_HIDE, ewWaitUntilTerminated, RC);
    if (not UninstallSilent()) and DirExists(ExpandConstant('{app}\data')) then
    begin
      if MsgBox('ナレッジ（社内文書・検索データ）とチャット履歴もすべて削除しますか？' + #13#10 +
                '「いいえ」を選ぶと、これらのデータは残ります（再インストールで引き続き利用できます）。',
                mbConfirmation, MB_YESNO) = IDYES then
        DelTree(ExpandConstant('{app}\data'), True, True, True);
    end;
  end;
end;
