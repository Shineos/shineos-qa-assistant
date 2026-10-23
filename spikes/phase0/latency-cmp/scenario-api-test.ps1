param([string]$Label = "api-run", [string]$Base = "http://127.0.0.1:8300")
# API scenario test (v2.6.0): /api/chat 以外の機能面(作成モード/エクスポート/履歴操作/ナレッジ管理/
# 設定反映/キャッシュ/モデル一覧/出典プレビュー)を black-box で検証する。
# scenario-test.ps1 と同じ出力形式(TSV: tag, PASS/FAIL, detail, ms)。実行後、このランナーが
# 作成したチャット/ナレッジを削除する(履歴汚染の再発防止 - scenario-test.ps1 と同じ方針)。
# 注意: PS5.1 互換のため本ファイルは ASCII のみ。日本語は UTF-8 一時ファイル経由で送受信する。
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.Encoding]::UTF8
$base = $Base
$utf8 = New-Object Text.UTF8Encoding($false)
$script:createdChats = @()
$script:createdFileIds = @()
$ansDir = Join-Path (Split-Path -Parent $MyInvocation.MyCommand.Path) "answers"
if (-not (Test-Path $ansDir)) { [IO.Directory]::CreateDirectory($ansDir) | Out-Null }
$script:results = New-Object System.Collections.Generic.List[string]

function Invoke-Api([string]$method, [string]$path, $bodyObj = $null, [string]$contentType = "application/json") {
  # curl.exe 経由で HTTP 呼び出し。戻り値: @{ code = int; body = string }
  $tmp = $null; $args = @('-s', '-X', $method, ($base + $path), '-w', "`n%{http_code}")
  if ($bodyObj -ne $null) {
    $tmp = [IO.Path]::GetTempFileName()
    if ($contentType -eq 'application/json') {
      $json = $bodyObj | ConvertTo-Json -Compress -Depth 6
      [IO.File]::WriteAllText($tmp, $json, $utf8)
    } else {
      [IO.File]::WriteAllText($tmp, [string]$bodyObj, $utf8)
    }
    $args += @('-H', "Content-Type: $contentType", '--data-binary', "@$tmp")
  }
  $out = [IO.Path]::GetTempFileName()
  $codeOut = (& curl.exe @args -o $out -w "%{http_code}") -join ""
  $raw = [IO.File]::ReadAllText($out, [Text.Encoding]::UTF8)
  Remove-Item $out -Force
  if ($tmp) { Remove-Item $tmp -Force }
  $code = 0; $body = $raw
  if ($codeOut -match '^\d{3}$') { $code = [int]$Matches[0] }
  return @{ code = $code; body = $body }
}

function Invoke-ApiUpload([string]$path, [string]$file, [string]$name) {
  $out = [IO.Path]::GetTempFileName()
  $codeOut = (& curl.exe -s -X POST ($base + $path) -F ("file=@{0};filename={1};type=application/octet-stream" -f $file, $name) -o $out -w "%{http_code}") -join ""
  $raw = [IO.File]::ReadAllText($out, [Text.Encoding]::UTF8)
  Remove-Item $out -Force
  $code = 0; $body = $raw
  if ($codeOut -match '^\d{3}$') { $code = [int]$Matches[0] }
  return @{ code = $code; body = $body }
}

function Ask-Sse([string]$q, [string]$uuid, $draft = $null) {
  # /api/chat (SSE) を実行し delta 全文 / done / meta を返す
  $payload = @{ chat_uuid = $uuid; message = $q }
  if ($draft -ne $null) { $payload.draft = $draft }
  $tmp = [IO.Path]::GetTempFileName()
  [IO.File]::WriteAllText($tmp, ($payload | ConvertTo-Json -Compress -Depth 6), $utf8)
  $out = [IO.Path]::GetTempFileName()
  $null = & curl.exe -s -N --max-time 300 -X POST ($base + '/api/chat') -H "Content-Type: application/json; charset=utf-8" --data-binary "@$tmp" -o $out
  $lines = [IO.File]::ReadAllLines($out, [Text.Encoding]::UTF8)
  Remove-Item $tmp, $out -Force
  $ev = ''; $answer = New-Object Text.StringBuilder; $done = $null; $meta = $null
  foreach ($ln in $lines) {
    if ($ln.StartsWith("event: ")) { $ev = $ln.Substring(7).Trim() }
    elseif ($ln.StartsWith("data: ")) {
      $json = $ln.Substring(6)
      try { $o = $json | ConvertFrom-Json } catch { continue }
      if ($ev -eq "delta") { [void]$answer.Append($o.content) }
      elseif ($ev -eq "done") { $done = $o }
      elseif ($ev -eq "meta") { $meta = $o }
    }
  }
  if ($meta -and $meta.chat_uuid) { $script:createdChats += $meta.chat_uuid }
  return @{ text = $answer.ToString(); done = $done; meta = $meta }
}

function Record([string]$tag, [bool]$ok, [string]$detail, [int]$ms) {
  $line = "{0}`t{1}`t{2}`t{3}" -f $tag, $(if ($ok) { "PASS" } else { "FAIL" }), $detail, $ms
  $script:results.Add($line)
  Write-Output $line
}

function Get-JsonProp([string]$json, [string]$prop) {
  try { $o = $json | ConvertFrom-Json } catch { return $null }
  return $o.$prop
}

Write-Output "=== $Label (api scenarios) ==="
$swAll = [Diagnostics.Stopwatch]::StartNew()

# ---- a01: 作成モード - 参照なしで自動参照(ナレッジ全体)からFAQ集を生成 (v2.6.0 auto-refs) ----
$sw = [Diagnostics.Stopwatch]::StartNew()
$r = Ask-Sse "出張規程からFAQ集を作成してください" ("scn-api-a01-" + [Guid]::NewGuid().ToString('N').Substring(0,8)) @{ template = "auto" }
$d = $r.done
$ok = ($d -ne $null -and $d.kind -eq "draft" -and $d.sources -and @($d.sources).Count -ge 1 -and $r.text.Length -gt 50)
$srcCount = 0; if ($d -and $d.sources) { $srcCount = @($d.sources).Count }
Record "a01-draft-auto-refs" $ok ("kind={0} srcs={1} chars={2} tpl={3}" -f $(if ($d) { $d.kind } else { "none" }), $srcCount, $r.text.Length, $(if ($d) { $d.template } else { "?" })) $sw.ElapsedMilliseconds
if ($d) { $script:createdChats += $r.meta.chat_uuid }

# ---- a02: draft mode - explicit template (procedure) is reflected in done.template ----
# done.template carries the LABEL; compare via codepoints (this file must stay ASCII-only)
$tplProcedure = [string][char]0x624B + [char]0x9806 + [char]0x66F8
$sw = [Diagnostics.Stopwatch]::StartNew()
$uuidA02 = "scn-api-a02-" + [Guid]::NewGuid().ToString('N').Substring(0,8)
$r2 = Ask-Sse "会議室の予約手順をまとめてください" $uuidA02 @{ template = "procedure" }
$d2 = $r2.done
$ok = ($d2 -ne $null -and $d2.kind -eq "draft" -and ($d2.template -eq "procedure" -or $d2.template -eq $tplProcedure))
Record "a02-draft-template-procedure" $ok ("tpl={0} chars={1}" -f $(if ($d2) { $d2.template } else { "none" }), $r2.text.Length) $sw.ElapsedMilliseconds

# ---- a03: draft .md export (UTF-8 BOM + Markdown heading) ----
# NOTE: server replaces an unknown chat_uuid with a new uuid - use meta.chat_uuid
$sw = [Diagnostics.Stopwatch]::StartNew()
$realUuidA02 = $r2.meta.chat_uuid
$msgId = 0
$detail = Get-JsonProp (Invoke-Api 'GET' ("/api/chats/" + $realUuidA02)).body "messages"
if ($detail) { foreach ($m in $detail) { if ($m.kind -eq "draft") { $msgId = $m.id } } }
$ok = $false; $head = ""
if ($msgId -gt 0) {
  $exp = Invoke-Api 'GET' ("/api/messages/" + $msgId + "/export?format=md")
  $head = $exp.body.Substring(0, [Math]::Min(40, $exp.body.Length))
  $ok = ($exp.code -eq 200 -and $exp.body.Length -gt 100 -and $exp.body.Contains("#"))
}
[IO.File]::WriteAllText((Join-Path $ansDir "$Label-a03-export.md"), $(if ($msgId -gt 0) { (Invoke-Api 'GET' ("/api/messages/" + $msgId + "/export?format=md")).body } else { "" }), $utf8)
Record "a03-draft-export-md" $ok ("msg={0} head={1}" -f $msgId, ($head -replace "\s", " ")) $sw.ElapsedMilliseconds

# ---- a04: 下書きの .docx エクスポート (OOXML zip: PK header) ----
$sw = [Diagnostics.Stopwatch]::StartNew()
$ok = $false; $info = "msg=0"
if ($msgId -gt 0) {
  $exp = Invoke-Api 'GET' ("/api/messages/" + $msgId + "/export?format=docx")
  $bytes = [Text.Encoding]::GetEncoding("ISO-8859-1").GetBytes($exp.body)
  $pk = ($bytes.Length -gt 2 -and $bytes[0] -eq 0x50 -and $bytes[1] -eq 0x4B)
  $ok = ($exp.code -eq 200 -and $pk)
  $info = "msg={0} bytes={1} pk={2}" -f $msgId, $bytes.Length, $pk
}
Record "a04-draft-export-docx" $ok $info $sw.ElapsedMilliseconds

# ---- a05: history - rename (POST /api/chats + rename + verify via title search) ----
# NOTE known issue: title search INNER-JOINs messages, so a chat with zero messages
# never matches. Send one short clarify question first to create messages, then search.
$sw = [Diagnostics.Stopwatch]::StartNew()
$uuidA05 = (Get-JsonProp (Invoke-Api 'POST' '/api/chats').body "uuid")
$script:createdChats += $uuidA05
$marker = "v260apiren-" + [Guid]::NewGuid().ToString('N').Substring(0,6)
$null = Invoke-Api 'POST' ("/api/chats/" + $uuidA05 + "/rename") @{ title = $marker }
$null = Ask-Sse "テスト" $uuidA05
$found = (Invoke-Api 'GET' ("/api/chats?q=" + $marker)).body
$ok = ($found -and $found.Contains($uuidA05))
Record "a05-history-rename" $ok ("uuid={0} found={1}" -f $uuidA05.Substring(0,8), $(if ($ok) { "yes" } else { "none" })) $sw.ElapsedMilliseconds

# ---- a06: 履歴 - ブックマーク切替 + ブックマークのみ表示フィルタ ----
$sw = [Diagnostics.Stopwatch]::StartNew()
$null = Invoke-Api 'POST' ("/api/chats/" + $uuidA05 + "/bookmark") @{ bookmarked = $true }
$bmList = Invoke-Api 'GET' '/api/chats?bookmarked=1'
$onOk = $bmList.body.Contains($uuidA05)
$null = Invoke-Api 'POST' ("/api/chats/" + $uuidA05 + "/bookmark") @{ bookmarked = $false }
$bmList2 = Invoke-Api 'GET' '/api/chats?bookmarked=1'
$offOk = -not $bmList2.body.Contains($uuidA05)
Record "a06-history-bookmark" ($onOk -and $offOk) ("on={0} off={1}" -f $onOk, $offOk) $sw.ElapsedMilliseconds

# ---- a07: 履歴 - アーカイブ切替 (既定一覧から外れ、archived=1 に現れる) ----
$sw = [Diagnostics.Stopwatch]::StartNew()
$null = Invoke-Api 'POST' ("/api/chats/" + $uuidA05 + "/archive") @{ archived = $true }
$defList = Invoke-Api 'GET' '/api/chats'
$goneFromDefault = -not $defList.body.Contains($uuidA05)
$arcList = Invoke-Api 'GET' '/api/chats?archived=1'
$inArchived = $arcList.body.Contains($uuidA05)
$null = Invoke-Api 'POST' ("/api/chats/" + $uuidA05 + "/archive") @{ archived = $false }
Record "a07-history-archive" ($goneFromDefault -and $inArchived) ("hidden={0} inArchived={1}" -f $goneFromDefault, $inArchived) $sw.ElapsedMilliseconds

# ---- a08: ナレッジ - 単体アップロード → 一覧 → 元ファイル配信 → 削除 ----
$sw = [Diagnostics.Stopwatch]::StartNew()
$tmpMd = [IO.Path]::GetTempFileName() + ".md"
[IO.File]::WriteAllText($tmpMd, "V260APIE2E marker: the conference room projector lamp part number is LP-47.", $utf8)
$up = Invoke-ApiUpload '/api/knowledge' $tmpMd "v260-api-e2e.md"
$upJson = $up.body | ConvertFrom-Json
$fok = $false; $fid = 0
if ($upJson -and $upJson.results -and @($upJson.results).Count -ge 1) {
  $fok = [bool]$upJson.results[0].ok
  $fid = $upJson.results[0].file_id
}
$script:createdFileIds += $fid
$inList = (Invoke-Api 'GET' '/api/knowledge').body.Contains("v260-api-e2e.md")
$fget = Invoke-Api 'GET' ("/api/knowledge/" + $fid + "/file")
$fileOk = ($fget.code -eq 200 -and $fget.body.Contains("LP-47"))
$del = Invoke-Api 'DELETE' ("/api/knowledge/" + $fid)
$goneList = -not (Invoke-Api 'GET' '/api/knowledge').body.Contains("v260-api-e2e.md")
$fget2 = Invoke-Api 'GET' ("/api/knowledge/" + $fid + "/file")
$goneFile = ($fget2.code -eq 404)
Remove-Item $tmpMd -Force
Record "a08-knowledge-lifecycle" ($fok -and $inList -and $fileOk -and $goneList -and $goneFile) ("up={0} list={1} file={2} deleted={3}/{4}" -f $fok, $inList, $fileOk, $goneList, $goneFile) $sw.ElapsedMilliseconds

# ---- a09: ナレッジ - エラーエントリの再取り込み(retry)が graceful (5xx でない) ----
$sw = [Diagnostics.Stopwatch]::StartNew()
$errId = 0
$kbList = (Invoke-Api 'GET' '/api/knowledge').body | ConvertFrom-Json
foreach ($f in $kbList) { if ($f.status -eq "error") { $errId = $f.file_id; break } }
$ok = $true; $info = "no-error-entry (skip)"
if ($errId -gt 0) {
  $ret = Invoke-Api 'POST' ("/api/knowledge/" + $errId + "/retry")
  $ok = ($ret.code -lt 500)
  $info = "id={0} code={1}" -f $errId, $ret.code
}
Record "a09-knowledge-retry-graceful" $ok $info $sw.ElapsedMilliseconds

# ---- a10: 設定 - web_search / ext.drawing の roundtrip (元に戻す) ----
$sw = [Diagnostics.Stopwatch]::StartNew()
$before = (Invoke-Api 'GET' '/api/settings').body | ConvertFrom-Json
$null = Invoke-Api 'POST' '/api/settings' @{ web_search = $true }
$s1 = ((Invoke-Api 'GET' '/api/settings').body | ConvertFrom-Json).web_search
$null = Invoke-Api 'POST' '/api/settings' @{ "ext.drawing" = $true }
$s2 = ((Invoke-Api 'GET' '/api/settings').body | ConvertFrom-Json).extensions.drawing
$null = Invoke-Api 'POST' '/api/settings' @{ web_search = $false; "ext.drawing" = $false }
$after = (Invoke-Api 'GET' '/api/settings').body | ConvertFrom-Json
$restored = (-not $after.web_search) -and (-not $after.extensions.drawing)
Record "a10-settings-roundtrip" ($s1 -and $s2 -and $restored) ("web={0} ext={1} restored={2}" -f $s1, $s2, $restored) $sw.ElapsedMilliseconds

# ---- a11: answer cache - 2nd identical question must be done.cached=true ----
# NOTE: a no-hit answer is not cached (requires sources>0); retry up to 3 times on no-hit
$sw = [Diagnostics.Stopwatch]::StartNew()
$q = "慶弔休暇の日数をすべて教えてください"
$null = Invoke-Api 'POST' '/api/cache-clear'
$attempt = 0
do {
  $attempt++
  $r1c = Ask-Sse $q ("scn-api-a11a-" + [Guid]::NewGuid().ToString('N').Substring(0,8) + "-" + $attempt)
  $cached1 = $false; if ($r1c.done) { $cached1 = [bool]$r1c.done.cached }
} while (-not $cached1 -and $r1c.text.Contains("該当する記載がありません") -and $attempt -lt 3)
$r2c = Ask-Sse $q ("scn-api-a11b-" + [Guid]::NewGuid().ToString('N').Substring(0,8))
$c2 = $false; if ($r2c.done) { $c2 = [bool]$r2c.done.cached }
Record "a11-cache-hit" ((-not $cached1) -and $c2) ("attempts={0} first={1} second={2}" -f $attempt, $cached1, $c2) $sw.ElapsedMilliseconds

# ---- a12: モデル一覧 - カタログと導入状況 (needs_wizard 含む) ----
$sw = [Diagnostics.Stopwatch]::StartNew()
$m = (Invoke-Api 'GET' '/api/models')
$mj = $m.body | ConvertFrom-Json
$nModels = 0; if ($mj -and $mj.models) { $nModels = @($mj.models).Count }
$ok = ($m.code -eq 200 -and $nModels -ge 1 -and ($m.body.Contains("needs_wizard")))
Record "a12-models-status" $ok ("code={0} models={1}" -f $m.code, $nModels) $sw.ElapsedMilliseconds

# ---- a13: 出典プレビュー - PDF のページ画像 + ヒット頁 (図面PDF) ----
$sw = [Diagnostics.Stopwatch]::StartNew()
$pvId = 0
foreach ($f in $kbList) { if ($f.name -eq "drawing-sample.pdf") { $pvId = $f.file_id } }
$ok = $false; $info = "drawing-sample.pdf not found"
if ($pvId -gt 0) {
  $pv = Invoke-Api 'GET' ("/api/knowledge/" + $pvId + "/preview?snippet=" + [Uri]::EscapeDataString("材質"))
  $pvj = $pv.body | ConvertFrom-Json
  $ok = ($pv.code -eq 200 -and $pvj.hit_page -ge 1 -and @($pvj.pages).Count -ge 1)
  $info = "id={0} code={1} hitPage={2}" -f $pvId, $pv.code, $(if ($pvj) { $pvj.hit_page } else { "?" })
}
Record "a13-source-preview" $ok $info $sw.ElapsedMilliseconds

# ---- a14: chat detail - message persistence (user/assistant + sources_json) ----
# uses the a11b cache-hit chat (sources_json is restored through the cache too)
$sw = [Diagnostics.Stopwatch]::StartNew()
$realUuidA11 = $r2c.meta.chat_uuid
$cd = (Invoke-Api 'GET' ("/api/chats/" + $realUuidA11)).body | ConvertFrom-Json
$nMsg = 0; if ($cd -and $cd.messages) { $nMsg = @($cd.messages).Count }
$hasUser = $false; $hasAsst = $false; $hasSrc = $false
if ($cd -and $cd.messages) {
  foreach ($m in $cd.messages) {
    if ($m.role -eq "user") { $hasUser = $true }
    if ($m.role -eq "assistant") { $hasAsst = $true; if ($m.sources_json -and $m.sources_json.Length -gt 2) { $hasSrc = $true } }
  }
}
Record "a14-chat-persistence" ($nMsg -ge 2 -and $hasUser -and $hasAsst -and $hasSrc) ("msgs={0} user={1} asst={2} src={3}" -f $nMsg, $hasUser, $hasAsst, $hasSrc) $sw.ElapsedMilliseconds

# ---- 後片付け: このランナーが作成したチャット/ナレッジを削除 ----
$removed = 0
foreach ($u in ($script:createdChats | Select-Object -Unique)) {
  if ($u) { $null = Invoke-Api 'DELETE' ("/api/chats/" + $u); $removed++ }
}
$removedF = 0
foreach ($fid in ($script:createdFileIds | Select-Object -Unique)) {
  if ($fid -gt 0) { $null = Invoke-Api 'DELETE' ("/api/knowledge/" + $fid); $removedF++ }
}

$pass = 0
foreach ($line in $script:results) { if ($line.Contains("`tPASS`t")) { $pass++ } }
Write-Output ("SCORE`t{0}/{1}" -f $pass, $script:results.Count)
Write-Output ("CLEANUP`tremoved {0} chats / {1} files" -f $removed, $removedF)
if ($pass -lt $script:results.Count) { exit 1 } else { exit 0 }
