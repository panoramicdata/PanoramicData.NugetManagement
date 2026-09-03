Updated [coverlet.collector](https://github.com/coverlet-coverage/coverlet) from 8.0.1 to 10.0.0.

<details>
<summary>Release notes</summary>

_Sourced from [coverlet.collector's releases](https://github.com/coverlet-coverage/coverlet/releases)._

## 10.0.0

## Improvements

- Unique Report Filenames (coverlet.MTP and AzDO) [#​1866](https://github.com/coverlet-coverage/coverlet/issues/1866)
- Add `--coverlet-file-prefix` option for unique report files [#​1869](https://github.com/coverlet-coverage/coverlet/pull/1869)
- Introduce .NET 10 support [#​1823](https://github.com/coverlet-coverage/coverlet/pull/1823)

## Fixed

- Fix [BUG] Wrong branch rate on IAsyncEnumerable for generic type [#​1836](https://github.com/coverlet-coverage/coverlet/issues/1836)
- Fix [BUG] Missing Coverage after moving to MTP [#​1843](https://github.com/coverlet-coverage/coverlet/issues/1843)
- Fix [BUG] No coverage reported when targeting .NET Framework with 8.0.1 [#​1842](https://github.com/coverlet-coverage/coverlet/issues/1842)
- Fix [BUG] Behavior changes between MTP and Legacy (msbuild) [#​1878](https://github.com/coverlet-coverage/coverlet/issues/1878)
- Fix [BUG] Coverlet.MTP - Unable to load coverlet.mtp.appsettings.json [#​1880](https://github.com/coverlet-coverage/coverlet/issues/1880)
- Fix [BUG] Coverlet.Collector produces empty report when Mediator.SourceGenerator is referenced [#​1718](https://github.com/coverlet-coverage/coverlet/issues/1718) by <https://github.com/yusyd>
- Fix [BUG] Crash during instrumentation (Methods using LibraryImport/DllImport have no body) [#​1762](https://github.com/coverlet-coverage/coverlet/issues/1762)

## Maintenance

- Add comprehensive async method tests and documentation for issue [#​1864](https://github.com/coverlet-coverage/coverlet/pull/1864)
- Replace Tmds.ExecFunction Package in coverlet.core.coverage.tests [#​1833](https://github.com/coverlet-coverage/coverlet/issues/1833)
- Add net9.0 and net10.0 targets [#​1822](https://github.com/coverlet-coverage/coverlet/issues/1822)

[Diff between 8.0.1 and 10.0.0](https://github.com/coverlet-coverage/coverlet/compare/v8.0.1...v10.0.0)

Commits viewable in [compare view](https://github.com/coverlet-coverage/coverlet/compare/v8.0.1...v10.0.0).
</details>

[![Dependabot compatibility score](https://dependabot-badges.githubapp.com/badges/compatibility_score?dependency-name=coverlet.collector&package-manager=nuget&previous-version=8.0.1&new-version=10.0.0)](https://docs.github.com/en/github/managing-security-vulnerabilities/about-dependabot-security-updates#about-compatibility-scores)

You can trigger a rebase of this PR by commenting `@dependabot rebase`.

[//]: # (dependabot-automerge-start)
[//]: # (dependabot-automerge-end)

---

<details>
<summary>Dependabot commands and options</summary>
<br />

You can trigger Dependabot actions by commenting on this PR:
- `@dependabot rebase` will rebase this PR
- `@dependabot recreate` will recreate this PR, overwriting any edits that have been made to it
- `@dependabot show <dependency name> ignore conditions` will show all of the ignore conditions of the specified dependency
- `@dependabot ignore this major version` will close this PR and stop Dependabot creating any more for this major version (unless you reopen the PR or upgrade to it yourself)
- `@dependabot ignore this minor version` will close this PR and stop Dependabot creating any more for this minor version (unless you reopen the PR or upgrade to it yourself)
- `@dependabot ignore this dependency` will close this PR and stop Dependabot creating any more for this dependency (unless you reopen the PR or upgrade to it yourself)


</details>

> **Note**
> Automatic rebases have been disabled on this pull request as it has been open for over 30 days.

