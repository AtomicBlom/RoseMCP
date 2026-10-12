#pragma once

// The change marks: an outline round each element a live edit changed, and a tag under it saying what
// changed, to what, and how long ago.
//
// Included after the provider's aliases, like everything else that builds XAML.
//
// What an apply changed is handed over by the request dispatcher, on the UI thread, in the same
// dispatch as the batch that changed it -- so a mark is drawn against the tree the edits just made.
// Only a property set or cleared on an element that was already in the app is marked: an element the
// batch built is the change itself, and the properties it was built with were never different on
// anybody's screen. The XAML view reads the broker's record of every edit that took, structural ones
// included, so nothing an agent did is missing from both places.
//
// The marks fade from the app ten minutes after they were drawn, or when the switch goes off. Switching
// it on again draws every change this session made, each with a fresh ten minutes from the press: the
// person pressing it is asking to see them, and a switch that turned on to show nothing would read as
// broken. The XAML view keeps its own marks for the whole session; these are for looking at the app.
//
// Every mark is placed by asking the app where its element is, once a second while any is on screen,
// and the element is looked up by handle each time rather than held: an element the app lets go is not
// kept alive by a mark, and a mark whose element is gone or elsewhere is simply not drawn. The timer is
// what makes this cost something, so it runs only while there is a mark to place.

class RoseChangesTool final : protected RoseWidgets
{
public:
	explicit RoseChangesTool(IRoseOverlaySurface& surface) : m_surface(surface) {}

	// Whether the changes are marked in the app. On until somebody turns it off: an edit an agent made
	// to somebody's screen is the thing worth seeing first.
	bool Shown() const { return m_shown; }

	/// Takes what an apply changed. A change to an element already marked moves its tag to the newest
	/// change and starts its ten minutes again; the oldest element marked is let go when there are more
	/// than the overlay will draw.
	void Record(const std::vector<TapChange>& changes)
	{
		if (changes.empty()) return;

		const auto now = Clock::now();
		const auto wall = std::chrono::system_clock::now();

		for (const auto& change : changes)
		{
			if (change.Handle == 0) continue;

			auto& marked = m_marked[change.Handle];
			marked.Handle = change.Handle;
			marked.Since = now;
			marked.Changes.push_back({ change.Property, change.Old, change.New, wall });
			if (marked.Changes.size() > MaxChangesPerElement) marked.Changes.erase(marked.Changes.begin());
		}

		while (m_marked.size() > MaxElements) Drop(Oldest());

		if (m_shown) Draw();
	}

	/// Switches the marks on or off. On draws every change this session made, each for another ten
	/// minutes from now; off takes them all down and stops the timer, and nothing is drawn again until
	/// it is switched back on.
	void Show(bool shown)
	{
		if (shown == m_shown) return;

		m_shown = shown;
		Log(std::wstring(L"overlay: change marks ") + (shown ? L"on" : L"off"));

		if (!shown)
		{
			StopTimer();
			for (auto& [handle, marked] : m_marked) Conceal(marked);
			return;
		}

		const auto now = Clock::now();
		for (auto& [handle, marked] : m_marked) marked.Since = now;

		Draw();
	}

	/// Drops every change and takes its mark off the app, because the session the edits were made in
	/// has ended. The switch keeps its position: it is the person's, and a later session's changes are
	/// marked or not as they left it.
	void Forget()
	{
		StopTimer();

		for (auto& [handle, marked] : m_marked)
		{
			m_surface.Unmark(marked.Box);
			m_surface.Unmark(marked.Tag);
		}

		m_marked.clear();
	}

private:
	using Clock = std::chrono::steady_clock;

	// How long a mark stays on the app after it was drawn. Long enough to go and look after an agent
	// says what it did; short enough that a session's marks do not accumulate over the app being edited.
	static constexpr auto Lifetime = std::chrono::minutes(10);

	// How many elements are marked at once, and how many changes each remembers. A batch that restyles a
	// whole page should not cover it in tags, and the tag says only the newest change and how many more.
	static constexpr size_t MaxElements = 24;
	static constexpr size_t MaxChangesPerElement = 8;

	// The tag's longest value, beyond which it is cut: a template or a long string would otherwise make
	// one tag wider than the element it labels.
	static constexpr size_t MaxValueLength = 24;

	struct Change
	{
		std::wstring Property;
		std::wstring Old;
		std::wstring New;
		std::chrono::system_clock::time_point When;
	};

	struct Marked
	{
		InstanceHandle Handle = 0;
		std::vector<Change> Changes;

		// When this mark's ten minutes started: the newest change to its element, or the press that
		// switched the marks back on.
		Clock::time_point Since{};

		xcontrols::Grid Box{ nullptr };
		xcontrols::Border Tag{ nullptr };
	};

	InstanceHandle Oldest() const
	{
		InstanceHandle oldest = 0;
		auto since = (Clock::time_point::max)();

		for (const auto& [handle, marked] : m_marked)
		{
			if (marked.Since < since)
			{
				since = marked.Since;
				oldest = handle;
			}
		}

		return oldest;
	}

	void Drop(InstanceHandle handle)
	{
		const auto found = m_marked.find(handle);
		if (found == m_marked.end()) return;

		m_surface.Unmark(found->second.Box);
		m_surface.Unmark(found->second.Tag);
		m_marked.erase(found);
	}

	// Places every mark still inside its ten minutes, takes down the rest, and keeps the timer running
	// only while one is on screen.
	void Draw()
	{
		const auto now = Clock::now();
		const auto wall = std::chrono::system_clock::now();
		bool any = false;

		for (auto& [handle, marked] : m_marked)
		{
			if (!m_shown || now - marked.Since > Lifetime)
			{
				Conceal(marked);
				continue;
			}

			// Counted whether or not it can be drawn this second: it is still inside its ten minutes, and an
			// element scrolled out of view or collapsed for a moment is drawn again when it comes back.
			any = true;
			if (!Place(marked, wall)) Conceal(marked);
		}

		if (any) StartTimer();
		else StopTimer();
	}

	// Puts one mark round its element, building it the first time. False where the element is gone,
	// elsewhere, or has no size.
	bool Place(Marked& marked, std::chrono::system_clock::time_point wall)
	{
		const auto element = m_surface.ElementOf(marked.Handle);
		if (!element) return false;

		winrt::Windows::Foundation::Rect rect{};
		if (!m_surface.Bounds(element, rect)) return false;

		if (!marked.Box) Build(marked);

		// Outside the element by a few pixels, so the outline does not sit on the edge the change may
		// well have been about -- a margin, a border.
		constexpr double outset = 3.0;
		marked.Box.Width(rect.Width + 2.0 * outset);
		marked.Box.Height(rect.Height + 2.0 * outset);
		xcontrols::Canvas::SetLeft(marked.Box, rect.X - outset);
		xcontrols::Canvas::SetTop(marked.Box, rect.Y - outset);
		marked.Box.Visibility(xaml::Visibility::Visible);

		if (const auto text = marked.Tag.Child().try_as<xcontrols::TextBlock>()) text.Text(Words(marked, wall));

		// Under the element's lower left, where the pick's caption -- above, at the upper left -- is
		// not. Inside the element instead where under it would be off the bottom of the window.
		const double below = rect.Y + rect.Height + outset + 2.0;
		const double extent = m_surface.Extent().Height;
		const double top = below + BadgeHeight > extent ? rect.Y + rect.Height - BadgeHeight - 2.0 : below;
		xcontrols::Canvas::SetLeft(marked.Tag, rect.X);
		xcontrols::Canvas::SetTop(marked.Tag, top);
		marked.Tag.Visibility(xaml::Visibility::Visible);
		return true;
	}

	// Teal rather than the accent, because the accent is the pick's: an element that is both picked and
	// changed has to show both, and two rose outlines one inside the other read as one thicker one.
	void Build(Marked& marked)
	{
		marked.Box = xcontrols::Grid();
		marked.Box.IsHitTestVisible(false);
		marked.Box.Visibility(xaml::Visibility::Collapsed);

		// The dark companion, for the same reason every outline here has one: one stroke in one colour
		// disappears on an app drawn in it.
		auto contrast = xshapes::Rectangle();
		contrast.Stroke(Brush(0xB0, 0x10, 0x10, 0x14));
		contrast.StrokeThickness(1);
		contrast.Margin(xaml::Thickness{ -1.5, -1.5, -1.5, -1.5 });
		contrast.StrokeDashArray().Append(3);
		contrast.StrokeDashArray().Append(2);
		marked.Box.Children().Append(contrast);

		auto line = xshapes::Rectangle();
		line.Stroke(Brush(0xFF, 0x0E, 0x9F, 0xBF));
		line.StrokeThickness(1.5);
		line.StrokeDashArray().Append(3);
		line.StrokeDashArray().Append(2);
		marked.Box.Children().Append(line);

		marked.Tag = xcontrols::Border();
		marked.Tag.IsHitTestVisible(false);
		marked.Tag.Visibility(xaml::Visibility::Collapsed);
		marked.Tag.Background(Brush(0xF0, 0x0E, 0x7C, 0x96));
		marked.Tag.BorderBrush(Brush(0x90, 0x10, 0x10, 0x14));
		marked.Tag.BorderThickness(xaml::Thickness{ 1, 1, 1, 1 });
		marked.Tag.CornerRadius(xaml::CornerRadius{ 3, 3, 3, 3 });
		marked.Tag.Padding(xaml::Thickness{ 5, 1, 5, 2 });
		marked.Tag.Child(Label(L"", 11.0, 0xFF, nullptr));

		m_surface.Mark(marked.Box);
		m_surface.Mark(marked.Tag);
	}

	static void Conceal(Marked const& marked)
	{
		if (marked.Box) marked.Box.Visibility(xaml::Visibility::Collapsed);
		if (marked.Tag) marked.Tag.Visibility(xaml::Visibility::Collapsed);
	}

	// "Margin 12,0,12,0 → 8,0,8,0 · agent · 2 min ago", with how many other properties changed when
	// more than one did. The newest change leads because it is the one an agent has just said it made.
	static std::wstring Words(Marked const& marked, std::chrono::system_clock::time_point wall)
	{
		if (marked.Changes.empty()) return std::wstring();

		const auto& newest = marked.Changes.back();
		const std::wstring now = newest.New.empty() ? std::wstring(L"cleared") : Shortened(newest.New);
		std::wstring words = newest.Property + L" " + Shortened(newest.Old) + L" \x2192 " + now;

		std::vector<std::wstring> others;
		for (const auto& change : marked.Changes)
		{
			const bool counted = change.Property == newest.Property
				|| std::find(others.begin(), others.end(), change.Property) != others.end();
			if (!counted) others.push_back(change.Property);
		}

		if (!others.empty()) words += L" +" + std::to_wstring(others.size()) + L" more";

		return words + L" \x00B7 agent \x00B7 " + Ago(wall - newest.When);
	}

	// An empty value is said as two quotes rather than as nothing, since a TextBlock's text really can be
	// empty and "Text  → Hello" reads as a value that went missing.
	static std::wstring Shortened(std::wstring const& value)
	{
		if (value.empty()) return L"\"\"";
		if (value.size() <= MaxValueLength) return value;

		return value.substr(0, MaxValueLength - 1) + L"\x2026";
	}

	static std::wstring Ago(std::chrono::system_clock::duration elapsed)
	{
		const auto minutes = std::chrono::duration_cast<std::chrono::minutes>(elapsed).count();
		if (minutes < 1) return L"just now";
		if (minutes == 1) return L"1 min ago";

		return std::to_wstring(minutes) + L" min ago";
	}

	void StartTimer()
	{
		if (!m_timer)
		{
			m_timer = xaml::DispatcherTimer();
			m_timer.Interval(std::chrono::seconds(1));
			m_timer.Tick(
				[this](winrt::Windows::Foundation::IInspectable const&, winrt::Windows::Foundation::IInspectable const&)
				{
					try
					{
						Draw();
					}
					catch (winrt::hresult_error const&)
					{
						// Runs over somebody else's app for as long as a mark is up. Never throw out of it.
					}
				});
		}

		if (!m_timer.IsEnabled()) m_timer.Start();
	}

	void StopTimer()
	{
		if (m_timer && m_timer.IsEnabled()) m_timer.Stop();
	}

	// By handle, because a change names its element by handle and a second change to the same element
	// has to find the mark the first one made.
	std::map<InstanceHandle, Marked> m_marked;
	bool m_shown = true;

	// Ticks on the UI thread, since it was made there, which is the only thread a mark may be touched on.
	xaml::DispatcherTimer m_timer{ nullptr };

	IRoseOverlaySurface& m_surface;
};
