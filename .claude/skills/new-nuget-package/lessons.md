# Lessons every new client must apply

Each item cost real debugging time in an earlier package (mostly Splunk.Api, 2026-10-08). Build them in from the start.

## Refit

- **`Buffered = true`** in RefitSettings. Otherwise `[Body]` content is a read-once push stream, so POSTs can never be retried on 429/503 or re-sent after re-login.
- **`TransportExceptionFactory = static (_, e, _) => e`.** Otherwise Refit 16 wraps `TimeoutException`, `HttpRequestException` and `ObjectDisposedException` in `ApiRequestException`, and the exceptions you document never reach callers. (TheHive.Api#22.)
- Interface paths are relative, with no leading `/`, plus `UrlResolutionMode.Rfc3986`, so a path-prefixed base URL works.
- Path parameters are escaped as one segment (`a/b` becomes `a%2Fb`). Never rebuild URLs with `UriBuilder` setters, which double-escape `%`; concatenate strings instead.
- Every method takes a required `CancellationToken` last, and the API has no optional parameters (Sonar S2360). Optional query parameters travel in a nullable options object.

## Transport

- Use **`SocketsHttpHandler`**, not `HttpClientHandler`. Set `PooledConnectionIdleTimeout` below the server's keep-alive timeout (splunkd's busy timeout is 12 s, so use 5 s) and `PooledConnectionLifetime` to 5 minutes. Reused stale sockets fail as "connection forcibly closed" under load.
- `SocketsHttpHandler.SslOptions.RemoteCertificateValidationCallback` receives the **SslStream** as its sender, not the request, so a public validation callback must not take a request. Offer SHA-256 thumbprint pinning, not "ignore certificate errors".
- **Retry connection-establishment failures** (`HttpRequestError.ConnectionError`, `SecureConnectionError` and `NameResolutionError`) for any verb, since nothing was sent. Do not retry failures after sending.
- Validate options before creating the transport, or a rejected configuration leaks a handler.
- Reject credentials, a query or a fragment in the base URL. They leak into logs and exception messages.

## Serialization

- `JsonSerializerOptions` needs an explicit `TypeInfoResolver` before `MakeReadOnly()`.
- Read the vendor's loose types tolerantly: booleans as `1`, `"1"`, `"true"` or `"yes"`; numbers as strings or empty strings; enums falling back to `Unknown = 0`. A JSON `null` must not wipe a collection's `= []` default; use a resolver modifier.
- Never name a namespace segment `System`. It shadows `System.*` everywhere beneath it.

## Tests and tooling

- Bash heredocs eat backslashes. Write C# with the Write or Edit tools.
- `dotnet format whitespace` before building: tool-written files are LF, but CRLF is enforced.
- A file-based harness (`dotnet run X.cs`) disables reflection JSON by default. Add `#:property PublishAot=false` and `#:property JsonSerializerIsReflectionEnabledByDefault=true`, or every Codacy-backed rule silently passes.
- **Prove a regression test is red without its fix** (revert the fix, run `dotnet format`, build, run) before trusting it. A test that passes both ways proves nothing; drop the speculative fix.
- `gh` REST calls give spurious rate limits on this machine. Use `gh api graphql` for check-run status, and `gh auth token` (not `gh auth status`) for login checks.
- Read-only guards must allow the vendor's read-only POSTs: login, creating searches, and post-process reads.
