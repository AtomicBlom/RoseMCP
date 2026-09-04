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
		if (FAILED(hr)) return hr;

		BSTR initData = nullptr;
		if (SUCCEEDED(m_diagnostics->GetInitializationData(&initData)) && initData)
		{
			g_workDir.assign(initData, SysStringLen(initData));
			SysFreeString(initData);
		}

		m_diagnostics->QueryInterface(__uuidof(IVisualTreeService), reinterpret_cast<void**>(&m_tree));
		if (!m_tree)
		{
			Log(L"SetSite: no IVisualTreeService");
			return E_NOINTERFACE;
		}

		// Read the request first, because reading it is also what learns the generation it carries --
		// and the tree snapshot below is written before anything is dispatched, so it would otherwise
		// be stamped with the *previous* request's number and rejected by the host as stale. Not
		// hypothetical: it is what the continuous-apply test caught within the hour of the stamp being
		// added, and it presented as a tree read that timed out and blamed the app's diagnostics layer.
		const std::wstring request = ReadRequest();

		// Enumerate the tree (synchronous callbacks on this thread) to build the snapshot and name
		// map, then serve the host's request and run any commands. All on the UI thread, where XAML
		// lives -- which is why each request re-injects rather than being answered off a worker thread.
		hr = m_tree->AdviseVisualTreeChange(this);
		Log(L"enumerated " + std::to_wstring(m_nodes.size()) + L" element(s) (advise hr=0x" + Hex(hr) + L")");
		WriteTreeSnapshot();

		// The toolbar is installed once and left there. It goes in after the snapshot so the very first
		// tree cannot contain it, and the snapshot filters it out of every one after that.
		Overlay().Install(m_diagnostics);

		// Per-element source info only exists here, where the tree was walked, so it is handed to the
		// overlay: it is what "just my XAML" decides on, and a click has no other way to learn it.
		std::map<InstanceHandle, std::wstring> sources;
		for (const auto& node : m_nodes)
		{
			if (!node.File.empty()) sources[node.Handle] = node.File;
		}

		Overlay().SetSources(std::move(sources));

		if (request.rfind(L"properties ", 0) == 0)
		{
			// "properties <handle>" gives the set (non-default) properties; a trailing " all" includes
			// the framework defaults too. Filtering defaults out keeps the interesting values from being
			// pushed past the row cap on an element with hundreds of properties.
			const bool includeDefaults = request.size() >= 4 && request.compare(request.size() - 4, 4, L" all") == 0;
			WriteProperties(static_cast<InstanceHandle>(_wcstoui64(request.c_str() + 11, nullptr, 10)), includeDefaults);
		}
		else if (request.rfind(L"selecthandle ", 0) == 0)
		{
			// Checked before the arming verb below, and named without a space after "select" so the
			// two cannot be confused: arming parses its tokens as flags, and a handle is not one.
			Overlay().SelectByHandle(static_cast<InstanceHandle>(_wcstoui64(request.c_str() + 13, nullptr, 10)));
		}
		else if (request == L"deselect")
		{
			// The same act as the toolbar button, so the mark and the recorded selection go together
			// whichever end asks. An agent that has finished with an element, and #51's tree watcher
			// noticing the element is gone, both want exactly this.
			Overlay().Deselect();
		}
		else if (request == L"apply")
		{
			ApplyCommands();
		}
		else if (request == L"select" || request.rfind(L"select ", 0) == 0)
		{
			// Arming from the agent and arming from the toolbar are the same act; whichever happens,
			// the overlay writes select.ready and the host reads the pick back the same way.
			//
			// Tokenised rather than suffix-matched: "all" asks for elements the framework would not
			// hit-test (explicit, never the default -- see Beneath), and "nomyxaml" turns off the
			// preference for the app's own markup. A flag the person set on the toolbar is left alone
			// unless the request actually mentions it.
			bool includeAll = false;
			for (const auto& token : Tokens(request))
			{
				if (token == L"all") includeAll = true;
				else if (token == L"myxaml") Overlay().SetJustMyXaml(true);
				else if (token == L"nomyxaml") Overlay().SetJustMyXaml(false);
			}

			Overlay().BeginSelect(includeAll);
		}

		// Last, and for every request rather than only the ones that change the mode. The state file
		// is how the host asks what the toolbar is doing, and its answer is only usable if the host
		// can tell it was written for the question just asked -- so every injection leaves that proof
		// behind, including a tree or properties read that touches the mode not at all.
		Overlay().RefreshState();

		return S_OK;
	}

	HRESULT STDMETHODCALLTYPE GetSite(REFIID riid, void** ppv) override
	{
		if (!m_diagnostics) return E_FAIL;
		return m_diagnostics->QueryInterface(riid, ppv);
	}

	HRESULT STDMETHODCALLTYPE OnVisualTreeChange(
		ParentChildRelation relation, VisualElement element, VisualMutationType mutationType) override
	{
		if (mutationType != Add)
		{
			// A selection whose element has left the tree is stale in both halves -- the mark drawn
			// over the app and the handle the host will keep calling with -- so the overlay is told.
			// It matches on the handle and does nothing when it is not the selected one, which is
			// what makes this safe to run once per advised tap (#68).
			Overlay().ClearIfRemoved(element.Handle);
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
	void WriteTreeSnapshot()
	{
		if (g_workDir.empty()) return;

		const auto excluded = OverlaySubtree();

		const std::wstring finalPath = g_workDir + L"\\tree.tsv";
		const std::wstring tempPath = finalPath + L".tmp";
		size_t written = 0;
		{
			std::ofstream file(tempPath.c_str(), std::ios::trunc | std::ios::binary);
			if (!file)
			{
				Log(L"could not open tree.tsv.tmp for writing");
				return;
			}

			// Each element's address, computed once for the whole snapshot rather than per row. It is
			// reported because it is the only way to address an element the markup never named, and
			// an unnamed element is the ordinary case for a click that lands inside a template.
			const auto paths = ComputePaths();

			for (const auto& node : m_nodes)
			{
				if (excluded.count(node.Handle)) continue;

				const auto address = paths.find(node.Handle);
				const std::wstring path = address != paths.end() ? address->second : std::wstring();

				const std::wstring row = std::to_wstring(node.Handle) + L'\t' + std::to_wstring(node.Parent) + L'\t'
					+ std::to_wstring(node.ChildIndex) + L'\t' + Escape(node.Type.c_str()) + L'\t' + Escape(node.Name.c_str())
					+ L'\t' + Escape(node.File.c_str()) + L'\t' + std::to_wstring(node.Line) + L'\t' + std::to_wstring(node.Column)
					+ L'\t' + Escape(path.c_str());
				file << Utf8(row) << '\n';
				written++;
			}
		}

		_wremove(finalPath.c_str());
		if (_wrename(tempPath.c_str(), finalPath.c_str()) != 0)
		{
			Log(L"could not rename tree.tsv.tmp to tree.tsv");
			return;
		}

		WriteMarker(L"tree.ready", std::to_wstring(written));
		Log(L"wrote tree.tsv with " + std::to_wstring(written) + L" element(s)");
	}

	// The handles of our own toolbar's elements, empty whenever the layer is not enumerated at all.
	// Enumeration is parent-before-child in practice, but this closes over the subtree rather than
	// assuming it, since one missed pass would leak our UI into the answer.
	std::set<InstanceHandle> OverlaySubtree() const
	{
		std::set<InstanceHandle> excluded;
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

	// The host writes one line saying what it wants of this injection (a tree is always written; a
	// "properties <handle>" line asks for that element's property chain as well).
	// The request is the first line, and the host's generation for it the second. Two lines of one
	// file rather than a file each, because the first line is all the verb parsing has ever read --
	// so a second line costs nothing and cannot disturb it, and "properties <handle> all" still
	// matches on its own suffix.
	std::wstring ReadRequest()
	{
		g_generation.clear();

		if (g_workDir.empty()) return std::wstring();

		std::wifstream file(g_workDir + L"\\request.txt");
		std::wstring line;
		if (!file || !std::getline(file, line)) return std::wstring();

		if (!line.empty() && line.back() == L'\r') line.pop_back();

		std::wstring generation;
		if (std::getline(file, generation))
		{
			if (!generation.empty() && generation.back() == L'\r') generation.pop_back();
			g_generation = generation;
		}

		return line;
	}

	// One element's property chain: every effective (non-overridden) value with its type, provenance
	// (default/style/local/...), and the source location that set it, plus an element row carrying its
	// type and its own declaration site. Source locations are populated only when the app carries XAML
	// source info; otherwise those fields are empty and the caller degrades to provenance alone.
	/// Turns a handle to a SolidColorBrush into #AARRGGBB, leaving anything else alone.
	///
	/// Reads a CornerRadius off the element itself, because XAML diagnostics renders it as nothing.
	///
	/// Both are structs, both are set by the same markup, and only one comes back with a value:
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

	void WriteProperties(InstanceHandle handle, bool includeDefaults)
	{
		if (g_workDir.empty()) return;

		unsigned int sourceCount = 0;
		unsigned int valueCount = 0;
		PropertyChainSource* sources = nullptr;
		PropertyChainValue* values = nullptr;
		const HRESULT hr = m_tree->GetPropertyValuesChain(handle, &sourceCount, &sources, &valueCount, &values);
		if (FAILED(hr))
		{
			Log(L"GetPropertyValuesChain(" + std::to_wstring(handle) + L") failed hr=0x" + Hex(hr));
			WriteMarker(L"properties.ready", L"error");
			return;
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

		const std::wstring finalPath = g_workDir + L"\\properties.tsv";
		const std::wstring tempPath = finalPath + L".tmp";
		unsigned int written = 0;
		{
			std::ofstream file(tempPath.c_str(), std::ios::trunc | std::ios::binary);
			if (!file)
			{
				Log(L"could not open properties.tsv.tmp for writing");
				return;
			}

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
				if (emptyButNotNull && std::wcscmp(declaredType, L"Windows.UI.Xaml.CornerRadius") == 0
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

		_wremove(finalPath.c_str());
		if (_wrename(tempPath.c_str(), finalPath.c_str()) != 0)
		{
			Log(L"could not rename properties.tsv.tmp to properties.tsv");
			return;
		}

		WriteMarker(L"properties.ready", std::to_wstring(written));
		Log(L"wrote properties.tsv with " + std::to_wstring(written) + L" propert(y/ies) for handle " + std::to_wstring(handle));

		FreePropertyChain(sources, sourceCount, values, valueCount);
	}

	// Applies each command from commands.tsv and writes apply.tsv -- one row per command with its
	// outcome (applied / target not found / property not found / a failure code) -- so the host can
	// report per-command results to the agent (#12).
	void ApplyCommands()
	{
		if (g_workDir.empty()) return;

		const std::vector<Command> commands = ReadCommands();
		Log(L"applying " + std::to_wstring(commands.size()) + L" command(s)");

		// Slots live for one batch and no longer. They name instances that have been built but not
		// yet attached to anything, which is what lets a nested element be created, filled and then
		// handed to its parent -- and a slot surviving into the next apply would let one batch's
		// half-built element be reached by another's command.
		m_slots.clear();

		const std::wstring finalPath = g_workDir + L"\\apply.tsv";
		const std::wstring tempPath = finalPath + L".tmp";
		{
			std::ofstream file(tempPath.c_str(), std::ios::trunc | std::ios::binary);
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

				if (file)
				{
					// The arg goes on the end, after the status, so a reader of the older four-field row
					// is unaffected. It is there because the host keys these results by what it sent,
					// and op-target-property alone stops being unique the moment one slot gets two
					// children: both rows would be "AddChild <slot> <blank>", and the second child's
					// outcome would overwrite the first's.
					const std::wstring row = command.op + L'\t' + Escape(command.target.c_str()) + L'\t'
						+ Escape(command.property.c_str()) + L'\t' + status + L'\t' + Escape(command.arg.c_str());
					file << Utf8(row) << '\n';
				}
			}
		}

		_wremove(finalPath.c_str());
		if (_wrename(tempPath.c_str(), finalPath.c_str()) != 0)
		{
			Log(L"could not rename apply.tsv.tmp to apply.tsv");
			return;
		}

		WriteMarker(L"apply.ready", std::to_wstring(commands.size()));
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
			L"Windows.UI.Xaml.Controls.", L"Windows.UI.Xaml.Shapes.",
			L"Windows.UI.Xaml.Media.", L"Windows.UI.Xaml." })
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
		std::set<InstanceHandle> doomed{ root };
		for (bool grew = true; grew; )
		{
			grew = false;
			for (const auto& node : m_nodes)
			{
				if (doomed.count(node.Handle)) continue;
				if (doomed.count(node.Parent) == 0) continue;

				doomed.insert(node.Handle);
				grew = true;
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

	std::vector<Command> ReadCommands()
	{
		std::vector<Command> commands;
		if (g_workDir.empty()) return commands;

		std::wifstream file(g_workDir + L"\\commands.tsv");
		if (!file) return commands;

		std::wstring line;
		while (std::getline(file, line))
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

	void Unadvise()
	{
		if (m_tree) m_tree->UnadviseVisualTreeChange(this);
		if (m_tree) { m_tree->Release(); m_tree = nullptr; }
		if (m_diagnostics) { m_diagnostics->Release(); m_diagnostics = nullptr; }
	}

	std::atomic<long> m_refs{ 1 };
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
	*ppv = nullptr;
	return CLASS_E_CLASSNOTAVAILABLE;
}

extern "C" HRESULT STDAPICALLTYPE DllCanUnloadNow()
{
	return g_lockCount == 0 ? S_OK : S_FALSE;
}
