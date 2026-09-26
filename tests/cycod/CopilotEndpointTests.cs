using System.Net;
using System.Net.Sockets;
using System.Text;

[TestClass]
public class CopilotEndpointTests
{
    [TestMethod]
    public async Task DiscoveryUsesCustomBasePathAndRoutesMetadataAsync()
    {
        RequireAutoMode();
        using var server = new ModelsServer("""
            {"data":[
              {"id":"responses-only","supported_endpoints":["/responses"]},
              {"id":"chat-only","supported_endpoints":["/chat/completions"]},
              {"id":"both","supported_endpoints":["/responses","/chat/completions"]},
              {"id":"legacy"},
              {"id":"empty","supported_endpoints":[]}
            ]}
            """);
        var endpoint = server.Endpoint + "proxy/v1";
        Assert.IsTrue(Route("responses-only", endpoint));
        var request = await server.Request;
        Assert.AreEqual("GET /proxy/v1/models HTTP/1.1", request[0]);
        Assert.IsTrue(request.Any(line => line.Equals("Authorization: Bearer test-token", StringComparison.OrdinalIgnoreCase)));
        Assert.IsTrue(request.Any(line => line.Equals("Editor-Version: test-editor", StringComparison.OrdinalIgnoreCase)));
        Assert.IsFalse(Route("chat-only", endpoint + "/"));
        Assert.IsFalse(Route("both", endpoint));
        Assert.IsFalse(Route("legacy", endpoint));
        Assert.IsFalse(Route("empty", endpoint));
        Assert.IsFalse(Route("unknown", endpoint));
        Assert.IsTrue(Route("RESPONSES-ONLY", endpoint));
    }

    [TestMethod]
    public async Task CacheIsIsolatedByEndpointAsync()
    {
        RequireAutoMode();
        using var responses = new ModelsServer("""{"data":[{"id":"same-model","supported_endpoints":["/responses"]}]}""");
        using var chat = new ModelsServer("""{"data":[{"id":"same-model","supported_endpoints":["/chat/completions"]}]}""");
        Assert.IsTrue(Route("same-model", responses.Endpoint));
        await responses.Request;
        Assert.IsFalse(Route("same-model", chat.Endpoint));
        await chat.Request;
        Assert.IsTrue(Route("same-model", responses.Endpoint));
    }

    [TestMethod]
    public async Task DiscoveryFailureFallsBackWithoutPoisoningOtherEndpointsAsync()
    {
        RequireAutoMode();
        using var unavailable = new ModelsServer("{}", "503 Service Unavailable");
        using var healthy = new ModelsServer("""{"data":[{"id":"model","supported_endpoints":["/responses"]}]}""");
        Assert.IsFalse(Route("model", unavailable.Endpoint));
        await unavailable.Request;
        Assert.IsTrue(Route("model", healthy.Endpoint));
        await healthy.Request;
    }

    [TestMethod]
    public void ForcedModeBypassesDiscovery()
    {
        var mode = EnvironmentHelpers.FindEnvVar("COPILOT_API_MODE");
        if (mode != "chat" && mode != "responses")
        {
            Assert.Inconclusive("Run with COPILOT_API_MODE=chat or responses in the test process environment.");
            return;
        }
        // An invalid URI proves the override bypasses discovery rather than merely fetching successfully.
        Assert.AreEqual(mode == "responses", Route("model", "not an absolute URI"));
    }

    private static bool Route(string model, string endpoint) =>
        CopilotEndpointRouter.ShouldUseResponsesApi(model, "test-token", "test-editor", endpoint);

    private static void RequireAutoMode()
    {
        var mode = EnvironmentHelpers.FindEnvVar("COPILOT_API_MODE");
        if (!string.IsNullOrEmpty(mode) && mode != "auto")
            Assert.Inconclusive("Run with COPILOT_API_MODE=auto in the test process environment.");
    }

    private sealed class ModelsServer : IDisposable
    {
        public string Endpoint { get; }
        public Task<List<string>> Request { get; }
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _timeout = new(TimeSpan.FromSeconds(5));

        public ModelsServer(string body, string status = "200 OK")
        {
            _listener.Start();
            Endpoint = $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/";
            Request = ServeAsync(body, status);
        }

        private async Task<List<string>> ServeAsync(string body, string status)
        {
            using var connection = await _listener.AcceptTcpClientAsync(_timeout.Token);
            using var stream = connection.GetStream();
            using var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, leaveOpen: true);
            var headers = new List<string>();
            while (await reader.ReadLineAsync(_timeout.Token) is { Length: > 0 } line)
                headers.Add(line);
            var payload = Encoding.UTF8.GetBytes(body);
            var response = Encoding.ASCII.GetBytes($"HTTP/1.1 {status}\r\nContent-Type: application/json\r\nContent-Length: {payload.Length}\r\nConnection: close\r\n\r\n");
            await stream.WriteAsync(response, _timeout.Token);
            await stream.WriteAsync(payload, _timeout.Token);
            return headers;
        }

        public void Dispose()
        {
            _timeout.Cancel();
            _listener.Stop();
            _timeout.Dispose();
        }
    }
}
