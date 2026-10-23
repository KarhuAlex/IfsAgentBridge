# IfsAgentBridge

A read-only [MCP](https://modelcontextprotocol.io) server that lets an AI agent query an IFS Cloud OData API.

Early development. It has only been run against the bundled mock service, not against a real IFS system.

The server sends GET requests only, and only to the projections you list. You can hide properties, and it caps rows, response size and request time.

## Tools

- `list_projections`: the projections you allowed.
- `describe_entity_set`: keys, properties and navigation properties, read from `$metadata`.
- `query_entity_set`: `select`, `filter`, `orderby`, `top` and `expand`, checked against `$metadata` and your policy before anything is sent.
- `get_entity`: one entity by key.
- `build_odata_url`: validates a request and returns the URL without fetching data.

## Try it with the mock service

You need the .NET 10 SDK.

```bash
dotnet run --project MockODataService --urls http://127.0.0.1:5100
dotnet publish IfsODataMcp.Server -c Release -o publish
claude mcp add ifs-agent-bridge -- dotnet /abs/path/publish/IfsODataMcp.Server.dll --config /abs/path/config/mock.json
```

Claude Desktop takes the same command and arguments in `claude_desktop_config.json`.

Settings live in a JSON file. [`config/mock.json`](config/mock.json) shows the allowed projections, denied properties and limits. [`config/ifs.example.json`](config/ifs.example.json) is a template for a real system. Any setting can also be set with an `IFSBRIDGE_` environment variable, for example `IFSBRIDGE_Policy__MaxTop=100`.

## Security

- Only GET is sent. A guard rejects any other method, any other host and plain http to remote hosts.
- Nothing is reachable until you list it. Denied properties cannot be selected, filtered on, sorted by or returned.
- Tokens stay in memory and are removed from logs and error messages.
- The audit log records tool, entity set, parameter names, row count and duration. It never records values.

Data from the ERP is untrusted. A free-text field can contain instructions aimed at the agent, and the bridge cannot stop a model from following them. Keep the allow-list narrow, use a read-only account, and do not give the same agent a tool that can send data out.

## Not verified against IFS

The URL pattern and sign-in settings in `config/ifs.example.json` are assumptions. Check these against your system:

- the service root URL of a projection and the `$metadata` path
- the OIDC authority, a public client with PKCE, its scopes and a loopback redirect such as `http://127.0.0.1:7890/callback`
- that the access token is accepted as a bearer token by the projection endpoints
- key formats for composite and non-string keys

## Limitations

- `$filter` is checked with a tokenizer, not a full OData parser. `any` and `all` are not supported.
- No paging. `hasMore` tells the agent when there is more.
- stdio only.

## Roadmap

- HTTP transport
- Client credentials sign-in
- More tools, such as counts and paging

## License

[MIT](LICENSE)
