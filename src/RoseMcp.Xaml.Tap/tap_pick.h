#pragma once

// The pick: which element is selected, the marks that say so, and what a later request is told
// about it.
//
// Included after the provider's aliases, like everything else that builds XAML.
//
// A state object rather than a tool, because two modes read it and only one writes it. Select mode
// puts a pick here by clicking; rulers mode measures *from* it and never changes it. Left as members
// of the overlay, those two were indistinguishable -- rulers reached into the selected element, the
// anchor rectangle and select's own two badges, and nothing said which of them owned what.
//
// It owns the visual half as well as the data, and that is deliberate: the box, the badge and the
// fade are how the pick is *said*, and a pick whose rectangle and whose mark can disagree is the
// failure the two of them exist to prevent. One object holds both, so they change together.

class RosePick final : protected RoseWidgets
{
public:
	explicit RosePick(IRoseOverlaySurface& surface) : m_surface(surface) {}

	// Builds the four marks and puts them on the layer that scales with the app. Hover is dashed and
	// thin, the pick solid and heavier, so the two never read as the same thing.
	void Build()
	{
		m_hoverBox = Outline(1.0, true);
		m_surface.Mark(m_hoverBox);
		m_hoverBadge = Badge();
		m_surface.Mark(m_hoverBadge);

		// The pick rests on screen until something else replaces it or it is cleared, so it is the
		// one mark that has to be liveable with. At full strength on a large container it is a
		// full-window box sitting over the app for as long as the selection lasts, which is what a
		// second user reported: not hard to see, hard to put up with.
		//
		// So it is drawn at the strength it should have when somebody is looking at it, and its own
		// opacity carries the rest -- SelectionNear when the pointer is inside it, SelectionFar when
		// it is not. Baking the fade into the brushes instead was the first attempt, and it cannot
		// express the thing that actually makes this work: the mark being loud enough to read at the
		// moment you look for it.
		m_selectBox = Outline(2.0, false, 0xFF, 0x33);
		m_surface.Mark(m_selectBox);
		m_selectBadge = Badge();
		m_surface.Mark(m_selectBadge);

		m_selectionFade = MakeFader({ m_selectBox, m_selectBadge });
	}

	// What is picked, for the modes that read it. A null element and a false Has() are the same
	// state said two ways; both are offered because a caller testing "is there a pick" should not
	// have to hold a projected type to ask.
	bool Has() const { return m_hasSelection; }
	InstanceHandle Handle() const { return m_selectedHandle; }
	xaml::FrameworkElement Element() const { return m_selectedElement; }

	// The anchor's own rectangle, which is what a measurement is taken from. The pick also holds the
	// rectangle the pointer is tested against -- grown to take in the badge -- but that one is the
	// proximity fade's business and stays private.
	winrt::Windows::Foundation::Rect AnchorRect() const { return m_anchorRect; }

	// The two captions, which a measurement must not draw a number over. Handed out because both sit
	// at the top-left corner of a rectangle -- exactly where the top and left insets are measured.
	std::vector<xcontrols::Border> Captions() const { return { m_selectBadge, m_hoverBadge }; }

	// The pick as rows, and why the last one went away. Both answer a question asked after the fact,
	// so they are held rather than derived: the stack a click landed on cannot be recovered once the
	// pointer has moved.
	const std::string& Rows() const { return m_selectionRows; }
	const std::wstring& GoneReason() const { return m_goneReason; }
	// A new pick's rows, which also retires the note about why the last one went away: that note
	// exists to explain an empty selection, and this one is not empty.
	void PublishRows(std::string rows)
	{
		m_selectionRows = std::move(rows);
		m_goneReason.clear();
	}

	// Draws the hover mark over an element, or takes it away when there is nothing under the pointer.
	void Hover(xaml::UIElement const& element, std::wstring const& caption)
	{
		ShowBox(m_hoverBox, m_hoverBadge, element, caption);
	}

	/// Takes a pick, and reports whether the mark could be drawn.
	///
	/// The pick is taken either way, and the return value is not a failure. An element that cannot
	/// be measured is still the selected element -- every later request is about it, and an edit
	/// addressed to it still lands -- so "drawn nothing" and "nothing is selected" are two facts and
	/// this sets only the second. The caller logs the first.
	///
	/// The element's layout is watched for the same reason however the pick was made: the mark is
	/// drawn at coordinates read out of the app once, and an app that moves the element afterwards
	/// leaves the mark behind. How the selection was made says nothing about whether the element
	/// stays still.
	bool Take(xaml::UIElement const& element, InstanceHandle handle, winrt::Windows::Foundation::Rect const& rect)
	{
		const bool drawn = ShowBox(m_selectBox, m_selectBadge, element, Describe(element));

		m_hasSelection = true;
		m_selectedHandle = handle;
		m_selectionRect = WithBadge(rect);
		m_anchorRect = rect;
		WatchSelectionLayout(element);
		Reveal();

		m_surface.PickChanged();
		m_surface.Chrome();
		return drawn;
	}

	// Fades the mark on proximity. Both marks are persistent by design, so both spend most of
	// their life being something the person did not ask to look at.
	void PointerMoved(winrt::Windows::Foundation::Point const& point)
	{
		const bool inside = m_hasSelection && Contains(m_selectionRect, point);
		if (inside == m_overSelection) return;

		m_overSelection = inside;
		m_selectionFade.To(inside ? SelectionNear : SelectionFar);
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
		m_anchorRect = {};
		m_selectedHandle = 0;
		m_selectionRows.clear();

		// The bands and any leaders go with it. They describe an element that is no longer the
		// selection, and a measurement from a rectangle nothing is pointing at is the confident wrong
		// answer this repository likes least.
		m_surface.PickChanged();

		// The note outlives this call, because nothing is waiting on it: the element went away between
		// requests, and the host only finds out when it next asks.
		if (goneReason && had) m_goneReason = goneReason;

		m_surface.Chrome();
		return had;
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

private:
	// How near the pointer has to be for a mark to be legible, and what it settles to when the
	// pointer is elsewhere. Both marks are persistent by design -- the selection outlives the pick
	// that made it, and the toolbar outlives everything -- so both spend most of their life being
	// something the person did not ask to look at. Fading on proximity is what lets them stay
	// available without staying in the way.
	static constexpr double SelectionNear = 0.50;
	static constexpr double SelectionFar = 0.10;

	// Shows a freshly made selection, snapping when the pointer is already inside it and fading it up
	// when it is not.
	//
	// Asking where the pointer is, rather than who asked for the selection, because that is the
	// question the answer actually turns on -- and it happens to answer both callers. A click lands
	// under the pointer, so the mark should simply be there: fading up would pretend the pointer were
	// still on its way to somewhere it already is. A selection made by handle, which is the way an
	// agent reaches this, lands wherever the element happens to be, and appearing at full
	// strength somewhere the person is not looking is a flash in the corner of the eye rather than an
	// answer. Both paths reach here, so which one it was is never asked: the pointer's position is
	// the whole question, and a selection by handle that happens to land under the pointer snaps
	// for the same reason a click does.
	void Reveal()
	{
		m_overSelection = Contains(m_selectionRect, m_surface.Pointer());

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
					if (!m_surface.Bounds(m_selectedElement, rect)) return;

					const auto withBadge = WithBadge(rect);
					if (withBadge.X == m_selectionRect.X && withBadge.Y == m_selectionRect.Y
						&& withBadge.Width == m_selectionRect.Width && withBadge.Height == m_selectionRect.Height)
					{
						return;
					}

					m_updatingSelection = true;
					ShowBox(m_selectBox, m_selectBadge, m_selectedElement, Describe(m_selectedElement));
					m_anchorRect = rect;
					m_selectionRect = withBadge;

					// Inside the guard, because drawing the bands is itself a layout pass: outside it
					// this handler would schedule itself for as long as the app stayed up.
					m_surface.PickChanged();
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

		if (!element || !m_surface.Bounds(element, rect))
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

	// Where the selection is, so the pointer can be tested against it without asking the app.
	// Held rather than recomputed: an element that has moved or been laid out again is its own
	// problem, and reaching into the app's tree on every pointer move to find out would be less a
	// fix for it so much as a reason to be blamed for the app feeling slow.
	winrt::Windows::Foundation::Rect m_selectionRect{};

	// The anchor's own rectangle, without the badge the proximity test adds to it. Held for the same
	// reason: a hover is hundreds of events a second, and asking the app where the anchor is on each
	// one would put a TransformToVisual in the pointer's path for a number that has not changed.
	winrt::Windows::Foundation::Rect m_anchorRect{};

	// Which element is selected, so a removal can be recognised. The handle and not the name, since
	// a Remove callback carries an empty Name, measured, so matching on one would never fire.
	InstanceHandle m_selectedHandle = 0;

	// The pick, as the rows that describe it, and the reason the last one went away. Both answer
	// a question asked after the fact, so they are held rather than derived on demand: the stack a
	// click landed on cannot be recovered once the pointer has moved.
	std::string m_selectionRows;
	std::wstring m_goneReason;

	// The picked element itself, held so its mark can be re-measured when the app moves it, and the
	// subscription that says when to.
	xaml::FrameworkElement m_selectedElement{ nullptr };
	winrt::event_token m_layoutToken{};

	// Guards the redraw against the layout pass it causes.
	bool m_updatingSelection = false;

	// Whether the pointer is inside the mark, which is what its opacity follows.
	bool m_overSelection = false;

	// Whether a pick is currently marked. Tracked rather than inferred from the box's visibility,
	// because the box is also hidden when an element could not be measured, and "drawn nothing"
	// is not the same fact as "nothing is selected".
	bool m_hasSelection = false;

	xcontrols::Grid m_hoverBox{ nullptr };
	xcontrols::Grid m_selectBox{ nullptr };
	xcontrols::Border m_hoverBadge{ nullptr };
	xcontrols::Border m_selectBadge{ nullptr };
	Fader m_selectionFade;

	IRoseOverlaySurface& m_surface;
};
