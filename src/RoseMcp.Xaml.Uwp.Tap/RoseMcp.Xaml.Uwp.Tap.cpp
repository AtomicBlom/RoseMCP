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

#include <windows.h>
#include <unknwn.h>
#include <ocidl.h>

#undef GetCurrentTime

#include <xamlOM.h>

#include <atomic>
#include <string>
#include <vector>
#include <map>
#include <set>
#include <functional>
#include <fstream>
#include <sstream>
#include <mutex>
#include <cstdlib>
#include <cmath>
#include <chrono>

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

static std::atomic<long> g_lockCount{ 0 };
static std::wstring g_workDir;
static std::mutex g_logMutex;

static std::wstring Hex(HRESULT hr)
{
	wchar_t buffer[9];
	swprintf_s(buffer, L"%08x", static_cast<unsigned>(hr));
	return buffer;
}

static std::string Utf8(const std::wstring& text);

// UTF-8, for the same reason the snapshots are: a wofstream narrows to the ANSI code page, so an
// element name or a separator outside it lands in the log as a question mark. A diagnostic file that
// mangles the very names it exists to report is worth the one extra call.
static void Log(const std::wstring& line)
{
	OutputDebugStringW((L"[RoseMcp.Xaml.Uwp.Tap] " + line + L"\n").c_str());

	std::lock_guard<std::mutex> guard(g_logMutex);
	if (g_workDir.empty()) return;

	std::ofstream file(g_workDir + L"\\rosemcp.xaml.uwp.tap.log", std::ios::app | std::ios::binary);
	if (file) file << Utf8(line) << '\n';
}

// The snapshot is UTF-8 so the host reads it with one fixed encoding regardless of the app's locale;
// std::wofstream would narrow to the ANSI code page and lose any non-ASCII name.
static std::string Utf8(const std::wstring& text)
{
	if (text.empty()) return std::string();
	const int size = WideCharToMultiByte(CP_UTF8, 0, text.c_str(), static_cast<int>(text.size()), nullptr, 0, nullptr, nullptr);
	std::string out(static_cast<size_t>(size), '\0');
	WideCharToMultiByte(CP_UTF8, 0, text.c_str(), static_cast<int>(text.size()), out.data(), size, nullptr, nullptr);
	return out;
}

// Where a property's effective value came from -- the bridge from a live value to how it was set.
static std::wstring Provenance(BaseValueSource source)
{
	switch (source)
	{
		case BaseValueSourceDefault: return L"Default";
		case BaseValueSourceBuiltInStyle: return L"BuiltInStyle";
		case BaseValueSourceStyle: return L"Style";
		case BaseValueSourceLocal: return L"Local";
		case Inherited: return L"Inherited";
		case DefaultStyleTrigger: return L"DefaultStyleTrigger";
		case TemplateTrigger: return L"TemplateTrigger";
		case StyleTrigger: return L"StyleTrigger";
		case ImplicitStyleReference: return L"ImplicitStyleReference";
		case ParentTemplate: return L"ParentTemplate";
		case ParentTemplateTrigger: return L"ParentTemplateTrigger";
		case Animation: return L"Animation";
		case Coercion: return L"Coercion";
		case BaseValueSourceVisualState: return L"VisualState";
		default: return L"Unknown";
	}
}

// The UIElement properties backed by a composition Visual. They read as BaseValueSourceLocal the
// moment the framework touches one, whatever the XAML says, so an element whose whole declaration is
// two attributes reported six local sets that do not exist -- crowding out, in that same answer, the
// one property whose absence explained why the element was not hit-testable.
//
// A fixed list rather than a rule, because that is what this is: these six were added to UIElement
// together and there is no flag distinguishing them. They are still reported when the caller asks for
// defaults, since "everything on this element" is a legitimate question; they are just not evidence
// of what the markup sets, which is what the default view is for.
static bool IsComposition(const wchar_t* propertyName)
{
	if (!propertyName) return false;

	static const wchar_t* const composition[] = {
		L"CenterPoint", L"Rotation", L"RotationAxis", L"Scale", L"TransformMatrix", L"Translation",
	};

	for (const auto* candidate : composition)
	{
		if (wcscmp(propertyName, candidate) == 0) return true;
	}

	return false;
}

// A tab or newline in a type or name would break the row-per-element snapshot; keep every field on
// one line and reversible.
static std::wstring Escape(const wchar_t* text)
{
	std::wstring result;
	if (!text) return result;
	for (const wchar_t* c = text; *c; ++c)
	{
		switch (*c)
		{
			case L'\t': result += L"\\t"; break;
			case L'\r': result += L"\\r"; break;
			case L'\n': result += L"\\n"; break;
			case L'\\': result += L"\\\\"; break;
			default: result += *c; break;
		}
	}

	return result;
}

// One element of the visual tree, captured as it is announced.
struct TreeNode
{
	InstanceHandle Handle;
	InstanceHandle Parent;
	unsigned int ChildIndex;
	std::wstring Type;
	std::wstring Name;

	// Where the element was declared, from VisualElement::SrcInfo -- the field that separates the
	// app's own markup from a control template's parts, and so the basis of "just my XAML".
	std::wstring File;
	unsigned int Line;
	unsigned int Column;
};

// One parsed command line: op TAB target TAB property TAB valueType TAB value
struct Command
{
	std::wstring op;
	std::wstring target;
	std::wstring property;
	std::wstring valueType;
	std::wstring value;
};

// The name the overlay's root carries in the live tree, so the tree snapshot can drop RoseMCP's own
// UI instead of reporting it as part of the app's.
static const wchar_t* const OverlayRootName = L"__RoseMcpOverlay";

// Segoe MDL2 Assets codepoints. Kept named and in one place because they are unreadable inline and a
// wrong one renders as a hollow box rather than failing, so they have to be easy to check and swap --
// and worth checking against the font's own character map, which is how two glyphs that are simply
// absent from Segoe UI were caught before they shipped as boxes.
//
// These follow what Visual Studio's live-tree toolbar does: a plain pointer for the neutral mode, a
// pointer inside a marquee for picking, and a chevron to fold away.
static const wchar_t* const IconIdle = L"\xE8B0";   // Cursor -- a plain arrow pointer
static const wchar_t* const IconHide = L"\xE76B";   // ChevronLeft
static const wchar_t* const IconMyXaml = L"\xE943"; // Code -- braces, for "just my XAML"
static const wchar_t* const IconDeselect = L"\xE711"; // Cancel -- a plain cross, for clearing the pick



// The resident in-app toolbar (#18). Installed on the diagnostics UI layer at the first injection and
// left there for the life of the app, because the point of it is that a person can arm select mode
// themselves and then talk to the agent -- rather than having to ask the agent to arm it first.
//
// Click-through is structural here, not a trick. A XAML panel whose Background is null does not take
// part in hit testing, so the root Grid and the Canvas inside it are invisible to input and every
// click reaches the app underneath; only the toolbar, which does have a Background, takes input.
// Select mode adds a full-bleed capture layer beneath the toolbar, and a Background that is merely
// transparent *does* hit-test, so that layer collects the pick; removing it restores click-through.
// That is the whole mechanism -- no input hooks, no window subclassing, and nothing that could
// collide with a modifier chord the app itself wants to use.
//
// It outlives the RoseTap instance that built it, so it is a leaked singleton rather than a member:
// its event handlers capture `this`, and a `this` that could be deleted at the end of an injection
// would leave the app holding handlers into freed memory. It also keeps its own reference to
// IXamlDiagnostics, which is what lets a click resolve to a handle long after that injection is done.
class RoseOverlay
{
public:
	// Idempotent: the second and later injections find the toolbar already there and leave it alone.
	void Install(IXamlDiagnostics* diagnostics)
	{
		if (m_root || !diagnostics) return;

		try
		{
			::IInspectable* rawLayer = nullptr;
			if (FAILED(diagnostics->GetUiLayer(&rawLayer)) || !rawLayer)
			{
				Log(L"overlay: GetUiLayer returned nothing");
				return;
			}

			winrt::Windows::Foundation::IInspectable layerObject{ nullptr };
			winrt::attach_abi(layerObject, rawLayer); // adopt the ref GetUiLayer returned
			m_layer = layerObject.try_as<xcontrols::Panel>();
			if (!m_layer)
			{
				Log(L"overlay: the UI layer is not a Panel");
				return;
			}

			m_diagnostics = diagnostics;
			m_diagnostics->AddRef();

			Build();
			m_layer.Children().Append(m_root);
			WriteState();

			const auto bounds = xaml::Window::Current().Bounds();
			Log(L"overlay: toolbar installed on a " + std::wstring(winrt::get_class_name(m_layer))
				+ L" UI layer (arranged " + std::to_wstring(static_cast<int>(m_layer.ActualWidth())) + L"x"
				+ std::to_wstring(static_cast<int>(m_layer.ActualHeight())) + L"), window "
				+ std::to_wstring(static_cast<int>(bounds.Width)) + L"x"
				+ std::to_wstring(static_cast<int>(bounds.Height)));
		}
		catch (winrt::hresult_error const& error)
		{
			Log(std::wstring(L"overlay: install failed: ") + error.message().c_str());
			m_root = nullptr;
		}
	}

	bool Installed() const { return static_cast<bool>(m_root); }

	/// Takes the handle-to-source-file map from the tree enumeration, which is the only place it is
	/// available: VisualElement::SrcInfo comes per element as the tree is walked, and the overlay only
	/// ever sees UIElements. Refreshed on every injection, so it is current as of arming.
	void SetSources(std::map<InstanceHandle, std::wstring> sources)
	{
		m_sources = std::move(sources);
	}

	// What the toolbar is currently being used for. An operation in progress pins the panel at full
	// strength: fading the thing somebody is in the middle of using is exactly the wrong moment for
	// it, and proximity is the wrong question to ask then -- during a pick the pointer is out in the
	// app by definition, which is precisely when the toolbar must stay readable.
	//
	// An enum and a set rather than a second look at m_selecting, because Select is the first of
	// these and not the last: Ruler and Zoom are coming, and neither should have to remember to do
	// this. Adding one here is adding one line there.
	enum class Operation
	{
		Select,
	};

	void BeginOperation(Operation operation)
	{
		m_operations.insert(operation);
		RefreshPanelFade();
	}

	void EndOperation(Operation operation)
	{
		m_operations.erase(operation);
		RefreshPanelFade();
	}

	// The panel is legible when it is being used or when the pointer is on it, and a hint otherwise.
	// One place, because the two conditions are independent and either can change without the other.
	void RefreshPanelFade()
	{
		m_panelFade.To(m_operations.empty() && !m_overPanel ? PanelFar : PanelNear);
	}

	// Arms select mode. Returns whether it is armed, so the host can confirm rather than assume --
	// including the case where the person had already armed it from the toolbar.
	bool BeginSelect(bool includeAllElements = false)
	{
		if (!m_root) return false;

		m_includeAllElements = includeAllElements;
		Chrome();
		if (m_selecting)
		{
			WriteState();
			WriteArmed();
			return true;
		}

		try
		{
			m_capture = xcontrols::Grid();

			// A faint wash, not a plain Transparent: this is the "select mode is on" affordance, and a
			// layer that swallows every click while looking like nothing at all is a layer that reads
			// as the app having hung.
			m_capture.Background(Brush(0x14, 0x00, 0x78, 0xD4));

			// Explicit, for the same reason the root is: it has to cover the window, and it cannot get
			// that from an alignment.
			const auto bounds = xaml::Window::Current().Bounds();
			m_capture.Width(bounds.Width);
			m_capture.Height(bounds.Height);
			m_capture.PointerPressed(
				[this](winrt::Windows::Foundation::IInspectable const&, xinput::PointerRoutedEventArgs const& e)
				{
					OnPick(e);
				});

			// Hover feedback is the whole reason this layer takes pointer moves as well as presses:
			// without it there is no evidence the overlay has noticed the pointer at all.
			m_capture.PointerMoved(
				[this](winrt::Windows::Foundation::IInspectable const&, xinput::PointerRoutedEventArgs const& e)
				{
					OnHover(e);
				});
			m_capture.PointerExited(
				[this](winrt::Windows::Foundation::IInspectable const&, xinput::PointerRoutedEventArgs const&)
				{
					ShowBox(m_hoverBox, m_hoverBadge, nullptr, std::wstring());
				});

			// Beneath the Canvas that holds the toolbar, so the toolbar's own buttons stay clickable
			// while the rest of the window is collecting the pick.
			// After arrange, not before: the point of reporting it is to catch the case where XAML gave
			// the layer nothing, and before arrange every layer looks like that.
			m_capture.SizeChanged(
				[this](winrt::Windows::Foundation::IInspectable const&, xaml::SizeChangedEventArgs const&)
				{
					WriteArmed();
				});

			m_root.Children().InsertAt(0, m_capture);
			m_selecting = true;
			BeginOperation(Operation::Select);
			Chrome();
			WriteState();
			Log(L"overlay: select mode armed");
			return true;
		}
		catch (winrt::hresult_error const& error)
		{
			Log(std::wstring(L"overlay: arming select mode failed: ") + error.message().c_str());
			return false;
		}
	}

	/// Whether a pick prefers the element declared in the app's own markup over a control template's
	/// parts. Set from the host or from the toolbar's toggle; the two are the same switch.
	void SetJustMyXaml(bool justMyXaml)
	{
		m_justMyXaml = justMyXaml;
		Chrome();
	}

	// Clears the pick: the mark on screen and the record on disk, which have to go together or
	// this is a lie. Hiding the box alone leaves rose_xaml_selection reporting a selection the
	// person can no longer see; deleting the files alone leaves a mark pointing at nothing.
	//
	// Reachable without arming, which is what was missing. Once something was picked the only
	// ways out were picking something else or restarting the app.
	//
	// Returns whether there was anything to clear, so a caller can tell "cleared" from "nothing
	// was selected" rather than having both read as success.
	bool Deselect()
	{
		const bool had = m_hasSelection;

		ShowBox(m_selectBox, m_selectBadge, nullptr, std::wstring());
		m_hasSelection = false;
		m_overSelection = false;
		m_selectionRect = {};

		if (!g_workDir.empty())
		{
			// Removed rather than emptied. The host waits on selection.ready existing, so a truncated
			// one would still read as a selection that had arrived and merely say nothing about it.
			_wremove((g_workDir + L"\\selection.ready").c_str());
			_wremove((g_workDir + L"\\selection.tsv").c_str());
		}

		Chrome();
		// Written after the clearing, so the host can confirm rather than assume -- and carrying
		// whether there was anything to clear, because "cleared" and "nothing was selected" are
		// different answers to the same request.
		if (!g_workDir.empty())
		{
			std::wofstream done(g_workDir + L"\\deselect.ready", std::ios::trunc);
			if (done) done << (had ? L"cleared" : L"nothing") << L"\n";
		}

		Log(had ? L"overlay: selection cleared" : L"overlay: deselect with nothing selected");
		return had;
	}

	// Selects an element by its handle, with no hit test anywhere in the path -- which is the whole
	// point of it.
	//
	// Some controls cannot be picked by clicking at all. A slider is the reported case, and it is not
	// fixable at the hit-test layer: "what does a click land on" is a question the framework answers
	// and the answer is sometimes not the thing you meant. Visual Studio's own XAML tools have the
	// same gap, and the established way round it everywhere is to stop clicking and pick from the
	// tree. rose_xaml_tree already hands out a handle for every element, so this closes that loop --
	// and it is equally the way an agent selects something structurally, by type or by name or by the
	// file it came from, without a person having to point at it.
	bool SelectByHandle(InstanceHandle handle)
	{
		if (!m_diagnostics || handle == 0) return false;

		::IInspectable* raw = nullptr;
		if (FAILED(m_diagnostics->GetIInspectableFromHandle(handle, &raw)) || !raw)
		{
			Log(L"overlay: no live object for handle " + std::to_wstring(handle));
			return false;
		}

		winrt::Windows::Foundation::IInspectable instance{ nullptr };
		winrt::attach_abi(instance, raw); // adopt the ref

		// Not every handle in the tree is a UIElement -- a Brush or a resource has one too -- and
		// nothing can be outlined that has no place on screen.
		const auto element = instance.try_as<xaml::UIElement>();
		if (!element)
		{
			Log(L"overlay: handle " + std::to_wstring(handle) + L" is not a UIElement");
			return false;
		}

		winrt::Windows::Foundation::Rect rect{};
		if (!Bounds(element, rect))
		{
			Log(L"overlay: handle " + std::to_wstring(handle) + L" has no laid-out bounds");
			return false;
		}

		RecordFromTree(element, handle);

		const bool drawn = ShowBox(m_selectBox, m_selectBadge, element, Describe(element));
		m_hasSelection = true;
		m_selectionRect = WithBadge(rect);
		Reveal();
		Chrome();

		Log(L"overlay: selected " + Describe(element) + L" by handle; outline "
			+ std::wstring(drawn ? L"drawn" : L"NOT drawn"));

		return true;
	}

	void EndSelect()	{
		try
		{
			if (m_capture && m_root)
			{
				uint32_t index = 0;
				if (m_root.Children().IndexOf(m_capture, index)) m_root.Children().RemoveAt(index);
			}
		}
		catch (winrt::hresult_error const&)
		{
			// Best-effort teardown; the layer may already be gone.
		}

		m_capture = nullptr;
		m_selecting = false;
		EndOperation(Operation::Select);
		ShowBox(m_hoverBox, m_hoverBadge, nullptr, std::wstring());
		Chrome();
		WriteState();
	}

private:
	// How near the pointer has to be for a mark to be legible, and what it settles to when the
	// pointer is elsewhere. Both marks are persistent by design -- the selection outlives the pick
	// that made it, and the toolbar outlives everything -- so both spend most of their life being
	// something the person did not ask to look at. Fading on proximity is what lets them stay
	// available without staying in the way.
	static constexpr double SelectionNear = 0.50;
	static constexpr double SelectionFar = 0.10;
	static constexpr double PanelNear = 1.00;
	static constexpr double PanelFar = 0.50;

	// Long enough to read as a fade rather than a flicker, short enough not to lag the pointer.
	static constexpr int FadeMilliseconds = 160;

	// The badge sits this far above the element it captions. Shared with the proximity test, which
	// has to treat the caption as part of the selection.
	static constexpr double BadgeHeight = 18.0;
	static constexpr double BadgeGap = 2.0;

	static xmedia::SolidColorBrush Brush(uint8_t a, uint8_t r, uint8_t g, uint8_t b)
	{
		return xmedia::SolidColorBrush(winrt::Windows::UI::Color{ a, r, g, b });
	}

	// RoseMCP's own accent, the crimson the app icon's tile is drawn in (tools/Rose.ps1).
	static xmedia::SolidColorBrush Accent() { return Brush(0xFF, 0xC2, 0x18, 0x5B); }

	static xmedia::SolidColorBrush Idle() { return Brush(0xFF, 0x2C, 0x2C, 0x36); }

	static double Clamp(double value, double low, double high)
	{
		if (high < low) return low;
		if (value < low) return low;
		if (value > high) return high;
		return value;
	}

	void Build()
	{
		m_root = xcontrols::Grid();
		m_root.Name(OverlayRootName);

		// Sized explicitly, never by alignment. Stretch only fills when the parent hands its children
		// the space, and the diagnostics UI layer does not: it measures them at their desired size. A
		// stretching root therefore came out 0x0, which was invisible in the worst way -- the toolbar
		// still drew, because a Canvas does not clip what hangs outside it, and it still took input,
		// because it has a size of its own. Only the full-bleed capture layer collapsed, so select mode
		// armed, showed no tint, and never saw a single pointer event.
		Resize();

		m_canvas = xcontrols::Canvas();
		m_root.Children().Append(m_canvas);

		// The window is not a fixed size, and neither is the thing we are covering.
		xaml::Window::Current().SizeChanged(
			[this](winrt::Windows::Foundation::IInspectable const&,
				winrt::Windows::UI::Core::WindowSizeChangedEventArgs const&)
			{
				Resize();
				Place();
			});

		// The outlines go on first so the toolbar always draws over them. Hover is dashed and thin,
		// the pick solid and heavier, so the two never read as the same thing.
		m_hoverBox = Outline(1.0, true);
		m_hoverBadge = Badge();

		// The pick rests on screen until something else replaces it or it is cleared, so it is the
		// one mark that has to be liveable with. At full strength on a large container it is a
		// full-window box sitting over the app for as long as the selection lasts, which is what a
		// second user reported: not hard to see, hard to put up with.
		//
		// So it is drawn at the strength it should have when somebody is looking at it, and its
		// opacity carries the rest -- SelectionNear when the pointer is inside it, SelectionFar when
		// it is not. Baking the fade into the brushes instead was the first attempt, and it cannot
		// express the thing that actually makes this work: the mark being loud enough to read at the
		// moment you look for it.
		m_selectBox = Outline(2.0, false, 0xFF, 0x33);
		m_selectBadge = Badge();

		m_panel = xcontrols::Border();
		m_panel.Background(Brush(0xF0, 0x1C, 0x1C, 0x22));
		m_panel.BorderBrush(Brush(0xFF, 0x53, 0x53, 0x63));
		m_panel.BorderThickness(xaml::Thickness{ 1, 1, 1, 1 });
		m_panel.CornerRadius(xaml::CornerRadius{ 3, 3, 3, 3 });
		m_panel.Child(BuildBar());
		m_panel.Opacity(PanelFar);
		m_canvas.Children().Append(m_panel);

		const auto bounds = xaml::Window::Current().Bounds();
		m_dragLeft = bounds.Width > 220.0 ? bounds.Width - 200.0 : 16.0;
		m_dragTop = 16.0;
		Place();
		Chrome();

		// Last, because it reads the toolbar's position and the marks it fades.
		WatchPointer();
	}

	// One row: grip, mark, then the modes and Hide. No status text -- the feedback that matters is on
	// the element being hovered or picked, not in a line of prose over the app.
	xaml::UIElement BuildBar()
	{
		m_bar = xcontrols::StackPanel();
		m_bar.Orientation(xcontrols::Orientation::Horizontal);
		m_bar.Spacing(4);
		m_bar.Padding(xaml::Thickness{ 4, 3, 4, 3 });
		m_bar.Children().Append(DragHandle());
		m_bar.Children().Append(BuildMark());

		m_idleButton = Chip(Glyph(IconIdle, 12.0), L"Idle", [this] { EndSelect(); });
		m_selectButton = Chip(SelectIcon(), L"Select element", [this] { BeginSelect(false); });
		m_myXamlButton = Chip(
			Glyph(IconMyXaml, 12.0),
			L"Just my XAML -- pick the element declared in the app's own markup, not a control template's parts",
			[this] { ToggleMyXaml(); });
		m_bar.Children().Append(m_idleButton);
		m_bar.Children().Append(m_selectButton);
		m_bar.Children().Append(m_myXamlButton);

		// The way back out of a pick. Disabled rather than hidden when there is nothing selected:
		// a button that comes and goes moves the three beside it, and this toolbar sits over
		// somebody else's application.
		m_deselectButton = Chip(
			Glyph(IconDeselect, 12.0),
			L"Deselect -- clear the picked element and its mark",
			[this] { Deselect(); });
		m_bar.Children().Append(m_deselectButton);
		m_bar.Children().Append(Chip(Glyph(IconHide, 12.0), L"Hide", [this] { Collapse(true); }));

		// Collapsed is the grip on its own, in the same panel, so folding away changes nothing else.
		m_thumb = xcontrols::Border();
		m_thumb.Visibility(xaml::Visibility::Collapsed);

		// A Background is what makes the whole thumb draggable. Without one only the dots themselves
		// hit-test, so grabbing it meant hitting a 2px circle exactly -- which is how it felt.
		m_thumb.Background(Brush(0x00, 0x00, 0x00, 0x00));
		m_thumb.Padding(xaml::Thickness{ 8, 5, 8, 5 });
		m_thumb.Child(Dots(0xB8));
		AttachDrag(m_thumb);
		m_thumb.Tapped(
			[this](winrt::Windows::Foundation::IInspectable const&, xinput::TappedRoutedEventArgs const& e)
			{
				e.Handled(true);
				Collapse(false);
			});

		auto content = xcontrols::Grid();
		content.Children().Append(m_bar);
		content.Children().Append(m_thumb);
		return content;
	}

	// The mark, drawn rather than embedded. It is the same rhodonea rose as the app icon --
	// r = cos(3*theta/2), even-odd filled, rotated 90 degrees, the curve tools/Rose.ps1 draws -- so the
	// toolbar cannot drift from the brand. As geometry it is exact at any size and any DPI, takes its
	// colour from the toolbar, and needs no resource, no decode and nothing asynchronous at all.
	//
	// The rose alone, no monogram: above 32px the icon adds the stem and leg that make the R, and at
	// the size this is drawn those are sub-pixel. There is no tile behind it either -- the toolbar is
	// already a dark panel, and a second rounded square inside one reads as a sticker.
	//
	// n=3 and d=2 are not both odd, so the curve closes at 2*d*pi and has 2n = 6 petals. Even-odd is
	// the whole point: it cancels where the curve crosses itself, and that is what makes the flower.
	xaml::UIElement BuildMark()
	{
		constexpr double pi = 3.14159265358979323846;
		constexpr double extent = 16.0;
		constexpr double centre = extent / 2.0;
		constexpr double radius = extent * 0.46; // no tile corner to keep clear of, so wider than 0.36
		constexpr double rotation = pi / 2.0;
		constexpr double k = 3.0 / 2.0;
		constexpr double end = 4.0 * pi;
		constexpr int steps = 720;               // smooth at this size; Rose.ps1's 2400 is for 256px

		winrt::Windows::Foundation::Point start{};
		xmedia::PolyLineSegment segment;
		for (int i = 0; i <= steps; i++)
		{
			const double t = end * i / steps;
			const double r = radius * std::cos(k * t);
			const winrt::Windows::Foundation::Point point{
				static_cast<float>(centre + r * std::cos(t + rotation)),
				static_cast<float>(centre + r * std::sin(t + rotation)) };

			if (i == 0)
			{
				start = point;
			}
			else
			{
				segment.Points().Append(point);
			}
		}

		xmedia::PathFigure figure;
		figure.StartPoint(start);
		figure.IsClosed(true);
		figure.IsFilled(true);
		figure.Segments().Append(segment);

		xmedia::PathGeometry geometry;
		geometry.FillRule(xmedia::FillRule::EvenOdd);
		geometry.Figures().Append(figure);

		m_mark = xshapes::Path();
		m_mark.Data(geometry);
		m_mark.Fill(Brush(0xFF, 0xE4, 0xE4, 0xEC));
		m_mark.Width(extent);
		m_mark.Height(extent);
		m_mark.VerticalAlignment(xaml::VerticalAlignment::Center);
		m_mark.Margin(xaml::Thickness{ 2, 0, 3, 0 });
		return m_mark;
	}

	xcontrols::TextBlock Label(const wchar_t* text, double size, uint8_t grey, const wchar_t* fontFamily)
	{
		auto block = xcontrols::TextBlock();
		block.Text(text);
		block.FontSize(size);
		if (fontFamily) block.FontFamily(xmedia::FontFamily(fontFamily));
		block.Foreground(Brush(0xFF, grey, grey, grey));
		block.HorizontalAlignment(xaml::HorizontalAlignment::Center);
		block.VerticalAlignment(xaml::VerticalAlignment::Center);
		return block;
	}

	// Six dots, drawn rather than typed. The obvious characters for a grip -- braille U+283F, MDL2's
	// GripperBar -- are not in Segoe UI, so a glyph here is a hollow box on some machines depending on
	// what the font fallback finds. Shapes cannot miss, and the dot count is then exactly what was asked.
	static xaml::UIElement Dots(uint8_t grey)
	{
		auto columns = xcontrols::StackPanel();
		columns.Orientation(xcontrols::Orientation::Horizontal);
		columns.Spacing(2);
		columns.VerticalAlignment(xaml::VerticalAlignment::Center);

		for (int column = 0; column < 2; column++)
		{
			auto rows = xcontrols::StackPanel();
			rows.Orientation(xcontrols::Orientation::Vertical);
			rows.Spacing(2);

			for (int row = 0; row < 3; row++)
			{
				auto dot = xshapes::Ellipse();
				dot.Width(2);
				dot.Height(2);
				dot.Fill(Brush(0xFF, grey, grey, grey));
				rows.Children().Append(dot);
			}

			columns.Children().Append(rows);
		}

		return columns;
	}

	xcontrols::Border DragHandle()
	{
		auto handle = xcontrols::Border();

		// A Background is what makes it take input at all; transparent keeps it from being seen.
		handle.Background(Brush(0x00, 0x00, 0x00, 0x00));
		handle.Padding(xaml::Thickness{ 3, 0, 3, 0 });
		handle.Child(Dots(0x8C));
		AttachDrag(handle);
		return handle;
	}

	xcontrols::TextBlock Glyph(const wchar_t* glyph, double size)
	{
		return Label(glyph, size, 0xDC, L"Segoe MDL2 Assets");
	}

	// A pointer inside a marquee. MDL2 has no single glyph for it -- the nearest, SelectAll, is a
	// dense grid that turns to mush at button size -- so it is composed: a dashed rectangle with the
	// same pointer the Idle button uses, smaller and offset, sitting in it.
	xaml::UIElement SelectIcon()
	{
		auto host = xcontrols::Grid();
		host.Width(16);
		host.Height(16);

		auto marquee = xshapes::Rectangle();
		marquee.Stroke(Brush(0xFF, 0xDC, 0xDC, 0xDC));
		marquee.StrokeThickness(1);
		marquee.StrokeDashArray().Append(2);
		marquee.StrokeDashArray().Append(2);
		// Both centred in the host, so the composition has no built-in bias; the pointer is then
		// nudged down and right off that centre, which is where a cursor sits inside a marquee.
		marquee.Width(13);
		marquee.Height(13);
		marquee.HorizontalAlignment(xaml::HorizontalAlignment::Center);
		marquee.VerticalAlignment(xaml::VerticalAlignment::Center);
		host.Children().Append(marquee);

		auto pointer = Glyph(IconIdle, 10.0);
		pointer.HorizontalAlignment(xaml::HorizontalAlignment::Center);
		pointer.VerticalAlignment(xaml::VerticalAlignment::Center);
		pointer.Margin(xaml::Thickness{ 4, 3, 0, 0 });
		host.Children().Append(pointer);

		return host;
	}

	// Icon-only, with the words moved into a tooltip: a glyph that misses still leaves the meaning
	// reachable, and the toolbar stays out of the way of the app it is sitting on.
	xcontrols::Button Chip(xaml::UIElement const& content, const wchar_t* tip, std::function<void()> action)
	{
		auto button = xcontrols::Button();
		button.Content(content);

		// Square, and explicitly so: a Button sized by its padding comes out a few pixels wider than
		// tall with an icon in it, and three of those in a row is the thing that looks unconsidered.
		button.Padding(xaml::Thickness{ 0, 0, 0, 0 });
		button.Width(24);
		button.Height(24);
		button.MinWidth(0);
		button.MinHeight(0);
		button.Background(Idle());
		button.BorderThickness(xaml::Thickness{ 0, 0, 0, 0 });
		button.CornerRadius(xaml::CornerRadius{ 3, 3, 3, 3 });
		button.VerticalAlignment(xaml::VerticalAlignment::Center);
		xcontrols::ToolTipService::SetToolTip(button, winrt::box_value(winrt::hstring{ tip }));
		button.Click(
			[action](winrt::Windows::Foundation::IInspectable const&, xaml::RoutedEventArgs const&) { action(); });
		return button;
	}

	// Two strokes, not one: the accent rose, with a dark companion sitting a pixel outside it.
	//
	// One rose stroke is invisible on a rose-coloured app, which is not a hypothetical -- it is the
	// obvious thing to hit the moment RoseMCP is pointed at something built with RoseMCP's own palette.
	// UWP does offer a blend mode for this (ElementCompositeMode::MinBlend, which exists precisely to
	// make adorners readable over arbitrary content), but min() only separates the outline from a
	// *lighter* ground -- over a dark app it darkens the outline into the background instead, trading
	// one invisible case for another. Two contrasting strokes is what design tools do, and it holds on
	// any ground at all.
	//
	// The pair lives in a Grid so a caller moves one element: the rose stretches to the bounds, and the
	// dark one is inset by a negative margin so its stroke lands just outside the rose's.
	xcontrols::Grid Outline(double thickness, bool dashed, uint8_t strokeAlpha = 0xFF, uint8_t fillAlpha = 0x00)
	{
		auto box = xcontrols::Grid();
		box.Visibility(xaml::Visibility::Collapsed);
		box.IsHitTestVisible(false);

		// The fill goes in first, under both strokes. It is what carries a resting mark: an outline
		// alone is either loud enough to be an obstruction or too faint to find, whereas a wash over
		// the element reads at a few percent -- the same trick the capture layer already uses.
		if (fillAlpha > 0)
		{
			auto wash = xshapes::Rectangle();
			wash.Fill(Brush(fillAlpha, 0xC2, 0x18, 0x5B));
			box.Children().Append(wash);
		}

		auto contrast = xshapes::Rectangle();
		contrast.Stroke(Brush(static_cast<uint8_t>(0xB0 * strokeAlpha / 0xFF), 0x10, 0x10, 0x14));
		contrast.StrokeThickness(1);
		contrast.Margin(xaml::Thickness{ -thickness, -thickness, -thickness, -thickness });
		box.Children().Append(contrast);

		auto rose = xshapes::Rectangle();
		rose.Stroke(Brush(strokeAlpha, 0xC2, 0x18, 0x5B));
		rose.StrokeThickness(thickness);
		if (dashed)
		{
			rose.StrokeDashArray().Append(3);
			rose.StrokeDashArray().Append(2);
			contrast.StrokeDashArray().Append(3);
			contrast.StrokeDashArray().Append(2);
		}

		box.Children().Append(rose);

		m_canvas.Children().Append(box);
		return box;
	}

	xcontrols::Border Badge()
	{
		auto badge = xcontrols::Border();
		badge.Visibility(xaml::Visibility::Collapsed);
		badge.IsHitTestVisible(false);
		badge.Background(Accent());
		badge.BorderBrush(Brush(0x90, 0x10, 0x10, 0x14));
		badge.BorderThickness(xaml::Thickness{ 1, 1, 1, 1 });
		badge.CornerRadius(xaml::CornerRadius{ 2, 2, 2, 2 });
		badge.Padding(xaml::Thickness{ 4, 1, 4, 2 });
		badge.Child(Label(L"", 11.0, 0xF0, nullptr));
		m_canvas.Children().Append(badge);
		return badge;
	}

	void AttachDrag(xaml::UIElement const& handle)
	{
		handle.ManipulationMode(xinput::ManipulationModes::TranslateX | xinput::ManipulationModes::TranslateY);
		handle.ManipulationDelta(
			[this](winrt::Windows::Foundation::IInspectable const&, xinput::ManipulationDeltaRoutedEventArgs const& e)
			{
				const auto translation = e.Delta().Translation;
				m_dragLeft += translation.X;
				m_dragTop += translation.Y;
				Place();
				e.Handled(true);
			});
	}

	// The drag position is tracked unclamped and clamped only on the way to the Canvas. Clamping the
	// stored value instead is what made the toolbar feel detached from the pointer: once it had been
	// pinned at an edge the stored position no longer matched where the pointer actually was, so the
	// panel set off again the instant the pointer turned around, while it was still outside the window.
	void Resize()
	{
		const auto bounds = xaml::Window::Current().Bounds();
		if (m_root)
		{
			m_root.Width(bounds.Width);
			m_root.Height(bounds.Height);
		}

		if (m_capture)
		{
			m_capture.Width(bounds.Width);
			m_capture.Height(bounds.Height);
		}
	}

	void Place()
	{
		if (!m_panel) return;

		const auto bounds = xaml::Window::Current().Bounds();
		xcontrols::Canvas::SetLeft(m_panel, Clamp(m_dragLeft, 0.0, bounds.Width - m_panel.ActualWidth()));
		xcontrols::Canvas::SetTop(m_panel, Clamp(m_dragTop, 0.0, bounds.Height - m_panel.ActualHeight()));
	}

	void Collapse(bool collapsed)
	{
		if (!m_bar || !m_thumb) return;

		m_bar.Visibility(collapsed ? xaml::Visibility::Collapsed : xaml::Visibility::Visible);
		m_thumb.Visibility(collapsed ? xaml::Visibility::Visible : xaml::Visibility::Collapsed);
	}

	void ToggleMyXaml()
	{
		m_justMyXaml = !m_justMyXaml;
		Chrome();
		WriteState();
		Log(std::wstring(L"overlay: just-my-XAML ") + (m_justMyXaml ? L"on" : L"off"));
	}


	// Which mode is current, said in the toolbar itself: the active button wears the accent, the
	// inactive one the panel's own grey. Just-my-XAML is a toggle rather than a mode, so it is lit
	// whenever it is on regardless of whether a pick is in progress.
	void Chrome()
	{
		if (!m_idleButton || !m_selectButton) return;

		m_idleButton.Background(m_selecting ? Idle() : Accent());
		m_selectButton.Background(m_selecting ? Accent() : Idle());
		if (m_myXamlButton) m_myXamlButton.Background(m_justMyXaml ? Accent() : Idle());

		// Deselect is an action rather than a mode, so it never wears the accent -- only whether
		// there is anything for it to do.
		if (m_deselectButton) m_deselectButton.IsEnabled(m_hasSelection);
	}

	/// Whether an element was declared in the app's own markup.
	///
	/// One question -- did this come out of the app's own package and assembly, or out of something
	/// it merely references -- asked twice, because the two URI schemes encode ownership differently.
	/// An element with no source info at all is not claimed either way: absent is not the same as
	/// framework, and treating it as framework would quietly empty the filter on an app that has no
	/// source info to give.
	bool IsAppXaml(InstanceHandle handle) const
	{
		const auto found = m_sources.find(handle);
		if (found == m_sources.end()) return false;

		const std::wstring& source = found->second;

		// A page or user control. Under ms-appx the owner is the *authority*, which names the package
		// the markup came out of, and an empty one means the app's own:
		//
		//     ms-appx:///Views/Shell/ShellView.xaml                             <- the app's own
		//     ms-appx://Microsoft.UI.Xaml.2.8/.../21h1_themeresources.xaml      <- WinUI 2's
		//
		// Testing the scheme alone made the whole filter a no-op for any app on WinUI 2: system WinUI
		// serves its themes as ms-resource:, so the scheme happens to separate them there, but
		// Microsoft.UI.Xaml is a framework package inside the app package and its themes are ms-appx:
		// like everything else. Every templated part of every MUXC control counted as the developer's
		// own markup -- on the stack where "just my XAML" is worth the most, since that is the stack
		// with the deepest templates.
		if (source.rfind(L"ms-appx:///", 0) == 0) return true;
		if (source.rfind(L"ms-appx:", 0) == 0) return false;
		if (source.rfind(L"ms-resource:", 0) != 0) return false;

		// A resource dictionary. Here the authority is empty either way, so it says nothing -- and an
		// app that themes its own controls keeps those styles in ResourceDictionaries, served as
		// ms-resource: exactly like the framework's. Reading "ms-resource: means theirs" steered
		// clicks away from markup the developer unambiguously owns and can edit.
		//
		// The discriminator here is the assembly component. A dictionary that came from a referenced
		// assembly names it:
		//
		//     ms-resource:///Files/windows.ui.xaml;component/themes/generic.xaml   <- the framework's
		//     ms-resource:///Files/Themes/Default/Controls/TextBox.Styles.xaml     <- the app's own
		//
		// which also gives the right answer for a third-party control library: its templates are not
		// the framework's, but they are equally not something the developer is going to edit.
		return source.find(L";component/") == std::wstring::npos;
	}

	// Where an element sits in the window, in the coordinates the overlay's Canvas uses -- the UI layer
	// is sized to the window, so the window root's space is the Canvas's space.
	bool Bounds(xaml::UIElement const& element, winrt::Windows::Foundation::Rect& rect)
	{
		try
		{
			const auto root = xaml::Window::Current().Content();
			if (!root) return false;

			const auto transform = element.TransformToVisual(root);
			const auto origin = transform.TransformPoint(winrt::Windows::Foundation::Point{ 0, 0 });
			const auto size = element.RenderSize();
			if (size.Width <= 0 || size.Height <= 0) return false;

			rect = winrt::Windows::Foundation::Rect{ origin.X, origin.Y, size.Width, size.Height };
			return true;
		}
		catch (winrt::hresult_error const& error)
		{
			Trace(std::wstring(L"bounds failed: ") + error.message().c_str());
			return false;
		}
	}

	static std::wstring Describe(xaml::UIElement const& element)
	{
		std::wstring typeName{ winrt::get_class_name(element) };
		const auto lastDot = typeName.rfind(L'.');
		if (lastDot != std::wstring::npos) typeName = typeName.substr(lastDot + 1);

		std::wstring name;
		if (const auto frameworkElement = element.try_as<xaml::FrameworkElement>())
		{
			name = frameworkElement.Name();
		}

		return name.empty() ? typeName : (typeName + L" \x00B7 " + name);
	}


	// Watches where the pointer is, so the marks can get out of the way when it is not near them.
	//
	// A passive observer, and it has to be. The outlines are IsHitTestVisible(false) and the whole
	// design of this overlay is that it does not take input away from the app it is sitting on, so
	// PointerEntered on the mark is not available and giving it one would be the one thing this must
	// never do. CoreWindow sees every move before XAML routes it and consumes nothing.
	void WatchPointer()
	{
		m_selectionFade = MakeFader({ m_selectBox, m_selectBadge });
		m_panelFade = MakeFader({ m_panel });

		const auto window = xaml::Window::Current().CoreWindow();
		if (!window) return;

		window.PointerMoved(
			[this](winrt::Windows::UI::Core::CoreWindow const&, winrt::Windows::UI::Core::PointerEventArgs const& e)
			{
				try
				{
					Proximity(e.CurrentPoint().Position());
				}
				catch (winrt::hresult_error const&)
				{
					// Runs on every pointer move in somebody else's application. Never throw out of it.
				}
			});

		// Leaving the window is not a move, and without this whatever was last under the pointer stays
		// lit for as long as the pointer is somewhere else entirely.
		window.PointerExited(
			[this](winrt::Windows::UI::Core::CoreWindow const&, winrt::Windows::UI::Core::PointerEventArgs const&)
			{
				try
				{
					Proximity(winrt::Windows::Foundation::Point{ -1.0f, -1.0f });
				}
				catch (winrt::hresult_error const&)
				{
				}
			});
	}

	// Shows a freshly made selection, snapping when the pointer is already inside it and fading it up
	// when it is not.
	//
	// Asking where the pointer is, rather than who asked for the selection, because that is the
	// question the answer actually turns on -- and it happens to answer both callers. A click lands
	// under the pointer, so the mark should simply be there: fading up would pretend the pointer were
	// still on its way to somewhere it already is. A selection made by handle, which is #46 and the
	// way an agent will reach this, lands wherever the element happens to be, and appearing at full
	// strength somewhere the person is not looking is a flash in the corner of the eye rather than an
	// answer. Today every path here is a click, so this always snaps; #46 gets the other half free.
	void Reveal()
	{
		m_overSelection = Contains(m_selectionRect, m_pointer);

		if (m_overSelection)
		{
			m_selectionFade.Snap(SelectionNear);
			return;
		}

		// Left at SelectionNear rather than settling straight to SelectionFar: a selection nobody
		// watched arrive is one they have to be told about, and the next pointer move fades it back
		// down through the ordinary proximity rule.
		m_selectionFade.To(SelectionNear);
	}

	// Both marks fade on the same rule: near the pointer they are legible, away from it they are a
	// hint. Only a change of state starts an animation -- this runs on every pointer move, and
	// restarting a storyboard sixty times a second over an app somebody is using is not acceptable.
	void Proximity(winrt::Windows::Foundation::Point const& point)
	{
		m_pointer = point;

		const bool overSelection = m_hasSelection && Contains(m_selectionRect, point);
		if (overSelection != m_overSelection)
		{
			m_overSelection = overSelection;
			m_selectionFade.To(overSelection ? SelectionNear : SelectionFar);
		}

		const bool overPanel = Contains(PanelRect(), point);
		if (overPanel != m_overPanel)
		{
			m_overPanel = overPanel;
			RefreshPanelFade();
		}
	}

	// One storyboard per mark, built once and re-aimed, because the two obvious ways to do this are
	// both wrong and they fail in opposite directions.
	//
	// Stop() before re-beginning looks like the tidy thing and is not: stopping an animation reverts
	// the property to its *local* value, so one of the two directions snapped instead of fading --
	// whichever direction happened to be heading back towards the value last written with .Opacity().
	// The toolbar's mouse-out and the selection's mouse-in were both instant for exactly that reason,
	// and they were instant in opposite directions because their local values sat at opposite ends.
	//
	// Building a fresh storyboard each time is the other trap: releasing one that is holding its end
	// value lets the property fall back. Re-aiming a storyboard that stays alive has neither problem,
	// and a DoubleAnimation with no From always starts from wherever the property has actually got to,
	// so an interrupted fade hands over rather than jumping.
	struct Fader
	{
		xanim::Storyboard Board{ nullptr };
		std::vector<xanim::DoubleAnimation> Animations;

		// What it is already heading for, so asking for the same thing twice is not a restart. The
		// panel is asked on every pointer move across its edge and on every change of operation,
		// and most of those answers are the one it is already giving.
		double Target = -1.0;

		void To(double value)
		{
			if (!Board || Target == value) return;

			Target = value;

			for (auto const& animation : Animations)
			{
				animation.To(value);
			}

			Board.Begin();
		}

		// Arrives at a value with no animation, for the moment when animating would be a lie. A pick
		// happens under the pointer, so the mark is already being looked at: it should be there, not
		// fade up as though the pointer were on its way.
		//
		// Still driven through the storyboard rather than by writing Opacity, because a held
		// animation outranks a local value -- and SkipToFill leaves it held at the new value, so the
		// next fade hands over from it the same way any other would.
		void Snap(double value)
		{
			if (!Board) return;

			Target = value;

			for (auto const& animation : Animations)
			{
				animation.To(value);
			}

			Board.Begin();
			Board.SkipToFill();
		}
	};

	// Opacity is the one visual property XAML animates off the UI thread, which is what makes this
	// affordable at all: a dependent animation would cost the app frames every time the pointer
	// crossed an edge, and that is a strange thing to charge somebody for a diagnostics overlay.
	static Fader MakeFader(std::vector<xaml::UIElement> const& targets)
	{
		Fader fader;
		fader.Board = xanim::Storyboard();

		for (auto const& target : targets)
		{
			if (!target) continue;

			xanim::DoubleAnimation animation;
			animation.EnableDependentAnimation(false);

			// Duration is a value struct of a TimeSpan *and* a DurationType, and the type is not
			// implied by the TimeSpan. Leaving it at its zero -- Automatic -- was the whole of why
			// these ran for about a second instead of the sixth of one written just below.
			animation.Duration(xaml::Duration{
				winrt::Windows::Foundation::TimeSpan{ std::chrono::milliseconds(FadeMilliseconds) },
				xaml::DurationType::TimeSpan });

			xanim::Storyboard::SetTarget(animation, target);
			xanim::Storyboard::SetTargetProperty(animation, L"Opacity");
			fader.Board.Children().Append(animation);
			fader.Animations.push_back(animation);
		}

		return fader;
	}

	// Where the toolbar is now, read live rather than remembered: it is draggable, it folds down to
	// the grip, and the window it is clamped to resizes.
	winrt::Windows::Foundation::Rect PanelRect() const
	{
		if (!m_panel) return {};

		const double left = xcontrols::Canvas::GetLeft(m_panel);
		const double top = xcontrols::Canvas::GetTop(m_panel);
		if (std::isnan(left) || std::isnan(top)) return {};

		return winrt::Windows::Foundation::Rect{
			static_cast<float>(left),
			static_cast<float>(top),
			static_cast<float>(m_panel.ActualWidth()),
			static_cast<float>(m_panel.ActualHeight()) };
	}

	static bool Contains(winrt::Windows::Foundation::Rect const& rect, winrt::Windows::Foundation::Point const& point)
	{
		return rect.Width > 0 && rect.Height > 0
			&& point.X >= rect.X && point.X < rect.X + rect.Width
			&& point.Y >= rect.Y && point.Y < rect.Y + rect.Height;
	}

	// The element's own rectangle, grown upwards to take in the badge that sits above it. Pointing at
	// the caption is pointing at the selection -- without this, moving onto the one part that is still
	// legible at rest is what makes the rest of the mark disappear.
	static winrt::Windows::Foundation::Rect WithBadge(winrt::Windows::Foundation::Rect const& rect)
	{
		const float reach = static_cast<float>(BadgeHeight + BadgeGap);
		const float top = rect.Y - reach;
		if (top < 0.0f) return rect; // The badge was drawn inside the element, so the rect already covers it.

		return winrt::Windows::Foundation::Rect{ rect.X, top, rect.Width, rect.Height + reach };
	}

	// Moves an outline and its badge onto an element, or hides both when there is nothing to show.
	bool ShowBox(
		xcontrols::Grid const& box,
		xcontrols::Border const& badge,
		xaml::UIElement const& element,
		std::wstring const& caption)
	{
		winrt::Windows::Foundation::Rect rect{};
		if (!box || !badge) return false;

		if (!element || !Bounds(element, rect))
		{
			box.Visibility(xaml::Visibility::Collapsed);
			badge.Visibility(xaml::Visibility::Collapsed);
			return false;
		}

		box.Width(rect.Width);
		box.Height(rect.Height);
		xcontrols::Canvas::SetLeft(box, rect.X);
		xcontrols::Canvas::SetTop(box, rect.Y);
		box.Visibility(xaml::Visibility::Visible);

		if (const auto text = badge.Child().try_as<xcontrols::TextBlock>()) text.Text(caption);

		// Above the element, unless that would be off the top of the window, in which case inside it.
		const double top = rect.Y - BadgeHeight - BadgeGap;
		xcontrols::Canvas::SetLeft(badge, rect.X);
		xcontrols::Canvas::SetTop(badge, top < 0.0 ? rect.Y + 2.0 : top);
		badge.Visibility(xaml::Visibility::Visible);
		return true;
	}

	xaml::UIElement Beneath(winrt::Windows::Foundation::Point const& point, winrt::Windows::Foundation::Rect& rect)
	{
		const auto root = xaml::Window::Current().Content();
		if (!root)
		{
			Trace(L"beneath: the window has no content");
			return nullptr;
		}

		// includeAllElements is the caller's choice and defaults to FALSE, which is the whole point.
		//
		// With it true -- as this shipped -- the hit test returns elements the framework would never
		// route input to, and on a real app that made click-to-select useless: an empty Grid with no
		// Background, stretched over the window as a dialog host, sat topmost over everything and
		// every click resolved to it. Input passes straight through such a panel, so the app was
		// perfectly usable while the selector insisted that was the thing being clicked.
		//
		// The irony is total: a null Background not taking part in hit testing is the exact rule this
		// overlay is built on -- it is why the toolbar is click-through -- and then the selector asked
		// the framework to ignore it. "Click an element to select it" has to mean the element the
		// app's own input system would route that click to, or it means nothing.
		//
		// True stays available on request, because inspecting an invisible overlay host is sometimes
		// exactly the goal. It is never the default.
		const auto found = xmedia::VisualTreeHelper::FindElementsInHostCoordinates(point, root, m_includeAllElements);
		uint32_t considered = 0;
		for (auto&& element : found)
		{
			considered++;
			if (IsOurs(element)) continue;      // Our own layers, if they are ever in this tree at all.
			if (!Bounds(element, rect)) continue; // Zero-sized or not laid out: not what was pointed at.

			Trace(L"beneath: " + Describe(element) + L" at " + std::to_wstring(static_cast<int>(rect.X))
				+ L"," + std::to_wstring(static_cast<int>(rect.Y)) + L" "
				+ std::to_wstring(static_cast<int>(rect.Width)) + L"x"
				+ std::to_wstring(static_cast<int>(rect.Height))
				+ L" (of " + std::to_wstring(considered) + L" considered)");
			return element;
		}

		Trace(L"beneath: nothing usable under " + std::to_wstring(static_cast<int>(point.X)) + L","
			+ std::to_wstring(static_cast<int>(point.Y)) + L" (" + std::to_wstring(considered) + L" considered)");
		return nullptr;
	}

	// The first few pointer moves are traced and the rest are not: enough to tell a hover that found
	// nothing from one that found something and failed to draw it, without a line per mouse move.
	void Trace(const std::wstring& line)
	{
		if (m_traces >= 6) return;
		m_traces++;
		Log(line);
	}

	void OnHover(xinput::PointerRoutedEventArgs const& e)
	{
		try
		{
			winrt::Windows::Foundation::Rect rect{};
			const auto element = Beneath(e.GetCurrentPoint(nullptr).Position(), rect);
			ShowBox(m_hoverBox, m_hoverBadge, element, element ? Describe(element) : std::wstring());
		}
		catch (winrt::hresult_error const& error)
		{
			Trace(std::wstring(L"hover failed: ") + error.message().c_str());
		}
	}

	void OnPick(xinput::PointerRoutedEventArgs const& e)
	{
		try
		{
			e.Handled(true); // Swallow the click so it does not also reach the app.
			const auto point = e.GetCurrentPoint(nullptr).Position();

			// A click is a pointer position, and not always preceded by a move -- a touch, or the
			// pointer arriving and pressing in one gesture, produces no PointerMoved at all.
			m_pointer = point;
			winrt::Windows::Foundation::Rect rect{};
			if (const auto element = Beneath(point, rect))
			{
				Record(element, point);

				// The picked element keeps its outline after select mode ends: that persistent mark is
				// the evidence of what "the selected element" now means, for the person and the agent.
				const bool drawn = ShowBox(m_selectBox, m_selectBadge, element, Describe(element));
				m_hasSelection = true;
				m_selectionRect = WithBadge(rect);
				Reveal();
				Chrome();
				Log(L"overlay: selection outline " + std::wstring(drawn ? L"drawn" : L"NOT drawn"));
			}
		}
		catch (winrt::hresult_error const& error)
		{
			Log(std::wstring(L"overlay: hit test failed: ") + error.message().c_str());
		}

		EndSelect();
	}

	/// Writes the selection for an element that arrived from the tree rather than from a click.
	///
	/// The named element leads, then its ancestors outwards. A click records the hit stack because
	/// one element is rarely the one wanted -- a click on a button lands on some templated child of
	/// it -- and arriving from the tree has the same problem from the other side: the handle you had
	/// was the one the tree gave you, and the container you actually meant is one or two hops up.
	/// Walking up costs nothing and keeps the file one shape, so a caller reads the stack the same
	/// way whichever route made it.
	///
	/// Just-my-XAML deliberately does not apply. It exists to decide *which* of several elements
	/// under a click was meant; here the caller has named one exactly, and overriding that would be
	/// answering a question nobody asked.
	void RecordFromTree(xaml::UIElement const& element, InstanceHandle handle)
	{
		if (g_workDir.empty()) return;

		unsigned int written = 0;

		{
			std::ofstream file((g_workDir + L"\\selection.tsv").c_str(), std::ios::trunc | std::ios::binary);
			if (!file) return;

			xaml::DependencyObject node = element;
			while (node && written < 16)
			{
				if (const auto candidate = node.try_as<xaml::UIElement>())
				{
					if (IsOurs(candidate)) break; // Walked out of the app and into our own overlay.

					WriteCandidate(file, candidate);
					written++;
				}

				node = xmedia::VisualTreeHelper::GetParent(node);
			}
		}

		std::wofstream ready(g_workDir + L"\\selection.ready", std::ios::trunc);
		if (ready) ready << handle << L"\n";

		Log(L"overlay: recorded " + Describe(element) + L" and " + std::to_wstring(written) + L" row(s) from the tree");
	}

	// Anything under our own root is ours -- the capture layer, the toolbar, and every part of it.
	bool IsOurs(xaml::UIElement const& element) const
	{
		if (!m_root) return false;

		xaml::DependencyObject node = element;
		while (node)
		{
			if (node == m_root) return true;
			node = xmedia::VisualTreeHelper::GetParent(node);
		}

		return false;
	}

	/// Writes the whole hit stack, topmost first, the framework's own pick leading.
	///
	/// One element is not enough to be useful even when it is the right one: a click on a button
	/// lands on some templated child of it, and a click meant for a container lands on the content
	/// inside. Handing back the ordered stack lets the caller walk down for the templated part or up
	/// for the container without another round trip, and the enumeration is already ordered, so it
	/// costs a few more rows in a file that is written once per click.
	void Record(xaml::UIElement const& element, winrt::Windows::Foundation::Point const& point)
	{
		if (g_workDir.empty()) return;

		const auto root = xaml::Window::Current().Content();
		InstanceHandle selected = 0;
		unsigned int written = 0;

		{
			std::ofstream file((g_workDir + L"\\selection.tsv").c_str(), std::ios::trunc | std::ios::binary);
			if (!file) return;

			InstanceHandle topmost = 0;
			InstanceHandle topmostApp = 0;

			if (root)
			{
				for (auto&& candidate : xmedia::VisualTreeHelper::FindElementsInHostCoordinates(point, root, m_includeAllElements))
				{
					if (IsOurs(candidate)) continue;

					winrt::Windows::Foundation::Rect ignored{};
					if (!Bounds(candidate, ignored)) continue;
					if (written >= 16) break; // Deep templates go on a long way; the top of the stack is the useful part.

					const InstanceHandle handle = WriteCandidate(file, candidate);
					if (topmost == 0) topmost = handle;
					if (topmostApp == 0 && IsAppXaml(handle)) topmostApp = handle;
					written++;
				}
			}

			// The framework found nothing usable but something was picked, so say that much.
			if (written == 0) topmost = WriteCandidate(file, element);

			// The rows stay in hit order -- that ordering is the point of returning a stack. What
			// just-my-XAML changes is which of them is *the* selection: a click on a button should
			// mean the button the developer wrote, not whichever part of its template happens to be
			// on top. It falls back to the framework's own pick when nothing in the stack came from
			// the app's markup, so an app with no source info degrades to the previous behaviour
			// rather than selecting nothing.
			selected = (m_justMyXaml && topmostApp != 0) ? topmostApp : topmost;
		}

		std::wofstream ready(g_workDir + L"\\selection.ready", std::ios::trunc);
		if (ready) ready << selected << L"\n";
		Log(L"overlay: recorded " + Describe(element) + L" and " + std::to_wstring(written) + L" candidate(s)");
	}

	InstanceHandle WriteCandidate(std::ofstream& file, xaml::UIElement const& candidate)
	{
		InstanceHandle handle = 0;
		if (m_diagnostics)
		{
			m_diagnostics->GetHandleFromIInspectable(reinterpret_cast<::IInspectable*>(winrt::get_abi(candidate)), &handle);
		}

		std::wstring typeName{ winrt::get_class_name(candidate) };
		std::wstring name;
		if (const auto frameworkElement = candidate.try_as<xaml::FrameworkElement>())
		{
			name = frameworkElement.Name();
		}

		const std::wstring row = std::to_wstring(handle) + L'\t' + Escape(typeName.c_str()) + L'\t'
			+ Escape(name.c_str()) + L'\t' + (IsFrameworkType(typeName) ? L"1" : L"0");
		file << Utf8(row) << '\n';
		return handle;
	}

	/// Whether a type belongs to the XAML framework rather than to the app or a library. Namespace is
	/// a coarse test and deliberately not dressed up as more: an app's Button is a framework type
	/// declared in the app's markup, so this narrows a candidate stack and never decides it alone.
	static bool IsFrameworkType(const std::wstring& typeName)
	{
		return typeName.rfind(L"Windows.UI.Xaml.", 0) == 0
			|| typeName.rfind(L"Microsoft.UI.Xaml.", 0) == 0;
	}

	// The mode, on disk, because the person can change it from the toolbar without the host being in
	// the conversation at all -- so the host has to be able to ask, rather than remember what it set.
	void WriteState()
	{
		if (g_workDir.empty()) return;

		std::wofstream state(g_workDir + L"\\overlay.state", std::ios::trunc);
		if (state) state << (m_selecting ? L"select" : L"idle") << L" justMyXaml=" << (m_justMyXaml ? L"1" : L"0") << L"\n";
	}

	// "armed <width>x<height>", the extent XAML arranged the capture layer at. A zero here is the
	// whole bug this reports: select mode that is on, invisible, and cannot be pointed at.
	void WriteArmed()
	{
		if (g_workDir.empty() || !m_capture) return;

		const int width = static_cast<int>(m_capture.ActualWidth());
		const int height = static_cast<int>(m_capture.ActualHeight());

		std::wofstream armed(g_workDir + L"\\select.ready", std::ios::trunc);
		if (armed) armed << L"armed " << width << L"x" << height << L"\n";

		Log(L"overlay: capture layer arranged at " + std::to_wstring(width) + L"x" + std::to_wstring(height));
	}

	IXamlDiagnostics* m_diagnostics = nullptr;
	xcontrols::Panel m_layer{ nullptr };
	xcontrols::Grid m_root{ nullptr };
	xcontrols::Canvas m_canvas{ nullptr };
	xcontrols::Border m_panel{ nullptr };
	xcontrols::Grid m_capture{ nullptr };
	xcontrols::StackPanel m_bar{ nullptr };
	xcontrols::Border m_thumb{ nullptr };
	xshapes::Path m_mark{ nullptr };
	xcontrols::Button m_idleButton{ nullptr };
	xcontrols::Button m_selectButton{ nullptr };
	xcontrols::Button m_myXamlButton{ nullptr };
	xcontrols::Button m_deselectButton{ nullptr };
	xcontrols::Grid m_hoverBox{ nullptr };
	xcontrols::Grid m_selectBox{ nullptr };
	xcontrols::Border m_hoverBadge{ nullptr };
	xcontrols::Border m_selectBadge{ nullptr };
	Fader m_selectionFade;
	Fader m_panelFade;

	// Where the selection is, so the pointer can be tested against it without asking the app.
	// Held rather than recomputed: an element that has moved or been laid out again is issue #51's
	// problem, and reaching into the app's tree on every pointer move to find out would not be a
	// fix for it so much as a reason to be blamed for the app feeling slow.
	winrt::Windows::Foundation::Rect m_selectionRect{};
	// The last place the pointer was seen, in window coordinates. Kept because a selection can be
	// made at a moment when there is no pointer event to read it from.
	winrt::Windows::Foundation::Point m_pointer{ -1.0f, -1.0f };
	bool m_overSelection = false;
	bool m_overPanel = false;
	double m_dragLeft = 16.0;
	double m_dragTop = 16.0;
	bool m_selecting = false;
	std::set<Operation> m_operations;

	// Whether a pick is currently marked. Tracked rather than inferred from the box's visibility,
	// because the box is also hidden when an element could not be measured, and "drawn nothing"
	// is not the same fact as "nothing is selected".
	bool m_hasSelection = false;
	int m_traces = 0;
	bool m_includeAllElements = false;
	bool m_justMyXaml = true;
	std::map<InstanceHandle, std::wstring> m_sources;
};

// Splits a request line on spaces, dropping the leading verb. Tokenised because matching a suffix
// gets the wrong answer the moment there are two flags.
static std::vector<std::wstring> Tokens(const std::wstring& request)
{
	std::vector<std::wstring> tokens;
	std::wistringstream stream(request);
	std::wstring token;
	while (stream >> token)
	{
		tokens.push_back(token);
	}

	if (!tokens.empty()) tokens.erase(tokens.begin());
	return tokens;
}

// Leaked deliberately: see the note on RoseOverlay. Only ever touched on the app's UI thread.
static RoseOverlay* g_overlay = nullptr;

static RoseOverlay& Overlay()
{
	if (!g_overlay) g_overlay = new RoseOverlay();
	return *g_overlay;
}

class RoseTap final : public IObjectWithSite, public IVisualTreeServiceCallback
{
public:
	HRESULT STDMETHODCALLTYPE QueryInterface(REFIID riid, void** ppv) override
	{
		if (!ppv) return E_POINTER;
		if (riid == IID_IUnknown || riid == __uuidof(IObjectWithSite))
		{
			*ppv = static_cast<IObjectWithSite*>(this);
		}
		else if (riid == __uuidof(IVisualTreeServiceCallback))
		{
			*ppv = static_cast<IVisualTreeServiceCallback*>(this);
		}
		else
		{
			*ppv = nullptr;
			return E_NOINTERFACE;
		}

		AddRef();
		return S_OK;
	}

	ULONG STDMETHODCALLTYPE AddRef() override { return ++m_refs; }

	ULONG STDMETHODCALLTYPE Release() override
	{
		const long remaining = --m_refs;
		if (remaining == 0) delete this;
		return remaining;
	}

	HRESULT STDMETHODCALLTYPE SetSite(IUnknown* site) override
	{
		if (!site)
		{
			Unadvise();
			return S_OK;
		}

		HRESULT hr = site->QueryInterface(__uuidof(IXamlDiagnostics), reinterpret_cast<void**>(&m_diagnostics));
		if (FAILED(hr)) return hr;

		BSTR initData = nullptr;
		if (SUCCEEDED(m_diagnostics->GetInitializationData(&initData)) && initData)
		{
			g_workDir.assign(initData, SysStringLen(initData));
			SysFreeString(initData);
		}

		m_diagnostics->QueryInterface(__uuidof(IVisualTreeService), reinterpret_cast<void**>(&m_tree));
		if (!m_tree)
		{
			Log(L"SetSite: no IVisualTreeService");
			return E_NOINTERFACE;
		}

		// Enumerate the tree (synchronous callbacks on this thread) to build the snapshot and name
		// map, then serve the host's request and run any commands. All on the UI thread, where XAML
		// lives -- which is why each request re-injects rather than being answered off a worker thread.
		hr = m_tree->AdviseVisualTreeChange(this);
		Log(L"enumerated " + std::to_wstring(m_nodes.size()) + L" element(s) (advise hr=0x" + Hex(hr) + L")");
		WriteTreeSnapshot();

		// The toolbar is installed once and left there. It goes in after the snapshot so the very first
		// tree cannot contain it, and the snapshot filters it out of every one after that.
		Overlay().Install(m_diagnostics);

		// Per-element source info only exists here, where the tree was walked, so it is handed to the
		// overlay: it is what "just my XAML" decides on, and a click has no other way to learn it.
		std::map<InstanceHandle, std::wstring> sources;
		for (const auto& node : m_nodes)
		{
			if (!node.File.empty()) sources[node.Handle] = node.File;
		}

		Overlay().SetSources(std::move(sources));

		const std::wstring request = ReadRequest();
		if (request.rfind(L"properties ", 0) == 0)
		{
			// "properties <handle>" gives the set (non-default) properties; a trailing " all" includes
			// the framework defaults too. Filtering defaults out keeps the interesting values from being
			// pushed past the row cap on an element with hundreds of properties.
			const bool includeDefaults = request.size() >= 4 && request.compare(request.size() - 4, 4, L" all") == 0;
			WriteProperties(static_cast<InstanceHandle>(_wcstoui64(request.c_str() + 11, nullptr, 10)), includeDefaults);
		}
		else if (request.rfind(L"selecthandle ", 0) == 0)
		{
			// Checked before the arming verb below, and named without a space after "select" so the
			// two cannot be confused: arming parses its tokens as flags, and a handle is not one.
			Overlay().SelectByHandle(static_cast<InstanceHandle>(_wcstoui64(request.c_str() + 13, nullptr, 10)));
		}
		else if (request == L"deselect")
		{
			// The same act as the toolbar button, so the mark and the recorded selection go together
			// whichever end asks. An agent that has finished with an element, and #51's tree watcher
			// noticing the element is gone, both want exactly this.
			Overlay().Deselect();
		}
		else if (request == L"apply")
		{
			ApplyCommands();
		}
		else if (request == L"select" || request.rfind(L"select ", 0) == 0)
		{
			// Arming from the agent and arming from the toolbar are the same act; whichever happens,
			// the overlay writes select.ready and the host reads the pick back the same way.
			//
			// Tokenised rather than suffix-matched: "all" asks for elements the framework would not
			// hit-test (explicit, never the default -- see Beneath), and "nomyxaml" turns off the
			// preference for the app's own markup. A flag the person set on the toolbar is left alone
			// unless the request actually mentions it.
			bool includeAll = false;
			for (const auto& token : Tokens(request))
			{
				if (token == L"all") includeAll = true;
				else if (token == L"myxaml") Overlay().SetJustMyXaml(true);
				else if (token == L"nomyxaml") Overlay().SetJustMyXaml(false);
			}

			Overlay().BeginSelect(includeAll);
		}

		return S_OK;
	}

	HRESULT STDMETHODCALLTYPE GetSite(REFIID riid, void** ppv) override
	{
		if (!m_diagnostics) return E_FAIL;
		return m_diagnostics->QueryInterface(riid, ppv);
	}

	HRESULT STDMETHODCALLTYPE OnVisualTreeChange(
		ParentChildRelation relation, VisualElement element, VisualMutationType mutationType) override
	{
		if (mutationType != Add) return S_OK;

		// SrcInfo comes per element and was previously dropped on the floor. It is what tells an
		// element the developer wrote from one a control template produced, which is the whole of
		// "just my XAML" -- and it is a different field from PropertyChainSource::SrcInfo, so the
		// two can be populated independently. Empty is recorded as empty; absent source info must
		// not be reported as "declared nowhere".
		m_nodes.push_back({ element.Handle, relation.Parent, relation.ChildIndex,
			element.Type ? element.Type : L"", element.Name ? element.Name : L"",
			element.SrcInfo.FileName ? element.SrcInfo.FileName : L"",
			element.SrcInfo.LineNumber, element.SrcInfo.ColumnNumber });

		if (element.Name && element.Name[0])
		{
			m_byName[element.Name] = element.Handle;
		}

		return S_OK;
	}

private:
	// One row per element: Handle, Parent, ChildIndex, Type, Name. Written to a temp file and renamed
	// so the host never reads a half-written snapshot; a ".ready" marker is the host's signal.
	//
	// The resident toolbar is dropped from the answer: the tool reports the app's UI, not RoseMCP's own.
	// The diagnostics UI layer it lives on is not enumerated by AdviseVisualTreeChange on the versions
	// tested -- the count is identical before and after the toolbar goes up -- so this is a guard against
	// a framework that does enumerate it, not a fix for one that does.
	void WriteTreeSnapshot()
	{
		if (g_workDir.empty()) return;

		const auto excluded = OverlaySubtree();

		const std::wstring finalPath = g_workDir + L"\\tree.tsv";
		const std::wstring tempPath = finalPath + L".tmp";
		size_t written = 0;
		{
			std::ofstream file(tempPath.c_str(), std::ios::trunc | std::ios::binary);
			if (!file)
			{
				Log(L"could not open tree.tsv.tmp for writing");
				return;
			}

			for (const auto& node : m_nodes)
			{
				if (excluded.count(node.Handle)) continue;

				const std::wstring row = std::to_wstring(node.Handle) + L'\t' + std::to_wstring(node.Parent) + L'\t'
					+ std::to_wstring(node.ChildIndex) + L'\t' + Escape(node.Type.c_str()) + L'\t' + Escape(node.Name.c_str())
					+ L'\t' + Escape(node.File.c_str()) + L'\t' + std::to_wstring(node.Line) + L'\t' + std::to_wstring(node.Column);
				file << Utf8(row) << '\n';
				written++;
			}
		}

		_wremove(finalPath.c_str());
		if (_wrename(tempPath.c_str(), finalPath.c_str()) != 0)
		{
			Log(L"could not rename tree.tsv.tmp to tree.tsv");
			return;
		}

		std::wofstream ready(g_workDir + L"\\tree.ready", std::ios::trunc);
		if (ready) ready << written << L"\n";
		Log(L"wrote tree.tsv with " + std::to_wstring(written) + L" element(s)");
	}

	// The handles of our own toolbar's elements, empty whenever the layer is not enumerated at all.
	// Enumeration is parent-before-child in practice, but this closes over the subtree rather than
	// assuming it, since one missed pass would leak our UI into the answer.
	std::set<InstanceHandle> OverlaySubtree() const
	{
		std::set<InstanceHandle> excluded;
		for (const auto& node : m_nodes)
		{
			if (node.Name == OverlayRootName) excluded.insert(node.Handle);
		}

		if (excluded.empty()) return excluded;

		for (bool grew = true; grew; )
		{
			grew = false;
			for (const auto& node : m_nodes)
			{
				if (excluded.count(node.Handle)) continue;
				if (!excluded.count(node.Parent)) continue;
				excluded.insert(node.Handle);
				grew = true;
			}
		}

		return excluded;
	}

	// The host writes one line saying what it wants of this injection (a tree is always written; a
	// "properties <handle>" line asks for that element's property chain as well).
	std::wstring ReadRequest()
	{
		if (g_workDir.empty()) return std::wstring();

		std::wifstream file(g_workDir + L"\\request.txt");
		std::wstring line;
		if (file && std::getline(file, line))
		{
			if (!line.empty() && line.back() == L'\r') line.pop_back();
			return line;
		}

		return std::wstring();
	}

	// One element's property chain: every effective (non-overridden) value with its type, provenance
	// (default/style/local/...), and the source location that set it, plus an element row carrying its
	// type and its own declaration site. Source locations are populated only when the app carries XAML
	// source info; otherwise those fields are empty and the caller degrades to provenance alone.
	/// Turns a handle to a SolidColorBrush into #AARRGGBB, leaving anything else alone.
	///
	/// The handle round-trips through GetIInspectableFromHandle, which is the reverse of what the
	/// overlay uses to identify a clicked element. Only SolidColorBrush is rendered: it is the one
	/// with an unambiguous textual form, and the overwhelming majority of what a hot reload sets. A
	/// gradient or a brush behind a ThemeResource is left as its handle rather than being flattened
	/// into a colour that would misrepresent it -- naming the resource key would be the better answer
	/// there, and is a separate piece of work.
	bool RenderBrush(const wchar_t* valueText, std::wstring& rendered)
	{
		if (!m_diagnostics || !valueText || !valueText[0]) return false;

		const InstanceHandle valueHandle = static_cast<InstanceHandle>(_wcstoui64(valueText, nullptr, 10));
		if (valueHandle == 0) return false;

		::IInspectable* raw = nullptr;
		if (FAILED(m_diagnostics->GetIInspectableFromHandle(valueHandle, &raw)) || !raw) return false;

		winrt::Windows::Foundation::IInspectable instance{ nullptr };
		winrt::attach_abi(instance, raw); // adopt the ref

		const auto brush = instance.try_as<xmedia::SolidColorBrush>();
		if (!brush) return false;

		const auto colour = brush.Color();
		wchar_t buffer[10];
		swprintf_s(buffer, L"#%02X%02X%02X%02X", colour.A, colour.R, colour.G, colour.B);
		rendered = buffer;
		return true;
	}

	void WriteProperties(InstanceHandle handle, bool includeDefaults)
	{
		if (g_workDir.empty()) return;

		unsigned int sourceCount = 0;
		unsigned int valueCount = 0;
		PropertyChainSource* sources = nullptr;
		PropertyChainValue* values = nullptr;
		const HRESULT hr = m_tree->GetPropertyValuesChain(handle, &sourceCount, &sources, &valueCount, &values);
		if (FAILED(hr))
		{
			Log(L"GetPropertyValuesChain(" + std::to_wstring(handle) + L") failed hr=0x" + Hex(hr));
			std::wofstream ready(g_workDir + L"\\properties.ready", std::ios::trunc);
			if (ready) ready << L"error\n";
			return;
		}

		std::wstring elementType;
		std::wstring elementFile;
		unsigned int elementLine = 0;
		unsigned int elementColumn = 0;
		for (unsigned int i = 0; i < sourceCount; i++)
		{
			if (elementType.empty() && sources[i].TargetType) elementType = sources[i].TargetType;
			const bool localWithSource = sources[i].Source == BaseValueSourceLocal && sources[i].SrcInfo.FileName && sources[i].SrcInfo.FileName[0];
			if (localWithSource && elementFile.empty())
			{
				elementFile = sources[i].SrcInfo.FileName;
				elementLine = sources[i].SrcInfo.LineNumber;
				elementColumn = sources[i].SrcInfo.ColumnNumber;
			}
		}

		const std::wstring finalPath = g_workDir + L"\\properties.tsv";
		const std::wstring tempPath = finalPath + L".tmp";
		unsigned int written = 0;
		{
			std::ofstream file(tempPath.c_str(), std::ios::trunc | std::ios::binary);
			if (!file)
			{
				Log(L"could not open properties.tsv.tmp for writing");
				return;
			}

			const std::wstring elementRow = L"E\t" + Escape(elementType.c_str()) + L'\t' + Escape(elementFile.c_str())
				+ L'\t' + std::to_wstring(elementLine) + L'\t' + std::to_wstring(elementColumn);
			file << Utf8(elementRow) << '\n';

			for (unsigned int i = 0; i < valueCount && written < 256; i++)
			{
				const PropertyChainValue& value = values[i];
				if (value.Overridden) continue; // Only the effective value of each property.
				if (!includeDefaults && IsComposition(value.PropertyName)) continue;

				std::wstring provenance = L"Unknown";
				std::wstring file2;
				unsigned int line = 0;
				unsigned int column = 0;
				if (value.PropertyChainIndex < sourceCount)
				{
					const PropertyChainSource& source = sources[value.PropertyChainIndex];
					provenance = Provenance(source.Source);
					if (!includeDefaults && source.Source == BaseValueSourceDefault) continue;

					// The location belongs to the *source object*, not to the property.
					// PropertyChainValue carries no source info at all -- the granularity this API
					// offers is per source, so a per-property file and line is a fabrication by
					// construction. For a Local value the source is the element itself, so every
					// locally-set property was being stamped with the element's own tag position:
					// six composition properties on a two-attribute element all claimed the same
					// file, line and column, and a reader who went and looked would find nothing
					// there. Worse, a genuine attribution was byte-identical to that.
					//
					// So it is emitted only when the source is something *other* than the element,
					// where it locates a real and different thing -- the style or template that set
					// the value, which is information the caller cannot get any other way. When the
					// source is the element, the element's own row already carries its position and
					// the property says nothing it cannot support.
					const bool sourceIsElement = source.Handle == handle;
					if (!sourceIsElement && source.SrcInfo.FileName && source.SrcInfo.FileName[0])
					{
						file2 = source.SrcInfo.FileName;
						line = source.SrcInfo.LineNumber;
						column = source.SrcInfo.ColumnNumber;
					}
				}

				const bool isNull = (value.MetadataBits & IsValueNull) != 0;
				const wchar_t* valueText = isNull ? L"" : (value.Value ? value.Value : L"");

				// A brush arrives as an object handle, which is the one thing a caller cannot use:
				// setting Background="Blue" and reading back "2447634627144" makes hot reload
				// unverifiable, and confirming the edit landed is the first thing anyone does after
				// applying one. So a SolidColorBrush is resolved and rendered as its colour.
				std::wstring rendered;
				if (!isNull && (value.MetadataBits & IsValueHandle) != 0 && RenderBrush(valueText, rendered))
				{
					valueText = rendered.c_str();
				}
				const wchar_t* valueType = value.ValueType && value.ValueType[0] ? value.ValueType : (value.Type ? value.Type : L"");

				std::wstring row = L"P\t" + Escape(value.PropertyName ? value.PropertyName : L"") + L'\t'
					+ Escape(valueText) + L'\t' + Escape(valueType) + L'\t' + Escape(value.DeclaringType ? value.DeclaringType : L"")
					+ L'\t' + provenance + L'\t' + Escape(file2.c_str()) + L'\t' + std::to_wstring(line) + L'\t'
					+ std::to_wstring(column) + L'\t' + (isNull ? L"1" : L"0");
				file << Utf8(row) << '\n';
				written++;
			}
		}

		_wremove(finalPath.c_str());
		if (_wrename(tempPath.c_str(), finalPath.c_str()) != 0)
		{
			Log(L"could not rename properties.tsv.tmp to properties.tsv");
			return;
		}

		std::wofstream ready(g_workDir + L"\\properties.ready", std::ios::trunc);
		if (ready) ready << written << L"\n";
		Log(L"wrote properties.tsv with " + std::to_wstring(written) + L" propert(y/ies) for handle " + std::to_wstring(handle));

		FreePropertyChain(sources, sourceCount, values, valueCount);
	}

	// Applies each command from commands.tsv and writes apply.tsv -- one row per command with its
	// outcome (applied / target not found / property not found / a failure code) -- so the host can
	// report per-command results to the agent (#12).
	void ApplyCommands()
	{
		if (g_workDir.empty()) return;

		const std::vector<Command> commands = ReadCommands();
		Log(L"applying " + std::to_wstring(commands.size()) + L" command(s)");

		const std::wstring finalPath = g_workDir + L"\\apply.tsv";
		const std::wstring tempPath = finalPath + L".tmp";
		{
			std::ofstream file(tempPath.c_str(), std::ios::trunc | std::ios::binary);
			for (const auto& command : commands)
			{
				std::wstring status;
				if (command.op == L"SetProperty") status = ApplySetProperty(command);
				else if (command.op == L"ClearProperty") status = ApplyClearProperty(command);
				else status = L"unsupported op";

				if (file)
				{
					const std::wstring row = command.op + L'\t' + Escape(command.target.c_str()) + L'\t'
						+ Escape(command.property.c_str()) + L'\t' + status;
					file << Utf8(row) << '\n';
				}
			}
		}

		_wremove(finalPath.c_str());
		if (_wrename(tempPath.c_str(), finalPath.c_str()) != 0)
		{
			Log(L"could not rename apply.tsv.tmp to apply.tsv");
			return;
		}

		std::wofstream ready(g_workDir + L"\\apply.ready", std::ios::trunc);
		if (ready) ready << commands.size() << L"\n";
	}

	// The host sends the type it thinks the value should be, inferred from the property's name and the
	// shape of the string. That guess is right for the common cases and wrong whenever a property's
	// type cannot be read off its value -- CornerRadius="0" parses as a number, so it arrived as a
	// Double, which CreateInstance built without complaint and SetProperty then rejected with a bare
	// E_FAIL. So the hint is tried first, because it carries intent the runtime does not have (a colour
	// string is meant as a SolidColorBrush even where the live value is some other Brush), and the
	// property's own declared type is the fallback, because it is a fact rather than a guess.
	std::wstring ApplySetProperty(const Command& command)
	{
		InstanceHandle target = 0;
		if (!Find(command.target, target)) return L"target not found";

		unsigned int index = 0;
		std::wstring declaredType;
		if (!PropertyIndex(target, command.property, index, declaredType)) return L"property not found";

		std::wstring attempted;
		std::wstring failure;
		for (const auto& type : { command.valueType, declaredType })
		{
			if (type.empty() || type == attempted) continue;
			attempted = type;

			InstanceHandle valueHandle = 0;
			BSTR typeName = SysAllocString(type.c_str());
			BSTR value = SysAllocString(command.value.c_str());
			HRESULT hr = m_tree->CreateInstance(typeName, value, &valueHandle);
			SysFreeString(typeName);
			SysFreeString(value);
			if (FAILED(hr))
			{
				failure = L"CreateInstance(" + type + L") failed 0x" + Hex(hr);
				continue;
			}

			hr = m_tree->SetProperty(target, valueHandle, index);
			if (hr == S_OK)
			{
				Log(L"  set " + command.target + L"." + command.property + L" = " + command.value
					+ L" (as " + type + L")");
				return L"applied";
			}

			failure = L"SetProperty(" + type + L") failed 0x" + Hex(hr);
		}

		if (failure.empty()) failure = L"no value type to build " + command.property + L" from";
		Log(L"  " + command.target + L"." + command.property + L": " + failure);
		return failure;
	}

	std::wstring ApplyClearProperty(const Command& command)
	{
		InstanceHandle target = 0;
		if (!Find(command.target, target)) return L"target not found";

		unsigned int index = 0;
		if (!PropertyIndex(target, command.property, index)) return L"property not found";

		const HRESULT hr = m_tree->ClearProperty(target, index);
		if (hr != S_OK) return L"ClearProperty failed 0x" + Hex(hr);

		Log(L"  cleared " + command.target + L"." + command.property);
		return L"applied";
	}

	bool Find(const std::wstring& name, InstanceHandle& handle)
	{
		const auto it = m_byName.find(name);
		if (it == m_byName.end())
		{
			Log(L"  target '" + name + L"' not found in the live tree");
			return false;
		}

		handle = it->second;
		return true;
	}

	bool PropertyIndex(InstanceHandle handle, const std::wstring& name, unsigned int& index)
	{
		std::wstring ignored;
		return PropertyIndex(handle, name, index, ignored);
	}

	// Also reports the property's own declared value type. That is the one authoritative answer to
	// "what does this property want", and the apply side needs it: a value built as the wrong type is
	// created quite happily and only fails at SetProperty, with an E_FAIL that names nothing.
	bool PropertyIndex(InstanceHandle handle, const std::wstring& name, unsigned int& index, std::wstring& valueType)
	{
		unsigned int sourceCount = 0;
		unsigned int propertyCount = 0;
		PropertyChainSource* sources = nullptr;
		PropertyChainValue* values = nullptr;
		const HRESULT hr = m_tree->GetPropertyValuesChain(handle, &sourceCount, &sources, &propertyCount, &values);
		if (FAILED(hr)) return false;

		bool found = false;
		for (unsigned int i = 0; i < propertyCount; i++)
		{
			if (!found && values[i].PropertyName && name == values[i].PropertyName)
			{
				index = values[i].Index;
				valueType = values[i].Type ? values[i].Type : L"";
				found = true;
			}
		}

		FreePropertyChain(sources, sourceCount, values, propertyCount);
		return found;
	}

	static void FreePropertyChain(PropertyChainSource* sources, unsigned int sourceCount, PropertyChainValue* values, unsigned int valueCount)
	{
		for (unsigned int i = 0; i < sourceCount; i++)
		{
			SysFreeString(sources[i].TargetType);
			SysFreeString(sources[i].Name);
			SysFreeString(sources[i].SrcInfo.FileName);
			SysFreeString(sources[i].SrcInfo.Hash);
		}

		for (unsigned int i = 0; i < valueCount; i++)
		{
			SysFreeString(values[i].Type);
			SysFreeString(values[i].DeclaringType);
			SysFreeString(values[i].ValueType);
			SysFreeString(values[i].ItemType);
			SysFreeString(values[i].Value);
			SysFreeString(values[i].PropertyName);
		}

		CoTaskMemFree(sources);
		CoTaskMemFree(values);
	}

	std::vector<Command> ReadCommands()
	{
		std::vector<Command> commands;
		if (g_workDir.empty()) return commands;

		std::wifstream file(g_workDir + L"\\commands.tsv");
		if (!file) return commands;

		std::wstring line;
		while (std::getline(file, line))
		{
			if (!line.empty() && line.back() == L'\r') line.pop_back();
			if (line.empty()) continue;

			std::vector<std::wstring> fields;
			std::wstringstream stream(line);
			std::wstring field;
			while (std::getline(stream, field, L'\t')) fields.push_back(field);
			fields.resize(5);
			commands.push_back({ fields[0], fields[1], fields[2], fields[3], fields[4] });
		}

		return commands;
	}

	void Unadvise()
	{
		if (m_tree) m_tree->UnadviseVisualTreeChange(this);
		if (m_tree) { m_tree->Release(); m_tree = nullptr; }
		if (m_diagnostics) { m_diagnostics->Release(); m_diagnostics = nullptr; }
	}

	std::atomic<long> m_refs{ 1 };
	IXamlDiagnostics* m_diagnostics = nullptr;
	IVisualTreeService* m_tree = nullptr;
	std::vector<TreeNode> m_nodes;
	std::map<std::wstring, InstanceHandle> m_byName;
};

class RoseTapFactory final : public IClassFactory
{
public:
	HRESULT STDMETHODCALLTYPE QueryInterface(REFIID riid, void** ppv) override
	{
		if (!ppv) return E_POINTER;
		if (riid == IID_IUnknown || riid == IID_IClassFactory)
		{
			*ppv = static_cast<IClassFactory*>(this);
			AddRef();
			return S_OK;
		}
		*ppv = nullptr;
		return E_NOINTERFACE;
	}

	ULONG STDMETHODCALLTYPE AddRef() override { return 2; }
	ULONG STDMETHODCALLTYPE Release() override { return 1; }

	HRESULT STDMETHODCALLTYPE CreateInstance(IUnknown* outer, REFIID riid, void** ppv) override
	{
		if (outer) return CLASS_E_NOAGGREGATION;
		auto* tap = new (std::nothrow) RoseTap();
		if (!tap) return E_OUTOFMEMORY;
		const HRESULT hr = tap->QueryInterface(riid, ppv);
		tap->Release();
		return hr;
	}

	HRESULT STDMETHODCALLTYPE LockServer(BOOL lock) override
	{
		lock ? ++g_lockCount : --g_lockCount;
		return S_OK;
	}
};

static RoseTapFactory g_factory;

extern "C" HRESULT STDAPICALLTYPE DllGetClassObject(REFCLSID rclsid, REFIID riid, void** ppv)
{
	if (rclsid == CLSID_RoseTap)
	{
		return g_factory.QueryInterface(riid, ppv);
	}
	*ppv = nullptr;
	return CLASS_E_CLASSNOTAVAILABLE;
}

extern "C" HRESULT STDAPICALLTYPE DllCanUnloadNow()
{
	return g_lockCount == 0 ? S_OK : S_FALSE;
}
