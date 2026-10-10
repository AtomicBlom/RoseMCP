# The probe-app tests run on a hosted runner, in a job that sets the machine up

**Decision.** The integration tests that need a XAML probe app -- the `ProbeApp` category: the tap
injected into a running classic UWP, modern UWP or WinUI 3 app, the visual tree, the properties
read, the pick, the overlay and the live-edit apply -- run in CI in a job of their own, `probe-apps`,
on a GitHub-hosted `windows-latest` runner. The job installs what they need before running them, one
step per component, and sets `ROSEMCP_TESTS_REQUIRE_TOOLCHAIN` so that any test which would have
skipped fails instead.

**Why its own job.** The tests need a machine the rest of the suite does not: developer mode, Visual
Studio's MSBuild with the UWP tooling, the C++ toolset and the Windows SDK, and the Windows App
Runtime. Setting those up is minutes the other integration tests should not wait for, and the
suite takes the machine to itself -- the probe packages are registered machine-wide under fixed
names -- which a hosted job gives it for free, since each job is a fresh machine. It runs on a pull
request only when something a probe-app test reaches has changed, and always on main.

**Why each setup step checks its own result.** Every component a probe-app test needs is also a
reason it can fail, and a failure several minutes into the tests -- an `Add-AppxPackage` HRESULT, a
probe that never opened a window -- says nothing about which component was missing. A step that
reads back what it set, or looks for the files a build will open, names the component while the
reason is still in view. Visual Studio is checked by those files rather than by component IDs,
because the IDs differ between Build Tools and the IDE products and change between releases, while
the files are what the build actually opens; only what is missing is installed.

**Why a skip is a failure there.** A skip reads as a pass. The probe fixtures skip when the machine
cannot build or register an app, which is right on a developer machine without the C++ workload and
wrong on a runner whose setup was supposed to provide it: a runner that silently lost a component
would report the same green as one that ran everything, and nobody reads the skip count of a green
job. So the job says the machine was meant to have everything, and a skip becomes a failure naming
what was missing. The integration job sets the same switch for the same reason, and
`ProbeAppCategoryTests` checks that both do and that no test skips around it.

**The UWP probes' frameworks come from their own build, not from a setup step.** A UWP debug build
depends on framework packages that Visual Studio installs the first time it deploys one, so a machine
it has never deployed from -- a hosted runner -- refuses to register the probes with 0x80073CF3. The
built manifests ask for these, each from `CN=Microsoft Corporation`:

| Probe | Framework | Minimum version | Where the build found it |
|---|---|---|---|
| classic UWP | `Microsoft.VCLibs.140.00.Debug` | 14.0.33519.0 | the Windows SDK's `ExtensionSDKs\Microsoft.VCLibs\14.0\AppX\Debug\<arch>` |
| classic UWP | `Microsoft.NET.CoreRuntime.2.2` | 2.2.31331.1 | the `runtime.win10-<arch>.microsoft.net.uwpcoreruntimesdk` package, `tools\Appx` |
| classic UWP | `Microsoft.NET.CoreFramework.Debug.2.2` | 2.2.31327.1 | the same package |
| modern UWP | `Microsoft.VCLibs.140.00.Debug` | 14.0.33519.0 | the Windows SDK, as above |

Each build's `.build.appxrecipe` names the same packages as `ResolvedSDKReference` items, with the
architecture and the `.appx` each installs from -- the list Visual Studio's own deploy installs. So the
fixtures install from it: a registration refused for a missing framework installs everything the
recipe holds for the architecture Windows named, and registers again, once. The frameworks are named
by the build that linked against them rather than kept in a list here or in the job, so a probe that
gains a dependency needs no change to either, and a developer machine without Visual Studio's deploy
history registers the probes the same way the runner does. The second registration is the check that
the install took: it succeeds only when every framework the manifest names is present. They go in as
packages of their own before the layout because `Add-AppxPackage -Register -DependencyPath` treats each
dependency as another loose layout and refuses an `.appx`.

When a registration fails anyway, the failure carries the deployment engine's whole message and the
errors from its log for that activity, so it names the framework it wanted, and the job keeps the
AppX deployment event log and the runner's framework packages in its evidence.

**Why hosted rather than self-hosted.** A self-hosted runner is a machine somebody keeps up, and
this repository is public: a self-hosted runner serving its pull requests runs a fork's code on that
machine, which GitHub advises against for public repositories. Nothing the tests need is beyond what
a hosted image can be given in a few minutes of setup, so the hosted runner is the choice until it
is shown not to be enough.

**What is least certain, and the fallback.** Whether a hosted image can activate a packaged app into
an AppContainer from the runner's own session at all. Every other step can be checked before the
tests start; this one is known only from the first run. If the hosted image cannot do it, the
fallback is a self-hosted runner -- the developer box that already runs this suite -- with the job's
`runs-on` pointed at its label and the job restricted to runs that do not come from a fork, which
keeps the rest of this decision as it is.

**What it does not do.** It does not measure a flake rate. Each run is one sample of these tests on
a fresh machine, which catches a test that fails every time and some that fail often; a rate needs
repeated runs and something that counts them, which this job does not have.
