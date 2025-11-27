using System;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using EffinitiveFramework.Core;
using EffinitiveFramework.Core.Configuration;

namespace TestHttp2Post
{
    // GET endpoint
    public class GetTestEndpoint : EndpointBase<EmptyRequest, TestResponse>
    {
        protected override string Route => "/test";
        protected override string Method => "GET";

        public override ValueTask<TestResponse> HandleAsync(EmptyRequest request, CancellationToken cancellationToken)
        {
            return ValueTask.FromResult(new TestResponse
            {
                Message = "GET request successful",
                Success = true
            });
        }
    }

    // POST endpoint
    public class PostTestEndpoint : EndpointBase<TestRequest, TestResponse>
    {
        protected override string Route => "/test";
        protected override string Method => "POST";

        public override ValueTask<TestResponse> HandleAsync(TestRequest request, CancellationToken cancellationToken)
        {
            return ValueTask.FromResult(new TestResponse
            {
                Message = $"Received: {request?.Name} - {request?.Email}",
                Success = true
            });
        }
    }

    public class EmptyRequest { }

    public class TestRequest
    {
        public string? Name { get; set; }
        public string? Email { get; set; }
    }

    public class TestResponse
    {
        public string Message { get; set; } = "";
        public bool Success { get; set; }
    }

    class Program
    {
        static async Task Main(string[] args)
        {
            Console.WriteLine("Starting HTTP/2 test server on https://localhost:5001...");
            
            var cts = new CancellationTokenSource();
            Console.CancelKeyPress += (s, e) =>
            {
                e.Cancel = true;
                cts.Cancel();
            };

            var app = EffinitiveApp.Create()
                .UseHttpsPort(5001)
                .ConfigureTls(tls =>
                {
                    tls.CertificatePath = "localhost.pfx";
                    tls.CertificatePassword = "dev-password";
                })
                .MapEndpoints(Assembly.GetExecutingAssembly())
                .Build();

            Console.WriteLine("Server started!");
            Console.WriteLine("Test with:");
            Console.WriteLine("  GET:  Invoke-WebRequest -Uri 'https://localhost:5001/test' -Method GET -SkipCertificateCheck");
            Console.WriteLine("  POST: Invoke-WebRequest -Uri 'https://localhost:5001/test' -Method POST -Body '{\"Name\":\"Test\",\"Email\":\"test@test.com\"}' -ContentType 'application/json' -SkipCertificateCheck");
            Console.WriteLine("\nPress Ctrl+C to stop...\n");

            await app.RunAsync(cts.Token);
        }
    }
}
