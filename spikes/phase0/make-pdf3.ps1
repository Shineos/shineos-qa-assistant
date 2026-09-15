# 非圧縮ストリームのPDF（フィルタ無し）— 生成ツールの圧縮バグを回避した確実なテストPDF
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
$utf8 = [System.Text.Encoding]::UTF8
$pdfPath = 'D:\dev\shineos-local-ai\spikes\phase0\testdocs\telework-policy.pdf'
$content = 'BT /F1 12 Tf 50 720 Td (TELEWORK ALLOWANCE POLICY.) Tj 0 -20 Td (The telework allowance is 5,000 yen per month.) Tj 0 -20 Td (Employees working remotely 3 or more days per week are eligible.) Tj 0 -20 Td (Applications must be submitted via the attendance system.) Tj ET'
$head = "%PDF-1.4`n1 0 obj<</Type/Catalog/Pages 2 0 R>>endobj`n2 0 obj<</Type/Pages/Kids[3 0 R]/Count 1>>endobj`n3 0 obj<</Type/Page/Parent 2 0 R/MediaBox[0 0 612 792]/Contents 4 0 R/Resources<</Font<</F1 5 0 R>>>>>>endobj`n4 0 obj<</Length $($content.Length)>>stream`n"
$tail = "`nendstream endobj`n5 0 obj<</Type/Font/Subtype/Type1/BaseFont/Helvetica>>endobj`ntrailer<</Root 1 0 R>>`n%%EOF"
[IO.File]::WriteAllText($pdfPath, $head + $content + $tail, $utf8)
Write-Output "pdf created (uncompressed stream)"
$r = curl.exe -s -X POST http://127.0.0.1:8300/api/knowledge -F "files=@D:/dev/shineos-local-ai/spikes/phase0/testdocs/telework-policy.pdf;filename=telework-policy.pdf;type=application/pdf"
Write-Output "ingest: $r"
