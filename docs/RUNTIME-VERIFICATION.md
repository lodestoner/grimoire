# Bounded operation verification

The Windows regression runner includes contained-process and cancellation fixtures by default:

```powershell
dotnet restore tests/Grimoire.Tests/Grimoire.Tests.csproj --locked-mode
dotnet build tests/Grimoire.Tests/Grimoire.Tests.csproj -c Release --no-restore
dotnet run --project tests/Grimoire.Tests/Grimoire.Tests.csproj -c Release --no-build -- --report test-results/windows-regression.txt
```

The runner's `--fixture` child mode uses its own self-contained executable and existing runtime files. No extra helper download, account, credential, hook, clipboard access, or public network request is needed. The HTTP fixture binds a dynamically allocated loopback port only.

Coverage includes literal executable arguments and stdin, blocked stdin deadlines, stdout/stderr limits, nonzero exits that also print stdout, explicit cancellation, a rejecting synchronization context, and descendant termination for both a living parent and a parent that exits while its child holds inherited pipes. CLI fixtures check requested Codex/Claude flags, known npm shim resolution without cmd interpolation, and explicit rejection of an unresolved `.cmd` file. A flag check does not prove a selected-input read boundary; Codex and Gemini actions are temporarily disabled pending behavioral verification. Further fixtures check temporary-directory cleanup, message/path-free error diagnostics, loopback API/HTTP spell cancellation, and authored command-spell exit handling.

Captured child stdout and stderr each have a four Mi-character limit. Exceeding it fails the operation rather than returning a shortened result. Process deadlines cover launch, stdin, both output drains, and exit. Cancellation allows up to five additional seconds for termination and reaping; an abnormally stalled native startup cannot hold the caller indefinitely. The supported Windows minimum is Windows 10 build 17763 (1809), matching the existing installer requirement and exceeding the required job-attribute support. A Windows job is attached atomically during process creation and disallows breakaway. See Microsoft's [job assignment explanation](https://devblogs.microsoft.com/oldnewthing/20230209-00/?p=107812) and [handle inheritance guidance](https://learn.microsoft.com/en-us/windows/win32/procthread/creating-processes).

The operation lifecycle is confined to its creating UI thread; cross-thread transitions fail explicitly. Synthetic lifecycle tests cover overlap rejection, cancelled operations followed by fresh work, failed setup cleanup, and quit waiting through cleanup. The busy UI permits one operation at a time, exposes Cancel, checks cancellation before delivering a result, and cancels/waits on quit. Recipes and metric rewrites share one five-minute scope across their passes. File batches check cancellation between files and do not swallow cancellation exceptions. Already completed file/integration side effects are not rolled back.

The public Windows Build workflow runs these synthetic process, cancellation, lifecycle, prompt-composition and image-routing regressions. Its text report records the result for each run; see [public source status](PUBLIC-SOURCE-STATUS.md) and the linked workflow before claiming a check passed. A Linux cross-build checks compilation only and cannot prove UI behavior or authenticated provider integration.

Manual limits: vendor account inference, installed vendor CLI versions, npm Node execution, image permission behavior, interactive Cancel/focus/paste/quit, and external integrations have not been exercised. Unsupported provider CLI options fail without retrying under weaker restrictions. Existing non-provider integration implementations retain their own transport behavior; cancellation still suppresses their result delivery, but cannot undo a completed side effect. Parser and FFmpeg operations can outlast cancellation; see [SECURITY.md](../SECURITY.md).
