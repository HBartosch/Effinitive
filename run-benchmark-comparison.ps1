# Comprehensive Benchmark: Test actual benchmark endpoints
param([int]$DurationSeconds = 20)

Write-Host "`n================================================================" -ForegroundColor Cyan
Write-Host "    EffinitiveFramework vs GenHTTP - Benchmark Endpoints Test" -ForegroundColor Cyan
Write-Host "================================================================`n" -ForegroundColor Cyan

taskkill /F /IM dotnet.exe 2>$null | Out-Null
Start-Sleep -Seconds 2

function Test-Endpoint {
    param([string]$Name, [string]$Url, [int]$Threads)
    
    $output = dotnet run --project LoadTest --configuration Release -- $Threads $DurationSeconds $Url 2>&1 | Out-String
    
    if ($output -match "Throughput: (\d+) req") {
        $rps = [int]$matches[1]
        Write-Host "    $Name`: $rps req/s" -ForegroundColor Green
        return $rps
    }
    return 0
}

# Start EffinitiveFramework
Write-Host "Starting EffinitiveFramework..." -ForegroundColor Yellow
$effServer = Start-Process powershell -ArgumentList "-NoExit", "-Command", "cd '$PSScriptRoot'; dotnet run --configuration Release --project samples/EffinitiveFramework.Sample" -PassThru -WindowStyle Minimized
Start-Sleep -Seconds 8

Write-Host "Warming up..." -ForegroundColor Gray
1..100 | ForEach-Object {
    try { 
        Invoke-WebRequest -Uri "http://localhost:5000/" -TimeoutSec 2 -UseBasicParsing | Out-Null
        Invoke-WebRequest -Uri "http://localhost:5000/user/123" -TimeoutSec 2 -UseBasicParsing | Out-Null
    } catch {}
}
Start-Sleep -Seconds 3

Write-Host "`n================================================================" -ForegroundColor Cyan
Write-Host "EffinitiveFramework Results" -ForegroundColor White
Write-Host "================================================================" -ForegroundColor Cyan

$effResults = @{}

foreach ($threads in @(64, 256, 512)) {
    Write-Host "`n  Concurrency: $threads threads" -ForegroundColor Yellow
    
    $plaintext = Test-Endpoint -Name "GET / (plaintext)" -Url "http://localhost:5000/" -Threads $threads
    $userGet = Test-Endpoint -Name "GET /user/123 (route param)" -Url "http://localhost:5000/user/123" -Threads $threads
    $userPost = Test-Endpoint -Name "POST /user (empty)" -Url "http://localhost:5000/user" -Threads $threads
    
    $effResults[$threads] = @{
        Plaintext = $plaintext
        UserGet = $userGet
        UserPost = $userPost
        Average = [int](($plaintext + $userGet + $userPost) / 3)
    }
}

taskkill /F /IM dotnet.exe 2>$null | Out-Null
Start-Sleep -Seconds 3

# Start GenHTTP
Write-Host "`n================================================================" -ForegroundColor Cyan
Write-Host "Starting GenHTTP..." -ForegroundColor Yellow
Write-Host "================================================================" -ForegroundColor Cyan

$genServer = Start-Process powershell -ArgumentList "-NoExit", "-Command", "cd '$PSScriptRoot'; dotnet run --configuration Release --project GenHttpSample" -PassThru -WindowStyle Minimized
Start-Sleep -Seconds 8

Write-Host "Warming up..." -ForegroundColor Gray
1..100 | ForEach-Object {
    try { 
        Invoke-WebRequest -Uri "http://localhost:5001/api/benchmark" -TimeoutSec 2 -UseBasicParsing | Out-Null
    } catch {}
}
Start-Sleep -Seconds 3

Write-Host "`n================================================================" -ForegroundColor Cyan
Write-Host "GenHTTP Results" -ForegroundColor White
Write-Host "================================================================" -ForegroundColor Cyan

$genResults = @{}

foreach ($threads in @(64, 256, 512)) {
    Write-Host "`n  Concurrency: $threads threads" -ForegroundColor Yellow
    
    $benchmark = Test-Endpoint -Name "GET /api/benchmark" -Url "http://localhost:5001/api/benchmark" -Threads $threads
    
    $genResults[$threads] = @{
        Benchmark = $benchmark
    }
}

taskkill /F /IM dotnet.exe 2>$null | Out-Null

# Summary
Write-Host "`n================================================================" -ForegroundColor Cyan
Write-Host "                        FINAL RESULTS" -ForegroundColor Cyan
Write-Host "================================================================`n" -ForegroundColor Cyan

Write-Host "Concurrency | Effinitive Avg | GenHTTP      | Winner" -ForegroundColor White
Write-Host "------------|----------------|--------------|------------------" -ForegroundColor White

foreach ($threads in @(64, 256, 512)) {
    $effAvg = $effResults[$threads].Average
    $gen = $genResults[$threads].Benchmark
    
    $winner = if ($effAvg -gt $gen -and $gen -gt 0) {
        $pct = [math]::Round((($effAvg - $gen) / $gen) * 100, 1)
        "Effinitive +$pct%"
    } elseif ($gen -gt $effAvg) {
        $pct = [math]::Round((($gen - $effAvg) / $effAvg) * 100, 1)
        "GenHTTP +$pct%"
    } else {
        "Effinitive (GenHTTP N/A)"
    }
    
    $color = if ($effAvg -gt $gen -or $gen -eq 0) { "Green" } else { "Yellow" }
    
    $effStr = "{0,9:N0}" -f $effAvg
    $genStr = if ($gen -gt 0) { "{0,9:N0}" -f $gen } else { "      N/A" }
    
    Write-Host ("{0,11} | {1} req/s | {2} req/s | {3}" -f $threads, $effStr, $genStr, $winner) -ForegroundColor $color
}

Write-Host "`n================================================================" -ForegroundColor Cyan
Write-Host "Detailed Breakdown (EffinitiveFramework)" -ForegroundColor Cyan
Write-Host "================================================================`n" -ForegroundColor Cyan

foreach ($threads in @(64, 256, 512)) {
    Write-Host "$threads threads:" -ForegroundColor Yellow
    Write-Host "  Plaintext (GET /):         $($effResults[$threads].Plaintext) req/s" -ForegroundColor White
    Write-Host "  Route Param (GET /user/x): $($effResults[$threads].UserGet) req/s" -ForegroundColor White
    Write-Host "  POST Empty:                $($effResults[$threads].UserPost) req/s" -ForegroundColor White
    Write-Host "  Average:                   $($effResults[$threads].Average) req/s" -ForegroundColor Green
    Write-Host ""
}

Write-Host "web-frameworks-benchmark baseline: 39,923 req/s (GenHTTP)" -ForegroundColor Gray
$bestAvg = ($effResults.Values | ForEach-Object { $_.Average } | Measure-Object -Maximum).Maximum
$comparison = [math]::Round(($bestAvg / 39923) * 100, 1)
Write-Host "EffinitiveFramework best: $bestAvg req/s ($comparison% of baseline)" -ForegroundColor $(if ($comparison -gt 100) { "Green" } else { "Yellow" })
Write-Host ""
