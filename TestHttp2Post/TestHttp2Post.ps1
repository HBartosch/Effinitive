# Simple HTTP/2 POST test using .NET HttpClient
$code = @'
using System;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;

class Program
{
    static async Task Main()
    {
        var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (message, cert, chain, sslPolicyErrors) => true
        };

        using var client = new HttpClient(handler)
        {
            DefaultRequestVersion = new Version(2, 0),
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact
        };

        var json = "{\"Name\":\"HTTP/2 Test\",\"Email\":\"http2@test.com\"}";
        var content = new StringContent(json, Encoding.UTF8, "application/json");

        Console.WriteLine($"Testing HTTP/2 POST to https://localhost:6001/api/http2-benchmark");
        Console.WriteLine($"Body: {json}");
        
        try
        {
            var response = await client.PostAsync("https://localhost:6001/api/http2-benchmark", content);
            var responseBody = await response.Content.ReadAsStringAsync();
            
            Console.WriteLine($"Status: {(int)response.StatusCode} {response.StatusCode}");
            Console.WriteLine($"Protocol: HTTP/{response.Version}");
            Console.WriteLine($"Response: {responseBody}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error: {ex.Message}");
        }
    }
}
'@

Add-Type -TypeDefinition $code -ReferencedAssemblies @('System.Net.Http', 'System.Net.Primitives')
[Program]::Main().Wait()
