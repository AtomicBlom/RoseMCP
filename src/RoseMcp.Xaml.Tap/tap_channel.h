#pragma once

// The provider's channel to the host: the work folder, the generation stamp, the log, and the
// escaping and tokenising that keep a work-folder file one line of fixed columns.
//
// Framework-free on purpose, and that is the whole value of the layer. No xamlOM, no WinRT
// projection, nothing Windows-XAML about it -- so this is what a provider for a stack with no COM
// and no injection would implement unchanged. Anything framework-bound put here is a compile error,
// which is the check that the boundary is real rather than asserted: TreeNode belongs next door
// precisely because it holds an InstanceHandle.
//
// The including provider defines RoseTapName and RoseTapLogFile before this header. Two providers
// serving the same machine write adjacent work folders, and a log that named neither of them would
// interleave two apps' diagnostics in one file.

#include <windows.h>

#include <atomic>
#include <string>
#include <vector>
#include <map>
#include <set>
#include <algorithm>
#include <functional>
#include <fstream>
#include <sstream>
#include <mutex>
#include <cstdlib>
#include <cmath>
#include <chrono>

static std::atomic<long> g_lockCount{ 0 };
static std::wstring g_workDir;

// The host's number for the request being served, echoed into everything written back so the host
// can tell a file this request produced from one left behind by the last (#57).
//
// It is needed because every handshake here was "does this file exist", and the host clears the
// marker before injecting -- so the moment that clear silently fails, the wait is satisfied by the
// *previous* request's marker and the host reads an answer written before it asked the question.
// Existence cannot distinguish those; a number the host chose can. Carried as the text the host
// wrote rather than parsed, since nothing on this side has any reason to do arithmetic on it.
static std::wstring g_generation;
static std::mutex g_logMutex;

// A ".ready" marker, plus the generation of the request it answers.
//
// One function for all of them so the next one added cannot forget the stamp, which is exactly how
// this got left half done: #57 gave the overlay's markers a generation and the tree, properties and
// apply handshakes kept answering on existence alone (#89). Continuous hot reload is what makes that
// matter -- a stale apply.ready reports the *previous* apply's per-edit outcomes as this one's, and
// in a loop that applies the same property over and over the keys line up, so it reads as success.
//
// Deliberately not used for selection.ready. That file records a click, which outlives the injection
// that armed select mode by design, so stamping it with the generation current when the click
// happened would have the read that goes looking for it reject its own answer as stale. The two
// needs are contradictory in one file; separating them is the fix, and it is not this one.
static void WriteMarker(const std::wstring& name, const std::wstring& payload)
{
	if (g_workDir.empty()) return;

	std::wofstream marker(g_workDir + L"\\" + name, std::ios::trunc);
	if (!marker) return;

	marker << payload;
	if (!g_generation.empty()) marker << L" gen=" << g_generation;
	marker << L"\n";
}

static std::wstring Hex(HRESULT hr)
{
	wchar_t buffer[9];
	swprintf_s(buffer, L"%08x", static_cast<unsigned>(hr));
	return buffer;
}

static std::string Utf8(const std::wstring& text);

// UTF-8, for the same reason the snapshots are: a wofstream narrows to the ANSI code page, so an
// element name or a separator outside it lands in the log as a question mark. A diagnostic file that
// mangles the very names it exists to report is worth the one extra call.
static void Log(const std::wstring& line)
{
	OutputDebugStringW((L"[" + std::wstring(RoseTapName) + L"] " + line + L"\n").c_str());

	std::lock_guard<std::mutex> guard(g_logMutex);
	if (g_workDir.empty()) return;

	std::ofstream file(g_workDir + RoseTapLogFile, std::ios::app | std::ios::binary);
	if (file) file << Utf8(line) << '\n';
}

// The snapshot is UTF-8 so the host reads it with one fixed encoding regardless of the app's locale;
// std::wofstream would narrow to the ANSI code page and lose any non-ASCII name.
static std::string Utf8(const std::wstring& text)
{
	if (text.empty()) return std::string();
	const int size = WideCharToMultiByte(CP_UTF8, 0, text.c_str(), static_cast<int>(text.size()), nullptr, 0, nullptr, nullptr);
	std::string out(static_cast<size_t>(size), '\0');
	WideCharToMultiByte(CP_UTF8, 0, text.c_str(), static_cast<int>(text.size()), out.data(), size, nullptr, nullptr);
	return out;
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

// One parsed command line: op TAB target TAB property TAB valueType TAB value
struct Command
{
	std::wstring op;
	std::wstring target;
	std::wstring property;
	std::wstring valueType;
	std::wstring value;

	// Structural commands need a second thing named and a position for it: AddChild says which slot
	// holds the child and where in its new parent it goes. Given their own fields rather than folded
	// into property and value, because the result rows are keyed on op, target and property, and
	// having "property" mean a child slot in one row and a property name in the next would make those
	// keys mean two different things.
	std::wstring arg;
	unsigned int index = 0;
};

// Splits a request line on spaces, dropping the leading verb. Tokenised because matching a suffix
// gets the wrong answer the moment there are two flags.
static std::vector<std::wstring> Tokens(const std::wstring& request)
{
	std::vector<std::wstring> tokens;
	std::wistringstream stream(request);
	std::wstring token;
	while (stream >> token)
	{
		tokens.push_back(token);
	}

	if (!tokens.empty()) tokens.erase(tokens.begin());
	return tokens;
}
