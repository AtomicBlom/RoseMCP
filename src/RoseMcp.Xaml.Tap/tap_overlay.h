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
class RoseOverlay final : public IRoseOverlay, public IRoseOverlaySurface, protected RoseWidgets
{
public:
	// Idempotent: the second and later injections find the toolbar already there and leave it alone.
	void Install(IXamlDiagnostics* diagnostics, const std::vector<InstanceHandle>& appElements = {}) override
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
	void SetSources(std::map<InstanceHandle, std::wstring> sources) override
	{
		m_sources = std::move(sources);
	}

	// What the toolbar is currently being used for. An operation in progress pins the panel at full
	// strength: fading the thing somebody is in the middle of using is exactly the wrong moment for
	// it, and proximity is the wrong question to ask then -- during a pick the pointer is out in the
	// app by definition, which is precisely when the toolbar must stay readable.
	//
	// An enum and a set rather than a second look at one mode's flag, because Select is the first of
	// these and not the last: neither of the others should have to remember to do this. Adding one
	// here is adding one line there.
	enum class Operation
	{
		Select,
		Rulers,
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
		const bool busy = !m_operations.empty() || m_overPanel || m_zoomTool.Active();
		m_panelFade.To(busy ? PanelNear : PanelFar);
	}

	// What the pointer is being used for, one thing at a time. Both modes want the same full-bleed
	// layer over the app, and two of those would be two things claiming every click.
	//
	// One mode rather than a flag each, so "what is the overlay doing" has a single answer: the host
	// reports it, and a caller that wants the app clickable again asks for idle without having to
	// know which mode it is leaving.
	enum class Pointing
	{
		None,
		Select,
		Rulers,
	};

	// Arms select mode. Returns whether it is armed, so the host can confirm rather than assume --
	// including the case where the person had already armed it from the toolbar.
	bool BeginSelect(bool includeAllElements = false) override
	{
		m_includeAllElements = includeAllElements;
		return Point(Pointing::Select);
	}

	// Arms rulers mode: the picked element is the anchor, whatever the pointer is over is measured
	// against it, and the anchor's own margin and padding are drawn around it. A click anchors
	// somewhere else without leaving the mode, which is what makes it a sweep rather than a sequence
	// of arm-click-arm.
	bool BeginRulers(bool includeAllElements = false) override
	{
		m_includeAllElements = includeAllElements;
		return Point(Pointing::Rulers);
	}

	// Leaves whichever mode is on, which is the toolbar's Idle button. The pick is not a mode and
	// stays: clearing it is Deselect's job, and folding the two together would remove "keep this one
	// and stop capturing my clicks".
	void GoIdle() override
	{
		HideCapture();
		SetPointing(Pointing::None);
		m_pick.Hover(nullptr, std::wstring());
		m_rulersTool.Refresh(m_pointing == Pointing::Rulers);
		Chrome();
	}

	/// Whether a pick prefers the element declared in the app's own markup over a control template's
	/// parts. Set from the host or from the toolbar's toggle; the two are the same switch.
	void SetJustMyXaml(bool justMyXaml) override
	{
		m_justMyXaml = justMyXaml;
		Chrome();
	}

	/// The pick as rows, the mode, and why the last selection went away: everything a later request
	/// needs to answer "what is selected", with no file in between. Rows are in the shape the work
	/// folder writes, so one parser on the host serves whichever channel carried them.
	const std::string& SelectionRows() const override { return m_pick.Rows(); }
	const std::wstring& GoneReason() const override { return m_pick.GoneReason(); }
	bool Selecting() const { return m_pointing == Pointing::Select; }
	bool JustMyXaml() const override { return m_justMyXaml; }

	/// What the overlay is doing with the pointer, as the one word the host reports. Said rather than
	/// left to a flag, because a caller deciding whether the app is clickable needs the answer to
	/// cover every mode there is, including ones added after it was written.
	const wchar_t* ModeName() const override
	{
		switch (m_pointing)
		{
			case Pointing::Select: return L"select";
			case Pointing::Rulers: return L"rulers";
			default: return L"idle";
		}
	}

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
	bool WaitForArmedExtent(int& width, int& height, unsigned int timeoutMs) override
	{
		std::unique_lock<std::mutex> guard(m_armedMutex);
		m_armedSignal.wait_for(guard, std::chrono::milliseconds(timeoutMs), [this] { return m_armedKnown; });

		width = m_armedWidth;
		height = m_armedHeight;
		return m_armedKnown;
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
	bool Deselect() override
	{
		const bool had = m_pick.Clear(nullptr);

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
	void ClearIfRemoved(InstanceHandle handle) override
	{
		if (handle == 0 || handle != m_pick.Handle()) return;

		// Said rather than merely done. A selection that vanishes with no explanation reads as a bug
		// in the overlay, and an agent that asks for properties after a navigation deserves the
		// sentence rather than an HRESULT from three layers down.
		m_pick.Clear(L"The selected element was removed from the visual tree, so the selection was cleared. "
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
	bool SelectByHandle(InstanceHandle handle) override
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

		const bool drawn = m_pick.Take(element, handle, rect);

		Log(L"overlay: selected " + RosePick::Describe(element) + L" by handle; outline "
			+ std::wstring(drawn ? L"drawn" : L"NOT drawn"));

		return true;
	}

private:
	// Arms one of the two pointer modes, or confirms the one that is already armed.
	bool Point(Pointing mode)
	{
		if (!m_root || mode == Pointing::None) return false;

		// Switching between the modes keeps the layer that is already up rather than replacing it.
		// Arming is what a caller waits on, and what it waits for is a layer XAML has arranged --
		// which this one already is.
		if (m_pointing != Pointing::None)
		{
			SetPointing(mode);
			m_pick.Hover(nullptr, std::wstring());
			m_rulersTool.ClearMeasurement();
			m_rulersTool.Refresh(m_pointing == Pointing::Rulers);
			Chrome();
			WriteArmed();
			Log(L"overlay: " + std::wstring(ModeName()) + L" mode armed over the layer already up");
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

			// A faint wash, not a plain Transparent: this is the "a mode is on" affordance, and a
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
			// without it there is no evidence the overlay has noticed the pointer at all. In rulers
			// mode it is more than feedback -- the hovered element is half the measurement.
			m_capture.PointerMoved(
				[this](winrt::Windows::Foundation::IInspectable const&, xinput::PointerRoutedEventArgs const& e)
				{
					OnHover(e);
				});
			m_capture.PointerExited(
				[this](winrt::Windows::Foundation::IInspectable const&, xinput::PointerRoutedEventArgs const&)
				{
					m_pick.Hover(nullptr, std::wstring());
					m_rulersTool.ClearMeasurement();
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
			SetPointing(mode);
			m_rulersTool.Refresh(m_pointing == Pointing::Rulers);
			Chrome();
			Log(L"overlay: " + std::wstring(ModeName()) + L" mode armed");
			return true;
		}
		catch (winrt::hresult_error const& error)
		{
			Log(std::wstring(L"overlay: arming ") + ModeName() + L" mode failed: " + error.message().c_str());
			return false;
		}
	}

	// The mode and the operations that follow from it, in one place. Two modes and one operation set
	// is two things to keep in step, and the panel fade is exactly the sort of thing a new mode
	// forgets to do for itself.
	void SetPointing(Pointing mode)
	{
		m_pointing = mode;

		if (mode == Pointing::Select) BeginOperation(Operation::Select);
		else EndOperation(Operation::Select);

		if (mode == Pointing::Rulers) BeginOperation(Operation::Rulers);
		else EndOperation(Operation::Rulers);
	}

	// Takes the full-bleed layer away, which is what makes the app clickable again.
	void HideCapture()
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
	}

	// How near the pointer has to be for the toolbar to be fully legible, and what it settles to
	// when the pointer is elsewhere. The toolbar outlives everything, so it spends most of its life
	// being something the person did not ask to look at; fading on proximity is what lets it stay
	// available without staying in the way.
	static constexpr double PanelNear = 1.00;
	static constexpr double PanelFar = 0.50;

	// How far the toolbar sits from the edge it is anchored to.
	static constexpr double EdgeMargin = 16.0;

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

		// The box model's bands, under the outlines. Why, and why these colours, is on
		// RoseRulersTool::BuildBands.
		m_rulersTool.BuildBands();

		// The marks go on next so the toolbar always draws over them. Why each looks as it does is
		// on RosePick::Build, which owns them.
		m_pick.Build();

		m_rulersTool.BuildLeaders();

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

		// The three pointer modes first and together, because they are one choice: whatever the pointer
		// is doing, it is doing one of these, and the accent moves between them. What follows them is a
		// toggle and an action, which are different kinds of thing and do not belong in that run.
		m_idleButton = Chip(Glyph(IconIdle, 12.0), L"Idle", [this] { GoIdle(); });
		m_selectButton = Chip(SelectIcon(), L"Select element", [this] { BeginSelect(false); });
		m_rulersButton = Chip(
			Glyph(IconRulers, 12.0),
			L"Rulers -- show the picked element's margin and padding, and measure from it to whatever "
				L"the pointer is over. Click to anchor somewhere else",
			[this] { BeginRulers(); });
		m_bar.Children().Append(m_idleButton);
		m_bar.Children().Append(m_selectButton);
		m_bar.Children().Append(m_rulersButton);

		m_myXamlButton = Chip(
			Glyph(IconMyXaml, 12.0),
			L"Just my XAML -- pick the element declared in the app's own markup, not a control template's parts",
			[this] { ToggleMyXaml(); });
		m_bar.Children().Append(m_myXamlButton);

		// The way back out of a pick. Disabled rather than hidden when there is nothing selected:
		// a button that comes and goes moves the three beside it, and this toolbar sits over
		// somebody else's application.
		m_deselectButton = Chip(
			Glyph(IconDeselect, 12.0),
			L"Deselect -- clear the picked element and its mark",
			[this] { Deselect(); });
		m_bar.Children().Append(m_deselectButton);

		m_bar.Children().Append(m_zoomTool.Button());

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
		m_rows.Children().Append(m_rulersTool.BuildRow());
		m_rows.Children().Append(m_zoomTool.BuildZoomRow());

		auto content = xcontrols::Grid();
		content.Children().Append(m_rows);
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


	// Here rather than in the widget kit because it hands the handle to AttachDrag, which moves the
	// panel: the grip is part of this overlay rather than part of its visual language.
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
		Log(std::wstring(L"overlay: just-my-XAML ") + (m_justMyXaml ? L"on" : L"off"));
	}

	// The app's own content, which is deliberately not the root. The overlay sits on the diagnostics
	// UI layer -- a panel the framework hands out through GetUiLayer, beside the app's content rather
	// than inside it -- so rendering the content element captures the app and not this toolbar.
	// Rendering the root instead would put the toolbar inside its own lens.
	xaml::UIElement AppContent() const override
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


	// Which mode is current, said in the toolbar itself: the active button wears the accent, the
	// inactive one the panel's own grey. Just-my-XAML is a toggle rather than a mode, so it is lit
	// whenever it is on regardless of whether a pick is in progress.
	void Chrome() override
	{
		if (!m_idleButton || !m_selectButton) return;

		Paint(m_idleButton, m_pointing == Pointing::None ? Accent() : Idle());
		Paint(m_selectButton, m_pointing == Pointing::Select ? Accent() : Idle());
		Paint(m_rulersButton, m_pointing == Pointing::Rulers ? Accent() : Idle());
		Paint(m_myXamlButton, m_justMyXaml ? Accent() : Idle());

		// Deselect is an action rather than a mode, so it never wears the accent -- only whether
		// there is anything for it to do.
		Enable(m_deselectButton, m_pick.Has());

		// Its own buttons are its own business: which of them wears the accent depends on state
		// this no longer holds.
		m_zoomTool.RefreshChrome();
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
	winrt::Windows::Foundation::Size Extent() const override
	{
		const auto root = Root();
		return root ? root.Size() : winrt::Windows::Foundation::Size{ 0, 0 };
	}

	xaml::UIElement Content() const
	{
		const auto root = Root();
		return root ? root.Content() : nullptr;
	}

	// The rest of what a tool may ask of this overlay. Private overrides deliberately: a tool reaches
	// them through IRoseOverlaySurface, and nothing holding a RoseOverlay can, so the surface stays a
	// contract with the tools rather than becoming part of the overlay's own public shape.
	winrt::Windows::Foundation::Point Pointer() const override { return m_pointer; }

	xaml::UIElement Marks() const override { return m_marks; }

	void Adorn(xaml::UIElement const& element) override
	{
		if (m_canvas && element) m_canvas.Children().Append(element);
	}

	void Reposition() override { Place(); }

	void Mark(xaml::UIElement const& element) override
	{
		if (m_marks && element) m_marks.Children().Append(element);
	}

	// What depends on a pick: a measurement taken from an element that is no longer selected would
	// be a confident wrong answer, so the rulers are refreshed. The pick says it changed; deciding
	// what that costs is this overlay's business.
	void PickChanged() override
	{
		m_rulersTool.Refresh(m_pointing == Pointing::Rulers);
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
	bool Bounds(xaml::UIElement const& element, winrt::Windows::Foundation::Rect& rect) override
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

	// Both marks fade on the same rule: near the pointer they are legible, away from it they are a
	// hint. Only a change of state starts an animation -- this runs on every pointer move, and
	// restarting a storyboard sixty times a second over an app somebody is using is not acceptable.
	void Proximity(winrt::Windows::Foundation::Point const& point)
	{
		m_pointer = point;

		// Told rather than asked: what a pointer move means to the magnifier is its own business,
		// and m_pointer is already set above for it to read.
		m_zoomTool.PointerMoved();

		// Told rather than asked, for the same reason: how loud the pick's mark should be is the
		// pick's own business.
		m_pick.PointerMoved(point);

		const bool overPanel = Contains(PanelRect(), point);
		if (overPanel != m_overPanel)
		{
			m_overPanel = overPanel;
			RefreshPanelFade();
		}
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

	// The rulers mode's own drawing, all of it on the marks canvas so it scales with a magnified app
	// exactly as the outlines do.
	//
	// Two things are drawn and they answer different halves of one question. The bands are the
	// anchor's own box model -- its margin outside its rectangle, its border and padding inside --
	// which is what says whether the space around a control comes from the control itself. The leaders
	// are the distances to whatever the pointer is over, which is what says whether that space comes
	// from the gap to its neighbour. Somebody looking at four pixels they cannot account for has to be
	// able to tell those apart, and neither drawn alone does it.


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

			Trace(L"beneath: " + RosePick::Describe(element) + L" at " + std::to_wstring(static_cast<int>(rect.X))
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
			m_pick.Hover(element, element ? RosePick::Describe(element) : std::wstring());

			if (m_pointing == Pointing::Rulers) m_rulersTool.MeasureTo(element, rect);
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
				// The picked element keeps its outline after select mode ends: that persistent mark is
				// the evidence of what "the selected element" now means, for the person and the agent.
				const bool drawn = m_pick.Take(element, Record(element, point), rect);
				Log(L"overlay: selection outline " + std::wstring(drawn ? L"drawn" : L"NOT drawn"));
			}
		}
		catch (winrt::hresult_error const& error)
		{
			Log(std::wstring(L"overlay: hit test failed: ") + error.message().c_str());
		}

		// Picking is the end of select mode and the *middle* of rulers mode. A click there moves the
		// anchor and the sweep carries on, which is what makes it one gesture rather than a round of
		// arm, click, arm again for every element somebody wants to measure from.
		if (m_pointing == Pointing::Select) GoIdle();
		else m_rulersTool.ClearMeasurement(); // The pointer is over the new anchor, so there is nothing to measure to.
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

		m_pick.PublishRows(rows.str());

		Log(L"overlay: recorded " + RosePick::Describe(element) + L" and " + std::to_wstring(written) + L" row(s) from the tree");
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

		m_pick.PublishRows(file.str());
		Log(L"overlay: recorded " + RosePick::Describe(element) + L" and " + std::to_wstring(written) + L" candidate(s)");

		return selected;
	}

	/// <summary>
	/// Keeps the recorded selection where a later request can read it.
	/// </summary>
	/// <remarks>
	/// Held rather than written, because a pick outlives the request that armed it by design: the person
	/// clicks whenever they click, and the host asks afterwards. Holding it is what lets that later
	/// question be answered on the same channel as every other one.
	/// </remarks>
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
	Fader m_panelFade;

	// The extent the capture layer was arranged at, and the signal that says it is known. Arming and
	// knowing the size are two moments rather than one, so a caller off the UI thread waits for the
	// second instead of reading a zero and calling that a failure.
	std::mutex m_armedMutex;
	std::condition_variable m_armedSignal;
	bool m_armedKnown = false;
	int m_armedWidth = 0;
	int m_armedHeight = 0;
	// The last place the pointer was seen, in window coordinates. Kept because a selection can be
	// made at a moment when there is no pointer event to read it from.
	winrt::Windows::Foundation::Point m_pointer{ -1.0f, -1.0f };
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
	Pointing m_pointing = Pointing::None;
	std::set<Operation> m_operations;
	int m_traces = 0;
	bool m_includeAllElements = false;
	bool m_justMyXaml = true;
	std::map<InstanceHandle, std::wstring> m_sources;

	// The rulers mode's button. It arms a pointer mode, which is arbitration between modes, so it
	// stays here while the row and the drawing are the tool's.
	xcontrols::Button m_rulersButton{ nullptr };

	// The rows of the panel: the bar, the rulers readout, and the magnifier's own row.
	xcontrols::StackPanel m_rows{ nullptr };

	// The pick: which element is selected, the marks that say so, and what a later request is told
	// about it. Select mode writes it and rulers mode only reads it, which is why it is an object of
	// its own rather than fifteen members here.
	//
	// Declared before the tools, since the rulers hold a reference to it and a member's initialiser
	// runs in declaration order.
	RosePick m_pick{ *this };

	// The two tools, each owning its own row of the toolbar, its own marks and its own state, and
	// each reaching back through IRoseOverlaySurface for what only the overlay can answer.
	// Constructed with *this because the surface is this overlay; both store the reference and
	// nothing more, so handing it over from an initialiser is safe.
	RoseZoomTool m_zoomTool{ *this };
	RoseRulersTool m_rulersTool{ *this, m_pick };
};

// Leaked deliberately: see the note on RoseOverlay. Only ever touched on the app's UI thread.
static RoseOverlay* g_overlay = nullptr;

static IRoseOverlay& Overlay()
{
	if (!g_overlay) g_overlay = new RoseOverlay();
	return *g_overlay;
}
