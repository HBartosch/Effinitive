# EffinitiveFramework Stress Test
param(
    [int]$DurationSeconds = 30,
    [int]$WarmupSeconds = 5
)

Write-Host "Starting stress test..." -ForegroundColor Cyan

# Kill existing processes
taskkill /F /IM dotnet.exe 2>$null | Out-Null
Start-Sleep -Seconds 2

# Start server
Write-Host "Starting server..." -ForegroundColor Yellow
$server = Start-Process powershell -ArgumentList "-NoExit", "-Command", "cd '$PSScriptRoot'; dotnet run --configuration Release --project samples/EffinitiveFramework.Sample" -PassThru -WindowStyle Minimized
Start-Sleep -Seconds 5

# Warmup
Write-Host "Warming up..." -ForegroundColor Yellow
1..100 | ForEach-Object {
    try { Invoke-WebRequest -Uri "http://localhost:5000/" -TimeoutSec 2 -UseBasicParsing | Out-Null } catch {}
}
Start-Sleep -Seconds $WarmupSeconds

Write-Host ""
Write-Host "Running stress test..." -ForegroundColor Green
Write-Host ""

function Test-Load {
    param([int]$Threads)
    
    Write-Host "Testing $Threads threads..." -ForegroundColor Cyan
    
    $jobs = 1..$Threads | ForEach-Object {
        Start-Job -ScriptBlock {
            param($EndTime)
            $count = 0
            $errors = 0
            
            while ((Get-Date) -lt $EndTime) {
                try {
                    $r = Invoke-WebRequest -Uri "http://localhost:5000/" -TimeoutSec 5 -UseBasicParsing
                    if ($r.StatusCode -eq 200) { $count++ }
                } catch {
                    $errors++
                }
            }
            
            @{ Success = $count; Errors = $errors }
        } -ArgumentList (Get-Date).AddSeconds($DurationSeconds)
    }
    
    $jobs | Wait-Job | Out-Null
    
    $total = 0
    $errs = 0
    
    $jobs | ForEach-Object {
        $result = Receive-Job -Job $_
        $total += $result.Success
        $errs += $result.Errors
        Remove-Job -Job $_
    }
    
    $rps = [math]::Round($total / $DurationSeconds, 0)
    
    Write-Host "  Requests: $total" -ForegroundColor Green
    Write-Host "  Errors: $errs" -ForegroundColor $(if ($errs -gt 0) { "Red" } else { "Gray" })
    Write-Host "  Req/Sec: $rps" -ForegroundColor Yellow
    Write-Host ""
    
    @{
        Threads = $Threads
        Total = $total
        Errors = $errs
        ReqPerSec = $rps
    }
}

$results = @()
$results += Test-Load -Threads 64
$results += Test-Load -Threads 256
$results += Test-Load -Threads 512

Write-Host ""
Write-Host "SUMMARY" -ForegroundColor Cyan
Write-Host "-------" -ForegroundColor Cyan
$results | ForEach-Object {
    Write-Host "$($_.Threads) threads: $($_.ReqPerSec) req/s" -ForegroundColor White
}

$best = ($results | Measure-Object -Property ReqPerSec -Maximum).Maximum
$pct = [math]::Round(($best / 39923) * 100, 1)
Write-Host ""
Write-Host "Best: $best req/s" -ForegroundColor $(if ($best -gt 39923) { "Green" } else { "Yellow" })
Write-Host "GenHTTP: 39,923 req/s" -ForegroundColor Gray
Write-Host "Comparison: $pct%" -ForegroundColor $(if ($pct -gt 100) { "Green" } else { "Yellow" })

# Cleanup
taskkill /F /IM dotnet.exe 2>$null | Out-Null
Write-Host ""
Write-Host "Done!" -ForegroundColor Green
