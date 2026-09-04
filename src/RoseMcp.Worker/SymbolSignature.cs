using Microsoft.CodeAnalysis;

namespace RoseMcp.Worker;

/// <summary>
/// One way of spelling a symbol, shared by everything that reports one.
/// <para>
/// Shared rather than copied because a display format copied is a display format that drifts: the
/// same member would be spelled one way by a navigation result and another way by the error that
/// refused to edit it, and a caller comparing the two would reasonably conclude they were different
/// members.
/// </para>
/// </summary>
public static class SymbolSignature
{
	/// <summary>
	/// Fully qualified, with parameter types and names, and the language's own names for the special
	/// types -- <c>int</c> rather than <c>System.Int32</c>, which is what the caller reading the
	/// answer would have written themselves.
	/// </summary>
	public static readonly SymbolDisplayFormat Format = new(
		globalNamespaceStyle: SymbolDisplayGlobalNamespaceStyle.Omitted,
		typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces,
		genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters,
		memberOptions: SymbolDisplayMemberOptions.IncludeParameters
			| SymbolDisplayMemberOptions.IncludeType
			| SymbolDisplayMemberOptions.IncludeContainingType,
		parameterOptions: SymbolDisplayParameterOptions.IncludeType | SymbolDisplayParameterOptions.IncludeName,
		miscellaneousOptions: SymbolDisplayMiscellaneousOptions.UseSpecialTypes);

	public static string Of(ISymbol symbol) => symbol.ToDisplayString(Format);
}
