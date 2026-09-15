# テスト用 .docx / .pdf を生成してナレッジ登録APIで取り込み検証
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
$utf8 = [System.Text.Encoding]::UTF8
$OutDir = 'D:\dev\shineos-local-ai\spikes\phase0\testdocs'
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

# ---- 1) DOCX 生成 (zip + word/document.xml) ----
$docxPath = Join-Path $OutDir 'テスト社内規程.docx'
$paras = @(
  '【テスト規程】リモートワーク手当規程',
  'リモートワーク手当は月額5,000円を支給する。',
  '支給対象は週3日以上在宅勤務を行う正社員とする。',
  '申請は毎月末に勤怠システムから行うこと。',
  '支給開始は申請の翌月からとなる。'
)
$sb = New-Object System.Text.StringBuilder
[void]$sb.Append('<?xml version="1.0" encoding="UTF-8" standalone="yes"?><w:document xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main"><w:body>')
foreach ($p in $paras) { [void]$sb.Append("<w:p><w:r><w:t>$p</w:t></w:r></w:p>") }
[void]$sb.Append('</w:body></w:document>')
if (Test-Path $docxPath) { Remove-Item $docxPath }
$zip = [System.IO.Compression.ZipFile]::Open($docxPath, 'Create')
try {
  function Add-Entry([string]$path, [string]$content) {
    $e = $zip.CreateEntry($path)
    $s = $e.Open(); $b = $utf8.GetBytes($content); $s.Write($b, 0, $b.Length); $s.Close()
  }
  Add-Entry '[Content_Types].xml' '<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/><Default Extension="xml" ContentType="application/xml"/><Override PartName="/word/document.xml" ContentType="application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml"/></Types>'
  Add-Entry '_rels/.rels' '<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="word/document.xml"/></Relationships>'
  Add-Entry 'word/document.xml' $sb.ToString()
} finally { $zip.Dispose() }
Write-Output "docx created: $docxPath"

# ---- 2) PDF 生成 (FlateDecode + Tj、ASCII本文) ----
$pdfPath = Join-Path $OutDir 'test-policy.pdf'
$text = 'BT /F1 12 Tf 50 700 Td (TEST TELEWORK ALLOWANCE POLICY.) Tj 0 -20 Td (The telework allowance is 5,000 yen per month.) Tj 0 -20 Td (Employees working remotely 3 or more days per week are eligible.) Tj ET'
$ms = New-Object System.IO.MemoryStream
$ds = New-Object System.IO.Compression.DeflateStream($ms, [System.IO.Compression.CompressionMode]::Compress)
$b = $utf8.GetBytes($text); $ds.Write($b, 0, $b.Length); $ds.Close()
$zlib = [byte[]](0x78, 0x9C) + $ms.ToArray()  # zlibヘッダ付与
$streamStr = [Convert]::ToBase64String($zlib)
$pdf = "%PDF-1.4`n1 0 obj<</Type/Catalog/Pages 2 0 R>>endobj`n2 0 obj<</Type/Pages/Kids[3 0 R]/Count 1>>endobj`n3 0 obj<</Type/Page/Parent 2 0 R/MediaBox[0 0 612 792]/Contents 4 0 R/Resources<</Font<</F1 5 0 R>>>>>>endobj`n4 0 obj<</Length $($zlib.Length)/Filter/FlateDecode>>stream`n!!STREAM!!`nendstream endobj`n5 0 obj<</Type/Font/Subtype/Type1/BaseFont/Helvetica>>endobj`ntrailer<</Root 1 0 R>>`n%%EOF"
$pdfBytes = $utf8.GetBytes($pdf.Replace('!!STREAM!!', [System.Text.Encoding]::GetEncoding('ISO-8859-1').GetString($zlib)))
[IO.File]::WriteAllBytes($pdfPath, [byte[]]$pdfBytes)
Write-Output "pdf created: $pdfPath"

# ---- 3) APIで取り込み ----
foreach ($f in @($docxPath, $pdfPath)) {
  $r = curl.exe -s --max-time 120 -X POST http://127.0.0.1:8300/api/knowledge -F ("files=@`"$f`"")
  Write-Output "ingest $(Split-Path $f -Leaf): $r"
}
curl.exe -s http://127.0.0.1:8300/api/knowledge
