#pragma once

// The rulers: the gaps between the picked element and whatever the pointer is over, the bands of its
// box model, and the readout that spells all three out.
//
// Included after the provider's aliases, like everything else that builds XAML. The geometry it
// draws comes from tap_measure.h, which names nothing external at all -- what a measurement *means*
// is worth being able to read apart from the code that draws one, because "the gap" has no single
// answer once two rectangles overlap.
//
// It reads the pick and never writes it. That is the whole reason RosePick exists as an object: this
// mode measures *from* the selected element, so it needs the element, its rectangle and the two
// captions a number must not cover -- and it needs none of them to be its own.
//
// The mode itself stays with the overlay. Arming a pointer mode is arbitration between modes, and
// there is one capture layer to arbitrate over, so this is told whether it is the current mode
// rather than asking.

// The geometry itself is self-contained and framework-free, so it comes from here rather than from
// each provider.
#include "tap_measure.h"

class RoseRulersTool final : protected RoseWidgets
{
public:
	RoseRulersTool(IRoseOverlaySurface& surface, RosePick& pick) : m_surface(surface), m_pick(pick) {}

	// The box model's bands, which go on *under* the pick's outlines: an outline is the edge of the
	// element and a band is the space around it, so a translucent fill drawn over the outline would
	// wash out the one line that says exactly where the element ends.
	//
	// Warm outside and cool inside, which is the convention every browser's element inspector uses --
	// worth following rather than restating in the brand's own colours, since three shades of one hue
	// would be three bands nobody can tell apart.
	void BuildBands()
	{
		m_marginBand = BuildBand(0x4C, 0xF9, 0xA8, 0x5C);  // orange, outside the element
		m_borderBand = BuildBand(0x4C, 0xF2, 0xD0, 0x7A);  // yellow, the border itself
		m_paddingBand = BuildBand(0x4C, 0x9B, 0xC8, 0x7A); // green, inside it
	}

	// Four dimension lines, which is as many as a measurement can want: one per axis where the
	// rectangles miss each other, two where they overlap. Built once and re-aimed, like everything
	// else on this canvas, so a hover costs property sets rather than a tree of new elements.
	//
	// After the pick's marks rather than before, so a leader draws over an outline instead of under
	// it: the number is what somebody is reading at that moment.
	void BuildLeaders()
	{
		for (int leader = 0; leader < 4; leader++)
		{
			m_leaders.push_back(BuildLeader());
		}
	}

	// The anchor's size, margin and padding as text. Hidden until the mode is on, for the same reason
	// the zoom row is: a row that is always there makes every app carry a taller toolbar for a mode
	// almost nobody is in.
	//
	// The numbers live here and not on the bands, because this is where the two facts a band cannot
	// draw can be said. A side of zero is a band of no width, indistinguishable from a side that was
	// never drawn; a type with no Padding property at all is indistinguishable from one whose padding
	// happens to be nothing. Both are ordinary and both matter to somebody asking where four pixels
	// came from.
	xaml::UIElement BuildRow()
	{
		m_rulersRow = xcontrols::StackPanel();
		m_rulersRow.Orientation(xcontrols::Orientation::Horizontal);
		m_rulersRow.Spacing(6);
		m_rulersRow.Padding(xaml::Thickness{ 6, 0, 4, 3 });
		m_rulersRow.Visibility(xaml::Visibility::Collapsed);

		m_sizeLabel = Label(L"click an element to anchor", 11.0, 0xC8, nullptr);
		m_marginLabel = Label(L"", 11.0, 0xE8, nullptr);
		m_paddingLabel = Label(L"", 11.0, 0xE8, nullptr);

		m_sizePill = Pill(m_sizeLabel);
		m_marginPill = Pill(m_marginLabel);
		m_paddingPill = Pill(m_paddingLabel);

		m_rulersRow.Children().Append(m_sizePill);
		m_rulersRow.Children().Append(m_marginPill);
		m_rulersRow.Children().Append(m_paddingPill);
		return m_rulersRow;
	}

	/// <summary>
	/// Draws the anchor's box model and fills the toolbar's rulers row, or takes both away.
	/// </summary>
	/// <remarks>
	/// Called wherever the mode, the anchor or the anchor's geometry changes, so one place decides what
	/// the mode is showing rather than one per event that could each get it wrong.
	/// </remarks>
	void Refresh(bool armed)
	{
		const bool rulers = armed;
		if (m_rulersRow) m_rulersRow.Visibility(rulers ? xaml::Visibility::Visible : xaml::Visibility::Collapsed);
		if (!rulers)
		{
			HideBands();
			HideLeaders();
			return;
		}

		winrt::Windows::Foundation::Rect rect{};
		if (!m_pick.Has() || !m_pick.Element() || !m_surface.Bounds(m_pick.Element(), rect))
		{
			HideBands();
			SetRulersText(std::wstring(), std::wstring(), std::wstring());
			return;
		}

		const auto margin = ToEdges(m_pick.Element().Margin());
		const auto border = BorderOf(m_pick.Element());

		xaml::Thickness padding{};
		const bool hasPadding = PaddingOf(m_pick.Element(), padding);

		const auto bands = BoxModel(ToRuler(rect), margin, border, hasPadding ? ToEdges(padding) : RulerEdges{});
		PlaceBand(m_marginBand, bands.Margin, bands.Border);
		PlaceBand(m_borderBand, bands.Border, bands.Padding);
		PlaceBand(m_paddingBand, bands.Padding, bands.Content);

		// The numbers go in the row rather than on the bands. Eight labels around a control the size of
		// a button is not a reading of its box model, it is something drawn over the app -- and the row
		// is the only place that can say the two things a band cannot: which sides are zero, and that a
		// type has no padding to speak of.
		SetRulersText(
			Number(rect.Width) + L"\x00D7" + Number(rect.Height),
			L"Margin " + Thick(margin),
			hasPadding ? L"Padding " + Thick(ToEdges(padding)) : L"Padding none");
	}

	void SetRulersText(std::wstring const& size, std::wstring const& margin, std::wstring const& padding)
	{
		// An empty pill is a small blank lozenge that says nothing, so a value with nothing to report
		// takes its pill with it. The size pill stays, because with nothing anchored it is the one
		// that says what to do about that.
		Fill(m_sizeLabel, m_sizePill, size.empty() ? L"click an element to anchor" : size);
		Fill(m_marginLabel, m_marginPill, margin);
		Fill(m_paddingLabel, m_paddingPill, padding);
	}

	// Measures whatever is under the pointer against the anchor, or takes the leaders away when there
	// is nothing to measure.

	// Measures whatever is under the pointer against the anchor, or takes the leaders away when there
	// is nothing to measure.
	// Takes the dimension lines away, for the moments when there is nothing to measure: leaving the
	// mode, the pointer leaving the window, and the pointer sitting on the anchor itself. The bands
	// stay, because they describe the anchor rather than a gap to anything.
	void ClearMeasurement() { HideLeaders(); }

	void MeasureTo(xaml::UIElement const& element, winrt::Windows::Foundation::Rect const& rect)
	{
		if (!element || !m_pick.Has() || !m_pick.Element())
		{
			HideLeaders();
			return;
		}

		// The anchor against itself is four zeroes drawn over the bands that already say what those
		// zeroes mean, so it is not drawn at all.
		if (element == m_pick.Element().try_as<xaml::UIElement>())
		{
			HideLeaders();
			return;
		}

		DrawMeasurement(Measure(ToRuler(m_pick.AnchorRect()), ToRuler(rect)));
	}

	// One measurement, as up to four dimension lines.
	//
	// An axis whose spans overlap draws two, one in from each of the anchor's edges, so a nested
	// element carries its inset on all four sides -- which is the padding or the margin, and the
	// answer people come to this for. An axis whose spans miss each other draws one, the separation.

private:
	// How far a measurement's number sits off its own line, and how far a label is pushed each time it
	// lands on one already placed. The same number does both, so a label that has been moved sits a
	// clear row away from its neighbour rather than at some second spacing nothing else uses.
	static constexpr double LabelGap = 3.0;

	// How many times a label may be pushed before it is drawn where it is. Four dimension lines can
	// need three moves between them; a cap past that is only ever reached by a label being pushed off
	// the window, which is worse than a crowded one.
	static constexpr int LabelAttempts = 6;

	// How much line has to stay showing either side of a number sitting on it. Below this the number
	// goes outside the line's end instead, because what it would otherwise cover are the ends -- and
	// the ends are where the extension lines turn off to the two edges being measured.
	static constexpr double EndRoom = 6.0;

	// The room a label is measured in. Any number larger than the window does, since a Border round a
	// two-digit number takes what it needs and no more.
	static constexpr float LabelRoom = 4096.0f;

	// One dimension line: a stroke with a tick at each end, the dark companion that keeps it readable
	// over an app of any colour, a dashed extension for each rectangle the line does not reach, and
	// the number.
	struct Leader
	{
		xshapes::Line Contrast{ nullptr };
		xshapes::Line Line{ nullptr };
		xshapes::Line CapFrom{ nullptr };
		xshapes::Line CapTo{ nullptr };
		xshapes::Line ExtendFrom{ nullptr };
		xshapes::Line ExtendTo{ nullptr };
		xcontrols::Border Label{ nullptr };

		// The thin line from a number to the line it belongs to, drawn only when the number had to be
		// moved off it. A label centred on its own dimension line needs nothing to explain it.
		xshapes::Line Tie{ nullptr };
	};

	// One band of the box model, as four strips rather than one stroked rectangle. A Thickness is four
	// numbers and a stroke is one, so a padding of 24,4,24,4 cannot be drawn as a stroked outline at
	// all -- and that asymmetric case is precisely the one somebody is inspecting.
	struct Band
	{
		xshapes::Rectangle Top{ nullptr };
		xshapes::Rectangle Bottom{ nullptr };
		xshapes::Rectangle Left{ nullptr };
		xshapes::Rectangle Right{ nullptr };
	};

	static RulerRect ToRuler(winrt::Windows::Foundation::Rect const& rect)
	{
		return RulerRect{ rect.X, rect.Y, rect.Width, rect.Height };
	}

	static RulerEdges ToEdges(xaml::Thickness const& thickness)
	{
		return RulerEdges{ thickness.Left, thickness.Top, thickness.Right, thickness.Bottom };
	}

	// A measurement as it is written down. A whole number keeps no decimal and anything else keeps
	// one: rounding 12.5 to 12 would be a confident wrong answer about the exact discrepancy somebody
	// is hunting, which is the only reason they are looking at this at all.
	static std::wstring Number(double value)
	{
		const double rounded = std::round(value * 10.0) / 10.0;
		wchar_t text[32] = {};
		swprintf_s(text, rounded == std::floor(rounded) ? L"%.0f" : L"%.1f", rounded);
		return text;
	}

	// A thickness as all four sides, always, in the order XAML writes them.
	//
	// XAML's own shorthands are not used here even though the value came from one. "Padding 24" is
	// shorter and asks the reader to know that a single number means all four sides -- and a person
	// looking at this row is looking at it because they cannot account for a few pixels, which is
	// exactly when guessing whether a number covers one side or four is not good enough.
	static std::wstring Thick(RulerEdges const& edges)
	{
		return Number(edges.Left) + L"," + Number(edges.Top) + L"," + Number(edges.Right) + L"," + Number(edges.Bottom);
	}

	/// <summary>Reads an element's padding, and says whether its type has one at all.</summary>
	/// <remarks>
	/// XAML declares Padding on no common base -- Control, Border, TextBlock, RichTextBlock,
	/// ContentPresenter and the panels each declare their own -- so it is asked of those projections in
	/// turn. A type with none is reported as having none rather than as having zero: a Rectangle does
	/// not have a padding of nothing, it has no padding, and a band of zero width would draw the two
	/// cases identically.
	/// </remarks>
	static bool PaddingOf(xaml::UIElement const& element, xaml::Thickness& padding)
	{
		if (const auto control = element.try_as<xcontrols::Control>()) { padding = control.Padding(); return true; }
		if (const auto border = element.try_as<xcontrols::Border>()) { padding = border.Padding(); return true; }
		if (const auto text = element.try_as<xcontrols::TextBlock>()) { padding = text.Padding(); return true; }
		if (const auto rich = element.try_as<xcontrols::RichTextBlock>()) { padding = rich.Padding(); return true; }
		if (const auto presenter = element.try_as<xcontrols::ContentPresenter>()) { padding = presenter.Padding(); return true; }
		if (const auto stack = element.try_as<xcontrols::StackPanel>()) { padding = stack.Padding(); return true; }
		if (const auto grid = element.try_as<xcontrols::Grid>()) { padding = grid.Padding(); return true; }
		if (const auto relative = element.try_as<xcontrols::RelativePanel>()) { padding = relative.Padding(); return true; }

		return false;
	}

	// The border thickness, on the same terms. It matters because the element's own rectangle takes it
	// in: without it the padding band would start at the outside of the border and be wrong by exactly
	// the border on every element that has one.
	static RulerEdges BorderOf(xaml::UIElement const& element)
	{
		if (const auto control = element.try_as<xcontrols::Control>()) return ToEdges(control.BorderThickness());
		if (const auto border = element.try_as<xcontrols::Border>()) return ToEdges(border.BorderThickness());
		if (const auto presenter = element.try_as<xcontrols::ContentPresenter>()) return ToEdges(presenter.BorderThickness());
		if (const auto stack = element.try_as<xcontrols::StackPanel>()) return ToEdges(stack.BorderThickness());
		if (const auto grid = element.try_as<xcontrols::Grid>()) return ToEdges(grid.BorderThickness());
		if (const auto relative = element.try_as<xcontrols::RelativePanel>()) return ToEdges(relative.BorderThickness());

		return RulerEdges{};
	}

	xshapes::Line RulerLine(uint8_t alpha, uint8_t red, uint8_t green, uint8_t blue, double thickness, bool dashed)
	{
		auto line = xshapes::Line();
		line.Stroke(Brush(alpha, red, green, blue));
		line.StrokeThickness(thickness);
		line.IsHitTestVisible(false);
		line.Visibility(xaml::Visibility::Collapsed);

		if (dashed)
		{
			line.StrokeDashArray().Append(2);
			line.StrokeDashArray().Append(2);
		}

		m_surface.Mark(line);
		return line;
	}

	Leader BuildLeader()
	{
		Leader leader;

		// The dark companion goes down first and wider, so it shows as a fringe either side of the line
		// it backs. Same reason the outlines have one: a rose line on a rose-coloured app is no line at
		// all, and RoseMCP pointed at something built with RoseMCP's own palette is not a hypothetical.
		leader.Contrast = RulerLine(0xB0, 0x10, 0x10, 0x14, 3.0, false);
		leader.Line = RulerLine(0xFF, 0xC2, 0x18, 0x5B, 1.0, false);
		leader.CapFrom = RulerLine(0xFF, 0xC2, 0x18, 0x5B, 1.0, false);
		leader.CapTo = RulerLine(0xFF, 0xC2, 0x18, 0x5B, 1.0, false);
		leader.ExtendFrom = RulerLine(0x80, 0xC2, 0x18, 0x5B, 1.0, true);
		leader.ExtendTo = RulerLine(0x80, 0xC2, 0x18, 0x5B, 1.0, true);

		// Built before the label so it passes behind it rather than across it.
		leader.Tie = RulerLine(0xC0, 0xC2, 0x18, 0x5B, 1.0, false);
		leader.Label = Badge();
		m_surface.Mark(leader.Label);
		return leader;
	}

	xshapes::Rectangle BandStrip(uint8_t alpha, uint8_t red, uint8_t green, uint8_t blue)
	{
		auto strip = xshapes::Rectangle();
		strip.Fill(Brush(alpha, red, green, blue));
		strip.IsHitTestVisible(false);
		strip.Visibility(xaml::Visibility::Collapsed);
		m_surface.Mark(strip);
		return strip;
	}

	Band BuildBand(uint8_t alpha, uint8_t red, uint8_t green, uint8_t blue)
	{
		Band band;
		band.Top = BandStrip(alpha, red, green, blue);
		band.Bottom = BandStrip(alpha, red, green, blue);
		band.Left = BandStrip(alpha, red, green, blue);
		band.Right = BandStrip(alpha, red, green, blue);
		return band;
	}

	static void Conceal(xaml::UIElement const& element)
	{
		if (element) element.Visibility(xaml::Visibility::Collapsed);
	}

	static void HideLeader(Leader const& leader)
	{
		Conceal(leader.Contrast);
		Conceal(leader.Line);
		Conceal(leader.CapFrom);
		Conceal(leader.CapTo);
		Conceal(leader.ExtendFrom);
		Conceal(leader.ExtendTo);
		Conceal(leader.Tie);
		Conceal(leader.Label);
	}

	void HideLeaders()
	{
		for (auto const& leader : m_leaders) HideLeader(leader);
	}

	static void HideBand(Band const& band)
	{
		Conceal(band.Top);
		Conceal(band.Bottom);
		Conceal(band.Left);
		Conceal(band.Right);
	}

	void HideBands()
	{
		HideBand(m_marginBand);
		HideBand(m_borderBand);
		HideBand(m_paddingBand);
	}

	// One strip of a band, or nothing where that side has no thickness. Clamped at zero rather than
	// trusted: an element constrained below the size it asked for can have a padding wider than it is,
	// and a negative extent draws a rectangle hanging off the opposite side.
	static void PlaceStrip(xshapes::Rectangle const& strip, double left, double top, double width, double height)
	{
		if (!strip) return;

		if (width <= 0.0 || height <= 0.0)
		{
			strip.Visibility(xaml::Visibility::Collapsed);
			return;
		}

		strip.Width(width);
		strip.Height(height);
		xcontrols::Canvas::SetLeft(strip, left);
		xcontrols::Canvas::SetTop(strip, top);
		strip.Visibility(xaml::Visibility::Visible);
	}

	// The frame between two rectangles: the top and bottom strips run the full width and the sides
	// fill what is left between them, so no two strips meet at a corner. Overlapping ones would double
	// the fill's opacity there and draw a darker square at each corner of every band.
	static void PlaceBand(Band const& band, RulerRect const& outer, RulerRect const& inner)
	{
		PlaceStrip(band.Top, outer.Left, outer.Top, outer.Width, inner.Top - outer.Top);
		PlaceStrip(band.Bottom, outer.Left, inner.Bottom(), outer.Width, outer.Bottom() - inner.Bottom());
		PlaceStrip(band.Left, outer.Left, inner.Top, inner.Left - outer.Left, inner.Height);
		PlaceStrip(band.Right, inner.Right(), inner.Top, outer.Right() - inner.Right(), inner.Height);
	}


	void DrawMeasurement(RulerMeasurement const& measurement)
	{
		HideLeaders();

		// Collisions are decided within one measurement, so the record of what is on screen starts
		// empty each time. Keeping it across measurements would have a label avoiding a number that
		// was taken off the screen by the same call.
		m_placedLabels.clear();

		// The two element captions are on the canvas before any number is, and they are the labels a
		// number is most likely to land on: both sit at the top-left corner of a rectangle, which is
		// exactly where the top and left insets are measured. Seeded rather than special-cased, so the
		// same push that keeps two numbers apart keeps a number off a caption.
		for (const auto& caption : m_pick.Captions()) Occupy(caption);

		const auto& anchor = measurement.Anchor;
		const auto& hovered = measurement.Hovered;

		// Where each axis's lines sit on the other one, which is inside the band the two rectangles
		// share whenever they share one.
		const double alongY = LeaderCross(anchor.Top, anchor.Bottom(), hovered.Top, hovered.Bottom());
		const double alongX = LeaderCross(anchor.Left, anchor.Right(), hovered.Left, hovered.Right());

		size_t next = 0;

		if (measurement.Horizontal.Separated)
		{
			const double from = measurement.Horizontal.Before ? anchor.Left : anchor.Right();
			const double to = measurement.Horizontal.Before ? hovered.Right() : hovered.Left;
			DrawLeader(next++, true, from, to, alongY, measurement.Horizontal.Gap, anchor, hovered);
		}
		else
		{
			DrawLeader(next++, true, anchor.Left, hovered.Left, alongY, measurement.Horizontal.Low, anchor, hovered);
			DrawLeader(next++, true, anchor.Right(), hovered.Right(), alongY, measurement.Horizontal.High, anchor, hovered);
		}

		if (measurement.Vertical.Separated)
		{
			const double from = measurement.Vertical.Before ? anchor.Top : anchor.Bottom();
			const double to = measurement.Vertical.Before ? hovered.Bottom() : hovered.Top;
			DrawLeader(next++, false, from, to, alongX, measurement.Vertical.Gap, anchor, hovered);
		}
		else
		{
			DrawLeader(next++, false, anchor.Top, hovered.Top, alongX, measurement.Vertical.Low, anchor, hovered);
			DrawLeader(next++, false, anchor.Bottom(), hovered.Bottom(), alongX, measurement.Vertical.High, anchor, hovered);
		}
	}

	/// <summary>One dimension line, from an edge of the anchor to an edge of the hovered element.</summary>
	/// <remarks>
	/// <paramref name="from"/> always belongs to the anchor and <paramref name="to"/> to the hovered
	/// element, which is what lets each extension know whose rectangle to reach back to.
	/// </remarks>
	void DrawLeader(
		size_t index,
		bool horizontal,
		double from,
		double to,
		double cross,
		double value,
		RulerRect const& anchor,
		RulerRect const& hovered)
	{
		if (index >= m_leaders.size()) return;

		auto const& leader = m_leaders[index];
		if (!leader.Line) return;

		Stroke(leader.Contrast, horizontal, from, to, cross);
		Stroke(leader.Line, horizontal, from, to, cross);

		// The ticks are what leave a line of no length still reading as a measurement: two edges flush
		// are a gap of zero, which is a real answer and one nothing else on screen would show.
		Cap(leader.CapFrom, horizontal, from, cross);
		Cap(leader.CapTo, horizontal, to, cross);

		Extend(leader.ExtendFrom, horizontal, from, cross, anchor);
		Extend(leader.ExtendTo, horizontal, to, cross, hovered);

		Caption(leader, horizontal, from, to, cross, anchor, Number(value));
	}

	static void Stroke(xshapes::Line const& line, bool horizontal, double from, double to, double cross)
	{
		if (!line) return;

		line.X1(horizontal ? from : cross);
		line.Y1(horizontal ? cross : from);
		line.X2(horizontal ? to : cross);
		line.Y2(horizontal ? cross : to);
		line.Visibility(xaml::Visibility::Visible);
	}

	static void Cap(xshapes::Line const& line, bool horizontal, double at, double cross)
	{
		if (!line) return;

		const double reach = 4.0;
		line.X1(horizontal ? at : cross - reach);
		line.Y1(horizontal ? cross - reach : at);
		line.X2(horizontal ? at : cross + reach);
		line.Y2(horizontal ? cross + reach : at);
		line.Visibility(xaml::Visibility::Visible);
	}

	// The dashed line back to a rectangle the dimension line does not touch, the way a drawing does
	// it. Drawn only where it is needed: where the line already crosses the rectangle, an extension is
	// a dashed line lying over the element for no reason.
	static void Extend(xshapes::Line const& line, bool horizontal, double at, double cross, RulerRect const& rect)
	{
		if (!line) return;

		const double low = horizontal ? rect.Top : rect.Left;
		const double high = horizontal ? rect.Bottom() : rect.Right();
		if (cross >= low && cross <= high)
		{
			line.Visibility(xaml::Visibility::Collapsed);
			return;
		}

		const double edge = cross < low ? low : high;
		Stroke(line, !horizontal, edge, cross, at);
	}

	/// <summary>The number, centred on its own line where there is room for it and moved out where
	/// there is not.</summary>
	/// <remarks>
	/// Four dimension lines round a small element put four numbers in a space that does not hold them,
	/// and they collide exactly when the measurement is most worth reading. So a label that lands on
	/// one already placed is pushed further out, perpendicular to its own line, until it is clear.
	/// <para>
	/// Pushed away from the line rather than along it, which is not arbitrary: sliding a number along
	/// its line slides it off the thing it measures, and two numbers on one axis would then appear in
	/// the order they happened to be drawn rather than the order their edges are in. Away from the
	/// line, a number stays on the side it belongs to.
	/// </para>
	/// <para>
	/// A label that has been moved gets a tie line back to the point it is labelling, because once a
	/// number is no longer sitting on its line there is nothing else to say which line it is for --
	/// which is the whole problem being fixed, arrived at from the other direction.
	/// </para>
	/// </remarks>
	void Caption(
		Leader const& leader,
		bool horizontal,
		double from,
		double to,
		double cross,
		RulerRect const& anchor,
		std::wstring const& text)
	{
		auto const& label = leader.Label;
		if (!label) return;

		if (const auto block = label.Child().try_as<xcontrols::TextBlock>()) block.Text(text);

		// Measured rather than read off ActualWidth, which is a fact about the last layout pass and is
		// zero for a label being shown for the first time. Where a number goes is decided here and now,
		// so a size from the last pass would place this frame's number by the previous frame's width --
		// and the first of a session by a width of nothing, which fits anywhere.
		label.Measure(winrt::Windows::Foundation::Size{ LabelRoom, LabelRoom });
		const auto desired = label.DesiredSize();

		const double width = desired.Width > 0.0f ? desired.Width : 28.0;
		const double height = desired.Height > 0.0f ? desired.Height : BadgeHeight;

		const double low = (std::min)(from, to);
		const double high = (std::max)(from, to);
		const double middle = (from + to) / 2.0;

		// How much of the line the number takes up, and whether the line can hold it with its ends
		// still showing.
		const double along = horizontal ? width : height;
		const bool fits = along + 2.0 * EndRoom <= high - low;

		// Which way is out of the measurement: away from the anchor's middle. That is what puts the
		// left inset's number left of everything it measures and the right inset's number right of it,
		// rather than both of them in the middle where the two would also collide.
		const double centre = horizontal ? anchor.Left + anchor.Width / 2.0 : anchor.Top + anchor.Height / 2.0;
		const bool outwardIsLow = middle < centre;

		// Along the line: centred on it where it fits, outside its far end where it does not. A gap of
		// four pixels has a line four pixels long and a number three times that wide, so a centred one
		// covers both ends -- and the ends are the kinks where the extension lines turn off to the two
		// edges being measured. Covering those hides which edges the number is about, which is the one
		// thing the number cannot say for itself.
		double lengthways;
		if (fits) lengthways = middle - along / 2.0;
		else if (outwardIsLow) lengthways = low - LabelGap - along;
		else lengthways = high + LabelGap;

		// Across it: centred, so the number reads as belonging to the line whether it sits on it or
		// just off the end of it.
		const double centred = cross - (horizontal ? height : width) / 2.0;
		double sideways = centred;

		for (int attempt = 0; ; attempt++)
		{
			const auto rect = horizontal
				? RulerRect{ lengthways, sideways, width, height }
				: RulerRect{ sideways, lengthways, width, height };

			// The last attempt is taken whatever it lands on. A number in a crowd is still readable and
			// a measurement silently missing from the four is not.
			if (!Overlaps(rect) || attempt >= LabelAttempts)
			{
				m_placedLabels.push_back(rect);
				xcontrols::Canvas::SetLeft(label, rect.Left);
				xcontrols::Canvas::SetTop(label, rect.Top);
				label.Visibility(xaml::Visibility::Visible);

				// Only for a number that had to be moved off its own line. One sitting on the line, or
				// squared up with the end of it, is already attached to what it measures.
				TieBack(
					leader.Tie,
					rect,
					horizontal,
					horizontal ? middle : cross,
					horizontal ? cross : middle,
					sideways != centred);
				return;
			}

			if (horizontal) sideways -= height + LabelGap;
			else sideways += width + LabelGap;
		}
	}

	// Counts something already on the canvas as taken, so a number is kept off it. A badge that is not
	// being shown claims nothing, and one that has never been laid out has no rectangle to claim.
	void Occupy(xcontrols::Border const& badge)
	{
		if (!badge || badge.Visibility() != xaml::Visibility::Visible) return;

		const double left = xcontrols::Canvas::GetLeft(badge);
		const double top = xcontrols::Canvas::GetTop(badge);
		if (std::isnan(left) || std::isnan(top)) return;

		const double width = badge.ActualWidth();
		const double height = badge.ActualHeight();
		if (width <= 0.0 || height <= 0.0) return;

		m_placedLabels.push_back(RulerRect{ left, top, width, height });
	}

	// Whether a label would land on one already placed, with a gap so that two that merely touch still
	// read as two numbers rather than one longer one.
	bool Overlaps(RulerRect const& candidate) const
	{
		for (auto const& placed : m_placedLabels)
		{
			const bool clear = candidate.Right() + LabelGap <= placed.Left
				|| placed.Right() + LabelGap <= candidate.Left
				|| candidate.Bottom() + LabelGap <= placed.Top
				|| placed.Bottom() + LabelGap <= candidate.Top;

			if (!clear) return true;
		}

		return false;
	}

	// The tie from a moved label back to the point it measures, leaving the label's own edge rather
	// than its centre so the line is never drawn underneath the number it belongs to.
	static void TieBack(
		xshapes::Line const& tie,
		RulerRect const& label,
		bool horizontal,
		double pointX,
		double pointY,
		bool moved)
	{
		if (!tie) return;

		if (!moved)
		{
			tie.Visibility(xaml::Visibility::Collapsed);
			return;
		}

		tie.X1(horizontal ? label.Left + label.Width / 2.0 : label.Left);
		tie.Y1(horizontal ? label.Bottom() : label.Top + label.Height / 2.0);
		tie.X2(pointX);
		tie.Y2(pointY);
		tie.Visibility(xaml::Visibility::Visible);
	}


	// Rulers.
	xcontrols::StackPanel m_rulersRow{ nullptr };
	xcontrols::TextBlock m_sizeLabel{ nullptr };
	xcontrols::TextBlock m_marginLabel{ nullptr };
	xcontrols::TextBlock m_paddingLabel{ nullptr };
	xcontrols::Border m_sizePill{ nullptr };
	xcontrols::Border m_marginPill{ nullptr };
	xcontrols::Border m_paddingPill{ nullptr };
	std::vector<Leader> m_leaders;

	// Where this measurement's numbers have been put, so the next one can be kept off them. Held for
	// one measurement only; DrawMeasurement empties it.
	std::vector<RulerRect> m_placedLabels;
	Band m_marginBand;
	Band m_borderBand;
	Band m_paddingBand;

	IRoseOverlaySurface& m_surface;
	RosePick& m_pick;
};
