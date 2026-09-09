// RoseMcp.Xaml.Uwp.Tap: a XAML Diagnostics provider (TAP) injected into a running UWP app.
//
// Named for the XAML framework it binds to rather than the app model: everything here is
// Windows.UI.Xaml, which classic and modern UWP both use. WinUI 3 is Microsoft.UI.Xaml, a different
// dll to initialise and a different set of projections, so it earns a sibling rather than a flag.
//
// InitializeXamlDiagnosticsEx (in Windows.UI.Xaml.dll), called from the live-app host, loads this DLL
// into the target and hands the COM object below the live XAML diagnostics site. From that site we
// enumerate the visual tree via AdviseVisualTreeChange (synchronous callbacks on the app's UI thread,
// the only thread XAML may be touched from), write a snapshot of it for the host to read, and apply
// any hot-reload commands the host left for us.
//
// The provider runs in the app's AppContainer, which cannot read Program Files or write arbitrary
// paths, so its DLL, the files it reads (commands.tsv), and the ones it writes (tree.tsv, its log)
// all live in a working folder the host granted the package rights to. That folder's path arrives as
// the diagnostics initialization data.
//
// Everything the host and provider exchange goes through that folder as tab-separated files. This is
// the seed of the session channel (#2): a snapshot request/response today, a longer-lived protocol
// later. Values with tabs or newlines are escaped so a row is always one line of fixed columns.
//
// What remains in this file is only what is genuinely UWP: the identity, the Windows.UI.Xaml
// projections, the six aliases bound to them, and the class id. Everything else moved to
// ../RoseMcp.Xaml.Tap, which the WinUI 3 provider includes the same way against its own aliases
// (#73). The include order below is load-bearing, and each step says why.

// Identity, before the channel that logs through it. Two providers may serve one machine and write
// adjacent work folders, so the log has to name which of them wrote it.
static const wchar_t* const RoseTapName = L"RoseMcp.Xaml.Uwp.Tap";
static const wchar_t* const RoseTapLogFile = L"\\rosemcp.xaml.uwp.tap.log";

// The framework-free channel, then the xamlOM ABI layer. Both before the projections, which the
// original required and the comment below still does.
#include "../RoseMcp.Xaml.Tap/tap_channel.h"
#include "../RoseMcp.Xaml.Tap/tap_diagnostics.h"

// C++/WinRT projections, for the resident in-app toolbar (#18): build the overlay on the diagnostics
// UI layer, hit-test the element under a click, and report it. Included after the ABI headers above;
// the two live in separate namespaces.
#include <winrt/Windows.Foundation.h>
#include <winrt/Windows.Foundation.Collections.h>
#include <winrt/Windows.UI.h>
#include <winrt/Windows.UI.Xaml.h>
#include <winrt/Windows.UI.Xaml.Controls.h>
#include <winrt/Windows.UI.Xaml.Controls.Primitives.h> // ButtonBase::Click, or it is "auto before defined"
#include <winrt/Windows.UI.Xaml.Media.h>
#include <winrt/Windows.UI.Xaml.Media.Animation.h> // Storyboard and DoubleAnimation, for the proximity fades
#include <winrt/Windows.UI.Xaml.Shapes.h> // Rectangle and Path, for the outlines and the mark
#include <winrt/Windows.UI.Xaml.Media.Imaging.h> // RenderTargetBitmap and WriteableBitmap, for the magnifier
#include <winrt/Windows.Storage.Streams.h>       // IBuffer, which is how pixels come back
#include <robuffer.h> // IBufferByteAccess: the only way to write into a WriteableBitmap's buffer
#include <winrt/Windows.UI.Core.h>        // WindowSizeChangedEventArgs
#include <winrt/Windows.UI.Input.h>
#include <winrt/Windows.UI.Xaml.Input.h>

namespace xaml = winrt::Windows::UI::Xaml;
namespace xcontrols = winrt::Windows::UI::Xaml::Controls;
namespace xmedia = winrt::Windows::UI::Xaml::Media;
namespace xanim = winrt::Windows::UI::Xaml::Media::Animation;
namespace xinput = winrt::Windows::UI::Xaml::Input;
namespace xshapes = winrt::Windows::UI::Xaml::Shapes;
namespace ximaging = winrt::Windows::UI::Xaml::Media::Imaging;

// The same root as a string, for the two places that compare a CLR type name rather than a type.
// The live tree reports names, not types, so a namespace alias cannot help there -- and a literal
// spelled one framework's way reads back empty on the other, which looks like a framework quirk
// rather than a wrong comparison.
#define RoseTapXamlRoot L"Windows.UI.Xaml."

// UWP has one window, so IXamlDiagnostics::GetUiLayer() names the only layer there is and the
// per-XamlRoot API WinUI 3 needs does not exist here. Declining is the whole implementation.
static bool RoseTapGetUiLayerForRoot(IXamlDiagnostics*, InstanceHandle, ::IInspectable**)
{
	return false;
}

// {7b9e5c10-2d4a-4f3b-9e21-a1b2c3d4e5f6}
static const CLSID CLSID_RoseTap =
{ 0x7b9e5c10, 0x2d4a, 0x4f3b, { 0x9e, 0x21, 0xa1, 0xb2, 0xc3, 0xd4, 0xe5, 0xf6 } };

// The overlay's one genuine seam (#75): a passive observer of pointer movement, used for the
// proximity fade that gets the marks out of the way. Two properties are required, and CoreWindow has
// both -- it sees every move before XAML routes it, and it consumes nothing, so select mode never
// takes input away from the app underneath.
//
// WinUI 3 has no CoreWindow, and its InputPointerSource was measured against exactly these two
// properties rather than assumed equivalent, because the issue proposing the port doubted it. On a
// WinUI 3 app, sweeping 41 moves over plain content and then 33 over an element whose handler sets
// Handled: the island's source counted all 74, while the app's own root handler counted the 41 and
// none of the 33. So it sees moves the app has already handled, and takes none of them away. The
// WinUI provider (#76) implements this function with it.
//
// Positions arrive in the window's coordinate space, which is the anchor's: the diagnostics UI layer
// is sized to the window, so no transform is needed here. A WinUI implementation reporting island
// coordinates must check that still holds rather than inherit the assumption.
//
// The anchor goes unused under UWP, where the CoreWindow is ambient. It is in the signature because
// WinUI 3 has no ambient window and must reach the content island through an element.
static bool RoseTapWatchPointer(
	xaml::UIElement const& anchor,
	std::function<void(winrt::Windows::Foundation::Point const&)> onMove,
	std::function<void()> onExit)
{
	(void)anchor;

	const auto current = xaml::Window::Current();
	if (!current) return false;

	const auto window = current.CoreWindow();
	if (!window) return false;

	window.PointerMoved(
		[onMove](winrt::Windows::UI::Core::CoreWindow const&, winrt::Windows::UI::Core::PointerEventArgs const& e)
		{
			onMove(e.CurrentPoint().Position());
		});

	window.PointerExited(
		[onExit](winrt::Windows::UI::Core::CoreWindow const&, winrt::Windows::UI::Core::PointerEventArgs const&)
		{
			onExit();
		});

	return true;
}

// Where the tap body runs, and how anything gets onto the UI thread from off it.
//
// Two pieces of work converge here. UWP enumerates the tree inline on whichever thread advises, so
// the body needs no thread of its own (#76) -- unlike WinUI 3, which deadlocks if advised from the
// UI thread. And the pipe reader is a background thread that has to reach the UI thread to touch
// XAML at all (#50). The dispatcher comes from IXamlDiagnostics::GetDispatcher, which is a xamlOM
// method rather than a framework one, so the shared half asks for it and only the cast is here: a
// CoreDispatcher on UWP, a DispatcherQueue on WinUI 3.
//
// The contract the shared half relies on: RunTapBody may return before the body has finished, and
// RunOnUiThread may not.
static winrt::Windows::UI::Core::CoreDispatcher g_dispatcher{ nullptr };
static DWORD g_uiThreadId = 0;

static void RoseTapCaptureDispatcher(::IInspectable* raw)
{
	// The siting thread, recorded whether or not a dispatcher came with it: SetSite runs on the UI
	// thread on both frameworks, so this is how the injected path knows it is already there.
	g_uiThreadId = ::GetCurrentThreadId();

	if (!raw) return;

	winrt::Windows::Foundation::IInspectable dispatcher{ nullptr };
	winrt::attach_abi(dispatcher, raw); // adopt the ref
	if (!g_dispatcher) g_dispatcher = dispatcher.try_as<winrt::Windows::UI::Core::CoreDispatcher>();

	Log(g_dispatcher ? L"SetSite: holding the UI dispatcher" : L"SetSite: no CoreDispatcher available");
}

// Posted to the UI thread, so SetSite returns at once. Posted rather than run on a thread of its own,
// which is the part that is not a preference.
//
// SetSite is called inline from inside InitializeXamlDiagnosticsEx: a blocking cross-process call
// served by the app's UI thread, reached while our own DLL is being brought into the process. Running
// the body here holds that call open for as long as the walk and the toolbar take, and everything the
// app has queued waits behind it. Starting a thread to escape that asks the loader for a lock the
// injecting thread may be holding, and a loader deadlock does not fail, it stops -- taking the UI
// thread with it, which is the whole of the app.
//
// Posting costs neither. It returns immediately, and the body runs on the next pump, by which time the
// injection call has returned. The work still happens on the UI thread, which on UWP is where it has to
// happen: the enumeration arrives on whichever thread asks for it, and asking from anywhere else puts a
// second writer on the node list.
//
// A target that gives no dispatcher is served inline, as before. That is worse for the app and is the
// only thing left that can be done for it.
static void RoseTapRunTapBody(std::function<void()> body)
{
	if (!g_dispatcher)
	{
		Log(L"SetSite: no dispatcher to post the tap body to, so it runs inside the injection call");
		body();
		return;
	}

	g_dispatcher.RunAsync(
		winrt::Windows::UI::Core::CoreDispatcherPriority::Normal,
		[body = std::move(body)]() { body(); });
}

// Blocking on the async action is safe off the UI thread and only there; on it, it would throw, and
// the thread-id check above means it is never reached from there.
static bool RoseTapRunOnUiThread(const std::function<void()>& work)
{
	if (::GetCurrentThreadId() == g_uiThreadId)
	{
		work();
		return true;
	}

	if (!g_dispatcher) return false;

	try
	{
		g_dispatcher.RunAsync(
			winrt::Windows::UI::Core::CoreDispatcherPriority::Normal,
			[&work]() { work(); }).get();
		return true;
	}
	catch (...)
	{
		// A dispatcher whose window has gone, most likely. The caller answers with an empty frame,
		// which the host reports rather than hanging on.
		return false;
	}
}

// The tree walk is asked for from the UI thread here, because UWP enumerates inline on whichever
// thread asks.
//
// Advising from anywhere else would deliver OnVisualTreeChange there while the app goes on delivering
// live mutations to the same handler on the UI thread. Two writers to one node list is a data race, and
// an intermittent one, which is the shape that survives a test suite. The body is already on the UI
// thread by the time this runs, so it resolves inline; it goes through the dispatcher anyway for the
// one path that has no dispatcher to post to. WinUI 3 cannot do this and must advise off the UI thread,
// which is why this is a seam and not a rule.
static bool RoseTapRunWalk(const std::function<void()>& walk)
{
	return RoseTapRunOnUiThread(walk);
}

// The two layers that need the aliases and the class id above: the overlay is written against the
// six aliases, and the COM object needs CLSID_RoseTap to answer DllGetClassObject.
#include "../RoseMcp.Xaml.Tap/tap_overlay.h"
#include "../RoseMcp.Xaml.Tap/tap_object.h"
