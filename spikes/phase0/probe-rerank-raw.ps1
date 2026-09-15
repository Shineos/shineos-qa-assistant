$ErrorActionPreference = 'Stop'
$Engine = 'D:\dev\shineos-local-ai\spikes\phase0\engine\cpu'
$Model  = 'D:\dev\shineos-local-ai\spikes\phase0\models\bge-reranker-v2-m3-Q8_0.gguf'
$Port   = 8103
$log    = "$env:TEMP\llama-rerank2.log"
$proc = Start-Process -FilePath "$Engine\llama-server.exe" -WindowStyle Hidden -PassThru `
  -ArgumentList @('-m', $Model, '--host', '127.0.0.1', '--port', "$Port", '--rerank', '--pooling', 'rank', '-c', '4096', '-t', '8') `
  -RedirectStandardError $log
try {
  $ready = $false
  foreach ($i in 1..60) { Start-Sleep -Milliseconds 500; try { Invoke-RestMethod "http://127.0.0.1:$Port/health" -TimeoutSec 2 | Out-Null; $ready = $true; break } catch {} }
  if (-not $ready) { throw 'not healthy' }
  $query = [char]0x5BBF + [char]0x6CCA + 'X'   # placeholder ascii-safe
  $docs = @('docA', 'docB')
  $body = @{ query = ' lodging fee limit '; documents = $docs; top_n = 2 } | ConvertTo-Json
  $r = Invoke-RestMethod -Method Post "http://127.0.0.1:$Port/v1/rerank" -ContentType 'application/json' -Body ([System.Text.Encoding]::UTF8.GetBytes($body))
  Write-Output '--- parsed properties of results[0] ---'
  $r.results[0] | Get-Member -MemberType NoteProperty | ForEach-Object { $_.Name }
  Write-Output '--- raw json ---'
  ($r | ConvertTo-Json -Depth 5)
} finally {
  if ($proc -and -not $proc.HasExited) { Stop-Process -Id $proc.Id -Force }
}
