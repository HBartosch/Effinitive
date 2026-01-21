#!/usr/bin/env pwsh
# Comprehensive verification of all sample endpoints

Write-Host "`n╔════════════════════════════════════════════════════════╗" -ForegroundColor Cyan
Write-Host "║  EffinitiveFramework - Comprehensive Sample Test     ║" -ForegroundColor Cyan
Write-Host "╚════════════════════════════════════════════════════════╝`n" -ForegroundColor Cyan

$results = @()
$totalTests = 0
$passedTests = 0

function Test-Endpoint {
    param(
        [string]$Name,
        [string]$Method,
        [string]$Uri,
        [string]$Body = $null,
        [int]$ExpectedStatus = 200
    )
    
    $script:totalTests++
    
    try {
        $params = @{
            Method = $Method
            Uri = $Uri
            UseBasicParsing = $true
            TimeoutSec = 5
        }
        
        if ($Body) {
            $params.Body = $Body
            $params.ContentType = "application/json"
        }
        
        $response = Invoke-WebRequest @params
        
        if ($response.StatusCode -eq $ExpectedStatus) {
            Write-Host "  ✅ $Name" -ForegroundColor Green
            $script:passedTests++
            return $true
        } else {
            Write-Host "  ❌ $Name - Expected $ExpectedStatus, got $($response.StatusCode)" -ForegroundColor Red
            return $false
        }
    } catch {
        $errorMsg = $_.Exception.Message
        if ($errorMsg -like "*$ExpectedStatus*") {
            Write-Host "  ✅ $Name" -ForegroundColor Green
            $script:passedTests++
            return $true
        }
        Write-Host "  ❌ $Name - $errorMsg" -ForegroundColor Red
        return $false
    }
}

# ============================================================================
# Test 1: Main Sample (NoRequestEndpointBase + EndpointBase)
# ============================================================================
Write-Host "1. Main Sample - Mixed Endpoint Types" -ForegroundColor Yellow
Start-Process powershell -ArgumentList "-NoExit", "-WindowStyle", "Minimized", "-Command", "cd '$PWD\samples\EffinitiveFramework.Sample'; dotnet run --configuration Release --no-build 2>&1 | Out-Null"
Start-Sleep -Seconds 6

Test-Endpoint "GET / (NoRequestEndpointBase)" "GET" "http://localhost:5000/"
Test-Endpoint "GET /user/123 (NoRequestEndpointBase with route param)" "GET" "http://localhost:5000/user/123"
Test-Endpoint "GET /api/users (EndpointBase EmptyRequest)" "GET" "http://localhost:5000/api/users"
Test-Endpoint "GET /api/health (NoRequestEndpointBase)" "GET" "http://localhost:5000/api/health"
Test-Endpoint "GET /health (NoRequestEndpointBase)" "GET" "http://localhost:5000/health"
Test-Endpoint "GET /api/plain (NoRequestEndpointBase text/plain)" "GET" "http://localhost:5000/api/plain"
Test-Endpoint "GET /api/html (NoRequestAsyncEndpointBase text/html)" "GET" "http://localhost:5000/api/html"
Test-Endpoint "GET /api/stats/database (NoRequestAsyncEndpointBase)" "GET" "http://localhost:5000/api/stats/database"

$userBody = @{name='Alice';email='alice@test.com'} | ConvertTo-Json
Test-Endpoint "POST /api/users (EndpointBase CreateUserRequest)" "POST" "http://localhost:5000/api/users" $userBody

taskkill /F /IM dotnet.exe 2>$null | Out-Null
Start-Sleep -Seconds 3

# ============================================================================
# Test 2: Validation Sample (AsyncEndpointBase with validation)
# ============================================================================
Write-Host "`n2. Validation Sample - AsyncEndpointBase with Validation" -ForegroundColor Yellow
Start-Process powershell -ArgumentList "-NoExit", "-WindowStyle", "Minimized", "-Command", "cd '$PWD\samples\EffinitiveFramework.Validation.Sample'; dotnet run --configuration Release --no-build 2>&1 | Out-Null"
Start-Sleep -Seconds 6

$validUser = @{
    name='John Doe'
    email='john@example.com'
    age=25
    role='User'
    password='password123'
    confirmPassword='password123'
} | ConvertTo-Json

$invalidUser = @{
    name='J'
    email='invalid'
    age=15
} | ConvertTo-Json

$validOrder = @{
    productName='Widget'
    quantity=5
    unitPrice=10.50
    minimumOrderValue=50
    totalAmount=52.50
    shippingAddresses=@('123 Main St')
} | ConvertTo-Json

Test-Endpoint "POST /users (valid data)" "POST" "http://localhost:5000/users" $validUser
Test-Endpoint "POST /users (invalid data, expect 400)" "POST" "http://localhost:5000/users" $invalidUser 400
Test-Endpoint "POST /orders (valid data)" "POST" "http://localhost:5000/orders" $validOrder

taskkill /F /IM dotnet.exe 2>$null | Out-Null
Start-Sleep -Seconds 3

# ============================================================================
# Test 3: Auth Sample (AsyncEndpointBase with JWT auth)
# ============================================================================
Write-Host "`n3. Auth Sample - AsyncEndpointBase with Authentication" -ForegroundColor Yellow
Start-Process powershell -ArgumentList "-NoExit", "-WindowStyle", "Minimized", "-Command", "cd '$PWD\samples\EffinitiveFramework.Auth.Sample'; dotnet run --configuration Release --no-build 2>&1 | Out-Null"
Start-Sleep -Seconds 6

Test-Endpoint "GET /public (no auth required)" "GET" "http://localhost:5000/public"

$loginBody = @{username='admin';password='admin123'} | ConvertTo-Json
$tokenResponse = Invoke-WebRequest -Method Post -Uri "http://localhost:5000/auth/token" -Body $loginBody -ContentType "application/json" -UseBasicParsing
$token = ($tokenResponse.Content | ConvertFrom-Json).token

if ($token) {
    Write-Host "  ✅ POST /auth/token (get JWT token)" -ForegroundColor Green
    $script:passedTests++
    $script:totalTests++
    
    # Test protected endpoints with token
    try {
        $headers = @{Authorization = "Bearer $token"}
        $r = Invoke-WebRequest -Uri "http://localhost:5000/protected" -Headers $headers -UseBasicParsing -TimeoutSec 5
        Write-Host "  ✅ GET /protected (with valid token)" -ForegroundColor Green
        $script:passedTests++
        $script:totalTests++
    } catch {
        Write-Host "  ❌ GET /protected (with valid token) - $_" -ForegroundColor Red
        $script:totalTests++
    }
    
    try {
        $r = Invoke-WebRequest -Uri "http://localhost:5000/admin" -Headers $headers -UseBasicParsing -TimeoutSec 5
        Write-Host "  ✅ GET /admin (with admin token)" -ForegroundColor Green
        $script:passedTests++
        $script:totalTests++
    } catch {
        Write-Host "  ❌ GET /admin (with admin token) - $_" -ForegroundColor Red
        $script:totalTests++
    }
    
    try {
        $r = Invoke-WebRequest -Uri "http://localhost:5000/me" -Headers $headers -UseBasicParsing -TimeoutSec 5
        Write-Host "  ✅ GET /me (user info from token)" -ForegroundColor Green
        $script:passedTests++
        $script:totalTests++
    } catch {
        Write-Host "  ❌ GET /me (user info from token) - $_" -ForegroundColor Red
        $script:totalTests++
    }
} else {
    Write-Host "  ❌ POST /auth/token - No token received" -ForegroundColor Red
    $script:totalTests++
}

# Test unauthorized access
try {
    Invoke-WebRequest -Uri "http://localhost:5000/protected" -UseBasicParsing -TimeoutSec 5 -ErrorAction Stop
    Write-Host "  ❌ GET /protected (no token) - Should have failed" -ForegroundColor Red
    $script:totalTests++
} catch {
    if ($_.Exception.Message -like "*401*" -or $_.Exception.Message -like "*Unauthorized*") {
        Write-Host "  ✅ GET /protected (no token, expect 401)" -ForegroundColor Green
        $script:passedTests++
    } else {
        Write-Host "  ❌ GET /protected (no token) - $_" -ForegroundColor Red
    }
    $script:totalTests++
}

taskkill /F /IM dotnet.exe 2>$null | Out-Null
Start-Sleep -Seconds 3

# ============================================================================
# Test 4: EFCore Sample (AsyncEndpointBase with database)
# ============================================================================
Write-Host "`n4. EFCore Sample - AsyncEndpointBase with Entity Framework" -ForegroundColor Yellow
Start-Process powershell -ArgumentList "-NoExit", "-WindowStyle", "Minimized", "-Command", "cd '$PWD\samples\EffinitiveFramework.EFCore.Sample'; dotnet run --configuration Release --no-build 2>&1 | Out-Null"
Start-Sleep -Seconds 7

Test-Endpoint "GET /api/products (list all)" "GET" "http://localhost:5000/api/products"
Test-Endpoint "GET /api/products/1 (get by id)" "GET" "http://localhost:5000/api/products/1"
Test-Endpoint "GET /api/orders (list all)" "GET" "http://localhost:5000/api/orders"

$newProduct = @{
    name='Test Product'
    price=99.99
    stock=10
} | ConvertTo-Json

Test-Endpoint "POST /api/products (create)" "POST" "http://localhost:5000/api/products" $newProduct

taskkill /F /IM dotnet.exe 2>$null | Out-Null

# ============================================================================
# Summary
# ============================================================================
Write-Host "`n╔════════════════════════════════════════════════════════╗" -ForegroundColor Cyan
Write-Host "║  Test Summary                                         ║" -ForegroundColor Cyan
Write-Host "╚════════════════════════════════════════════════════════╝" -ForegroundColor Cyan

$percentage = [math]::Round(($passedTests / $totalTests) * 100, 1)
$color = if ($passedTests -eq $totalTests) { "Green" } else { "Yellow" }

Write-Host "`nTotal Tests: $totalTests" -ForegroundColor White
Write-Host "Passed: $passedTests" -ForegroundColor Green
Write-Host "Failed: $($totalTests - $passedTests)" -ForegroundColor $(if ($passedTests -eq $totalTests) { "Green" } else { "Red" })
Write-Host "Success Rate: $percentage%" -ForegroundColor $color

if ($passedTests -eq $totalTests) {
    Write-Host "`n🎉 All tests passed! Framework is fully functional!" -ForegroundColor Green
} else {
    Write-Host "`n⚠️  Some tests failed. Review the output above." -ForegroundColor Yellow
}

Write-Host ""
