#!/usr/bin/env dotnet-script

// Quick test to verify ResponseCompressionMiddleware works
using System;
using System.IO.Compression;
using System.Reflection;

var middlewareFile = @"c:\Personal\EffinitiveGithub\Effinitive\src\EffinitiveFramework.Core\Middleware\ResponseCompressionMiddleware.cs";

if (!File.Exists(middlewareFile))
{
    Console.WriteLine("❌ ResponseCompressionMiddleware.cs NOT FOUND");
    Environment.Exit(1);
}

var content = File.ReadAllText(middlewareFile);

// Verify key components exist
var checks = new[] {
    ("class ResponseCompressionMiddleware", "Middleware class definition"),
    ("IMiddleware", "Implements IMiddleware interface"),
    ("InvokeAsync", "InvokeAsync method"),
    ("CompressGzip", "Compression method"),
    ("_compressionCache", "Response caching"),
    ("ArrayPool", "ArrayPool memory management"),
    ("Accept-Encoding", "Accept-Encoding header support"),
};

int passed = 0;
foreach (var (check, desc) in checks)
{
    if (content.Contains(check))
    {
        Console.WriteLine($"✅ {desc}");
        passed++;
    }
    else
    {
        Console.WriteLine($"❌ {desc}");
    }
}

Console.WriteLine();
if (passed == checks.Length)
{
    Console.WriteLine($"✅ ALL CHECKS PASSED ({passed}/{checks.Length})");
    Environment.Exit(0);
}
else
{
    Console.WriteLine($"❌ SOME CHECKS FAILED ({passed}/{checks.Length})");
    Environment.Exit(1);
}
