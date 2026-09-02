namespace RoseMcp.LiveApp.Debugging;

/// <summary>
/// A method addressed by name, the way a tracepoint or breakpoint is requested before any module has
/// loaded. It carries the assembly (simple name) the method lives in, so binding can wait for exactly
/// that module, plus the declaring type's full name and the method name.
/// </summary>
internal sealed record SymbolLocation(string ModuleSimpleName, string TypeName, string MethodName)
{
	/// <summary>
	/// Two spellings. <c>Namespace.Type.Method</c> guesses the module from the first namespace
	/// segment, which is right when the assembly is named for its root namespace. When it is not,
	/// give the assembly explicitly as <c>Assembly!Namespace.Type.Method</c>.
	/// </summary>
	public static SymbolLocation Parse(string spec)
	{
		if (string.IsNullOrWhiteSpace(spec)) throw new ArgumentException("A location is required.", nameof(spec));

		string? assembly = null;
		var bang = spec.IndexOf('!');
		if (bang > 0)
		{
			assembly = spec[..bang];
			spec = spec[(bang + 1)..];
		}

		var lastDot = spec.LastIndexOf('.');
		var firstDot = spec.IndexOf('.');
		if (lastDot <= 0 || lastDot == spec.Length - 1)
		{
			throw new ArgumentException($"Expected [Assembly!]Namespace.Type.Method, got '{spec}'.", nameof(spec));
		}

		var typeName = spec[..lastDot];
		var methodName = spec[(lastDot + 1)..];
		var moduleName = assembly ?? (firstDot > 0 ? spec[..firstDot] : typeName);

		// Strip only a real assembly extension. A dotted assembly name must not have its last
		// segment mistaken for one.
		if (moduleName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) || moduleName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
		{
			moduleName = moduleName[..^4];
		}

		return new SymbolLocation(moduleName, typeName, methodName);
	}
}
