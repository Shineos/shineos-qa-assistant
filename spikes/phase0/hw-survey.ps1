$ErrorActionPreference = 'SilentlyContinue'
Write-Output '=== CPU ==='
Get-CimInstance Win32_Processor | ForEach-Object {
  '{0} | cores={1} threads={2} maxClock={3}MHz' -f $_.Name, $_.NumberOfCores, $_.NumberOfLogicalProcessors, $_.MaxClockSpeed
}
Write-Output '=== RAM ==='
$cs = Get-CimInstance Win32_ComputerSystem
'{0:N1} GB' -f ($cs.TotalPhysicalMemory / 1GB)
$os = Get-CimInstance Win32_OperatingSystem
'free={0:N1} GB' -f ($os.FreePhysicalMemory / 1MB)
Write-Output '=== GPU ==='
Get-CimInstance Win32_VideoController | ForEach-Object {
  '{0} | driver={1} | VRAM={2:N1} GB' -f $_.Name, $_.DriverVersion, ($_.AdapterRAM / 1GB)
}
Write-Output '=== llama.cpp / ollama presence ==='
(Get-Command llama-server -ErrorAction SilentlyContinue) | ForEach-Object { 'llama-server: ' + $_.Source }
(Get-Command ollama -ErrorAction SilentlyContinue) | ForEach-Object { 'ollama: ' + $_.Source }
if (Test-Path "$env:LOCALAPPDATA\Programs\Ollama\ollama.exe") { 'ollama(per-user): ' + "$env:LOCALAPPDATA\Programs\Ollama\ollama.exe" }
Write-Output '=== AVX2 support ==='
$cpu = (Get-CimInstance Win32_Processor).Name
'CPU: ' + $cpu
