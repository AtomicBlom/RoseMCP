// RoseXamlTap: a XAML Diagnostics provider (TAP) injected into a running UWP/WinUI app.
//
// InitializeXamlDiagnosticsEx (in Windows.UI.Xaml.dll), called from the live-app host, loads this DLL
// into the target and hands the COM object below the live XAML diagnostics site. From that site we
// enumerate the visual tree via AdviseVisualTreeChange (synchronous callbacks on the app's UI thread,
// the only thread XAML may be touched from), write a snapshot of it for the host to read, and apply
// any hot-reload commands the host left for us.
//
// The provider runs in the app's AppContainer, which cannot read Program Files or write arbitrary
// paths, so its DLL, the files it reads (commands.tsv), and the ones it writes (tree.tsv, its log)
// all live in a working folder the host granted the package rights to. That folder's path arrives as
// the diagnostics initialization data.
//
// Everything the host and provider exchange goes through that folder as tab-separated files. This is
// the seed of the session channel (#2): a snapshot request/response today, a longer-lived protocol
// later. Values with tabs or newlines are escaped so a row is always one line of fixed columns.

#include <windows.h>
#include <unknwn.h>
#include <ocidl.h>

#undef GetCurrentTime

#include <xamlOM.h>

#include <atomic>
#include <string>
#include <vector>
#include <map>
#include <fstream>
#include <sstream>
#include <mutex>

// {7b9e5c10-2d4a-4f3b-9e21-a1b2c3d4e5f6}
static const CLSID CLSID_RoseXamlTap =
{ 0x7b9e5c10, 0x2d4a, 0x4f3b, { 0x9e, 0x21, 0xa1, 0xb2, 0xc3, 0xd4, 0xe5, 0xf6 } };

static std::atomic<long> g_lockCount{ 0 };
static std::wstring g_workDir;
static std::mutex g_logMutex;

static std::wstring Hex(HRESULT hr)
{
	wchar_t buffer[9];
	swprintf_s(buffer, L"%08x", static_cast<unsigned>(hr));
	return buffer;
}

static void Log(const std::wstring& line)
{
	OutputDebugStringW((L"[RoseXamlTap] " + line + L"\n").c_str());

	std::lock_guard<std::mutex> guard(g_logMutex);
	if (g_workDir.empty()) return;

	std::wofstream file(g_workDir + L"\\rosexamltap.log", std::ios::app);
	if (file) file << line << L"\n";
}

// A tab or newline in a type or name would break the row-per-element snapshot; keep every field on
// one line and reversible.
static std::wstring Escape(const wchar_t* text)
{
	std::wstring result;
	if (!text) return result;
	for (const wchar_t* c = text; *c; ++c)
	{
		switch (*c)
		{
			case L'\t': result += L"\\t"; break;
			case L'\r': result += L"\\r"; break;
			case L'\n': result += L"\\n"; break;
			case L'\\': result += L"\\\\"; break;
			default: result += *c; break;
		}
	}

	return result;
}

// One element of the visual tree, captured as it is announced.
struct TreeNode
{
	InstanceHandle Handle;
	InstanceHandle Parent;
	unsigned int ChildIndex;
	std::wstring Type;
	std::wstring Name;
};

// One parsed command line: op TAB target TAB property TAB valueType TAB value
struct Command
{
	std::wstring op;
	std::wstring target;
	std::wstring property;
	std::wstring valueType;
	std::wstring value;
};

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

		// Enumerate the tree (synchronous callbacks on this thread) to build the snapshot and name
		// map, then write the snapshot and run any commands. All on the UI thread, where XAML lives.
		hr = m_tree->AdviseVisualTreeChange(this);
		Log(L"enumerated " + std::to_wstring(m_nodes.size()) + L" element(s) (advise hr=0x" + Hex(hr) + L")");
		WriteTreeSnapshot();
		ApplyCommands();
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
		if (mutationType != Add) return S_OK;

		m_nodes.push_back({ element.Handle, relation.Parent, relation.ChildIndex,
			element.Type ? element.Type : L"", element.Name ? element.Name : L"" });

		if (element.Name && element.Name[0])
		{
			m_byName[element.Name] = element.Handle;
		}

		return S_OK;
	}

private:
	// One row per element: Handle, Parent, ChildIndex, Type, Name. Written to a temp file and renamed
	// so the host never reads a half-written snapshot; a ".ready" marker is the host's signal.
	void WriteTreeSnapshot()
	{
		if (g_workDir.empty()) return;

		const std::wstring finalPath = g_workDir + L"\\tree.tsv";
		const std::wstring tempPath = finalPath + L".tmp";
		{
			std::wofstream file(tempPath, std::ios::trunc);
			if (!file)
			{
				Log(L"could not open tree.tsv.tmp for writing");
				return;
			}

			for (const auto& node : m_nodes)
			{
				file << node.Handle << L'\t' << node.Parent << L'\t' << node.ChildIndex << L'\t'
					<< Escape(node.Type.c_str()) << L'\t' << Escape(node.Name.c_str()) << L'\n';
			}
		}

		_wremove(finalPath.c_str());
		if (_wrename(tempPath.c_str(), finalPath.c_str()) != 0)
		{
			Log(L"could not rename tree.tsv.tmp to tree.tsv");
			return;
		}

		std::wofstream ready(g_workDir + L"\\tree.ready", std::ios::trunc);
		if (ready) ready << m_nodes.size() << L"\n";
		Log(L"wrote tree.tsv with " + std::to_wstring(m_nodes.size()) + L" element(s)");
	}

	void ApplyCommands()
	{
		const std::vector<Command> commands = ReadCommands();
		if (commands.empty()) return;

		Log(L"applying " + std::to_wstring(commands.size()) + L" command(s)");
		for (const auto& command : commands)
		{
			if (command.op == L"SetProperty")
			{
				ApplySetProperty(command);
			}
			else if (command.op == L"ClearProperty")
			{
				ApplyClearProperty(command);
			}
			else
			{
				Log(L"  skipped unsupported op '" + command.op + L"'");
			}
		}
	}

	void ApplySetProperty(const Command& command)
	{
		InstanceHandle target = 0;
		if (!Find(command.target, target)) return;

		unsigned int index = 0;
		if (!PropertyIndex(target, command.property, index))
		{
			Log(L"  SetProperty " + command.target + L"." + command.property + L": property not found on element");
			return;
		}

		BSTR typeName = SysAllocString(command.valueType.c_str());
		BSTR value = SysAllocString(command.value.c_str());
		InstanceHandle valueHandle = 0;
		HRESULT hr = m_tree->CreateInstance(typeName, value, &valueHandle);
		SysFreeString(typeName);
		SysFreeString(value);
		if (FAILED(hr))
		{
			Log(L"  SetProperty " + command.target + L"." + command.property + L": CreateInstance(" + command.valueType + L", " + command.value + L") failed hr=0x" + Hex(hr));
			return;
		}

		hr = m_tree->SetProperty(target, valueHandle, index);
		Log(hr == S_OK
			? L"  set " + command.target + L"." + command.property + L" = " + command.value
			: L"  SetProperty " + command.target + L"." + command.property + L" failed hr=0x" + Hex(hr));
	}

	void ApplyClearProperty(const Command& command)
	{
		InstanceHandle target = 0;
		if (!Find(command.target, target)) return;

		unsigned int index = 0;
		if (!PropertyIndex(target, command.property, index))
		{
			Log(L"  ClearProperty " + command.target + L"." + command.property + L": property not found");
			return;
		}

		HRESULT hr = m_tree->ClearProperty(target, index);
		Log(hr == S_OK
			? L"  cleared " + command.target + L"." + command.property
			: L"  ClearProperty " + command.target + L"." + command.property + L" failed hr=0x" + Hex(hr));
	}

	bool Find(const std::wstring& name, InstanceHandle& handle)
	{
		const auto it = m_byName.find(name);
		if (it == m_byName.end())
		{
			Log(L"  target '" + name + L"' not found in the live tree");
			return false;
		}

		handle = it->second;
		return true;
	}

	bool PropertyIndex(InstanceHandle handle, const std::wstring& name, unsigned int& index)
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
			fields.resize(5);
			commands.push_back({ fields[0], fields[1], fields[2], fields[3], fields[4] });
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
	std::map<std::wstring, InstanceHandle> m_byName;
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
	if (rclsid == CLSID_RoseXamlTap)
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
