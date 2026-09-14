# 長文テスト文書（対象事実を中央に埋め込む）: docx(日本語) + pdf(英語・非圧縮)
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
$utf8 = [System.Text.Encoding]::UTF8
$OutDir = 'D:\dev\shineos-local-ai\spikes\phase0\testdocs'
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

# ---- 1) 長文 docx（駐車場の事実を第7項目に埋める） ----
$docxPath = Join-Path $OutDir 'オフィス利用規程.docx'
$paras = @(
  '【総務規程】オフィス利用ガイドライン',
  '会議室の利用は、予約システムから行うこと。予約は利用日の30日前から可能である。',
  '大会議室は最大20名まで利用できる。プロジェクターの借用は総務部に申請する。',
  '備品（文房具・トナーカートリッジ）は備品管理システムから申請する。トナーは在庫切れの2週間前までに発注すること。',
  '名刺の注文は月1回までとする。必要枚数は所属長の承認後に印刷会社へ発注する。',
  '制服は入社時に支給する。サイズ交換は入社後1か月以内に総務部へ連絡すること。',
  '駐車場の利用申請は、使用日の7日前までに総務部へ提出すること。利用料金は1時間200円で、月極は12,000円である。',
  '自転車置き場は登録制である。登録は入館証の発行と合わせて行う。',
  '郵便物の私物発送は禁止されている。業務郵便は総務部経由で発送すること。',
  'ゴミの分別は各階の分別ステーションのルールに従う。段ボールは月曜と木曜に回収する。',
  'オフィスの空調は18時30分に自動停止する。残業時は各フロアのリモコンで対応すること。',
  '本規程は2026年4月1日から施行する。'
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
Write-Output "long docx created (12 paragraphs)"

# ---- 2) 長文 pdf（英語・非圧縮ストリーム、parking を中ほどに） ----
$pdfPath = Join-Path $OutDir 'office-guideline.pdf'
$lines = @(
  'OFFICE UTILIZATION GUIDELINES 2026.',
  'Meeting rooms must be reserved via the booking system. Reservations open 30 days before use.',
  'The large conference room holds up to 20 people. Borrow projectors from General Affairs.',
  'Supplies (stationery and toner) are requested via the asset system. Order toner 2 weeks before stockout.',
  'Business cards may be ordered once per month with supervisor approval.',
  'Uniforms are provided at hire. Size exchanges must be requested within 1 month.',
  'Parking: applications must be submitted to General Affairs at least 7 days in advance. The fee is 200 yen per hour or 12,000 yen monthly.',
  'The bicycle area requires registration together with your building pass.',
  'Personal mail may not be shipped from the office. Business mail goes through General Affairs.',
  'Garbage separation follows each floor station rules. Cardboard is collected Monday and Thursday.',
  'Office air conditioning stops automatically at 18:30.',
  'These guidelines take effect on April 1, 2026.'
)
$content = 'BT /F1 12 Tf 50 720 Td'
$y = 720
foreach ($l in $lines) {
  $content += " ($l) Tj 0 -20 Td"
}
$content = $content -replace ' 0 -20 Td$', ' ET'
$head = "%PDF-1.4`n1 0 obj<</Type/Catalog/Pages 2 0 R>>endobj`n2 0 obj<</Type/Pages/Kids[3 0 R]/Count 1>>endobj`n3 0 obj<</Type/Page/Parent 2 0 R/MediaBox[0 0 612 792]/Contents 4 0 R/Resources<</Font<</F1 5 0 R>>>>>>endobj`n4 0 obj<</Length $($content.Length)>>stream`n"
$tail = "`nendstream endobj`n5 0 obj<</Type/Font/Subtype/Type1/BaseFont/Helvetica>>endobj`ntrailer<</Root 1 0 R>>`n%%EOF"
[IO.File]::WriteAllText($pdfPath, $head + $content + $tail, $utf8)
Write-Output "long pdf created (12 lines, parking at line 7)"

# ---- 3) APIで取り込み ----
Add-Type -AssemblyName System.Net.Http
$client = [System.Net.Http.HttpClient]::new()
foreach ($f in @($docxPath, $pdfPath)) {
  $form = [System.Net.Http.MultipartFormDataContent]::new()
  $bytes = [IO.File]::ReadAllBytes($f)
  $fc = [System.Net.Http.ByteArrayContent]::new($bytes)
  $fc.Headers.ContentType = [System.Net.Http.Headers.MediaTypeHeaderValue]::Parse('application/octet-stream')
  $name = [IO.Path]::GetFileName($f)
  $form.Add($fc, 'files', [Uri]::EscapeDataString($name))
  $resp = $client.PostAsync('http://127.0.0.1:8300/api/knowledge', $form).Result
  Write-Output "ingest ${name}: $($resp.Content.ReadAsStringAsync().Result)"
}
