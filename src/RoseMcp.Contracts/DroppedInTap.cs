namespace RoseMcp.Contracts;

/// <summary>
/// A tap found in one of RoseMCP's tap folders: what its assembly's <c>RoseMcpTap</c> attribute says,
/// and where the assembly is. Read from metadata; the assembly is never loaded into a RoseMCP process,
/// only into the app it serves.
/// </summary>
public sealed record DroppedInTap
{
	/// <summary>The framework the tap serves, as it reads in an error: <c>Contoso</c>.</summary>
	public required string Name { get; init; }

	/// <summary>The framework's own assembly, which a target it serves has loaded: <c>Contoso.dll</c>. Empty where the tap does not say.</summary>
	public required string Module { get; init; }

	/// <summary>The pipe the framework's diagnostics endpoint listens on, with <c>{pid}</c> for the target's process id.</summary>
	public required string Endpoint { get; init; }

	/// <summary>The type the endpoint starts the tap through, by full name.</summary>
	public required string EntryType { get; init; }

	/// <summary>The protocol version the tap says it speaks. Its greeting is what is checked; this lets a tap that could never be accepted be refused before it is loaded into anything.</summary>
	public required int Protocol { get; init; }

	/// <summary>The tap's assembly, by full path.</summary>
	public required string AssemblyPath { get; init; }
}
