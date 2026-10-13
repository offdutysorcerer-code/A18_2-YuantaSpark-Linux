using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;

// An offline, credential-free SDK regression check. Does NOT initialize or login to the broker.
var sdkPath = Environment.GetEnvironmentVariable("YUANTA_SDK_PATH") ?? "/vendor/YuantaSparkAPI.dll";
if (!File.Exists(sdkPath)) throw new FileNotFoundException("SDK not found", sdkPath);
var assembly = Assembly.LoadFrom(sdkPath);
var acnoType = assembly.GetType("YuantaOneAPI.Acno", true)!;
var rightType = assembly.GetType("YuantaOneAPI.MarketRight", true)!;
var apiType = assembly.GetType("YuantaOneAPI.APIHandle", true)!;
var rightField = acnoType.GetField("LstMarketRight", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)!;
var update = apiType.GetMethod("UpdateMarketLimit", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!;
var listType = typeof(List<>).MakeGenericType(rightType);

string RunCase(string name, bool nullAccount = false, bool nullRights = false)
{
    var account = Activator.CreateInstance(acnoType)!;
    if (nullRights) rightField.SetValue(account, null);
    var args = new object?[] { Activator.CreateInstance(listType)!, new List<int>(), nullAccount ? null : account };
    try
    {
        update.Invoke(null, args);
        return $"{name}: OK";
    }
    catch (TargetInvocationException e)
    {
        return $"{name}: {e.InnerException?.GetType().Name ?? e.GetType().Name}";
    }
}

var outputs = new[]
{
    RunCase("account_not_found", nullAccount: true),
    RunCase("rights_list_null", nullRights: true),
    RunCase("rights_list_initialized")
};
Console.WriteLine($"SDK SHA256: {Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(sdkPath))).ToLowerInvariant()}");
foreach (var output in outputs) Console.WriteLine(output);
if (!outputs[0].EndsWith("NullReferenceException") || !outputs[1].EndsWith("NullReferenceException") || !outputs[2].EndsWith("OK"))
    throw new Exception("Unexpected SDK behavior: investigate before changing broker-facing code.");
Console.WriteLine("PASS (offline regression only; live account state NOT examined)");
