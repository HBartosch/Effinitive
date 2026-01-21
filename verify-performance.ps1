#!/usr/bin/env pwsh
# Quick performance verification

Write-Host "`n=== Performance Verification ===" -ForegroundColor Cyan

# Start server
Write-Host "Starting server..." -ForegroundColor Yellow
Start-Process powershell -ArgumentList "-WindowStyle","Minimized","-Command","cd '$PWD\samples\EffinitiveFramework.Sample'; dotnet run -c Release --no-build 2>&1 | Out-Null"
Start-Sleep 7

Write-Host "Running 10-second test..." -ForegroundColor Yellow

$count = 0
$errors = 0
$start = Get-Date

while ((Get-Date) -lt $start.AddSeconds(10)) {
    try {
        $null = Invoke-WebRequest -Uri "http://localhost:5000/api/health" -UseBasicParsing -TimeoutSec 1
        $count++
    } catch {
        $errors++
    }
}

$duration = ((Get-Date) - $start).TotalSeconds
$rps = [math]::Round($count / $duration)

Write-Host "`nResults:" -ForegroundColor Cyan
Write-Host "  Total Requests: $count" -ForegroundColor White
Write-Host "  Errors: $errors" -ForegroundColor White
Write-Host "  Duration: $([math]::Round($duration, 2))s" -ForegroundColor White
Write-Host "  Requests/Second: $rps" -ForegroundColor Green

if ($rps -gt 1000) {
    Write-Host "`n✅ Performance is excellent!" -ForegroundColor Green
} elseif ($rps -gt 500) {
    Write-Host "`n✅ Performance is good!" -ForegroundColor Green
} else {
    Write-Host "`n⚠️  Performance is lower than expected" -ForegroundColor Yellow
}

taskkill /F /IM dotnet.exe 2>$null | Out-Null
Write-Host ""
