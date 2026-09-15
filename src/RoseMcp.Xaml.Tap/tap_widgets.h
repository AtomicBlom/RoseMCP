#pragma once

// The overlay's visual language: the accent it is drawn in, the shape of a chip, and the factories
// that build one.
//
// Deliberately not self-contained, like tap_overlay.h and for the same reason -- every signature
// here names a projected type, so it is included after the provider's aliases.
//
// A base class rather than a namespace, and the reason is not brevity at the call site. Everything
// drawn on this toolbar has to be drawn the same way: one accent, one chip size, one hover wash. A
// tool that builds part of the toolbar inherits the language rather than being handed it, so there
// is no way to draw a chip that is nearly right. It holds no state, so inheriting it costs nothing
// and two inheritors do not interact.
//
// Everything here is static and touches no member. That is the test of whether something belongs:
// a helper that needs the overlay's tree -- Outline and Badge, which attach themselves to the marks
// layer -- is not part of the language, it is part of the overlay.

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
static const wchar_t* const IconRulers = L"\xECC6";   // Ruler -- a ruler, for the measuring mode

class RoseWidgets
{
protected:
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

	// One readout, in its own rounded pill. Three values on one line run together as prose, and these
	// are three separate facts about the anchor rather than a sentence about it -- so each is given an
	// edge, the way the chips above them have one.
	static xcontrols::Border Pill(xcontrols::TextBlock const& text)
	{
		auto pill = xcontrols::Border();
		pill.Background(Idle());
		pill.CornerRadius(xaml::CornerRadius{ 9, 9, 9, 9 });
		pill.Padding(xaml::Thickness{ 8, 1, 8, 2 });
		pill.VerticalAlignment(xaml::VerticalAlignment::Center);
		pill.Child(text);
		return pill;
	}

	static xcontrols::TextBlock Label(const wchar_t* text, double size, uint8_t grey, const wchar_t* fontFamily)
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

	static xcontrols::TextBlock Glyph(const wchar_t* glyph, double size)
	{
		return Label(glyph, size, 0xDC, L"Segoe MDL2 Assets");
	}

	// A pointer inside a marquee. MDL2 has no single glyph for it -- the nearest, SelectAll, is a
	// dense grid that turns to mush at button size -- so it is composed: a dashed rectangle with the
	// same pointer the Idle button uses, smaller and offset, sitting in it.
	static xaml::UIElement SelectIcon()
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
	static xcontrols::Button Chip(xaml::UIElement const& content, const wchar_t* tip, std::function<void()> action)
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

	static void Fill(xcontrols::TextBlock const& text, xcontrols::Border const& pill, std::wstring const& value)
	{
		if (text) text.Text(value);
		if (pill) pill.Visibility(value.empty() ? xaml::Visibility::Collapsed : xaml::Visibility::Visible);
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
	// Built and handed back rather than attached: what layer a mark belongs on is the caller's
	// business, and a factory that appends itself cannot be used by a second one.
	static xcontrols::Grid Outline(double thickness, bool dashed, uint8_t strokeAlpha = 0xFF, uint8_t fillAlpha = 0x00)
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

		return box;
	}

	static xcontrols::Border Badge()
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
		return badge;
	}

	// Long enough to read as a fade rather than a flicker, short enough not to lag the pointer.
	static constexpr int FadeMilliseconds = 160;

	// The badge sits this far above the element it captions. Shared with the proximity test, which
	// has to treat the caption as part of the selection.
	static constexpr double BadgeHeight = 18.0;
	static constexpr double BadgeGap = 2.0;

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

	static bool Contains(winrt::Windows::Foundation::Rect const& rect, winrt::Windows::Foundation::Point const& point)
	{
		return rect.Width > 0 && rect.Height > 0
			&& point.X >= rect.X && point.X < rect.X + rect.Width
			&& point.Y >= rect.Y && point.Y < rect.Y + rect.Height;
	}
};
