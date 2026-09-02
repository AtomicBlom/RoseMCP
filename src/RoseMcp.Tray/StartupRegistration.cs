using Microsoft.Win32;

namespace RoseMcp.Tray;

/// <summary>
/// Whether this tray starts with Windows, through the per-user Run key.
/// <para>
/// The Run key rather than an MSIX StartupTask, because this app is deliberately unpackaged --
/// <c>WindowsPackageType=None</c>, so it stays xcopy-runnable -- and a StartupTask needs a package
/// identity it does not have. Per-user rather than machine-wide: it needs no elevation, and a warm
/// Roslyn host holding somebody's solution belongs to that somebody.
/// </para>
/// <para>
/// It matters more here than for most tray apps. The point of the broker is one warm worker per
/// solution shared across every session, and a client that starts before the tray does gets its own
/// workers instead -- so the tray being up first is the difference between the feature working and
/// quietly not.
/// </para>
/// </summary>
public static class StartupRegistration
{
	private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

	/// <summary>The value name under Run. Stable, so toggling twice does not leave two entries.</summary>
	private const string ValueName = "RoseMCP";

	/// <summary>
	/// This executable, quoted. Whatever writes the key writes its own path, so promoting a build to
	/// a new install root and toggling from there is all it takes to move the registration.
	/// </summary>
	public static string? ExecutablePath => Environment.ProcessPath;

	/// <summary>True when Windows will start this exact executable at sign-in.</summary>
	public static bool IsEnabled => Registered() is { } registered
		&& ExecutablePath is { Length: > 0 } path
		&& string.Equals(registered, path, StringComparison.OrdinalIgnoreCase);

	/// <summary>
	/// True when something is registered but it is not this executable -- an install that has moved.
	/// Worth distinguishing from off, because the checkbox would otherwise read as off while Windows
	/// goes on starting a copy from the old path.
	/// </summary>
	public static bool PointsElsewhere => Registered() is { Length: > 0 } registered
		&& ExecutablePath is { Length: > 0 } path
		&& !string.Equals(registered, path, StringComparison.OrdinalIgnoreCase);

	/// <summary>The path Windows currently has, unquoted, or null when there is no registration.</summary>
	public static string? Registered()
	{
		try
		{
			using var key = Registry.CurrentUser.OpenSubKey(RunKey);

			return key?.GetValue(ValueName) is string value ? value.Trim().Trim('"') : null;
		}
		catch (Exception exception) when (exception is System.Security.SecurityException or UnauthorizedAccessException)
		{
			return null;
		}
	}

	/// <summary>
	/// Registers or unregisters this executable. Returns false when the registry refused, which a
	/// caller should show rather than swallow: a toggle that silently does nothing is worse than one
	/// that is not offered.
	/// </summary>
	public static bool Set(bool enabled)
	{
		try
		{
			using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
			if (key is null) return false;

			if (!enabled)
			{
				key.DeleteValue(ValueName, throwOnMissingValue: false);

				return true;
			}

			if (ExecutablePath is not { Length: > 0 } path) return false;

			// Quoted, because the install path may contain spaces and Windows parses this value as a
			// command line.
			key.SetValue(ValueName, $"\"{path}\"", RegistryValueKind.String);

			return true;
		}
		catch (Exception exception) when (exception is System.Security.SecurityException
			or UnauthorizedAccessException or IOException)
		{
			return false;
		}
	}
}
