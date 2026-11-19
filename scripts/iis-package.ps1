<#
.SYNOPSIS
  Builds the VERITA UI, the VERITA API and the HIS Sync worker into zips ready to copy to an IIS server.

.DESCRIPTION
  Produces, in out\iis (or -OutDir):
    verita-web-<stamp>.zip  Angular build for the IIS application at -WebPath, with web.config
                            (SPA fallback + caching) and index.html pointed at -ApiUrl.
    verita-api-<stamp>.zip  dotnet publish output for the IIS application hosting the API
                            (web.config from the SDK, no appsettings.Development.json).
    verita-his-sync-<stamp>.zip
                            dotnet publish output of tools/HisSync for its own IIS application
                            (-SyncPath) with its own app pool: Start mode AlwaysRunning, Idle
                            time-out 0, preload on. See tools/HisSync/README.md.

  The zips contain no secrets. On the server, set ConnectionStrings__Verita and Jwt__Key
  (and Cors__Origins__0 only if the UI is on a different host) for the API app pool. HIS Sync
  reads ConnectionStrings:Verita and His:ConnectionString from appsettings.Production.json
  kept in its folder (never in the zip) or from app pool environment variables.

  Prerequisites on the build machine: .NET 8 SDK, Node 24 (npm), git (optional, for the stamp).

.PARAMETER WebPath
  IIS application path of the UI. Default /ui (http://<server>:9100/ui). Use / for a site root.

.PARAMETER SyncPath
  IIS application path of the HIS Sync worker, for the instructions printed at the end. Default /his-sync.

.PARAMETER Only
  all (default): UI, API and HIS Sync. both: UI and API (as before). web, api or sync: one of them.

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
.EXAMPLE
  .\scripts\iis-package.ps1 -Only sync
#>
[CmdletBinding()]
param(
    [string]$WebPath = '/ui',
    [string]$ApiUrl = '/api',
    [string]$SyncPath = '/his-sync',
    [string]$OutDir,
    [ValidateSet('all', 'both', 'web', 'api', 'sync')]
    [string]$Only = 'all',
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
        if (& git -C $Root status --porcelain -- src web tools/HisSync tools/HisExtract 2>$null) { $stamp = "$stamp-dirty" }
    }
}

$stage = Join-Path $OutDir 'stage'
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
New-Item -ItemType Directory -Force -Path $stage | Out-Null
$zips = @()

# ---------------------------------------------------------------- API
$SyncPath = '/' + $SyncPath.Trim().Trim('/')

function Assert-WebConfig([string]$Dir, [string]$Dll) {
    $path = Join-Path $Dir 'web.config'
    if (-not (Test-Path $path)) { throw "dotnet publish did not produce web.config in $Dir." }
    # A malformed web.config stops IIS with 500.19 before the app starts.
    try { $xml = [xml](Get-Content $path -Raw) } catch { throw "web.config in $Dir is not valid XML: $($_.Exception.Message)" }
    $core = $xml.SelectSingleNode('//aspNetCore')
    if (-not $core) { throw "web.config in $Dir has no <aspNetCore> element." }
    if ($core.GetAttribute('arguments') -notmatch [regex]::Escape($Dll) -and $core.GetAttribute('processPath') -notmatch [regex]::Escape($Dll -replace '\.dll$', '')) {
        throw "web.config in $Dir does not start $Dll."
    }
    if (-not (Test-Path (Join-Path $Dir $Dll))) { throw "dotnet publish did not produce $Dll." }
    # Where WebDAV is installed it answers PUT and DELETE with 405 before ASP.NET Core sees them: both the module
    # and the handler must be removed (see src/Verita.Api/web.config).
    if (-not $xml.SelectSingleNode("//modules/remove[@name='WebDAVModule']")) { throw "web.config in $Dir does not remove the WebDAVModule module; PUT and DELETE would fail with 405 on IIS." }
    if (-not $xml.SelectSingleNode("//handlers/remove[@name='WebDAV']")) { throw "web.config in $Dir does not remove the WebDAV handler; PUT and DELETE would fail with 405 on IIS." }
    return $xml
}

if ($Only -in 'all', 'both', 'api') {
    Require-Tool 'dotnet' 'Install the .NET 8 SDK.'
    Step 'Publishing API (Release)'
    $apiOut = Join-Path $stage 'api'
    Invoke-Checked 'dotnet' @('publish', 'src/Verita.Api/Verita.Api.csproj', '-c', 'Release', '-o', $apiOut, '--nologo') $Root

    # Development settings never go to a server.
    Get-ChildItem $apiOut -Filter 'appsettings.Development.json' | Remove-Item -Force
    $null = Assert-WebConfig $apiOut 'Verita.Api.dll'

    Step 'Zipping API'
    $apiZip = Join-Path $OutDir "verita-api-$stamp.zip"
    New-Zip $apiOut $apiZip
    $zips += $apiZip
}

# ---------------------------------------------------------------- HIS Sync worker
if ($Only -in 'all', 'sync') {
    Require-Tool 'dotnet' 'Install the .NET 8 SDK.'
    Step 'Publishing HIS Sync worker (Release)'
    $syncOut = Join-Path $stage 'his-sync'
    Invoke-Checked 'dotnet' @('publish', 'tools/HisSync/HisSync.csproj', '-c', 'Release', '-o', $syncOut, '--nologo') $Root

    # Server settings (connection strings) live on the server, never in the package.
    Get-ChildItem $syncOut -Filter 'appsettings.*.json' | Remove-Item -Force
    $syncXml = Assert-WebConfig $syncOut 'Verita.HisSync.dll'
    # The workers only start when IIS loads the site: Application Initialization requests /health on pool start.
    if (-not $syncXml.SelectSingleNode('//applicationInitialization')) { throw 'HIS Sync web.config has no <applicationInitialization>; the workers would not start until a first request.' }
    if (-not (Test-Path (Join-Path $syncOut 'HisExtract.dll'))) { throw 'HIS Sync publish is missing HisExtract.dll.' }

    # The shipped appsettings.json must not carry a connection string.
    $settings = Get-Content (Join-Path $syncOut 'appsettings.json') -Raw | ConvertFrom-Json
    $secrets = @()
    if ($settings.ConnectionStrings.Verita) { $secrets += 'ConnectionStrings:Verita' }
    if ($settings.His.ConnectionString) { $secrets += 'His:ConnectionString' }
    foreach ($t in @($settings.His.Tenants.PSObject.Properties)) { if ($t.Value.ConnectionString) { $secrets += "His:Tenants:$($t.Name):ConnectionString" } }
    if ($secrets.Count -gt 0) { throw "tools/HisSync/appsettings.json has values for $($secrets -join ', '). Move them to appsettings.Production.json on the server." }

    Step 'Zipping HIS Sync worker'
    $syncZip = Join-Path $OutDir "verita-his-sync-$stamp.zip"
    New-Zip $syncOut $syncZip
    $zips += $syncZip
}

# ---------------------------------------------------------------- UI
if ($Only -in 'all', 'both', 'web') {
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

On the IIS server (run the release's schema script in deploy/iis/sql first: neither app changes the database):
  UI       -> application $WebPath : clear its folder, extract verita-web-*.zip into it.
  API      -> application for $ApiUrl : drop app_offline.htm in its folder, extract verita-api-*.zip
              (overwrite), delete app_offline.htm. Its app pool needs ConnectionStrings__Verita and
              Jwt__Key set; check <api>/health returns {"status":"ok"}.
  HIS Sync -> application $SyncPath with its own app pool (No Managed Code, Start mode AlwaysRunning,
              Idle time-out 0, periodic recycle off) and Preload enabled; needs the IIS Application
              Initialization feature. Drop app_offline.htm, extract verita-his-sync-*.zip (overwrite),
              delete app_offline.htm. Keep appsettings.Production.json (ConnectionStrings:Verita,
              His:ConnectionString) in that folder: the zip never contains it. Check
              $SyncPath/health returns {"status":"ok"} and System > HIS Sync shows the workers Idle.
              Run one HIS Sync per VERITA database. See tools/HisSync/README.md.
"@
