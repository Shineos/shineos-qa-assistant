# スモーク: llama-server /v1/rerank (bge-reranker-v2-m3 GGUF Q8)
# 目的: リランクエンドポイント動作と日本語ランキング品質
$ErrorActionPreference = 'Stop'
$Engine = 'D:\dev\shineos-local-ai\spikes\phase0\engine\cpu'
$Model  = 'D:\dev\shineos-local-ai\spikes\phase0\models\bge-reranker-v2-m3-Q8_0.gguf'
$Port   = 8103
$log    = "$env:TEMP\llama-rerank.log"

Write-Output '[1] starting llama-server (rerank mode)...'
$proc = Start-Process -FilePath "$Engine\llama-server.exe" -WindowStyle Hidden -PassThru `
  -ArgumentList @('-m', $Model, '--host', '127.0.0.1', '--port', "$Port", '--rerank', '--pooling', 'rank', '-c', '4096', '-t', '8') `
  -RedirectStandardError $log
try {
  $ready = $false
  foreach ($i in 1..60) {
    Start-Sleep -Milliseconds 500
    try { Invoke-RestMethod "http://127.0.0.1:$Port/health" -TimeoutSec 2 | Out-Null; $ready = $true; break } catch {}
  }
  if (-not $ready) { throw 'llama-server did not become healthy in 30s' }
  Write-Output '[1] healthy.'

  $query = '宿泊費の上限額はいくらですか'
  $docs = @(
    '1泊あたりの宿泊費は15,000円（税込）を上限とする。',                          # 正解
    '交通費は実費精算とし、航空券はエコノミークラスに限る。',                      # 旅費だが違う主題
    '経費精算は月末締めで、翌月10日までに所属長の承認を得て提出すること。',        # 経費だが違う主題
    'パスワードは90日ごとに変更しなければならない。',                              # 無関係
    '社内文書の保管期間は5年間とする。'                                            # 無関係
  )
  $body = @{ model = 'reranker'; query = $query; documents = $docs; top_n = $docs.Count } | ConvertTo-Json
  $t0 = Get-Date
  $r = Invoke-RestMethod -Method Post "http://127.0.0.1:$Port/v1/rerank" -ContentType 'application/json' -Body $body
  $ms = ((Get-Date) - $t0).TotalMilliseconds
  Write-Output ('[2] rerank of {0} docs took {1:N0} ms' -f $docs.Count, $ms)
  Write-Output '[3] ranking (index: score → doc):'
  foreach ($res in ($r.results | Sort-Object score -Descending)) {
    '    #{0} score={1:N6}  {2}' -f $res.index, $res.score, $docs[$res.index]
  }
  $top = ($r.results | Sort-Object score -Descending | Select-Object -First 1).index
  if ($top -eq 0) { Write-Output '[PASS] 正解doc(0)がトップ' } else { Write-Output ('[FAIL] トップはdoc {0}' -f $top) }
} finally {
  if ($proc -and -not $proc.HasExited) { Stop-Process -Id $proc.Id -Force }
  Write-Output '[4] llama-server stopped.'
}
