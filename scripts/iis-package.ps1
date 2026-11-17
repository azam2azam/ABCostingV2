<#
.SYNOPSIS
  Builds the VERITA UI and API into two zips ready to copy to an IIS server.

.DESCRIPTION
  Produces, in out\iis (or -OutDir):
    verita-web-<stamp>.zip  Angular build for the IIS application at -WebPath, with web.config
                            (SPA fallback + caching) and index.html pointed at -ApiUrl.
    verita-api-<stamp>.zip  dotnet publish output for the IIS application hosting the API
                            (web.config from the SDK, no appsettings.Development.json).

  The zips contain no secrets. On the server, set ConnectionStrings__Verita and Jwt__Key
  (and Cors__Origins__0 only if the UI is on a different host) for the API app pool.

  Prerequisites on the build machine: .NET 8 SDK, Node 24 (npm), git (optional, for the stamp).

.PARAMETER WebPath
  IIS application path of the UI. Default /ui (http://<server>:9100/ui). Use / for a site root.

.PARAMETER ApiUrl
  Where the UI calls the API: a path on the same host (default /api, the IIS application
  http://<server>:9100/api) or a full URL (https://verita-api.example.com). The API's own
  routes sit under it, so with /api the UI calls /api/api/v1/... and health is /api/health.

.EXAMPLE
  .\scripts\iis-package.ps1
.EXAMPLE
  .\scripts\iis-package.ps1 -WebPath /verita -ApiUrl /verita-api
.EXAMPLE
  .\scripts\iis-package.ps1 -Only web -SkipNpmCi
#>
[CmdletBinding()]
param(
    [string]$WebPath = '/ui',
    [string]$ApiUrl = '/api',
    [string]$OutDir,
    [ValidateSet('both', 'web', 'api')]
    [string]$Only = 'both',
    [switch]$SkipNpmCi
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
Add-Type -AssemblyName System.IO.Compression.FileSystem

$Root = Split-Path -Parent $PSScriptRoot
if (-not $OutDir) { $OutDir = Join-Path $Root 'out\iis' }
$Utf8NoBom = New-Object System.Text.UTF8Encoding($false)

function Step([string]$Message) { Write-Host "`n==> $Message" -ForegroundColor Cyan }

function Invoke-Checked([string]$File, [string[]]$Arguments, [string]$WorkDir) {
    Push-Location $WorkDir
    try {
        & $File @Arguments
        if ($LASTEXITCODE -ne 0) { throw "$File $($Arguments -join ' ') failed with exit code $LASTEXITCODE" }
    } finally { Pop-Location }
}

function New-Zip([string]$SourceDir, [string]$ZipPath) {
    if (Test-Path $ZipPath) { Remove-Item $ZipPath -Force }
    [System.IO.Compression.ZipFile]::CreateFromDirectory($SourceDir, $ZipPath, [System.IO.Compression.CompressionLevel]::Optimal, $false)
    $mb = [math]::Round((Get-Item $ZipPath).Length / 1MB, 1)
    Write-Host "    $ZipPath ($mb MB)" -ForegroundColor Green
}

function Require-Tool([string]$Name, [string]$Hint) {
    if (-not (Get-Command $Name -ErrorAction SilentlyContinue)) { throw "$Name was not found on PATH. $Hint" }
}

# Normalise the paths: '/verita' -> base href '/verita/', rewrite target '/verita/index.html'.
$WebPath = '/' + $WebPath.Trim().Trim('/')
$BaseHref = if ($WebPath -eq '/') { '/' } else { "$WebPath/" }
$ApiUrl = $ApiUrl.Trim().TrimEnd('/')
if (-not $ApiUrl) { throw '-ApiUrl cannot be empty.' }

$stamp = Get-Date -Format 'yyyyMMdd-HHmm'
if (Get-Command git -ErrorAction SilentlyContinue) {
    $sha = (& git -C $Root rev-parse --short HEAD 2>$null)
    if ($LASTEXITCODE -eq 0 -and $sha) {
        $stamp = "$stamp-$sha"
        if (& git -C $Root status --porcelain -- src web 2>$null) { $stamp = "$stamp-dirty" }
    }
}

$stage = Join-Path $OutDir 'stage'
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
New-Item -ItemType Directory -Force -Path $stage | Out-Null
$zips = @()

# ---------------------------------------------------------------- API
if ($Only -in 'both', 'api') {
    Require-Tool 'dotnet' 'Install the .NET 8 SDK.'
    Step 'Publishing API (Release)'
    $apiOut = Join-Path $stage 'api'
    Invoke-Checked 'dotnet' @('publish', 'src/Verita.Api/Verita.Api.csproj', '-c', 'Release', '-o', $apiOut, '--nologo') $Root

    # Development settings never go to a server.
    Get-ChildItem $apiOut -Filter 'appsettings.Development.json' | Remove-Item -Force
    if (-not (Test-Path (Join-Path $apiOut 'web.config'))) { throw 'dotnet publish did not produce web.config.' }
    # Without this, IIS WebDAV answers PUT/DELETE with 405 (see src/Verita.Api/web.config).
    if ((Get-Content (Join-Path $apiOut 'web.config') -Raw) -notmatch 'WebDAVModule') { throw 'API web.config does not remove WebDAV; PUT and DELETE would fail with 405 on IIS.' }
    if (-not (Test-Path (Join-Path $apiOut 'Verita.Api.dll'))) { throw 'dotnet publish did not produce Verita.Api.dll.' }

    Step 'Zipping API'
    $apiZip = Join-Path $OutDir "verita-api-$stamp.zip"
    New-Zip $apiOut $apiZip
    $zips += $apiZip
}

# ---------------------------------------------------------------- UI
if ($Only -in 'both', 'web') {
    Require-Tool 'npm' 'Install Node 24.'
    $webDir = Join-Path $Root 'web'
    if (-not $SkipNpmCi -or -not (Test-Path (Join-Path $webDir 'node_modules'))) {
        Step 'Installing UI packages (npm ci)'
        Invoke-Checked 'npm' @('ci', '--no-audit', '--no-fund') $webDir
    }

    Step "Building UI for $BaseHref"
    Invoke-Checked 'npx' @('ng', 'build', '--configuration', 'production', "--base-href=$BaseHref") $webDir

    $built = Join-Path $webDir 'dist\web\browser'
    if (-not (Test-Path (Join-Path $built 'index.html'))) { throw "UI build output not found at $built" }
    $webOut = Join-Path $stage 'web'
    Copy-Item $built $webOut -Recurse
    # Azure Static Web Apps config is meaningless on IIS.
    Get-ChildItem $webOut -Filter 'staticwebapp.config.json' | Remove-Item -Force

    # Point the UI at the API (read by web/src/app/core/api-url.ts).
    $indexPath = Join-Path $webOut 'index.html'
    $index = [System.IO.File]::ReadAllText($indexPath)
    # The production build minifies index.html (an empty content="" becomes a bare `content`).
    $pattern = '<meta name="verita-api"[^>]*>'
    if ($index -notmatch $pattern) { throw 'index.html has no <meta name="verita-api"> tag; is web/src/index.html up to date?' }
    $apiAttr = [System.Security.SecurityElement]::Escape($ApiUrl)
    $index = [regex]::Replace($index, $pattern, "<meta name=`"verita-api`" content=`"$apiAttr`">")
    [System.IO.File]::WriteAllText($indexPath, $index, $Utf8NoBom)

    # IIS: SPA fallback (needs the URL Rewrite module), long cache for hashed bundles, no cache for index.html.
    $indexUrl = "${BaseHref}index.html"
    $webConfig = @"
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <system.webServer>
    <rewrite>
      <rules>
        <clear />
        <rule name="VERITA SPA fallback" stopProcessing="true">
          <match url=".*" />
          <conditions logicalGrouping="MatchAll">
            <add input="{REQUEST_FILENAME}" matchType="IsFile" negate="true" />
            <add input="{REQUEST_FILENAME}" matchType="IsDirectory" negate="true" />
          </conditions>
          <action type="Rewrite" url="$indexUrl" />
        </rule>
      </rules>
    </rewrite>
    <staticContent>
      <clientCache cacheControlMode="UseMaxAge" cacheControlMaxAge="365.00:00:00" />
    </staticContent>
  </system.webServer>
  <location path="index.html">
    <system.webServer>
      <staticContent>
        <clientCache cacheControlMode="DisableCache" />
      </staticContent>
    </system.webServer>
  </location>
</configuration>
"@
    [System.IO.File]::WriteAllText((Join-Path $webOut 'web.config'), $webConfig, $Utf8NoBom)

    Step 'Zipping UI'
    $webZip = Join-Path $OutDir "verita-web-$stamp.zip"
    New-Zip $webOut $webZip
    $zips += $webZip
}

Remove-Item $stage -Recurse -Force

Step 'Done'
$zips | ForEach-Object { Write-Host "    $_" }
Write-Host @"

On the IIS server:
  UI  -> application $WebPath : clear its folder, extract verita-web-*.zip into it.
  API -> application for $ApiUrl : drop app_offline.htm in its folder, extract verita-api-*.zip
         (overwrite), delete app_offline.htm. Its app pool needs ConnectionStrings__Verita and
         Jwt__Key set; check <api>/health returns {"status":"ok"}.
"@
