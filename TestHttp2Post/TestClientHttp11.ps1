$body = @{
    Name = "Test"
    Email = "test@test.com"
} | ConvertTo-Json

Write-Host "Testing HTTP/1.1 POST to http://localhost:5000/test"
Write-Host "Body: $body"

try {
    $response = Invoke-WebRequest -Uri 'http://localhost:5000/test' -Method POST -Body $body -ContentType 'application/json'
    Write-Host "Success! Status: $($response.StatusCode)"
    Write-Host "Response: $($response.Content)"
} catch {
    Write-Host "Error: $_"
    Write-Host $_.Exception
}
