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
; バージョンはISCC /DMyAppVersion=x.y.z で上書き可能（アップグレード経路テスト等）
#ifndef MyAppVersion
#define MyAppVersion "2.0.0"
#endif
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
#ifdef LiteBuild
OutputBaseFilename=ShineosQA-Setup-{#MyAppVersion}-lite
#else
OutputBaseFilename=ShineosQA-Setup-{#MyAppVersion}
#endif
VersionInfoVersion={#MyAppVersion}
VersionInfoCompany={#MyAppPublisher}
VersionInfoDescription={#MyAppName}

[Languages]
Name: "japanese"; MessagesFile: "compiler:Languages\Japanese.isl"

[Tasks]
Name: "desktopicon"; Description: "デスクトップにショートカットを作成"

[Files]
; バックエンド（自己完結publish: .NETランタイム同梱・109MB）
; ※Excludes: data は必須。ローカルでpublish出力からexeを起動すると data/knowledge.db が
;   生成されることがあり、それを同梱してインストールすると**ユーザーのナレッジDBを空DBで上書き**する
Source: "..\output\backend-pub\*"; DestDir: "{app}"; Excludes: "data,data\*,*.db,*.db-shm,*.db-wal"; Flags: ignoreversion recursesubdirs createallsubdirs
; llama.cpp CPUエンジン（llama-server + 依存DLL一式。バリアントDLLは実行時に自動選択される）
Source: "..\spikes\phase0\engine\cpu\llama-server.exe"; DestDir: "{app}\engine"; Flags: ignoreversion
Source: "..\spikes\phase0\engine\cpu\*.dll";            DestDir: "{app}\engine"; Flags: ignoreversion
; 共通
Source: "..\vendor\THIRD-PARTY-NOTICES.txt"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\vendor\licenses\*";               DestDir: "{app}\licenses"; Flags: ignoreversion
Source: "..\assets\app.ico";                  DestDir: "{app}\assets"; Flags: ignoreversion
Source: "launch.vbs";                         DestDir: "{app}"; Flags: ignoreversion
; 既定モデルの同梱は構成で切替:
;   既定（full）: モデル3点を同梱（インストール直後に使える・完全オフライン・約2.2GB）
;   /DLiteBuild : モデル非同梱（約200MB・Store提出用。初回起動時にアプリ内ウィザードで
;                 必須モデル〜選択チャットモデル〜リランカを約2.3GB DL — installerサイズ上限対策。
;                 アプリ側は Models.cs / wizard.ts の初回DLフローが既存実装）
; 標準4B・高品質30Bはアプリ内からオンデマンドDL
#ifndef LiteBuild
Source: "..\spikes\phase0\models\Qwen3-1.7B-IQ4_XS.gguf";        DestDir: "{app}\models"; Flags: ignoreversion
Source: "..\spikes\phase0\models\bge-m3-Q8_0.gguf";              DestDir: "{app}\models"; Flags: ignoreversion
Source: "..\spikes\phase0\models\bge-reranker-v2-m3-Q8_0.gguf";  DestDir: "{app}\models"; Flags: ignoreversion
#endif

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

{ インストール完了後に install.completed マーカーを書く（サイレント再実行時の 11 判定に使用）。
  config.json はここで書かない: InnoのSaveStringToFileはANSI書き出しのため日本語インストール先で
  文字化けし、化けたパスのゴミディレクトリが作られる実障害があった。代わりにバックエンドが
  初回起動時にUTF-8で絶対パス付きconfig.jsonを生成する（Program.cs EnsureDefaultConfig） }
procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
  begin
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
