# Analyzers and generators

Read before changing analyzer loading, `RoseMcp.XamlStubs`, or anything handing Roslyn an `AnalyzerReference`.

- **Analyzer assemblies are never loaded from where they live.** They are shadow-copied first. A
  loaded assembly is held open for the life of the process, and this process lives for hours, so
  loading them in place means the user cannot rebuild their own generator -- `dotnet build` fails
  with MSB3021. There is a regression test for this; do not "simplify" it away.
- **A XAML project's generated half is synthesised, and says so.** The markup compiler runs only in a
  real build, so `MSBuildWorkspace` hands us code-behind missing its base type, its `x:Name` fields
  and `InitializeComponent` -- 2030 phantom errors in one project of a 50-project UWP app.
  `RoseMcp.XamlStubs` parses the markup and generates that partial. Element types resolve out of the
  Roslyn type universe; anything that does not resolve is left out and reported, never faked. Check
  changes against the `.g.i.cs` files a real build leaves in `obj` -- that comparison is what found
  the four things reasoning had missed, and it agrees exactly today.
- **Never hand Roslyn a custom `AnalyzerReference`.** Its serializer switches on the concrete type --
  `AnalyzerFileReference`, `AnalyzerImageReference`, and an interface nested inside an internal class
  -- and throws `Unexpected value` on everything else. It checksums a project's analyzer references
  whenever it builds the index behind `FindDerivedClasses`, which every member-level find-references
  and rename reaches through `FindImplementedInterfaceMembers`. The stub generator used to be an
  in-memory subclass, for the good reason that there was then no analyzer assembly to ship or
  version-match, and that made those two tools throw on every solution containing XAML -- while
  type-level searches, which never build that index, went on working and hid it. It is a real
  assembly now, loaded as an `AnalyzerFileReference` through the shadow-copying loader, which is
  also what keeps it rebuildable. Two tests in `XamlWorkspaceTests` fail with `Unexpected value` if
  anyone wraps it again; the fixture's `Greeter` exists only to force that walk. Roslyn constructs
  the generator itself now, so there is no callback to hand it -- it reports through one generated
  document, which `GeneratedDocumentService` hides and `XamlStubReportReader` parses.
