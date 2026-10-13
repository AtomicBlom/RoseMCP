using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

using Microsoft.Win32;

using RoseMcp.Contracts;

namespace RoseMcp.Broker;

/// <summary>
/// The packaged apps installed for this user, read from Windows: the package repository's list of this user's
/// packages, each family's current package with its properties, and each package's manifest.
/// <para>
/// The repository key is the one list of a user's packages Win32 offers without the WinRT package manager, which the
/// broker cannot reach from a plain .NET target; everything read for a package after its name is a documented call
/// -- <c>FindPackagesByPackageFamily</c> for its properties and <c>GetPackagePathByFullName</c> for its manifest.
/// Frameworks, resource packages and optional packages are left out: none is something a person launches.
/// </para>
/// <para>
/// What a package's manifest resolves to is kept by its full name, which a new version changes, because resolving a
/// resource string loads the package's resource index and a list of a few hundred packages would otherwise pay that
/// on every read. Off Windows the list is empty.
/// </para>
/// </summary>
public sealed class WindowsInstalledApps : IInstalledApps
{
	private const string Repository = @"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\Repository\Packages";

	private const uint PackageFilterHead = 0x00000010;
	private const uint PackagePropertyFramework = 0x00000001;
	private const uint PackagePropertyResource = 0x00000002;
	private const uint PackagePropertyOptional = 0x00000008;
	private const uint PackagePropertyDevelopmentMode = 0x00010000;
	private const int ErrorInsufficientBuffer = 122;

	private readonly ConcurrentDictionary<string, IReadOnlyList<InstalledApp>> _byFullName = new(StringComparer.OrdinalIgnoreCase);

	/// <inheritdoc />
	public IReadOnlyList<InstalledApp> Read()
	{
		if (!OperatingSystem.IsWindows()) return [];

		try
		{
			return ReadWindows();
		}
		catch (Exception)
		{
			// Best effort, like the running list: what could not be read is not listed.
			return [];
		}
	}

	[SupportedOSPlatform("windows")]
	private IReadOnlyList<InstalledApp> ReadWindows()
	{
		var families = Families();
		var apps = new List<InstalledApp>();

		foreach (var family in families)
		{
			try
			{
				foreach (var (fullName, properties) in CurrentPackages(family))
				{
					var isLaunchable = (properties & (PackagePropertyFramework | PackagePropertyResource | PackagePropertyOptional)) == 0;
					if (!isLaunchable) continue;

					var developmentMode = (properties & PackagePropertyDevelopmentMode) != 0 || FromADeveloper(fullName);
					apps.AddRange(_byFullName.GetOrAdd(fullName, name => AppsOf(name, family, developmentMode)));
				}
			}
			catch (Exception exception) when (exception is not OutOfMemoryException)
			{
				// One package that cannot be read is left out, rather than taking every other package with it.
			}
		}

		return InstalledApps.Ordered(apps);
	}

	/// <summary>Every package family this user has a package of, from the package repository's key.</summary>
	[SupportedOSPlatform("windows")]
	private static IReadOnlyList<string> Families()
	{
		using var packages = Registry.CurrentUser.OpenSubKey(Repository);
		if (packages is null) return [];

		return
		[
			.. packages.GetSubKeyNames()
				.Select(InstalledApps.FamilyOf)
				.OfType<string>()
				.Distinct(StringComparer.OrdinalIgnoreCase),
		];
	}

	/// <summary>The family's current packages, each with its <c>PACKAGE_PROPERTY_*</c> flags.</summary>
	[SupportedOSPlatform("windows")]
	private static List<(string FullName, uint Properties)> CurrentPackages(string family)
	{
		uint count = 0;
		uint bufferLength = 0;

		var result = FindPackagesByPackageFamily(family, PackageFilterHead, ref count, IntPtr.Zero, ref bufferLength, IntPtr.Zero, IntPtr.Zero);
		if (result != ErrorInsufficientBuffer || count == 0) return [];

		var names = Marshal.AllocHGlobal((int)count * IntPtr.Size);
		var buffer = Marshal.AllocHGlobal((int)bufferLength * sizeof(char));
		var properties = Marshal.AllocHGlobal((int)count * sizeof(uint));

		try
		{
			result = FindPackagesByPackageFamily(family, PackageFilterHead, ref count, names, ref bufferLength, buffer, properties);
			if (result != 0) return [];

			var packages = new List<(string, uint)>((int)count);
			for (var index = 0; index < count; index++)
			{
				var name = Marshal.PtrToStringUni(Marshal.ReadIntPtr(names, index * IntPtr.Size));
				var flags = (uint)Marshal.ReadInt32(properties, index * sizeof(uint));

				if (name is not null) packages.Add((name, flags));
			}

			return packages;
		}
		finally
		{
			Marshal.FreeHGlobal(names);
			Marshal.FreeHGlobal(buffer);
			Marshal.FreeHGlobal(properties);
		}
	}

	/// <summary>The apps one package declares, named as they would be in the Start menu; none where its manifest cannot be read.</summary>
	[SupportedOSPlatform("windows")]
	private static IReadOnlyList<InstalledApp> AppsOf(string fullName, string family, bool developmentMode)
	{
		var location = PackagePath(fullName);
		if (location is null) return [];

		InstalledApps.Manifest manifest;
		try
		{
			manifest = InstalledApps.Read(File.ReadAllText(Path.Combine(location, "AppxManifest.xml")));
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Xml.XmlException)
		{
			return [];
		}

		var packageName = Resolved(manifest.DisplayName, manifest, fullName) ?? manifest.IdentityName;

		return
		[
			.. manifest.Apps.Select(app => new InstalledApp
			{
				AppUserModelId = $"{family}!{app.Id}",
				DisplayName = Resolved(app.DisplayName, manifest, fullName) ?? packageName,
				PackageFamilyName = family,
				InstallLocation = location,
				DevelopmentMode = developmentMode,
			}),
		];
	}

	/// <summary>A display name as a person reads it: itself, or the string its resource reference names; null where neither.</summary>
	[SupportedOSPlatform("windows")]
	private static string? Resolved(string? displayName, InstalledApps.Manifest manifest, string fullName)
	{
		if (string.IsNullOrWhiteSpace(displayName)) return null;
		if (!InstalledApps.IsReference(displayName)) return displayName;

		foreach (var candidate in InstalledApps.ResourceCandidates(displayName, manifest.IdentityName, fullName))
		{
			var buffer = new char[512];
			var loaded = SHLoadIndirectString(candidate, buffer, (uint)buffer.Length, IntPtr.Zero) == 0;
			if (!loaded) continue;

			var text = new string(buffer, 0, Array.IndexOf(buffer, '\0') is var end and >= 0 ? end : buffer.Length);
			if (!string.IsNullOrWhiteSpace(text)) return text;
		}

		return null;
	}

	[SupportedOSPlatform("windows")]
	private static string? PackagePath(string fullName)
	{
		uint length = 0;
		var result = GetPackagePathByFullName(fullName, ref length, null);
		if (result != ErrorInsufficientBuffer || length == 0) return null;

		var buffer = new char[length];
		result = GetPackagePathByFullName(fullName, ref length, buffer);

		return result == 0 ? new string(buffer, 0, (int)Math.Max(0, length - 1)) : null;
	}

	/// <summary>
	/// Whether a package was registered from a folder without a signature, which is what deploying a build does. Read
	/// from its origin, because the properties <c>FindPackagesByPackageFamily</c> reports leave development mode unset
	/// for exactly those packages. A signed origin is not taken as development: packages a person installs through App
	/// Installer arrive signed by a developer certificate, and every one of them would be listed first. A Windows
	/// without the call says no, which only orders the list differently.
	/// </summary>
	[SupportedOSPlatform("windows")]
	private static bool FromADeveloper(string fullName)
	{
		try
		{
			return GetStagedPackageOrigin(fullName, out var origin) == 0 && origin == PackageOriginDeveloperUnsigned;
		}
		catch (Exception exception) when (exception is EntryPointNotFoundException or DllNotFoundException)
		{
			return false;
		}
	}

	/// <summary><c>PackageOrigin_DeveloperUnsigned</c>: registered from a folder, unsigned.</summary>
	private const int PackageOriginDeveloperUnsigned = 4;

	// Exported by the app model's API set rather than by kernel32, which forwards most of its siblings but not this.
	[DllImport("api-ms-win-appmodel-runtime-l1-1-1.dll", CharSet = CharSet.Unicode, SetLastError = false)]
	private static extern int GetStagedPackageOrigin(string packageFullName, out int origin);

	[DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = false)]
	private static extern int FindPackagesByPackageFamily(
		string packageFamilyName,
		uint packageFilters,
		ref uint count,
		IntPtr packageFullNames,
		ref uint bufferLength,
		IntPtr buffer,
		IntPtr packageProperties);

	[DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = false)]
	private static extern int GetPackagePathByFullName(string packageFullName, ref uint pathLength, [Out] char[]? path);

	[DllImport("shlwapi.dll", CharSet = CharSet.Unicode, SetLastError = false)]
	private static extern int SHLoadIndirectString(string source, [Out] char[] output, uint outputLength, IntPtr reserved);
}
