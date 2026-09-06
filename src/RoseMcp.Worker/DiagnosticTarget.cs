using RoseMcp.Contracts;

namespace RoseMcp.Worker;

/// <summary>
/// Which slice of the solution a diagnostics call means, worked out from what it named.
/// <para>
/// It used to be one argument called <c>target</c> that was a file path under document scope and a
/// project name under project scope. Every other tool here spells a file <c>filePath</c> and a
/// project <c>project</c>, and the routing layer carries a paragraph about the hazard that one
/// argument created for it -- so the two are separate arguments, and which one is given says what
/// the scope is. <c>scope</c> stays for the case neither covers, which is the whole solution.
/// </para>
/// </summary>
/// <param name="Scope">How much to analyse.</param>
/// <param name="Target">The file or project that scope applies to, or null for the solution.</param>
public readonly record struct DiagnosticTarget(DiagnosticScope Scope, string? Target)
{
	/// <summary>Reads the three ways a call can say what to analyse.</summary>
	/// <exception cref="ArgumentException">
	/// Both a file and a project were given, or a scope was named with nothing for it to apply to.
	/// </exception>
	public static DiagnosticTarget From(string? filePath, string? project, string? scope)
	{
		if (filePath is { Length: > 0 } && project is { Length: > 0 })
		{
			throw new ArgumentException(
				"Pass filePath or project, not both. A file and a project are different questions, and which "
					+ "of them was meant is not something this can decide.");
		}

		if (filePath is { Length: > 0 }) return new(DiagnosticScope.Document, filePath);

		if (project is { Length: > 0 }) return new(DiagnosticScope.Project, project);

		// A scope with nothing for it to apply to used to analyse the whole solution, which answers a
		// question many times the size of the one asked and reads exactly like an answer to it.
		if (ArgumentValues.Scope(scope) is var wanted && wanted != DiagnosticScope.Solution)
		{
			throw new ArgumentException(
				$"scope is {scope} but nothing says which one: pass filePath for a document, or project for a "
					+ "project. Leave both off for the whole solution.");
		}

		return new(DiagnosticScope.Solution, null);
	}
}
