namespace RoseMcp.Worker;

/// <summary>
/// Which analyzer config files a project's build finds for itself by walking up from its sources: an
/// .editorconfig in each directory above one, and a .globalconfig the same way. Each is a property a project
/// can turn off, and a project that turns one off reads none of that kind however many are on disk.
/// </summary>
[Flags]
public enum ConfigDiscovery
{
	/// <summary>Neither kind is discovered.</summary>
	None = 0,

	/// <summary>.editorconfig files are discovered, unless <c>DiscoverEditorConfigFiles</c> is false.</summary>
	EditorConfig = 1,

	/// <summary>.globalconfig files are discovered, unless <c>DiscoverGlobalAnalyzerConfigFiles</c> is false.</summary>
	GlobalConfig = 2,

	/// <summary>Both, which is what a project that says nothing gets.</summary>
	Both = EditorConfig | GlobalConfig,
}
