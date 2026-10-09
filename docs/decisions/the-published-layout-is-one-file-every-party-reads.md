# The published layout is one file every party reads

**Decision.** Where each part of RoseMCP sits -- in an install, and in the Windows package an install
is laid down from -- is written once, in `tools/published-layout.json`, committed beside the scripts
that read it. `deploy.ps1` publishes from it and checks the package against it; `install.ps1` and
`build-installer.ps1` check a package and lay it down from it, through helpers in
`RoseMcp.Deploy.ps1`. The two parties that cannot read it are held to it by unit tests:
`PublishedLayoutTests` stages the layout from the file and drives every C# resolver against it with
the repository fallback off, and `InstallerLayoutTests` reads `installer/rosemcp.iss` against it.

**Why it matters enough to be structural.** The layout has five parties, and two of them --
`install.ps1` and the Inno installer -- are packaged content that runs on somebody else's machine.
When each wrote the layout down for itself, the unit test staged its own copy of the tree, so moving
the tray or renaming a host folder in PowerShell passed every test and failed at install time, with
the failure in a stranger's log rather than in CI.

**Why a committed file that is read, rather than one the publish emits.** The alternative was to
have `Publish-Tree` write a `layout.json` listing what it produced, keep a committed copy for the
tests, and test that the two match. That is a sixth copy of the layout plus a test that can only run
where a full Windows package can be built -- a publish of five projects for three architectures and
two native providers, which the unit suite cannot afford and the Linux leg cannot do at all. A file
that is the *input* to the publish cannot drift from what the publish writes, because the publish
has nowhere else to get a folder name from; and `Assert-WindowsPackage` still checks the produced
tree against it, so a publish that ignored it would fail packaging.

**Why not shell out to `deploy.ps1 -Mode package` from a test.** The same cost, plus a unit test that
starts processes, needs the MSVC toolsets and takes minutes. The unit suite starts no child process,
and that is what makes it cheap enough to run on every change.

**Why the C# resolvers keep their own literals.** They could read the file at run time, but then an
install would need it beside every host, and a missing or unreadable file would become a new way for
the broker to fail to find its worker. A resolver's folder name is checked against the file by a test
instead, which fails the moment the two disagree and adds nothing to what an install must carry.

**Why the Inno script is tested rather than generated.** Inno cannot read JSON, so either
`build-installer.ps1` generates an `#include` from the file, or the script restates the layout and a
test holds it to the file. Generating makes `rosemcp.iss` uncompilable on its own and moves Inno
syntax into PowerShell string building, where nothing checks it until ISCC runs on a release tag. A
test of the script as written runs on every change, on both operating systems, and fails naming the
line that disagrees.

**What the file is allowed to contain.** Only what more than one party needs: which architectures
ship and which debug hosts each carries, where each component, host and provider sits, and how the
package arranges them. A fact about something else -- the PE machine word a RID's images carry, the
process names an install is stopped by -- stays in `RoseMcp.Deploy.ps1`, where it already has one
home; the process names especially, because the Inno installer stops an install with that script
alone and no layout beside it. The file is ASCII JSON with no comments, because Windows
PowerShell 5.1 reads it too; the reasons for each placement are in its `why` fields.
