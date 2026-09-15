# スモーク: llama-server /v1/embeddings (bge-m3 GGUF) vs Ollama bge-m3
# 目的: (1) エンドポイント動作 (2) pooling/正規化フラグの確認 (3) Ollama版とのコサイン一致
$ErrorActionPreference = 'Stop'
$Engine = 'D:\dev\shineos-local-ai\spikes\phase0\engine\cpu'
$Model  = 'D:\dev\shineos-local-ai\spikes\phase0\models\bge-m3-Q8_0.gguf'
$Port   = 8102
$log    = "$env:TEMP\llama-embed.log"

function Get-Cosine([double[]]$a, [double[]]$b) {
  $dot = 0.0; $na = 0.0; $nb = 0.0
  for ($i = 0; $i -lt $a.Length; $i++) { $dot += $a[$i] * $b[$i]; $na += $a[$i] * $a[$i]; $nb += $b[$i] * $b[$i] }
  return $dot / [math]::Sqrt($na * $nb)
}

Write-Output '[1] starting llama-server (embedding mode)...'
$proc = Start-Process -FilePath "$Engine\llama-server.exe" -WindowStyle Hidden -PassThru `
  -ArgumentList @('-m', $Model, '--host', '127.0.0.1', '--port', "$Port", '--embedding', '--pooling', 'cls', '-c', '2048', '-t', '8') `
  -RedirectStandardError $log
try {
  $ready = $false
  foreach ($i in 1..60) {
    Start-Sleep -Milliseconds 500
    try { Invoke-RestMethod "http://127.0.0.1:$Port/health" -TimeoutSec 2 | Out-Null; $ready = $true; break } catch {}
  }
  if (-not $ready) { throw 'llama-server did not become healthy in 30s' }
  Write-Output '[1] healthy.'

  $texts = @(
    '経費精算の手順について教えてください',
    '旅費規程における宿泊費の上限額はいくらですか',
    'PCのパスワード変更方法を教えてください',
    'the quick brown fox jumps over the lazy dog'
  )

  $t0 = Get-Date
  $llamaEmb = @()
  foreach ($t in $texts) {
    $j = @{ input = $t } | ConvertTo-Json
    $r = Invoke-RestMethod -Method Post "http://127.0.0.1:$Port/v1/embeddings" -ContentType 'application/json' -Body ([System.Text.Encoding]::UTF8.GetBytes($j))
    $llamaEmb += , [double[]]$r.data[0].embedding
  }
  $embMs = ((Get-Date) - $t0).TotalMilliseconds / $texts.Count
  Write-Output ('[2] llama embeddings: dim={0}, avg latency={1:N0} ms' -f $llamaEmb[0].Length, $embMs)

  $ollamaEmb = @()
  foreach ($t in $texts) {
    $j = @{ model = 'bge-m3'; input = $t } | ConvertTo-Json
    $r = Invoke-RestMethod -Method Post 'http://127.0.0.1:11434/api/embed' -ContentType 'application/json' -Body ([System.Text.Encoding]::UTF8.GetBytes($j))
    $ollamaEmb += , [double[]]$r.embeddings[0]
  }
  Write-Output ('[3] ollama embeddings: dim={0}' -f $ollamaEmb[0].Length)

  Write-Output '[4] cross-system cosine (same text, llama GGUF-Q8 vs ollama):'
  for ($i = 0; $i -lt $texts.Count; $i++) {
    '    text{0}: {1:N6}' -f ($i + 1), (Get-Cosine $llamaEmb[$i] $ollamaEmb[$i])
  }
  Write-Output '[5] intra-system semantic sanity (expect jp-expense pair > jp vs en):'
  '    llama  cos(t1,t2)={0:N4}  cos(t1,t4)={1:N4}' -f (Get-Cosine $llamaEmb[0] $llamaEmb[1]), (Get-Cosine $llamaEmb[0] $llamaEmb[3])
  '    ollama cos(t1,t2)={0:N4}  cos(t1,t4)={1:N4}' -f (Get-Cosine $ollamaEmb[0] $ollamaEmb[1]), (Get-Cosine $ollamaEmb[0] $ollamaEmb[3])
} finally {
  if ($proc -and -not $proc.HasExited) { Stop-Process -Id $proc.Id -Force }
  Write-Output '[6] llama-server stopped.'
}
Write-Output '--- server log tail ---'
Get-Content $log -Tail 15
