# UTF-8マルチパート（ブラウザと同一形式）での再アップロード検証
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
Add-Type -AssemblyName System.Net.Http
$path = 'D:\dev\shineos-local-ai\spikes\phase0\testdocs\テスト社内規程.docx'
$client = [System.Net.Http.HttpClient]::new()
$form = [System.Net.Http.MultipartFormDataContent]::new()
$bytes = [IO.File]::ReadAllBytes($path)
$fc = [System.Net.Http.ByteArrayContent]::new($bytes)
$fc.Headers.ContentType = [System.Net.Http.Headers.MediaTypeHeaderValue]::Parse('application/vnd.openxmlformats-officedocument.wordprocessingml.document')
$form.Add($fc, 'files', [Uri]::EscapeDataString('テスト社内規程.docx'))
$resp = $client.PostAsync('http://127.0.0.1:8300/api/knowledge', $form).Result
$text = $resp.Content.ReadAsStringAsync().Result
Write-Output "HTTP $([int]$resp.StatusCode): $text"
