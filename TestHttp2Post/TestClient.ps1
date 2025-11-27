# Ignore certificate errors for testing
add-type @"
    using System.Net;
    using System.Security.Cryptography.X509Certificates;
    public class TrustAllCertsPolicy : ICertificatePolicy {
        public bool CheckValidationResult(
            ServicePoint srvPoint, X509Certificate certificate,
            WebRequest request, int certificateProblem) {
            return true;
        }
    }
"@
[System.Net.ServicePointManager]::CertificatePolicy = New-Object TrustAllCertsPolicy
[System.Net.ServicePointManager]::SecurityProtocol = [System.Net.SecurityProtocolType]::Tls12

$body = @{
    Name = "Test"
    Email = "test@test.com"
} | ConvertTo-Json

Write-Host "Testing POST to https://localhost:5001/test"
Write-Host "Body: $body"

try {
    $response = Invoke-WebRequest -Uri 'https://localhost:5001/test' -Method POST -Body $body -ContentType 'application/json'
    Write-Host "Success! Status: $($response.StatusCode)"
    Write-Host "Protocol Version: $($response.Headers['Server'])"
    Write-Host "Response: $($response.Content)"
} catch {
    Write-Host "Error: $_"
    Write-Host $_.Exception
}
