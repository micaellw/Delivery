param(
    [Parameter(Mandatory=$true)][string]$AppName,
    [Parameter(Mandatory=$true)][string]$ApplicationId,
    [Parameter(Mandatory=$true)][string]$ApiBaseUrl
)
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Definition
$appDir = Resolve-Path (Join-Path $scriptDir "..")

Write-Host "APP_NAME: $AppName" -ForegroundColor Yellow
Write-Host "APPLICATION_ID: $ApplicationId" -ForegroundColor Yellow
Write-Host "API_BASE_URL: $ApiBaseUrl" -ForegroundColor Yellow

docker run --rm -e "WHITELABEL_APP_NAME=$AppName" -e "APP_NAME=$AppName" -e "APPLICATION_ID=$ApplicationId" -e "API_BASE_URL=$ApiBaseUrl" -v "${appDir}:/app" -w /app ghcr.io/cirruslabs/flutter:stable bash scripts/build-inside-docker.sh

if ($LASTEXITCODE -eq 0) {
    $apkPath = Join-Path $appDir "build\app\outputs\flutter-apk\app-release.apk"
    if (Test-Path $apkPath) {
        Copy-Item $apkPath -Destination (Join-Path $scriptDir "..\apk\delivery-app.apk") -Force
        Write-Host "Output File: ..\apk\delivery-app.apk" -ForegroundColor White
    }
} else { exit $LASTEXITCODE }

