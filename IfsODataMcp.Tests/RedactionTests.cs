using System.Net;
using System.Text.Json;
using IfsODataMcp.Core;
using IfsODataMcp.Core.Audit;
using IfsODataMcp.Core.Logging;
using IfsODataMcp.Core.Query;
using IfsODataMcp.Core.Security;
using Microsoft.Extensions.Logging;

namespace IfsODataMcp.Tests;

public class RedactionTests
{
    // Not a real token: a made-up JWT-shaped value used only to exercise the patterns.
    private const string FakeJwt = "eyJhbGciOiJub25lIn0.eyJzdWIiOiJ0ZXN0LXVzZXIifQ.c2lnbmF0dXJl";
    private const string FakeToken = "tok_1234567890_abcdef";

    [Theory]
    [InlineData("Authorization: Bearer " + FakeToken)]
    [InlineData("header was 'Bearer " + FakeToken + "' today")]
    [InlineData("jwt " + FakeJwt + " end")]
    [InlineData("https://idp.example.test/cb?code=" + FakeToken + "&state=x")]
    [InlineData("{\"access_token\":\"" + FakeToken + "\",\"expires_in\":3600}")]
    [InlineData("refresh_token=" + FakeToken)]
    [InlineData("client_secret: " + FakeToken)]
    [InlineData("code_verifier=" + FakeToken)]
    public void Common_credential_shapes_are_masked(string text)
    {
        var redacted = new SecretRedactor().Redact(text);

        Assert.DoesNotContain(FakeToken, redacted);
        Assert.DoesNotContain(FakeJwt, redacted);
        Assert.Contains(SecretRedactor.Mask, redacted);
    }

    [Fact]
    public void Registered_secrets_are_masked_wherever_they_appear()
    {
        var redactor = new SecretRedactor();
        redactor.Register("plain-secret-value");

        var redacted = redactor.Redact("oops: plain-secret-value leaked, again plain-secret-value");

        Assert.DoesNotContain("plain-secret-value", redacted);
    }

    [Fact]
    public void Ordinary_text_is_left_alone()
    {
        const string text = "Supplier S-1001 returned error code 404 for the token bucket";

        Assert.Equal(text, new SecretRedactor().Redact(text));
    }

    [Fact]
    public void Logger_output_never_contains_registered_or_pattern_secrets()
    {
        var redactor = new SecretRedactor();
        redactor.Register(FakeToken);
        var output = new StringWriter();
        using var factory = LoggerFactory.Create(b => b.AddProvider(new RedactingLoggerProvider(output, redactor, LogLevel.Trace)));
        var logger = factory.CreateLogger("test");

        logger.LogInformation("sending token {Token}", FakeToken);
        logger.LogError(new InvalidOperationException("failed with Bearer " + FakeJwt), "call failed for {Url}", "https://x.example.test?access_token=" + FakeToken);

        var text = output.ToString();
        Assert.DoesNotContain(FakeToken, text);
        Assert.DoesNotContain(FakeJwt, text);
        Assert.Contains("sending token", text);
    }

    [Fact]
    public void Logger_respects_the_minimum_level()
    {
        var output = new StringWriter();
        using var factory = LoggerFactory.Create(b => b.AddProvider(new RedactingLoggerProvider(output, new SecretRedactor(), LogLevel.Warning)));

        factory.CreateLogger("t").LogInformation("quiet");
        factory.CreateLogger("t").LogWarning("loud");

        Assert.DoesNotContain("quiet", output.ToString());
        Assert.Contains("loud", output.ToString());
    }

    [Fact]
    public async Task Transport_errors_that_echo_a_token_are_redacted_in_the_exception()
    {
        var redactor = new SecretRedactor();
        redactor.Register(FakeToken);
        var handler = new StubHandler(_ => throw new HttpRequestException($"connection reset while sending Bearer {FakeToken}"));
        var gateway = TestSupport.Gateway(handler, redactor: redactor);

        var ex = await Assert.ThrowsAsync<UpstreamException>(() =>
            gateway.QueryAsync("ProcurementDemo", "Suppliers", new ODataQueryRequest(), CancellationToken.None));

        Assert.DoesNotContain(FakeToken, ex.Message);
        Assert.DoesNotContain(FakeToken, ex.ToString());
    }

    [Fact]
    public async Task Upstream_error_bodies_that_echo_a_token_are_redacted()
    {
        var redactor = new SecretRedactor();
        redactor.Register(FakeToken);
        var handler = StubHandler.Xml(IfsODataMcp.Tests.TestSupportXml.Procurement, _ =>
            new HttpResponseMessage(HttpStatusCode.Unauthorized)
            {
                Content = new StringContent("{\"error\":{\"message\":\"token " + FakeToken + " was rejected\"}}"),
            });
        var gateway = TestSupport.Gateway(handler, redactor: redactor);

        var ex = await Assert.ThrowsAsync<UpstreamException>(() =>
            gateway.QueryAsync("ProcurementDemo", "Suppliers", new ODataQueryRequest(), CancellationToken.None));

        Assert.Equal(401, ex.StatusCode);
        Assert.DoesNotContain(FakeToken, ex.Message);
    }

    [Fact]
    public async Task Audit_entries_contain_names_and_counts_but_never_values()
    {
        var audit = new MemoryAuditLog();
        var handler = StubHandler.Xml(TestSupportXml.Procurement, _ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"value":[{"SupplierId":"S-1","Name":"Very Secret Supplier Name"}]}"""),
        });
        var gateway = TestSupport.Gateway(handler, audit: audit);

        await gateway.QueryAsync("ProcurementDemo", "Suppliers",
            new ODataQueryRequest(Select: ["Name"], Filter: "Name eq 'Very Secret Supplier Name'", Top: 5), CancellationToken.None);

        var entry = Assert.Single(audit.Entries);
        var json = JsonSerializer.Serialize(entry);
        Assert.DoesNotContain("Very Secret", json);
        Assert.Equal(["select", "filter", "top"], entry.ParameterNames);
        Assert.Equal(1, entry.RowCount);
        Assert.Equal("ok", entry.Outcome);
        Assert.Equal("Suppliers", entry.EntitySet);
    }

    [Fact]
    public async Task Rejected_requests_are_audited_with_a_category_only()
    {
        var audit = new MemoryAuditLog();
        var gateway = TestSupport.Gateway(StubHandler.Json("{}"), audit: audit);

        await Assert.ThrowsAsync<PolicyViolationException>(() =>
            gateway.QueryAsync("StaffDirectoryDemo", "Employees", new ODataQueryRequest(Filter: "NationalId eq '123'"), CancellationToken.None));

        var entry = Assert.Single(audit.Entries);
        Assert.Equal("policy_violation", entry.Outcome);
        Assert.DoesNotContain("123", JsonSerializer.Serialize(entry));
    }

    [Fact]
    public void File_audit_log_writes_json_lines()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ifs-audit-{Guid.NewGuid():N}.jsonl");
        try
        {
            var log = new FileAuditLog(path);
            log.Write(new AuditEntry(DateTimeOffset.UtcNow, "query_entity_set", "P", "S", ["top"], 3, 12, "ok"));
            log.Write(new AuditEntry(DateTimeOffset.UtcNow, "get_entity", "P", "S", ["keys"], 1, 5, "ok"));

            var lines = File.ReadAllLines(path);
            Assert.Equal(2, lines.Length);
            using var doc = JsonDocument.Parse(lines[0]);
            Assert.Equal("query_entity_set", doc.RootElement.GetProperty("tool").GetString());
            Assert.Equal(3, doc.RootElement.GetProperty("rowCount").GetInt32());
        }
        finally
        {
            File.Delete(path);
        }
    }
}

internal static class TestSupportXml
{
    public static string Procurement => MockODataService.MockData.ProcurementMetadata;
}
