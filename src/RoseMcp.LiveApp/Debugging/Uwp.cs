using System.Runtime.InteropServices;

namespace RoseMcp.LiveApp.Debugging;

/// <summary>
/// The UWP-specific hop a plain .NET attach does not need: put the package into debug mode so the
/// system will not suspend or time it out, then activate it and get the pid. These are the same shell
/// COM interfaces plmdebug.exe and Visual Studio use -- <c>IPackageDebugSettings</c> and
/// <c>IApplicationActivationManager</c> -- declared only as far as the methods used here.
/// </summary>
internal static class Uwp
{
	private static readonly Guid ClsidPackageDebugSettings = new("B1AEC16F-2383-4852-B0E9-8F0B1DC66B4D");
	private static readonly Guid ClsidApplicationActivationManager = new("45BA127D-10A8-46EA-8AB7-56EA9078943C");

	/// <summary>
	/// The environment the app is activated with, as a Win32 multi-string.
	/// <para>
	/// One variable, and it is the one that turns XAML source info on. Without it every element and
	/// every property comes back with an empty file and line -- which is not merely a missing nicety:
	/// it is the difference between "this property was set at MainPage.xaml:41" and a blank that is
	/// indistinguishable from "not set in source". It is also the only exact way to tell an element
	/// the developer wrote from a part of a control template, which is what a "just my XAML" view is.
	/// </para>
	/// <para>
	/// This slot was passed as null while the source-info gap was carried as a known limitation, and
	/// the limitation was simply this argument going unused.
	/// </para>
	/// </summary>
	private const string ActivationEnvironment = "ENABLE_XAML_DIAGNOSTICS_SOURCE_INFO=1\0\0";

	/// <summary>Debug mode on a package: no suspension, no activation timeout, no termination.</summary>
	public static void EnableDebugging(string packageFullName)
		=> EnableDebugging(packageFullName, null);

	/// <summary>
	/// Debug mode with a registered debugger command line (issue #5, from birth): on the next
	/// activation the system creates the app suspended and launches this command line as the app's
	/// debugger, appending <c>-p &lt;pid&gt; -tid &lt;tid&gt;</c>. The command line has a length limit
	/// around 256 characters, so the caller keeps it short.
	/// </summary>
	public static void EnableDebugging(string packageFullName, string? debuggerCommandLine)
	{
		var settings = CreateComInstance<IPackageDebugSettings>(ClsidPackageDebugSettings);

		// The environment block has to outlive the call, so it is pinned rather than marshalled by
		// the runtime: the signature takes an IntPtr precisely so the lifetime is explicit here.
		var environment = Marshal.StringToHGlobalUni(ActivationEnvironment);
		try
		{
			settings.EnableDebugging(packageFullName, debuggerCommandLine, environment);
		}
		finally
		{
			Marshal.FreeHGlobal(environment);
		}
	}

	public static void DisableDebugging(string packageFullName)
	{
		var settings = CreateComInstance<IPackageDebugSettings>(ClsidPackageDebugSettings);
		settings.DisableDebugging(packageFullName);
	}

	/// <summary>Activate an app by its AUMID and return the pid the system assigned it.</summary>
	public static int ActivateApplication(string appUserModelId)
	{
		var manager = CreateComInstance<IApplicationActivationManager>(ClsidApplicationActivationManager);
		manager.ActivateApplication(appUserModelId, null, ActivateOptions.None, out var pid);
		return (int)pid;
	}

	/// <summary>Resolve a package's full name (with version and hash) from its family name.</summary>
	public static string ResolvePackageFullName(string packageFamilyName)
	{
		uint count = 0;
		uint bufferLength = 0;
		var hr = FindPackagesByPackageFamily(packageFamilyName, PackageFilterHead, ref count, IntPtr.Zero, ref bufferLength, IntPtr.Zero, IntPtr.Zero);
		if (hr != ErrorInsufficientBuffer)
		{
			throw new InvalidOperationException($"FindPackagesByPackageFamily sizing for '{packageFamilyName}' returned {hr}.");
		}

		if (count == 0)
		{
			throw new InvalidOperationException($"No package is registered for family '{packageFamilyName}'. Is the app deployed?");
		}

		var names = new IntPtr[count];
		var buffer = new char[bufferLength];
		var properties = new uint[count];
		var namesHandle = GCHandle.Alloc(names, GCHandleType.Pinned);
		var bufferHandle = GCHandle.Alloc(buffer, GCHandleType.Pinned);
		var propsHandle = GCHandle.Alloc(properties, GCHandleType.Pinned);
		try
		{
			hr = FindPackagesByPackageFamily(packageFamilyName, PackageFilterHead, ref count, namesHandle.AddrOfPinnedObject(), ref bufferLength, bufferHandle.AddrOfPinnedObject(), propsHandle.AddrOfPinnedObject());
			if (hr != 0)
			{
				throw new InvalidOperationException($"FindPackagesByPackageFamily for '{packageFamilyName}' returned {hr}.");
			}

			// One head package per family; take the first full name.
			return Marshal.PtrToStringUni(names[0])!;
		}
		finally
		{
			namesHandle.Free();
			bufferHandle.Free();
			propsHandle.Free();
		}
	}

	private static T CreateComInstance<T>(Guid clsid)
	{
		var type = Type.GetTypeFromCLSID(clsid, throwOnError: true)!;
		return (T)Activator.CreateInstance(type)!;
	}

	private const uint PackageFilterHead = 0x00000010;
	private const int ErrorInsufficientBuffer = 122;

	[DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = false)]
	private static extern int FindPackagesByPackageFamily(
		string packageFamilyName,
		uint packageFilters,
		ref uint count,
		IntPtr packageFullNames,
		ref uint bufferLength,
		IntPtr buffer,
		IntPtr packageProperties);

	private enum ActivateOptions
	{
		None = 0,
		DesignMode = 1,
		NoErrorUI = 2,
		NoSplashScreen = 4,
	}

	[ComImport]
	[Guid("F27C3930-8029-4AD1-94E3-3DBA417810C1")]
	[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
	private interface IPackageDebugSettings
	{
		// Only the first two vtable slots are declared; nothing below them is called.
		void EnableDebugging(
			[MarshalAs(UnmanagedType.LPWStr)] string packageFullName,
			[MarshalAs(UnmanagedType.LPWStr)] string? debuggerCommandLine,
			IntPtr environment);

		void DisableDebugging([MarshalAs(UnmanagedType.LPWStr)] string packageFullName);
	}

	[ComImport]
	[Guid("2E941141-7F97-4756-BA1D-9DECDE894A3D")]
	[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
	private interface IApplicationActivationManager
	{
		// ActivateApplication is the first slot; ActivateForFile/Protocol follow and are not declared.
		void ActivateApplication(
			[MarshalAs(UnmanagedType.LPWStr)] string appUserModelId,
			[MarshalAs(UnmanagedType.LPWStr)] string? arguments,
			ActivateOptions options,
			out uint processId);
	}
}
