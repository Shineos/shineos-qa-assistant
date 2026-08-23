; ============================================================================
; 社内知恵袋 - インストーラ（Inno Setup 6.7.3）
;
; 社内規定・業務マニュアルをナレッジ化して社内Q&Aを実現する
; 「ダブルクリック一発・初期設定なし」のローカルQ&A環境インストーラ
;
; ビルド: ISCC.exe installer.iss  → dist\ShineosQA-Setup-<ver>.exe
; 詳細: ../../docs/shineos-qa-assistant-build.md
; ============================================================================

#define MyAppName "社内知恵袋"
#define MyAppVersion "1.0.42"
#define MyAppPublisher "Shineos Inc."
#define MyAppURL "https://shineos.com"
#define MyAppExeName "open-webui.exe"
; 社内知恵袋（shineos-qa-assistant）専用の新GUID（旧「Shineos Local AI」とは別アプリとして扱う）
#define MyAppId "{{F8185C4F-C11F-4EF4-BF15-6EFFE7E5C47B}"

; --- 更新時に変更する定数 ----------------------------------------------------
#define PythonVersion "3.12.10"        ; python.org のアーカイブURLに依存
#define OpenWebuiVersion "0.11.0"      ; pip install open-webui==<version>
#define Port "8080"                    ; open-webui serve --port <Port>

[Setup]
AppId={#MyAppId}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL=https://shineos.com/contact/
DefaultDirName={autopf}\ShineosQA
DisableProgramGroupPage=yes
PrivilegesRequired=admin
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

; --- セットアップ中のみ使用するファイル（{tmp} に展開） ----------------------
; 長い処理（Python/Ollama/モデルDL/venv）はファイルコピー前に実行するため、
; dontcopy で展開し [Code] から ExtractTemporaryFile する
[Files]
Source: "..\scripts\preflight.ps1";        DestDir: "{tmp}"; Flags: dontcopy
Source: "..\scripts\run_all.ps1";          DestDir: "{tmp}"; Flags: dontcopy
Source: "..\scripts\setup_python.ps1";     DestDir: "{tmp}"; Flags: dontcopy
Source: "..\scripts\setup_ollama.ps1";     DestDir: "{tmp}"; Flags: dontcopy
Source: "..\scripts\setup_openwebui.ps1";  DestDir: "{tmp}"; Flags: dontcopy
Source: "..\scripts\register_service.ps1"; DestDir: "{tmp}"; Flags: dontcopy
Source: "..\scripts\configure_model.ps1";   DestDir: "{tmp}"; Flags: dontcopy
Source: "..\scripts\setup_knowledge.ps1";   DestDir: "{tmp}"; Flags: dontcopy
Source: "..\scripts\wait_ready.ps1";       DestDir: "{tmp}"; Flags: dontcopy
Source: "..\vendor\nssm.exe";              DestDir: "{tmp}"; Flags: dontcopy

; --- インストール先へ配置するファイル ----------------------------------------
Source: "..\vendor\nssm.exe";              DestDir: "{app}\tools"; Flags: ignoreversion
Source: "..\tools\filegen_server.py";     DestDir: "{app}\tools"; Flags: ignoreversion
Source: "..\tools\mcpo\tools\file_export_server.py"; DestDir: "{app}\tools\mcpo\tools"; Flags: ignoreversion
Source: "..\tools\mcpo\tools\file_export_mcp.py";    DestDir: "{app}\tools\mcpo\tools"; Flags: ignoreversion
Source: "..\tools\mcpo\tools\__init__.py";          DestDir: "{app}\tools\mcpo\tools"; Flags: ignoreversion
Source: "..\tools\mcpo\templates\*";      DestDir: "{app}\tools\mcpo\templates"; Flags: ignoreversion recursesubdirs
Source: "..\tools\mcpo\requirements.txt"; DestDir: "{app}\tools\mcpo"; Flags: ignoreversion
Source: "..\scripts\start_openwebui.bat";  DestDir: "{app}";       Flags: ignoreversion
Source: "..\scripts\configure_model.ps1";  DestDir: "{app}";       Flags: ignoreversion
Source: "..\scripts\setup_knowledge.ps1";  DestDir: "{app}";       Flags: ignoreversion
Source: "..\assets\app.ico";               DestDir: "{app}\assets"; Flags: ignoreversion
Source: "..\vendor\THIRD-PARTY-NOTICES.txt"; DestDir: "{app}";     Flags: ignoreversion
; WebView2 ラッパーアプリ（URL入力不要・閉じたらサービス停止）
Source: "..\dist\ShineosQA.App\*";    DestDir: "{app}\app";   Flags: ignoreversion recursesubdirs
; ナレッジ（社内文書）: このフォルダに PDF/Markdown を置くとインストール時に自動登録される
Source: "..\knowledge\*";             DestDir: "{app}\knowledge"; Flags: ignoreversion recursesubdirs

[Icons]
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\app\ShineosQA.exe"; IconFilename: "{app}\assets\app.ico"

; --- アンインストール時の完全削除 --------------------------------------------
[UninstallDelete]
Type: filesandordirs; Name: "{app}\data"
Type: filesandordirs; Name: "{app}\venv"
Type: filesandordirs; Name: "{app}\python"
Type: filesandordirs; Name: "{app}\logs"
Type: filesandordirs; Name: "{app}\tools"
Type: filesandordirs; Name: "{app}\app"
Type: filesandordirs; Name: "{app}\knowledge"
Type: files; Name: "{app}\install.log"
Type: files; Name: "{app}\configure_model.ps1"
Type: files; Name: "{app}\setup_knowledge.ps1"
Type: files; Name: "{userdesktop}\ShineosQA-はじめに.txt"

[Code]
var
  ProgressPage: TOutputProgressWizardPage;
  ModelPage: TInputOptionWizardPage;
  KnowledgePage: TInputOptionWizardPage;
  RamGB: Integer;
  PortFree: Boolean;
  OsOk: Boolean;
  SelectedModel: String;
  SelectedPort: Integer;
  SelectedKnowledgeDir: String;
  UninstallRemoveOllama: Boolean;

const
  PREFLIGHT_INI = 'preflight.ini';

{ ---------- 共通ヘルパー ---------- }

procedure ExtractSetupFiles;
begin
  ExtractTemporaryFile('preflight.ps1');
  ExtractTemporaryFile('run_all.ps1');
  ExtractTemporaryFile('setup_python.ps1');
  ExtractTemporaryFile('setup_ollama.ps1');
  ExtractTemporaryFile('setup_openwebui.ps1');
  ExtractTemporaryFile('register_service.ps1');
  ExtractTemporaryFile('configure_model.ps1');
  ExtractTemporaryFile('setup_knowledge.ps1');
  ExtractTemporaryFile('wait_ready.ps1');
  ExtractTemporaryFile('nssm.exe');
end;

{ preflight.ps1 を実行し、結果（OS・空きポート・RAM）を読み取る }
procedure RunPreflight;
var
  RC: Integer;
  Ini: String;
begin
  PortFree := True;
  OsOk := True;
  RamGB := 8;
  SelectedPort := {#Port};
  Ini := ExpandConstant('{tmp}\' + PREFLIGHT_INI);
  if Exec('powershell.exe',
      '-NoProfile -ExecutionPolicy Bypass -File "' + ExpandConstant('{tmp}\preflight.ps1') + '" -IniPath "' + Ini + '"',
      '', SW_HIDE, ewWaitUntilTerminated, RC) then
  begin
    OsOk := (GetIniString('preflight', 'os_ok', 'no', Ini) = 'yes');
    PortFree := (GetIniString('preflight', 'port_8080_free', 'no', Ini) = 'yes');
    SelectedPort := GetIniInt('preflight', 'port', {#Port}, 8080, 65535, Ini);
    RamGB := GetIniInt('preflight', 'ram_gb', 8, 4, 512, Ini);
  end;
end;

{ 一時フォルダのPowerShellスクリプトを実行（非表示・待機） }
function RunPowerShell(Script, Params: String; var ResultCode: Integer): Boolean;
begin
  Result := Exec('powershell.exe',
    '-NoProfile -ExecutionPolicy Bypass -File "' + ExpandConstant('{tmp}\' + Script) + '" ' + Params,
    '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
end;

{ 長いステップ用: PowerShellをコンソール表示で実行し、ライブログを確認できるようにする }
function RunPowerShellVisible(Script, Params: String; var ResultCode: Integer): Boolean;
begin
  Result := Exec('powershell.exe',
    '-NoProfile -ExecutionPolicy Bypass -File "' + ExpandConstant('{tmp}\' + Script) + '" ' + Params,
    '', SW_SHOWNORMAL, ewWaitUntilTerminated, ResultCode);
end;


{ ---------- ウィザード初期化 ---------- }

procedure InitializeWizard;
begin
  ExtractSetupFiles;
  RunPreflight;

  if not OsOk then
    MsgBox('Windows 10 / 11（64bit）以外の環境では 社内知恵袋 を利用できません。' + #13#10 +
           'インストールを中止してください。', mbError, MB_OK);

  if not PortFree then
    MsgBox('ポート 8080 が使用中のため、8081 以降の空きポート（' + IntToStr(SelectedPort) + '）でインストールします。', mbInformation, MB_OK);

  ModelPage := CreateInputOptionPage(wpSelectDir,
    'AIモデルの選択',
    'インストールするAIモデルを選択してください',
    '検出メモリ: ' + IntToStr(RamGB) + ' GB。使用ポート: ' + IntToStr(SelectedPort) + '（8080 が空いていれば 8080）。' + #13#10 +
    '動作が重い場合は下の「軽量」を選択してください。',
    True, False);
  ModelPage.Add('qwen2.5:3b（推奨）　高速・確実・約1.9GB・応答約1秒（8GB機でも快適）');
  ModelPage.Add('qwen2.5:7b（高品質）　16GB以上のメモリ推奨・約4.7GB・応答数秒');
  ModelPage.Add('qwen2.5:1.5b（軽量）　8GB機に最適・最速・約1GB・応答1秒未満');
  { 検出メモリに応じて既定を自動選択（8GB未満は軽量、16GB以上は高品質、それ以外は推奨） }
  if RamGB < 8 then
    ModelPage.SelectedValueIndex := 2
  else if RamGB >= 16 then
    ModelPage.SelectedValueIndex := 1
  else
    ModelPage.SelectedValueIndex := 0;

  { ナレッジ（社内文書）の登録方法（任意）
    TInputDirWizardPage はパス検証があり「指定しない」を表現できないため、
    ラジオボタン + フォルダ選択ダイアログ（BrowseForFolder）方式にする }
  KnowledgePage := CreateInputOptionPage(ModelPage.ID,
    '社内文書（ナレッジ）フォルダ',
    '社内文書の登録方法を選択してください',
    '「指定しない」を選ぶと、インストーラ同梱のサンプル（QA_list.md）のみ登録されます。' + #13#10 +
    '「フォルダを指定する」を選ぶと、フォルダ選択ダイアログが表示されます。' + #13#10 +
    'インストール後は、アプリ画面からも資料を追加できます。',
    True, False);
  KnowledgePage.Add('指定しない（インストーラ同梱のサンプルのみ登録）');
  KnowledgePage.Add('社内文書フォルダを指定する（選択ダイアログを表示）');
  KnowledgePage.SelectedValueIndex := 0;
end;

{ ---------- 長い処理（キャンセル可能な進捗ページ） ---------- }

function RunLongSteps(AppDir: String): Boolean;
var
  RC: Integer;
  StepError: AnsiString;  // LoadStringFromFile は AnsiString が必須
begin
  Result := False;
  ProgressPage := CreateOutputProgressPage('インストール中',
    '社内知恵袋 のセットアップを実行しています。' + #13#10 +
    '完了まで約20〜60分かかります（Ollama本体1.5GB＋AIモデル2.5GB＋Python・Open WebUI等、合計約7GBのダウンロードを含みます）。' + #13#10 +
    'インストール中はウィンドウを閉じないでください。');
  try
    ProgressPage.Show;

    ProgressPage.SetText('環境チェック中...', '');
    RunPreflight;
    if not OsOk then
    begin
      MsgBox('Windows 10 / 11（64bit）以外の環境ではインストールできません。', mbError, MB_OK);
      Exit;
    end;
    if not PortFree then
    begin
      MsgBox('ポート 8080〜8099 がすべて使用中のため続行できません。' + #13#10 +
             'いずれかのプログラムを終了してから「次へ」をもう一度押してください。', mbError, MB_OK);
      Exit;
    end;

    ProgressPage.SetProgress(5, 100);
    ProgressPage.SetText('インストール中...', 'ログは表示されているログウィンドウにリアルタイム表示されます（各ステップの開始・成功・失敗を明記）');
    if not RunPowerShellVisible('run_all.ps1',
        '-AppDir "' + AppDir + '" -TmpDir "' + ExpandConstant('{tmp}') + '" -Model "' + SelectedModel + '" -PythonVersion "{#PythonVersion}" -OpenWebuiVersion "{#OpenWebuiVersion}"', RC)
       or (RC <> 0) then
    begin
      StepError := '';
      if LoadStringFromFile(ExpandConstant('{tmp}\step_error.txt'), StepError) and (StepError <> '') then
        MsgBox('インストールに失敗しました。' + #13#10 + #13#10 +
               '失敗したステップ: ' + StepError + #13#10 + #13#10 +
               'ログ: ' + AppDir + '\install.log' + #13#10 +
               '「次へ」をもう一度押すと続きから再開できます。', mbError, MB_OK)
      else
        MsgBox('インストールに失敗しました。' + #13#10 +
               'ログ: ' + AppDir + '\install.log' + #13#10 +
               '「次へ」をもう一度押すと続きから再開できます。', mbError, MB_OK);
      Exit;
    end;

    ProgressPage.SetProgress(100, 100);
    Result := True;
  finally
    ProgressPage.Hide;
    ProgressPage.Free;
  end;
end;

function NextButtonClick(CurPageID: Integer): Boolean;
var
  AppDir: String;
begin
  Result := True;
  { ナレッジ登録方法の選択（「指定する」を選んだ場合のみフォルダ選択ダイアログを表示） }
  if CurPageID = KnowledgePage.ID then
  begin
    SelectedKnowledgeDir := '';
    if KnowledgePage.SelectedValueIndex = 1 then
    begin
      if not BrowseForFolder('社内文書（PDF・Markdown）を置いたフォルダを選択してください', SelectedKnowledgeDir, False) then
        SelectedKnowledgeDir := '';
    end;
  end;
  if CurPageID = wpReady then
  begin
    AppDir := ExpandConstant('{app}');
    if not IsAdmin then
    begin
      MsgBox('インストールには管理者権限が必要です。' + #13#10 +
             'exeを右クリック →「管理者として実行」を選択してやり直してください。', mbError, MB_OK);
      Result := False;
      Exit;
    end;
    if ModelPage.SelectedValueIndex = 1 then
      SelectedModel := 'qwen2.5:7b'
    else if ModelPage.SelectedValueIndex = 2 then
      SelectedModel := 'qwen2.5:1.5b'
    else
      SelectedModel := 'qwen2.5:3b';
    Result := RunLongSteps(AppDir);
  end;
end;

{ ---------- 仕上げ（ファイルコピー後） ---------- }

procedure WriteUsageFile(AppDir: String);
var
  S: String;
begin
  S := '社内知恵袋 - はじめに' + #13#10 +
       '=====================================' + #13#10 + #13#10 +
       'デスクトップの「社内知恵袋」を開くと、そのまま社内Q&Aを始められます（ログイン不要・初期設定なし）。' + #13#10 + #13#10 +
       '・使用モデル: ' + SelectedModel + '（文書検索用: bge-m3）' + #13#10 +
       '・社内Q&A（ナレッジ検索）: ' + AppDir + '\knowledge フォルダの社内文書（PDF・Markdown）を自動登録済み。' + #13#10 +
       '　質問すると、根拠となった文書名・該当箇所つきで回答します' + #13#10 +
       '・用途別プリセット: チャットのモデル選択で「経費精算ガイド」「ITヘルプデスク」に切り替えられます' + #13#10 +
       '・ナレッジの追加: アプリ画面（Open WebUI）のナレッジメニューから、いつでも資料（PDF・Markdown）を追加できます' + #13#10 +
       '　（ファイル名や文書の先頭に【経費精算】などのタグを付けると検索精度が向上します）' + #13#10 +
       '・ナレッジに無いことは「該当する記載がありません」と回答します（ハルシネーション抑制）' + #13#10 +
       '・Web検索（オプション）: チャットのWeb検索ボタンをONにすると利用できます（DuckDuckGo・APIキー不要）' + #13#10 +
       '・完全オフライン: Web検索ボタンをOFFのままにすれば、一切インターネットに接続しません' + #13#10 + #13#10 +
       '・PCを再起動しても自動で起動します（Windowsサービス: ShineosQA）' + #13#10 +
       '・アプリを閉じるとサービスも停止します（再起動後は自動で再開）' + #13#10 +
       '・アンインストール: 設定アプリ → アプリ → 社内知恵袋' + #13#10 +
       '・再インストールするとデータ（ナレッジ・アップロードした文書など）は初期化されます' + #13#10 + #13#10 +
       '不具合やご相談は https://shineos.com/contact/ まで。' + #13#10;
  SaveStringToFile(ExpandConstant('{userdesktop}\ShineosQA-はじめに.txt'), S, True);
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  RC: Integer;
  AppDir: String;
  Ready: Boolean;
begin
  if CurStep = ssPostInstall then
  begin
    AppDir := ExpandConstant('{app}');
    Ready := False;
    ProgressPage := CreateOutputProgressPage('仕上げ',
      'サービスを登録して起動しています...');
    try
      ProgressPage.Show;

      ProgressPage.SetText('Windowsサービスを登録中...', '');
      Ready := RunPowerShell('register_service.ps1', '-AppDir "' + AppDir + '" -Model "' + SelectedModel + '" -Port ' + IntToStr(SelectedPort), RC) and (RC = 0);
      if not Ready then
      begin
        MsgBox('サービスの登録に失敗しました。' + #13#10 +
               'ログ: ' + AppDir + '\install.log' + #13#10 + #13#10 +
               '手動で起動する場合は ' + AppDir + '\start_openwebui.bat をダブルクリックしてください。',
               mbError, MB_OK);
      end
      else
      begin
        ProgressPage.SetText('Open WebUI を起動しています（初回は数分かかります）...', '');
        Ready := RunPowerShell('wait_ready.ps1', '-Port ' + IntToStr(SelectedPort) + ' -TimeoutSec 180', RC) and (RC = 0);
        if not Ready then
          MsgBox('Open WebUI の起動確認がタイムアウトしました。' + #13#10 +
                 'ブラウザで http://localhost:' + IntToStr(SelectedPort) + ' を開いて起動を確認してください。' + #13#10 +
                 'ログ: ' + AppDir + '\logs\openwebui.err.log', mbInformation, MB_OK);

        { AIモデル設定（qwen3系の思考モード無効化とコンテキスト長の最適化） }
        ProgressPage.SetText('AIモデルを設定しています...', '');
        if not (RunPowerShell('configure_model.ps1', '-BaseUrl "http://localhost:' + IntToStr(SelectedPort) + '" -Model "' + SelectedModel + '" -LogFile "' + AppDir + '\logs\configure_model.log"', RC) and (RC = 0)) then
          MsgBox('モデル設定に失敗しました。' + #13#10 +
                 'qwen3系モデルの場合、思考モードが無効化されないため応答が遅くなることがあります。' + #13#10 +
                 'ログ: ' + AppDir + '\logs\openwebui.err.log', mbInformation, MB_OK);

        { ナレッジ自動登録（knowledgeフォルダの社内文書をベクトル化してRAG検索可能にする） }
        { インストール画面で指定されたフォルダの文書をインストール先の knowledge フォルダへコピー }
        if SelectedKnowledgeDir <> '' then
        begin
          ProgressPage.SetText('社内文書をコピーしています...', '');
          Exec('robocopy.exe', '"' + SelectedKnowledgeDir + '" "' + AppDir + '\knowledge" /E /NFL /NDL /NJH /NJS /NC /NS', '', SW_HIDE, ewWaitUntilTerminated, RC);
          { robocopy は 0〜7 が成功（8以上はエラー） }
          if RC >= 8 then
            MsgBox('社内文書フォルダのコピーに失敗しました（コード: ' + IntToStr(RC) + '）。' + #13#10 +
                   'インストール後、アプリ画面から資料を追加してください。', mbInformation, MB_OK);
        end;
        ProgressPage.SetText('ナレッジ（社内文書）を登録しています...', '');
        if not (RunPowerShell('setup_knowledge.ps1', '-BaseUrl "http://localhost:' + IntToStr(SelectedPort) + '" -KnowledgeDir "' + AppDir + '\knowledge" -LogFile "' + AppDir + '\logs\setup_knowledge.log"', RC) and (RC = 0)) then
          MsgBox('ナレッジ登録に失敗しました。' + #13#10 +
                 'インストール後、アプリ画面（Open WebUI）から資料を追加できます。' + #13#10 +
                 'ログ: ' + AppDir + '\logs\setup_knowledge.log', mbInformation, MB_OK);
      end;

      { 使用ポートをラッパーアプリ用に保存 }
      SaveStringToFile(ExpandConstant('{app}\port.txt'), IntToStr(SelectedPort), False);

      WriteUsageFile(AppDir);

      { 一般ユーザーでもサービスを開始・停止できるように DACL を設定 }
      { （WebView2ラッパーアプリがUACなしで start/stop するために必要） }
      ProgressPage.SetText('サービスをユーザー操作可能に設定中...', '');
      Exec('sc.exe', 'sdset ShineosQA "D:(A;;0x34;;;AU)(A;;GA;;;SY)(A;;GA;;;BA)"', '', SW_HIDE, ewWaitUntilTerminated, RC);

      WizardForm.FinishedLabel.Caption :=
        'インストールが完了しました。' + #13#10 + #13#10 +
        'デスクトップの「社内知恵袋」をダブルクリックすると、アプリ画面が開きます（URL入力不要）。' + #13#10 +
        'アプリを閉じるとサービスも停止します。' + #13#10 + #13#10 +
        '詳しい使い方はデスクトップの「ShineosQA-はじめに.txt」を参照してください。';
    finally
      ProgressPage.Hide;
      ProgressPage.Free;
    end;
  end;
end;

{ ---------- アンインストール ---------- }

{ アンインストール開始時に「Ollama・モデルも削除するか」を確認する }
function InitializeUninstall: Boolean;
begin
  Result := True;
  UninstallRemoveOllama := False;
  if not UninstallSilent then
    UninstallRemoveOllama := MsgBox(
      'Ollama（LLM実行エンジン）とダウンロード済みAIモデル（選択モデルにより約2〜5GB）も削除しますか？' + #13#10 + #13#10 +
      '「はい」: Ollama本体・AIモデル・関連サービス・性能設定をすべて削除します。' + #13#10 +
      '　　　　 再インストール時はOllamaとモデルの再ダウンロード（約2時間）が必要です。' + #13#10 +
      '「いいえ」: 社内知恵袋 のファイルとサービスだけを削除し、Ollama は残します。',
      mbConfirmation, MB_YESNO) = IDYES;
end;

{ Ollama 本体・モデル・サービスを完全削除する }
procedure RemoveOllamaCompletely;
var
  RC: Integer;
  OllamaUnins: String;
begin
  { 実行中のプロセスを停止（直接起動・トレイアプリ） }
  Exec('taskkill.exe', '/IM ollama.exe /F', '', SW_HIDE, ewWaitUntilTerminated, RC);
  Exec('taskkill.exe', '/IM "ollama app.exe" /F', '', SW_HIDE, ewWaitUntilTerminated, RC);

  { 公式サービスとフォールバックサービスを停止・削除 }
  Exec(ExpandConstant('{sys}\sc.exe'), 'stop Ollama', '', SW_HIDE, ewWaitUntilTerminated, RC);
  Exec(ExpandConstant('{sys}\sc.exe'), 'delete Ollama', '', SW_HIDE, ewWaitUntilTerminated, RC);
  Exec(ExpandConstant('{sys}\sc.exe'), 'stop ShineosOllama', '', SW_HIDE, ewWaitUntilTerminated, RC);
  Exec(ExpandConstant('{sys}\sc.exe'), 'delete ShineosOllama', '', SW_HIDE, ewWaitUntilTerminated, RC);

  { 公式アンインストーラで Ollama 本体を削除（Inno製のため unins000.exe が存在する） }
  OllamaUnins := ExpandConstant('{localappdata}\Programs\Ollama\unins000.exe');
  if FileExists(OllamaUnins) then
  begin
    Exec(OllamaUnins, '/VERYSILENT /SUPPRESSMSGBOXES /NORESTART', '', SW_HIDE, ewWaitUntilTerminated, RC);
  end;

  { 残ったモデル・データ・プログラムを確実に削除 }
  DelTree(ExpandConstant('{userprofile}\.ollama'), True, True, True);
  DelTree(ExpandConstant('{localappdata}\Programs\Ollama'), True, True, True);
  DelTree(ExpandConstant('{appdata}\Ollama'), True, True, True);
  { サービス（SYSTEMアカウント）がモデルを保存した場合は systemprofile 側も削除 }
  DelTree(ExpandConstant('{sys}\..\systemprofile\.ollama'), True, True, True);

  { インストーラが設定した性能チューニング用環境変数を削除 }
  RegDeleteValue(HKEY_LOCAL_MACHINE, 'SYSTEM\CurrentControlSet\Control\Session Manager\Environment', 'OLLAMA_MAX_LOADED_MODELS');
  RegDeleteValue(HKEY_LOCAL_MACHINE, 'SYSTEM\CurrentControlSet\Control\Session Manager\Environment', 'OLLAMA_KV_CACHE_TYPE');
  RegDeleteValue(HKEY_LOCAL_MACHINE, 'SYSTEM\CurrentControlSet\Control\Session Manager\Environment', 'OLLAMA_FLASH_ATTENTION');
  RegDeleteValue(HKEY_LOCAL_MACHINE, 'SYSTEM\CurrentControlSet\Control\Session Manager\Environment', 'OLLAMA_KEEP_ALIVE');
  RegDeleteValue(HKEY_LOCAL_MACHINE, 'SYSTEM\CurrentControlSet\Control\Session Manager\Environment', 'OLLAMA_NUM_PARALLEL');
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  RC: Integer;
begin
  if CurUninstallStep = usUninstall then
  begin
    { サービス停止 → 削除（ファイル削除前に実行される） }
    Exec(ExpandConstant('{sys}\sc.exe'), 'stop ShineosQA', '', SW_HIDE, ewWaitUntilTerminated, RC);
    Exec(ExpandConstant('{sys}\sc.exe'), 'delete ShineosQA', '', SW_HIDE, ewWaitUntilTerminated, RC);
    if UninstallRemoveOllama then
      RemoveOllamaCompletely
    else
    begin
      { Ollama を残す場合はフォールバックサービスのみ削除 }
      Exec(ExpandConstant('{sys}\sc.exe'), 'stop ShineosOllama', '', SW_HIDE, ewWaitUntilTerminated, RC);
      Exec(ExpandConstant('{sys}\sc.exe'), 'delete ShineosOllama', '', SW_HIDE, ewWaitUntilTerminated, RC);
    end;
  end;
end;
