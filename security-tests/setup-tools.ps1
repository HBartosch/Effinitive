# Setup script for all FREE security testing tools
# Run this once to install all required tools

$ErrorActionPreference = "Stop"

Write-Host "========================================" -ForegroundColor Cyan
Write-Host "EffinitiveFramework Security Tools Setup" -ForegroundColor Cyan
Write-Host "========================================`n" -ForegroundColor Cyan

# Check prerequisites
Write-Host "Checking prerequisites..." -ForegroundColor Yellow

# .NET SDK
if (Get-Command dotnet -ErrorAction SilentlyContinue) {
    $dotnetVersion = dotnet --version
    Write-Host "✅ .NET SDK: $dotnetVersion" -ForegroundColor Green
} else {
    Write-Host "❌ .NET SDK not found - Install from https://dot.net" -ForegroundColor Red
    exit 1
}

# Check for Chocolatey
$hasChoco = Get-Command choco -ErrorAction SilentlyContinue
if ($hasChoco) {
    Write-Host "✅ Chocolatey found" -ForegroundColor Green
} else {
    Write-Host "⚠️  Chocolatey not found - Some tools may need manual installation" -ForegroundColor Yellow
}

Write-Host "`n========================================" -ForegroundColor Cyan
Write-Host "Installing Security Analysis Packages" -ForegroundColor Cyan
Write-Host "========================================`n" -ForegroundColor Cyan

# Install .NET security analyzers
Write-Host "Installing Microsoft.CodeAnalysis.NetAnalyzers..." -ForegroundColor Yellow
dotnet add ../src/EffinitiveFramework.Core/EffinitiveFramework.Core.csproj package Microsoft.CodeAnalysis.NetAnalyzers

Write-Host "Installing SecurityCodeScan.VS2019..." -ForegroundColor Yellow
dotnet add ../src/EffinitiveFramework.Core/EffinitiveFramework.Core.csproj package SecurityCodeScan.VS2019

Write-Host "`n========================================" -ForegroundColor Cyan
Write-Host "Installing Testing Tools" -ForegroundColor Cyan
Write-Host "========================================`n" -ForegroundColor Cyan

# SharpFuzz (fuzzing)
Write-Host "Installing SharpFuzz..." -ForegroundColor Yellow
dotnet tool install --global SharpFuzz.CommandLine 2>&1 | Out-Null
if ($LASTEXITCODE -eq 0) {
    Write-Host "✅ SharpFuzz installed" -ForegroundColor Green
} else {
    Write-Host "⚠️  SharpFuzz may already be installed or failed" -ForegroundColor Yellow
}

# nghttp2 (includes h2load)
Write-Host "`nInstalling nghttp2 (h2load)..." -ForegroundColor Yellow
if ($hasChoco) {
    choco install nghttp2 -y
    if ($LASTEXITCODE -eq 0) {
        Write-Host "✅ nghttp2/h2load installed" -ForegroundColor Green
    } else {
        Write-Host "⚠️  nghttp2 installation may have failed" -ForegroundColor Yellow
    }
} else {
    Write-Host "⚠️  Chocolatey not available - Download nghttp2 manually from:" -ForegroundColor Yellow
    Write-Host "   https://github.com/nghttp2/nghttp2/releases" -ForegroundColor Gray
}

# Python (for slowloris)
Write-Host "`nChecking Python installation..." -ForegroundColor Yellow
if (Get-Command python -ErrorAction SilentlyContinue) {
    try {
        $pythonVersion = python --version 2>&1
        Write-Host "✅ Python found: $pythonVersion" -ForegroundColor Green
        
        Write-Host "Installing slowloris module..." -ForegroundColor Yellow
        python -m pip install slowloris 2>&1 | Out-Null
        if ($LASTEXITCODE -eq 0) {
            Write-Host "✅ slowloris installed" -ForegroundColor Green
        } else {
            Write-Host "⚠️  slowloris installation may have failed" -ForegroundColor Yellow
        }
    } catch {
        Write-Host "⚠️  Python version check failed: $_" -ForegroundColor Yellow
    }
} else {
    Write-Host "⚠️  Python not found - Install from https://python.org" -ForegroundColor Yellow
    Write-Host "   (Optional - needed for Slowloris attack testing)" -ForegroundColor Gray
}

# h2spec
Write-Host "`nInstalling h2spec..." -ForegroundColor Yellow
$h2specUrl = "https://github.com/summerwind/h2spec/releases/download/v2.6.0/h2spec_windows_amd64.zip"
$h2specDir = "$PSScriptRoot\tools"
$h2specZip = "$h2specDir\h2spec.zip"

if (-not (Test-Path $h2specDir)) {
    New-Item -ItemType Directory -Path $h2specDir | Out-Null
}

try {
    Write-Host "Downloading h2spec from GitHub..." -ForegroundColor Yellow
    Invoke-WebRequest -Uri $h2specUrl -OutFile $h2specZip -ErrorAction Stop
    Expand-Archive -Path $h2specZip -DestinationPath $h2specDir -Force
    Remove-Item $h2specZip
    
    # Add to PATH for current session
    $env:PATH = "$h2specDir;$env:PATH"
    
    Write-Host "✅ h2spec installed to $h2specDir" -ForegroundColor Green
    Write-Host "   Add $h2specDir to your PATH permanently to use h2spec globally" -ForegroundColor Gray
} catch {
    Write-Host "⚠️  h2spec download failed - Install manually from:" -ForegroundColor Yellow
    Write-Host "   https://github.com/summerwind/h2spec/releases" -ForegroundColor Gray
}

# Summary
Write-Host "`n========================================" -ForegroundColor Cyan
Write-Host "Setup Summary" -ForegroundColor Cyan
Write-Host "========================================`n" -ForegroundColor Cyan

$tools = @(
    @{ Name = ".NET Analyzers"; Command = "dotnet build /p:RunAnalyzers=true"; Check = $true }
    @{ Name = "SharpFuzz"; Command = "sharpfuzz"; Check = (Get-Command sharpfuzz -ErrorAction SilentlyContinue) -ne $null }
    @{ Name = "h2load"; Command = "h2load"; Check = (Get-Command h2load -ErrorAction SilentlyContinue) -ne $null }
    @{ Name = "Python/slowloris"; Command = "python -m slowloris"; Check = (Get-Command python -ErrorAction SilentlyContinue) -ne $null }
    @{ Name = "h2spec"; Command = "h2spec"; Check = (Test-Path "$h2specDir\h2spec.exe") }
)

foreach ($tool in $tools) {
    $status = if ($tool.Check) { "✅ Ready" } else { "❌ Not Available" }
    $color = if ($tool.Check) { "Green" } else { "Red" }
    Write-Host ("{0,-25} {1}" -f $tool.Name, $status) -ForegroundColor $color
}

Write-Host "`n========================================" -ForegroundColor Cyan
Write-Host "Next Steps" -ForegroundColor Cyan
Write-Host "========================================`n" -ForegroundColor Cyan

Write-Host "1. Run all security tests:" -ForegroundColor White
Write-Host "   .\security-tests\run-all-tests.ps1`n" -ForegroundColor Gray

Write-Host "2. Run specific tests:" -ForegroundColor White
Write-Host "   .\security-tests\test-load.ps1" -ForegroundColor Gray
Write-Host "   .\security-tests\test-sse-load.ps1" -ForegroundColor Gray
Write-Host "   .\security-tests\test-slowloris.ps1`n" -ForegroundColor Gray

Write-Host "3. View documentation:" -ForegroundColor White
Write-Host "   Get-Content SECURITY_ASSESSMENT.md`n" -ForegroundColor Gray

Write-Host "✅ Setup complete!" -ForegroundColor Green
