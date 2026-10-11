namespace RoseMcp.Contracts;

/// <summary>One app of a package installed for this user, as <c>GET /operator/debug/packages</c> lists it: what launching it names.</summary>
public sealed record InstalledApp
{
	/// <summary>The application user-model id, <c>PackageFamilyName!AppId</c>, which a launch takes.</summary>
	public required string AppUserModelId { get; init; }

	/// <summary>What the app calls itself, else its package, else its package's name.</summary>
	public required string DisplayName { get; init; }

	/// <summary>The package family the app belongs to, the part of its id before the <c>!</c>.</summary>
	public required string PackageFamilyName { get; init; }

	/// <summary>Where the package is installed from: a build's layout folder for one registered in development mode.</summary>
	public string? InstallLocation { get; init; }

	/// <summary>
	/// Whether the package was registered from a folder in development mode, which is what a build deploying it does:
	/// the packages a developer debugs, listed first.
	/// </summary>
	public bool DevelopmentMode { get; init; }
}
