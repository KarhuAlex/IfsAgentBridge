# Changelog

## Unreleased

- First version: stdio MCP server with `list_projections`, `describe_entity_set`, `query_entity_set`, `get_entity` and `build_odata_url`.
- Allow-lists for projections and entity sets, a property deny-list, and limits for rows, expand depth, response size and request time.
- Sign-in with no authentication, a static bearer token, or OIDC authorization code with PKCE.
- Local audit log without data values.
- Mock OData service and identity provider for tests and demos.
