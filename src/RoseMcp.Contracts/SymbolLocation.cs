using System.Globalization;

namespace RoseMcp.Contracts;

/// <summary>
/// The debugger's location grammar: a method addressed by name, the way a breakpoint or tracepoint
/// is requested before any module has loaded, and optionally an instruction inside it.
/// <para>
/// It carries the declaring type's full name as metadata spells it, the method's own name, and the
/// assembly's simple name when one was written. Without one it names no module at all, and the type
/// is looked for in every module the target loads: an assembly's name says nothing reliable about the
/// namespaces inside it, so reading a module off the type name is a guess that fails on the first
/// repository to name the two apart.
/// </para>
/// <para>
/// Here rather than beside the debugger for the reason <see cref="ValuePath"/> is: the host that
/// owns it is <c>net10.0-windows</c> and neither test project takes a compile reference on it, so a
/// grammar living there is a grammar no test can see. Every failure this can have is a breakpoint
/// somewhere other than where it was asked for, reported as a success.
/// </para>
/// </summary>
public sealed record SymbolLocation(
	string? Assembly,
	string TypeName,
	string MethodName,
	int? IlOffset)
{
	private const string OffsetMarker = "@IL_";

	/// <summary>
	/// Two spellings. <c>Namespace.Type.Method</c> names no assembly and binds in whichever module
	/// declares the type. <c>Assembly!Namespace.Type.Method</c> names one, which is how to choose
	/// between modules declaring a type of the same name.
	/// <para>
	/// Either may end in <c>@IL_001f</c> to name an instruction inside the method rather than its
	/// first. The offset rides in the location rather than arriving as an argument of its own, so
	/// that what a breakpoint reports is what sets the same breakpoint again, and so that the
	/// agent-facing and host-facing tools go on declaring the same arguments.
	/// </para>
	/// </summary>
	/// <exception cref="ArgumentException">The spelling is not one of those.</exception>
	public static SymbolLocation Parse(string spec)
	{
		if (string.IsNullOrWhiteSpace(spec)) throw new ArgumentException("A location is required.", nameof(spec));

		var (name, ilOffset) = SplitOffset(spec.Trim());
		spec = name;

		string? assembly = null;
		var bang = spec.IndexOf('!');
		if (bang > 0)
		{
			assembly = spec[..bang];
			spec = spec[(bang + 1)..];

			// Strip only a real assembly extension. A dotted assembly name must not have its last
			// segment mistaken for one.
			var hasExtension = assembly.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
				|| assembly.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);
			if (hasExtension) assembly = assembly[..^4];
		}

		var lastDot = spec.LastIndexOf('.');
		if (lastDot <= 0 || lastDot == spec.Length - 1)
		{
			throw new ArgumentException($"Expected [Assembly!]Namespace.Type.Method, got '{spec}'.", nameof(spec));
		}

		return new SymbolLocation(assembly, spec[..lastDot], spec[(lastDot + 1)..], ilOffset);
	}

	/// <summary>
	/// Takes an <c>@IL_001f</c> suffix off the end, leaving the method name.
	/// <para>
	/// Anchored past the last dot so a type cannot be mistaken for one. A compiler-generated type's
	/// name can contain almost anything, but the method is always the last dotted segment, so a
	/// marker after it belongs to the method and a marker before it is part of a name.
	/// </para>
	/// <para>
	/// A marker that is there and unreadable is refused rather than dropped: breaking at the method's
	/// start instead of where somebody pointed is a stop in the wrong place reported as a success,
	/// which is the one outcome worth failing a call over. So the digits are hexadecimal and nothing
	/// else -- <c>HexNumber</c> would allow surrounding space, and any style at all reads
	/// <c>ffffffff</c> as minus one, which is not an instruction in any method.
	/// </para>
	/// </summary>
	private static (string Name, int? IlOffset) SplitOffset(string spec)
	{
		var at = spec.LastIndexOf(OffsetMarker, StringComparison.OrdinalIgnoreCase);
		if (at <= 0 || at < spec.LastIndexOf('.')) return (spec, null);

		var digits = spec[(at + OffsetMarker.Length)..];
		var read = int.TryParse(digits, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var offset);
		if (!read || offset < 0)
		{
			throw new ArgumentException(
				$"Expected a hexadecimal instruction offset after {OffsetMarker}, as in @IL_001f, got '{digits}'.",
				nameof(spec));
		}

		return (spec[..at], offset);
	}
}
