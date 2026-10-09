using System.Text.RegularExpressions;
using System.Xml.Linq;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace RoseMcp.UnitTests;

/// <summary>
/// Nothing writes to stdout in stdio mode except protocol frames, held against every line of source a
/// stdio process can load rather than against whichever paths the integration suite happens to drive.
/// <para>
/// A stray write corrupts the stream, and the failure reads as a protocol error in whatever parsed the
/// next frame, a long way from the print statement that caused it. The integration suite does parse a
/// child's stdout as JSON-RPC, but only on the paths it runs, and it blames the protocol. This reads the
/// source instead, so a failure names the file and the line.
/// </para>
/// <para>
/// A stdio process is a project that serves MCP over stdio, found by its call to
/// <c>WithStdioServerTransport</c>, and every project it references, analyzer references included: the
/// XAML stubs run inside the worker, so their stdout is the worker's. The WinUI windows are referenced
/// by none of them and are not read. The check is syntax rather than binding, because binding needs a
/// project's references and so a build, and the live-app host cannot build off Windows, where this
/// suite also runs.
/// </para>
/// <para>
/// It catches three things, every branch of every <c>#if</c> read whichever symbol it turns on:
/// </para>
/// <list type="bullet">
/// <item>A <see cref="Console"/> member that writes to stdout, however <c>Console</c> is spelled or imported.</item>
/// <item><c>AddConsole</c> without <c>LogToStandardErrorThreshold = LogLevel.Trace</c>, and the console formatters.</item>
/// <item>
/// A host builder that registers the default providers, a stdout console logger among them: a
/// <c>Create*Builder</c> on <c>Host</c>, <c>WebApplication</c> or <c>WebHost</c>, or a
/// <c>HostApplicationBuilder</c> constructed directly, unless its arguments set <c>DisableDefaults</c>.
/// It passes only when that builder's providers are cleared: <c>ConfigureLogging</c> on its own call
/// chain or on the local it is assigned to, with a lambda that clears its parameter; or, in the same
/// function, <c>local.Logging.ClearProviders()</c>, or <c>local.Logging</c> passed to a method of the
/// same file, by its bare name, in a parameter that method clears.
/// </item>
/// </list>
/// <para>
/// What gets past it is a write that names none of these -- a stream opened some other way -- which is
/// what the logging tests are for. A builder cleared some way it does not follow, through a field, a
/// lambda or a method of another file, fails it, and is better written one of the ways above.
/// </para>
/// </summary>
public sealed partial class StdoutRuleTests
{
	private const string Rule =
		"Nothing writes to stdout in stdio mode except protocol frames (CLAUDE.md, docs/invariants/transport-and-lifetime.md). "
		+ "Write to Console.Error, or log; each line below is a write a stdio process can make:";

	/// <summary>The members of <see cref="Console"/> that reach stdout. <c>Error</c>, <c>In</c> and the rest do not.</summary>
	private static readonly HashSet<string> StdoutMembers = new(StringComparer.Ordinal) { "Write", "WriteLine", "Out", "OpenStandardOutput", "SetOut" };

	/// <summary>
	/// Console logging that configures a formatter and not the stream, so it writes to stdout unless
	/// <c>AddConsole</c>'s own options move it.
	/// </summary>
	private static readonly HashSet<string> ConsoleFormatters = new(StringComparer.Ordinal) { "AddSimpleConsole", "AddJsonConsole", "AddSystemdConsole" };

	/// <summary>
	/// The host builders that register the default logging providers, console included. The empty
	/// builders register none, and are left out for that reason.
	/// </summary>
	private static readonly HashSet<string> HostBuilders = new(StringComparer.Ordinal) { "CreateApplicationBuilder", "CreateDefaultBuilder", "CreateBuilder", "CreateSlimBuilder" };

	private static readonly CSharpParseOptions Options = new(LanguageVersion.Preview, preprocessorSymbols: ["DEBUG", "TRACE"]);

	[Test]
	public void Nothing_a_stdio_process_loads_writes_to_stdout()
	{
		var writes = StdioProjects().SelectMany(WritesIn).ToList();

		writes.ShouldBeEmpty($"{Rule}{Environment.NewLine}{string.Join(Environment.NewLine, writes)}");
	}

	/// <summary>
	/// The hosts are found rather than listed, so this is what stops a finder that has gone blind from
	/// passing the test above by reading nothing.
	/// </summary>
	[Test]
	public void Reads_every_host_and_what_it_loads()
	{
		var hosts = StdioHosts().Select(project => project.Name).ToList();
		var loaded = StdioProjects().Select(project => project.Name).ToList();

		hosts.ShouldBe(["RoseMcp.LiveApp", "RoseMcp.Server", "RoseMcp.Worker"], ignoreOrder: true);
		loaded.ShouldContain("RoseMcp.Broker");
		loaded.ShouldContain("RoseMcp.XamlStubs");
		loaded.ShouldNotContain("RoseMcp.Tray");
		loaded.ShouldNotContain("RoseMcp.Inspector");
	}

	[Test]
	[Arguments("Console.WriteLine(\"x\");")]
	[Arguments("System.Console.Write(1);")]
	[Arguments("global::System.Console.Out.Flush();")]
	[Arguments("var stream = Console.OpenStandardOutput();")]
	[Arguments("Console.SetOut(TextWriter.Null);")]
	[Arguments("Action<string> write = Console.WriteLine;")]
	[Arguments("logging.AddConsole();")]
	[Arguments("logging.AddConsole(console => console.LogToStandardErrorThreshold = LogLevel.Warning);")]
	[Arguments("logging.AddSimpleConsole();")]
	public void Finds_a_write_however_it_is_spelled(string statement)
	{
		WritesIn(Method(statement), ConsoleImports.None).ShouldNotBeEmpty();
	}

	[Test]
	[Arguments("using static System.Console;", "WriteLine(\"x\");")]
	[Arguments("using static System.Console;", "Out.Flush();")]
	[Arguments("using Terminal = System.Console;", "Terminal.Out.Write(1);")]
	[Arguments("global using static System.Console;", "Write(1);")]
	public void Finds_a_write_through_an_import(string import, string statement)
	{
		WritesIn(import + Environment.NewLine + Method(statement), ConsoleImports.None).ShouldNotBeEmpty();
	}

	/// <summary>A global using applies to every file of its project, and is usually in a file of its own.</summary>
	[Test]
	public void Finds_a_write_through_another_files_global_import()
	{
		var imports = ConsoleImports.Of(CSharpSyntaxTree.ParseText("global using static System.Console;", Options).GetCompilationUnitRoot().Usings);

		WritesIn(Method("WriteLine(1);"), imports).ShouldNotBeEmpty();
		WritesIn(Method("WriteLine(1);"), ConsoleImports.None).ShouldBeEmpty();
	}

	[Test]
	[Arguments("Console.Error.WriteLine(\"x\");")]
	[Arguments("await Console.Error.WriteLineAsync(\"x\");")]
	[Arguments("// Console.WriteLine(\"x\");")]
	[Arguments("var text = \"Console.WriteLine\";")]
	[Arguments("writer.WriteLine(Console.ReadLine());")]
	[Arguments("logging.AddConsole(console => console.LogToStandardErrorThreshold = LogLevel.Trace);")]
	public void Leaves_stderr_and_text_alone(string statement)
	{
		WritesIn(Method(statement), ConsoleImports.None).ShouldBeEmpty();
	}

	/// <summary>Every branch of every conditional, whatever symbol it turns on and whichever side of it the write is.</summary>
	[Test]
	[Arguments("#if DEBUG\nConsole.WriteLine(1);\n#endif")]
	[Arguments("#if !DEBUG\nConsole.WriteLine(1);\n#endif")]
	[Arguments("#if WINDOWS\nConsole.WriteLine(1);\n#endif")]
	[Arguments("#if NET10_0_OR_GREATER\nConsole.Error.WriteLine(1);\n#else\nConsole.WriteLine(1);\n#endif")]
	[Arguments("#if WINDOWS\nConsole.Error.WriteLine(1);\n#elif A && !B\nConsole.WriteLine(1);\n#else\nConsole.Error.WriteLine(1);\n#endif")]
	public void Reads_every_branch_of_a_conditional(string body)
	{
		WritesIn(Method(body.Replace("\n", Environment.NewLine, StringComparison.Ordinal)), ConsoleImports.None).ShouldNotBeEmpty();
	}

	[Test]
	[Arguments("var builder = Host.CreateApplicationBuilder(args);")]
	[Arguments("var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder();")]
	[Arguments("var builder = WebApplication.CreateBuilder();")]
	[Arguments("var builder = WebApplication.CreateSlimBuilder();")]
	[Arguments("var host = Host.CreateDefaultBuilder(args).Build();")]
	[Arguments("var host = WebHost.CreateDefaultBuilder(args).Build();")]
	[Arguments("var builder = new HostApplicationBuilder(args);")]
	[Arguments("var builder = new Microsoft.Extensions.Hosting.HostApplicationBuilder(settings);")]
	[Arguments("HostApplicationBuilder builder = new(args);")]
	[Arguments("using var factory = LoggerFactory.Create(logging => { logging.ClearProviders(); });\nvar builder = Host.CreateApplicationBuilder();")]
	[Arguments("var first = Host.CreateApplicationBuilder();\nfirst.Logging.ClearProviders();\nvar second = Host.CreateApplicationBuilder();")]
	[Arguments("var builder = Host.CreateApplicationBuilder();\nAction clear = () => builder.Logging.ClearProviders();")]
	[Arguments("var builder = Host.CreateDefaultBuilder();\nother.ConfigureLogging(logging => logging.ClearProviders());")]
	[Arguments("var builder = Host.CreateDefaultBuilder();\nbuilder.ConfigureLogging(logging => other.ClearProviders());")]
	public void Finds_a_host_whose_default_console_logger_is_left_in(string statement)
	{
		WritesIn(Method(statement.Replace("\n", Environment.NewLine, StringComparison.Ordinal)), ConsoleImports.None).ShouldNotBeEmpty();
	}

	/// <summary>
	/// A top-level program is one function, and a local function in it is another: a host the local
	/// function builds and clears clears nothing for the one the program builds.
	/// </summary>
	[Test]
	public void Finds_a_top_level_host_beside_a_local_function_that_clears_its_own()
	{
		string[] lines =
		[
			"var builder = Host.CreateApplicationBuilder(args);",
			"void Other() { var builder = Host.CreateApplicationBuilder(); builder.Logging.ClearProviders(); }",
		];

		WritesIn(string.Join(Environment.NewLine, lines), ConsoleImports.None).Select(write => write.Split(':')[0]).ShouldBe(["1"]);
	}

	[Test]
	[Arguments("var builder = Host.CreateApplicationBuilder(args);\nbuilder.Logging.ClearProviders();")]
	[Arguments("var builder = new HostApplicationBuilder(args);\nbuilder.Logging.ClearProviders();")]
	[Arguments("var builder = new HostApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true });")]
	[Arguments("var host = Host.CreateDefaultBuilder(args).ConfigureLogging(logging => logging.ClearProviders()).Build();")]
	[Arguments("var host = WebHost.CreateDefaultBuilder(args).ConfigureLogging((context, logging) => logging.ClearProviders()).Build();")]
	[Arguments("var builder = Host.CreateDefaultBuilder();\nbuilder.ConfigureLogging(logging => logging.ClearProviders());")]
	[Arguments("var builder = Host.CreateEmptyApplicationBuilder(new());")]
	[Arguments("var builder = ImmutableArray.CreateBuilder<int>();")]
	public void Leaves_a_host_alone_that_clears_its_logging_providers(string statement)
	{
		WritesIn(Method(statement.Replace("\n", Environment.NewLine, StringComparison.Ordinal)), ConsoleImports.None).ShouldBeEmpty();
	}

	/// <summary>
	/// The shape the server has: one method that clears the providers it is given, called by each method
	/// that builds a host. A method that builds and clears a host of its own clears nothing for its
	/// caller, a same-named method on another object is not this file's, and a method clears only the
	/// parameter it calls <c>ClearProviders</c> on.
	/// </summary>
	[Test]
	public void Follows_the_logging_into_the_parameter_a_method_of_the_same_file_clears()
	{
		string[] lines =
		[
			"class C {",
			"void M() { var builder = Host.CreateApplicationBuilder(); }",
			"void N() { var builder = Host.CreateApplicationBuilder(); Configure(builder.Logging); }",
			"void O() { Relay(); var builder = Host.CreateApplicationBuilder(); }",
			"void Relay() { var builder = Host.CreateApplicationBuilder(); builder.Logging.ClearProviders(); }",
			"void P(C other) { var builder = Host.CreateApplicationBuilder(); other.Configure(builder.Logging); }",
			"void Q() { var builder = Host.CreateApplicationBuilder(); Pair(null, builder.Logging); }",
			"void R() { var builder = Host.CreateApplicationBuilder(); Pair(kept: null, cleared: builder.Logging); }",
			"static void Configure(ILoggingBuilder logging) => Helper(logging);",
			"static void Helper(ILoggingBuilder logging) => logging.ClearProviders();",
			"static void Pair(ILoggingBuilder cleared, ILoggingBuilder kept) => cleared.ClearProviders(); }",
		];

		WritesIn(string.Join(Environment.NewLine, lines), ConsoleImports.None).Select(write => write.Split(':')[0]).ShouldBe(["2", "4", "6", "7"]);
	}

	private static string Method(string body) => $"class C{Environment.NewLine}{{{Environment.NewLine}void M(){Environment.NewLine}{{{Environment.NewLine}{body}{Environment.NewLine}}}{Environment.NewLine}}}";

	private static IReadOnlyList<string> WritesIn(string source, ConsoleImports imports) =>
		[.. Parse(source, string.Empty).SelectMany(tree => WritesIn(tree, imports)).Select(write => $"{write.Line}: {write.What}").Distinct()];

	/// <summary>
	/// A file twice: as Debug compiles it, and with its conditional directives blanked, so that every
	/// branch of every <c>#if</c>, <c>#elif</c> and <c>#else</c> is code at once. The second parse may
	/// hold two branches that could never compile together, which a syntax walk does not mind, and its
	/// lines are the file's own, since only the directive lines are emptied.
	/// </summary>
	private static SyntaxTree[] Parse(string text, string path) =>
	[
		CSharpSyntaxTree.ParseText(text, Options, path),
		CSharpSyntaxTree.ParseText(ConditionalDirective().Replace(text, string.Empty), Options, path),
	];

	[GeneratedRegex(@"^[ \t]*#[ \t]*(if|elif|else|endif)\b.*$", RegexOptions.Multiline)]
	private static partial Regex ConditionalDirective();

	/// <summary>Every write in a project's sources, as <c>file:line: what</c> from the repository root.</summary>
	private static IEnumerable<string> WritesIn(Project project)
	{
		var trees = project.Sources
			.SelectMany(path => Parse(File.ReadAllText(path), path))
			.ToList();
		var projectWide = ConsoleImports.FromProjectFile(project.File)
			.With(ConsoleImports.FromProjectFile(Checkout.RepositoryFile("Directory.Build.props")))
			.With(ConsoleImports.Of(trees.SelectMany(tree => tree.GetCompilationUnitRoot().Usings).Where(directive => directive.GlobalKeyword.IsKind(SyntaxKind.GlobalKeyword))));
		var root = Checkout.RepositoryFile();

		return trees
			.SelectMany(tree => WritesIn(tree, projectWide).Select(write => $"{Path.GetRelativePath(root, tree.FilePath).Replace('\\', '/')}:{write.Line}: {write.What}"))
			.Distinct();
	}

	private static IEnumerable<(int Line, string What)> WritesIn(SyntaxTree tree, ConsoleImports projectWide)
	{
		var root = tree.GetCompilationUnitRoot();
		var imports = projectWide.With(ConsoleImports.Of(root.DescendantNodes().OfType<UsingDirectiveSyntax>()));
		var clearing = new ClearingParameters(root);

		foreach (var node in root.DescendantNodes())
		{
			var what = node switch
			{
				ExpressionSyntax creation when HostCreation(creation) is { } host && !ClearsProviders(creation, clearing) =>
					$"{host} keeps the default logging providers, whose console logger writes to stdout; call ClearProviders() on the "
					+ "builder's own Logging, in ConfigureLogging on its own chain, or in a method of this file it passes its Logging to",
				MemberAccessExpressionSyntax access when StdoutMembers.Contains(access.Name.Identifier.ValueText) && imports.Names(access.Expression) =>
					$"Console.{access.Name.Identifier.ValueText}",
				IdentifierNameSyntax name when imports.Static && StdoutMembers.Contains(name.Identifier.ValueText) && IsUnqualified(name) =>
					$"{name.Identifier.ValueText}, which a static import of Console makes Console.{name.Identifier.ValueText}",
				InvocationExpressionSyntax call => ConsoleLogging(call),
				_ => null,
			};

			if (what is not null) yield return (node.GetLocation().GetLineSpan().StartLinePosition.Line + 1, what);
		}
	}

	/// <summary>A name standing on its own, which is the only way a statically imported member is reached.</summary>
	private static bool IsUnqualified(IdentifierNameSyntax name) => name.Parent switch
	{
		MemberAccessExpressionSyntax access => access.Expression == name,
		QualifiedNameSyntax or MemberBindingExpressionSyntax or NameColonSyntax or NameEqualsSyntax or UsingDirectiveSyntax => false,
		_ => true,
	};

	/// <summary>
	/// Console logging that leaves anything on stdout. <c>LogToStandardErrorThreshold</c> sends a message
	/// at or above it to stderr and everything below it to stdout, and it defaults to sending nothing, so
	/// only <c>LogLevel.Trace</c> keeps stdout clear.
	/// </summary>
	private static string? ConsoleLogging(InvocationExpressionSyntax call)
	{
		var name = InvokedName(call);

		if (name is not null && ConsoleFormatters.Contains(name))
		{
			return $"{name}, console logging that writes to stdout; use AddConsole with LogToStandardErrorThreshold = LogLevel.Trace";
		}

		if (name != "AddConsole") return null;

		var allToStderr = call.ArgumentList.DescendantNodes()
			.OfType<AssignmentExpressionSyntax>()
			.Any(assignment => AssignedName(assignment.Left) == "LogToStandardErrorThreshold" && assignment.Right.ToString().EndsWith("LogLevel.Trace", StringComparison.Ordinal));

		return allToStderr ? null : "AddConsole without LogToStandardErrorThreshold = LogLevel.Trace, which leaves every level below the threshold on stdout";
	}

	/// <summary>
	/// What an expression creates, when it is a host builder that registers the default logging
	/// providers: a <c>Create*Builder</c> call on <c>Host</c>, <c>WebApplication</c> or <c>WebHost</c>,
	/// or a <c>HostApplicationBuilder</c> constructed directly. The empty builders register no providers
	/// and are not matched, and nor is a creation whose arguments set <c>DisableDefaults = true</c>.
	/// </summary>
	private static string? HostCreation(ExpressionSyntax expression)
	{
		var host = expression switch
		{
			InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax access }
				when HostBuilders.Contains(access.Name.Identifier.ValueText) && LastName(access.Expression) is "Host" or "WebApplication" or "WebHost" =>
				$"{LastName(access.Expression)}.{access.Name.Identifier.ValueText}",
			ObjectCreationExpressionSyntax creation when LastName(creation.Type) == "HostApplicationBuilder" => "new HostApplicationBuilder",
			ImplicitObjectCreationExpressionSyntax { Parent: EqualsValueClauseSyntax { Parent: VariableDeclaratorSyntax { Parent: VariableDeclarationSyntax declaration } } }
				when LastName(declaration.Type) == "HostApplicationBuilder" => "new HostApplicationBuilder",
			_ => null,
		};

		if (host is null) return null;

		var disablesDefaults = expression.DescendantNodes()
			.OfType<AssignmentExpressionSyntax>()
			.Any(assignment => AssignedName(assignment.Left) == "DisableDefaults" && assignment.Right.IsKind(SyntaxKind.TrueLiteralExpression));

		return disablesDefaults ? null : host;
	}

	/// <summary>
	/// Whether a host builder's own providers are cleared, tied to the builder rather than to anything
	/// nearby: <c>ConfigureLogging</c> on the creation's own call chain, or on the local it is assigned to,
	/// with a lambda that clears its parameter or a method of this file that does; or, in the same
	/// function as the creation, <c>local.Logging.ClearProviders()</c>, or <c>local.Logging</c> passed to a
	/// method of this file in a parameter it clears. A clear on another builder, in another function, or
	/// through a method reached any way but by its bare name, does not count.
	/// </summary>
	private static bool ClearsProviders(ExpressionSyntax creation, ClearingParameters clearing)
	{
		for (SyntaxNode current = creation; current.Parent is MemberAccessExpressionSyntax link && link.Expression == current && link.Parent is InvocationExpressionSyntax next; current = next)
		{
			if (link.Name.Identifier.ValueText == "ConfigureLogging" && ConfiguresAClear(next, clearing)) return true;
		}

		var local = creation.Parent switch
		{
			EqualsValueClauseSyntax { Parent: VariableDeclaratorSyntax declarator } => declarator.Identifier.ValueText,
			AssignmentExpressionSyntax { Left: IdentifierNameSyntax target } assignment when assignment.Right == creation => target.Identifier.ValueText,
			_ => null,
		};
		if (local is null) return false;

		var function = FunctionOf(creation);

		return function.DescendantNodes()
			.OfType<InvocationExpressionSyntax>()
			.Where(call => FunctionOf(call) == function)
			.Any(call =>
			{
				var clearsLogging = clearing.ClearedBy(call).Any(cleared => cleared is MemberAccessExpressionSyntax { Name.Identifier.ValueText: "Logging", Expression: IdentifierNameSyntax owner }
					&& owner.Identifier.ValueText == local);
				var configuresLocal = call.Expression is MemberAccessExpressionSyntax { Name.Identifier.ValueText: "ConfigureLogging", Expression: IdentifierNameSyntax receiver }
					&& receiver.Identifier.ValueText == local
					&& ConfiguresAClear(call, clearing);

				return clearsLogging || configuresLocal;
			});
	}

	/// <summary>A <c>ConfigureLogging</c> call given a lambda that clears its own parameter, or a method of this file that clears its.</summary>
	private static bool ConfiguresAClear(InvocationExpressionSyntax configure, ClearingParameters clearing) =>
		configure.ArgumentList.Arguments.Any(argument => argument.Expression switch
		{
			AnonymousFunctionExpressionSyntax lambda => lambda.DescendantNodes()
				.OfType<InvocationExpressionSyntax>()
				.SelectMany(clearing.ClearedBy)
				.Any(cleared => cleared is IdentifierNameSyntax name && ParametersOf(lambda).Contains(name.Identifier.ValueText)),
			IdentifierNameSyntax method => clearing.ClearsAny(method.Identifier.ValueText),
			_ => false,
		});

	private static IEnumerable<string> ParametersOf(AnonymousFunctionExpressionSyntax lambda) => lambda switch
	{
		SimpleLambdaExpressionSyntax simple => [simple.Parameter.Identifier.ValueText],
		ParenthesizedLambdaExpressionSyntax parenthesized => parenthesized.ParameterList.Parameters.Select(parameter => parameter.Identifier.ValueText),
		AnonymousMethodExpressionSyntax { ParameterList: { } parameters } => parameters.Parameters.Select(parameter => parameter.Identifier.ValueText),
		_ => [],
	};

	/// <summary>The method, local function, accessor or lambda a node runs in; a top-level statement runs in the file.</summary>
	private static SyntaxNode FunctionOf(SyntaxNode node) =>
		node.Ancestors().FirstOrDefault(ancestor => ancestor is BaseMethodDeclarationSyntax or LocalFunctionStatementSyntax or AnonymousFunctionExpressionSyntax or AccessorDeclarationSyntax)
			?? node.SyntaxTree.GetRoot();

	/// <summary>
	/// Which parameters each method and local function of a file clears the providers of: one it calls
	/// <c>ClearProviders</c> on, or passes in a parameter position that the method it calls clears.
	/// </summary>
	private sealed class ClearingParameters
	{
		private readonly List<(string Name, string[] Parameters, SyntaxNode Body, HashSet<int> Cleared)> _functions;

		public ClearingParameters(SyntaxNode root)
		{
			_functions = [.. root.DescendantNodes()
				.Select(node => node switch
				{
					MethodDeclarationSyntax method => (Name: method.Identifier.ValueText, List: (ParameterListSyntax?)method.ParameterList, Body: (SyntaxNode)method),
					LocalFunctionStatementSyntax function => (Name: function.Identifier.ValueText, List: function.ParameterList, Body: function),
					_ => (Name: string.Empty, List: null, Body: node),
				})
				.Where(function => function.List is not null)
				.Select(function => (function.Name, Parameters: function.List!.Parameters.Select(parameter => parameter.Identifier.ValueText).ToArray(), function.Body, Cleared: new HashSet<int>()))];

			for (var grew = true; grew;)
			{
				grew = false;
				foreach (var function in _functions)
				{
					var indexes = function.Body.DescendantNodes()
						.OfType<InvocationExpressionSyntax>()
						.SelectMany(ClearedBy)
						.OfType<IdentifierNameSyntax>()
						.Select(name => Array.IndexOf(function.Parameters, name.Identifier.ValueText))
						.Where(index => index >= 0)
						.ToList();

					foreach (var index in indexes) grew |= function.Cleared.Add(index);
				}
			}
		}

		public bool ClearsAny(string name) => _functions.Any(function => function.Name == name && function.Cleared.Count > 0);

		/// <summary>
		/// What a call clears the providers of: the receiver of <c>ClearProviders()</c>, and each argument a
		/// method of this file, called by its bare name, clears the parameter of.
		/// </summary>
		public IEnumerable<ExpressionSyntax> ClearedBy(InvocationExpressionSyntax call)
		{
			if (call.Expression is MemberAccessExpressionSyntax { Name.Identifier.ValueText: "ClearProviders" } access) yield return access.Expression;
			if (call.Expression is not IdentifierNameSyntax callee) yield break;

			var arguments = call.ArgumentList.Arguments;
			foreach (var function in _functions.Where(function => function.Name == callee.Identifier.ValueText && function.Parameters.Length >= arguments.Count))
			{
				for (var position = 0; position < arguments.Count; position++)
				{
					var index = arguments[position].NameColon is { } named ? Array.IndexOf(function.Parameters, named.Name.Identifier.ValueText) : position;
					if (function.Cleared.Contains(index)) yield return arguments[position].Expression;
				}
			}
		}
	}

	private static string? InvokedName(InvocationExpressionSyntax call) => call.Expression switch
	{
		MemberAccessExpressionSyntax access => access.Name.Identifier.ValueText,
		IdentifierNameSyntax identifier => identifier.Identifier.ValueText,
		_ => null,
	};

	/// <summary>The type a static call is made on, however it is qualified.</summary>
	private static string? LastName(ExpressionSyntax receiver) => receiver switch
	{
		MemberAccessExpressionSyntax access => access.Name.Identifier.ValueText,
		AliasQualifiedNameSyntax alias => alias.Name.Identifier.ValueText,
		QualifiedNameSyntax qualified => qualified.Right.Identifier.ValueText,
		SimpleNameSyntax name => name.Identifier.ValueText,
		_ => null,
	};

	private static string? AssignedName(ExpressionSyntax target) => target switch
	{
		MemberAccessExpressionSyntax access => access.Name.Identifier.ValueText,
		IdentifierNameSyntax identifier => identifier.Identifier.ValueText,
		_ => null,
	};

	/// <summary>Every project that serves MCP over stdio, and every project each one references, once.</summary>
	private static IReadOnlyList<Project> StdioProjects()
	{
		var seen = new Dictionary<string, Project>(StringComparer.OrdinalIgnoreCase);
		var pending = new Queue<Project>(StdioHosts());

		while (pending.TryDequeue(out var project))
		{
			if (!seen.TryAdd(project.File, project)) continue;

			foreach (var reference in project.References) pending.Enqueue(reference);
		}

		return [.. seen.Values];
	}

	/// <summary>The projects under <c>src</c> that call <c>WithStdioServerTransport</c>, in code rather than in a comment.</summary>
	private static IEnumerable<Project> StdioHosts() =>
		Directory.EnumerateFiles(Checkout.RepositoryFile("src"), "*.csproj", SearchOption.AllDirectories)
			.Select(path => new Project(Path.GetFullPath(path)))
			.Where(project => project.Sources.Any(ServesStdio));

	private static bool ServesStdio(string path)
	{
		var text = File.ReadAllText(path);
		if (!text.Contains("WithStdioServerTransport", StringComparison.Ordinal)) return false;

		return CSharpSyntaxTree.ParseText(text, Options).GetRoot().DescendantNodes()
			.OfType<MemberAccessExpressionSyntax>()
			.Any(access => access.Name.Identifier.ValueText == "WithStdioServerTransport");
	}

	/// <summary>A project file, read without MSBuild: its sources are the C# under its folder, less its build output.</summary>
	private sealed record Project(string File)
	{
		public string Name => Path.GetFileNameWithoutExtension(File);

		public string Folder => Path.GetDirectoryName(File)!;

		public IEnumerable<string> Sources =>
			Directory.EnumerateFiles(Folder, "*.cs", SearchOption.AllDirectories)
				.Where(path => !Path.GetRelativePath(Folder, path).Split('/', '\\').Any(segment => segment is "bin" or "obj"));

		/// <summary>Every <c>ProjectReference</c>, analyzer references included, written with either separator.</summary>
		public IEnumerable<Project> References =>
			XDocument.Load(File).Descendants()
				.Where(element => element.Name.LocalName == "ProjectReference")
				.Select(element => (string?)element.Attribute("Include"))
				.OfType<string>()
				.Select(include => new Project(Path.GetFullPath(Path.Combine(Folder, include.Replace('\\', Path.DirectorySeparatorChar)))));
	}

	/// <summary>How code can reach Console without writing its name: a static import, or an alias.</summary>
	private sealed record ConsoleImports(bool Static, IReadOnlySet<string> Aliases)
	{
		public static readonly ConsoleImports None = new(false, new HashSet<string>(StringComparer.Ordinal));

		/// <summary>Whether an expression is Console itself, spelled any way the compiler would accept.</summary>
		public bool Names(ExpressionSyntax expression) =>
			IsConsole(expression.ToString()) || (expression is IdentifierNameSyntax identifier && Aliases.Contains(identifier.Identifier.ValueText));

		public ConsoleImports With(ConsoleImports other) =>
			new(Static || other.Static, Aliases.Union(other.Aliases).ToHashSet(StringComparer.Ordinal));

		public static ConsoleImports Of(IEnumerable<UsingDirectiveSyntax> directives)
		{
			var isStatic = false;
			var aliases = new HashSet<string>(StringComparer.Ordinal);

			foreach (var directive in directives.Where(directive => IsConsole(directive.NamespaceOrType.ToString())))
			{
				if (directive.Alias is not null) aliases.Add(directive.Alias.Name.Identifier.ValueText);
				else if (directive.StaticKeyword.IsKind(SyntaxKind.StaticKeyword)) isStatic = true;
			}

			return new(isStatic, aliases);
		}

		/// <summary>The <c>Using</c> items an MSBuild file adds to every source in a project, which no source shows.</summary>
		public static ConsoleImports FromProjectFile(string path)
		{
			var usings = XDocument.Load(path).Descendants()
				.Where(element => element.Name.LocalName == "Using" && IsConsole((string?)element.Attribute("Include") ?? string.Empty))
				.ToList();
			var isStatic = usings.Any(element => string.Equals((string?)element.Attribute("Static"), "true", StringComparison.OrdinalIgnoreCase));
			var aliases = usings.Select(element => (string?)element.Attribute("Alias")).OfType<string>().ToHashSet(StringComparer.Ordinal);

			return new(isStatic, aliases);
		}

		private static bool IsConsole(string name)
		{
			var bare = string.Concat(name.Where(character => !char.IsWhiteSpace(character)));
			if (bare.StartsWith("global::", StringComparison.Ordinal)) bare = bare["global::".Length..];

			return bare is "Console" or "System.Console";
		}
	}
}
