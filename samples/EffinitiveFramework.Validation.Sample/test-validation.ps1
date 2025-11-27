# Test invalid user data
Write-Host "Testing validation with INVALID data..." -ForegroundColor Yellow
Write-Host ""

$invalidUser = @{
    name = 'J'                   # Too short (min 2 chars)
    email = 'invalid'            # Not a valid email
    age = 15                     # Too young (min 18)
    role = 'InvalidRole'         # Not in enum
    password = '123'             # Too short (min 8 chars)
    confirmPassword = 'different' # Doesn't match password
}

try {
    $response = Invoke-RestMethod -Method Post -Uri http://localhost:5000/users `
        -Body ($invalidUser | ConvertTo-Json) `
        -ContentType 'application/json'
    
    Write-Host "Unexpected success:" -ForegroundColor Red
    $response | ConvertTo-Json -Depth 5
}
catch {
    $statusCode = $_.Exception.Response.StatusCode.Value__
    Write-Host "Status Code: $statusCode" -ForegroundColor Cyan
    
    if ($_.ErrorDetails.Message) {
        Write-Host "Validation Errors:" -ForegroundColor Cyan
        $_.ErrorDetails.Message | ConvertFrom-Json | ConvertTo-Json -Depth 5
    }
}

Write-Host ""
Write-Host "-----------------------------------" -ForegroundColor Gray
Write-Host ""

# Test valid user data
Write-Host "Testing validation with VALID data..." -ForegroundColor Green
Write-Host ""

$validUser = @{
    name = 'John Doe'
    email = 'john@example.com'
    age = 25
    role = 'User'
    password = 'password123'
    confirmPassword = 'password123'
}

try {
    $response = Invoke-RestMethod -Method Post -Uri http://localhost:5000/users `
        -Body ($validUser | ConvertTo-Json) `
        -ContentType 'application/json'
    
    Write-Host "✅ Success! User created:" -ForegroundColor Green
    $response | ConvertTo-Json -Depth 5
}
catch {
    Write-Host "❌ Unexpected error:" -ForegroundColor Red
    $_.Exception.Message
}
