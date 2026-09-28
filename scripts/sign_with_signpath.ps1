# sign_with_signpath.ps1 - SignPath REST API で直接コード署名する
#
# GitHubコネクタ（Trusted Build System）を経由せず、SignPath API に直接
# 署名リクエストを送る方式。署名ポリシーが Trusted Build System 検証を
# 要求しない場合に利用できる。
#
# 必要な環境変数（GitHub Actions の Secrets/Variables から注入）:
#   SIGNPATH_API_TOKEN / SIGNPATH_ORG_ID / SIGNPATH_PROJECT_SLUG / SIGNPATH_SIGNING_POLICY_SLUG
#
# 終了コード: 0 = 成功 / 非0 = 失敗
param(
    [string]$ArtifactPath,   # 署名対象（未署名exe）
    [string]$OutputPath,     # 署名済みexeの保存先
    [int]$TimeoutSeconds = 600  # 承認待ち込みの完了待ち上限（手動承認ポリシーでは長めに）
)

$ErrorActionPreference = 'Stop'

$token = $env:SIGNPATH_API_TOKEN
$org = $env:SIGNPATH_ORG_ID
$project = $env:SIGNPATH_PROJECT_SLUG
$policy = $env:SIGNPATH_SIGNING_POLICY_SLUG
if (-not $token -or -not $org -or -not $project -or -not $policy) {
    throw 'SIGNPATH_API_TOKEN / SIGNPATH_ORG_ID / SIGNPATH_PROJECT_SLUG / SIGNPATH_SIGNING_POLICY_SLUG が設定されていません'
}
if (-not (Test-Path $ArtifactPath)) { throw "artifact not found: $ArtifactPath" }

$base = "https://app.signpath.io/api/v1/$org"

# 1) 署名リクエスト送信
Write-Output "submitting signing request ($project / $policy)..."
$submit = & curl.exe -s --max-time 300 -X POST -H "Authorization: Bearer $token" `
    -F "ProjectSlug=$project" -F "SigningPolicySlug=$policy" -F "Artifact=@$ArtifactPath" `
    "$base/SigningRequests/SubmitWithArtifact"
if ($LASTEXITCODE -ne 0) { throw "submit failed (curl exit $LASTEXITCODE): $submit" }
$req = $submit | ConvertFrom-Json
if (-not $req.signingRequestId) { throw "submit failed: $submit" }
Write-Output "signingRequestId: $($req.signingRequestId) (status: $($req.status))"

# 2) 完了までポーリング（最大600秒）
$deadline = (Get-Date).AddSeconds(600)
$status = $null
while ((Get-Date) -lt $deadline) {
    Start-Sleep -Seconds 5
    $status = (& curl.exe -s --max-time 20 -H "Authorization: Bearer $token" `
        "$base/SigningRequests/$($req.signingRequestId)") | ConvertFrom-Json
    Write-Output "status: $($status.status) / $($status.workflowStatus)"
    if ($status.status -eq 'Completed') { break }
    if ($status.isFinalStatus -and $status.status -ne 'Completed') {
        throw "signing failed: $($status.status) / $($status.workflowStatus)"
    }
}
if (-not $status -or $status.status -ne 'Completed') { throw 'signing timeout' }

# 3) 署名済みアーティファクト取得
Write-Output "downloading signed artifact..."
& curl.exe -s --max-time 300 -H "Authorization: Bearer $token" `
    "$base/SigningRequests/$($req.signingRequestId)/SignedArtifact" -o $OutputPath
if ($LASTEXITCODE -ne 0) { throw "download failed (curl exit $LASTEXITCODE)" }
if (-not (Test-Path $OutputPath)) { throw 'signed artifact not downloaded' }
Write-Output "signed: $OutputPath ($((Get-Item $OutputPath).Length) bytes)"
exit 0
