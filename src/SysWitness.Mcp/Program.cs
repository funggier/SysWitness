using System.Text;
using SysWitness.Mcp;
using SysWitness.Sdk;

Console.OutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
try
{
    var endpoint = Environment.GetEnvironmentVariable("SYSWITNESS_API_BASE_URL") ?? "http://127.0.0.1:8769";
    var token = Environment.GetEnvironmentVariable("SYSWITNESS_MCP_TOKEN") ?? "";
    using var client = new SysWitnessClient(new Uri(endpoint, UriKind.Absolute), token);
    await new McpServer(client).RunAsync(Console.OpenStandardInput());
    return 0;
}
catch (Exception ex) when (ex is ArgumentException or UriFormatException)
{
    Console.Error.WriteLine("Invalid local SysWitness endpoint or credential configuration.");
    return 1;
}
catch (Exception)
{
    Console.Error.WriteLine("SysWitness MCP stopped after a transport or internal failure.");
    return 1;
}
