using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

internal static class Fixture
{
    public const string Token = "synthetic-client-test-token-00000000";
    public const string GuidText = "11111111-2222-4333-8444-555555555555";
    public static void Check(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }
}

internal sealed class McpPeer : IAsyncDisposable
{
    private readonly Process process;
    private readonly Channel<string> lines = Channel.CreateUnbounded<string>();
    private readonly Task outputPump;
    private readonly Task<string> errors;
    public string? Diagnostics { get; private set; }

    public McpPeer(string endpoint)
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var configuration = Directory.GetParent(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar))!.Name;
        var dll = Path.Combine(root, "src", "SysWitness.Mcp", "bin", configuration, "net10.0", "SysWitness.Mcp.dll");
        var start = new ProcessStartInfo("dotnet") { RedirectStandardInput = true,
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        start.ArgumentList.Add(dll);
        start.Environment["SYSWITNESS_API_BASE_URL"] = endpoint;
        start.Environment["SYSWITNESS_MCP_TOKEN"] = Fixture.Token;
        process = Process.Start(start)!;
        errors = process.StandardError.ReadToEndAsync();
        outputPump = Task.Run(async () =>
        {
            try
            {
                while (await process.StandardOutput.ReadLineAsync() is { } line)
                    await lines.Writer.WriteAsync(line);
            }
            finally { lines.Writer.TryComplete(); }
        });
    }

    public async Task Send(string json)
    {
        await process.StandardInput.WriteLineAsync(json);
        await process.StandardInput.FlushAsync();
    }

    public async Task<JsonElement?> Read(int timeout = 3000)
    {
        using var cts = new CancellationTokenSource(timeout);
        try
        {
            var line = await lines.Reader.ReadAsync(cts.Token);
            using var document = JsonDocument.Parse(line);
            return document.RootElement.Clone();
        }
        catch (OperationCanceledException) { return null; }
        catch (ChannelClosedException) { return null; }
    }

    public async Task<JsonElement> Require(int timeout = 3000)
    {
        return await Read(timeout) ?? throw new InvalidOperationException("Expected a JSON-RPC response; server returned none.");
    }

    public async Task ExpectExit(int exitCode)
    {
        using var cts = new CancellationTokenSource(3000);
        await process.WaitForExitAsync(cts.Token);
        Fixture.Check(process.ExitCode == exitCode, "Unexpected server exit status.");
    }

    public void CloseInput() => process.StandardInput.Close();

    public async Task Initialize()
    {
        await Send("""{"jsonrpc":"2.0","id":"init","method":"initialize","params":{"protocolVersion":"2025-11-25","capabilities":{},"clientInfo":{"name":"synthetic-tests","version":"1"}}}""");
        var response = await Require();
        Fixture.Check(response.TryGetProperty("result", out var r) &&
            r.GetProperty("protocolVersion").GetString() == "2025-11-25", "Expected supported initialize result.");
        await Send("""{"jsonrpc":"2.0","method":"notifications/initialized"}""");
    }

    public async Task<JsonElement> Call(string name, string arguments, int id = 2)
    {
        await Send("{\"jsonrpc\":\"2.0\",\"id\":" + id + ",\"method\":\"tools/call\",\"params\":{\"name\":\"" + name + "\",\"arguments\":" + arguments + "}}");
        return await Require();
    }

    public async ValueTask DisposeAsync()
    {
        try { process.StandardInput.Close(); } catch (IOException) { }
        using var cts = new CancellationTokenSource(3000);
        try { await process.WaitForExitAsync(cts.Token); }
        catch (OperationCanceledException) { process.Kill(true); await process.WaitForExitAsync(); }
        await outputPump;
        Diagnostics = await errors;
        process.Dispose();
        Fixture.Check(!Diagnostics.Contains(Fixture.Token, StringComparison.Ordinal), "Credential leaked to stderr.");
    }
}

internal sealed class FakeApi : IAsyncDisposable
{
    private readonly TcpListener listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource stop = new();
    private readonly ConcurrentBag<Task> clients = [];
    private readonly Task accept;
    public ConcurrentQueue<(string Target, string Token)> Requests { get; } = new();
    public string Origin { get; }
    public int Status { get; set; } = 200;
    public string Body { get; set; } = """{"items":[{"eventId":"synthetic-event"}],"limit":2,"returned":1,"nextCursor":null,"quality":"derived","limitations":["synthetic-fixture"],"coverage":{"status":"degraded"}}""";
    public string ContentType { get; set; } = "application/json";
    public string? Location { get; set; }

    public FakeApi()
    {
        listener.Start();
        Origin = $"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}";
        accept = Task.Run(async () =>
        {
            try
            {
                while (!stop.IsCancellationRequested)
                {
                    var client = await listener.AcceptTcpClientAsync(stop.Token);
                    clients.Add(Handle(client));
                }
            }
            catch (OperationCanceledException) { }
        });
    }

    private async Task Handle(TcpClient client)
    {
        using (client)
        {
            try
            {
                var stream = client.GetStream();
                using var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, true);
                var first = await reader.ReadLineAsync(stop.Token);
                var token = "";
                while (await reader.ReadLineAsync(stop.Token) is { Length: > 0 } line)
                    if (line.StartsWith("X-SysWitness-Token:", StringComparison.OrdinalIgnoreCase))
                        token = line["X-SysWitness-Token:".Length..].Trim();
                var target = first?.Split(' ')[1] ?? "";
                Requests.Enqueue((target, token));
                if (target.Contains("channel=wait", StringComparison.Ordinal))
                {
                    await Task.Delay(800, stop.Token);
                }
                var bytes = Encoding.UTF8.GetBytes(Body);
                var headers = $"HTTP/1.1 {Status} Fixture\r\nContent-Type: {ContentType}\r\nContent-Length: {bytes.Length}\r\nConnection: close\r\n";
                if (Location is not null) headers += $"Location: {Location}\r\n";
                await stream.WriteAsync(Encoding.ASCII.GetBytes(headers + "\r\n"), stop.Token);
                await stream.WriteAsync(bytes, stop.Token);
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or SocketException) { }
        }
    }

    public async ValueTask DisposeAsync()
    {
        stop.Cancel();
        listener.Stop();
        await accept;
        await Task.WhenAll(clients);
        stop.Dispose();
    }
}
