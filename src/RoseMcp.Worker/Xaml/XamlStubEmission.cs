namespace RoseMcp.Worker.Xaml;

/// <summary>What the emitter made of one XAML file, including the reasons it did less than asked.</summary>
public sealed record XamlStubEmission
{
	/// <summary>Null when nothing was emitted; <see cref="SkipReason"/> then says why.</summary>
	public required string? HintName { get; init; }

	public required string? Source { get; init; }

	/// <summary>Null when a stub was emitted. Set when the file needed none, or could not have one.</summary>
	public required string? SkipReason { get; init; }

	/// <summary>
	/// Element types that did not resolve in this project, as "Name: element". Their fields are
	/// omitted rather than faked, so the caller can see exactly what will not bind and why.
	/// </summary>
	public required IReadOnlyList<string> UnresolvedTypes { get; init; }

	public static XamlStubEmission Skipped(string reason) => new()
	{
		HintName = null,
		Source = null,
		SkipReason = reason,
		UnresolvedTypes = [],
	};
}
