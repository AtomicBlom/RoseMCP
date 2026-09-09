#pragma once

// The COM object the diagnostics site talks to, its class factory, and the two DLL exports.
//
// Almost all of RoseTap is pure xamlOM ABI -- SetSite, OnVisualTreeChange, the tree snapshot, the
// property read, and applying a batch of commands -- which UWP and WinUI 3 implement identically.
// Its only projection uses are RenderCornerRadius and RenderBrush, try_as<> chains over control
// types that exist under both roots, so they resolve through the aliases like everything in the
// overlay.
//
// Included last, and needs CLSID_RoseTap defined by the provider: the class id is the one piece of
// genuine identity here, since it is what the host's injection names and what two providers must not
// share.

// The provider instance the reader serves from, and a lock over it.
//
// This is not bookkeeping, it is the correctness of the whole channel: InitializeXamlDiagnosticsEx
// builds a *new* provider on every injection, each with its own node list. The reader thread outlives
// any one of them, so a captured `this` would go on answering from the tree the first injection saw
// -- which is exactly how a removal came back as still present, and is a use-after-free the moment an
// old instance is released. It serves whichever instance is current, holding a reference to it.
class RoseTap;
static std::mutex g_activeMutex;
static RoseTap* g_active = nullptr;

// Declared here and defined after RoseTap, because SetSite calls them and they need the whole class.
static void SetActiveTap(RoseTap* tap);
static void StartPipeReader();

class RoseTap final : public IObjectWithSite, public IVisualTreeServiceCallback
{
public:
	HRESULT STDMETHODCALLTYPE QueryInterface(REFIID riid, void** ppv) override
	{
		if (!ppv) return E_POINTER;
		if (riid == IID_IUnknown || riid == __uuidof(IObjectWithSite))
		{
			*ppv = static_cast<IObjectWithSite*>(this);
		}
		else if (riid == __uuidof(IVisualTreeServiceCallback))
		{
			*ppv = static_cast<IVisualTreeServiceCallback*>(this);
		}
		else
		{
			*ppv = nullptr;
			return E_NOINTERFACE;
		}

		AddRef();
		return S_OK;
	}

	ULONG STDMETHODCALLTYPE AddRef() override { return ++m_refs; }

	ULONG STDMETHODCALLTYPE Release() override
	{
		const long remaining = --m_refs;
		if (remaining == 0) delete this;
		return remaining;
	}

	HRESULT STDMETHODCALLTYPE SetSite(IUnknown* site) override
	{
		if (!site)
		{
			Unadvise();
			return S_OK;
		}

		HRESULT hr = site->QueryInterface(__uuidof(IXamlDiagnostics), reinterpret_cast<void**>(&m_diagnostics));
		if (FAILED(hr))
		{
			// Said, not just returned: XamlDiagnostics::Launch discards what SetSite gives back, on the
			// reasoning that the app must keep running either way. So a bare return here is the tap
			// declining to start and telling nobody, which is the failure this whole file is worst at.
			Log(L"SetSite: the site is not an IXamlDiagnostics (hr=0x" + Hex(hr) + L")");
			return hr;
		}

		// Reported before g_workDir is assigned, deliberately: assigning it moves the log into the
		// work folder, so a line written afterwards saying the folder is unknown would be written
		// to the folder it just said it does not know.
		BSTR initData = nullptr;
		const HRESULT initHr = m_diagnostics->GetInitializationData(&initData);
		if (FAILED(initHr) || !initData)
		{
			Log(L"SetSite: no initialization data (hr=0x" + Hex(initHr) + L"), so there is no work folder");
			return FAILED(initHr) ? initHr : E_UNEXPECTED;
		}

		std::wstring data(initData, SysStringLen(initData));
		SysFreeString(initData);

		// "<work dir>|<pipe name>", the pipe name optional while the two channels overlap (#50).
		// Split on '|' because it cannot occur in a Windows path, so the work dir cannot contain one
		// and an older host that sends no pipe name still parses as exactly itself.
		const size_t bar = data.find(L'|');
		if (bar == std::wstring::npos)
		{
			g_workDir = data;
		}
		else
		{
			g_workDir = data.substr(0, bar);
			g_pipeName = data.substr(bar + 1);
		}

		// The UI thread, taken from the diagnostics site rather than from whichever thread we happen
		// to be on. Two things need it and they need it differently: the pipe reader is a background
		// thread that has to get onto the UI thread to touch XAML at all (#50), and the tap body has
		// to get back onto it after advising off it (#76). GetDispatcher is the documented route and
		// works from anywhere, which asking the current thread does not.
		//
		// What comes back is framework-shaped -- a CoreDispatcher on UWP, a DispatcherQueue on WinUI
		// 3 -- so the provider adopts it and the rest of this file only ever says "run this there".
		::IInspectable* rawDispatcher = nullptr;
		if (FAILED(m_diagnostics->GetDispatcher(&rawDispatcher))) rawDispatcher = nullptr;

		// Called even with nothing to give, because it also records *which* thread this is. That is
		// what lets "run this on the UI thread" answer correctly from the injected path, which is
		// already there, and from the reader thread, which is not.
		RoseTapCaptureDispatcher(rawDispatcher);

		ConnectPipe();

		m_diagnostics->QueryInterface(__uuidof(IVisualTreeService), reinterpret_cast<void**>(&m_tree));
		if (!m_tree)
		{
			Log(L"SetSite: no IVisualTreeService");
			return E_NOINTERFACE;
		}

		// Handed to the provider rather than run here, because where the rest of this may run is the
		// one thing the two frameworks genuinely disagree about (#76). WinUI 3 dispatches tap creation
		// onto the UI thread, and its AdviseVisualTreeChange enqueues the walk *back* onto that thread
		// and then blocks the caller waiting for it, with no check for already being on it -- so
		// advising from here deadlocks the one thread that can do the work. It presents as no
		// callbacks, no error and no return, which is why it read for a long time as the framework
		// ignoring us. UWP enumerates inline on the calling thread and wants no thread at all.
		//
		// The AddRef is not bookkeeping. XamlDiagnostics::Launch holds the tap only in a local ComPtr
		// and drops it the moment Launch returns; normally AdviseVisualTreeChange is what takes a
		// lasting reference, so returning before advising leaves nothing owning us, and the body would
		// then run against a destroyed object -- which it did, reading a thread id that was never set.
		AddRef();
		RoseTapRunTapBody([this]()
		{
			ServeRequest();
			Release();
		});

		return S_OK;
	}

	/// <summary>
	/// One injection's work: walk the tree and put the toolbar up. Nothing else, because injection loads
	/// this provider and every request after that arrives on the pipe.
	/// </summary>
	/// <remarks>
	/// Split out of SetSite so the two halves can run on different threads, which WinUI 3 requires
	/// and UWP does not care about. The division is exact rather than cautious: the walk is the only
	/// thing that must not run on the UI thread, and everything after it must not run anywhere else,
	/// because the framework dispatches for the walk alone -- "during normal operation it is the
	/// caller's responsibility to dispatch to the correct thread", in its own words.
	/// </remarks>
	void ServeRequest()
	{
		// The walk, on whichever thread this framework needs it on. The provider is the only half that
		// knows which that is, and getting it wrong deadlocks on one framework and races on the other.
		if (!RoseTapRunWalk([this]() { Walk(); }))
		{
			Log(L"the tree walk could not be dispatched, so this injection has no tree");
			return;
		}

		// Everything past the walk reads or writes live XAML, so it goes to the thread that owns it.
		// The division is exact rather than cautious: the walk is the only part whose thread the two
		// frameworks disagree about, and dividing the rest by which individual lines happen to touch an
		// element is how the next edit gets it wrong.
		RoseTapRunOnUiThread([this]()
		{
			// The toolbar is installed once and left there. The snapshot filters it out of every tree, so
			// what it costs is an element in the app that the app did not put there.
			//
			// Handed the elements out of the walk, because WinUI 3 asks which XamlRoot a diagnostics
			// layer is wanted for and the only reliable answer is an element already known to be in the
			// app's tree. All of them, not the first: the node the enumeration starts from is the host
			// object rather than a UIElement, so it has no XamlRoot to give.
			std::vector<InstanceHandle> candidates;
			candidates.reserve(m_nodes.size());
			for (const auto& node : m_nodes) candidates.push_back(node.Handle);

			Overlay().Install(m_diagnostics, candidates);

			// Per-element source info only exists here, where the tree was walked, so it is handed to the
			// overlay: it is what "just my XAML" decides on, and a click has no other way to learn it.
			std::map<InstanceHandle, std::wstring> sources;
			for (const auto& node : m_nodes)
			{
				if (!node.File.empty()) sources[node.Handle] = node.File;
			}

			Overlay().SetSources(std::move(sources));
		});

		// This instance is the one the reader should answer from now on, and only then is it safe to
		// have a reader at all: a request served before the walk would answer from an empty tree.
		SetActiveTap(this);
		StartPipeReader();
	}

	/// <summary>
	/// Enumerates the tree into the node list, and leaves this tap advised for everything after.
	/// </summary>
	/// <remarks>
	/// The enumeration arrives on the UI thread on both frameworks, which is what keeps the node list
	/// to a single writer: UWP delivers it inline to whoever asked and the provider therefore asks from
	/// the UI thread, while WinUI 3 enqueues it onto the UI thread whoever asks. The two get there by
	/// opposite routes, so where to ask from is the provider's decision and not this file's.
	/// </remarks>
	void Walk()
	{
		const HRESULT hr = m_tree->AdviseVisualTreeChange(this);
		Log(L"enumerated " + std::to_wstring(m_nodes.size()) + L" element(s) (advise hr=0x" + Hex(hr) + L")");
	}

	/// <summary>
	/// One request off the pipe, answered. Every request the host makes arrives here; injection only
	/// loads this provider.
	/// </summary>
	/// <remarks>
	/// An empty reply means the request was not understood, which a caller reports rather than retries.
	/// Every verb below therefore answers with something even when the answer is "nothing", because a
	/// caller that read a refusal as a lost message would send a batch of structural edits twice.
	/// </remarks>
	std::string Serve(const std::wstring& request)
	{
		if (request == L"tree")
		{
			size_t written = 0;
			std::string rows;
			if (!RoseTapRunOnUiThread([&] { rows = TreeSnapshotRows(written); })) return std::string();

			Log(L"pipe: served tree with " + std::to_wstring(written) + L" element(s)");
			return rows;
		}

		if (request.rfind(L"properties ", 0) == 0)
		{
			// Same parse as the injected path: a trailing " all" asks for the framework defaults too.
			const bool includeDefaults = request.size() >= 4 && request.compare(request.size() - 4, 4, L" all") == 0;
			const InstanceHandle handle = static_cast<InstanceHandle>(_wcstoui64(request.c_str() + 11, nullptr, 10));

			std::string reply;
			if (!RoseTapRunOnUiThread([&] { reply = PropertiesReply(handle, includeDefaults); })) return std::string();

			return reply;
		}

		// The overlay verbs. Each of these was a request the host made by injecting, purely because the
		// work has to happen on the app's UI thread and injection was the only way onto it. A resident
		// reader reaches the same thread through the dispatcher, so they are messages.
		//
		// selecthandle is matched before the arming verb, and the arming match requires the space: a
		// handle is not one of the flags select parses, and "selecthandle 1" must not be read as arming.
		if (request.rfind(L"selecthandle ", 0) == 0)
		{
			const InstanceHandle handle = static_cast<InstanceHandle>(_wcstoui64(request.c_str() + 13, nullptr, 10));

			bool selected = false;
			if (!RoseTapRunOnUiThread([&] { selected = Overlay().SelectByHandle(handle); })) return std::string();

			return selected ? std::string("selected\n") : std::string("none\n");
		}

		if (request == L"select" || request.rfind(L"select ", 0) == 0)
		{
			if (!RoseTapRunOnUiThread([&]
			{
				// Tokenised rather than suffix-matched: "all" asks for elements the framework would not
				// hit-test, and "nomyxaml" turns off the preference for the app's own markup. A flag the
				// person set on the toolbar is left alone unless the request actually mentions it.
				bool includeAll = false;
				for (const auto& token : Tokens(request))
				{
					if (token == L"all") includeAll = true;
					else if (token == L"myxaml") Overlay().SetJustMyXaml(true);
					else if (token == L"nomyxaml") Overlay().SetJustMyXaml(false);
				}

				Overlay().BeginSelect(includeAll);
			})) return std::string();

			// Waited for here rather than inside the dispatch, because the pass being waited for runs on
			// the thread the dispatch would be holding.
			int width = 0;
			int height = 0;
			Overlay().WaitForArmedExtent(width, height, 5000);

			return "armed\t" + std::to_string(width) + "\t" + std::to_string(height) + "\n";
		}

		if (request == L"idle")
		{
			if (!RoseTapRunOnUiThread([&] { Overlay().EndSelect(); })) return std::string();
			return std::string("idle\n");
		}

		if (request == L"deselect")
		{
			bool had = false;
			if (!RoseTapRunOnUiThread([&] { had = Overlay().Deselect(); })) return std::string();
			return had ? std::string("cleared\n") : std::string("nothing\n");
		}

		if (request == L"selection")
		{
			// The mode on the first line, then the candidate rows in the shape the work folder writes them,
			// so one parser on the host serves either channel. Asked for rather than pushed: a pick outlives
			// the request that armed it by design, because the person clicks when they click.
			std::string reply;
			if (!RoseTapRunOnUiThread([&]
			{
				reply = std::string(Overlay().Selecting() ? "select" : "idle")
					+ "\t" + (Overlay().JustMyXaml() ? "1" : "0")
					+ "\t" + Utf8(Escape(Overlay().GoneReason().c_str())) + "\n"
					+ Overlay().SelectionRows();
			})) return std::string();

			return reply;
		}

		if (request == L"apply" || request.rfind(L"apply\n", 0) == 0)
		{
			// The batch rides in the frame, one command per line after the verb. Through the work folder it
			// needs a staged commands.tsv read back with a narrow stream, which is a second encoding decision
			// on a path that already had one. A frame is UTF-8 at both ends.
			const size_t split = request.find(L'\n');

			std::vector<std::wstring> lines;
			if (split != std::wstring::npos)
			{
				std::wstringstream stream(request.substr(split + 1));
				std::wstring line;
				while (std::getline(stream, line, L'\n')) lines.push_back(line);
			}

			const std::vector<Command> commands = ParseCommands(lines);

			std::string rows;
			if (!RoseTapRunOnUiThread([&] { rows = ApplyBatch(commands); })) return std::string();

			Log(L"pipe: applied " + std::to_wstring(commands.size()) + L" command(s)");

			// An empty batch still answers with something. An empty frame means "not served here", and a
			// caller reading this one as a refusal falls back to injecting the same batch -- which for
			// anything the batch adds is a second copy.
			return rows.empty() ? std::string("\n") : rows;
		}

		if (request == L"detach")
		{
			// Asked for rather than inferred, so the host can say it happened. The pipe closing is the
			// only other signal a provider gets that its session is over, and by then there is nothing
			// left to answer on.
			return EndSession() ? std::string("released") : std::string("already released");
		}

		Log(L"pipe: no handler for '" + request + L"', falling back to the files");
		return std::string();
	}

	/// <summary>
	/// Gives back the two framework interfaces this tap holds, because its session has ended.
	/// </summary>
	/// <remarks>
	/// They were released only from SetSite(nullptr), which nothing reaches: no tap is ever unadvised
	/// (#68), so each injection left an IXamlDiagnostics and an IVisualTreeService held for the life
	/// of the app. A destructor would not have helped -- the framework's advise and the reader's
	/// active pointer both hold a reference, so the object is never deleted either.
	/// <para>
	/// On the UI thread, because unadvising is a call into the framework and everything past the tree
	/// walk belongs there. A thread that cannot be reached leaves the interfaces held and says so: a
	/// leak in somebody else's app is a worse outcome than a leak, but not as bad as a crash in it.
	/// </para>
	/// </remarks>
	bool EndSession()
	{
		bool released = false;
		if (!RoseTapRunOnUiThread([&] { released = Unadvise(); }))
		{
			Log(L"detach: could not reach the UI thread, so the two interfaces stay held");
			return false;
		}

		Log(released
			? L"detach: released IXamlDiagnostics and IVisualTreeService"
			: L"detach: nothing to release; this tap had already given them back");

		return released;
	}

	/// <summary>
	/// Stands this tap down as a later injection takes over: it stays advised, because it cannot do
	/// otherwise, but it stops keeping a copy of the tree and stops acting on what it is told.
	/// </summary>
	/// <remarks>
	/// A tap cannot be unadvised while its app goes on being inspected. UnadviseVisualTreeChange empties
	/// the handle map the diagnostics session mints element and value handles from, and leaves the
	/// service enumerating nothing for the next callback advised on it -- so a brush read comes back as
	/// the number it was addressed by rather than a colour, and the next injection walks an empty tree.
	/// Both are confident wrong answers rather than failures. So a provider is created per injection and
	/// every one of them stays a sink for the life of the app.
	/// <para>
	/// What that costs is what this removes. Each sink appends every add to a tree copy of its own, never
	/// cleared, and every mutation in the app is delivered to all of them on the UI thread -- the thread
	/// the next injection needs in order to be sited at all. Standing the old ones down leaves N sinks
	/// costing one tree and one handler that does any work.
	/// </para>
	/// <para>
	/// On the UI thread, because that is where the callbacks arrive: clearing the node list from another
	/// thread races a walk appending to it. A thread that cannot be reached leaves the tap holding its
	/// tree and says so, which is the same trade EndSession makes.
	/// </para>
	/// </remarks>
	bool StandDown()
	{
		if (!RoseTapRunOnUiThread([&] { Retire(); }))
		{
			Log(L"retired: could not reach the UI thread, so this tap keeps its copy of the tree");
			return false;
		}

		Log(L"retired: stood down, holding no tree");
		return true;
	}

	HRESULT STDMETHODCALLTYPE GetSite(REFIID riid, void** ppv) override
	{
		if (!m_diagnostics) return E_FAIL;
		return m_diagnostics->QueryInterface(riid, ppv);
	}

	HRESULT STDMETHODCALLTYPE OnVisualTreeChange(
		ParentChildRelation relation, VisualElement element, VisualMutationType mutationType) override
	{
		// A tap that has been stood down is still advised, because nothing can unadvise it, so this is
		// where it stops costing anything. Answering and doing nothing is the whole of standing down.
		if (m_retired) return S_OK;

		if (mutationType != Add)
		{
			// A selection whose element has left the tree is stale in both halves -- the mark drawn
			// over the app and the handle the host will keep calling with -- so the overlay is told.
			// It matches on the handle and does nothing when it is not the selected one.
			Overlay().ClearIfRemoved(element.Handle);

			// And the node list follows the tree it describes. A list that only ever grows is accurate
			// for exactly as long as something else refreshes it, which is what a fresh walk per
			// injection quietly does; a resident tap answering reads between injections has no such
			// refresh, and reports elements the framework has already let go. Closed over descendants,
			// because removing a Border removes the TextBlock inside it and the framework is not
			// required to say so twice -- and if it does, the second call finds nothing left to drop.
			ForgetSubtree(element.Handle);
			return S_OK;
		}

		// SrcInfo comes per element and was previously dropped on the floor. It is what tells an
		// element the developer wrote from one a control template produced, which is the whole of
		// "just my XAML" -- and it is a different field from PropertyChainSource::SrcInfo, so the
		// two can be populated independently. Empty is recorded as empty; absent source info must
		// not be reported as "declared nowhere".
		m_nodes.push_back({ element.Handle, relation.Parent, relation.ChildIndex,
			element.Type ? element.Type : L"", element.Name ? element.Name : L"",
			element.SrcInfo.FileName ? element.SrcInfo.FileName : L"",
			element.SrcInfo.LineNumber, element.SrcInfo.ColumnNumber });

		if (element.Name && element.Name[0])
		{
			m_byName[element.Name].push_back(element.Handle);
		}

		return S_OK;
	}

private:
	// One row per element: Handle, Parent, ChildIndex, Type, Name. Written to a temp file and renamed
	// so the host never reads a half-written snapshot; a ".ready" marker is the host's signal.
	//
	// The resident toolbar is dropped from the answer: the tool reports the app's UI, not RoseMCP's own.
	// The diagnostics UI layer it lives on is not enumerated by AdviseVisualTreeChange on the versions
	// tested -- the count is identical before and after the toolbar goes up -- so this is a guard against
	// a framework that does enumerate it, not a fix for one that does.
	// The snapshot's rows, UTF-8, newline-separated. Separated from writing them so the same bytes
	// can go down the pipe (#50) or into tree.tsv, rather than one of the two being built a second
	// way and drifting -- the address column is exactly the sort of thing that would drift.
	std::string TreeSnapshotRows(size_t& written)
	{
		const auto excluded = OverlaySubtree();

		// Each element's address, computed once for the whole snapshot rather than per row. It is
		// reported because it is the only way to address an element the markup never named, and an
		// unnamed element is the ordinary case for a click that lands inside a template.
		const auto paths = ComputePaths();

		std::string rows;
		written = 0;

		for (const auto& node : m_nodes)
		{
			if (excluded.count(node.Handle)) continue;

			const auto address = paths.find(node.Handle);
			const std::wstring path = address != paths.end() ? address->second : std::wstring();

			const std::wstring row = std::to_wstring(node.Handle) + L'\t' + std::to_wstring(node.Parent) + L'\t'
				+ std::to_wstring(node.ChildIndex) + L'\t' + Escape(node.Type.c_str()) + L'\t' + Escape(node.Name.c_str())
				+ L'\t' + Escape(node.File.c_str()) + L'\t' + std::to_wstring(node.Line) + L'\t' + std::to_wstring(node.Column)
				+ L'\t' + Escape(path.c_str());
			rows += Utf8(row);
			rows += '\n';
			written++;
		}

		return rows;
	}

	// The handles of our own toolbar's elements, empty whenever the layer is not enumerated at all.
	// Enumeration is parent-before-child in practice, but this closes over the subtree rather than
	// assuming it, since one missed pass would leak our UI into the answer.
	std::set<InstanceHandle> OverlaySubtree() const
	{
		std::set<InstanceHandle> excluded;

		// Flipping RoseTapShowOverlayInTree stops the toolbar hiding itself, so the tree and property
		// tools can be pointed at RoseMCP's own UI. See its declaration for why it exists.
		if (RoseTapShowOverlayInTree) return excluded;

		for (const auto& node : m_nodes)
		{
			if (node.Name == OverlayRootName) excluded.insert(node.Handle);
		}

		if (excluded.empty()) return excluded;

		for (bool grew = true; grew; )
		{
			grew = false;
			for (const auto& node : m_nodes)
			{
				if (excluded.count(node.Handle)) continue;
				if (!excluded.count(node.Parent)) continue;
				excluded.insert(node.Handle);
				grew = true;
			}
		}

		return excluded;
	}

	// One element's property chain: every effective (non-overridden) value with its type, provenance
	// (default/style/local/...), and the source location that set it, plus an element row carrying its
	// type and its own declaration site. Source locations are populated only when the app carries XAML
	// source info; otherwise those fields are empty and the caller degrades to provenance alone.
	/// Turns a handle to a SolidColorBrush into #AARRGGBB, leaving anything else alone.
	///
	/// Reads a CornerRadius off the element itself, because XAML diagnostics renders it as nothing.
	///
	/// Both are structs, both are set by the same markup, and only one comes back with a value --
	/// spelled here as UWP reports it, where WinUI 3 says Microsoft.UI.Xaml:
	///
	///     {"name":"Padding",      "value":"24,24,24,24", "valueType":"Windows.UI.Xaml.Thickness"}
	///     {"name":"CornerRadius", "value":"",            "valueType":"Windows.UI.Xaml.CornerRadius"}
	///
	/// That is not our formatting -- the BSTR is populated by the framework and populated with
	/// nothing -- so it can only be fixed by reading the value a second way.
	///
	/// Measured before being written, because the alternative was a per-type special case with a
	/// maintenance tail and no idea how long the tail was. A sweep of every property of every
	/// element in the probe app, 3,485 rows, found 32 empty-but-not-null values: 18 String
	/// properties that genuinely are empty, and 14 CornerRadius. Thickness, GridLength, Size,
	/// Vector3 and the rest all stringify. One type, so one special case.
	///
	/// The tail is still real: CornerRadius is declared by several unrelated types, and there is no
	/// generic way to read a dependency property without the property's own static. If a seventh
	/// type appears this returns false, and the caller reports the gap rather than an empty string --
	/// which is the part that makes the next one findable instead of silent.
	bool RenderCornerRadius(InstanceHandle handle, std::wstring& rendered)
	{
		if (!m_diagnostics || handle == 0) return false;

		::IInspectable* raw = nullptr;
		if (FAILED(m_diagnostics->GetIInspectableFromHandle(handle, &raw)) || !raw) return false;

		winrt::Windows::Foundation::IInspectable instance{ nullptr };
		winrt::attach_abi(instance, raw); // adopt the ref

		xaml::CornerRadius radius{};
		if (const auto border = instance.try_as<xcontrols::Border>()) radius = border.CornerRadius();
		else if (const auto control = instance.try_as<xcontrols::Control>()) radius = control.CornerRadius();
		else if (const auto grid = instance.try_as<xcontrols::Grid>()) radius = grid.CornerRadius();
		else if (const auto stack = instance.try_as<xcontrols::StackPanel>()) radius = stack.CornerRadius();
		else if (const auto relative = instance.try_as<xcontrols::RelativePanel>()) radius = relative.CornerRadius();
		else if (const auto presenter = instance.try_as<xcontrols::ContentPresenter>()) radius = presenter.CornerRadius();
		else return false;

		// The same four-number form Thickness arrives in, so the two read alike and a caller that
		// parses one parses the other.
		rendered = Number(radius.TopLeft) + L"," + Number(radius.TopRight)
			+ L"," + Number(radius.BottomRight) + L"," + Number(radius.BottomLeft);

		return true;
	}

	/// A double as XAML would write it: no trailing zeros, and no decimal point when it is whole.
	static std::wstring Number(double value)
	{
		wchar_t buffer[32];
		swprintf_s(buffer, L"%g", value);
		return buffer;
	}

	/// Whether an empty value is a value or a gap.
	///
	/// An unset string property really is the empty string -- AutomationProperties.Name and
	/// SelectedText account for 18 of the 32 empty values in the probe -- so reporting those as
	/// unrenderable would be a false alarm on the majority of them. Anything else that comes back
	/// empty while not being null is the framework declining to stringify something, which is a gap.
	static bool IsStringType(const wchar_t* valueType)
	{
		if (!valueType) return false;

		const std::wstring type = valueType;
		return type == L"Windows.Foundation.String" || type == L"System.String" || type == L"String";
	}

	/// The handle round-trips through GetIInspectableFromHandle, which is the reverse of what the
	/// overlay uses to identify a clicked element. Only SolidColorBrush is rendered: it is the one
	/// with an unambiguous textual form, and the overwhelming majority of what a hot reload sets. A
	/// gradient or a brush behind a ThemeResource is left as its handle rather than being flattened
	/// into a colour that would misrepresent it -- naming the resource key would be the better answer
	/// there, and is a separate piece of work.
	bool RenderBrush(const wchar_t* valueText, std::wstring& rendered)
	{
		if (!m_diagnostics || !valueText || !valueText[0]) return false;

		const InstanceHandle valueHandle = static_cast<InstanceHandle>(_wcstoui64(valueText, nullptr, 10));
		if (valueHandle == 0) return false;

		::IInspectable* raw = nullptr;
		if (FAILED(m_diagnostics->GetIInspectableFromHandle(valueHandle, &raw)) || !raw) return false;

		winrt::Windows::Foundation::IInspectable instance{ nullptr };
		winrt::attach_abi(instance, raw); // adopt the ref

		const auto brush = instance.try_as<xmedia::SolidColorBrush>();
		if (!brush) return false;

		const auto colour = brush.Color();
		wchar_t buffer[10];
		swprintf_s(buffer, L"#%02X%02X%02X%02X", colour.A, colour.R, colour.G, colour.B);
		rendered = buffer;
		return true;
	}

	// The property rows, into whatever sink the caller has. An std::ostream rather than a file, so
	// the same builder serves properties.tsv and the pipe reply (#50) -- one builder, because two
	// would drift and the provenance column is exactly what would drift.
	//
	// Returns false when the property chain could not be read at all, which the caller reports
	// differently from "read it and there was nothing".
	bool EmitProperties(std::ostream& file, InstanceHandle handle, bool includeDefaults, unsigned int& written)
	{
		written = 0;

		unsigned int sourceCount = 0;
		unsigned int valueCount = 0;
		PropertyChainSource* sources = nullptr;
		PropertyChainValue* values = nullptr;
		const HRESULT hr = m_tree->GetPropertyValuesChain(handle, &sourceCount, &sources, &valueCount, &values);
		if (FAILED(hr))
		{
			Log(L"GetPropertyValuesChain(" + std::to_wstring(handle) + L") failed hr=0x" + Hex(hr));
			return false;
		}

		std::wstring elementType;
		std::wstring elementFile;
		unsigned int elementLine = 0;
		unsigned int elementColumn = 0;
		for (unsigned int i = 0; i < sourceCount; i++)
		{
			if (elementType.empty() && sources[i].TargetType) elementType = sources[i].TargetType;
			const bool localWithSource = sources[i].Source == BaseValueSourceLocal && sources[i].SrcInfo.FileName && sources[i].SrcInfo.FileName[0];
			if (localWithSource && elementFile.empty())
			{
				elementFile = sources[i].SrcInfo.FileName;
				elementLine = sources[i].SrcInfo.LineNumber;
				elementColumn = sources[i].SrcInfo.ColumnNumber;
			}
		}

		{
			const std::wstring elementRow = L"E\t" + Escape(elementType.c_str()) + L'\t' + Escape(elementFile.c_str())
				+ L'\t' + std::to_wstring(elementLine) + L'\t' + std::to_wstring(elementColumn);
			file << Utf8(elementRow) << '\n';

			for (unsigned int i = 0; i < valueCount && written < 256; i++)
			{
				const PropertyChainValue& value = values[i];
				if (value.Overridden) continue; // Only the effective value of each property.
				if (!includeDefaults && IsComposition(value.PropertyName)) continue;

				std::wstring provenance = L"Unknown";
				std::wstring file2;
				unsigned int line = 0;
				unsigned int column = 0;
				if (value.PropertyChainIndex < sourceCount)
				{
					const PropertyChainSource& source = sources[value.PropertyChainIndex];
					provenance = Provenance(source.Source);
					if (!includeDefaults && source.Source == BaseValueSourceDefault) continue;

					// The location belongs to the *source object*, not to the property.
					// PropertyChainValue carries no source info at all -- the granularity this API
					// offers is per source, so a per-property file and line is a fabrication by
					// construction. For a Local value the source is the element itself, so every
					// locally-set property was being stamped with the element's own tag position:
					// six composition properties on a two-attribute element all claimed the same
					// file, line and column, and a reader who went and looked would find nothing
					// there. Worse, a genuine attribution was byte-identical to that.
					//
					// So it is emitted only when the source is something *other* than the element,
					// where it locates a real and different thing -- the style or template that set
					// the value, which is information the caller cannot get any other way. When the
					// source is the element, the element's own row already carries its position and
					// the property says nothing it cannot support.
					const bool sourceIsElement = source.Handle == handle;
					if (!sourceIsElement && source.SrcInfo.FileName && source.SrcInfo.FileName[0])
					{
						file2 = source.SrcInfo.FileName;
						line = source.SrcInfo.LineNumber;
						column = source.SrcInfo.ColumnNumber;
					}
				}

				const bool isNull = (value.MetadataBits & IsValueNull) != 0;
				const wchar_t* valueText = isNull ? L"" : (value.Value ? value.Value : L"");

				// A brush arrives as an object handle, which is the one thing a caller cannot use:
				// setting Background="Blue" and reading back "2447634627144" makes hot reload
				// unverifiable, and confirming the edit landed is the first thing anyone does after
				// applying one. So a SolidColorBrush is resolved and rendered as its colour.
				std::wstring rendered;
				if (!isNull && (value.MetadataBits & IsValueHandle) != 0 && RenderBrush(valueText, rendered))
				{
					valueText = rendered.c_str();
				}

				// A CornerRadius arrives as an empty string, which is the framework declining to
				// stringify it rather than anything we did. Read off the element instead (#21).
				const wchar_t* declaredType = value.ValueType && value.ValueType[0]
					? value.ValueType
					: (value.Type ? value.Type : L"");

				const bool emptyButNotNull = !isNull && !valueText[0];
				if (emptyButNotNull && std::wcscmp(declaredType, RoseTapXamlRoot L"CornerRadius") == 0
					&& RenderCornerRadius(handle, rendered))
				{
					valueText = rendered.c_str();
				}

				// Whatever is still empty and is not a string is a gap, said so rather than left to
				// look like an unset property. That indistinguishability is the whole of why the
				// CornerRadius case went unnoticed, and the next one should not need a sweep to find.
				const bool unrenderable = !isNull && !valueText[0] && !IsStringType(declaredType);

				std::wstring row = L"P\t" + Escape(value.PropertyName ? value.PropertyName : L"") + L'\t'
					+ Escape(valueText) + L'\t' + Escape(declaredType) + L'\t' + Escape(value.DeclaringType ? value.DeclaringType : L"")
					+ L'\t' + provenance + L'\t' + Escape(file2.c_str()) + L'\t' + std::to_wstring(line) + L'\t'
					+ std::to_wstring(column) + L'\t' + (isNull ? L"1" : L"0")
					+ L'\t' + (unrenderable ? L"1" : L"0");
				file << Utf8(row) << '\n';
				written++;
			}
		}

		FreePropertyChain(sources, sourceCount, values, valueCount);
		return true;
	}

	// The same rows as a string, for the pipe. A leading status line so "could not read the chain" is
	// distinguishable from "read it and there were no rows", which the marker file said with the word
	// "error" and a reply of nothing at all could not.
	std::string PropertiesReply(InstanceHandle handle, bool includeDefaults)
	{
		std::ostringstream rows;
		unsigned int written = 0;
		if (!EmitProperties(rows, handle, includeDefaults, written)) return "error\n";

		Log(L"pipe: served " + std::to_wstring(written) + L" propert(y/ies) for handle " + std::to_wstring(handle));
		return "ok\n" + rows.str();
	}

	// Applies each command from commands.tsv and writes apply.tsv -- one row per command with its
	// outcome (applied / target not found / property not found / a failure code) -- so the host can
	// report per-command results to the agent (#12).
	/// <summary>
	/// Runs a batch and returns one result row per command, in the order they were given.
	/// </summary>
	/// <remarks>
	/// The rows are the answer whichever channel asked for the batch: over the pipe they are the reply
	/// frame, and through the work folder they are what apply.tsv holds. One builder, because a result
	/// that meant one thing on one channel and something else on the other is a difference nothing would
	/// show until an edit reported the wrong outcome.
	/// </remarks>
	std::string ApplyBatch(const std::vector<Command>& commands)
	{
		Log(L"applying " + std::to_wstring(commands.size()) + L" command(s)");

		// Slots live for one batch and no longer. They name instances that have been built but not
		// yet attached to anything, which is what lets a nested element be created, filled and then
		// handed to its parent -- and a slot surviving into the next apply would let one batch's
		// half-built element be reached by another's command.
		m_slots.clear();

		std::string rows;
		for (const auto& command : commands)
		{
			std::wstring status;
			if (command.op == L"SetProperty") status = ApplySetProperty(command);
			else if (command.op == L"ClearProperty") status = ApplyClearProperty(command);
			else if (command.op == L"RemoveChild") status = ApplyRemoveChild(command);
			else if (command.op == L"CreateInstance") status = ApplyCreate(command);
			else if (command.op == L"AddChild") status = ApplyAddChild(command);
			else if (command.op == L"ReplaceResource") status = ApplyReplaceResource(command);
			else status = L"unsupported op";

			// The arg goes on the end, after the status. It is there because the host keys these results
			// by what it sent, and op-target-property alone stops being unique the moment one slot gets
			// two children: both rows would be "AddChild <slot> <blank>", and the second child's outcome
			// would overwrite the first's.
			const std::wstring row = command.op + L'\t' + Escape(command.target.c_str()) + L'\t'
				+ Escape(command.property.c_str()) + L'\t' + status + L'\t' + Escape(command.arg.c_str());
			rows += Utf8(row);
			rows += '\n';
		}

		return rows;
	}

	// The host sends the type it thinks the value should be, inferred from the property's name and the
	// shape of the string. That guess is right for the common cases and wrong whenever a property's
	// type cannot be read off its value -- CornerRadius="0" parses as a number, so it arrived as a
	// Double, which CreateInstance built without complaint and SetProperty then rejected with a bare
	// E_FAIL. So the hint is tried first, because it carries intent the runtime does not have (a colour
	// string is meant as a SolidColorBrush even where the live value is some other Brush), and the
	// property's own declared type is the fallback, because it is a fact rather than a guess.
	std::wstring ApplySetProperty(const Command& command)
	{
		InstanceHandle target = 0;
		const std::wstring unresolved = Resolve(command.target, target);
		if (!unresolved.empty()) return unresolved;

		unsigned int index = 0;
		std::wstring declaredType;
		if (!PropertyIndex(target, command.property, index, declaredType)) return L"property not found";

		std::wstring attempted;
		std::wstring failure;
		for (const auto& type : { command.valueType, declaredType })
		{
			if (type.empty() || type == attempted) continue;
			attempted = type;

			InstanceHandle valueHandle = 0;
			BSTR typeName = SysAllocString(type.c_str());
			BSTR value = SysAllocString(command.value.c_str());
			HRESULT hr = m_tree->CreateInstance(typeName, value, &valueHandle);
			SysFreeString(typeName);
			SysFreeString(value);
			if (FAILED(hr))
			{
				failure = L"CreateInstance(" + type + L") failed 0x" + Hex(hr);
				continue;
			}

			hr = m_tree->SetProperty(target, valueHandle, index);
			if (hr == S_OK)
			{
				Log(L"  set " + command.target + L"." + command.property + L" = " + command.value
					+ L" (as " + type + L")");
				return L"applied";
			}

			failure = L"SetProperty(" + type + L") failed 0x" + Hex(hr);
		}

		if (failure.empty()) failure = L"no value type to build " + command.property + L" from";
		Log(L"  " + command.target + L"." + command.property + L": " + failure);
		return failure;
	}

	// Removes an element from its parent's children.
	//
	// The command names the child, because that is what a diff knows: the element is in the old
	// markup and not in the new one. RemoveChild takes a *parent and a position*, so both are read
	// off the live tree here rather than carried in the command -- which is the better source in any
	// case, since a markup index and a visual index are not always the same number and it is the
	// visual one that is about to be indexed.
	std::wstring ApplyRemoveChild(const Command& command)
	{
		InstanceHandle child = 0;
		const std::wstring unresolved = Resolve(command.target, child);
		if (!unresolved.empty()) return unresolved;

		const TreeIndex index = BuildIndex();
		const auto found = index.ByHandle.find(child);
		if (found == index.ByHandle.end()) return L"target not found: it is not in the tree snapshot";

		const InstanceHandle parent = m_nodes[found->second].Parent;
		if (parent == 0) return L"cannot remove: it has no parent in the tree";

		InstanceHandle collection = 0;
		unsigned int position = 0;
		if (!LocateInParent(parent, child, collection, position))
		{
			return L"cannot remove: it is not in any collection its parent exposes";
		}

		const HRESULT hr = m_tree->RemoveChild(collection, position);
		if (hr != S_OK) return L"RemoveChild failed 0x" + Hex(hr);

		// The node list is append-only: OnVisualTreeChange appends on Add and removes nothing on a
		// Remove. So what has just gone has to be forgotten here, or the rest of this batch is
		// resolved and indexed against a tree that no longer exists -- and the failure that produces
		// is the removal landing on the sibling that moved up into the vacated position.
		ForgetSubtree(child);

		Log(L"  removed " + command.target);
		return L"applied";
	}

	// Builds an instance and keeps it in a slot for the rest of this batch, unattached to anything.
	std::wstring ApplyCreate(const Command& command)
	{
		std::wstring resolved;
		std::wstring failure;
		const InstanceHandle handle = Construct(command.property, resolved, failure);
		if (handle == 0) return failure;

		m_slots[command.target] = handle;
		Log(L"  built " + resolved + L" into " + command.target);
		return L"applied";
	}

	// The type name markup carries is a local one -- "Border" -- while the value types the apply path
	// builds are spelled out in full, so this looked like it needed a mapping. Measured, it does not:
	// CreateInstance resolves a bare local name on the versions tested, and "Grid" and "Rectangle"
	// both built on the first candidate even though they live in different namespaces.
	//
	// The candidates after the first are kept anyway, and cost nothing because they are only reached
	// when the one before failed. They exist for the case the measurement cannot speak for: a control
	// the app declares itself, whose local name the framework has no reason to know. Those are
	// answered from the full names the live tree already reports for elements of that local name --
	// the app's own answer about its own types, right by construction for anything already on screen
	// -- and then from the framework namespaces a XAML author would have meant.
	//
	// The two failure codes are worth keeping in mind if this ever needs revisiting. E_FAIL (0x80004005)
	// is "no type of that name"; E_UNEXPECTED (0x8000ffff) is a real type that could not be built as
	// asked, which is what an empty value string produced before it became a null one.
	InstanceHandle Construct(const std::wstring& typeName, std::wstring& resolved, std::wstring& failure)
	{
		if (typeName.empty())
		{
			failure = L"cannot build: no type was named";
			return 0;
		}

		std::vector<std::wstring> candidates{ typeName };

		for (const auto& node : m_nodes)
		{
			if (LocalType(node.Type) != typeName) continue;
			if (std::find(candidates.begin(), candidates.end(), node.Type) != candidates.end()) continue;

			candidates.push_back(node.Type);
		}

		for (const auto* space : {
			RoseTapXamlRoot L"Controls.", RoseTapXamlRoot L"Shapes.",
			RoseTapXamlRoot L"Media.", RoseTapXamlRoot })
		{
			const std::wstring qualified = std::wstring(space) + typeName;
			if (std::find(candidates.begin(), candidates.end(), qualified) != candidates.end()) continue;

			candidates.push_back(qualified);
		}

		for (const auto& candidate : candidates)
		{
			// A null value, not an empty one. An element has no textual value to parse, and asking the
			// framework to parse "" as a Grid is what E_UNEXPECTED was complaining about -- the type
			// resolved perfectly well, which the two different failure codes made clear: E_FAIL for a
			// name that names nothing, E_UNEXPECTED for a real type given an argument it cannot use.
			InstanceHandle handle = 0;
			BSTR name = SysAllocString(candidate.c_str());
			const HRESULT hr = m_tree->CreateInstance(name, nullptr, &handle);
			SysFreeString(name);

			if (FAILED(hr) || handle == 0)
			{
				failure = L"CreateInstance(" + candidate + L") failed 0x" + Hex(hr);
				Log(L"  " + failure);
				continue;
			}

			resolved = candidate;
			return handle;
		}

		return 0;
	}

	// Swaps what a key in an element's resource dictionary resolves to.
	//
	// ReplaceResource is on IVisualTreeService2, which is asked for here rather than at startup: a
	// framework without it should cost this one command and not the whole XAML surface.
	std::wstring ApplyReplaceResource(const Command& command)
	{
		InstanceHandle owner = 0;
		const std::wstring unresolvedOwner = Resolve(command.target, owner);
		if (!unresolvedOwner.empty()) return unresolvedOwner;

		InstanceHandle value = 0;
		const std::wstring unresolvedValue = Resolve(command.arg, value);
		if (!unresolvedValue.empty()) return unresolvedValue;

		InstanceHandle dictionary = 0;
		if (!ResourcesOf(owner, dictionary)) return L"cannot replace: that element has no Resources dictionary";

		// The key is a handle, not a string, which is the part of this signature that surprises. A
		// boxed hstring is the honest way to make one: the diagnostics host can hand back a handle for
		// any IInspectable, and a resource key in markup is a string.
		InstanceHandle key = 0;
		if (!KeyHandle(command.property, key)) return L"cannot replace: could not make a handle for the key";

		IVisualTreeService2* resources = nullptr;
		if (!m_diagnostics
			|| FAILED(m_diagnostics->QueryInterface(__uuidof(IVisualTreeService2), reinterpret_cast<void**>(&resources)))
			|| !resources)
		{
			return L"cannot replace: this framework does not offer IVisualTreeService2";
		}

		const HRESULT hr = resources->ReplaceResource(dictionary, key, value);
		resources->Release();

		if (hr != S_OK) return L"ReplaceResource failed 0x" + Hex(hr);

		Log(L"  replaced resource " + command.property + L" on " + command.target);
		return L"applied";
	}

	// An element's resource dictionary.
	//
	// Not from the property chain, which is where the first attempt looked and found nothing: that
	// chain reports dependency properties, and Resources is not one -- it is an ordinary property on
	// FrameworkElement. Asking the element itself is the answer, and the diagnostics host converts
	// between handles and objects in both directions, so there is a route to it and back.
	bool ResourcesOf(InstanceHandle owner, InstanceHandle& dictionary)
	{
		if (!m_diagnostics) return false;

		::IInspectable* raw = nullptr;
		if (FAILED(m_diagnostics->GetIInspectableFromHandle(owner, &raw)) || !raw) return false;

		winrt::Windows::Foundation::IInspectable instance{ nullptr };
		winrt::attach_abi(instance, raw); // adopt the ref

		const auto element = instance.try_as<xaml::FrameworkElement>();
		if (!element) return false;

		const auto resources = element.Resources();
		if (!resources) return false;

		const HRESULT hr = m_diagnostics->GetHandleFromIInspectable(
			reinterpret_cast<::IInspectable*>(winrt::get_abi(resources)), &dictionary);

		return SUCCEEDED(hr) && dictionary != 0;
	}

	bool KeyHandle(const std::wstring& key, InstanceHandle& handle)
	{
		if (!m_diagnostics || key.empty()) return false;

		const auto boxed = winrt::box_value(winrt::hstring{ key });
		const HRESULT hr = m_diagnostics->GetHandleFromIInspectable(
			reinterpret_cast<::IInspectable*>(winrt::get_abi(boxed)), &handle);

		return SUCCEEDED(hr) && handle != 0;
	}

	// Puts a built instance into its new parent's children.
	std::wstring ApplyAddChild(const Command& command)
	{
		InstanceHandle parent = 0;
		const std::wstring unresolvedParent = Resolve(command.target, parent);
		if (!unresolvedParent.empty()) return unresolvedParent;

		InstanceHandle child = 0;
		const std::wstring unresolvedChild = Resolve(command.arg, child);
		if (!unresolvedChild.empty()) return unresolvedChild;

		// The same lesson RemoveChild taught: what the API calls a parent is the collection, not the
		// element. Here there is no child already in it to search for, so the collection has to be
		// named -- and it is refused rather than guessed at when the parent has no children
		// collection, because a Border holds its content in a single Child property and putting
		// something there is a SetProperty, not an add.
		InstanceHandle collection = 0;
		std::wstring found;
		if (!ChildCollectionOf(parent, collection, found))
		{
			return found.empty()
				? L"cannot add: its parent exposes no children collection"
				: L"cannot add: its parent exposes no children collection (it has " + found + L")";
		}

		const HRESULT hr = m_tree->AddChild(collection, child, command.index);
		if (hr != S_OK) return L"AddChild failed 0x" + Hex(hr);

		Log(L"  added " + command.arg + L" under " + command.target + L" at " + std::to_wstring(command.index));
		return L"applied";
	}

	// The collection an element keeps its children in, by name, plus what was there to choose from
	// when none of the names matched -- so a refusal can say what it saw instead of only that it
	// failed.
	bool ChildCollectionOf(InstanceHandle parent, InstanceHandle& collection, std::wstring& found)
	{
		if (!m_tree) return false;

		unsigned int sourceCount = 0;
		unsigned int propertyCount = 0;
		PropertyChainSource* sources = nullptr;
		PropertyChainValue* values = nullptr;
		if (FAILED(m_tree->GetPropertyValuesChain(parent, &sourceCount, &sources, &propertyCount, &values)))
		{
			return false;
		}

		bool located = false;
		for (const auto* wanted : { L"Children", L"Items" })
		{
			for (unsigned int i = 0; i < propertyCount && !located; i++)
			{
				const bool isCollection = (values[i].MetadataBits & IsValueCollection) != 0;
				const bool isHandle = (values[i].MetadataBits & IsValueHandle) != 0;
				const bool isReadOnly = (values[i].MetadataBits & IsValueCollectionReadOnly) != 0;
				if (!isCollection || !isHandle || isReadOnly) continue;
				if (!values[i].Value || !values[i].Value[0] || !values[i].PropertyName) continue;
				if (std::wcscmp(values[i].PropertyName, wanted) != 0) continue;

				collection = static_cast<InstanceHandle>(_wcstoui64(values[i].Value, nullptr, 10));
				located = collection != 0;
			}

			if (located) break;
		}

		if (!located)
		{
			for (unsigned int i = 0; i < propertyCount; i++)
			{
				if ((values[i].MetadataBits & IsValueCollection) == 0 || !values[i].PropertyName) continue;

				if (!found.empty()) found += L", ";
				found += values[i].PropertyName;
			}
		}

		FreePropertyChain(sources, sourceCount, values, propertyCount);
		return located;
	}

	// Where a child actually sits: the collection holding it, and its index in that collection.
	//
	// RemoveChild is documented as taking a "parent", and the element is not what it means -- passing
	// the panel handle returns ERROR_NOT_FOUND, which is what sent me looking. What it wants is the
	// collection the child is in, which is the value of one of the parent's collection-valued
	// properties: Children on a Panel, Items on an ItemsControl, and something else again elsewhere.
	//
	// Found by looking through those collections for the child rather than from a table of property
	// names, because the name differs per container and the collection that contains the child is the
	// answer by definition. Asking the collection also yields the index it will be removed at, rather
	// than one inferred from the order the tree happened to be enumerated in -- which is the number
	// that has to be right, and the one a sibling shifting would have made wrong.
	bool LocateInParent(InstanceHandle parent, InstanceHandle child, InstanceHandle& collection, unsigned int& index)
	{
		if (!m_tree) return false;

		unsigned int sourceCount = 0;
		unsigned int propertyCount = 0;
		PropertyChainSource* sources = nullptr;
		PropertyChainValue* values = nullptr;
		if (FAILED(m_tree->GetPropertyValuesChain(parent, &sourceCount, &sources, &propertyCount, &values)))
		{
			return false;
		}

		bool found = false;
		for (unsigned int i = 0; i < propertyCount && !found; i++)
		{
			const bool isCollection = (values[i].MetadataBits & IsValueCollection) != 0;
			const bool isHandle = (values[i].MetadataBits & IsValueHandle) != 0;
			if (!isCollection || !isHandle || !values[i].Value || !values[i].Value[0]) continue;

			const InstanceHandle candidate = static_cast<InstanceHandle>(_wcstoui64(values[i].Value, nullptr, 10));
			if (candidate == 0) continue;

			if (IndexIn(candidate, child, index))
			{
				collection = candidate;
				found = true;
				Log(L"  found the child in " + std::wstring(values[i].PropertyName ? values[i].PropertyName : L"?")
					+ L" at index " + std::to_wstring(index));
			}
		}

		FreePropertyChain(sources, sourceCount, values, propertyCount);
		return found;
	}

	// The child's index within a collection, asked of the collection itself.
	bool IndexIn(InstanceHandle collection, InstanceHandle child, unsigned int& index)
	{
		unsigned int count = 0;
		if (FAILED(m_tree->GetCollectionCount(collection, &count)) || count == 0) return false;

		unsigned int returned = count;
		CollectionElementValue* elements = nullptr;
		if (FAILED(m_tree->GetCollectionElements(collection, 0, &returned, &elements)) || !elements) return false;

		bool found = false;
		for (unsigned int i = 0; i < returned && !found; i++)
		{
			if ((elements[i].MetadataBits & IsValueHandle) == 0 || !elements[i].Value) continue;
			if (static_cast<InstanceHandle>(_wcstoui64(elements[i].Value, nullptr, 10)) != child) continue;

			index = elements[i].Index;
			found = true;
		}

		for (unsigned int i = 0; i < returned; i++)
		{
			SysFreeString(elements[i].ValueType);
			SysFreeString(elements[i].Value);
		}

		CoTaskMemFree(elements);
		return found;
	}

	// Drops an element and everything beneath it from the node list and the name map.
	//
	// Closed over rather than assumed one level deep: removing a Border removes the TextBlock inside
	// it, and leaving those descendants behind would leave addresses that resolve to elements the
	// framework has already let go.
	void ForgetSubtree(InstanceHandle root)
	{
		// Children by parent, built once and walked down. Rescanning the whole list once per level is
		// affordable for an edit this tap made and is not for one the app made: this runs on the UI
		// thread for every element the app lets go, and an app lets go of elements continuously.
		std::map<InstanceHandle, std::vector<InstanceHandle>> children;
		for (const auto& node : m_nodes) children[node.Parent].push_back(node.Handle);

		std::set<InstanceHandle> doomed{ root };
		std::vector<InstanceHandle> pending{ root };
		while (!pending.empty())
		{
			const InstanceHandle current = pending.back();
			pending.pop_back();

			const auto found = children.find(current);
			if (found == children.end()) continue;

			for (const InstanceHandle child : found->second)
			{
				if (doomed.insert(child).second) pending.push_back(child);
			}
		}

		m_nodes.erase(
			std::remove_if(
				m_nodes.begin(),
				m_nodes.end(),
				[&doomed](const TreeNode& node) { return doomed.count(node.Handle) != 0; }),
			m_nodes.end());

		for (auto entry = m_byName.begin(); entry != m_byName.end(); )
		{
			auto& handles = entry->second;
			handles.erase(
				std::remove_if(
					handles.begin(),
					handles.end(),
					[&doomed](InstanceHandle handle) { return doomed.count(handle) != 0; }),
				handles.end());

			entry = handles.empty() ? m_byName.erase(entry) : std::next(entry);
		}
	}

	std::wstring ApplyClearProperty(const Command& command)
	{
		InstanceHandle target = 0;
		const std::wstring unresolved = Resolve(command.target, target);
		if (!unresolved.empty()) return unresolved;

		unsigned int index = 0;
		if (!PropertyIndex(target, command.property, index)) return L"property not found";

		const HRESULT hr = m_tree->ClearProperty(target, index);
		if (hr != S_OK) return L"ClearProperty failed 0x" + Hex(hr);

		Log(L"  cleared " + command.target + L"." + command.property);
		return L"applied";
	}

	// The local half of a CLR type name. The live tree carries `Windows.UI.Xaml.Controls.Border`
	// while a path segment carries `Border`, because markup names a type by a local name and an XML
	// prefix, and the prefix maps to a namespace nothing on this side can see. Both the counting and
	// the matching happen on the local name, which is what keeps the two halves in agreement.
	static std::wstring LocalType(const std::wstring& type)
	{
		const size_t dot = type.rfind(L'.');
		return dot == std::wstring::npos ? type : type.substr(dot + 1);
	}

	// Handle to position in m_nodes, and parent to its children in sibling order. Built once per
	// question: every path answer otherwise scans the whole node list, and doing that per node is
	// quadratic -- on an app with a few thousand elements that is the difference between writing a
	// snapshot and appearing to hang.
	struct TreeIndex
	{
		std::map<InstanceHandle, size_t> ByHandle;
		std::map<InstanceHandle, std::vector<size_t>> ByParent;
		std::vector<size_t> Roots;
	};

	TreeIndex BuildIndex() const
	{
		// Our own toolbar is left out, exactly as the reported snapshot leaves it out. It has to be
		// the same exclusion in both places or the two quietly disagree: an address is a position
		// among siblings, so counting an element nobody can see shifts every address after it, and
		// the address handed out would then resolve to the element next door. On the framework
		// versions tested the diagnostics layer is not enumerated at all, so this is a guard and not
		// a fix -- but an off-by-one that reports success is the wrong thing to leave to luck.
		const auto excluded = OverlaySubtree();

		TreeIndex index;
		for (size_t i = 0; i < m_nodes.size(); i++)
		{
			if (excluded.count(m_nodes[i].Handle)) continue;
			index.ByHandle[m_nodes[i].Handle] = i;
		}

		for (size_t i = 0; i < m_nodes.size(); i++)
		{
			if (excluded.count(m_nodes[i].Handle)) continue;

			// A parent that is not itself in the snapshot makes this node a root. Reporting parent 0
			// is one way that happens and not the only one: the enumeration starts somewhere, and a
			// subtree advised on its own has a parent that was never enumerated.
			const bool isRoot = index.ByHandle.count(m_nodes[i].Parent) == 0;
			if (isRoot) index.Roots.push_back(i);
			else index.ByParent[m_nodes[i].Parent].push_back(i);
		}

		const auto byChildIndex = [this](size_t a, size_t b) { return m_nodes[a].ChildIndex < m_nodes[b].ChildIndex; };
		for (auto& entry : index.ByParent) std::stable_sort(entry.second.begin(), entry.second.end(), byChildIndex);
		std::stable_sort(index.Roots.begin(), index.Roots.end(), byChildIndex);

		return index;
	}

	// Which siblings a node is counted among -- its parent's children, or the roots when it has no
	// parent in the snapshot.
	const std::vector<size_t>& SiblingsOf(const TreeIndex& index, size_t i) const
	{
		const auto found = index.ByParent.find(m_nodes[i].Parent);
		return found != index.ByParent.end() ? found->second : index.Roots;
	}

	// One Type[index] segment: the local type name, and the position among the siblings sharing it.
	std::wstring SegmentOf(const TreeIndex& index, size_t i) const
	{
		const std::wstring local = LocalType(m_nodes[i].Type);

		unsigned int position = 0;
		for (const size_t sibling : SiblingsOf(index, i))
		{
			if (sibling == i) break;
			if (LocalType(m_nodes[sibling].Type) == local) position++;
		}

		return local + L'[' + std::to_wstring(position) + L']';
	}

	// Every element's address, in one pass down from the roots. A named element is #name and anchors
	// everything beneath it; an unnamed one is its parent's address plus its own segment.
	//
	// This is the grammar RoseMcp.XamlDiff emits, so a path from a diff and a path from the tree mean
	// the same thing -- with one difference worth stating, because it decides which of them can be
	// trusted. A path computed here is resolved against the very tree it was computed from, so it is
	// exact. A diff's path is computed from markup, whose element order is not always the visual
	// tree's -- a ContentControl wraps its content in a presenter the markup never mentions -- so it
	// is a best effort, and where it misses it says so rather than landing somewhere plausible.
	std::map<InstanceHandle, std::wstring> ComputePaths() const
	{
		const TreeIndex index = BuildIndex();
		std::map<InstanceHandle, std::wstring> paths;

		std::vector<std::pair<size_t, std::wstring>> pending;
		for (auto root = index.Roots.rbegin(); root != index.Roots.rend(); ++root)
		{
			pending.push_back({ *root, std::wstring() });
		}

		while (!pending.empty())
		{
			const std::pair<size_t, std::wstring> item = pending.back();
			pending.pop_back();

			const TreeNode& node = m_nodes[item.first];
			std::wstring path;
			if (!node.Name.empty())
			{
				path = L"#" + node.Name;
			}
			else
			{
				const std::wstring segment = SegmentOf(index, item.first);
				path = item.second.empty() ? segment : item.second + L'/' + segment;
			}

			paths[node.Handle] = path;

			const auto children = index.ByParent.find(node.Handle);
			if (children == index.ByParent.end()) continue;
			for (auto child = children->second.rbegin(); child != children->second.rend(); ++child)
			{
				pending.push_back({ *child, path });
			}
		}

		return paths;
	}

	static std::vector<std::wstring> SplitPath(const std::wstring& path)
	{
		std::vector<std::wstring> segments;
		for (size_t start = 0; start <= path.size(); )
		{
			const size_t slash = path.find(L'/', start);
			const size_t length = slash == std::wstring::npos ? std::wstring::npos : slash - start;
			const std::wstring segment = path.substr(start, length);
			if (!segment.empty()) segments.push_back(segment);
			if (slash == std::wstring::npos) break;
			start = slash + 1;
		}

		return segments;
	}

	static bool ParseSegment(const std::wstring& segment, std::wstring& type, unsigned int& index)
	{
		if (segment.empty() || segment.back() != L']') return false;

		const size_t open = segment.find(L'[');
		if (open == std::wstring::npos) return false;

		type = segment.substr(0, open);
		if (type.empty()) return false;

		const std::wstring number = segment.substr(open + 1, segment.size() - open - 2);
		if (number.empty() || number.find_first_not_of(L"0123456789") != std::wstring::npos) return false;

		index = static_cast<unsigned int>(std::wcstoul(number.c_str(), nullptr, 10));
		return true;
	}

	// Resolves a target to exactly one element, or says why it could not. The reason travels back to
	// the agent as that edit's status: a bare "target not found" sent a caller looking for a mistake
	// in their address when the tree simply held two elements of that name, which is a different
	// problem with a different fix.
	//
	// A bare name and a path are both accepted. Anything with no brackets and no slash is a name --
	// including the #name an address is written with, so one string works whether it came from a
	// diff, from the tree, or from somebody typing it.
	std::wstring Resolve(const std::wstring& target, InstanceHandle& handle)
	{
		if (target.empty()) return L"target not found: no target was given";

		// A slot names something built earlier in this same batch and not yet attached to anything.
		// It is checked before the tree, because it is not in the tree -- that is the whole point of
		// it -- so no amount of walking would find it.
		if (target[0] == L'$')
		{
			const auto slot = m_slots.find(target);
			if (slot == m_slots.end()) return L"target not found: nothing has been built into " + target;

			handle = slot->second;
			return std::wstring();
		}

		const bool looksLikePath = target.find(L'/') != std::wstring::npos || target.find(L'[') != std::wstring::npos;
		if (!looksLikePath)
		{
			return ResolveName(target[0] == L'#' ? target.substr(1) : target, handle);
		}

		return ResolvePath(target, handle);
	}

	// A name belonging to more than one element is refused rather than answered with one of them.
	// The path form is how a caller says which, so the refusal names the count and leaves a route.
	std::wstring ResolveName(const std::wstring& name, InstanceHandle& handle)
	{
		const auto it = m_byName.find(name);
		if (it == m_byName.end() || it->second.empty())
		{
			Log(L"  target '" + name + L"' not found in the live tree");
			return L"target not found: no element is named '" + name + L"'";
		}

		if (it->second.size() > 1)
		{
			Log(L"  target '" + name + L"' names " + std::to_wstring(it->second.size()) + L" elements");
			return L"target ambiguous: " + std::to_wstring(it->second.size()) + L" elements are named '"
				+ name + L"'; address one of them by its path instead";
		}

		handle = it->second.front();
		return std::wstring();
	}

	std::wstring ResolvePath(const std::wstring& path, InstanceHandle& handle)
	{
		const std::vector<std::wstring> segments = SplitPath(path);
		if (segments.empty()) return L"target not found: '" + path + L"' has no path segments";

		const TreeIndex index = BuildIndex();
		const std::vector<size_t> none;
		size_t current = 0;

		for (size_t s = 0; s < segments.size(); s++)
		{
			// A name identifies an element outright wherever it appears, so it is looked up rather
			// than walked to. An emitted address only ever carries one as its first segment -- both
			// sides stop at the first name they meet -- so a later one comes from a caller who
			// composed the path, and honouring it costs nothing.
			if (segments[s][0] == L'#')
			{
				InstanceHandle named = 0;
				const std::wstring unresolved = ResolveName(segments[s].substr(1), named);
				if (!unresolved.empty()) return unresolved;

				const auto found = index.ByHandle.find(named);
				if (found == index.ByHandle.end())
				{
					return L"target not found: '" + segments[s] + L"' is not in the tree snapshot";
				}

				current = found->second;
				continue;
			}

			// The first segment is matched against the roots, and every later one against the
			// children of wherever the walk has reached.
			const std::vector<size_t>* candidates = &index.Roots;
			if (s > 0)
			{
				const auto children = index.ByParent.find(m_nodes[current].Handle);
				candidates = children != index.ByParent.end() ? &children->second : &none;
			}

			const std::wstring unstepped = Step(*candidates, segments[s], current);
			if (!unstepped.empty()) return unstepped;
		}

		handle = m_nodes[current].Handle;
		return std::wstring();
	}

	// Matches one segment among a set of siblings, counting the way the address was written.
	std::wstring Step(const std::vector<size_t>& siblings, const std::wstring& segment, size_t& current) const
	{
		std::wstring type;
		unsigned int wanted = 0;
		if (!ParseSegment(segment, type, wanted))
		{
			return L"target not found: '" + segment + L"' is neither Type[index] nor #name";
		}

		unsigned int position = 0;
		for (const size_t sibling : siblings)
		{
			if (LocalType(m_nodes[sibling].Type) != type) continue;
			if (position == wanted)
			{
				current = sibling;
				return std::wstring();
			}

			position++;
		}

		return L"target not found: no " + segment + L" here, among " + std::to_wstring(position)
			+ L" element(s) of type " + type;
	}

	bool PropertyIndex(InstanceHandle handle, const std::wstring& name, unsigned int& index)
	{
		std::wstring ignored;
		return PropertyIndex(handle, name, index, ignored);
	}

	// Also reports the property's own declared value type. That is the one authoritative answer to
	// "what does this property want", and the apply side needs it: a value built as the wrong type is
	// created quite happily and only fails at SetProperty, with an E_FAIL that names nothing.
	bool PropertyIndex(InstanceHandle handle, const std::wstring& name, unsigned int& index, std::wstring& valueType)
	{
		unsigned int sourceCount = 0;
		unsigned int propertyCount = 0;
		PropertyChainSource* sources = nullptr;
		PropertyChainValue* values = nullptr;
		const HRESULT hr = m_tree->GetPropertyValuesChain(handle, &sourceCount, &sources, &propertyCount, &values);
		if (FAILED(hr)) return false;

		bool found = false;
		for (unsigned int i = 0; i < propertyCount; i++)
		{
			if (!found && values[i].PropertyName && name == values[i].PropertyName)
			{
				index = values[i].Index;
				valueType = values[i].Type ? values[i].Type : L"";
				found = true;
			}
		}

		FreePropertyChain(sources, sourceCount, values, propertyCount);
		return found;
	}

	static void FreePropertyChain(PropertyChainSource* sources, unsigned int sourceCount, PropertyChainValue* values, unsigned int valueCount)
	{
		for (unsigned int i = 0; i < sourceCount; i++)
		{
			SysFreeString(sources[i].TargetType);
			SysFreeString(sources[i].Name);
			SysFreeString(sources[i].SrcInfo.FileName);
			SysFreeString(sources[i].SrcInfo.Hash);
		}

		for (unsigned int i = 0; i < valueCount; i++)
		{
			SysFreeString(values[i].Type);
			SysFreeString(values[i].DeclaringType);
			SysFreeString(values[i].ValueType);
			SysFreeString(values[i].ItemType);
			SysFreeString(values[i].Value);
			SysFreeString(values[i].PropertyName);
		}

		CoTaskMemFree(sources);
		CoTaskMemFree(values);
	}

	// One command per line, seven tab-separated fields. Shared by both channels, so a command means the
	// same thing whichever way it arrived.
	std::vector<Command> ParseCommands(const std::vector<std::wstring>& lines)
	{
		std::vector<Command> commands;
		for (std::wstring line : lines)
		{
			if (!line.empty() && line.back() == L'\r') line.pop_back();
			if (line.empty()) continue;

			std::vector<std::wstring> fields;
			std::wstringstream stream(line);
			std::wstring field;
			while (std::getline(stream, field, L'\t')) fields.push_back(field);
			fields.resize(7);
			commands.push_back({
				fields[0], fields[1], fields[2], fields[3], fields[4], fields[5],
				static_cast<unsigned int>(_wcstoui64(fields[6].c_str(), nullptr, 10)),
			});
		}

		return commands;
	}

	// Gives back the framework's two interfaces, and says whether there was anything to give back.
	//
	// Unadvising first is not tidiness: releasing m_tree while the framework still holds this tap as
	// a visual-tree callback leaves it calling into an object holding a dangling pointer.
	//
	// The overlay is untouched by this. It takes its own reference to IXamlDiagnostics when it is
	// installed, precisely so a click can resolve to a handle long after the injection that drew it
	// is over, so the toolbar outlives the release rather than being broken by it.
	// Sets the flag and gives back what the tap was holding for the tree it no longer answers about.
	// The vector is the expensive half by a distance, so it is shrunk rather than merely emptied.
	void Retire()
	{
		m_retired = true;

		m_nodes.clear();
		m_nodes.shrink_to_fit();
		m_byName.clear();
		m_slots.clear();
	}

	bool Unadvise()
	{
		const bool held = m_tree != nullptr || m_diagnostics != nullptr;

		if (m_tree) m_tree->UnadviseVisualTreeChange(this);
		if (m_tree) { m_tree->Release(); m_tree = nullptr; }
		if (m_diagnostics) { m_diagnostics->Release(); m_diagnostics = nullptr; }

		return held;
	}

	std::atomic<long> m_refs{ 1 };
	// Whether a later injection has taken over. Read and written on the UI thread alone, which is where
	// the callbacks it guards arrive.
	bool m_retired = false;
	IXamlDiagnostics* m_diagnostics = nullptr;
	IVisualTreeService* m_tree = nullptr;
	std::vector<TreeNode> m_nodes;
	// Every element a name belongs to, rather than the last one enumerated under it. A duplicated
	// x:Name is ordinary and not exotic -- a control template instantiated three times gives three
	// elements called the same thing -- and a single-valued map answered such a name with whichever
	// arrived last, so an apply landed on an arbitrary one of them and reported success either way.
	std::map<std::wstring, std::vector<InstanceHandle>> m_byName;

	// Instances built during the current apply and not yet attached to the tree, by the slot name the
	// host gave them. Cleared at the start of every batch.
	std::map<std::wstring, InstanceHandle> m_slots;
};

// Points the reader at the instance that has just been sited, and stands the one before it down. A
// reference is held for as long as it is the answering instance, so the reader cannot be left with a
// pointer to a released provider.
//
// Standing down rather than unadvising, and the difference is not a preference: a tap cannot be
// unadvised while the app goes on being inspected without emptying the diagnostics session's handle
// map underneath the tap that replaces it. What it can do is stop keeping a tree and stop acting on
// what it is told, which is where all of the cost is.
static void SetActiveTap(RoseTap* tap)
{
	RoseTap* previous = nullptr;
	{
		std::lock_guard<std::mutex> guard(g_activeMutex);
		previous = g_active;
		g_active = tap;
		if (g_active) g_active->AddRef();
	}

	if (!previous) return;

	if (previous != tap) previous->StandDown();

	previous->Release();
}

// The reader thread. One per process, started by the first injection that has a pipe to read, and it
// outlives every provider instance -- which is why it looks the instance up per request instead of
// closing over one.
static void PipeReaderLoop()
{
	std::string payload;
	while (ReadFrame(payload))
	{
		RoseTap* serving = nullptr;
		{
			std::lock_guard<std::mutex> guard(g_activeMutex);
			serving = g_active;
			if (serving) serving->AddRef();
		}

		std::string reply;
		if (serving)
		{
			// Serve reaches XAML, which throws. Uncaught it leaves a std::thread function by
			// exception, and that is std::terminate inside the app being inspected -- so a bad
			// request would take the target down rather than come back as a failed one.
			try
			{
				reply = serving->Serve(FromUtf8(payload));
			}
			catch (...)
			{
				reply.clear();
				Log(L"pipe: serving a request threw; answering empty");
			}

			serving->Release();
		}

		if (!WriteFrame(reply)) break;
	}

	// The handle goes with the loop and the flag goes back down, which is what lets a later
	// injection call ConnectPipe again. Leaving either set disables the pipe for the life of the
	// process, and every request after the first disconnect takes the file path without saying so.
	if (g_pipe != INVALID_HANDLE_VALUE)
	{
		::CloseHandle(g_pipe);
		g_pipe = INVALID_HANDLE_VALUE;
	}

	g_pipeRunning.store(false);

	// The pipe going is the other way a session ends: a host that was killed never asked to detach.
	// Idempotent, so a host that did ask and then closed the pipe behind it does not release twice.
	RoseTap* ending = nullptr;
	{
		std::lock_guard<std::mutex> guard(g_activeMutex);
		ending = g_active;
		if (ending) ending->AddRef();
	}

	if (ending)
	{
		ending->EndSession();
		ending->Release();
	}

	Log(L"pipe: reader stopped");
}

// Started at most once at a time, and startable again after a disconnect. The thread is detached
// rather than kept joinable: nothing here ever joins it, and a joinable std::thread reaching static
// destruction is std::terminate.
static void StartPipeReader()
{
	if (g_pipe == INVALID_HANDLE_VALUE) return;
	if (g_pipeRunning.exchange(true)) return;

	std::thread(PipeReaderLoop).detach();
	Log(L"pipe: reader started");
}

// A class id as text, for the log. Every handshake before SetSite identifies what it wants by a
// GUID and by nothing else, so a log line that cannot print one can only say that something
// happened.
static std::wstring GuidText(REFGUID id)
{
	wchar_t buffer[64];
	const int written = StringFromGUID2(id, buffer, ARRAYSIZE(buffer));
	return written > 0 ? std::wstring(buffer, static_cast<size_t>(written) - 1) : L"{?}";
}

class RoseTapFactory final : public IClassFactory
{
public:
	HRESULT STDMETHODCALLTYPE QueryInterface(REFIID riid, void** ppv) override
	{
		if (!ppv) return E_POINTER;
		if (riid == IID_IUnknown || riid == IID_IClassFactory)
		{
			*ppv = static_cast<IClassFactory*>(this);
			AddRef();
			return S_OK;
		}
		*ppv = nullptr;
		return E_NOINTERFACE;
	}

	ULONG STDMETHODCALLTYPE AddRef() override { return 2; }
	ULONG STDMETHODCALLTYPE Release() override { return 1; }

	HRESULT STDMETHODCALLTYPE CreateInstance(IUnknown* outer, REFIID riid, void** ppv) override
	{
		// The interface asked for is worth recording. Both frameworks ask for IObjectWithSite, and a
		// tap that answered E_NOINTERFACE here would be dropped without a word.
		Log(L"class factory: CreateInstance for " + GuidText(riid));
		if (outer) return CLASS_E_NOAGGREGATION;
		auto* tap = new (std::nothrow) RoseTap();
		if (!tap) return E_OUTOFMEMORY;
		const HRESULT hr = tap->QueryInterface(riid, ppv);
		tap->Release();
		return hr;
	}

	HRESULT STDMETHODCALLTYPE LockServer(BOOL lock) override
	{
		lock ? ++g_lockCount : --g_lockCount;
		return S_OK;
	}
};

static RoseTapFactory g_factory;

extern "C" HRESULT STDAPICALLTYPE DllGetClassObject(REFCLSID rclsid, REFIID riid, void** ppv)
{
	if (rclsid == CLSID_RoseTap)
	{
		return g_factory.QueryInterface(riid, ppv);
	}

	// Said rather than only returned, because the caller is the framework and it discards the
	// code. A provider staged under the wrong class id is otherwise indistinguishable from one
	// that was never asked for.
	Log(L"DllGetClassObject: refused " + GuidText(rclsid) + L"; this provider serves " + GuidText(CLSID_RoseTap));
	*ppv = nullptr;
	return CLASS_E_CLASSNOTAVAILABLE;
}

extern "C" HRESULT STDAPICALLTYPE DllCanUnloadNow()
{
	return g_lockCount == 0 ? S_OK : S_FALSE;
}

// The first thing the provider can observe about itself, and the reason it exists is #76.
//
// The framework loads the tap, creates it, then sites it, and a failure at any of the three is
// silent by design. Recording the load rather than inferring it puts "loaded but never created"
// and "never loaded" one line apart, where they were previously a week apart.
BOOL WINAPI DllMain(HINSTANCE instance, DWORD reason, LPVOID)
{
	if (reason == DLL_PROCESS_ATTACH)
	{
		DisableThreadLibraryCalls(instance);
		Log(L"loaded into pid " + std::to_wstring(GetCurrentProcessId()));
	}

	return TRUE;
}
