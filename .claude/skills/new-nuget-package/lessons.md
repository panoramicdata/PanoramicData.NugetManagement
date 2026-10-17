# Lessons every new client must apply

Each item cost real debugging time in an earlier package (mostly Splunk.Api, 2026-10-08). Build them in from the start.

## Refit

- **`Buffered = true`** in RefitSettings. Otherwise `[Body]` content is a read-once push stream, so POSTs can never be retried on 429/503 or re-sent after re-login.
- **Even buffered, a JSON `[Body]` arrives as `System.Net.Http.Json.JsonContent`,** not `ByteArrayContent`. A retry handler's "is this replayable?" check must accept `JsonContent` (it re-serialises on each send), or every JSON POST and PUT is sent exactly once (Rapid7.Api, 2026-10-09).
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
- The estate `coverage.config` excludes `CompilerGeneratedAttribute`, which also hides async state-machine bodies from the figure. Check once with that exclusion removed; the reference runs stayed at 100% of lines, with only compiler-generated iterator branches missing.
- Parallel agents share the session scratchpad: brief each to use its own subfolder, or their scripts overwrite each other.
- **Prove a regression test is red without its fix** (revert the fix, run `dotnet format`, build, run) before trusting it. A test that passes both ways proves nothing; drop the speculative fix.
- **`Publish.ps1` printed "Tag x pushed" even when `git push` failed** (fixed in f2c4812, 2026-10-12: it now retries and aborts, and CI-09 enforces the standard script; still confirm the tag landed); the DNS flake here (`getaddrinfo() thread failed to start`) made that happen on Rapid7.Api 3.0.34. After it runs, confirm with `git ls-remote --tags origin <version>`, re-push the tag with retries if missing, then watch the tag's CI run and the nuget flatcontainer.
- `gh` REST calls give spurious rate limits on this machine. Use `gh api graphql` for check-run status, and `gh auth token` (not `gh auth status`) for login checks.
- Read-only guards must allow the vendor's read-only POSTs: login, creating searches, and post-process reads.

## Bringing an existing package up to standard, and releasing it (Harvest.Api, 2026-10-15)

- **The package's major version is the vendor API's major version, even for a breaking refactor.** Harvest.Api went 2.4.7-beta to 2.5, not 3.0: breaking changes ship in minor releases. The next version must exceed the highest on nuget.org (check `https://api.nuget.org/v3-flatcontainer/<id>/index.json`).
- **Confirm Trusted Publishing is configured before running `Publish.ps1`.** It pushes the tag first; a missing policy then fails the publish job at "Login to NuGet" (HTTP 401, "No matching trust policy owned by user ..."). Recover with the policy added and `gh run rerun <id> --repo <repo> --failed`; never re-tag. The policy needs repository owner, repository, workflow file `ci.yml`, no environment, and must be created by the account that owns the package id.
- **Do not launch several `isolation: "worktree"` agents in one message.** Five at once failed with "Could not read the repository git config" and the harness then refused isolation for the rest. Create the worktrees yourself, one at a time (`git worktree add -b <branch> <path> <base>`), and brief each agent with its absolute path and "work only here".
- **Merge parallel resource branches with a per-file strategy, not blanket "keep both".** Every branch appends to `HarvestClient`, `IHarvestClient`, the client test and the README tables. Keeping both sides duplicated `Dispose` and broke the build; rebuild those files from the union of registrations and properties, then regenerate the count assertion from the interface.
- **`dotnet format whitespace <sln>` also rewrites untouched files** (final newlines in old tests). Exclude them or revert the churn before committing.
- **Live tests.**
  - Run them serially (`parallelizeTestCollections: false`): a read test that takes "the first project" otherwise races a CRUD test's cleanup and gets a 404.
  - A helper that swallows 403/422 hides real rejections; keep a strict scenario helper for new tests.
  - A list that returned nothing because of a wrong container key made read tests skip silently for years: once the key is fixed, expect old tests to start failing.
  - Probe the account read-only first (plan, counts, what returns 403) and tell the agents which features are off; tests for those assert the error mapping and are recorded as "unit-verified only".
- **Resolving a vendor credential:** a KeePassXC entry's Username may be the login email, not the account id. List the accounts the token can reach (here `GET id.getharvest.com/api/v2/accounts`) and ask which one is disposable before any write.
- **Browser work:** the shared `browser` skill drives a headed Chrome over CDP and handles no credentials. A public form (a contact form, an integration submission) can be sent with it once the user approves the wording; signing up for an account cannot, because the human has to enter the credentials.
