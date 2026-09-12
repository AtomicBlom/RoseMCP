#pragma once

// The geometry behind the rulers mode: the gaps between two elements, and the bands of one
// element's box model.
//
// Framework-free, like tap_channel.h and for the same reason. Nothing here names XAML, WinRT or
// xamlOM, so what a measurement *means* sits apart from the code that draws one -- and meaning is
// the half worth being able to read on its own, because "the gap" has no single answer once two
// rectangles overlap.
//
// Self-contained, unlike tap_overlay.h, so it is included from there rather than by each provider.

#include <algorithm>

// A rectangle in the overlay's own coordinates, which are the window root's device-independent
// pixels -- the unit XAML margins and padding are written in, so a number here is the number in the
// markup rather than a number the display scale has been applied to.
struct RulerRect
{
	double Left = 0.0;
	double Top = 0.0;
	double Width = 0.0;
	double Height = 0.0;

	double Right() const { return Left + Width; }
	double Bottom() const { return Top + Height; }
};

// The four sides of a Thickness, in the order XAML writes them.
struct RulerEdges
{
	double Left = 0.0;
	double Top = 0.0;
	double Right = 0.0;
	double Bottom = 0.0;

	bool Any() const { return Left != 0.0 || Top != 0.0 || Right != 0.0 || Bottom != 0.0; }
};

// One axis of a measurement, which is a separation or a pair of edge gaps and never both.
//
// An element nested inside another is what this mode is most often pointed at, and there the gap
// being asked about *is* the overlap: the distance from the container's edge in to the child's,
// which is the padding or the margin somebody is trying to account for. So overlapping is not the
// case with no answer, it is the case with two answers.
struct RulerAxis
{
	// Whether the two spans miss each other entirely. Separated carries Gap; overlapping carries Low
	// and High.
	bool Separated = false;

	// The distance between the facing edges, always positive. Zero is a real answer -- two edges
	// flush -- and is why touching counts as separated rather than as an overlap of nothing.
	double Gap = 0.0;

	// Which side the hovered span is on: left of the anchor, or above it. A distance that is always
	// positive cannot say, and the leader has to be drawn on one side or the other.
	bool Before = false;

	// Overlapping only: from the anchor's low edge in to the hovered's, and from the anchor's high
	// edge in to the hovered's. Both positive means the hovered span sits inside the anchor's, and
	// for a nested element they are the padding or margin on this axis. A negative one means it
	// overhangs that edge, which is equally the honest number.
	double Low = 0.0;
	double High = 0.0;
};

// How the two rectangles sit, for the sentence the toolbar says. The numbers above are the answer
// either way; this is only what to call it.
enum class RulerFit
{
	Apart,       // at least one axis separated
	Overlapping, // both axes overlap, neither rectangle enclosing the other
	Inside,      // the hovered rectangle is within the anchor
	Around,      // the anchor is within the hovered rectangle
	Same,        // the same rectangle
};

struct RulerMeasurement
{
	RulerRect Anchor;
	RulerRect Hovered;
	RulerAxis Horizontal;
	RulerAxis Vertical;
	RulerFit Fit = RulerFit::Apart;
};

// The bands of an element's box model, outermost first. The element's own rectangle is Border:
// RenderSize takes in the border and the padding and leaves out the margin, so the margin is the
// only band that grows outwards.
struct RulerBands
{
	RulerRect Margin;
	RulerRect Border;
	RulerRect Padding;
	RulerRect Content;
};

inline RulerAxis MeasureAxis(double anchorLow, double anchorHigh, double hoveredLow, double hoveredHigh)
{
	RulerAxis axis;

	if (hoveredLow >= anchorHigh)
	{
		axis.Separated = true;
		axis.Gap = hoveredLow - anchorHigh;
		return axis;
	}

	if (anchorLow >= hoveredHigh)
	{
		axis.Separated = true;
		axis.Gap = anchorLow - hoveredHigh;
		axis.Before = true;
		return axis;
	}

	axis.Low = hoveredLow - anchorLow;
	axis.High = anchorHigh - hoveredHigh;
	return axis;
}

inline RulerFit MeasureFit(RulerAxis const& horizontal, RulerAxis const& vertical)
{
	if (horizontal.Separated || vertical.Separated) return RulerFit::Apart;

	const bool same = horizontal.Low == 0.0 && horizontal.High == 0.0
		&& vertical.Low == 0.0 && vertical.High == 0.0;
	if (same) return RulerFit::Same;

	const bool inside = horizontal.Low >= 0.0 && horizontal.High >= 0.0
		&& vertical.Low >= 0.0 && vertical.High >= 0.0;
	if (inside) return RulerFit::Inside;

	const bool around = horizontal.Low <= 0.0 && horizontal.High <= 0.0
		&& vertical.Low <= 0.0 && vertical.High <= 0.0;
	if (around) return RulerFit::Around;

	return RulerFit::Overlapping;
}

inline RulerMeasurement Measure(RulerRect const& anchor, RulerRect const& hovered)
{
	RulerMeasurement measurement;
	measurement.Anchor = anchor;
	measurement.Hovered = hovered;
	measurement.Horizontal = MeasureAxis(anchor.Left, anchor.Right(), hovered.Left, hovered.Right());
	measurement.Vertical = MeasureAxis(anchor.Top, anchor.Bottom(), hovered.Top, hovered.Bottom());
	measurement.Fit = MeasureFit(measurement.Horizontal, measurement.Vertical);
	return measurement;
}

// Where a leader for one axis sits on the other one.
//
// Inside the shared band when the two rectangles have one, because a dimension line drawn where both
// elements are is a dimension line that needs no explaining. Where they share nothing it goes midway
// between their facing edges, which is inside neither -- so the caller draws the dashed extensions
// that connect each edge to it, the way a drawing does.
inline double LeaderCross(double anchorLow, double anchorHigh, double hoveredLow, double hoveredHigh)
{
	const double sharedLow = (std::max)(anchorLow, hoveredLow);
	const double sharedHigh = (std::min)(anchorHigh, hoveredHigh);
	if (sharedHigh >= sharedLow) return (sharedLow + sharedHigh) / 2.0;

	if (hoveredLow >= anchorHigh) return (anchorHigh + hoveredLow) / 2.0;
	return (hoveredHigh + anchorLow) / 2.0;
}

// A rectangle grown by a thickness, which is what a margin does: XAML arranges an element into its
// slot less the margin, so the band outside the element's own rectangle is the space the margin took.
inline RulerRect Grow(RulerRect const& rect, RulerEdges const& edges)
{
	return RulerRect{
		rect.Left - edges.Left,
		rect.Top - edges.Top,
		rect.Width + edges.Left + edges.Right,
		rect.Height + edges.Top + edges.Bottom };
}

// A rectangle shrunk by a thickness, never past nothing. A padding wider than the element it is on
// is not impossible -- an element can be clipped or constrained below its desired size -- and a
// negative extent would be drawn as a rectangle hanging off the wrong side.
inline RulerRect Shrink(RulerRect const& rect, RulerEdges const& edges)
{
	const double width = (std::max)(0.0, rect.Width - edges.Left - edges.Right);
	const double height = (std::max)(0.0, rect.Height - edges.Top - edges.Bottom);
	return RulerRect{ rect.Left + edges.Left, rect.Top + edges.Top, width, height };
}

inline RulerBands BoxModel(
	RulerRect const& border,
	RulerEdges const& margin,
	RulerEdges const& borderThickness,
	RulerEdges const& padding)
{
	RulerBands bands;
	bands.Margin = Grow(border, margin);
	bands.Border = border;
	bands.Padding = Shrink(border, borderThickness);
	bands.Content = Shrink(bands.Padding, padding);
	return bands;
}
