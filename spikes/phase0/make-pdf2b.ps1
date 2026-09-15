# PDF独自内容テスト（バイナリ正しく組み立てる版）
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
$utf8 = [System.Text.Encoding]::UTF8
$pdfPath = 'D:\dev\shineos-local-ai\spikes\phase0\testdocs\year-end-policy.pdf'
$text = 'BT /F1 12 Tf 50 720 Td (YEAR-END HOLIDAY POLICY 2026.) Tj 0 -20 Td (The office is closed from December 29 to January 3.) Tj 0 -20 Td (No application is required for this company-wide closure.) Tj 0 -20 Td (Employees taking extra leave must apply by December 15.) Tj ET'
# zlib (ヘッダ+raw deflate) を生成
$ms = New-Object System.IO.MemoryStream
$ds = New-Object System.IO.Compression.DeflateStream($ms, [System.IO.Compression.CompressionMode]::Compress)
$b = $utf8.GetBytes($text); $ds.Write($b, 0, $b.Length); $ds.Close()
$zlib = New-Object byte[] (2 + $ms.Length)
$zlib[0] = 0x78; $zlib[1] = 0x9C
[Array]::Copy($ms.ToArray(), 0, $zlib, 2, $ms.Length)
# PDFをバイト列で組み立て（文字列経由にしない＝UTF-8二重エンコード防止）
$out = New-Object System.IO.MemoryStream
$head = [byte[]]$utf8.GetBytes("%PDF-1.4`n1 0 obj<</Type/Catalog/Pages 2 0 R>>endobj`n2 0 obj<</Type/Pages/Kids[3 0 R]/Count 1>>endobj`n3 0 obj<</Type/Page/Parent 2 0 R/MediaBox[0 0 612 792]/Contents 4 0 R/Resources<</Font<</F1 5 0 R>>>>>>endobj`n4 0 obj<</Length $($zlib.Length)/Filter/FlateDecode>>stream`n")
$tail = [byte[]]$utf8.GetBytes("`nendstream endobj`n5 0 obj<</Type/Font/Subtype/Type1/BaseFont/Helvetica>>endobj`ntrailer<</Root 1 0 R>>`n%%EOF")
$out.Write($head, 0, $head.Length); $out.Write($zlib, 0, $zlib.Length); $out.Write($tail, 0, $tail.Length)
[IO.File]::WriteAllBytes($pdfPath, $out.ToArray())
Write-Output "pdf2 created ($($out.Length) bytes)"
$r = curl.exe -s -X POST http://127.0.0.1:8300/api/knowledge -F "files=@D:/dev/shineos-local-ai/spikes/phase0/testdocs/year-end-policy.pdf;filename=year-end-policy.pdf;type=application/pdf"
Write-Output "ingest: $r"
