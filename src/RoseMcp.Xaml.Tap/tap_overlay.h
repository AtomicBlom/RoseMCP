#pragma once

// The resident in-app toolbar, written entirely against seven namespace aliases rather than against
// either XAML root directly. That is what makes one source serve both frameworks: the provider
// defines xaml, xcontrols, xmedia, xanim, xinput, xshapes and ximaging for the stack it binds to, and
// nothing below this line names Windows.UI.Xaml or Microsoft.UI.Xaml at all.
//
// Deliberately not self-contained, and it cannot be. It is included after the provider has set up its
// projections, its aliases and its CLSID, because a header that pulled in one framework's projections
// itself would be the very coupling the split exists to remove. tap_channel.h and tap_diagnostics.h
// come first for the same reason: the overlay logs and writes markers through the channel.
//
// The provider must also define, before including this:
//
//   bool RoseTapWatchPointer(
//       xaml::UIElement const& anchor,
//       std::function<void(winrt::Windows::Foundation::Point const&)> onMove,
//       std::function<void()> onExit);
//
// which is the *only* thing here a namespace alias cannot supply (#75). It installs a passive
// observer of pointer movement in the anchor's window, reporting positions in the anchor's own
// coordinate space, and returns false when the framework offers nothing suitable. Two properties are
// required of it and neither is negotiable: it must consume nothing, and it must see moves the app
// has already marked handled. Everything else in this file is an alias swap.

// The name the overlay's root carries in the live tree, so the tree snapshot can drop RoseMCP's own
// UI instead of reporting it as part of the app's.
static const wchar_t* const OverlayRootName = L"__RoseMcpOverlay";

// Set true to stop the toolbar hiding itself from the tree, so RoseMCP's own tools can be pointed at
// RoseMCP's own UI. Off in anything anyone else runs: the toolbar is not part of the app, and
// reporting it as though it were is exactly the noise the filter exists to remove.
//
// It is here rather than being a line somebody comments out, because the question it answers comes up
// whenever the overlay misbehaves and the alternative is expensive: a log statement per theory, each
// costing a rebuild and a relaunch, and each reporting only the fields somebody thought to print. With
// the filter off, rose_xaml_tree and rose_xaml_properties answer about the toolbar exactly as they do
// about the app -- every property with its provenance, which is what separates a value that was set
// from one that is merely the framework's default.
static constexpr bool RoseTapShowOverlayInTree = false;

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
static const wchar_t* const IconZoom = L"\xE71E";     // Zoom -- a plain magnifier, for entering the mode
static const wchar_t* const IconZoomIn = L"\xE8A3";   // ZoomIn -- magnifier with a plus
static const wchar_t* const IconZoomOut = L"\xE71F";  // ZoomOut -- magnifier with a minus
static const wchar_t* const IconPixels = L"\xE7A8";   // GridView -- a lattice, for the pixel lens
static const wchar_t* const IconScale = L"\xE740";    // FullScreen -- arrows out, for scaling the app itself

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
	void Install(IXamlDiagnostics* diagnostics, const std::vector<InstanceHandle>& appElements = {})
	{
		if (m_root || !diagnostics) return;

		try
		{
			// The layer for the root the app is actually showing, where the framework distinguishes
			// them, and the only layer there is where it does not.
			//
			// GetUiLayer takes no argument, and on WinUI 3 that is the bug rather than a convenience:
			// its own documentation says IXamlDiagnostics2 exists to replace "IXamlDiagnostics APIs
			// that assume there is only one window". A desktop app can have several XamlRoots, and the
			// layer that comes back without naming one lays out at the right size, reports Visible,
			// holds its child -- and is never painted. Nothing about it can be inspected to discover
			// that, which is what made this expensive to find.
			::IInspectable* rawLayer = nullptr;
			const auto rootHandle = XamlRootHandle(diagnostics, appElements);
			const bool perRoot = RoseTapGetUiLayerForRoot(diagnostics, rootHandle, &rawLayer);

			// The fallback is a branch of its own and not another rung of this ladder. Written as a
			// chain of else-ifs, the two lines that merely *describe* what happened came before the one
			// that fetches the layer, so on UWP -- where there is no per-root layer but the root handle
			// resolves perfectly well -- it reported which path it was taking and then took neither.
			// The overlay never installed and select mode stopped arming.
			if (perRoot)
			{
				Log(L"overlay: using the diagnostics layer for the app's own XamlRoot");
			}
			else
			{
				if (!rootHandle && !appElements.empty())
				{
					Log(L"overlay: could not resolve the app's XamlRoot from "
						+ std::to_wstring(appElements.size()) + L" candidate element(s)");
				}

				if (FAILED(diagnostics->GetUiLayer(&rawLayer)) || !rawLayer)
				{
					Log(L"overlay: GetUiLayer returned nothing");
					return;
				}
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

			const auto bounds = Extent();
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

	// The handle of the XamlRoot the app's elements belong to, which is what GetUiLayerForXamlRoot
	// wants.
	//
	// Asked of elements the tree walk already enumerated, because those are the ones known to be in the
	// app's own tree. Asking our own layer would be circular -- it is the layer whose root is in
	// question -- and that circularity is why an earlier check reported "roots shared" whatever was
	// true.
	//
	// Several candidates rather than one, because the first node enumerated is not a UIElement: on
	// WinUI 3 it is the DesktopWindowXamlSource that hosts the tree, which has no XamlRoot to give.
	// Taking the first handle and trusting it silently fell back to the layer that is never drawn.
	InstanceHandle XamlRootHandle(IXamlDiagnostics* diagnostics, const std::vector<InstanceHandle>& candidates) const
	{
		if (!diagnostics) return 0;

		for (const auto candidate : candidates)
		{
			if (!candidate) continue;

			try
			{
				::IInspectable* rawElement = nullptr;
				if (FAILED(diagnostics->GetIInspectableFromHandle(candidate, &rawElement)) || !rawElement) continue;

				winrt::Windows::Foundation::IInspectable element{ nullptr };
				winrt::attach_abi(element, rawElement);

				const auto asUiElement = element.try_as<xaml::UIElement>();
				if (!asUiElement) continue;

				const auto root = asUiElement.XamlRoot();
				if (!root) continue;

				InstanceHandle handle = 0;
				const auto rootInspectable = root.as<winrt::Windows::Foundation::IInspectable>();
				if (FAILED(diagnostics->GetHandleFromIInspectable(
					reinterpret_cast<::IInspectable*>(winrt::get_abi(rootInspectable)), &handle)))
				{
					continue;
				}

				if (handle) return handle;
			}
			catch (winrt::hresult_error const&)
			{
				// A handle that will not resolve is simply not the one; try the next.
			}
		}

		return 0;
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
		// Zoom counts as an operation for this purpose even though nothing asked for it: it is a mode
		// somebody is working in, and a toolbar carrying the factor and the colour readout is no use
		// at half opacity while the pointer is out in the app, which is exactly where it has to be.
		const bool busy = !m_operations.empty() || m_overPanel || m_zoom != Zoom::Off;
		m_panelFade.To(busy ? PanelNear : PanelFar);
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

			// The layer about to be inserted has not been arranged, and the extent of the one before it is
			// not an answer about this one.
			{
				std::lock_guard<std::mutex> guard(m_armedMutex);
				m_armedKnown = false;
			}

			// A faint wash, not a plain Transparent: this is the "select mode is on" affordance, and a
			// layer that swallows every click while looking like nothing at all is a layer that reads
			// as the app having hung.
			m_capture.Background(Brush(0x14, 0x00, 0x78, 0xD4));

			// Explicit, for the same reason the root is: it has to cover the window, and it cannot get
			// that from an alignment.
			const auto bounds = Extent();
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

	/// The pick as rows, the mode, and why the last selection went away: everything a later request
	/// needs to answer "what is selected", with no file in between. Rows are in the shape the work
	/// folder writes, so one parser on the host serves whichever channel carried them.
	const std::string& SelectionRows() const { return m_selectionRows; }
	const std::wstring& GoneReason() const { return m_goneReason; }
	bool Selecting() const { return m_selecting; }
	bool JustMyXaml() const { return m_justMyXaml; }

	/// <summary>
	/// Waits for the capture layer to be arranged, and reports the extent it was given.
	/// </summary>
	/// <remarks>
	/// Arming inserts the layer; XAML arranges it on the next layout pass, and its size means nothing
	/// until then -- which is why the extent is reported from SizeChanged rather than by the call that
	/// armed. A caller on the reader thread can wait for that pass, because the thread doing the
	/// arranging is not this one. A caller already on the UI thread must never wait here, since the pass
	/// it is waiting for is the work it is itself blocking.
	/// <para>
	/// The extent is the answer worth having. Select mode that is on, invisible and cannot be pointed at
	/// is indistinguishable from a working one if all that is checked is that it armed.
	/// </para>
	/// </remarks>
	bool WaitForArmedExtent(int& width, int& height, unsigned int timeoutMs)
	{
		std::unique_lock<std::mutex> guard(m_armedMutex);
		m_armedSignal.wait_for(guard, std::chrono::milliseconds(timeoutMs), [this] { return m_armedKnown; });

		width = m_armedWidth;
		height = m_armedHeight;
		return m_armedKnown;
	}

	/// Rewrites the state file, whatever the request was. Called at the end of every injection so
	/// that the file always carries the generation of the request that has just been served -- which
	/// is what lets the host tell a current answer from one left behind by the previous request. A
	/// request that changes nothing about the mode still has to leave that proof behind, or reading
	/// the state after a plain tree call would come back as "cannot say".
	void RefreshState()
	{
		WriteState();
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
		const bool had = Clear(nullptr);

		// Written after the clearing, so the host can confirm rather than assume -- and carrying
		// whether there was anything to clear, because "cleared" and "nothing was selected" are
		// different answers to the same request.
		WriteMarker(L"deselect.ready", had ? L"cleared" : L"nothing");

		Log(had ? L"overlay: selection cleared" : L"overlay: deselect with nothing selected");
		return had;
	}

	/// Clears the selection when the element it points at leaves the visual tree (#51).
	///
	/// The selection is the one mark that outlives the interaction which drew it, so it is the one
	/// mark with nothing watching it. Left alone, the outline and badge stay exactly where they were
	/// while pointing at nothing -- and worse, the *recorded* selection stays too, so
	/// rose_xaml_properties and rose_xaml_apply get called against an element that is gone and fail
	/// with a diagnostics HRESULT instead of "the thing you picked no longer exists".
	///
	/// Matched on the handle and never on the name, because a removal callback does not carry one:
    /// measured, every VisualElement delivered with a Remove had an empty Name. Matching on the name
	/// would have compiled, run, and never once fired.
	///
	/// Idempotent by construction, which is not incidental: a tap is advised per injection and none
	/// of them ever unadvises (#68), so this is called once per live tap for a single removal. After
	/// the first, the handle is zero and the rest are free.
	void ClearIfRemoved(InstanceHandle handle)
	{
		if (handle == 0 || handle != m_selectedHandle) return;

		// Said rather than merely done. A selection that vanishes with no explanation reads as a bug
		// in the overlay, and an agent that asks for properties after a navigation deserves the
		// sentence rather than an HRESULT from three layers down.
		Clear(L"The selected element was removed from the visual tree, so the selection was cleared. "
			L"Something in the app took it away -- a navigation, a collapsed panel, a recycled list "
			L"container. Read the tree again and select what you want from it.");

		Log(L"overlay: selection cleared because handle " + std::to_wstring(handle) + L" left the tree");
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

		// Said apart, because they are different answers and only one of them is about the element
		// being unusable. Selecting something in another window is an ordinary thing to ask for in a
		// multi-window app, and answering it with "no laid-out bounds" sends the caller looking at an
		// element that is laid out perfectly well (#75).
		if (!SharesRoot(element))
		{
			Log(L"overlay: handle " + std::to_wstring(handle) + L" is in another window; this overlay "
				+ L"covers only the one it was installed in, so the element cannot be marked");
			return false;
		}

		winrt::Windows::Foundation::Rect rect{};
		if (!Bounds(element, rect))
		{
			Log(L"overlay: handle " + std::to_wstring(handle) + L" has no laid-out bounds");
			return false;
		}

		RecordFromTree(element, handle);
		m_selectedHandle = handle;

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

	// How much white a chip takes under the pointer, and under a press.
	static constexpr double HoverWash = 0.14;
	static constexpr double PressWash = 0.26;

	// What a chip that cannot be clicked looks like. The Button template's disabled brushes are made
	// transparent along with the rest of it, so this is the only thing separating a chip that is off
	// from one that is merely idle.
	static constexpr double DisabledFade = 0.5;

	// The chip, sized once and stated here rather than repeated at each of the places that has to
	// agree with it: the button, the Border that colours it, and the wash over that.
	static constexpr double ChipSize = 24.0;

	// How far the toolbar sits from the edge it is anchored to.
	static constexpr double EdgeMargin = 16.0;

	// The lens, in device pixels, so it is a whole number of them at every factor: 192 divides by
	// every power of two up to 32, which is what keeps each source pixel an exact square block.
	static constexpr int LensDevicePixels = 192;

	// Roughly twelve captures a second. Fast enough to follow an animating app, and slow enough that
	// the readback is a fraction of a frame rather than a tax on one -- and it is a rate limit, not a
	// queue: a capture still in flight when the timer fires is simply skipped.
	static constexpr int LensIntervalMs = 80;

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

		// Two layers, and the split is what lets the marks follow a scaled app.
		//
		// The outlines and badges are drawn at coordinates read out of the app, so when the app is
		// scaled they are wrong until they are scaled the same way -- a picked element kept its mark
		// sitting where the element used to be. They cannot simply be given the transform one by one:
		// each is positioned by Canvas.Left, and a RenderTransform on an element scales about that
		// element rather than about the canvas origin, which is a different mapping. A canvas of their
		// own takes the app's transform whole and every mark on it lands where its element did.
		//
		// The toolbar and the lens stay on the untransformed layer above, because a toolbar that grows
		// to 32x is not a toolbar.
		m_marks = xcontrols::Canvas();
		m_root.Children().Append(m_marks);

		m_canvas = xcontrols::Canvas();
		m_root.Children().Append(m_canvas);

		// The window is not a fixed size, and neither is the thing we are covering.
		//
		// XamlRoot::Changed rather than Window::SizeChanged (#75): it exists under both frameworks,
		// and it fires for the thing actually being covered. It also fires on a scale change, which
		// the window event does not -- moving the app to a monitor at a different DPI resizes the
		// XAML content without resizing the window, and the old handler slept through it.
		if (const auto root = Root())
		{
			root.Changed(
				[this](xaml::XamlRoot const&, xaml::XamlRootChangedEventArgs const&)
				{
					Resize();
					Place();
				});
		}

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

		// Centred at the top, and measured rather than guessed. This used to be
		// `bounds.Width - 200`, a constant standing in for the panel's width -- which is not known
		// when the toolbar is built, because nothing has been measured yet and ActualWidth is still
		// zero. That also defeats Place()'s clamp, whose upper bound is computed from the same zero.
		// So the guess was load-bearing, and it stopped being true the moment a button was added: the
		// panel hung off the right edge, far enough on some windows to be invisible rather than merely
		// awkward. Nothing here may depend on knowing the panel's width in advance.
		m_dragTop = EdgeMargin;
		m_dragLeft = Extent().Width / 2.0;

		// Which is why the real placement waits for a measurement. SizeChanged is the first moment the
		// panel has a width, and it fires again whenever it grows -- the zoom row appearing, a mode
		// changing a glyph -- so it is also what keeps a growing panel on screen.
		m_panel.SizeChanged(
			[this](winrt::Windows::Foundation::IInspectable const&, xaml::SizeChangedEventArgs const&)
			{
				if (!m_reportedGeometry)
				{
					m_reportedGeometry = true;
					LogGeometry();
				}

				// Centred once, on the first measurement, and never again. Re-centring on every size
				// change is what made the toolbar jump: the zoom row is wider than the row above it,
				// so turning the mode on moved every button in the first row sideways, under whichever
				// one the pointer was reaching for. Keeping the left edge fixed lets the panel grow
				// down and to the right around a first row that stays where it is.
				if (!m_placed)
				{
					m_placed = true;
					CentreAtTop();
					return;
				}

				Place();
			});

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

		m_zoomButton = Chip(
			Glyph(IconZoom, 12.0),
			L"Magnify -- scale the app, or inspect its pixels and read the colour under the pointer",
			[this] { ToggleZoom(); });
		m_bar.Children().Append(m_zoomButton);

		// Hide sits at the right edge rather than packed against its neighbours, so the slack that
		// appears when the zoom row makes the panel wider falls between the two groups instead of
		// trailing off the end. m_bar keeps the left group; the Grid is what stretches.
		// Two columns, and they are not optional: children of a Grid with no ColumnDefinitions all
		// occupy the same cell, so the strip and Hide were drawn on top of one another and the last
		// chips could not be clicked at all. The star column is the slack that appears when the zoom
		// row makes the panel wider, and it sits between the groups rather than after them.
		auto row = xcontrols::Grid();

		auto stretchy = xcontrols::ColumnDefinition();
		stretchy.Width(xaml::GridLength{ 1.0, xaml::GridUnitType::Star });
		auto snug = xcontrols::ColumnDefinition();
		snug.Width(xaml::GridLength{ 0.0, xaml::GridUnitType::Auto });
		row.ColumnDefinitions().Append(stretchy);
		row.ColumnDefinitions().Append(snug);

		m_bar.HorizontalAlignment(xaml::HorizontalAlignment::Left);
		xcontrols::Grid::SetColumn(m_bar, 0);
		row.Children().Append(m_bar);

		auto hide = Chip(Glyph(IconHide, 12.0), L"Hide", [this] { Collapse(true); });
		hide.Margin(xaml::Thickness{ 0, 3, 4, 3 });
		xcontrols::Grid::SetColumn(hide, 1);
		row.Children().Append(hide);

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

		// The zoom controls go in a row of their own, under the first, and that is not a layout
		// preference. The rule this toolbar already follows is that a control which comes and goes
		// moves the ones beside it, over somebody else's application -- which is why Deselect is
		// disabled rather than hidden. Four more chips in the first row would widen the toolbar
		// permanently for a mode almost nobody is in; a second row that appears underneath leaves
		// every button in the first row exactly where it was.
		m_rows = xcontrols::StackPanel();
		m_rows.Orientation(xcontrols::Orientation::Vertical);
		m_rows.Children().Append(row);
		m_rows.Children().Append(BuildZoomRow());

		auto content = xcontrols::Grid();
		content.Children().Append(m_rows);
		content.Children().Append(m_thumb);
		return content;
	}

	// Zoom factor, mode, and the colour under the pointer. Hidden until the mode is on.
	xaml::UIElement BuildZoomRow()
	{
		m_zoomRow = xcontrols::StackPanel();
		m_zoomRow.Orientation(xcontrols::Orientation::Horizontal);
		m_zoomRow.Spacing(4);
		m_zoomRow.Padding(xaml::Thickness{ 4, 0, 4, 3 });

		// Takes no space until the mode is on. Reserving its height permanently was the first attempt
		// and it is worse: it makes every app carry a double-height toolbar for a mode almost nobody
		// is in. What must not move when it appears is the row above it, and that is a placement
		// question rather than a sizing one -- see CentreAtTop.
		m_zoomRow.Visibility(xaml::Visibility::Collapsed);

		m_scaleButton = Chip(
			Glyph(IconScale, 12.0),
			L"Scale the app itself -- a render transform on the live content, so text and vectors stay sharp",
			[this] { SetZoomMode(Zoom::Transform); });
		m_pixelButton = Chip(
			Glyph(IconPixels, 12.0),
			L"Pixel lens -- a nearest-neighbour magnifier that follows the pointer, leaving the app untouched",
			[this] { SetZoomMode(Zoom::Lens); });
		m_zoomOutButton = Chip(Glyph(IconZoomOut, 12.0), L"Zoom out", [this] { ZoomBy(-1); });
		m_zoomInButton = Chip(Glyph(IconZoomIn, 12.0), L"Zoom in", [this] { ZoomBy(1); });

		m_zoomRow.Children().Append(m_scaleButton);
		m_zoomRow.Children().Append(m_pixelButton);
		m_zoomRow.Children().Append(m_zoomOutButton);
		m_zoomRow.Children().Append(m_zoomInButton);

		m_factorLabel = Label(L"4x", 11.0, 0xC8, nullptr);
		m_factorLabel.MinWidth(26);
		m_zoomRow.Children().Append(m_factorLabel);

		// The colour swatch and its hex, side by side. The swatch matters as much as the digits: six
		// hex characters are hard to tell apart at a glance and a filled square is not.
		m_swatch = xshapes::Rectangle();
		m_swatch.Width(12);
		m_swatch.Height(12);
		m_swatch.RadiusX(2);
		m_swatch.RadiusY(2);
		m_swatch.Stroke(Brush(0xFF, 0x53, 0x53, 0x63));
		m_swatch.StrokeThickness(1);
		m_swatch.Fill(Brush(0x00, 0x00, 0x00, 0x00));
		m_swatch.VerticalAlignment(xaml::VerticalAlignment::Center);
		m_zoomRow.Children().Append(m_swatch);

		// Monospaced, because a proportional hex string changes width as the pointer moves and the
		// whole row then jitters under the cursor.
		m_hexLabel = Label(L"--", 11.0, 0xE8, L"Consolas");
		m_hexLabel.MinWidth(64);
		m_zoomRow.Children().Append(m_hexLabel);

		return m_zoomRow;
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
		// The chip paints itself, and the Button template paints nothing.
		//
		// Setting Background on a Button colours the *Normal* state only: PointerOver and Pressed come
		// from ButtonBackgroundPointerOver and ButtonBackgroundPressed, which are the system's brushes
		// for the system's palette. On a dark toolbar that reads as the button jumping to a foreign
		// shade the moment the pointer touches it, and sweeping across a row interleaves each button's
		// transition with its neighbour's -- which is the flicker, and no amount of choosing a better
		// Background fixes it, because the states in question never consult it.
		//
		// So every state brush is made transparent and the colour lives on a Border we own. Hover and
		// press are a white wash over it at two opacities rather than three separate colours, which is
		// what keeps them correct when Chrome recolours the chip underneath: the wash does not need to
		// know what it is washing over.
		auto fill = xcontrols::Border();
		fill.Background(Idle());
		fill.CornerRadius(xaml::CornerRadius{ 3, 3, 3, 3 });

		auto glow = xcontrols::Border();
		glow.Background(Brush(0xFF, 0xFF, 0xFF, 0xFF));
		glow.CornerRadius(xaml::CornerRadius{ 3, 3, 3, 3 });
		glow.Opacity(0.0);
		glow.IsHitTestVisible(false);

		// Sized explicitly, because a Button does not stretch its content: HorizontalContentAlignment
		// is Center, so a Grid handed to Content sizes itself to the glyph inside it. The colour then
		// shrinks to the glyph and the chip loses the inset that made it look like a button at all --
		// which is not visible in any property, only on screen. The template used to paint the button's
		// own root and so was full size for free; painting our own Border means saying the size.
		auto stack = xcontrols::Grid();
		stack.Width(ChipSize);
		stack.Height(ChipSize);
		stack.Children().Append(fill);
		stack.Children().Append(glow);
		stack.Children().Append(content);

		auto button = xcontrols::Button();
		button.Content(stack);

		// How Chrome finds the two Borders it has to reach, since the Button's own Background no longer
		// means anything: the fill to recolour, and the wash to clear when the chip is disabled out
		// from under the pointer.
		button.Tag(fill);
		stack.Tag(glow);

		Neutralise(button);

		// Square, and explicitly so: a Button sized by its padding comes out a few pixels wider than
		// tall with an icon in it, and three of those in a row is the thing that looks unconsidered.
		button.Padding(xaml::Thickness{ 0, 0, 0, 0 });
		button.Width(ChipSize);
		button.Height(ChipSize);
		button.HorizontalContentAlignment(xaml::HorizontalAlignment::Stretch);
		button.VerticalContentAlignment(xaml::VerticalAlignment::Stretch);
		button.MinWidth(0);
		button.MinHeight(0);
		button.BorderThickness(xaml::Thickness{ 0, 0, 0, 0 });
		button.CornerRadius(xaml::CornerRadius{ 3, 3, 3, 3 });
		button.VerticalAlignment(xaml::VerticalAlignment::Center);
		xcontrols::ToolTipService::SetToolTip(button, winrt::box_value(winrt::hstring{ tip }));

		// Set outright rather than animated. A fade would be prettier and is what was there before, in
		// effect, via the template's transitions -- and the whole complaint is that those do not settle
		// when the pointer crosses several buttons faster than they run.
		button.PointerEntered([glow](auto const&, auto const&) { glow.Opacity(HoverWash); });
		button.PointerExited([glow](auto const&, auto const&) { glow.Opacity(0.0); });
		button.PointerPressed([glow](auto const&, auto const&) { glow.Opacity(PressWash); });
		button.PointerReleased([glow](auto const&, auto const&) { glow.Opacity(HoverWash); });

		// Losing capture is the case a hover-out does not cover: press, drag off, release elsewhere.
		// Without this the chip stays lit at the pressed wash with the pointer somewhere else entirely.
		button.PointerCaptureLost([glow](auto const&, auto const&) { glow.Opacity(0.0); });

		button.Click(
			[action](winrt::Windows::Foundation::IInspectable const&, xaml::RoutedEventArgs const&) { action(); });
		return button;
	}

	// Makes the Button template paint nothing at all, in every state, so the chip's own Border is the
	// only thing on screen. Overridden on the button rather than in the panel's resources, so this
	// cannot reach anything the app owns.
	static void Neutralise(xcontrols::Button const& button)
	{
		static const wchar_t* const keys[] = {
			L"ButtonBackground",
			L"ButtonBackgroundPointerOver",
			L"ButtonBackgroundPressed",
			L"ButtonBackgroundDisabled",
			L"ButtonBorderBrush",
			L"ButtonBorderBrushPointerOver",
			L"ButtonBorderBrushPressed",
			L"ButtonBorderBrushDisabled",
		};

		const auto clear = Brush(0x00, 0x00, 0x00, 0x00);
		for (const auto key : keys)
		{
			button.Resources().Insert(winrt::box_value(winrt::hstring{ key }), clear);
		}
	}

	// Recolours a chip. The Button's Background is not it: the colour is on the Border behind the
	// glyph, which is what Chip put in the Tag.
	static void Paint(xcontrols::Button const& button, xmedia::Brush const& brush)
	{
		if (!button) return;
		if (const auto fill = button.Tag().try_as<xcontrols::Border>()) fill.Background(brush);
	}

	// Enables or disables a chip, and says so on screen in the same breath.
	//
	// Control::IsEnabledChanged is the seam this looks like it wants and it does not fire here.
	// IsEnabled is coerced -- an element is disabled if any ancestor is, so the effective value is
	// computed by a walk over the subtree -- and setting it while the toolbar is still being
	// assembled records the value without raising anything. The chip then reports IsEnabled false
	// and draws at full strength: a button that looks live and answers nothing, which is the state
	// it is least excusable to be in. So the visual is set beside the state rather than in reply to
	// it, and there is one place that can be got wrong instead of one per chip.
	//
	// Clearing the wash is the other half. A disabled element receives no pointer input at all, so
	// no PointerExited ever arrives, and Deselect disables itself on the very click that operates
	// it -- leaving it wearing the hover wash with the pointer long gone.
	static void Enable(xcontrols::Button const& button, bool enabled)
	{
		if (!button) return;

		button.IsEnabled(enabled);

		const auto stack = button.Content().try_as<xcontrols::Grid>();
		if (!stack) return;

		stack.Opacity(enabled ? 1.0 : DisabledFade);
		if (enabled) return;

		if (const auto glow = stack.Tag().try_as<xcontrols::Border>()) glow.Opacity(0.0);
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

		m_marks.Children().Append(box);
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
		m_marks.Children().Append(badge);
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

				// Once it has been put somewhere on purpose, it stays there: the toolbar re-centres
				// itself as it grows only while nobody has expressed an opinion about where it goes.
				m_dragged = true;
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
		const auto bounds = Extent();
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

		const auto bounds = Extent();

		// The upper bounds are floored at zero rather than trusted. A panel wider than its window --
		// a narrow window, or the zoom row on a small one -- makes the far edge negative, and a range
		// whose top is below its bottom is not a range: clamping into it puts the toolbar somewhere
		// neither end asked for. Pinned at the near edge is the answer that degrades sensibly.
		const double right = (std::max)(0.0, bounds.Width - m_panel.ActualWidth());
		const double bottom = (std::max)(0.0, bounds.Height - m_panel.ActualHeight());

		xcontrols::Canvas::SetLeft(m_panel, Clamp(m_dragLeft, 0.0, right));
		xcontrols::Canvas::SetTop(m_panel, Clamp(m_dragTop, 0.0, bottom));
	}

	// The default home: centred on the top edge. Called from the panel's own SizeChanged, because
	// that is the first moment its width is a fact rather than an assumption.
	void CentreAtTop()
	{
		if (!m_panel) return;

		const auto bounds = Extent();
		const double width = m_panel.ActualWidth();

		m_dragLeft = width > 0.0 ? (bounds.Width - width) / 2.0 : bounds.Width / 2.0;
		m_dragTop = EdgeMargin;
		Place();
	}

	// Everything that decides whether the toolbar can be seen, in one line, once, after a layout pass.
	//
	// "Installed" is not "visible" and on WinUI 3 the two have already come apart: the install line
	// reported a toolbar on a correctly-sized layer that nothing was drawing. Guessing at the reason
	// from here costs a rebuild and a relaunch per guess, so it is cheaper to have the app say which
	// of size, position, opacity and visibility is the one that is wrong -- for the overlay, and for
	// the layer it was handed, which is the thing this code does not own.
	void LogGeometry()
	{
		try
		{
			const auto extent = Extent();

			std::wstring line = L"overlay: geometry window "
				+ std::to_wstring(static_cast<int>(extent.Width)) + L"x"
				+ std::to_wstring(static_cast<int>(extent.Height));

			if (m_layer)
			{
				line += L" | layer " + std::to_wstring(static_cast<int>(m_layer.ActualWidth())) + L"x"
					+ std::to_wstring(static_cast<int>(m_layer.ActualHeight()))
					+ L" vis=" + std::to_wstring(static_cast<int>(m_layer.Visibility()))
					+ L" opacity=" + std::to_wstring(m_layer.Opacity())
					+ L" children=" + std::to_wstring(m_layer.Children().Size());
			}

			if (m_root)
			{
				line += L" | root " + std::to_wstring(static_cast<int>(m_root.ActualWidth())) + L"x"
					+ std::to_wstring(static_cast<int>(m_root.ActualHeight()))
					+ L" vis=" + std::to_wstring(static_cast<int>(m_root.Visibility()))
					+ L" opacity=" + std::to_wstring(m_root.Opacity());
			}

			if (m_panel)
			{
				line += L" | panel " + std::to_wstring(static_cast<int>(m_panel.ActualWidth())) + L"x"
					+ std::to_wstring(static_cast<int>(m_panel.ActualHeight()))
					+ L" at " + std::to_wstring(static_cast<int>(xcontrols::Canvas::GetLeft(m_panel)))
					+ L"," + std::to_wstring(static_cast<int>(xcontrols::Canvas::GetTop(m_panel)))
					+ L" vis=" + std::to_wstring(static_cast<int>(m_panel.Visibility()))
					+ L" opacity=" + std::to_wstring(m_panel.Opacity());
			}

			// Whether the layer we were handed belongs to the same content root as the app.
			//
			// Everything above can say "laid out, sized, visible" and still describe something nobody
			// can see, because laying out and being presented are different questions and a XAML tree
			// answers only the first. Two content roots in one window is the way they come apart: the
			// overlay measures and arranges perfectly inside a root that is not the one on screen.
			// Worth asking directly rather than inferring, since the alternative is a rebuild per
			// theory.
			const auto content = AppContent();
			if (content && m_layer)
			{
				const auto layerRoot = m_layer.XamlRoot();
				const auto contentRoot = content.XamlRoot();
				const bool same = layerRoot && contentRoot && layerRoot == contentRoot;
				line += std::wstring(L" | roots ") + (same ? L"shared" : L"DIFFERENT")
					+ L" (layer=" + (layerRoot ? L"yes" : L"null")
					+ L" content=" + (contentRoot ? L"yes" : L"null") + L")";
			}

			Log(line);
		}
		catch (winrt::hresult_error const& error)
		{
			Log(std::wstring(L"overlay: could not read its own geometry: ") + error.message().c_str());
		}
	}

	// What folds away is everything that is not the grip, and the unit is the rows stack rather than
	// the strip of chips inside the first row. Hide sits beside that strip rather than in it, so that
	// the slack from a wider second row falls between the two groups -- and the zoom row sits under
	// it. Folding the strip alone therefore left both of them drawn over the thumb: a chip floating on
	// the grip, and a row of zoom controls under a toolbar that is supposed to be gone.
	//
	// The modes themselves keep running. Collapsing is a request to see the app under the toolbar, not
	// to undo the magnification that is the reason for looking.
	void Collapse(bool collapsed)
	{
		if (!m_rows || !m_thumb) return;

		m_rows.Visibility(collapsed ? xaml::Visibility::Collapsed : xaml::Visibility::Visible);
		m_thumb.Visibility(collapsed ? xaml::Visibility::Visible : xaml::Visibility::Collapsed);
	}

	void ToggleMyXaml()
	{
		m_justMyXaml = !m_justMyXaml;
		Chrome();
		WriteState();
		Log(std::wstring(L"overlay: just-my-XAML ") + (m_justMyXaml ? L"on" : L"off"));
	}

	// Magnification, in two kinds, because they answer different questions and neither substitutes
	// for the other.
	//
	// Transform scales the app's own content with a render transform. It stays live and stays sharp:
	// text is re-rendered at the new size rather than resampled, so it is the one to read fine
	// typography or a vector at. It is also a change to somebody else's tree -- reversible, and
	// restored exactly, but real while it is on, and the app is awkward to click through at 8x.
	//
	// Lens leaves the app completely alone and magnifies a *capture* of it, replicating whole pixels
	// so what you see is the pixel grid the app actually produced. That is the one to answer "what
	// colour is that, exactly" and "is this edge one pixel or two" with, and it is why the hex
	// readout lives with it rather than with the transform: a scaled render has no pixels to name.
	//
	// XAML offers no help with the second. WPF has RenderOptions.BitmapScalingMode; UWP and WinUI
	// have no bitmap scaling mode at all -- the only InterpolationMode either exposes is
	// ColorInterpolationMode on a gradient brush -- so a ScaleTransform on an Image always filters
	// and would smear exactly the thing being looked at. Replicating the pixels by hand is not a
	// workaround for that, it is the only way to get it, and it costs one buffer walk over a region
	// a few thousand pixels big.
	enum class Zoom
	{
		Off,
		Transform,
		Lens,
	};

	void ToggleZoom()
	{
		SetZoomMode(m_zoom == Zoom::Off ? Zoom::Lens : Zoom::Off);
	}

	void SetZoomMode(Zoom mode)
	{
		if (m_zoom == mode) return;

		// Leaving transform mode has to put the app back before anything else happens; leaving lens
		// mode has to stop the timer, or a capture keeps running over an app nobody is inspecting.
		if (m_zoom == Zoom::Transform) ClearTransform();
		if (m_zoom == Zoom::Lens) StopLens();

		m_zoom = mode;

		if (m_zoom == Zoom::Transform) ApplyTransform();
		if (m_zoom == Zoom::Lens) StartLens();

		if (m_zoomRow)
		{
			m_zoomRow.Visibility(m_zoom == Zoom::Off ? xaml::Visibility::Collapsed : xaml::Visibility::Visible);
		}

		if (m_lens && m_zoom != Zoom::Lens) m_lens.Visibility(xaml::Visibility::Collapsed);

		Chrome();
		Place();
		Log(L"overlay: zoom " + ZoomName(m_zoom) + L" at " + std::to_wstring(m_zoomFactor) + L"x");
	}

	static std::wstring ZoomName(Zoom mode)
	{
		switch (mode)
		{
			case Zoom::Transform: return L"transform";
			case Zoom::Lens: return L"lens";
			default: return L"off";
		}
	}

	// Powers of two, and not merely for tidiness: at a whole factor every source pixel becomes an
	// exact square block, so the lens shows the app's pixel grid rather than a moire of it. A
	// fractional factor puts some source pixels in a 3-wide column and its neighbour in a 4-wide one,
	// which reads as the app having uneven strokes it does not have.
	void ZoomBy(int step)
	{
		const int previous = m_zoomFactor;

		if (step > 0 && m_zoomFactor < 32) m_zoomFactor *= 2;
		if (step < 0 && m_zoomFactor > 2) m_zoomFactor /= 2;
		if (m_zoomFactor == previous) return;

		if (m_factorLabel) m_factorLabel.Text(std::to_wstring(m_zoomFactor) + L"x");
		if (m_zoom == Zoom::Transform) ApplyTransform();
		if (m_zoom == Zoom::Lens) UpdateLens();
	}

	// The app's own content, which is deliberately not the root. The overlay sits on the diagnostics
	// UI layer -- a panel the framework hands out through GetUiLayer, beside the app's content rather
	// than inside it -- so rendering the content element captures the app and not this toolbar.
	// Rendering the root instead would put the toolbar inside its own lens.
	xaml::UIElement AppContent() const
	{
		try
		{
			const auto root = Root();
			if (!root) return nullptr;
			return root.Content().try_as<xaml::UIElement>();
		}
		catch (winrt::hresult_error const&)
		{
			return nullptr;
		}
	}

	// What the transform mode scales. One element today -- the app's content -- but a list, because
	// the transform is saved and restored per element and a single saved slot is what made an earlier
	// version restore only the last thing it touched.
	std::vector<xaml::UIElement> ScaleTargets() const
	{
		std::vector<xaml::UIElement> targets;
		if (const auto content = AppContent()) targets.push_back(content);

		// The marks go with it, or a picked element's outline stays where the element was before the
		// app moved under it.
		if (m_marks) targets.push_back(m_marks);

		return targets;
	}

	void ApplyTransform()
	{
		const auto targets = ScaleTargets();
		if (targets.empty())
		{
			Log(L"overlay: zoom found no app content to scale");
			return;
		}

		try
		{
			// Saved on the way in, restored on the way out, rather than cleared. An app is free to
			// have a transform of its own, and setting that to null on exit would be a change nobody
			// asked for -- and one that would outlive the mode that made it.
			if (!m_transformSaved)
			{
				for (const auto& target : targets)
				{
					m_saved.push_back({ target, target.RenderTransform(), target.RenderTransformOrigin() });
				}

				m_transformSaved = true;
			}

			for (const auto& target : targets)
			{
				auto scale = xmedia::ScaleTransform();
				scale.ScaleX(static_cast<double>(m_zoomFactor));
				scale.ScaleY(static_cast<double>(m_zoomFactor));
				target.RenderTransform(scale);
			}

			UpdateTransformOrigin();
		}
		catch (winrt::hresult_error const& error)
		{
			Log(std::wstring(L"overlay: could not scale the app: ") + error.message().c_str());
		}
	}

	// Keeps the point under the pointer where it is, which is what makes the scaled app navigable:
	// a render transform grows its element about its origin, so putting the origin under the cursor
	// means moving the cursor pans rather than flings.
	//
	// It has to follow the pointer, and that was the whole bug. Set once when the mode was entered,
	// the origin was wherever the pointer happened to be at the moment of the *click that turned the
	// mode on* -- which is on the toolbar, because that is what was being clicked. Worse, when the
	// pointer is outside the window it reads (-1, -1) and clamped to (0, 0), so the app scaled about
	// its top-left corner and at 4x every part of it was off screen. An unknown pointer must leave
	// the origin alone rather than resolve to a corner.
	//
	// Normalised against the content's own size, not the window's: RenderTransformOrigin is a
	// fraction of the element, and an element that does not fill the window would otherwise anchor
	// somewhere the pointer is not.
	void UpdateTransformOrigin()
	{
		if (m_zoom != Zoom::Transform) return;

		const auto bounds = Extent();
		const bool known = m_pointer.X >= 0.0f && m_pointer.Y >= 0.0f
			&& m_pointer.X < bounds.Width && m_pointer.Y < bounds.Height;
		if (!known) return;

		try
		{
			for (const auto& target : ScaleTargets())
			{
				double width = bounds.Width;
				double height = bounds.Height;
				if (const auto framed = target.try_as<xaml::FrameworkElement>())
				{
					if (framed.ActualWidth() > 0.0) width = framed.ActualWidth();
					if (framed.ActualHeight() > 0.0) height = framed.ActualHeight();
				}

				const double x = width > 0.0 ? Clamp(m_pointer.X / width, 0.0, 1.0) : 0.5;
				const double y = height > 0.0 ? Clamp(m_pointer.Y / height, 0.0, 1.0) : 0.5;
				target.RenderTransformOrigin(
					winrt::Windows::Foundation::Point{ static_cast<float>(x), static_cast<float>(y) });
			}
		}
		catch (winrt::hresult_error const&)
		{
		}
	}

	void ClearTransform()
	{
		if (!m_transformSaved) return;

		m_transformSaved = false;

		for (const auto& saved : m_saved)
		{
			try
			{
				saved.Element.RenderTransform(saved.Transform);
				saved.Element.RenderTransformOrigin(saved.Origin);
			}
			catch (winrt::hresult_error const&)
			{
				// An element that has since left the tree is not ours to put back.
			}
		}

		m_saved.clear();
	}

	void StartLens()
	{
		if (!m_lens) BuildLens();

		if (!m_lensTimer)
		{
			m_lensTimer = xaml::DispatcherTimer();
			m_lensTimer.Interval(std::chrono::milliseconds(LensIntervalMs));
			m_lensTimer.Tick(
				[this](winrt::Windows::Foundation::IInspectable const&, winrt::Windows::Foundation::IInspectable const&)
				{
					try
					{
						CaptureFrame();
					}
					catch (winrt::hresult_error const&)
					{
						// Runs forever over somebody else's app. Never throw out of it.
					}
				});
		}

		m_lensTimer.Start();
		CaptureFrame();
	}

	void StopLens()
	{
		if (m_lensTimer) m_lensTimer.Stop();

		// Dropped rather than kept. A stale frame is worse than none: it would answer the next
		// question about a window that has since moved on, and answer it confidently.
		m_pixels.clear();
		m_pixelsWidth = 0;
		m_pixelsHeight = 0;

		if (m_lens) m_lens.Visibility(xaml::Visibility::Collapsed);
		if (m_hexLabel) m_hexLabel.Text(L"--");
		if (m_swatch) m_swatch.Fill(Brush(0x00, 0x00, 0x00, 0x00));
	}

	void BuildLens()
	{
		m_lensImage = xcontrols::Image();

		m_lens = xcontrols::Border();
		m_lens.BorderBrush(Accent());
		m_lens.BorderThickness(xaml::Thickness{ 1, 1, 1, 1 });
		m_lens.CornerRadius(xaml::CornerRadius{ 2, 2, 2, 2 });
		m_lens.Background(Brush(0xFF, 0x1C, 0x1C, 0x22));
		m_lens.Visibility(xaml::Visibility::Collapsed);

		// Never takes input, or the thing it is magnifying stops receiving the pointer that is
		// driving it.
		m_lens.IsHitTestVisible(false);

		// The pixel the hex is quoting, outlined in the accent so the two cannot be read apart.
		//
		// A magnified lens with no marker leaves "which of these blocks is under the cursor" to be
		// guessed, and at 4x the guess is wrong as often as not -- the readout then looks like it is
		// lagging or sampling somewhere else. Outlining exactly one source pixel is also what makes
		// the magnification legible as pixels rather than as a blurry crop.
		m_pixelBox = xshapes::Rectangle();
		m_pixelBox.Stroke(Accent());
		m_pixelBox.StrokeThickness(1.0);
		m_pixelBox.Fill(Brush(0x00, 0x00, 0x00, 0x00));
		m_pixelBox.HorizontalAlignment(xaml::HorizontalAlignment::Left);
		m_pixelBox.VerticalAlignment(xaml::VerticalAlignment::Top);
		m_pixelBox.IsHitTestVisible(false);

		auto stack = xcontrols::Grid();
		stack.Children().Append(m_lensImage);
		stack.Children().Append(m_pixelBox);

		m_lens.Child(stack);
		m_canvas.Children().Append(m_lens);
	}

	// One readback of the app, on a timer, and never two at once.
	//
	// RenderAsync is a GPU readback, so a second one queued behind the first buys nothing and costs
	// a frame; m_capturingFrame is what makes the timer a rate limit rather than a backlog. It also
	// means a slow app simply captures less often instead of falling further behind.
	//
	// Split across three methods for one reason, and it is not style. A completion handler runs on a
	// pool thread, and RenderTargetBitmap is a DependencyObject: reading PixelWidth off that thread
	// is a wrong-thread call, and merely *capturing* the bitmap in the handler is worse, because the
	// lambda is destroyed there and takes the last reference with it. Releasing a XAML object off the
	// UI thread is how this crashed the app a few seconds after the mode was switched on, with the
	// last line in the log being the one that says the mode is on. So the handlers below capture
	// nothing but `this` and a bool, and every line that touches XAML runs through
	// RoseTapRunOnUiThread. The bitmap and the operation are members so neither is owned by a lambda.
	void CaptureFrame()
	{
		if (m_zoom != Zoom::Lens || m_capturingFrame) return;

		const auto content = AppContent();
		if (!content) return;

		try
		{
			m_capturingFrame = true;
			m_lensBitmap = ximaging::RenderTargetBitmap();
			m_lensBitmap.RenderAsync(content).Completed(
				[this](winrt::Windows::Foundation::IAsyncAction const&, winrt::Windows::Foundation::AsyncStatus status)
				{
					const bool rendered = status == winrt::Windows::Foundation::AsyncStatus::Completed;
					RoseTapRunOnUiThread([this, rendered] { OnRendered(rendered); });
				});
		}
		catch (winrt::hresult_error const&)
		{
			m_capturingFrame = false;
		}
	}

	// On the UI thread, which is where the bitmap may be asked anything at all.
	void OnRendered(bool rendered)
	{
		if (!rendered || m_zoom != Zoom::Lens || !m_lensBitmap)
		{
			m_capturingFrame = false;
			return;
		}

		try
		{
			m_pixelsWidth = m_lensBitmap.PixelWidth();
			m_pixelsHeight = m_lensBitmap.PixelHeight();

			m_lensPixels = m_lensBitmap.GetPixelsAsync();
			m_lensPixels.Completed(
				[this](auto const&, winrt::Windows::Foundation::AsyncStatus status)
				{
					const bool got = status == winrt::Windows::Foundation::AsyncStatus::Completed;
					RoseTapRunOnUiThread([this, got] { OnPixels(got); });
				});
		}
		catch (winrt::hresult_error const&)
		{
			m_capturingFrame = false;
		}
	}

	// Also on the UI thread. The buffer itself is agile, but it is read here anyway so that the whole
	// capture path has exactly one thread in it and nothing has to be reasoned about twice.
	void OnPixels(bool got)
	{
		m_capturingFrame = false;
		if (!got || m_zoom != Zoom::Lens || !m_lensPixels) return;

		try
		{
			const auto buffer = m_lensPixels.GetResults();
			m_pixels.resize(buffer.Length());

			auto reader = winrt::Windows::Storage::Streams::DataReader::FromBuffer(buffer);
			reader.ReadBytes(winrt::array_view<uint8_t>(m_pixels.data(), m_pixels.data() + m_pixels.size()));

			UpdateLens();
		}
		catch (winrt::hresult_error const&)
		{
			m_pixels.clear();
		}
	}

	// Draws the lens from the last capture, and reads the colour under the pointer out of the same
	// buffer. The two are one operation on purpose: the hex is simply the middle pixel of what the
	// lens is showing, so they can never disagree about what is under the cursor.
	void UpdateLens()
	{
		if (m_zoom != Zoom::Lens || !m_lens || m_pixels.empty() || m_pixelsWidth <= 0) return;

		const auto bounds = Extent();
		const bool inside = m_pointer.X >= 0.0f && m_pointer.Y >= 0.0f
			&& m_pointer.X < bounds.Width && m_pointer.Y < bounds.Height;
		if (!inside)
		{
			m_lens.Visibility(xaml::Visibility::Collapsed);
			return;
		}

		// The capture is in device pixels and the pointer in DIPs. The ratio is measured rather than
		// assumed to be the rasterization scale: a capture is whatever size the framework chose for
		// it, and moving the window to a monitor at another DPI changes that without changing the
		// pointer's units.
		const double perDipX = bounds.Width > 0 ? m_pixelsWidth / bounds.Width : 1.0;
		const double perDipY = bounds.Height > 0 ? m_pixelsHeight / bounds.Height : 1.0;

		const int centreX = static_cast<int>(m_pointer.X * perDipX);
		const int centreY = static_cast<int>(m_pointer.Y * perDipY);

		// How many source pixels the lens covers, and the block each becomes.
		const int span = LensDevicePixels / m_zoomFactor;
		const int originX = centreX - span / 2;
		const int originY = centreY - span / 2;

		auto target = ximaging::WriteableBitmap(LensDevicePixels, LensDevicePixels);
		auto access = target.PixelBuffer().as<::Windows::Storage::Streams::IBufferByteAccess>();
		uint8_t* out = nullptr;
		if (FAILED(access->Buffer(&out)) || !out) return;

		// The replication itself, which is the whole of "nearest neighbour": every destination pixel
		// takes the source pixel it lands on, with no blending between them, so a one-pixel line
		// stays one block wide and a colour boundary stays a boundary.
		for (int y = 0; y < LensDevicePixels; ++y)
		{
			const int sourceY = originY + y / m_zoomFactor;
			for (int x = 0; x < LensDevicePixels; ++x)
			{
				const int sourceX = originX + x / m_zoomFactor;
				uint8_t* pixel = out + (static_cast<size_t>(y) * LensDevicePixels + x) * 4;

				// Outside the capture is drawn as the panel's own dark, so the lens says where the
				// window ends rather than repeating its edge pixel outwards.
				if (sourceX < 0 || sourceY < 0 || sourceX >= m_pixelsWidth || sourceY >= m_pixelsHeight)
				{
					pixel[0] = 0x22; pixel[1] = 0x1C; pixel[2] = 0x1C; pixel[3] = 0xFF;
					continue;
				}

				const size_t offset = (static_cast<size_t>(sourceY) * m_pixelsWidth + sourceX) * 4;
				if (offset + 3 >= m_pixels.size()) continue;

				pixel[0] = m_pixels[offset + 0];
				pixel[1] = m_pixels[offset + 1];
				pixel[2] = m_pixels[offset + 2];
				pixel[3] = m_pixels[offset + 3];
			}
		}

		target.Invalidate();
		m_lensImage.Source(target);

		// Sized so one bitmap pixel lands on one device pixel. Left to XAML's own layout it would be
		// scaled by the rasterization scale and resampled -- which is exactly the filtering this whole
		// path exists to avoid, reintroduced at the last step.
		const double dips = LensDevicePixels / (perDipX > 0.0 ? perDipX : 1.0);
		m_lensImage.Width(dips);
		m_lensImage.Height(dips);

		// The marker sits over the block the centre source pixel became, which is where the sample was
		// taken: span/2 blocks in, each block a factor of device pixels wide.
		if (m_pixelBox)
		{
			const double perDevice = perDipX > 0.0 ? perDipX : 1.0;
			const double block = m_zoomFactor / perDevice;
			const double offset = (span / 2) * m_zoomFactor / perDevice;

			m_pixelBox.Width(block);
			m_pixelBox.Height(block);
			m_pixelBox.Margin(xaml::Thickness{ offset, offset, 0, 0 });
		}

		PlaceLens(dips);
		ShowColourAt(centreX, centreY);
		m_lens.Visibility(xaml::Visibility::Visible);
	}

	// Beside the pointer, and flipped to whichever side has room, so the lens never sits under the
	// cursor it is following or hangs off the edge of the window.
	void PlaceLens(double size)
	{
		const auto bounds = Extent();
		const double gap = 18.0;

		double left = m_pointer.X + gap;
		if (left + size > bounds.Width) left = m_pointer.X - gap - size;

		double top = m_pointer.Y + gap;
		if (top + size > bounds.Height) top = m_pointer.Y - gap - size;

		xcontrols::Canvas::SetLeft(m_lens, Clamp(left, 0.0, (std::max)(0.0, bounds.Width - size)));
		xcontrols::Canvas::SetTop(m_lens, Clamp(top, 0.0, (std::max)(0.0, bounds.Height - size)));
	}

	// The colour under the pointer, as a swatch and as hex. BGRA is the order the capture comes in,
	// which is not the order it is written in -- getting that backwards produces a plausible colour
	// with red and blue swapped, and the only way to notice is to point it at something you already
	// know the value of.
	void ShowColourAt(int x, int y)
	{
		if (!m_hexLabel || !m_swatch) return;

		if (x < 0 || y < 0 || x >= m_pixelsWidth || y >= m_pixelsHeight)
		{
			m_hexLabel.Text(L"--");
			return;
		}

		const size_t offset = (static_cast<size_t>(y) * m_pixelsWidth + x) * 4;
		if (offset + 3 >= m_pixels.size()) return;

		const uint8_t blue = m_pixels[offset + 0];
		const uint8_t green = m_pixels[offset + 1];
		const uint8_t red = m_pixels[offset + 2];
		const uint8_t alpha = m_pixels[offset + 3];

		wchar_t hex[16] = {};
		swprintf_s(hex, L"#%02X%02X%02X", red, green, blue);
		m_hexLabel.Text(hex);
		m_swatch.Fill(Brush(0xFF, red, green, blue));

		// Said only when it is not the obvious answer: almost every pixel of a drawn window is
		// opaque, so a permanent alpha column would be noise, and a transparent one is worth knowing.
		if (alpha != 0xFF)
		{
			m_hexLabel.Text(std::wstring(hex) + L" a" + std::to_wstring(static_cast<int>(alpha)));
		}
	}


	// Which mode is current, said in the toolbar itself: the active button wears the accent, the
	// inactive one the panel's own grey. Just-my-XAML is a toggle rather than a mode, so it is lit
	// whenever it is on regardless of whether a pick is in progress.
	void Chrome()
	{
		if (!m_idleButton || !m_selectButton) return;

		Paint(m_idleButton, m_selecting ? Idle() : Accent());
		Paint(m_selectButton, m_selecting ? Accent() : Idle());
		Paint(m_myXamlButton, m_justMyXaml ? Accent() : Idle());

		// Deselect is an action rather than a mode, so it never wears the accent -- only whether
		// there is anything for it to do.
		Enable(m_deselectButton, m_hasSelection);

		Paint(m_zoomButton, m_zoom == Zoom::Off ? Idle() : Accent());
		Paint(m_scaleButton, m_zoom == Zoom::Transform ? Accent() : Idle());
		Paint(m_pixelButton, m_zoom == Zoom::Lens ? Accent() : Idle());

		// The ends of the range are said by disabling, not by silently doing nothing: a button that
		// responds to a click by leaving everything as it was reads as broken rather than as bounded.
		Enable(m_zoomInButton, m_zoomFactor < 32);
		Enable(m_zoomOutButton, m_zoomFactor > 2);
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

	// The XamlRoot this overlay lives in, taken from the diagnostics UI layer it was installed on.
	//
	// Everything below used to ask Window::Current, which does not exist in WinUI 3 (#75). XamlRoot
	// answers all three things that were wanted of it -- the extent, the root content, and a
	// size-changed event -- and it answers them on UWP too, since 1903, so this is one implementation
	// rather than a seam. That was worth checking rather than assuming: the alternative was a
	// per-provider host surface, and every line of it would have been the same line twice.
	//
	// It is also the more truthful question. Window::Current().Bounds() is the window; what the
	// overlay actually covers is the XAML content, and the UI layer is sized to that.
	xaml::XamlRoot Root() const
	{
		return m_layer ? m_layer.XamlRoot() : nullptr;
	}

	// The size of the area the overlay covers. Named Extent because Bounds is taken, just below, by
	// the question of where one element sits.
	winrt::Windows::Foundation::Size Extent() const
	{
		const auto root = Root();
		return root ? root.Size() : winrt::Windows::Foundation::Size{ 0, 0 };
	}

	xaml::UIElement Content() const
	{
		const auto root = Root();
		return root ? root.Content() : nullptr;
	}

	// Whether an element belongs to the XamlRoot this overlay was installed in.
	//
	// A WinUI 3 app can have several windows, and the tree snapshot enumerates whatever the framework
	// gives it, so an element can be laid out perfectly well somewhere this overlay cannot draw (#75).
	// The decision is one overlay, in the root the diagnostics site handed us, and elements outside it
	// are *said* to be outside it. The alternatives were both worse: an overlay per XamlRoot multiplies
	// a leaked singleton by however many windows the app opens, and drawing anyway puts the mark at the
	// right coordinates in the wrong window, which is the failure #45 and #51 already exist about.
	//
	// UWP reaches here too and always agrees, having one root. That is why this costs nothing to have.
	bool SharesRoot(xaml::UIElement const& element) const
	{
		const auto ours = Root();
		if (!ours) return false;

		const auto theirs = element.XamlRoot();
		return theirs && theirs == ours;
	}

	// Where an element sits in the window, in the coordinates the overlay's Canvas uses -- the UI layer
	// is sized to the window, so the window root's space is the Canvas's space.
	bool Bounds(xaml::UIElement const& element, winrt::Windows::Foundation::Rect& rect)
	{
		try
		{
			// Checked before the transform rather than left to it. TransformToVisual across two
			// XamlRoots does not fail in a way that says what went wrong, so the caller was told the
			// element had no laid-out bounds -- a confident wrong answer about one that is laid out
			// fine, in another window.
			if (!SharesRoot(element)) return false;

			const auto root = Content();
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
	// never do. The observer has to see every move *before* XAML routes it and consume nothing.
	//
	// This is the one seam in the overlay that a namespace alias cannot close (#75), so the provider
	// supplies it: UWP has CoreWindow, WinUI 3 does not and uses InputPointerSource off the content
	// island. Both were measured against the two properties above rather than assumed -- see the note
	// on RoseTapWatchPointer in the provider.
	//
	// Failure here is not fatal and deliberately so. It costs the proximity fade, which is why the
	// marks get out of the way; it does not cost select mode, which picks through the full-bleed
	// capture layer and never came through this.
	void WatchPointer()
	{
		m_selectionFade = MakeFader({ m_selectBox, m_selectBadge });
		m_panelFade = MakeFader({ m_panel });

		const auto watched = RoseTapWatchPointer(
			m_layer,
			[this](winrt::Windows::Foundation::Point const& position)
			{
				try
				{
					Proximity(position);
				}
				catch (winrt::hresult_error const&)
				{
					// Runs on every pointer move in somebody else's application. Never throw out of it.
				}
			},
			[this]()
			{
				// Leaving is not a move, and without this whatever was last under the pointer stays
				// lit for as long as the pointer is somewhere else entirely.
				try
				{
					Proximity(winrt::Windows::Foundation::Point{ -1.0f, -1.0f });
				}
				catch (winrt::hresult_error const&)
				{
				}
			});

		if (!watched) Log(L"overlay: no pointer source; the marks will not fade with proximity");
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

		// Redrawn from the frame already in hand rather than by capturing another. A pointer move is
		// hundreds of events a second and a readback is a GPU round trip; the timer is what decides
		// how fresh the pixels are, and this is what decides which of them are on screen. The two are
		// deliberately separate, so sweeping the pointer across the window costs a buffer walk and
		// nothing else.
		if (m_zoom == Zoom::Lens) UpdateLens();

		// The scaled app follows the pointer too, by moving the point it grows about rather than by
		// redrawing anything.
		if (m_zoom == Zoom::Transform) UpdateTransformOrigin();

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
	// Follows the picked element when the app re-lays out, which is not the same event as the element
	// changing size.
	//
	// The mark is drawn at coordinates read out of the app once, and an app that moves the element
	// afterwards leaves it behind -- pointing confidently at empty space. The probe does this to
	// itself every few seconds: a sibling leaves the panel, everything below it slides up, and nothing
	// about the picked element changed except where it is. SizeChanged cannot see that, because the
	// element was not resized; LayoutUpdated can, because it fires for the pass that moved it.
	//
	// It fires for every layout pass in the tree, so it is subscribed only while something is picked,
	// and it does one TransformToVisual when it fires. That is affordable in a way that recomputing on
	// every pointer move would not be -- which is the reason this was left alone until now.
	void WatchSelectionLayout(xaml::UIElement const& element)
	{
		if (m_selectedElement && m_layoutToken.value)
		{
			m_selectedElement.LayoutUpdated(m_layoutToken);
			m_layoutToken = {};
		}

		m_selectedElement = element ? element.try_as<xaml::FrameworkElement>() : nullptr;
		if (!m_selectedElement) return;

		m_layoutToken = m_selectedElement.LayoutUpdated(
			[this](winrt::Windows::Foundation::IInspectable const&, winrt::Windows::Foundation::IInspectable const&)
			{
				if (!m_hasSelection || !m_selectedElement) return;

				// Re-entrant by construction, and it has to be stopped twice over.
				//
				// LayoutUpdated fires for every layout pass in the tree, and moving the outline is
				// itself a layout pass -- so redrawing from this handler schedules the handler again.
				// The comparison below is what breaks that cycle, and it must compare like with like:
				// comparing raw bounds against m_selectionRect, which carries the badge, never matched,
				// so every pass redrew and scheduled another until the app went down with it.
				if (m_updatingSelection) return;

				try
				{
					winrt::Windows::Foundation::Rect rect{};
					if (!Bounds(m_selectedElement, rect)) return;

					const auto withBadge = WithBadge(rect);
					if (withBadge.X == m_selectionRect.X && withBadge.Y == m_selectionRect.Y
						&& withBadge.Width == m_selectionRect.Width && withBadge.Height == m_selectionRect.Height)
					{
						return;
					}

					m_updatingSelection = true;
					ShowBox(m_selectBox, m_selectBadge, m_selectedElement, Describe(m_selectedElement));
					m_selectionRect = withBadge;
					m_updatingSelection = false;
				}
				catch (winrt::hresult_error const&)
				{
					// An element mid-removal is #51's problem, not this one's.
					m_updatingSelection = false;
				}
			});
	}

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
		const auto root = Content();
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
				m_selectedHandle = Record(element, point);
				WatchSelectionLayout(element);

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

	/// The clearing itself. <paramref name="goneReason"/> is written where the host can find it later
	/// when the selection went away on its own; a deliberate deselect passes null, because the caller
	/// asking for it does not need to be told it happened.
	bool Clear(const wchar_t* goneReason)
	{
		const bool had = m_hasSelection;

		WatchSelectionLayout(nullptr);
		ShowBox(m_selectBox, m_selectBadge, nullptr, std::wstring());
		m_hasSelection = false;
		m_overSelection = false;
		m_selectionRect = {};
		m_selectedHandle = 0;
		m_selectionRows.clear();

		// The note outlives this call, because nothing is waiting on it: the element went away between
		// requests, and the host only finds out when it next asks.
		if (goneReason && had) m_goneReason = goneReason;

		if (!g_workDir.empty())
		{
			// Removed rather than emptied. The host waits on selection.ready existing, so a truncated
			// one would still read as a selection that had arrived and merely say nothing about it.
			_wremove((g_workDir + L"\\selection.ready").c_str());
			_wremove((g_workDir + L"\\selection.tsv").c_str());

			if (goneReason && had)
			{
				std::wofstream gone(g_workDir + L"\\selection.gone", std::ios::trunc);
				if (gone) gone << goneReason << L"\n";
			}
		}

		Chrome();
		return had;
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
		unsigned int written = 0;
		std::ostringstream rows;

		xaml::DependencyObject node = element;
		while (node && written < 16)
		{
			if (const auto candidate = node.try_as<xaml::UIElement>())
			{
				if (IsOurs(candidate)) break; // Walked out of the app and into our own overlay.

				WriteCandidate(rows, candidate);
				written++;
			}

			node = xmedia::VisualTreeHelper::GetParent(node);
		}

		PublishSelection(rows.str(), handle);

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
	InstanceHandle Record(xaml::UIElement const& element, winrt::Windows::Foundation::Point const& point)
	{
		const auto root = Content();
		InstanceHandle selected = 0;
		unsigned int written = 0;

		std::ostringstream file;
		{
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

		PublishSelection(file.str(), selected);
		Log(L"overlay: recorded " + Describe(element) + L" and " + std::to_wstring(written) + L" candidate(s)");

		return selected;
	}

	/// <summary>
	/// Keeps the recorded selection where a later request can read it, and mirrors it into the work
	/// folder for a host still reading files.
	/// </summary>
	/// <remarks>
	/// The rows are held rather than only written, because a pick outlives the request that armed it by
	/// design: the person clicks whenever they click, and the host asks afterwards. A file is one way to
	/// answer that later question and the pipe is another, so the answer lives here and each channel is
	/// a way of handing it over.
	/// </remarks>
	void PublishSelection(std::string rows, InstanceHandle selected)
	{
		m_selectionRows = std::move(rows);
		m_goneReason.clear();

		if (g_workDir.empty()) return;

		{
			std::ofstream file((g_workDir + L"\\selection.tsv").c_str(), std::ios::trunc | std::ios::binary);
			if (file) file << m_selectionRows;
		}

		std::wofstream ready(g_workDir + L"\\selection.ready", std::ios::trunc);
		if (ready) ready << selected << L"\n";
	}

	InstanceHandle WriteCandidate(std::ostream& file, xaml::UIElement const& candidate)
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
		if (!state) return;

		state << (m_selecting ? L"select" : L"idle") << L" justMyXaml=" << (m_justMyXaml ? L"1" : L"0");

		// Appended, never inserted: the host tokenises this line, so a reader that does not know
		// about the generation goes on working and one that does can tell whose answer this is.
		if (!g_generation.empty()) state << L" gen=" << g_generation;

		state << L"\n";
	}

	// "armed <width>x<height>", the extent XAML arranged the capture layer at. A zero here is the
	// whole bug this reports: select mode that is on, invisible, and cannot be pointed at.
	void WriteArmed()
	{
		if (!m_capture) return;

		const int width = static_cast<int>(m_capture.ActualWidth());
		const int height = static_cast<int>(m_capture.ActualHeight());

		// Recorded before anything is written, and whether or not there is a folder to write to: this is
		// what a caller waiting on the extent is waiting for, and it must not depend on a channel.
		{
			std::lock_guard<std::mutex> guard(m_armedMutex);
			m_armedWidth = width;
			m_armedHeight = height;
			m_armedKnown = true;
		}

		m_armedSignal.notify_all();

		if (!g_workDir.empty())
		{
			WriteMarker(L"select.ready", L"armed " + std::to_wstring(width) + L"x" + std::to_wstring(height));
		}

		Log(L"overlay: capture layer arranged at " + std::to_wstring(width) + L"x" + std::to_wstring(height));
	}

	IXamlDiagnostics* m_diagnostics = nullptr;
	xcontrols::Panel m_layer{ nullptr };

	// Set only where the diagnostics UI layer is not drawn; null everywhere else, so the two hosting
	// routes cannot both be half-taken.
	xcontrols::Grid m_root{ nullptr };
	xcontrols::Canvas m_canvas{ nullptr };

	// The outlines and badges, on their own layer so they can be scaled with the app while the
	// toolbar above them is not.
	xcontrols::Canvas m_marks{ nullptr };
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

	// Which element is selected, so a removal can be recognised. The handle and not the name:
	// a Remove callback carries an empty Name, measured, so matching on one would never fire.
	InstanceHandle m_selectedHandle = 0;

	// The pick, as the rows that describe it, and the reason the last one went away. Both are answers to
	// a question asked after the fact, so they are held rather than derived on demand: the elements a
	// click landed on cannot be recovered once the pointer has moved.
	std::string m_selectionRows;
	std::wstring m_goneReason;

	// The extent the capture layer was arranged at, and the signal that says it is known. Arming and
	// knowing the size are two moments rather than one, so a caller off the UI thread waits for the
	// second instead of reading a zero and calling that a failure.
	std::mutex m_armedMutex;
	std::condition_variable m_armedSignal;
	bool m_armedKnown = false;
	int m_armedWidth = 0;
	int m_armedHeight = 0;

	// The picked element itself, held so its mark can be re-measured when the app moves it, and the
	// subscription that says when to.
	xaml::FrameworkElement m_selectedElement{ nullptr };
	winrt::event_token m_layoutToken{};

	// Guards the redraw against the layout pass it causes.
	bool m_updatingSelection = false;
	// The last place the pointer was seen, in window coordinates. Kept because a selection can be
	// made at a moment when there is no pointer event to read it from.
	winrt::Windows::Foundation::Point m_pointer{ -1.0f, -1.0f };
	bool m_overSelection = false;
	bool m_overPanel = false;
	double m_dragLeft = 16.0;
	double m_dragTop = 16.0;

	// Whether the toolbar has been put somewhere on purpose. Until it has, it re-centres itself as it
	// grows; afterwards it stays where it was put and is only kept inside the window.
	bool m_dragged = false;

	// One geometry report per app, not one per layout pass.
	bool m_reportedGeometry = false;

	// Whether the toolbar has had its one and only automatic placement.
	bool m_placed = false;
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

	// Magnification.
	xcontrols::StackPanel m_rows{ nullptr };
	xcontrols::StackPanel m_zoomRow{ nullptr };
	xcontrols::Button m_zoomButton{ nullptr };
	xcontrols::Button m_scaleButton{ nullptr };
	xcontrols::Button m_pixelButton{ nullptr };
	xcontrols::Button m_zoomInButton{ nullptr };
	xcontrols::Button m_zoomOutButton{ nullptr };
	xcontrols::TextBlock m_factorLabel{ nullptr };
	xcontrols::TextBlock m_hexLabel{ nullptr };
	xshapes::Rectangle m_swatch{ nullptr };
	xcontrols::Border m_lens{ nullptr };
	xcontrols::Image m_lensImage{ nullptr };
	xshapes::Rectangle m_pixelBox{ nullptr };
	xaml::DispatcherTimer m_lensTimer{ nullptr };

	// Members rather than lambda captures, deliberately: a completion handler runs on a pool thread
	// and is destroyed there, so a XAML object captured into one is released off the UI thread.
	ximaging::RenderTargetBitmap m_lensBitmap{ nullptr };
	winrt::Windows::Foundation::IAsyncOperation<winrt::Windows::Storage::Streams::IBuffer> m_lensPixels{ nullptr };
	Zoom m_zoom = Zoom::Off;
	int m_zoomFactor = 4;

	// The last capture, in device pixels, BGRA8. Only ever touched on the UI thread: the readback
	// completes on a pool thread and hands its bytes over through RoseTapRunOnUiThread rather than
	// storing them there, so nothing here needs a lock.
	std::vector<uint8_t> m_pixels;
	int m_pixelsWidth = 0;
	int m_pixelsHeight = 0;
	bool m_capturingFrame = false;

	// What the app's root had before the transform mode replaced it, so leaving restores rather than
	// clears. The flag and not a null check: null is a legitimate thing to have saved.
	struct SavedTransform
	{
		xaml::UIElement Element{ nullptr };
		xmedia::Transform Transform{ nullptr };
		winrt::Windows::Foundation::Point Origin{};
	};

	std::vector<SavedTransform> m_saved;
	bool m_transformSaved = false;
};

// Leaked deliberately: see the note on RoseOverlay. Only ever touched on the app's UI thread.
static RoseOverlay* g_overlay = nullptr;

static RoseOverlay& Overlay()
{
	if (!g_overlay) g_overlay = new RoseOverlay();
	return *g_overlay;
}
