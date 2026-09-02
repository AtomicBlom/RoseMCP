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

	/// <summary>Debug mode on a package: no suspension, no activation timeout, no termination.</summary>
	public static void EnableDebugging(string packageFullName)
	{
		var settings = CreateComInstance<IPackageDebugSettings>(ClsidPackageDebugSettings);
		settings.EnableDebugging(packageFullName, null, IntPtr.Zero);
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
