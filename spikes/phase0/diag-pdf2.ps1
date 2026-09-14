Add-Type -AssemblyName System.IO.Compression
$bytes = [IO.File]::ReadAllBytes('D:\dev\shineos-local-ai\spikes\phase0\testdocs\year-end-policy.pdf')
$s = [System.Text.Encoding]::ASCII.GetString($bytes)
$i = $s.IndexOf('stream')
$start = $i + 6
if ($bytes[$start] -eq 13) { $start++ }
if ($bytes[$start] -eq 10) { $start++ }
$end = $s.IndexOf('endstream')
Write-Output "stream at $i start=$start end=$end len=$($end - $start)"
foreach ($skip in @(2, 0)) {
  try {
    $seg = New-Object byte[] ($end - $start - $skip)
    [Array]::Copy($bytes, $start + $skip, $seg, 0, $seg.Length)
    $ms = [IO.MemoryStream]::new($seg)
    $ds = [IO.Compression.DeflateStream]::new($ms, [IO.Compression.CompressionMode]::Decompress)
    $out = [IO.MemoryStream]::new(); $ds.CopyTo($out); $ds.Close()
    $txt = [System.Text.Encoding]::ASCII.GetString($out.ToArray())
    Write-Output "skip=$skip OK len=$($out.Length): $($txt.Substring(0, [Math]::Min(60, $txt.Length)))"
  } catch { Write-Output "skip=$skip FAIL: $($_.Exception.Message)" }
}
