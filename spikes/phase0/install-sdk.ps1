$ErrorActionPreference = 'Stop'
$dir = 'D:\dev\shineos-local-ai\tools\dotnet-sdk'
New-Item -ItemType Directory -Force -Path $dir | Out-Null
$script = "$env:TEMP\dotnet-install.ps1"
Invoke-WebRequest -Uri 'https://dot.net/v1/dotnet-install.ps1' -OutFile $script -UseBasicParsing
& powershell -NoProfile -ExecutionPolicy Bypass -File $script -Channel 10.0 -InstallDir $dir
& "$dir\dotnet.exe" --version
Write-Output 'SDK-READY'
