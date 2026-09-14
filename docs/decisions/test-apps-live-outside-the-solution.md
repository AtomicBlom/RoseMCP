# The live-app test apps live outside the solution

**Decision.** The apps the live-app tests drive live in `tests/apps/` -- a classic UWP app, a UWP app
on modern .NET and a WinUI 3 app -- and none of them is in `RoseMcp.slnx`. `tests/apps/` carries its
own `Directory.Build.props` and `Directory.Packages.props`, which shadow the repository's, and the
tests build each app on demand.

**Why the repository carries its own apps.** A test against an app that exists on one machine is a
test nobody else can run. The probes are small, their elements are named for the assertions made
about them, and they mirror each other element for element, so a test written against one XAML stack
can be written against another by changing only the app it drives.

**Why outside the solution.** They are foreign project types. The classic UWP app is an old-style
MSBuild project that `dotnet build` cannot build at all, and the others need toolchains that somebody
working on the Roslyn half should not have to install. The shadowing props keep them off the
repository's defaults -- `net10.0`, warnings as errors, central package management -- which not all of
them can follow.

**Why the classic UWP app is registered from its staged layout.** A classic UWP CoreCLR build writes
the managed app and a native CoreCLR apphost to separate folders, and the root `AppxManifest.xml` names
the managed one. Registered as built, Windows hosts the app under the desktop .NET Framework CLR, and it
dies at host startup before a line of its own code runs. Visual Studio avoids this by staging the
layout its `*.build.appxrecipe` describes, and the test fixture does the same, because MSBuild's
`Build` target writes the recipe and never stages it.
