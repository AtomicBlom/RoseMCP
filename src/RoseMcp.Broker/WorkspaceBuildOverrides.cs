namespace RoseMcp.Broker;

/// <summary>
/// MSBuild properties to start a worker under, when the ones it would choose for itself are not the
/// ones wanted.
/// <para>
/// A worker cannot change these without restarting: MSBuild global properties are fixed when the
/// workspace opens, and the design-time build has already run by the time anyone could ask. So they
/// travel on the command line, and changing them is a reload rather than a setting.
/// </para>
/// </summary>
public sealed record WorkspaceBuildOverrides
{
	public string? Configuration { get; init; }

	public string? Platform { get; init; }

	/// <summary>Further properties as <c>Name=Value</c>, for the cases neither of the above covers.</summary>
	public IReadOnlyList<string> Properties { get; init; } = [];

	/// <summary>Null when nothing was asked for, so the worker starts exactly as it would have.</summary>
	public static WorkspaceBuildOverrides? From(string? configuration, string? platform, string[]? properties)
	{
		var wanted = !string.IsNullOrWhiteSpace(configuration)
			|| !string.IsNullOrWhiteSpace(platform)
			|| properties is { Length: > 0 };

		return wanted
			? new WorkspaceBuildOverrides
			{
				Configuration = configuration,
				Platform = platform,
				Properties = properties ?? [],
			}
			: null;
	}
}
