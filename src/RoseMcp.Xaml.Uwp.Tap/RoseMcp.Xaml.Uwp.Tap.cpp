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
#include <winrt/Windows.UI.Input.h>
#include <winrt/Windows.UI.Xaml.Input.h>

namespace xaml = winrt::Windows::UI::Xaml;
namespace xcontrols = winrt::Windows::UI::Xaml::Controls;
namespace xmedia = winrt::Windows::UI::Xaml::Media;
namespace xinput = winrt::Windows::UI::Xaml::Input;

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

static void Log(const std::wstring& line)
{
	OutputDebugStringW((L"[RoseMcp.Xaml.Uwp.Tap] " + line + L"\n").c_str());

	std::lock_guard<std::mutex> guard(g_logMutex);
	if (g_workDir.empty()) return;

	std::wofstream file(g_workDir + L"\\rosemcp.xaml.uwp.tap.log", std::ios::app);
	if (file) file << line << L"\n";
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
			Log(L"overlay: toolbar installed on the UI layer");
		}
		catch (winrt::hresult_error const& error)
		{
			Log(std::wstring(L"overlay: install failed: ") + error.message().c_str());
			m_root = nullptr;
		}
	}

	bool Installed() const { return static_cast<bool>(m_root); }

	// Arms select mode. Returns whether it is armed, so the host can confirm rather than assume --
	// including the case where the person had already armed it from the toolbar.
	bool BeginSelect()
	{
		if (!m_root) return false;
		if (m_selecting)
		{
			WriteState();
			return true;
		}

		try
		{
			m_capture = xcontrols::Grid();

			// A faint wash, not a plain Transparent: this is the "select mode is on" affordance, and a
			// layer that swallows every click while looking like nothing at all is a layer that reads
			// as the app having hung.
			m_capture.Background(Brush(0x1E, 0x00, 0x78, 0xD4));
			m_capture.HorizontalAlignment(xaml::HorizontalAlignment::Stretch);
			m_capture.VerticalAlignment(xaml::VerticalAlignment::Stretch);
			m_capture.PointerPressed(
				[this](winrt::Windows::Foundation::IInspectable const&, xinput::PointerRoutedEventArgs const& e)
				{
					OnPick(e);
				});

			// Beneath the Canvas that holds the toolbar, so the toolbar's own buttons stay clickable
			// while the rest of the window is collecting the pick.
			m_root.Children().InsertAt(0, m_capture);
			m_selecting = true;
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

	void EndSelect()
	{
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
		Chrome();
		WriteState();
	}

private:
	static xmedia::SolidColorBrush Brush(uint8_t a, uint8_t r, uint8_t g, uint8_t b)
	{
		return xmedia::SolidColorBrush(winrt::Windows::UI::Color{ a, r, g, b });
	}

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
		m_root.HorizontalAlignment(xaml::HorizontalAlignment::Stretch);
		m_root.VerticalAlignment(xaml::VerticalAlignment::Stretch);

		m_canvas = xcontrols::Canvas();
		m_root.Children().Append(m_canvas);

		m_panel = xcontrols::Border();
		m_panel.Background(Brush(0xF0, 0x1C, 0x1C, 0x22));
		m_panel.BorderBrush(Brush(0xFF, 0x53, 0x53, 0x63));
		m_panel.BorderThickness(xaml::Thickness{ 1, 1, 1, 1 });
		m_panel.CornerRadius(xaml::CornerRadius{ 6, 6, 6, 6 });

		auto content = xcontrols::Grid();
		content.Children().Append(BuildFullView());
		content.Children().Append(BuildThumb());
		m_panel.Child(content);

		m_canvas.Children().Append(m_panel);

		const auto bounds = xaml::Window::Current().Bounds();
		m_left = bounds.Width > 248.0 ? bounds.Width - 232.0 : 16.0;
		m_top = 16.0;
		Place();
		Chrome();
	}

	xaml::UIElement BuildFullView()
	{
		m_full = xcontrols::StackPanel();
		m_full.Orientation(xcontrols::Orientation::Vertical);
		m_full.Spacing(4);
		m_full.Padding(xaml::Thickness{ 6, 4, 6, 6 });

		auto header = xcontrols::StackPanel();
		header.Orientation(xcontrols::Orientation::Horizontal);
		header.Spacing(6);
		header.Children().Append(DragHandle());
		header.Children().Append(Label(L"RoseMCP", 12.0, 0xB8));
		header.Children().Append(Chip(L"Hide", [this] { Collapse(true); }));
		m_full.Children().Append(header);

		auto modes = xcontrols::StackPanel();
		modes.Orientation(xcontrols::Orientation::Horizontal);
		modes.Spacing(4);
		m_idleButton = Chip(L"Idle", [this] { EndSelect(); });
		m_selectButton = Chip(L"Select Element", [this] { BeginSelect(); });
		modes.Children().Append(m_idleButton);
		modes.Children().Append(m_selectButton);
		m_full.Children().Append(modes);

		m_status = Label(L"", 11.0, 0x8C);
		m_status.TextWrapping(xaml::TextWrapping::Wrap);
		m_status.MaxWidth(200);
		m_full.Children().Append(m_status);

		return m_full;
	}

	// The collapsed view is the drag handle: one small grip that moves the toolbar, and a tap on it
	// brings the full view back. XAML suppresses Tapped after a manipulation, so the two do not fight.
	xaml::UIElement BuildThumb()
	{
		m_thumb = xcontrols::Border();
		m_thumb.Visibility(xaml::Visibility::Collapsed);
		m_thumb.Padding(xaml::Thickness{ 8, 4, 8, 4 });
		m_thumb.Child(Label(GripGlyph, 13.0, 0xB8));
		AttachDrag(m_thumb);
		m_thumb.Tapped(
			[this](winrt::Windows::Foundation::IInspectable const&, xinput::TappedRoutedEventArgs const& e)
			{
				e.Handled(true);
				Collapse(false);
			});

		return m_thumb;
	}

	xcontrols::TextBlock Label(const wchar_t* text, double size, uint8_t grey)
	{
		auto block = xcontrols::TextBlock();
		block.Text(text);
		block.FontSize(size);
		block.Foreground(Brush(0xFF, grey, grey, grey));
		block.VerticalAlignment(xaml::VerticalAlignment::Center);
		return block;
	}

	xcontrols::Border DragHandle()
	{
		auto handle = xcontrols::Border();

		// A Background is what makes it take input at all; transparent keeps it from being seen.
		handle.Background(Brush(0x00, 0x00, 0x00, 0x00));
		handle.Padding(xaml::Thickness{ 2, 0, 2, 0 });
		handle.Child(Label(GripGlyph, 13.0, 0x8C));
		AttachDrag(handle);
		return handle;
	}

	xcontrols::Button Chip(const wchar_t* text, std::function<void()> action)
	{
		auto button = xcontrols::Button();
		button.Content(winrt::box_value(winrt::hstring{ text }));
		button.FontSize(11.0);
		button.Padding(xaml::Thickness{ 8, 2, 8, 3 });
		button.MinWidth(0);
		button.MinHeight(0);
		button.Background(Brush(0xFF, 0x2C, 0x2C, 0x36));
		button.Foreground(Brush(0xFF, 0xDC, 0xDC, 0xE4));
		button.BorderThickness(xaml::Thickness{ 0, 0, 0, 0 });
		button.Click(
			[action](winrt::Windows::Foundation::IInspectable const&, xaml::RoutedEventArgs const&) { action(); });
		return button;
	}

	void AttachDrag(xaml::UIElement const& handle)
	{
		handle.ManipulationMode(xinput::ManipulationModes::TranslateX | xinput::ManipulationModes::TranslateY);
		handle.ManipulationDelta(
			[this](winrt::Windows::Foundation::IInspectable const&, xinput::ManipulationDeltaRoutedEventArgs const& e)
			{
				const auto translation = e.Delta().Translation;
				m_left += translation.X;
				m_top += translation.Y;
				Place();
				e.Handled(true);
			});
	}

	// Kept inside the window, so a toolbar dragged at the edge cannot be lost off-screen.
	void Place()
	{
		if (!m_panel) return;

		const auto bounds = xaml::Window::Current().Bounds();
		m_left = Clamp(m_left, 0.0, bounds.Width - m_panel.ActualWidth());
		m_top = Clamp(m_top, 0.0, bounds.Height - m_panel.ActualHeight());
		xcontrols::Canvas::SetLeft(m_panel, m_left);
		xcontrols::Canvas::SetTop(m_panel, m_top);
	}

	void Collapse(bool collapsed)
	{
		if (!m_full || !m_thumb) return;

		m_full.Visibility(collapsed ? xaml::Visibility::Collapsed : xaml::Visibility::Visible);
		m_thumb.Visibility(collapsed ? xaml::Visibility::Visible : xaml::Visibility::Collapsed);
	}

	// Which mode is current, said in the toolbar itself: the active button is lit and the status line
	// carries the last pick. Without this there is no way to tell armed from idle.
	void Chrome()
	{
		if (m_idleButton && m_selectButton)
		{
			m_idleButton.Background(m_selecting ? Brush(0xFF, 0x2C, 0x2C, 0x36) : Brush(0xFF, 0x3E, 0x3E, 0x4C));
			m_selectButton.Background(m_selecting ? Brush(0xFF, 0x00, 0x5A, 0xA8) : Brush(0xFF, 0x2C, 0x2C, 0x36));
		}

		if (!m_status) return;

		if (m_selecting)
		{
			m_status.Text(L"Click an element to select it.");
		}
		else if (!m_selectedLabel.empty())
		{
			m_status.Text(L"Selected " + m_selectedLabel);
		}
		else
		{
			m_status.Text(L"Idle.");
		}
	}

	void OnPick(xinput::PointerRoutedEventArgs const& e)
	{
		try
		{
			e.Handled(true); // Swallow the click so it does not also reach the app.
			const auto point = e.GetCurrentPoint(nullptr).Position();
			const auto root = xaml::Window::Current().Content();

			for (auto&& element : xmedia::VisualTreeHelper::FindElementsInHostCoordinates(point, root, true))
			{
				if (IsOurs(element)) continue; // Our own layers are on top; look past them.
				Record(element);
				break;
			}
		}
		catch (winrt::hresult_error const& error)
		{
			Log(std::wstring(L"overlay: hit test failed: ") + error.message().c_str());
		}

		EndSelect();
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

	void Record(xaml::UIElement const& element)
	{
		InstanceHandle handle = 0;
		if (m_diagnostics)
		{
			m_diagnostics->GetHandleFromIInspectable(reinterpret_cast<::IInspectable*>(winrt::get_abi(element)), &handle);
		}

		std::wstring typeName{ winrt::get_class_name(element) };
		std::wstring name;
		if (const auto frameworkElement = element.try_as<xaml::FrameworkElement>())
		{
			name = frameworkElement.Name();
		}

		m_selectedLabel = name.empty() ? typeName : (typeName + L" (" + name + L")");

		if (g_workDir.empty()) return;

		{
			std::ofstream file((g_workDir + L"\\selection.tsv").c_str(), std::ios::trunc | std::ios::binary);
			if (file)
			{
				const std::wstring row = std::to_wstring(handle) + L'\t' + Escape(typeName.c_str()) + L'\t' + Escape(name.c_str());
				file << Utf8(row) << '\n';
			}
		}

		std::wofstream ready(g_workDir + L"\\selection.ready", std::ios::trunc);
		if (ready) ready << handle << L"\n";
		Log(L"overlay: recorded " + m_selectedLabel);
	}

	// The mode, on disk, because the person can change it from the toolbar without the host being in
	// the conversation at all -- so the host has to be able to ask, rather than remember what it set.
	void WriteState()
	{
		if (g_workDir.empty()) return;

		std::wofstream state(g_workDir + L"\\overlay.state", std::ios::trunc);
		if (state) state << (m_selecting ? L"select" : L"idle") << L"\n";

		if (!m_selecting) return;

		std::wofstream armed(g_workDir + L"\\select.ready", std::ios::trunc);
		if (armed) armed << L"armed\n";
	}

	static constexpr const wchar_t* GripGlyph = L"\x2237";

	IXamlDiagnostics* m_diagnostics = nullptr;
	xcontrols::Panel m_layer{ nullptr };
	xcontrols::Grid m_root{ nullptr };
	xcontrols::Canvas m_canvas{ nullptr };
	xcontrols::Border m_panel{ nullptr };
	xcontrols::Grid m_capture{ nullptr };
	xcontrols::StackPanel m_full{ nullptr };
	xcontrols::Border m_thumb{ nullptr };
	xcontrols::TextBlock m_status{ nullptr };
	xcontrols::Button m_idleButton{ nullptr };
	xcontrols::Button m_selectButton{ nullptr };
	std::wstring m_selectedLabel;
	double m_left = 16.0;
	double m_top = 16.0;
	bool m_selecting = false;
};

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

		const std::wstring request = ReadRequest();
		if (request.rfind(L"properties ", 0) == 0)
		{
			// "properties <handle>" gives the set (non-default) properties; a trailing " all" includes
			// the framework defaults too. Filtering defaults out keeps the interesting values from being
			// pushed past the row cap on an element with hundreds of properties.
			const bool includeDefaults = request.size() >= 4 && request.compare(request.size() - 4, 4, L" all") == 0;
			WriteProperties(static_cast<InstanceHandle>(_wcstoui64(request.c_str() + 11, nullptr, 10)), includeDefaults);
		}
		else if (request == L"apply")
		{
			ApplyCommands();
		}
		else if (request == L"select")
		{
			// Arming from the agent and arming from the toolbar are the same act; whichever happens,
			// the overlay writes select.ready and the host reads the pick back the same way.
			Overlay().BeginSelect();
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

		m_nodes.push_back({ element.Handle, relation.Parent, relation.ChildIndex,
			element.Type ? element.Type : L"", element.Name ? element.Name : L"" });

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
					+ std::to_wstring(node.ChildIndex) + L'\t' + Escape(node.Type.c_str()) + L'\t' + Escape(node.Name.c_str());
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

				std::wstring provenance = L"Unknown";
				std::wstring file2;
				unsigned int line = 0;
				unsigned int column = 0;
				if (value.PropertyChainIndex < sourceCount)
				{
					const PropertyChainSource& source = sources[value.PropertyChainIndex];
					provenance = Provenance(source.Source);
					if (!includeDefaults && source.Source == BaseValueSourceDefault) continue;
					if (source.SrcInfo.FileName && source.SrcInfo.FileName[0])
					{
						file2 = source.SrcInfo.FileName;
						line = source.SrcInfo.LineNumber;
						column = source.SrcInfo.ColumnNumber;
					}
				}

				const bool isNull = (value.MetadataBits & IsValueNull) != 0;
				const wchar_t* valueText = isNull ? L"" : (value.Value ? value.Value : L"");
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

	std::wstring ApplySetProperty(const Command& command)
	{
		InstanceHandle target = 0;
		if (!Find(command.target, target)) return L"target not found";

		unsigned int index = 0;
		if (!PropertyIndex(target, command.property, index)) return L"property not found";

		BSTR typeName = SysAllocString(command.valueType.c_str());
		BSTR value = SysAllocString(command.value.c_str());
		InstanceHandle valueHandle = 0;
		HRESULT hr = m_tree->CreateInstance(typeName, value, &valueHandle);
		SysFreeString(typeName);
		SysFreeString(value);
		if (FAILED(hr)) return L"CreateInstance failed 0x" + Hex(hr);

		hr = m_tree->SetProperty(target, valueHandle, index);
		if (hr != S_OK) return L"SetProperty failed 0x" + Hex(hr);

		Log(L"  set " + command.target + L"." + command.property + L" = " + command.value);
		return L"applied";
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
