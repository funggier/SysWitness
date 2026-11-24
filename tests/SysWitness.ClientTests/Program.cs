using System.Text.Json;

var tests = new (string Name, Func<Task> Run)[]
{
    ("initialize_and_fixed_read_only_catalog", Catalog),
    ("named_get_preserves_evidence_and_encodes_filters", NamedGet),
    ("all_named_routes_are_fixed", Routes),
    ("invalid_arguments_never_reach_backend", Validation),
    ("http_denial_and_failure_remain_errors", Failures),
    ("redirect_is_not_followed", Redirect),
    ("response_bounds_and_json_fail_closed", ResponseBounds),
    ("credential_echo_is_redacted", Redaction),
    ("configuration_rejects_nonloopback", BadConfiguration),
    ("framing_and_request_errors_are_recoverable", Framing),
    ("cancellation_leaves_reader_responsive", Cancellation),
    ("new_projection_routes_are_named_reads", Projections),
    ("duplicate_backend_json_fails_closed", DuplicateBackendJson),
    ("rapid_invocations_are_rate_limited", RateLimit),
    ("stdin_eof_cancels_pending_reads", Eof)
};
var failures = 0;
foreach (var test in tests.Where(t => args.Length == 0 || t.Name.Contains(args[0], StringComparison.Ordinal)))
{
    try { await test.Run(); Console.WriteLine($"PASS {test.Name}"); }
    catch (Exception ex) { failures++; Console.WriteLine($"FAIL {test.Name}: {ex.GetType().Name}: {ex.Message}"); }
}
Console.WriteLine($"Client tests: {tests.Length} available; {failures} failed.");
return failures == 0 ? 0 : 1;

static void Check(bool condition, string message) => Fixture.Check(condition, message);
static JsonElement Result(JsonElement response)
{
    Check(response.TryGetProperty("result", out var result), "Expected a tool result; received a protocol error.");
    return result;
}
static bool IsToolError(JsonElement response) => Result(response).TryGetProperty("isError", out var error) && error.GetBoolean();
static JsonElement Data(JsonElement response)
{
    var result = Result(response);
    using var document = JsonDocument.Parse(result.GetProperty("content")[0].GetProperty("text").GetString()!);
    return document.RootElement.Clone();
}
static async Task Catalog()
{
    await using var api = new FakeApi();
    await using var peer = new McpPeer(api.Origin);
    await peer.Initialize();
    await peer.Send("""{"jsonrpc":"2.0","id":1,"method":"tools/list"}""");
    var response = await peer.Require();
    var tools = Result(response).GetProperty("tools").EnumerateArray().ToArray();
    var expected = new[] { "syswitness_get_health", "syswitness_get_events", "syswitness_get_event",
        "syswitness_get_coverage", "syswitness_get_gaps", "syswitness_get_storage_status",
        "syswitness_get_retention", "syswitness_get_process_events", "syswitness_get_process_timeline",
        "syswitness_get_process_activity", "syswitness_get_process_tree",
        "syswitness_get_connections", "syswitness_get_dns", "syswitness_get_network_inventory",
        "syswitness_get_file_activity", "syswitness_get_registry_activity", "syswitness_get_authentication_activity",
        "syswitness_get_service_activity", "syswitness_get_driver_activity", "syswitness_get_task_activity",
        "syswitness_get_device_activity", "syswitness_get_module_activity", "syswitness_get_process_access_activity",
        "syswitness_get_storage_projections" };
    Check(tools.Select(t => t.GetProperty("name").GetString()).Order().SequenceEqual(expected.Order()),
        "Catalog differs from the explicit named read allowlist.");
    foreach (var tool in tools)
    {
        Check(tool.GetProperty("annotations").GetProperty("readOnlyHint").GetBoolean(), "Tool must be read-only.");
        Check(!tool.GetProperty("inputSchema").GetProperty("additionalProperties").GetBoolean(), "Schema must reject unknown fields.");
    }
    Check(api.Requests.IsEmpty, "Discovery must not query private telemetry.");
}

static async Task NamedGet()
{
    await using var api = new FakeApi();
    await using var peer = new McpPeer(api.Origin);
    await peer.Initialize();
    var r = await peer.Call("syswitness_get_events", """{"limit":2,"channel":"A&B","fromUtc":"2026-11-20T11:00:00Z"}""");
    Check(!IsToolError(r), "Named GET should succeed.");
    var data = Data(r);
    Check(data.GetProperty("items")[0].GetProperty("eventId").GetString() == "synthetic-event", "Evidence was lost.");
    Check(data.GetProperty("coverage").GetProperty("status").GetString() == "degraded", "Coverage was changed.");
    Check(data.GetProperty("limitations")[0].GetString() == "synthetic-fixture", "Limitations were lost.");
    Check(api.Requests.TryDequeue(out var request) &&
        request.Target == "/api/v1/events?limit=2&channel=A%26B&fromUtc=2026-11-20T11%3A00%3A00.0000000%2B00%3A00" &&
        request.Token == Fixture.Token, "GET path, query escaping or credential header is wrong.");
}

static async Task Routes()
{
    await using var api = new FakeApi();
    await using var peer = new McpPeer(api.Origin);
    await peer.Initialize();
    var id = 10;
    var cases = new (string Tool, string Args, string Target)[]
    {
        ("syswitness_get_health", "{}", "/api/v1/health"),
        ("syswitness_get_event", """{"eventId":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"}""", "/api/v1/events/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"),
        ("syswitness_get_coverage", """{"channel":"System"}""", "/api/v1/monitoring/coverage?channel=System"),
        ("syswitness_get_gaps", """{"channel":"System","limit":2}""", "/api/v1/monitoring/gaps?channel=System&limit=2"),
        ("syswitness_get_storage_status", "{}", "/api/v1/storage/status"),
        ("syswitness_get_retention", """{"limit":2}""", "/api/v1/storage/retention?limit=2"),
        ("syswitness_get_process_events", """{"limit":2}""", "/api/v1/processes/events?limit=2"),
        ("syswitness_get_process_timeline", $$"""{"processGuid":"{{Fixture.GuidText}}","limit":2}""", $"/api/v1/processes/{Fixture.GuidText}/timeline?limit=2"),
        ("syswitness_get_process_activity", $$"""{"processGuid":"{{Fixture.GuidText}}","computer":"Fixture-PC","limit":2}""", $"/api/v1/processes/{Fixture.GuidText}/activity?limit=2&computer=Fixture-PC"),
        ("syswitness_get_process_tree", $$"""{"processGuid":"{{Fixture.GuidText}}","computer":"Fixture-PC","depth":0,"limit":2}""", $"/api/v1/processes/{Fixture.GuidText}/tree?computer=Fixture-PC&depth=0&limit=2"),
        ("syswitness_get_connections", $$"""{"processGuid":"{{Fixture.GuidText}}","limit":2}""", $"/api/v1/network/connections?limit=2&processGuid={Fixture.GuidText}"),
        ("syswitness_get_dns", """{"limit":2}""", "/api/v1/network/dns?limit=2")
    };
    foreach (var c in cases)
    {
        Check(!IsToolError(await peer.Call(c.Tool, c.Args, id++)), $"Named route failed: {c.Tool}");
        Check(api.Requests.TryDequeue(out var request) && request.Target == c.Target,
            $"Unexpected backend route for {c.Tool}.");
    }
}

static async Task Validation()
{
    await using var api = new FakeApi();
    await using var peer = new McpPeer(api.Origin);
    await peer.Initialize();
    var id = 20;
    var cases = new (string Tool, string Args)[]
    {
        ("syswitness_get_events", """{"url":"http://example.invalid"}"""),
        ("syswitness_get_events", """{"limit":0}"""),
        ("syswitness_get_events", """{"limit":501}"""),
        ("syswitness_get_events", """{"limit":2.5}"""),
        ("syswitness_get_events", """{"limit":"2"}"""),
        ("syswitness_get_events", """{"sourceEventId":65536}"""),
        ("syswitness_get_events", """{"fromUtc":"2026-11-20T11:00:00"}"""),
        ("syswitness_get_events", """{"fromUtc":"2026-11-20T12:00:00Z","toUtc":"2026-11-20T11:00:00Z"}"""),
        ("syswitness_get_events", """{"channel":"bad\u0000channel"}"""),
        ("syswitness_get_process_events", """{"category":"process"}"""),
        ("syswitness_get_process_timeline", """{"processGuid":"../events"}"""),
        ("syswitness_get_process_activity", $$"""{"processGuid":"{{Fixture.GuidText}}"}"""),
        ("syswitness_get_process_tree", $$"""{"processGuid":"{{Fixture.GuidText}}","computer":"Fixture-PC","depth":9}"""),
        ("syswitness_get_process_tree", $$"""{"processGuid":"{{Fixture.GuidText}}","computer":"Fixture-PC","limit":257}"""),
        ("syswitness_get_process_tree", $$"""{"processGuid":"{{Fixture.GuidText}}","computer":"Fixture-PC","cursor":"a"}"""),
        ("syswitness_get_event", """{"eventId":"../../health"}"""),
        ("syswitness_get_health", """{"token":"arbitrary"}""")
    };
    foreach (var c in cases)
        Check(IsToolError(await peer.Call(c.Tool, c.Args, id++)), $"Invalid arguments accepted for {c.Tool}.");
    Check(api.Requests.IsEmpty, "Rejected input reached backend.");
    var unknown = await peer.Call("host_control", "{}", id++);
    Check(unknown.GetProperty("error").GetProperty("code").GetInt32() == -32602, "Unknown tool must be a protocol error.");
    await peer.Send("{\"jsonrpc\":\"2.0\",\"id\":" + id + ",\"method\":\"tools/call\",\"params\":{\"name\":\"syswitness_get_events\",\"arguments\":[]}}");
    Check((await peer.Require()).GetProperty("error").GetProperty("code").GetInt32() == -32602, "Nonobject arguments must fail.");
}

static async Task Failures()
{
    await using var api = new FakeApi();
    await using var peer = new McpPeer(api.Origin);
    await peer.Initialize();
    var id = 50;
    foreach (var status in new[] { 401, 403, 429, 503 })
    {
        api.Status = status;
        api.Body = $$"""{"error":"fixture_denied","message":"{{Fixture.Token}}"}""";
        var response = await peer.Call("syswitness_get_events", "{}", id++);
        Check(IsToolError(response), $"HTTP {status} was represented as success.");
        Check(Data(response).GetProperty("httpStatus").GetInt32() == status, "Backend HTTP status was lost.");
        Check(!response.GetRawText().Contains(Fixture.Token, StringComparison.Ordinal), "Backend error body leaked token.");
    }
}

static async Task Redirect()
{
    await using var destination = new FakeApi();
    await using var api = new FakeApi { Status = 302, Location = destination.Origin + "/capture" };
    await using var peer = new McpPeer(api.Origin);
    await peer.Initialize();
    Check(IsToolError(await peer.Call("syswitness_get_events", "{}")), "Redirect should fail closed.");
    Check(destination.Requests.IsEmpty, "Client followed a redirect with credentials.");
}

static async Task ResponseBounds()
{
    await using var api = new FakeApi { Body = new string('x', 1_048_577) };
    await using var peer = new McpPeer(api.Origin);
    await peer.Initialize();
    Check(IsToolError(await peer.Call("syswitness_get_events", "{}", 2)), "Oversized body should fail.");
    api.Body = "{";
    Check(IsToolError(await peer.Call("syswitness_get_events", "{}", 3)), "Invalid JSON should fail.");
    api.Body = "{}"; api.ContentType = "text/html";
    Check(IsToolError(await peer.Call("syswitness_get_events", "{}", 4)), "Non-JSON body should fail.");
    api.ContentType = "application/json";
    api.Body = new string('[', 40) + "0" + new string(']', 40);
    Check(IsToolError(await peer.Call("syswitness_get_events", "{}", 5)), "Excessive response depth should fail.");
}

static async Task Redaction()
{
    await using var api = new FakeApi { Body = $$"""{"commandLine":"fixture {{Fixture.Token}}","token":"{{Fixture.Token}}"}""" };
    await using var peer = new McpPeer(api.Origin);
    await peer.Initialize();
    var r = await peer.Call("syswitness_get_events", "{}");
    Check(!IsToolError(r), "Safe redacted evidence should remain readable.");
    Check(!r.GetRawText().Contains(Fixture.Token, StringComparison.Ordinal), "Configured credential echoed in results.");
    Check(Data(r).GetProperty("commandLine").GetString()!.Contains("[REDACTED]", StringComparison.Ordinal), "Credential redaction was not explicit.");
}

static async Task BadConfiguration()
{
    await using var peer = new McpPeer("http://example.invalid:8769");
    await peer.ExpectExit(1);
    Check(await peer.Read(2000) is null, "Invalid configuration should not start serving.");
}

static async Task Framing()
{
    await using var api = new FakeApi();
    await using var peer = new McpPeer(api.Origin);
    await peer.Send("""{"jsonrpc":"2.0","id":0,"method":"tools/list"}""");
    Check((await peer.Require()).TryGetProperty("error", out _), "Tools should require initialized lifecycle.");
    await peer.Initialize();
    await peer.Send("{");
    Check((await peer.Require()).GetProperty("error").GetProperty("code").GetInt32() == -32700, "Invalid JSON should be parse error.");
    await peer.Send(new string('x', 65_537));
    Check((await peer.Require()).GetProperty("error").GetProperty("code").GetInt32() == -32600, "Oversized line should fail with bounded error.");
    await peer.Send("""{"jsonrpc":"2.0","id":90,"method":"tools/call","params":{"name":"syswitness_get_events","arguments":{"limit":1,"limit":2}}}""");
    Check((await peer.Require()).TryGetProperty("error", out _), "Duplicate properties should be rejected.");
    await peer.Send("""{"jsonrpc":"2.0","method":"unknown/notification"}""");
    await peer.Send("""{"jsonrpc":"2.0","id":91,"method":"ping"}""");
    Check((await peer.Require()).GetProperty("id").GetInt32() == 91, "Notifications received an illegal response or input did not recover.");
    await peer.Send("""{"jsonrpc":"2.0","id":91,"method":"ping"}""");
    Check((await peer.Require()).TryGetProperty("error", out _), "Reused request ID should fail.");
}

static async Task Cancellation()
{
    await using var api = new FakeApi();
    await using var peer = new McpPeer(api.Origin);
    await peer.Initialize();
    await peer.Send("""{"jsonrpc":"2.0","id":101,"method":"tools/call","params":{"name":"syswitness_get_events","arguments":{"channel":"wait"}}}""");
    await Task.Delay(100);
    await peer.Send("""{"jsonrpc":"2.0","method":"notifications/cancelled","params":{"requestId":101}}""");
    await peer.Send("""{"jsonrpc":"2.0","id":102,"method":"ping"}""");
    Check((await peer.Require(600)).GetProperty("id").GetInt32() == 102, "Reader blocked behind backend operation.");
    Check(await peer.Read(1000) is null, "Cancelled request produced a late response.");
    Check(!IsToolError(await peer.Call("syswitness_get_events", "{}", 103)), "Cancellation failed to free request capacity.");
}

static async Task Projections()
{
    await using var api = new FakeApi();
    await using var peer = new McpPeer(api.Origin);
    await peer.Initialize();
    var cases = new (string Tool, string Target)[]
    {
        ("syswitness_get_network_inventory", "/api/v1/network/inventory?limit=2"),
        ("syswitness_get_file_activity", "/api/v1/activity/files?limit=2"),
        ("syswitness_get_registry_activity", "/api/v1/activity/registry?limit=2"),
        ("syswitness_get_authentication_activity", "/api/v1/activity/authentication?limit=2"),
        ("syswitness_get_service_activity", "/api/v1/activity/services?limit=2"),
        ("syswitness_get_driver_activity", "/api/v1/activity/drivers?limit=2"),
        ("syswitness_get_task_activity", "/api/v1/activity/tasks?limit=2"),
        ("syswitness_get_device_activity", "/api/v1/activity/devices?limit=2"),
        ("syswitness_get_module_activity", "/api/v1/activity/modules?limit=2"),
        ("syswitness_get_process_access_activity", "/api/v1/activity/process-access?limit=2"),
        ("syswitness_get_storage_projections", "/api/v1/storage/projections")
    };
    var id = 200;
    foreach (var c in cases)
    {
        Check(!IsToolError(await peer.Call(c.Tool, c.Tool.EndsWith("storage_projections", StringComparison.Ordinal) ? "{}" : """{"limit":2}""", id++)),
            $"Projection read failed: {c.Tool}");
        Check(api.Requests.TryDequeue(out var request) && request.Target == c.Target, "Wrong projection route.");
    }
    Check(IsToolError(await peer.Call("syswitness_get_file_activity", """{"kind":"arbitrary","category":"files"}""", id++)),
        "Caller-selected projection paths must be rejected.");
}

static async Task DuplicateBackendJson()
{
    await using var api = new FakeApi { Body = """{"items":[],"items":[{"eventId":"shadow"}]}""" };
    await using var peer = new McpPeer(api.Origin);
    await peer.Initialize();
    Check(IsToolError(await peer.Call("syswitness_get_events", "{}")), "Ambiguous duplicate backend JSON should fail closed.");
}

static async Task RateLimit()
{
    await using var api = new FakeApi();
    await using var peer = new McpPeer(api.Origin);
    await peer.Initialize();
    for (var id = 1000; id < 1064; id++)
        await peer.Send("{\"jsonrpc\":\"2.0\",\"id\":" + id + ",\"method\":\"tools/call\",\"params\":{\"name\":\"syswitness_get_events\",\"arguments\":{\"channel\":\"wait\"}}}");
    var rateLimited = 0;
    for (var i = 0; i < 64; i++)
    {
        var response = await peer.Require();
        if (IsToolError(response) && Data(response).GetProperty("errorCode").GetString() == "rate_limited") rateLimited++;
    }
    Check(rateLimited > 0, "Rapid valid invocations should have a bounded rate.");
    Check(api.Requests.Count <= 4, "Burst should not create unbounded backend requests.");
}

static async Task Eof()
{
    await using var api = new FakeApi();
    await using var peer = new McpPeer(api.Origin);
    await peer.Initialize();
    await peer.Send("""{"jsonrpc":"2.0","id":2000,"method":"tools/call","params":{"name":"syswitness_get_events","arguments":{"channel":"wait"}}}""");
    await Task.Delay(50);
    peer.CloseInput();
    await peer.ExpectExit(0);
    Check(await peer.Read(100) is null, "EOF should cancel outstanding reads without late output.");
}
