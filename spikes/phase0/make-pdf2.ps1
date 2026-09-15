# PDF独自内容のテスト文書（年末年始休暇）を生成して登録
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
$utf8 = [System.Text.Encoding]::UTF8
$pdfPath = 'D:\dev\shineos-local-ai\spikes\phase0\testdocs\year-end-policy.pdf'
$text = 'BT /F1 12 Tf 50 720 Td (YEAR-END HOLIDAY POLICY 2026.) Tj 0 -20 Td (The office is closed from December 29 to January 3.) Tj 0 -20 Td (No application is required for this company-wide closure.) Tj 0 -20 Td (Employees taking extra leave must apply by December 15.) Tj ET'
$ms = New-Object System.IO.MemoryStream
$ds = New-Object System.IO.Compression.DeflateStream($ms, [System.IO.Compression.CompressionMode]::Compress)
$b = $utf8.GetBytes($text); $ds.Write($b, 0, $b.Length); $ds.Close()
$zlib = [byte[]](0x78, 0x9C) + $ms.ToArray()
$pdf = "%PDF-1.4`n1 0 obj<</Type/Catalog/Pages 2 0 R>>endobj`n2 0 obj<</Type/Pages/Kids[3 0 R]/Count 1>>endobj`n3 0 obj<</Type/Page/Parent 2 0 R/MediaBox[0 0 612 792]/Contents 4 0 R/Resources<</Font<</F1 5 0 R>>>>>>endobj`n4 0 obj<</Length $($zlib.Length)/Filter/FlateDecode>>stream`n!!STREAM!!`nendstream endobj`n5 0 obj<</Type/Font/Subtype/Type1/BaseFont/Helvetica>>endobj`ntrailer<</Root 1 0 R>>`n%%EOF"
$pdfBytes = $utf8.GetBytes($pdf.Replace('!!STREAM!!', [System.Text.Encoding]::GetEncoding('ISO-8859-1').GetString($zlib)))
[IO.File]::WriteAllBytes($pdfPath, [byte[]]$pdfBytes)
Write-Output "pdf2 created"
$r = curl.exe -s -X POST http://127.0.0.1:8300/api/knowledge -F "files=@D:/dev/shineos-local-ai/spikes/phase0/testdocs/year-end-policy.pdf;filename=year-end-policy.pdf;type=application/pdf"
Write-Output "ingest: $r"
