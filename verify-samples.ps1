#!/usr/bin/env pwsh
# Comprehensive verification test

Write-Host "`n=== EffinitiveFramework Sample Verification ===" -ForegroundColor Cyan

$passed = 0
$failed = 0

function Test-EP {
    param([string]$name, [string]$method, [string]$uri, [string]$body, [int]$expect = 200)
    try {
        $p = @{Method=$method; Uri=$uri; UseBasicParsing=$true; TimeoutSec=5}
        if ($body) { $p.Body = $body; $p.ContentType = 'application/json' }
        $r = Invoke-WebRequest @p
        if ($r.StatusCode -eq $expect) {
            Write-Host "  ✅ $name" -ForegroundColor Green
            $script:passed++
        } else {
            Write-Host "  ❌ $name - Expected $expect, got $($r.StatusCode)" -ForegroundColor Red
            $script:failed++
        }
    } catch {
        if ($_.Exception.Message -like "*$expect*") {
            Write-Host "  ✅ $name" -ForegroundColor Green
            $script:passed++
        } else {
            Write-Host "  ❌ $name - $($_.Exception.Message.Substring(0, [Math]::Min(80, $_.Exception.Message.Length)))" -ForegroundColor Red
            $script:failed++
        }
    }
}

# Main Sample
Write-Host "`n1. Main Sample" -ForegroundColor Yellow
Start-Process powershell -ArgumentList "-WindowStyle","Minimized","-Command","cd '$PWD\samples\EffinitiveFramework.Sample'; dotnet run -c Release --no-build 2>&1 | Out-Null"
Start-Sleep 6

Test-EP "GET /" "GET" "http://localhost:5000/"
Test-EP "GET /user/123" "GET" "http://localhost:5000/user/123"
Test-EP "GET /api/users" "GET" "http://localhost:5000/api/users"
Test-EP "GET /api/health" "GET" "http://localhost:5000/api/health"
Test-EP "GET /health" "GET" "http://localhost:5000/health"
Test-EP "GET /api/plain" "GET" "http://localhost:5000/api/plain"
Test-EP "GET /api/html" "GET" "http://localhost:5000/api/html"
Test-EP "GET /api/stats/database" "GET" "http://localhost:5000/api/stats/database"
Test-EP "POST /api/users" "POST" "http://localhost:5000/api/users" (@{name='Alice';email='test@test.com'} | ConvertTo-Json)

taskkill /F /IM dotnet.exe 2>$null | Out-Null
Start-Sleep 3

# Validation Sample
Write-Host "`n2. Validation Sample" -ForegroundColor Yellow
Start-Process powershell -ArgumentList "-WindowStyle","Minimized","-Command","cd '$PWD\samples\EffinitiveFramework.Validation.Sample'; dotnet run -c Release --no-build 2>&1 | Out-Null"
Start-Sleep 6

$validUser = @{name='John';email='john@test.com';age=25;role='User';password='password123';confirmPassword='password123'} | ConvertTo-Json
$invalidUser = @{name='J';email='invalid';age=15} | ConvertTo-Json

Test-EP "POST /users with valid data" "POST" "http://localhost:5000/users" $validUser
Test-EP "POST /users with invalid data" "POST" "http://localhost:5000/users" $invalidUser 400

taskkill /F /IM dotnet.exe 2>$null | Out-Null
Start-Sleep 3

# Auth Sample
Write-Host "`n3. Auth Sample" -ForegroundColor Yellow
Start-Process powershell -ArgumentList "-WindowStyle","Minimized","-Command","cd '$PWD\samples\EffinitiveFramework.Auth.Sample'; dotnet run -c Release --no-build 2>&1 | Out-Null"
Start-Sleep 6

Test-EP "GET /public" "GET" "http://localhost:5000/public"

$loginBody = @{username='admin';password='admin123'} | ConvertTo-Json
try {
    $tokenResp = Invoke-WebRequest -Method Post -Uri "http://localhost:5000/auth/token" -Body $loginBody -ContentType "application/json" -UseBasicParsing
    $token = ($tokenResp.Content | ConvertFrom-Json).token
    Write-Host "  ✅ POST /auth/token" -ForegroundColor Green
    $script:passed++
    
    # Test protected endpoints with token
    try {
        $headers = @{Authorization = "Bearer $token"}
        $r = Invoke-WebRequest -Uri "http://localhost:5000/protected" -Headers $headers -UseBasicParsing -TimeoutSec 5
        Write-Host "  ✅ GET /protected with token" -ForegroundColor Green
        $script:passed++
    } catch {
        Write-Host "  ❌ GET /protected with token - $($_.Exception.Message)" -ForegroundColor Red
        $script:failed++
    }
    
    try {
        $headers = @{Authorization = "Bearer $token"}
        $r = Invoke-WebRequest -Uri "http://localhost:5000/admin" -Headers $headers -UseBasicParsing -TimeoutSec 5
        Write-Host "  ✅ GET /admin with token" -ForegroundColor Green
        $script:passed++
    } catch {
        Write-Host "  ❌ GET /admin with token - $($_.Exception.Message)" -ForegroundColor Red
        $script:failed++
    }
    
    try {
        $headers = @{Authorization = "Bearer $token"}
        $r = Invoke-WebRequest -Uri "http://localhost:5000/me" -Headers $headers -UseBasicParsing -TimeoutSec 5
        Write-Host "  ✅ GET /me with token" -ForegroundColor Green
        $script:passed++
    } catch {
        Write-Host "  ❌ GET /me with token - $($_.Exception.Message)" -ForegroundColor Red
        $script:failed++
    }
} catch {
    Write-Host "  ❌ Auth endpoints - $_" -ForegroundColor Red
    $script:failed++
}

taskkill /F /IM dotnet.exe 2>$null | Out-Null
Start-Sleep 3

# EFCore Sample
Write-Host "`n4. EFCore Sample" -ForegroundColor Yellow
Start-Process powershell -ArgumentList "-WindowStyle","Minimized","-Command","cd '$PWD\samples\EffinitiveFramework.EFCore.Sample'; dotnet run -c Release --no-build 2>&1 | Out-Null"
Start-Sleep 7

Test-EP "GET /api/products" "GET" "http://localhost:5000/api/products"
Test-EP "GET /api/products/1" "GET" "http://localhost:5000/api/products/1"
Test-EP "GET /api/orders" "GET" "http://localhost:5000/api/orders"

$newProduct = @{name='Test';price=99.99;stock=10} | ConvertTo-Json
Test-EP "POST /api/products" "POST" "http://localhost:5000/api/products" $newProduct

taskkill /F /IM dotnet.exe 2>$null | Out-Null

# Summary
Write-Host "`n=== Summary ===" -ForegroundColor Cyan
$total = $passed + $failed
$pct = if ($total -gt 0) { [math]::Round(($passed/$total)*100, 1) } else { 0 }
Write-Host "Total: $total | Passed: $passed | Failed: $failed | Success: $pct%" -ForegroundColor $(if ($failed -eq 0) {'Green'} else {'Yellow'})

if ($failed -eq 0) {
    Write-Host "`n🎉 All samples working perfectly!" -ForegroundColor Green
} else {
    Write-Host "`n⚠️  $failed test(s) failed" -ForegroundColor Yellow
}
