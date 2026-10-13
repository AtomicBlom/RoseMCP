using System.Xml.Linq;

using RoseMcp.Contracts;

namespace RoseMcp.Broker;

/// <summary>
/// The rules that turn a package's manifest into the apps a person can launch from it, apart from the Win32 that
/// finds the packages, so a test can reach them on any platform.
/// </summary>
public static class InstalledApps
{
	/// <summary>
	/// A package's family name, from its full name: <c>Name_Version_Architecture_ResourceId_PublisherId</c> to
	/// <c>Name_PublisherId</c>; null for anything not in that shape.
	/// </summary>
	public static string? FamilyOf(string packageFullName)
	{
		var parts = packageFullName.Split('_');

		return parts.Length == 5 && parts[0].Length > 0 && parts[4].Length > 0 ? $"{parts[0]}_{parts[4]}" : null;
	}

	/// <summary>One app a manifest declares, before its display name is resolved.</summary>
	/// <param name="Id">The app's id within its package, the part of an application user-model id after the '!'.</param>
	/// <param name="DisplayName">The name its visual elements give it, which may be an <c>ms-resource:</c> reference.</param>
	public sealed record ManifestApp(string Id, string? DisplayName);

	/// <summary>What a manifest says of its package and the apps in it that a person can start.</summary>
	/// <param name="IdentityName">The package's name, which a resource reference is resolved within.</param>
	/// <param name="DisplayName">The package's own display name, which may be an <c>ms-resource:</c> reference.</param>
	/// <param name="Apps">The apps it declares, but those it keeps out of the Start menu.</param>
	public sealed record Manifest(string IdentityName, string? DisplayName, IReadOnlyList<ManifestApp> Apps);

	/// <summary>
	/// Reads a package manifest: its identity's name, its display name, and each application it declares but those
	/// whose visual elements say <c>AppListEntry="none"</c>, which are a package's helpers rather than apps a person
	/// starts. Matched by local name, because the namespaces each schema revision adds are not what is being asked.
	/// </summary>
	/// <exception cref="System.Xml.XmlException">The manifest is not XML.</exception>
	public static Manifest Read(string manifestXml)
	{
		var document = XDocument.Parse(manifestXml);
		var root = document.Root;

		var identity = Child(root, "Identity")?.Attribute("Name")?.Value ?? string.Empty;
		var displayName = Child(Child(root, "Properties"), "DisplayName")?.Value;

		var apps = new List<ManifestApp>();
		foreach (var application in Child(root, "Applications")?.Elements().Where(element => element.Name.LocalName == "Application") ?? [])
		{
			var id = application.Attribute("Id")?.Value;
			if (string.IsNullOrWhiteSpace(id)) continue;

			var visual = Child(application, "VisualElements");
			var hidden = string.Equals(visual?.Attribute("AppListEntry")?.Value, "none", StringComparison.OrdinalIgnoreCase);
			if (hidden) continue;

			apps.Add(new ManifestApp(id, visual?.Attribute("DisplayName")?.Value));
		}

		return new Manifest(identity, displayName, apps);
	}

	/// <summary>
	/// The indirect strings to try for a display name that is an <c>ms-resource:</c> reference, best first, in the
	/// form <c>SHLoadIndirectString</c> takes; empty for a name that is not one. A bare key is a string in the
	/// package's <c>Resources</c> map, which is where a manifest's own strings live; a path is resolved as written.
	/// </summary>
	public static IReadOnlyList<string> ResourceCandidates(string? displayName, string identityName, string packageFullName)
	{
		const string Scheme = "ms-resource:";

		if (displayName is null || !displayName.StartsWith(Scheme, StringComparison.OrdinalIgnoreCase)) return [];

		var reference = displayName[Scheme.Length..];
		if (reference.StartsWith("//", StringComparison.Ordinal)) return [$"@{{{packageFullName}?{displayName}}}"];

		var path = reference.TrimStart('/');
		var candidates = new List<string>();

		if (!path.Contains('/', StringComparison.Ordinal)) candidates.Add($"ms-resource://{identityName}/resources/{path}");
		candidates.Add($"ms-resource://{identityName}/{path}");

		return [.. candidates.Select(uri => $"@{{{packageFullName}?{uri}}}")];
	}

	/// <summary>Whether a display name still needs resolving, so a list never shows a reference as though it were a name.</summary>
	public static bool IsReference(string? displayName) =>
		displayName?.StartsWith("ms-resource:", StringComparison.OrdinalIgnoreCase) == true;

	/// <summary>Those in development mode first, then by name, then by id.</summary>
	public static IReadOnlyList<InstalledApp> Ordered(IEnumerable<InstalledApp> apps) =>
	[
		.. apps
			.OrderByDescending(app => app.DevelopmentMode)
			.ThenBy(app => app.DisplayName, StringComparer.CurrentCultureIgnoreCase)
			.ThenBy(app => app.AppUserModelId, StringComparer.OrdinalIgnoreCase),
	];

	private static XElement? Child(XElement? parent, string localName) =>
		parent?.Elements().FirstOrDefault(element => element.Name.LocalName == localName);
}
