using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace RoseMcp.UnitTests;

/// <summary>
/// One call site, compiled, bound and rewritten, so a test can say what a shape does in a line.
/// <para>
/// A real compilation rather than a parse, because which parameter an argument is for is the
/// compiler's answer and not the text's -- and because a call site that does not bind is one of the
/// outcomes being tested rather than a broken fixture.
/// </para>
/// </summary>
internal static class CallSites
{
	/// <summary>The method every fixture declares and every case changes.</summary>
	internal const string Target = "Target";

	/// <summary>The rewritten argument list as text, or null where the call site was left alone.</summary>
	internal static string? Rewrite(string source, string wanted, params string[] arguments) =>
		Rewrite(source, wanted, out _, arguments);

	/// <summary>The same, with the reason it gives for leaving one alone.</summary>
	/// <param name="source">A compilation unit declaring <c>Target</c> and calling it once.</param>
	/// <param name="wanted">The parameter list the declaration is being given.</param>
	/// <param name="refusal">Why the call site was left, or empty when it was rewritten.</param>
	/// <param name="arguments">Expressions to pass for new parameters, as name=expression.</param>
	internal static string? Rewrite(string source, string wanted, out string refusal, params string[] arguments)
	{
		var tree = CSharpSyntaxTree.ParseText(source);

		var compilation = CSharpCompilation.Create(
			"Shapes",
			[tree],
			[MetadataReference.CreateFromFile(typeof(object).Assembly.Location)],
			new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

		var root = tree.GetRoot();
		var model = compilation.GetSemanticModel(tree);

		// The first declaration in the source is the one being changed, which is how the tool is
		// pointed at one. A fixture holding an override declares the name twice on purpose.
		var declaration = root.DescendantNodes()
			.OfType<MethodDeclarationSyntax>()
			.First(method => method.Identifier.Text == Target);

		var call = root.DescendantNodes()
			.OfType<InvocationExpressionSyntax>()
			.Single(invocation => NameOf(invocation.Expression) == Target)
			.ArgumentList;

		var plan = ParameterPlan.For(
			declaration.ParameterList.Parameters,
			MemberSyntax.ParseParameters(wanted, null));

		var binding = CallSiteBinding.For(model, call, out refusal);

		return binding is null
			? null
			: CallSiteRewriter.Rewrite(call, binding, plan, Supplied(arguments), out refusal)?.ToString();
	}

	/// <summary>The name a call writes, whether or not it qualifies it.</summary>
	private static string NameOf(ExpressionSyntax expression) => expression switch
	{
		MemberAccessExpressionSyntax member => member.Name.Identifier.Text,
		SimpleNameSyntax name => name.Identifier.Text,
		_ => string.Empty,
	};

	/// <summary>The expressions to pass for new parameters, written the way the tool takes them.</summary>
	private static Dictionary<string, string> Supplied(IReadOnlyList<string> arguments)
	{
		var supplied = new Dictionary<string, string>(StringComparer.Ordinal);

		foreach (var argument in arguments)
		{
			var split = argument.IndexOf('=', StringComparison.Ordinal);

			supplied[argument[..split].Trim()] = argument[(split + 1)..].Trim();
		}

		return supplied;
	}
}
