#pragma once

// The tree as the tap knows it, and how to address into it.
//
// Three containers and the questions asked of them: the node list the visual-tree callbacks
// maintain, an index from name to handle, and the slots a batch builds into. Nothing here reaches
// the framework -- no IXamlDiagnostics, no IVisualTreeService, no projection -- because addressing
// is arithmetic over a list of rows that were handed to it, and that is worth being able to read,
// and eventually test, without an app running.
//
// It is a class rather than a group of functions because the three containers have to agree.
// Forgetting a subtree has to drop the same handles from the node list and from the name index, and
// an address is a position among siblings, so a node list that disagrees with the exclusion the
// snapshot applies hands out addresses that resolve to the element next door. Keeping them behind
// one surface is what makes those two facts checkable in one place.

#include "tap_surface.h"

#include <algorithm>
#include <map>
#include <set>
#include <string>
#include <vector>

class TapTree
{
public:
	// Appends an element and indexes it by name. The list follows the tree rather than only growing:
	// a resident tap answers reads between injections, and a walk per injection is what used to act
	// as the refresh.
	void Add(TreeNode node)
	{
		if (!node.Name.empty()) m_byName[node.Name].push_back(node.Handle);

		m_nodes.push_back(std::move(node));
	}

	// Drops an element and everything beneath it from the node list and the name map.
	//
	// Closed over rather than assumed one level deep: removing a Border removes the TextBlock inside
	// it, and leaving those descendants behind would leave addresses that resolve to elements the
	// framework has already let go. Dropping a subtree that is already gone is a no-op, which is what
	// makes it safe to call from both the framework's notification and the edit that caused one.
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

	// Gives the whole tree back, which is where all of a stood-down tap's cost was. The capacity goes
	// with it: a vector that has held a few thousand elements keeps the allocation until it is told
	// otherwise, and a superseded tap holding one is the leak this avoids.
	void Forget()
	{
		m_nodes.clear();
		m_nodes.shrink_to_fit();
		m_byName.clear();
		m_slots.clear();
	}

	size_t Count() const { return m_nodes.size(); }

	// The rows as enumerated, for the two callers that want the elements themselves rather than an
	// answer about them: the handles the overlay is installed against, and the handle-to-source map
	// behind "just my XAML".
	const std::vector<TreeNode>& Nodes() const { return m_nodes; }

	// The snapshot's rows, UTF-8, newline-separated. Separated from writing them so the same bytes
	// can go down the pipe (#50) or into tree.tsv, rather than one of the two being built a second
	// way and drifting -- the address column is exactly the sort of thing that would drift.
	//
	// One row per element: Handle, Parent, ChildIndex, Type, Name, File, Line, Column, address.
	std::string SnapshotRows(size_t& written) const
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

	// Resolves a target to exactly one element, or says why it could not. The reason travels back to
	// the agent as that edit's status: a bare "target not found" sent a caller looking for a mistake
	// in their address when the tree simply held two elements of that name, which is a different
	// problem with a different fix.
	//
	// A bare name and a path are both accepted. Anything with no brackets and no slash is a name --
	// including the #name an address is written with, so one string works whether it came from a
	// diff, from the tree, or from somebody typing it.
	std::wstring Resolve(const std::wstring& target, InstanceHandle& handle) const
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

	// The parent of an element as the snapshot has it, false when the element is not in the snapshot
	// at all. Asked rather than read off the index, because the exclusion the index applies is the
	// reason an element can be absent and a caller reaching past that would be reading a different
	// tree from the one addresses are computed against.
	bool ParentOf(InstanceHandle child, InstanceHandle& parent) const
	{
		const TreeIndex index = BuildIndex();
		const auto found = index.ByHandle.find(child);
		if (found == index.ByHandle.end()) return false;

		parent = m_nodes[found->second].Parent;
		return true;
	}

	// Every fully-qualified type name in the tree whose local name matches, in the order met.
	//
	// The tree is the evidence for what a namespace-less type name means: an app using a Border has
	// one in it, spelled the way this framework spells it. That beats guessing from a fixed list of
	// namespaces, which is the fallback rather than the first answer.
	std::vector<std::wstring> QualifiedTypesNamed(const std::wstring& localType) const
	{
		std::vector<std::wstring> qualified;

		for (const auto& node : m_nodes)
		{
			if (LocalType(node.Type) != localType) continue;
			if (std::find(qualified.begin(), qualified.end(), node.Type) != qualified.end()) continue;

			qualified.push_back(node.Type);
		}

		return qualified;
	}

	// Names something a batch has built and not yet attached. Slots live for one batch, because an
	// unattached instance the next batch could still name would be a handle to something nothing in
	// the tree accounts for.
	void Bind(const std::wstring& slot, InstanceHandle handle) { m_slots[slot] = handle; }
	void ClearSlots() { m_slots.clear(); }

private:
	// The handles of our own toolbar's elements, empty whenever the layer is not enumerated at all.
	// Enumeration is parent-before-child in practice, but this closes over the subtree rather than
	// assuming it, since one missed pass would leak our UI into the answer.
	//
	// The resident toolbar is dropped from every answer: the tool reports the app's UI, not RoseMCP's
	// own. The diagnostics UI layer it lives on is not enumerated by AdviseVisualTreeChange on the
	// versions tested -- the count is identical before and after the toolbar goes up -- so this is a
	// guard against a framework that does enumerate it, not a fix for one that does.
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

	// A name belonging to more than one element is refused rather than answered with one of them.
	// The path form is how a caller says which, so the refusal names the count and leaves a route.
	std::wstring ResolveName(const std::wstring& name, InstanceHandle& handle) const
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

	std::wstring ResolvePath(const std::wstring& path, InstanceHandle& handle) const
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

	std::vector<TreeNode> m_nodes;

	// Every element a name belongs to, rather than whichever was enumerated last under it. A
	// duplicated x:Name is ordinary and not exotic -- a control template instantiated three times
	// gives three elements called the same thing -- and a single-valued map answers such a name with
	// an arbitrary one of them, so an apply lands on an arbitrary element and reports success either
	// way. A list is what lets the ambiguity be refused instead.
	std::map<std::wstring, std::vector<InstanceHandle>> m_byName;

	// Instances built during the current apply and not yet attached to the tree, by the slot name the
	// host gave them. Cleared at the start of every batch.
	std::map<std::wstring, InstanceHandle> m_slots;
};
