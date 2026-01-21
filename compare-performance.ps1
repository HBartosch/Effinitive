# Comparative Performance Test: EffinitiveFramework vs GenHTTP
param([int]$DurationSeconds = 20)

Write-Host "`n===============================================================" -ForegroundColor Cyan
Write-Host "    EffinitiveFramework vs GenHTTP Performance Test" -ForegroundColor Cyan
Write-Host "===============================================================`n" -ForegroundColor Cyan

taskkill /F /IM dotnet.exe 2>$null | Out-Null
Start-Sleep -Seconds 2

function Test-Framework {
    param([string]$Name, [string]$Project, [int]$Port, [string]$Endpoint)
    
    Write-Host "`n===============================================================" -ForegroundColor Cyan
    Write-Host "Testing: $Name - $Endpoint" -ForegroundColor White
    Write-Host "===============================================================" -ForegroundColor Cyan
    
    Write-Host "Starting server on port $Port..." -ForegroundColor Yellow
    $server = Start-Process powershell -ArgumentList "-NoExit", "-Command", "cd '$PSScriptRoot'; dotnet run --configuration Release --project $Project" -PassThru -WindowStyle Minimized
    Start-Sleep -Seconds 6
    
    Write-Host "Warming up..." -ForegroundColor Yellow
    $testUrl = "http://localhost:$Port$Endpoint"
    1..50 | ForEach-Object {
        try { Invoke-WebRequest -Uri $testUrl -TimeoutSec 2 -UseBasicParsing | Out-Null } catch {}
    }
    Start-Sleep -Seconds 2
    
    Write-Host ""
    $results = @()
    
    foreach ($threads in @(64, 256, 512)) {
        Write-Host "  Testing $threads concurrent connections..." -ForegroundColor Gray
        
        $output = dotnet run --project LoadTest --configuration Release -- $threads $DurationSeconds $testUrl 2>&1 | Out-String
        
        if ($output -match "Throughput: (\d+) req") {
            $rps = [int]$matches[1]
            $results += @{ Threads = $threads; ReqPerSec = $rps }
            Write-Host "    -> $rps req/s" -ForegroundColor Green
        }
    }
    
    taskkill /F /IM dotnet.exe 2>$null | Out-Null
    Start-Sleep -Seconds 2
    
    return $results
}

$effinitiveResults = Test-Framework -Name "EffinitiveFramework" -Project "samples/EffinitiveFramework.Sample" -Port 5000 -Endpoint "/"
$genHttpResults = Test-Framework -Name "GenHTTP" -Project "GenHttpSample" -Port 5001 -Endpoint "/api/benchmark"

Write-Host "`n===============================================================" -ForegroundColor Cyan
Write-Host "                     RESULTS SUMMARY" -ForegroundColor Cyan
Write-Host "===============================================================`n" -ForegroundColor Cyan

Write-Host "Concurrency | EffinitiveFramework | GenHTTP       | Winner" -ForegroundColor White
Write-Host "------------|---------------------|---------------|------------" -ForegroundColor White

for ($i = 0; $i -lt 3; $i++) {
    $ef = $effinitiveResults[$i].ReqPerSec
    $gh = $genHttpResults[$i].ReqPerSec
    $threads = $effinitiveResults[$i].Threads
    
    $efStr = "{0,12:N0}" -f $ef
    $ghStr = "{0,12:N0}" -f $gh
    
    $winner = if ($ef -gt $gh) { 
        $pct = [math]::Round((($ef - $gh) / $gh) * 100, 1)
        "Effinitive +$pct%"
    } else { 
        $pct = [math]::Round((($gh - $ef) / $ef) * 100, 1)
        "GenHTTP +$pct%"
    }
    
    $color = if ($ef -gt $gh) { "Green" } else { "Yellow" }
    
    Write-Host ("{0,11} | {1} req/s | {2} req/s | {3}" -f $threads, $efStr, $ghStr, $winner) -ForegroundColor $color
}

Write-Host ""

$effinitiveAvg = ($effinitiveResults | Measure-Object -Property ReqPerSec -Average).Average
$genHttpAvg = ($genHttpResults | Measure-Object -Property ReqPerSec -Average).Average

Write-Host "Average Throughput:" -ForegroundColor White
Write-Host "  EffinitiveFramework: $([math]::Round($effinitiveAvg, 0)) req/s" -ForegroundColor Cyan
Write-Host "  GenHTTP:             $([math]::Round($genHttpAvg, 0)) req/s" -ForegroundColor Cyan

if ($effinitiveAvg -gt $genHttpAvg) {
    $improvement = [math]::Round((($effinitiveAvg - $genHttpAvg) / $genHttpAvg) * 100, 1)
    Write-Host "`n  Winner: EffinitiveFramework is $improvement% faster!" -ForegroundColor Green
} else {
    $diff = [math]::Round((($genHttpAvg - $effinitiveAvg) / $effinitiveAvg) * 100, 1)
    Write-Host "`n  GenHTTP is $diff% faster" -ForegroundColor Yellow
}

Write-Host "`n"
