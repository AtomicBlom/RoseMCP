#pragma once

// The magnifier: scaling the app with a render transform, or magnifying a capture of it pixel by
// pixel and reading the colour under the pointer.
//
// Included after the provider's aliases, like the overlay it lives on, because it builds XAML.
//
// A tool rather than more of the overlay. It owns its buttons, its row of the toolbar and its
// twenty-two members, and asks the overlay only for what it cannot answer itself -- so what a tool
// needs of the surface it is drawn on is a list somebody can read in one go, rather than something
// to be inferred from which of sixty members a method happens to touch.

// What a tool needs of the overlay hosting it, and what it tells the overlay in return.
//
// Narrow on purpose. Every addition here is a thing every future tool may reach for, so it holds
// what a tool genuinely cannot answer for itself: where the app's content is, how big the window
// is, where the pointer was last seen, an element's rectangle, the two layers it can put things on,
	// and the three things it says back when its own state changes.
struct IRoseOverlaySurface
{
	virtual ~IRoseOverlaySurface() = default;

	// The app's own content, deliberately not the overlay's root: rendering the content captures the
	// app without the toolbar, and rendering the root would put the toolbar inside its own lens.
	virtual xaml::UIElement AppContent() const = 0;

	// The window's extent, in the overlay's own coordinates.
	virtual winrt::Windows::Foundation::Size Extent() const = 0;

	// The last place the pointer was seen, or negative when it has not been seen. A tool must treat
	// an unknown pointer as unknown rather than clamping it, or a mode entered with the pointer
	// outside the window anchors itself to a corner.
	virtual winrt::Windows::Foundation::Point Pointer() const = 0;

	// The layer the outlines and badges are drawn on. It scales with the app, or a picked element's
	// outline stays where the element was before the app moved under it.
	virtual xaml::UIElement Marks() const = 0;

	// Puts an element on the overlay's own canvas, above the marks.
	virtual void Adorn(xaml::UIElement const& element) = 0;

	// Puts an element on the marks layer instead, so it scales with the app.
	virtual void Mark(xaml::UIElement const& element) = 0;

	// An element's rectangle in the overlay's coordinates, false when it has none to give or belongs
	// to a different XamlRoot. The overlay answers it because it is the thing that knows which root
	// it was installed in, and an element elsewhere is said to be elsewhere rather than drawn at the
	// right coordinates in the wrong window.
	virtual bool Bounds(xaml::UIElement const& element, winrt::Windows::Foundation::Rect& rect) = 0;

	// The pick changed. Said rather than acted on, because what depends on a pick is the overlay's
	// business: a measurement taken from an element that is no longer selected is a confident wrong
	// answer, so the rulers have to hear about it, and the pick is not the thing that knows they
	// exist.
	virtual void PickChanged() = 0;

	// Re-lights the toolbar. Called when a tool's state changes, because which button wears the
	// accent is the overlay's business and what changed is the tool's.
	virtual void Chrome() = 0;

	// Re-places the panel in the window. A tool whose row appears or disappears changes the panel's
	// size, and one that has grown past the edge it is anchored to has to be brought back inside.
	virtual void Reposition() = 0;
};

class RoseZoomTool final : protected RoseWidgets
{
public:
	explicit RoseZoomTool(IRoseOverlaySurface& surface) : m_surface(surface) {}

	// The toggle for the main bar, built here so the tool owns every button that reports its state.
	xcontrols::Button Button()
	{
		m_zoomButton = Chip(
			Glyph(IconZoom, 12.0),
			L"Magnify -- scale the app, or inspect its pixels and read the colour under the pointer",
			[this] { ToggleZoom(); });

		return m_zoomButton;
	}

	// Whether magnification is on, which the overlay asks because a mode nobody requested still
	// counts as work in progress: a toolbar carrying the factor and the colour readout is no use at
	// half opacity while the pointer is out in the app, which is exactly where it has to be.
	bool Active() const { return m_zoom != Zoom::Off; }

	// Follows the pointer without capturing another frame. A move is hundreds of events a second and
	// a readback is a GPU round trip; the timer decides how fresh the pixels are and this decides
	// which of them are on screen.
	void PointerMoved()
	{
		if (m_zoom == Zoom::Lens) UpdateLens();

		// The scaled app follows the pointer too, by moving the point it grows about rather than by
		// redrawing anything.
		if (m_zoom == Zoom::Transform) UpdateTransformOrigin();
	}

	// Lights its own buttons. The ends of the factor range are said by disabling rather than by
	// silently doing nothing: a button that responds to a click by leaving everything as it was
	// reads as broken rather than as bounded.
	void RefreshChrome() const
	{
		Paint(m_zoomButton, m_zoom == Zoom::Off ? Idle() : Accent());
		Paint(m_scaleButton, m_zoom == Zoom::Transform ? Accent() : Idle());
		Paint(m_pixelButton, m_zoom == Zoom::Lens ? Accent() : Idle());

		Enable(m_zoomInButton, m_zoomFactor < 32);
		Enable(m_zoomOutButton, m_zoomFactor > 2);
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

private:
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

		m_surface.Chrome();
		m_surface.Reposition();
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

	// What the transform mode scales. One element today -- the app's content -- but a list, because
	// the transform is saved and restored per element and a single saved slot is what made an earlier
	// version restore only the last thing it touched.
	std::vector<xaml::UIElement> ScaleTargets() const
	{
		std::vector<xaml::UIElement> targets;
		if (const auto content = m_surface.AppContent()) targets.push_back(content);

		// The marks go with it, or a picked element's outline stays where the element was before the
		// app moved under it.
		if (const auto marks = m_surface.Marks()) targets.push_back(marks);

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

		const auto bounds = m_surface.Extent();
		const bool known = m_surface.Pointer().X >= 0.0f && m_surface.Pointer().Y >= 0.0f
			&& m_surface.Pointer().X < bounds.Width && m_surface.Pointer().Y < bounds.Height;
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

				const double x = width > 0.0 ? Clamp(m_surface.Pointer().X / width, 0.0, 1.0) : 0.5;
				const double y = height > 0.0 ? Clamp(m_surface.Pointer().Y / height, 0.0, 1.0) : 0.5;
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
		m_surface.Adorn(m_lens);
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

		const auto content = m_surface.AppContent();
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

		const auto bounds = m_surface.Extent();
		const bool inside = m_surface.Pointer().X >= 0.0f && m_surface.Pointer().Y >= 0.0f
			&& m_surface.Pointer().X < bounds.Width && m_surface.Pointer().Y < bounds.Height;
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

		const int centreX = static_cast<int>(m_surface.Pointer().X * perDipX);
		const int centreY = static_cast<int>(m_surface.Pointer().Y * perDipY);

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
		const auto bounds = m_surface.Extent();
		const double gap = 18.0;

		double left = m_surface.Pointer().X + gap;
		if (left + size > bounds.Width) left = m_surface.Pointer().X - gap - size;

		double top = m_surface.Pointer().Y + gap;
		if (top + size > bounds.Height) top = m_surface.Pointer().Y - gap - size;

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

	// The lens, in device pixels, so it is a whole number of them at every factor: 192 divides by
	// every power of two up to 32, which is what keeps each source pixel an exact square block.
	static constexpr int LensDevicePixels = 192;

	// Roughly twelve captures a second. Fast enough to follow an animating app, and slow enough that
	// the readback is a fraction of a frame rather than a tax on one -- and it is a rate limit, not a
	// queue: a capture still in flight when the timer fires is simply skipped.
	static constexpr int LensIntervalMs = 80;

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

	IRoseOverlaySurface& m_surface;
};
