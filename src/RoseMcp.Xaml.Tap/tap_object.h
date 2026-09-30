#pragma once

// The COM object the diagnostics site talks to, its class factory, and the two DLL exports.
//
// What is left here is being sited, the visual-tree callbacks, and dispatching a request: the parts
// that are about holding the framework's interfaces and the pipe, rather than about the tree or an
// edit. All of it is xamlOM ABI, which UWP and WinUI 3 implement identically, so it names no
// projection and is compiled above the provider's alias block. That placement is the check rather
// than the claim: an xaml:: anything here fails to compile.
//
// Three collaborators hold the rest. TapTree is the node list, the name index, the batch's slots and
// the addressing over them, none of which touches the framework at all. TapProperties reads a
// property chain. TapEdits applies a batch, asking the first two for the target and the property's
// index. They are separate because the questions are: a tree read is answerable from rows already
// handed over, a property read costs a call into the framework, and an edit changes somebody else's
// running application and cannot be retried.
//
// The two things that reach into the projected world are declared in tap_surface.h and defined
// below the aliases -- the overlay, through IRoseOverlay, and the four reads that need a concrete
// type, in tap_render.h. Both speak in handles and strings, so neither drags this file down a tier.
//
// Needs CLSID_RoseTap defined by the provider: the class id is the one piece of genuine identity
// here, since it is what the host's injection names and what two providers must not share.

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

		// "<work dir>|<pipe name>|<key>". Split on '|' because it cannot occur in a Windows path, so the
		// work dir cannot contain one. The key is what the greeting presents; a provider given none
		// greets with an empty one, and the host refuses it by saying so rather than by timing out.
		const size_t bar = data.find(L'|');
		if (bar == std::wstring::npos)
		{
			g_workDir = data;
		}
		else
		{
			g_workDir = data.substr(0, bar);

			const size_t second = data.find(L'|', bar + 1);
			g_pipeName = data.substr(bar + 1, second == std::wstring::npos ? std::wstring::npos : second - bar - 1);
			g_pipeNonce = second == std::wstring::npos ? std::wstring() : data.substr(second + 1);
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

		m_diagnostics->QueryInterface(__uuidof(IVisualTreeService), reinterpret_cast<void**>(&m_service));
		if (!m_service)
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
			candidates.reserve(m_tree.Count());
			for (const auto& node : m_tree.Nodes()) candidates.push_back(node.Handle);

			Overlay().Install(m_diagnostics, candidates);

			// Per-element source info only exists here, where the tree was walked, so it is handed to the
			// overlay: it is what "just my XAML" decides on, and a click has no other way to learn it.
			std::map<InstanceHandle, std::wstring> sources;
			for (const auto& node : m_tree.Nodes())
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
		const HRESULT hr = m_service->AdviseVisualTreeChange(this);
		Log(L"enumerated " + std::to_wstring(m_tree.Count()) + L" element(s) (advise hr=0x" + Hex(hr) + L")");
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
			if (!RoseTapRunOnUiThread([&] { rows = m_tree.SnapshotRows(written); })) return std::string();

			Log(L"pipe: served tree with " + std::to_wstring(written) + L" element(s)");
			return rows;
		}

		if (request.rfind(L"properties ", 0) == 0)
		{
			// Same parse as the injected path: a trailing " all" asks for the framework defaults too.
			const bool includeDefaults = request.size() >= 4 && request.compare(request.size() - 4, 4, L" all") == 0;
			const InstanceHandle handle = static_cast<InstanceHandle>(_wcstoui64(request.c_str() + 11, nullptr, 10));

			std::string reply;
			if (!RoseTapRunOnUiThread([&] { reply = m_properties.Reply(handle, includeDefaults); })) return std::string();

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
				// hit-test, "nomyxaml" turns off the preference for the app's own markup, and "rulers"
				// asks for the measuring mode rather than the picking one. A flag the person set on the
				// toolbar is left alone unless the request actually mentions it.
				bool includeAll = false;
				bool rulers = false;
				for (const auto& token : Tokens(request))
				{
					if (token == L"all") includeAll = true;
					else if (token == L"rulers") rulers = true;
					else if (token == L"myxaml") Overlay().SetJustMyXaml(true);
					else if (token == L"nomyxaml") Overlay().SetJustMyXaml(false);
				}

				// Both modes lay the same pointer-capturing layer over the app, so both answer with the
				// extent it was arranged at. includeAll is what a pick resolves through and rulers picks
				// its anchor the same way, so it is set either way.
				if (rulers) Overlay().BeginRulers(includeAll);
				else Overlay().BeginSelect(includeAll);
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
			if (!RoseTapRunOnUiThread([&] { Overlay().GoIdle(); })) return std::string();
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
			// The mode on the first line, then the candidate rows. All three in one reply, because read
			// separately they can describe a state that never existed at any instant. Asked for rather than
			// pushed: a pick outlives the request that armed it by design, because the person clicks when
			// they click.
			std::string reply;
			if (!RoseTapRunOnUiThread([&]
			{
				// The mode by name rather than a flag for one of them. Rulers captures the pointer
				// exactly as select does, so a host that only knew about select would report an app it
				// cannot click as idle.
				reply = Utf8(Overlay().ModeName())
					+ "\t" + (Overlay().JustMyXaml() ? "1" : "0")
					+ "\t" + Utf8(Escape(Overlay().GoneReason().c_str())) + "\n"
					+ Overlay().SelectionRows();
			})) return std::string();

			return reply;
		}

		if (request == L"apply" || request.rfind(L"apply\n", 0) == 0)
		{
			// The batch rides in the frame, one command per line after the verb. A frame is UTF-8 at both
			// ends, so the batch needs no encoding decision of its own.
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
			if (!RoseTapRunOnUiThread([&] { rows = m_edits.Apply(commands); })) return std::string();

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

		Log(L"pipe: no handler for '" + request + L"'; answering empty");
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
			m_tree.ForgetSubtree(element.Handle);
			return S_OK;
		}

		// SrcInfo comes per element and was previously dropped on the floor. It is what tells an
		// element the developer wrote from one a control template produced, which is the whole of
		// "just my XAML" -- and it is a different field from PropertyChainSource::SrcInfo, so the
		// two can be populated independently. Empty is recorded as empty; absent source info must
		// not be reported as "declared nowhere".
		m_tree.Add({ element.Handle, relation.Parent, relation.ChildIndex,
			element.Type ? element.Type : L"", element.Name ? element.Name : L"",
			element.SrcInfo.FileName ? element.SrcInfo.FileName : L"",
			element.SrcInfo.LineNumber, element.SrcInfo.ColumnNumber });

		return S_OK;
	}

private:
	// Sets the flag and gives back what the tap was holding for the tree it no longer answers about,
	// which is where all of a superseded tap's cost was.
	void Retire()
	{
		m_retired = true;

		m_tree.Forget();
	}

	// Gives back the framework's two interfaces, and says whether there was anything to give back.
	//
	// Unadvising first is not tidiness: releasing m_service while the framework still holds this tap as
	// a visual-tree callback leaves it calling into an object holding a dangling pointer.
	//
	// The overlay is untouched by this. It takes its own reference to IXamlDiagnostics when it is
	// installed, precisely so a click can resolve to a handle long after the injection that drew it
	// is over, so the toolbar outlives the release rather than being broken by it.
	bool Unadvise()
	{
		const bool held = m_service != nullptr || m_diagnostics != nullptr;

		if (m_service) m_service->UnadviseVisualTreeChange(this);
		if (m_service) { m_service->Release(); m_service = nullptr; }
		if (m_diagnostics) { m_diagnostics->Release(); m_diagnostics = nullptr; }

		return held;
	}

	std::atomic<long> m_refs{ 1 };
	// Whether a later injection has taken over. Read and written on the UI thread alone, which is where
	// the callbacks it guards arrive.
	bool m_retired = false;
	IXamlDiagnostics* m_diagnostics = nullptr;
	IVisualTreeService* m_service = nullptr;

	// The tree this tap knows about, and every question asked of it. Kept behind one object because
	// the node list, the name index and the batch's slots have to agree: a removal has to leave all
	// three, and an address is a position among siblings, so a list that disagrees with the exclusion
	// the snapshot applies hands out addresses resolving to the element next door.
	TapTree m_tree;

	// The two collaborators, holding the interface pointers above by reference rather than copying
	// them. One authoritative pointer per interface, assigned when this tap is sited and released
	// when it gives them back: a copy in either of these would be a second lifetime to keep in step,
	// and the way that fails is a stale pointer used after the release rather than a compile error.
	//
	// Declared after what they refer to, since a member's initialiser runs in declaration order.
	TapProperties m_properties{ m_diagnostics, m_service };
	TapEdits m_edits{ m_diagnostics, m_service, m_tree, m_properties };
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
		// A frame with no id cannot be answered under one, so it is answered empty, which is how every
		// host reads "not served here". Nothing this provider's host sends is shaped like that.
		std::string id;
		std::string request;
		if (!SplitRequest(payload, id, request))
		{
			Log(L"pipe: a request arrived without an id; answering empty");
			if (!WriteFrame(std::string())) break;
			continue;
		}

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
				reply = serving->Serve(FromUtf8(request));
			}
			catch (...)
			{
				reply.clear();
				Log(L"pipe: serving a request threw; answering empty");
			}

			serving->Release();
		}

		if (!WriteFrame(id + "\n" + reply)) break;
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
