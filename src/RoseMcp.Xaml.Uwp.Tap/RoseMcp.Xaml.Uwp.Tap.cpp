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
#include <winrt/Windows.UI.Core.h>        // WindowSizeChangedEventArgs
#include <winrt/Windows.UI.Input.h>
#include <winrt/Windows.UI.Xaml.Input.h>

namespace xaml = winrt::Windows::UI::Xaml;
namespace xcontrols = winrt::Windows::UI::Xaml::Controls;
namespace xmedia = winrt::Windows::UI::Xaml::Media;
namespace xanim = winrt::Windows::UI::Xaml::Media::Animation;
namespace xinput = winrt::Windows::UI::Xaml::Input;
namespace xshapes = winrt::Windows::UI::Xaml::Shapes;

// {7b9e5c10-2d4a-4f3b-9e21-a1b2c3d4e5f6}
static const CLSID CLSID_RoseTap =
{ 0x7b9e5c10, 0x2d4a, 0x4f3b, { 0x9e, 0x21, 0xa1, 0xb2, 0xc3, 0xd4, 0xe5, 0xf6 } };

// The two layers that need the aliases and the class id above: the overlay is written against the
// six aliases, and the COM object needs CLSID_RoseTap to answer DllGetClassObject.
#include "../RoseMcp.Xaml.Tap/tap_overlay.h"
#include "../RoseMcp.Xaml.Tap/tap_object.h"
