using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace IfsODataMcp.Tests;

public sealed class StdioEndToEndTests : IAsyncLifetime
{
    private Process? _mock;
    private string _workDir = "";
    private string _auditPath = "";
    private string _baseUrl = "";

    public async Task InitializeAsync()
    {
        _workDir = Path.Combine(Path.GetTempPath(), "ifs-bridge-e2e-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_workDir);
        _auditPath = Path.Combine(_workDir, "audit.jsonl");

        var port = FreePort();
        _baseUrl = $"http://127.0.0.1:{port}";
        _mock = Process.Start(new ProcessStartInfo("dotnet", $"\"{Path.Combine(AppContext.BaseDirectory, "MockODataService.dll")}\" --urls {_baseUrl}")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = AppContext.BaseDirectory,
        })!;
        _mock.OutputDataReceived += (_, _) => { };
        _mock.ErrorDataReceived += (_, _) => { };
        _mock.BeginOutputReadLine();
        _mock.BeginErrorReadLine();

        using var http = new HttpClient();
        for (var attempt = 0; attempt < 100; attempt++)
        {
            try
            {
                using var response = await http.GetAsync(_baseUrl + "/");
                if (response.IsSuccessStatusCode) return;
            }
            catch (HttpRequestException)
            {
                await Task.Delay(100);
            }
        }

        throw new InvalidOperationException("The mock service did not start.");
    }

    public Task DisposeAsync()
    {
        try { _mock?.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
        _mock?.Dispose();
        try { Directory.Delete(_workDir, recursive: true); } catch (IOException) { }
        return Task.CompletedTask;
    }

    [Fact]
    public async Task Server_lists_and_answers_all_five_tools_over_stdio()
    {
        var configPath = Path.Combine(_workDir, "config.json");
        await File.WriteAllTextAsync(configPath, JsonSerializer.Serialize(new
        {
            Endpoint = new { BaseUrl = _baseUrl, ServiceRootTemplate = "{baseUrl}/odata/{projection}/" },
            Policy = new
            {
                Projections = new[] { new { Name = "ProcurementDemo" } },
                DeniedProperties = new[] { "ContactEmail", "ContactPhone" },
            },
            Audit = new { Path = _auditPath },
            Auth = new { Mode = "None" },
        }));

        var serverDll = Path.Combine(AppContext.BaseDirectory, "IfsODataMcp.Server.dll");
        await using var client = await McpClient.CreateAsync(new StdioClientTransport(new StdioClientTransportOptions
        {
            Name = "ifs-agent-bridge-e2e",
            Command = "dotnet",
            Arguments = [serverDll, "--config", configPath],
            WorkingDirectory = _workDir,
        }));

        var tools = await client.ListToolsAsync();
        Assert.Equal(
            ["build_odata_url", "describe_entity_set", "get_entity", "list_projections", "query_entity_set"],
            tools.Select(t => t.Name).Order().ToArray());

        var projections = Text(await client.CallToolAsync("list_projections", new Dictionary<string, object?>()));
        Assert.Contains("ProcurementDemo", projections);
        Assert.DoesNotContain("StaffDirectoryDemo", projections);

        var described = Text(await client.CallToolAsync("describe_entity_set",
            new Dictionary<string, object?> { ["projection"] = "ProcurementDemo", ["entitySet"] = "Suppliers" }));
        Assert.Contains("SupplierId", described);
        Assert.DoesNotContain("ContactEmail", described);

        var queried = await client.CallToolAsync("query_entity_set", new Dictionary<string, object?>
        {
            ["projection"] = "ProcurementDemo",
            ["entitySet"] = "Suppliers",
            ["select"] = "SupplierId,Name,ContactEmail",
            ["filter"] = "Country eq 'SE'",
            ["top"] = 3,
        });
        var queriedText = Text(queried);
        Assert.NotEqual(true, queried.IsError);
        Assert.Contains("untrusted", queriedText);
        Assert.DoesNotContain("\"ContactEmail\"", queriedText, StringComparison.Ordinal);
        Assert.Contains("policy denies", queriedText);

        var entity = Text(await client.CallToolAsync("get_entity", new Dictionary<string, object?>
        {
            ["projection"] = "ProcurementDemo",
            ["entitySet"] = "OrderLines",
            ["keys"] = new Dictionary<string, object?> { ["OrderNo"] = "PO-5001", ["LineNo"] = 1 },
        }));
        Assert.Contains("PO-5001", entity);

        var url = Text(await client.CallToolAsync("build_odata_url", new Dictionary<string, object?>
        {
            ["projection"] = "ProcurementDemo",
            ["entitySet"] = "PurchaseOrders",
            ["filter"] = "Status eq 'Closed'",
            ["top"] = 5,
        }));
        Assert.Contains("PurchaseOrders?", url);
        Assert.Contains("$top=5", url);

        var rejected = await client.CallToolAsync("query_entity_set", new Dictionary<string, object?>
        {
            ["projection"] = "StaffDirectoryDemo",
            ["entitySet"] = "Employees",
        });
        Assert.True(rejected.IsError);
        Assert.Contains("policy_violation", Text(rejected));

        // The audit log recorded every call without data values.
        var audit = await File.ReadAllTextAsync(_auditPath);
        Assert.Contains("query_entity_set", audit);
        Assert.DoesNotContain("Kestrel", audit);
        Assert.DoesNotContain("SE'", audit);
    }

    [Fact]
    public async Task Server_refuses_to_start_with_an_incomplete_configuration_and_says_why()
    {
        var psi = new ProcessStartInfo("dotnet", $"\"{Path.Combine(AppContext.BaseDirectory, "IfsODataMcp.Server.dll")}\"")
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            WorkingDirectory = _workDir,
        };
        psi.Environment["IFSBRIDGE_Endpoint__BaseUrl"] = "";

        using var process = Process.Start(psi)!;
        var error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        Assert.Equal(2, process.ExitCode);
        Assert.Contains("Endpoint:BaseUrl", error);
    }

    private static string Text(CallToolResult result) =>
        string.Concat(result.Content.OfType<TextContentBlock>().Select(b => b.Text));

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
