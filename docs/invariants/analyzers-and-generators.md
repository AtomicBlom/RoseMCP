# Analyzers and generators

Read before changing analyzer loading, `RoseMcp.XamlStubs`, or anything handing Roslyn an `AnalyzerReference`.

- **Analyzer assemblies are never loaded from where they live.** They are shadow-copied first. A
  loaded assembly is held open for the life of the process, and this process lives for hours, so
  loading them in place means the user cannot rebuild their own generator -- `dotnet build` fails
  with MSB3021. There is a regression test for this; do not "simplify" it away.
- **Each analyzer directory loads into an `AssemblyLoadContext` of its own, and a dependency is
  never resolved by simple name alone.** A solution spanning several target frameworks carries a
  generator per framework and the versions differ -- one 96-project solution holds four versions of
  `Microsoft.Extensions.Logging.Generators`, and a package ships the same version twice again under
  `roslyn3.11` and `roslyn4.x`. A context holds one assembly per identity, so loading them together
  means the second of each pair does not load; a map keyed on the simple name means whichever
  arrived last answers for all of them, and the runtime rejects the mismatch with
  `FUSION_E_REF_DEF_MISMATCH` (0x80131040). Either way the result is not an error but an analyzer
  that produces nothing, silently, while MSBuild goes on passing it to the compiler. The directory
  is the unit of isolation because that is the unit a package is laid out in: one file per name, so
  one version of each. Satellite assemblies belong to the directory above the culture folder they
  sit in, not to that folder. `AnalyzerVersionIsolationTests` fails with the production HRESULT if
  this is undone.
- **A context resolves from its whole directory, not only from what MSBuild declared.** The analyzer
  list a project carries is the set of entry points, not the closure of what they call: the SDK's
  `Microsoft.CodeAnalysis.CSharp.NetAnalyzers` calls into `Microsoft.CodeAnalysis.NetAnalyzers`
  beside it, and the interop generators into `Microsoft.Interop.SourceGeneration`, none of which any
  project lists. Resolve only the declared set and those assemblies load as a fraction of their
  types and none of their fixers -- which reads as a file with nothing to fix in it. Probing the
  directory is also what shadow-copies a dependency nothing asked for by name, so the lock this
  loader exists to avoid is not reintroduced through the dependency.
- **The compiler's own assemblies always come from the host.** `Microsoft.CodeAnalysis*`,
  `System.Collections.Immutable` and `System.Reflection.Metadata` resolve to the default context
  even when an analyzer directory ships its own. An analyzer that loads a second Roslyn is handed a
  `GeneratorInitializationContext` whose type it does not recognise as the one it was built
  against, which fails in a way that reads as the generator being broken rather than as two
  compilers being loaded.
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
