#!/usr/bin/env pwsh
# Quick test of all sample endpoints

Write-Host "`n=== Testing All Samples ===" -ForegroundColor Cyan

# Test 1: Main Sample
Write-Host "`n1. Main Sample" -ForegroundColor Yellow
Start-Process powershell -ArgumentList "-NoExit", "-WindowStyle", "Minimized", "-Command", "cd '$PWD\samples\EffinitiveFramework.Sample'; dotnet run --configuration Release --no-build"
Start-Sleep -Seconds 6

try {
    $r = Invoke-WebRequest -Uri "http://localhost:5000/" -UseBasicParsing -TimeoutSec 5
    Write-Host "  ✅ GET / - $($r.StatusCode)" -ForegroundColor Green
    $r = Invoke-WebRequest -Uri "http://localhost:5000/api/users" -UseBasicParsing -TimeoutSec 5
    Write-Host "  ✅ GET /api/users - $($r.StatusCode)" -ForegroundColor Green
} catch {
    Write-Host "  ❌ FAILED: $_" -ForegroundColor Red
}

taskkill /F /IM dotnet.exe 2>$null | Out-Null
Start-Sleep -Seconds 3

# Test 2: Validation Sample  
Write-Host "`n2. Validation Sample" -ForegroundColor Yellow
Start-Process powershell -ArgumentList "-NoExit", "-WindowStyle", "Minimized", "-Command", "cd '$PWD\samples\EffinitiveFramework.Validation.Sample'; dotnet run --configuration Release --no-build"
Start-Sleep -Seconds 6

try {
    $body = @{name='John';email='john@test.com';age=25;role='User';password='password123';confirmPassword='password123'} | ConvertTo-Json
    $r = Invoke-WebRequest -Method Post -Uri "http://localhost:5000/users" -Body $body -ContentType "application/json" -UseBasicParsing -TimeoutSec 5
    Write-Host "  ✅ POST /users - $($r.StatusCode)" -ForegroundColor Green
} catch {
    Write-Host "  ❌ FAILED: $_" -ForegroundColor Red
}

taskkill /F /IM dotnet.exe 2>$null | Out-Null
Start-Sleep -Seconds 3

# Test 3: Auth Sample
Write-Host "`n3. Auth Sample" -ForegroundColor Yellow  
Start-Process powershell -ArgumentList "-NoExit", "-WindowStyle", "Minimized", "-Command", "cd '$PWD\samples\EffinitiveFramework.Auth.Sample'; dotnet run --configuration Release --no-build"
Start-Sleep -Seconds 6

try {
    $r = Invoke-WebRequest -Uri "http://localhost:5000/public" -UseBasicParsing -TimeoutSec 5
    Write-Host "  ✅ GET /public - $($r.StatusCode)" -ForegroundColor Green
} catch {
    Write-Host "  ❌ FAILED: $_" -ForegroundColor Red
}

taskkill /F /IM dotnet.exe 2>$null | Out-Null
Start-Sleep -Seconds 3

# Test 4: EFCore Sample
Write-Host "`n4. EFCore Sample" -ForegroundColor Yellow
Start-Process powershell -ArgumentList "-NoExit", "-WindowStyle", "Minimized", "-Command", "cd '$PWD\samples\EffinitiveFramework.EFCore.Sample'; dotnet run --configuration Release --no-build"
Start-Sleep -Seconds 7

try {
    $r = Invoke-WebRequest -Uri "http://localhost:5000/api/products" -UseBasicParsing -TimeoutSec 5
    Write-Host "  ✅ GET /api/products - $($r.StatusCode)" -ForegroundColor Green
} catch {
    Write-Host "  ❌ FAILED: $_" -ForegroundColor Red
}

taskkill /F /IM dotnet.exe 2>$null | Out-Null

Write-Host "`n=== All Tests Complete! ===" -ForegroundColor Cyan
Write-Host ""
