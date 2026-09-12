using System.Runtime.InteropServices;

using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;

using RoseMcp.Ui.Core.Inspector;

namespace RoseMcp.Inspector;

/// <summary>
/// The entry point, written by hand because two things have to happen before a window exists.
/// <para>
/// One inspector per machine. Clicking Inspect on a second session in the tray should bring the
/// window that is open to the front on that session, not open another one beside it -- two windows
/// polling the same broker is twice the traffic to show the same thing, and the person now has to
/// remember which is which. The key has to be claimed before <c>Application.Start</c>, because by
/// the time a generated entry point has built an <c>App</c> the second process has already done the
/// work it should have handed over.
/// </para>
/// <para>
/// And the UI thread needs a synchronization context. Without one an <c>async void</c> handler
/// resumes on a pool thread after its first await, and the next line to touch a XAML object throws
/// a wrong-thread exception from somewhere unrelated to the code that caused it.
/// </para>
/// </summary>
public static class Program
{
	[STAThread]
	private static void Main()
	{
		WinRT.ComWrappersSupport.InitializeComWrappers();

		if (HandedOver(KeyOf(Environment.GetCommandLineArgs().Skip(1).ToArray()))) return;

		Application.Start(initialization =>
		{
			_ = initialization;

			var queue = DispatcherQueue.GetForCurrentThread();
			SynchronizationContext.SetSynchronizationContext(new DispatcherQueueSynchronizationContext(queue));

			_ = new App();
		});
	}

	/// <summary>
	/// Whether this launch handed its activation to an inspector that is already running, in which
	/// case this process has nothing left to do.
	/// <para>
	/// Everything here is inside a try, and failing means carrying on as the primary. Redirection is
	/// the part of app lifecycle least well served for an unpackaged app, and a second window is a
	/// far better outcome than a process that exits without one.
	/// </para>
	/// </summary>
	private static bool HandedOver(string key)
	{
		try
		{
			var running = AppInstance.FindOrRegisterForKey(key);
			if (running.IsCurrent) return false;

			Redirect(running, AppInstance.GetCurrent().GetActivatedEventArgs());
			return true;
		}
		catch (Exception exception)
		{
			System.Diagnostics.Debug.WriteLine($"Single-instance registration failed, carrying on: {exception.Message}");
			return false;
		}
	}

	/// <summary>
	/// Hands this launch's arguments to the instance already running, and waits for it to take them.
	/// <para>
	/// The wait cannot be a plain <c>Wait()</c>. This is an STA thread and redirection completes
	/// through a COM call back into it, so blocking it deadlocks -- the classic symptom being a
	/// second launch that never exits and never focuses anything. <c>CoWaitForMultipleObjects</c>
	/// waits while pumping, which is what lets that call arrive.
	/// </para>
	/// </summary>
	private static void Redirect(AppInstance running, AppActivationArguments arguments)
	{
		using var handed = new ManualResetEvent(initialState: false);

		_ = Task.Run(async () =>
		{
			try
			{
				await running.RedirectActivationToAsync(arguments);
			}
			finally
			{
				handed.Set();
			}
		});

		var handle = handed.SafeWaitHandle.DangerousGetHandle();
		_ = CoWaitForMultipleObjects(CoWaitAlertable | CoWaitInputAvailable, HandOverMilliseconds, 1, [handle], out _);
	}

	/// <summary>
	/// What this launch's single instance is keyed on: the process it is going to debug.
	/// <para>
	/// Read here rather than taken from the parsed options, because the key has to be claimed
	/// before <c>Application.Start</c> and the options are not read until the App is constructed.
	/// A command line that does not parse falls back to the app-wide key, which is the old
	/// behaviour and is no worse than refusing to start.
	/// </para>
	/// </summary>
	private static string KeyOf(string[] arguments)
	{
		try
		{
			return InspectorOptions.Parse(arguments).InstanceKey;
		}
		catch (ArgumentException)
		{
			return "RoseMcp.Inspector";
		}
	}

	/// <summary>
	/// How long to wait for the running instance to take the activation before giving up and
	/// exiting anyway. An instance that is wedged must not leave a second process waiting on it
	/// forever with no window to show for it.
	/// </summary>
	private const uint HandOverMilliseconds = 5000;

	private const uint CoWaitAlertable = 0x2;
	private const uint CoWaitInputAvailable = 0x4;

	[DllImport("ole32.dll")]
	private static extern uint CoWaitForMultipleObjects(
		uint flags,
		uint timeout,
		ulong count,
		nint[] handles,
		out uint index);
}
