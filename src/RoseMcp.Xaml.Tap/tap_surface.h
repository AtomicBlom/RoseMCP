#pragma once

// What the COM object needs from the framework-bound half of the tap, as declarations it can be
// compiled against without naming a projection.
//
// The tap divides by what a piece of code names rather than by what it does. tap_channel.h and
// tap_measure.h name nothing external; this file and tap_diagnostics.h name only xamlOM, which
// Windows.UI.Xaml and Microsoft.UI.Xaml declare identically; tap_overlay.h and tap_render.h name the
// projection aliases and are therefore compiled once per framework. The tier a file sits at is not a
// filing preference -- it is checked by including it before the aliases are defined, where naming one
// fails to compile.
//
// RoseTap is almost entirely xamlOM: siting, the node list, the tree snapshot, a property read, a
// batch of edits and the request dispatch are all ABI both frameworks implement the same way. Two
// things reached across into the projected world and pulled the whole object over with them -- the
// overlay it drives, and four reads that need a concrete projected type. Both are declared here, so
// the object compiles above the alias line and what is genuinely per-framework is the only thing
// below it.
//
// Nothing in a signature here is a projected type. That is the property that makes the split work,
// and it is the one to check when adding to it: a handle, a string, a bool or an std:: container
// crosses the boundary, and an xaml:: anything does not.

#include "tap_diagnostics.h"

#include <functional>
#include <map>
#include <string>
#include <vector>

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

/// One property an apply changed on one element, as the overlay marks it: which element, which
/// property, and what it said before and after. Built by the apply from the property chain walk it
/// makes anyway, so recording it asks the framework nothing more.
struct TapChange
{
	InstanceHandle Handle = 0;
	std::wstring Property;
	std::wstring Old;
	std::wstring New;
};

/// What the person asked about by pressing Ask on the toolbar, held until the host comes for it.
///
/// A latch rather than a message, because the pipe carries requests from the host and replies to
/// them, and nothing else: the app never speaks first. The press is written here on the UI thread
/// and read by the reader thread when the host asks, so it is the one piece of tap state with a lock
/// of its own -- and the read touches no XAML, which is what lets the host ask on its own refresh
/// without costing the app's UI thread anything.
///
/// Only the toolbar writes it, and only on a person's click. What it holds is a description of what
/// was on screen -- a handle, a measurement in words, a colour -- and all it can make happen is a
/// window opening with a note the person still has to write and send.
///
/// The reply carries how long ago the press was made, because the toolbar outlives every session: a
/// press made while no host was connected is collected by the next one, and judged by when it was
/// collected it would open a window, hours later, about a question nobody is waiting on. And the
/// sequence keeps counting across a forgotten press, so a host that collected press 3 before the pipe
/// went is never handed a different press 3 afterwards.
class RoseTapAsk
{
public:
	/// Records a press, replacing any the host has not collected: the latest press is the question.
	void Raise(std::wstring kind, std::wstring readable, InstanceHandle element, InstanceHandle other, std::wstring colour)
	{
		std::lock_guard<std::mutex> guard(m_mutex);
		m_sequence++;
		m_pressed = true;
		m_raisedTick = ::GetTickCount64();
		m_kind = std::move(kind);
		m_readable = std::move(readable);
		m_element = element;
		m_other = other;
		m_colour = std::move(colour);
	}

	/// The latest press as a reply row, or "none". <paramref name="seen"/> is the sequence the host
	/// already holds, which is how the toolbar learns its press arrived: reports whether this read is
	/// the one that told it so, since the chip that lit on the press goes back to rest then.
	std::string Reply(unsigned long seen, bool& arrived)
	{
		std::lock_guard<std::mutex> guard(m_mutex);

		arrived = false;
		if (!m_pressed) return "none\n";

		if (seen >= m_sequence && m_collected < m_sequence)
		{
			m_collected = m_sequence;
			arrived = true;
		}

		// The age last, so the seven fields before it mean what they always meant.
		const ULONGLONG age = ::GetTickCount64() - m_raisedTick;
		const std::wstring row = L"ask\t" + std::to_wstring(m_sequence) + L'\t' + Escape(m_kind.c_str()) + L'\t'
			+ Escape(m_readable.c_str()) + L'\t' + std::to_wstring(m_element) + L'\t' + std::to_wstring(m_other) + L'\t'
			+ Escape(m_colour.c_str()) + L'\t' + std::to_wstring(age);

		return Utf8(row) + "\n";
	}

	/// Whether a press is waiting for the host, which is what keeps the chip lit.
	bool Waiting()
	{
		std::lock_guard<std::mutex> guard(m_mutex);
		return m_pressed && m_collected < m_sequence;
	}

	/// Forgets the press, because the session it was for has ended. A later host starts from nothing,
	/// or the next session to connect would open a window about a question nobody asked it. The count
	/// is kept, so the next press is numbered after every one before it.
	void Forget()
	{
		std::lock_guard<std::mutex> guard(m_mutex);
		m_pressed = false;
		m_collected = m_sequence;
		m_raisedTick = 0;
		m_kind.clear();
		m_readable.clear();
		m_element = 0;
		m_other = 0;
		m_colour.clear();
	}

private:
	std::mutex m_mutex;
	unsigned long m_sequence = 0;
	unsigned long m_collected = 0;
	bool m_pressed = false;
	ULONGLONG m_raisedTick = 0;
	std::wstring m_kind;
	std::wstring m_readable;
	InstanceHandle m_element = 0;
	InstanceHandle m_other = 0;
	std::wstring m_colour;
};

/// The one latch, shared by the toolbar that writes it and the dispatcher that reads it.
static RoseTapAsk g_ask;

/// The overlay as the request dispatcher uses it: arming a pointer mode, picking, clearing, and
/// reading back what is picked.
///
/// An interface rather than the concrete type because every one of these already spoke in handles,
/// strings and bools -- the toolbar's XAML never crossed the boundary, so the only thing the concrete
/// type was supplying was a compile-time dependency on a framework. Reading the dispatcher no longer
/// requires knowing which of the two it is being compiled for.
///
/// Virtual dispatch is free at this granularity. The most frequent of these is ClearIfRemoved, once
/// per tree mutation, against a handle comparison and possibly a redraw.
struct IRoseOverlay
{
	virtual ~IRoseOverlay() = default;

	/// Puts the toolbar up, idempotently: a second injection finds it already there and leaves it be.
	/// The elements are the tree walk's candidates, which is where the app's own XamlRoot is found.
	virtual void Install(IXamlDiagnostics* diagnostics, const std::vector<InstanceHandle>& appElements) = 0;

	/// Takes the handle-to-source-file map from the tree enumeration, which is the only place it is
	/// available, and the basis of "just my XAML".
	virtual void SetSources(std::map<InstanceHandle, std::wstring> sources) = 0;

	/// Selects without a click, for a caller driving selection from the tree.
	virtual bool SelectByHandle(InstanceHandle handle) = 0;

	virtual void SetJustMyXaml(bool justMyXaml) = 0;

	/// Arms the pick mode over the app. Both modes share one capture layer, so arming either while the
	/// other is up keeps the layer already arranged.
	virtual bool BeginSelect(bool includeAllElements) = 0;
	virtual bool BeginRulers(bool includeAllElements) = 0;

	/// Waits for the capture layer to be arranged and reports the extent it was given. The caller must
	/// not already be on the UI thread, since the layout pass it waits for is the work it would block.
	virtual bool WaitForArmedExtent(int& width, int& height, unsigned int timeoutMs) = 0;

	/// Leaves the pointer mode. Separate from clearing the pick, or "keep this one and stop capturing
	/// my clicks" becomes unreachable.
	virtual void GoIdle() = 0;

	/// Clears the pick, reporting whether there was one.
	virtual bool Deselect() = 0;

	/// Drops the pick if this handle is what it pointed at, so a selection whose element leaves the
	/// tree does not outlive it.
	virtual void ClearIfRemoved(InstanceHandle handle) = 0;

	/// What the overlay is doing with the pointer, as one word. A name rather than a flag because a
	/// caller deciding whether the app is clickable needs an answer covering every mode there is.
	virtual const wchar_t* ModeName() const = 0;
	virtual bool JustMyXaml() const = 0;

	/// Why the last selection went away, and the pick as rows. Read together in one reply, because
	/// read separately they can describe a state that never existed at any instant.
	virtual const std::wstring& GoneReason() const = 0;
	virtual const std::string& SelectionRows() const = 0;

	/// Takes what an apply changed, to mark in the app. Called on the UI thread with the batch's
	/// property changes, after the batch has run.
	virtual void Changed(const std::vector<TapChange>& changes) = 0;

	/// Whether the changes are marked in the app, and switching that. The same switch as the
	/// toolbar's Changes button.
	virtual bool ChangesShown() const = 0;
	virtual void ShowChanges(bool shown) = 0;

	/// Drops every change and its mark, because the session they were made in has ended.
	virtual void ForgetChanges() = 0;

	/// Re-lights the toolbar, for a state that changed off the UI thread: the host collecting an Ask.
	virtual void Relight() = 0;
};

/// The one overlay, built on first use. Defined with the overlay itself, so this declaration is what
/// lets the dispatcher call it from above the alias line.
static IRoseOverlay& Overlay();

// The four reads xamlOM cannot serve, because the value lives on a concrete projected type.
//
// Each has the same shape: a handle becomes an IInspectable, a try_as<> chain finds the type that
// declares the property, and what comes back out is a string or another handle. The chain is the part
// that cannot be written once -- xcontrols::Border is a different type under each framework even
// where the source text is identical -- so the bodies live in tap_render.h, below the aliases, and
// everything that calls them does not.
//
// IXamlDiagnostics is passed rather than captured because these are free functions on purpose: a seam
// with no state is a seam that cannot be half-initialised.

/// The four corner radii, comma-separated in the same form a Thickness arrives in, so a caller that
/// parses one parses the other. False when the element declares no CornerRadius at all.
static bool RoseTapRenderCornerRadius(IXamlDiagnostics* diagnostics, InstanceHandle handle, std::wstring& rendered);

/// A brush handle as #AARRGGBB. Only a SolidColorBrush renders: it is the one with an unambiguous
/// textual form, and flattening a gradient or a ThemeResource into a colour would misrepresent it.
static bool RoseTapRenderBrush(IXamlDiagnostics* diagnostics, const wchar_t* valueText, std::wstring& rendered);

/// The handle of an element's own resource dictionary, for a keyed replacement.
static bool RoseTapResourcesOf(IXamlDiagnostics* diagnostics, InstanceHandle owner, InstanceHandle& dictionary);

/// The handle a resource key boxes to, which is how a dictionary is addressed by key.
static bool RoseTapKeyHandle(IXamlDiagnostics* diagnostics, const std::wstring& key, InstanceHandle& handle);

// Getting onto the app's UI thread, which each framework reaches a different way and neither reaches
// through a type that appears below.
//
// Where the walk and the body run is a seam rather than a rule because the two frameworks disagree
// about it: UWP enumerates inline on the thread that asks and reaches SetSite from inside the
// injection call, so the body must be posted and the walk must not be dispatched; WinUI dispatches
// tap creation onto the UI thread and its AdviseVisualTreeChange enqueues the walk back onto that
// same thread and blocks, so advising from SetSite would deadlock the one thread that could serve it.
// Each provider fills these with what its framework wants, and nothing here has to know which.

/// Runs the tap body -- the walk, the toolbar -- off the injection call. Never inline: holding
/// InitializeXamlDiagnosticsEx open for the length of a walk stalls everything the app has queued,
/// and starting a thread there asks the loader for a lock the injecting thread may hold, which does
/// not fail, it stops.
static void RoseTapRunTapBody(std::function<void()> body);

/// Runs work on the app's UI thread, inline when already there and dispatched otherwise. The pipe
/// reader needs this from one direction and the tap body from the other, which is why the thread id
/// is recorded beside it rather than asking the current thread -- that answers only while you are
/// already on it, and null everywhere else.
static bool RoseTapRunOnUiThread(const std::function<void()>& work);

/// Runs the tree walk. Dispatched or inline depending on what the framework does with
/// AdviseVisualTreeChange, which is the whole reason this is separate from the call above.
static bool RoseTapRunWalk(const std::function<void()>& walk);

/// Keeps the dispatcher the diagnostics site handed over, cast to whatever this framework's is -- a
/// CoreDispatcher on UWP, a DispatcherQueue on WinUI 3. Taken from IXamlDiagnostics::GetDispatcher,
/// which is xamlOM rather than a framework method, so only the cast is per-provider.
static void RoseTapCaptureDispatcher(::IInspectable* rawDispatcher);
