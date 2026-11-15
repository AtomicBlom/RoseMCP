# The worker compiles with the SDK's own Roslyn

**Decision.** The Roslyn packages the worker compiles against are pinned to the build the newest
supported .NET SDK ships, taken from the .NET team's public `dotnet-tools` feed when nuget.org does
not have it yet. `NuGet.config` maps those Roslyn package ids, and only those, to that feed; everything
else resolves from nuget.org alone.

**Why.** Each SDK makes a new C# version the default for its framework, and a Roslyn that predates
it does not know that version exists. It reads the project as the version before, so every use of a
new feature is reported as an error -- CS9327, "not available in C# 14.0" -- that the project's own
build does not have. That is a confidently wrong answer, not a failure, and it lands on exactly the
code an agent is most likely to be writing. The SDK's compiler reaches `dotnet-tools` when the SDK
ships and nuget.org some weeks later, so waiting for nuget.org means Rose is wrong about every new
project for that long. `DiagnosticsTests.Compiles_the_language_version_the_sdk_defaults_to` fails
on a Roslyn that is too old.

**Why not stay on nuget.org and wait.** Rose would load the solution and report errors in correct
code until the release arrived, and nothing in the result would say why.

**Why not load the SDK's own compiler at run time.** The worker uses Roslyn's workspace and
refactoring APIs, which the SDK does not ship. Pairing the SDK's `Microsoft.CodeAnalysis` with a
packaged `Workspaces` is two versions of one type system in one process.

**Why exact ids rather than `Microsoft.CodeAnalysis.*`.** The feed carries every Microsoft
package from every build. A wildcard would let an unrelated package, or a newer build of one, resolve
from it without anybody choosing that.

**When it changes.** Once nuget.org carries a release at least as new as the pinned build, the pin
moves to it and the mapping can go until the next SDK.
