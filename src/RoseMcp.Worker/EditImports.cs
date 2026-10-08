using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

using RoseMcp.Contracts;

namespace RoseMcp.Worker;

/// <summary>
/// The imports a member edit needs: the ones the caller asked for, and the ones the names it wrote
/// turn out to want.
/// <para>
/// Both run against the file the edit just touched and before it is formatted, which is what keeps
/// an import out of a second call. The two halves are separate because they are answerable at
/// different moments: what the caller asked for is known before anything compiles, and what the
/// code needs is only known once it has.
/// </para>
/// </summary>
internal static class EditImports
{
	/// <summary>
	/// How many distinct unresolved names an import is looked up for. An edit that introduces forty
	/// has gone wrong in a way no import list will fix, and forty searches would make reporting that
	/// failure slower than the failure.
	/// </summary>
	private const int Looked = 5;

	/// <summary>
	/// Ensures the imports the caller asked for, on the same file and before it is formatted.
	/// <para>
	/// Asked of the compilation rather than of the using list, because a namespace can be in scope
	/// three ways this file does not show: a global using, an implicit using from the SDK, or simply
	/// being the namespace the file is in. Adding a directive for one of those is IDE0005, which is a
	/// build error here -- so the check that looks unnecessary is the one that keeps the tool from
	/// breaking the build it was called to avoid.
	/// </para>
	/// </summary>
	internal static async Task<MemberEditService.Written> AskedForAsync(
		MemberEditService.Written written,
		MemberEditRequest request,
		List<string> notices,
		CancellationToken cancellationToken)
	{
		if (request.Usings.Count == 0 || written.Root is not CompilationUnitSyntax root) return written;

		var document = written.Document;
		var model = await document.GetSemanticModelAsync(cancellationToken);
		var tree = await document.GetSyntaxTreeAsync(cancellationToken);

		if (model is null || tree is null) return written;

		var style = UsingStyle.For(document.Project, tree, root, written.Rules.LineEnding);

		var insertion = UsingDirectives.Ensure(root, model, request.Usings, style, cancellationToken);

		if (insertion.Added.Count > 0)
		{
			notices.Add($"Imported {string.Join(", ", insertion.Added)}.");
		}

		foreach (var covered in insertion.AlreadyInScope)
		{
			notices.Add($"Did not import {covered}.");
		}

		if (insertion.Added.Count == 0) return written with { Root = insertion.Root };

		return written with
		{
			Root = insertion.Root,
			Asked = [.. written.Asked, await RegionAsync(document, cancellationToken)],
		};
	}

	/// <summary>
	/// Works out what would import the names the edit left unresolved and adds the ones with a single
	/// answer, handing back what it applied so the caller can say whether each one worked.
	/// <para>
	/// The half <see cref="MissingImports"/> stops short of. Reporting the namespace and leaving the
	/// caller to add it is a round trip at exactly the moment they were promised there would not be
	/// one: the code was just written by this tool, and it does not compile.
	/// </para>
	/// <para>
	/// An import that does not resolve the name it was fetched for is taken back out before this
	/// returns, and what it hands back says so; the caller reports what was kept.
	/// </para>
	/// </summary>
	internal static async Task<(Solution Solution, ResolvedImports.Imports Imports)> ForUnresolvedAsync(
		WorkspaceSnapshot snapshot,
		Solution solution,
		MemberEditService.Written written,
		string path,
		IReadOnlyList<DiagnosticEntry> introduced,
		CancellationToken cancellationToken)
	{
		var imports = await ResolvedImports.ForAsync(
			new WorkspaceSnapshot { Solution = solution, Revision = snapshot.Revision },
			introduced,
			path,
			Looked,
			cancellationToken);

		return await ResolvedImports.ApplyResolvingAsync(
			solution, written.Document.Id, imports, written.Rules, cancellationToken);
	}

	/// <summary>
	/// Where imports go in <paramref name="document"/> as it stood before the edit, which is what adding
	/// one asks to change.
	/// </summary>
	internal static async Task<TextSpan> RegionAsync(Document document, CancellationToken cancellationToken) =>
		await document.GetSyntaxRootAsync(cancellationToken) is CompilationUnitSyntax root
			? UsingDirectives.Region(root)
			: new TextSpan(0, 0);
}
