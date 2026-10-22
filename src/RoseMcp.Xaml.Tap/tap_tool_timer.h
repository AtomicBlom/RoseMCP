#pragma once

// The delayed select: a chip that holds a delay, the menu that chooses it, and the countdown that
// runs on the chip before the overlay enters select mode.
//
// Included after the provider's aliases, like the overlay it lives on, because it builds XAML.
//
// It exists for the UI that has to be opened by hand before it can be picked: a flyout, a menu, a
// hover state. Clicking Select arms the mode at once, with the pointer on the toolbar and the thing
// to inspect still closed. With a delay set, the countdown puts nothing over the app and captures
// nothing, so the person spends it opening what they want, and at zero select mode arms exactly as
// the chip would have -- the delay goes before the mode and changes nothing about it, as the
// Snipping Tool's timer does.
//
// Zero is a single DispatcherTimer on the UI thread, and the digit and the sweep a single storyboard
// of the same length started in the same call. They are not one clock: the sweep turns off the UI
// thread, so in an app whose UI thread is busy it can run out before the tick is served, and select
// arms when the tick is. What the timer decides is when select arms; the storyboard only shows it.
// Nothing of ours runs on another thread, nothing sleeps, and every way out of a countdown goes
// through Finish, which stops both.

class RoseTimerTool final : protected RoseWidgets
{
public:
	// expired is called when a countdown reaches zero, and the overlay arms select. busy is told whenever
	// the menu opens or closes and whenever a countdown starts or stops, so the overlay can keep the
	// panel at full strength while either is going on: a countdown happens with the pointer out in
	// the app, which is exactly when the panel would otherwise fade.
	RoseTimerTool(IRoseOverlaySurface& surface, std::function<void()> expired, std::function<void(bool)> busy)
		: m_surface(surface), m_expired(std::move(expired)), m_busy(std::move(busy))
	{
	}

	// The chip for the main bar, built here so the tool owns everything that shows its state.
	xcontrols::Button Button()
	{
		m_sweep = BuildSweep();

		m_iconSlot = xcontrols::Grid();
		m_iconSlot.HorizontalAlignment(xaml::HorizontalAlignment::Center);
		m_iconSlot.VerticalAlignment(xaml::VerticalAlignment::Center);

		auto face = xcontrols::Grid();
		face.Width(ChipSize);
		face.Height(ChipSize);
		face.Children().Append(m_sweep);
		face.Children().Append(m_iconSlot);

		m_button = Chip(
			face,
			L"Delay before select -- with a delay set, Select element counts down first, then enters select "
				L"mode. Open the flyout or hover the control meanwhile",
			[this] { OnChip(); });

		ShowDelay();
		return m_button;
	}

	// Seconds, or zero for no delay. Kept for the life of the overlay rather than reset after a pick,
	// so somebody picking several flyout items in turn sets it once.
	int Delay() const { return m_delay; }

	bool Running() const { return m_running; }

	// Starts the countdown. The caller has already left any pointer mode, so nothing is captured
	// while it runs.
	void Start()
	{
		if (m_delay <= 0 || m_running) return;

		CloseMenu();

		try
		{
			if (!m_timer)
			{
				m_timer = xaml::DispatcherTimer();
				m_timer.Tick(
					[this](winrt::Windows::Foundation::IInspectable const&, winrt::Windows::Foundation::IInspectable const&)
					{
						try
						{
							// Stopped before anything else: a DispatcherTimer has no one-shot mode, and a
							// second tick behind a slow arm would arm twice.
							Finish(true);
						}
						catch (winrt::hresult_error const& error)
						{
							// Runs inside somebody else's app. Never throw out of it.
							Log(std::wstring(L"overlay: countdown expiry failed: ") + error.message().c_str());
						}
					});
			}

			m_timer.Interval(std::chrono::seconds(m_delay));

			m_board = BuildCountdown(m_delay);
			m_sweep.Visibility(xaml::Visibility::Visible);
			m_running = true;

			m_timer.Start();
			m_board.Begin();
		}
		catch (winrt::hresult_error const& error)
		{
			Log(std::wstring(L"overlay: countdown could not start: ") + error.message().c_str());
			Finish(false);
			return;
		}

		Notify();
		Log(L"overlay: countdown of " + std::to_wstring(m_delay) + L"s started");
	}

	// Stops a countdown without arming select. Safe whether or not one is running; returns whether one
	// was.
	bool Cancel()
	{
		if (!m_running) return false;

		Finish(false);
		Log(L"overlay: countdown cancelled");
		return true;
	}

	// Closes the menu and cancels any countdown: what the toolbar folding away means for this tool.
	void Dismiss()
	{
		CloseMenu();
		Cancel();
	}

	// Never lit, unlike the modes beside it. The accent on this toolbar means a mode is taking the
	// pointer, and a delay takes nothing: the app stays clickable until the countdown ends. The delay
	// is read from the icon instead -- the number on the stopwatch, or the slash for none -- as the
	// Snipping Tool shows it. Only the menu's mark on the current choice follows the state.
	void RefreshChrome()
	{
		for (auto const& row : m_rows)
		{
			row.Bar.Visibility(row.Seconds == m_delay ? xaml::Visibility::Visible : xaml::Visibility::Collapsed);
		}
	}

private:
	// The menu's choices, which are the Snipping Tool's.
	static constexpr int Choices[] = { 0, 3, 5, 10 };

	static constexpr double MenuWidth = 168.0;
	static constexpr double RowHeight = 28.0;
	static constexpr double MenuGap = 6.0;

	// A choice in the menu: the button that takes it, and the mark shown on it while it is the current
	// one.
	struct MenuChoice
	{
		int Seconds = 0;
		xcontrols::Button Button{ nullptr };
		xshapes::Rectangle Bar{ nullptr };
	};

	// What the menu calls each choice, which is also what a screen reader says for it and for the chip.
	static std::wstring Describe(int seconds)
	{
		return seconds <= 0 ? std::wstring(L"No delay") : std::to_wstring(seconds) + L"-second delay";
	}

	// The stopwatch, with the delay laid over it in a badge, or slashed for no delay. MDL2 has a
	// stopwatch but no numbered or slashed one, so those are composed, the way SelectIcon is.
	struct TimerFace
	{
		xcontrols::Grid Host{ nullptr };
		xcontrols::TextBlock Digit{ nullptr };
	};

	static TimerFace TimerIcon(int seconds)
	{
		TimerFace face;
		face.Host = xcontrols::Grid();
		face.Host.Width(16);
		face.Host.Height(16);

		auto watch = Glyph(IconTimer, 13.0);
		watch.HorizontalAlignment(xaml::HorizontalAlignment::Left);
		watch.VerticalAlignment(xaml::VerticalAlignment::Top);
		face.Host.Children().Append(watch);

		if (seconds <= 0)
		{
			// A dark stroke under the light one, so the slash reads over the glyph it crosses.
			for (const auto& [thickness, grey] : { std::pair{ 3.0, 0x1C }, std::pair{ 1.5, 0xDC } })
			{
				auto slash = xshapes::Line();
				slash.X1(2);
				slash.Y1(2);
				slash.X2(14);
				slash.Y2(14);
				slash.StrokeThickness(thickness);
				slash.Stroke(Brush(0xFF, static_cast<uint8_t>(grey), static_cast<uint8_t>(grey), static_cast<uint8_t>(grey)));
				face.Host.Children().Append(slash);
			}

			return face;
		}

		face.Digit = Label(std::to_wstring(seconds).c_str(), 8.0, 0xF4, nullptr);
		face.Digit.FontWeight(winrt::Windows::UI::Text::FontWeight{ 700 });

		auto badge = xcontrols::Border();
		badge.Background(Brush(0xFF, 0x1C, 0x1C, 0x22));
		badge.CornerRadius(xaml::CornerRadius{ 2, 2, 2, 2 });
		badge.Padding(xaml::Thickness{ 1, 0, 1, 0 });
		badge.HorizontalAlignment(xaml::HorizontalAlignment::Right);
		badge.VerticalAlignment(xaml::VerticalAlignment::Bottom);
		badge.Margin(xaml::Thickness{ 0, 0, -3, -2 });
		badge.Child(face.Digit);
		face.Host.Children().Append(badge);

		return face;
	}

	// Puts the chosen delay on the chip. The icon is rebuilt rather than edited, because no delay and
	// a delay are different compositions; the countdown keeps the digit it animates.
	void ShowDelay()
	{
		if (!m_iconSlot) return;

		const auto face = TimerIcon(m_delay);
		m_iconSlot.Children().Clear();
		m_iconSlot.Children().Append(face.Host);
		m_digit = face.Digit;

		// The icon is the only place the delay is shown, and a screen reader cannot read an icon.
		if (m_button)
		{
			xaml::Automation::AutomationProperties::SetName(m_button, winrt::hstring{ L"Delay before select: " + Describe(m_delay) });
		}
	}

	// The sweep: the remaining time as a wedge that starts whole and shrinks counter-clockwise from 12
	// o'clock to nothing.
	//
	// Two half-discs, each clipped to its own half of the chip and rotated, rather than one wedge whose
	// geometry is rewritten. Rotation is a render transform, which XAML animates off the UI thread, so
	// the sweep costs the app nothing for as long as it runs; animating an arc's geometry would be a
	// dependent animation, and would take a frame's work from somebody else's UI thread sixty times a
	// second. Which half turns when is on BuildCountdown.
	xcontrols::Grid BuildSweep()
	{
		auto sweep = xcontrols::Grid();
		sweep.Width(ChipSize);
		sweep.Height(ChipSize);
		sweep.IsHitTestVisible(false);
		sweep.Visibility(xaml::Visibility::Collapsed);

		m_rightTurn = xmedia::RotateTransform();
		m_leftTurn = xmedia::RotateTransform();
		sweep.Children().Append(HalfDisc(true, m_rightTurn));
		sweep.Children().Append(HalfDisc(false, m_leftTurn));
		return sweep;
	}

	static xcontrols::Grid HalfDisc(bool right, xmedia::RotateTransform const& turn)
	{
		constexpr double centre = ChipSize / 2.0;
		constexpr double radius = centre - 1.0;

		const winrt::Windows::Foundation::Point top{ static_cast<float>(centre), static_cast<float>(centre - radius) };
		const winrt::Windows::Foundation::Point bottom{ static_cast<float>(centre), static_cast<float>(centre + radius) };

		xmedia::ArcSegment arc;
		arc.Point(right ? bottom : top);
		arc.Size(winrt::Windows::Foundation::Size{ static_cast<float>(radius), static_cast<float>(radius) });
		arc.SweepDirection(xmedia::SweepDirection::Clockwise);

		xmedia::PathFigure figure;
		figure.StartPoint(right ? top : bottom);
		figure.IsClosed(true);
		figure.IsFilled(true);
		figure.Segments().Append(arc);

		xmedia::PathGeometry geometry;
		geometry.Figures().Append(figure);

		turn.CenterX(centre);
		turn.CenterY(centre);

		auto disc = xshapes::Path();
		disc.Data(geometry);
		disc.Fill(Brush(0xC0, 0xC2, 0x18, 0x5B));
		disc.Width(ChipSize);
		disc.Height(ChipSize);
		disc.RenderTransform(turn);

		xmedia::RectangleGeometry clip;
		clip.Rect(winrt::Windows::Foundation::Rect{
			static_cast<float>(right ? centre : 0.0), 0.0f, static_cast<float>(centre), static_cast<float>(ChipSize) });

		auto half = xcontrols::Grid();
		half.Clip(clip);
		half.Children().Append(disc);
		return half;
	}

	// One storyboard for everything the countdown shows, the same length as the timer.
	//
	// The remaining wedge runs clockwise from 12 o'clock through an angle that falls from 360 to 0.
	// While it is over 180 the right half is whole and the left half shrinks, by turning the left
	// half-disc counter-clockwise out of its clip; after that the left half stays gone and the right
	// half-disc turns out of its own clip the same way. Negative angles are counter-clockwise.
	//
	// The digit is a keyframe a second rather than a once-a-second callback, so it is on the same
	// clock as the sweep. Stopping the board puts it back to the delay, which is the resting icon.
	xanim::Storyboard BuildCountdown(int seconds)
	{
		using winrt::Windows::Foundation::TimeSpan;
		const TimeSpan whole{ std::chrono::seconds(seconds) };
		const TimeSpan half{ whole.count() / 2 };

		auto board = xanim::Storyboard();

		xanim::DoubleAnimationUsingKeyFrames left;
		left.KeyFrames().Append(Linear(0.0, TimeSpan{ 0 }));
		left.KeyFrames().Append(Linear(-180.0, half));
		xanim::Storyboard::SetTarget(left, m_leftTurn);
		xanim::Storyboard::SetTargetProperty(left, L"Angle");
		board.Children().Append(left);

		xanim::DoubleAnimationUsingKeyFrames right;
		right.KeyFrames().Append(Linear(0.0, TimeSpan{ 0 }));
		right.KeyFrames().Append(Linear(0.0, half));
		right.KeyFrames().Append(Linear(-180.0, whole));
		xanim::Storyboard::SetTarget(right, m_rightTurn);
		xanim::Storyboard::SetTargetProperty(right, L"Angle");
		board.Children().Append(right);

		if (m_digit)
		{
			xanim::ObjectAnimationUsingKeyFrames digit;
			for (int elapsed = 0; elapsed < seconds; elapsed++)
			{
				xanim::DiscreteObjectKeyFrame frame;
				frame.KeyTime(xanim::KeyTime{ TimeSpan{ std::chrono::seconds(elapsed) } });
				frame.Value(winrt::box_value(winrt::hstring{ std::to_wstring(seconds - elapsed) }));
				digit.KeyFrames().Append(frame);
			}

			xanim::Storyboard::SetTarget(digit, m_digit);
			xanim::Storyboard::SetTargetProperty(digit, L"Text");
			board.Children().Append(digit);
		}

		return board;
	}

	static xanim::LinearDoubleKeyFrame Linear(double value, winrt::Windows::Foundation::TimeSpan at)
	{
		xanim::LinearDoubleKeyFrame frame;
		frame.Value(value);
		frame.KeyTime(xanim::KeyTime{ at });
		return frame;
	}

	// Every way out of a countdown, so none of them can leave the timer or the board running.
	//
	// m_expired comes last, after m_running is cleared and the timer and board are stopped: what it
	// does is arm select, and arming cancels any countdown. Called any earlier, that cancel would find
	// this one still running and finish it a second time from inside the first.
	void Finish(bool expired)
	{
		const bool wasRunning = m_running;
		m_running = false;

		if (m_timer) m_timer.Stop();

		if (m_board)
		{
			m_board.Stop();
			m_board = nullptr;
		}

		if (m_sweep) m_sweep.Visibility(xaml::Visibility::Collapsed);

		RefreshChrome();
		if (wasRunning) Notify();

		if (expired && wasRunning)
		{
			Log(L"overlay: countdown ended");
			m_expired();
		}
	}

	void OnChip()
	{
		// The chip is the way out of its own countdown, as the Select chip is.
		if (m_running)
		{
			Cancel();
			return;
		}

		if (m_menuOpen) CloseMenu();
		else OpenMenu();
	}

	// The menu is a Border on the overlay's own canvas, not a Flyout or a Popup: a popup opened from
	// the tap fail-fasts (docs/invariants/overlay.md). Under it, while it is open, is a transparent
	// layer over the whole window -- toolbar included -- that takes one press and closes the menu, so
	// a click anywhere else dismisses it the way a flyout's would. That press cannot dismiss anything
	// of the app's: the order is delay first, then Select, then the app's own UI, so the two are never
	// open together.
	void OpenMenu()
	{
		try
		{
			if (!m_menu) BuildMenu();

			const auto extent = m_surface.Extent();
			m_shield.Width(extent.Width);
			m_shield.Height(extent.Height);

			m_shield.Visibility(xaml::Visibility::Visible);
			m_menu.Visibility(xaml::Visibility::Visible);
			PlaceMenu();

			m_menuOpen = true;
			RefreshChrome();
			Notify();

			// Focus on the current choice, so the menu can be worked from the keyboard that opened it:
			// arrows between the choices, Enter to take one, Escape to leave.
			for (auto const& row : m_rows)
			{
				if (row.Seconds == m_delay) row.Button.Focus(xaml::FocusState::Programmatic);
			}
		}
		catch (winrt::hresult_error const& error)
		{
			Log(std::wstring(L"overlay: delay menu could not open: ") + error.message().c_str());
			CloseMenu();
		}
	}

	void CloseMenu()
	{
		if (m_shield) m_shield.Visibility(xaml::Visibility::Collapsed);
		if (m_menu) m_menu.Visibility(xaml::Visibility::Collapsed);

		if (!m_menuOpen) return;

		m_menuOpen = false;
		Notify();

		// Focus was in the menu, and a collapsed element cannot keep it; back to the chip that opened
		// it rather than wherever XAML falls back to. Refused harmlessly when the toolbar is folded.
		if (m_button) m_button.Focus(xaml::FocusState::Programmatic);
	}

	// Built on first open rather than with the chip. Adorning appends to the canvas, and the shield
	// has to land above the panel to catch a click on the toolbar; at the time the chip is built, the
	// panel is not on the canvas yet.
	void BuildMenu()
	{
		m_shield = xcontrols::Grid();
		m_shield.Background(Brush(0x00, 0x00, 0x00, 0x00)); // Transparent, not null: null takes no input.
		m_shield.Visibility(xaml::Visibility::Collapsed);
		m_shield.PointerPressed(
			[this](winrt::Windows::Foundation::IInspectable const&, xinput::PointerRoutedEventArgs const& e)
			{
				e.Handled(true);
				CloseMenu();
			});

		auto rows = xcontrols::StackPanel();
		rows.Orientation(xcontrols::Orientation::Vertical);
		rows.XYFocusKeyboardNavigation(xinput::XYFocusKeyboardNavigationMode::Enabled);
		for (const int seconds : Choices)
		{
			rows.Children().Append(MenuRow(seconds));
		}

		m_menu = xcontrols::Border();
		m_menu.Background(Brush(0xF0, 0x1C, 0x1C, 0x22));
		m_menu.BorderBrush(Brush(0xFF, 0x53, 0x53, 0x63));
		m_menu.BorderThickness(xaml::Thickness{ 1, 1, 1, 1 });
		m_menu.CornerRadius(xaml::CornerRadius{ 3, 3, 3, 3 });
		m_menu.Padding(xaml::Thickness{ 0, 4, 0, 4 });
		m_menu.Width(MenuWidth);
		m_menu.Visibility(xaml::Visibility::Collapsed);
		m_menu.Child(rows);

		// Escape leaves the menu as it would a flyout's; the shield is the same for a click.
		m_menu.KeyDown(
			[this](winrt::Windows::Foundation::IInspectable const&, xinput::KeyRoutedEventArgs const& e)
			{
				if (e.Key() != winrt::Windows::System::VirtualKey::Escape) return;

				e.Handled(true);
				CloseMenu();
			});

		m_surface.Adorn(m_shield);
		m_surface.Adorn(m_menu);
		RefreshChrome();
	}

	// A Button rather than a Grid taking taps, so a choice takes focus, answers Enter and Space, and is
	// named to a screen reader: the chip that opens the menu is a Button, and a menu reachable from the
	// keyboard has to be workable from it. Painted the way Chip paints, for the same reason -- the
	// template's own state brushes are the system's palette, not this toolbar's.
	xaml::UIElement MenuRow(int seconds)
	{
		auto face = xcontrols::Grid();

		auto glow = xcontrols::Border();
		glow.Background(Brush(0xFF, 0xFF, 0xFF, 0xFF));
		glow.Opacity(0.0);
		glow.IsHitTestVisible(false);
		face.Children().Append(glow);

		// The mark on the current choice, as in the Snipping Tool's own menu.
		auto bar = xshapes::Rectangle();
		bar.Width(3);
		bar.Height(14);
		bar.RadiusX(1.5);
		bar.RadiusY(1.5);
		bar.Fill(Accent());
		bar.HorizontalAlignment(xaml::HorizontalAlignment::Left);
		bar.VerticalAlignment(xaml::VerticalAlignment::Center);
		bar.Margin(xaml::Thickness{ 4, 0, 0, 0 });
		face.Children().Append(bar);

		auto content = xcontrols::StackPanel();
		content.Orientation(xcontrols::Orientation::Horizontal);
		content.Spacing(10);
		content.Margin(xaml::Thickness{ 16, 0, 12, 0 });
		content.VerticalAlignment(xaml::VerticalAlignment::Center);
		content.Children().Append(TimerIcon(seconds).Host);

		const std::wstring text = Describe(seconds);
		auto label = Label(text.c_str(), 12.0, 0xE4, nullptr);
		label.HorizontalAlignment(xaml::HorizontalAlignment::Left);
		content.Children().Append(label);
		face.Children().Append(content);

		auto row = xcontrols::Button();
		row.Content(face);
		Neutralise(row);
		row.Padding(xaml::Thickness{ 0, 0, 0, 0 });
		row.BorderThickness(xaml::Thickness{ 0, 0, 0, 0 });
		row.CornerRadius(xaml::CornerRadius{ 0, 0, 0, 0 });
		row.MinWidth(0);
		row.MinHeight(0);
		row.Height(RowHeight);
		row.HorizontalAlignment(xaml::HorizontalAlignment::Stretch);
		row.HorizontalContentAlignment(xaml::HorizontalAlignment::Stretch);
		row.VerticalContentAlignment(xaml::VerticalAlignment::Stretch);
		xaml::Automation::AutomationProperties::SetName(row, winrt::hstring{ text });

		row.PointerEntered([glow](auto const&, auto const&) { glow.Opacity(HoverWash); });
		row.PointerExited([glow](auto const&, auto const&) { glow.Opacity(0.0); });
		row.PointerPressed([glow](auto const&, auto const&) { glow.Opacity(PressWash); });
		row.PointerReleased([glow](auto const&, auto const&) { glow.Opacity(HoverWash); });
		row.PointerCaptureLost([glow](auto const&, auto const&) { glow.Opacity(0.0); });
		row.Click(
			[this, seconds](winrt::Windows::Foundation::IInspectable const&, xaml::RoutedEventArgs const&) { Choose(seconds); });

		m_rows.push_back(MenuChoice{ seconds, row, bar });
		return row;
	}

	void Choose(int seconds)
	{
		m_delay = seconds;
		ShowDelay();
		CloseMenu();
		RefreshChrome();
		Log(L"overlay: select delay " + (seconds > 0 ? std::to_wstring(seconds) + L"s" : std::wstring(L"off")));
	}

	// Under the chip, kept inside the window, and above it when there is no room below -- a toolbar
	// dragged to the bottom edge would otherwise open its menu off screen.
	void PlaceMenu()
	{
		const auto canvas = xmedia::VisualTreeHelper::GetParent(m_menu).try_as<xaml::UIElement>();
		if (!canvas || !m_button) return;

		const auto chip = m_button.TransformToVisual(canvas).TransformPoint(winrt::Windows::Foundation::Point{ 0, 0 });
		const auto extent = m_surface.Extent();
		const double height = RowHeight * std::size(Choices) + 10.0;

		double top = chip.Y + ChipSize + MenuGap;
		if (top + height > extent.Height) top = chip.Y - MenuGap - height;

		xcontrols::Canvas::SetLeft(m_menu, Clamp(chip.X, 0.0, extent.Width - MenuWidth));
		xcontrols::Canvas::SetTop(m_menu, Clamp(top, 0.0, extent.Height - height));
		xcontrols::Canvas::SetLeft(m_shield, 0.0);
		xcontrols::Canvas::SetTop(m_shield, 0.0);
	}

	void Notify()
	{
		if (m_busy) m_busy(m_menuOpen || m_running);
	}

	IRoseOverlaySurface& m_surface;
	std::function<void()> m_expired;
	std::function<void(bool)> m_busy;

	int m_delay = 0;
	bool m_running = false;
	bool m_menuOpen = false;

	xcontrols::Button m_button{ nullptr };
	xcontrols::Grid m_iconSlot{ nullptr };
	xcontrols::TextBlock m_digit{ nullptr };
	xcontrols::Grid m_sweep{ nullptr };
	xmedia::RotateTransform m_leftTurn{ nullptr };
	xmedia::RotateTransform m_rightTurn{ nullptr };

	xaml::DispatcherTimer m_timer{ nullptr };
	xanim::Storyboard m_board{ nullptr };

	xcontrols::Grid m_shield{ nullptr };
	xcontrols::Border m_menu{ nullptr };
	std::vector<MenuChoice> m_rows;
};
