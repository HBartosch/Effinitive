#!/usr/bin/env pwsh
# Test all sample endpoints to verify they work after the fix

Write-Host "=== Testing All EffinitiveFramework Samples ===" -ForegroundColor Cyan
Write-Host ""

$results = @()

# Test 1: Main Sample
Write-Host "1. Testing Main Sample..." -ForegroundColor Yellow
Start-Process powershell -ArgumentList "-NoExit", "-Command", "cd '$PWD'; dotnet run --project samples/EffinitiveFramework.Sample --configuration Release" -WindowStyle Minimized
Start-Sleep -Seconds 5

try {
    $response = Invoke-WebRequest -Uri "http://localhost:5000/" -UseBasicParsing -TimeoutSec 5
    $results += @{Sample="Main"; Endpoint="GET /"; Status=$response.StatusCode; Result="✅ PASS"}
    Write-Host "  ✅ GET / - $($response.StatusCode)" -ForegroundColor Green
} catch {
    $results += @{Sample="Main"; Endpoint="GET /"; Status="Error"; Result="❌ FAIL: $_"}
    Write-Host "  ❌ GET / - FAILED: $_" -ForegroundColor Red
}

try {
    $response = Invoke-WebRequest -Uri "http://localhost:5000/api/users" -UseBasicParsing -TimeoutSec 5
    $results += @{Sample="Main"; Endpoint="GET /api/users"; Status=$response.StatusCode; Result="✅ PASS"}
    Write-Host "  ✅ GET /api/users - $($response.StatusCode)" -ForegroundColor Green
} catch {
    $results += @{Sample="Main"; Endpoint="GET /api/users"; Status="Error"; Result="❌ FAIL: $_"}
    Write-Host "  ❌ GET /api/users - FAILED: $_" -ForegroundColor Red
}

taskkill /F /IM dotnet.exe 2>$null | Out-Null
Start-Sleep -Seconds 2

# Test 2: Validation Sample
Write-Host ""
Write-Host "2. Testing Validation Sample..." -ForegroundColor Yellow
Start-Process powershell -ArgumentList "-NoExit", "-Command", "cd '$PWD'; dotnet run --project samples/EffinitiveFramework.Validation.Sample --configuration Release" -WindowStyle Minimized
Start-Sleep -Seconds 5

try {
    $body = @{
        name = "John Doe"
        email = "john@example.com"
        age = 25
        role = "User"
        password = "password123"
        confirmPassword = "password123"
    } | ConvertTo-Json
    
    $response = Invoke-WebRequest -Method Post -Uri "http://localhost:5000/users" -Body $body -ContentType "application/json" -UseBasicParsing -TimeoutSec 5
    $results += @{Sample="Validation"; Endpoint="POST /users"; Status=$response.StatusCode; Result="✅ PASS"}
    Write-Host "  ✅ POST /users - $($response.StatusCode)" -ForegroundColor Green
} catch {
    $results += @{Sample="Validation"; Endpoint="POST /users"; Status="Error"; Result="❌ FAIL: $_"}
    Write-Host "  ❌ POST /users - FAILED: $_" -ForegroundColor Red
}

taskkill /F /IM dotnet.exe 2>$null | Out-Null
Start-Sleep -Seconds 2

# Test 3: Auth Sample
Write-Host ""
Write-Host "3. Testing Auth Sample..." -ForegroundColor Yellow
Start-Process powershell -ArgumentList "-NoExit", "-Command", "cd '$PWD'; dotnet run --project samples/EffinitiveFramework.Auth.Sample --configuration Release" -WindowStyle Minimized
Start-Sleep -Seconds 5

try {
    $response = Invoke-WebRequest -Uri "http://localhost:5000/public" -UseBasicParsing -TimeoutSec 5
    $results += @{Sample="Auth"; Endpoint="GET /public"; Status=$response.StatusCode; Result="✅ PASS"}
    Write-Host "  ✅ GET /public - $($response.StatusCode)" -ForegroundColor Green
} catch {
    $results += @{Sample="Auth"; Endpoint="GET /public"; Status="Error"; Result="❌ FAIL: $_"}
    Write-Host "  ❌ GET /public - FAILED: $_" -ForegroundColor Red
}

try {
    $body = @{
        username = "admin"
        password = "admin123"
    } | ConvertTo-Json
    
    $response = Invoke-WebRequest -Method Post -Uri "http://localhost:5000/auth/token" -Body $body -ContentType "application/json" -UseBasicParsing -TimeoutSec 5
    $results += @{Sample="Auth"; Endpoint="POST /auth/token"; Status=$response.StatusCode; Result="✅ PASS"}
    Write-Host "  ✅ POST /auth/token - $($response.StatusCode)" -ForegroundColor Green
} catch {
    $results += @{Sample="Auth"; Endpoint="POST /auth/token"; Status="Error"; Result="❌ FAIL: $_"}
    Write-Host "  ❌ POST /auth/token - FAILED: $_" -ForegroundColor Red
}

taskkill /F /IM dotnet.exe 2>$null | Out-Null
Start-Sleep -Seconds 2

# Test 4: EFCore Sample
Write-Host ""
Write-Host "4. Testing EFCore Sample..." -ForegroundColor Yellow
Start-Process powershell -ArgumentList "-NoExit", "-Command", "cd '$PWD'; dotnet run --project samples/EffinitiveFramework.EFCore.Sample --configuration Release" -WindowStyle Minimized
Start-Sleep -Seconds 6

try {
    $response = Invoke-WebRequest -Uri "http://localhost:5000/api/products" -UseBasicParsing -TimeoutSec 5
    $results += @{Sample="EFCore"; Endpoint="GET /api/products"; Status=$response.StatusCode; Result="✅ PASS"}
    Write-Host "  ✅ GET /api/products - $($response.StatusCode)" -ForegroundColor Green
} catch {
    $results += @{Sample="EFCore"; Endpoint="GET /api/products"; Status="Error"; Result="❌ FAIL: $_"}
    Write-Host "  ❌ GET /api/products - FAILED: $_" -ForegroundColor Red
}

taskkill /F /IM dotnet.exe 2>$null | Out-Null
Start-Sleep -Seconds 2

# Summary
Write-Host ""
Write-Host "=== Test Summary ===" -ForegroundColor Cyan
$passed = ($results | Where-Object { $_.Result -like "*PASS*" }).Count
$total = $results.Count
Write-Host "Passed: $passed / $total" -ForegroundColor $(if ($passed -eq $total) { "Green" } else { "Yellow" })
Write-Host ""

foreach ($result in $results) {
    $color = if ($result.Result -like "*PASS*") { "Green" } else { "Red" }
    Write-Host "  $($result.Sample.PadRight(12)) $($result.Endpoint.PadRight(20)) $($result.Result)" -ForegroundColor $color
}

Write-Host ""
if ($passed -eq $total) {
    Write-Host "🎉 All samples working correctly!" -ForegroundColor Green
} else {
    Write-Host "⚠️  Some samples have issues" -ForegroundColor Yellow
}
