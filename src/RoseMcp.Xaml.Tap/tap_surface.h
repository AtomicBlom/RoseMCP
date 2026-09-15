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
