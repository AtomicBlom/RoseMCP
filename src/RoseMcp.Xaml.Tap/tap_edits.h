#pragma once

// Applying a batch of hot-reload commands: setting a property, clearing one, building an instance,
// attaching it, removing one, and replacing a keyed resource.
//
// This is the half of the tap that changes somebody else's running application, and the asymmetry
// that follows from it is the reason the file exists on its own. A read can be retried; a batch
// cannot -- sending it twice puts a second copy of everything it adds into the app, and a missing
// reply cannot tell "never ran" from "ran, and the answer was lost". So every command reports its
// own outcome, and the batch never reports only its own success.
//
// It collaborates rather than owning: the tree resolves targets and is told what an edit removed or
// built, and the property chain answers what a property's index and declared type are. Both are
// references to the tap's own, because there is one tree and one chain reader per tap and a second
// of either would answer from a different state.

#include "tap_properties.h"
#include "tap_tree.h"

#include <algorithm>
#include <string>
#include <vector>

class TapEdits
{
public:
	TapEdits(IXamlDiagnostics*& diagnostics, IVisualTreeService*& service, TapTree& tree, TapProperties& properties)
		: m_diagnostics(diagnostics), m_service(service), m_tree(tree), m_properties(properties)
	{
	}

	/// <summary>
	/// Runs a batch and returns one result row per command, in the order they were given: applied,
	/// target not found, property not found, or a failure code. Per command, because a batch that
	/// reported only its own success would hide the one edit in it that did nothing.
	/// </summary>
	std::string Apply(const std::vector<Command>& commands)
	{
		Log(L"applying " + std::to_wstring(commands.size()) + L" command(s)");

		// Slots live for one batch and no longer. They name instances that have been built but not
		// yet attached to anything, which is what lets a nested element be created, filled and then
		// handed to its parent -- and a slot surviving into the next apply would let one batch's
		// half-built element be reached by another's command.
		m_tree.ClearSlots();

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

private:

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
		const std::wstring unresolved = m_tree.Resolve(command.target, target);
		if (!unresolved.empty()) return unresolved;

		unsigned int index = 0;
		std::wstring declaredType;
		if (!m_properties.IndexOf(target, command.property, index, declaredType)) return L"property not found";

		std::wstring attempted;
		std::wstring failure;
		for (const auto& type : { command.valueType, declaredType })
		{
			if (type.empty() || type == attempted) continue;
			attempted = type;

			InstanceHandle valueHandle = 0;
			BSTR typeName = SysAllocString(type.c_str());
			BSTR value = SysAllocString(command.value.c_str());
			HRESULT hr = m_service->CreateInstance(typeName, value, &valueHandle);
			SysFreeString(typeName);
			SysFreeString(value);
			if (FAILED(hr))
			{
				failure = L"CreateInstance(" + type + L") failed 0x" + Hex(hr);
				continue;
			}

			hr = m_service->SetProperty(target, valueHandle, index);
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
		const std::wstring unresolved = m_tree.Resolve(command.target, child);
		if (!unresolved.empty()) return unresolved;

		InstanceHandle parent = 0;
		if (!m_tree.ParentOf(child, parent)) return L"target not found: it is not in the tree snapshot";
		if (parent == 0) return L"cannot remove: it has no parent in the tree";

		InstanceHandle collection = 0;
		unsigned int position = 0;
		if (!LocateInParent(parent, child, collection, position))
		{
			return L"cannot remove: it is not in any collection its parent exposes";
		}

		const HRESULT hr = m_service->RemoveChild(collection, position);
		if (hr != S_OK) return L"RemoveChild failed 0x" + Hex(hr);

		// Forgotten here as well as on the framework's own notification, because that notification is
		// not guaranteed to arrive before the next command in this batch, and every command resolves
		// against the node list. The failure that produces is the next removal landing on the sibling
		// that moved up into the vacated position. Forgetting a subtree twice is a no-op, which is
		// what makes saying it in both places safe rather than merely redundant.
		m_tree.ForgetSubtree(child);

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

		m_tree.Bind(command.target, handle);
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

		for (const auto& qualified : m_tree.QualifiedTypesNamed(typeName))
		{
			if (std::find(candidates.begin(), candidates.end(), qualified) != candidates.end()) continue;

			candidates.push_back(qualified);
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
			const HRESULT hr = m_service->CreateInstance(name, nullptr, &handle);
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
		const std::wstring unresolvedOwner = m_tree.Resolve(command.target, owner);
		if (!unresolvedOwner.empty()) return unresolvedOwner;

		InstanceHandle value = 0;
		const std::wstring unresolvedValue = m_tree.Resolve(command.arg, value);
		if (!unresolvedValue.empty()) return unresolvedValue;

		InstanceHandle dictionary = 0;
		if (!RoseTapResourcesOf(m_diagnostics, owner, dictionary)) return L"cannot replace: that element has no Resources dictionary";

		// The key is a handle, not a string, which is the part of this signature that surprises. A
		// boxed hstring is the honest way to make one: the diagnostics host can hand back a handle for
		// any IInspectable, and a resource key in markup is a string.
		InstanceHandle key = 0;
		if (!RoseTapKeyHandle(m_diagnostics, command.property, key)) return L"cannot replace: could not make a handle for the key";

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

	// Puts a built instance into its new parent's children.
	std::wstring ApplyAddChild(const Command& command)
	{
		InstanceHandle parent = 0;
		const std::wstring unresolvedParent = m_tree.Resolve(command.target, parent);
		if (!unresolvedParent.empty()) return unresolvedParent;

		InstanceHandle child = 0;
		const std::wstring unresolvedChild = m_tree.Resolve(command.arg, child);
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

		const HRESULT hr = m_service->AddChild(collection, child, command.index);
		if (hr != S_OK) return L"AddChild failed 0x" + Hex(hr);

		Log(L"  added " + command.arg + L" under " + command.target + L" at " + std::to_wstring(command.index));
		return L"applied";
	}

	// The collection an element keeps its children in, by name, plus what was there to choose from
	// when none of the names matched -- so a refusal can say what it saw instead of only that it
	// failed.
	bool ChildCollectionOf(InstanceHandle parent, InstanceHandle& collection, std::wstring& found)
	{
		if (!m_service) return false;

		unsigned int sourceCount = 0;
		unsigned int propertyCount = 0;
		PropertyChainSource* sources = nullptr;
		PropertyChainValue* values = nullptr;
		if (FAILED(m_service->GetPropertyValuesChain(parent, &sourceCount, &sources, &propertyCount, &values)))
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
		if (!m_service) return false;

		unsigned int sourceCount = 0;
		unsigned int propertyCount = 0;
		PropertyChainSource* sources = nullptr;
		PropertyChainValue* values = nullptr;
		if (FAILED(m_service->GetPropertyValuesChain(parent, &sourceCount, &sources, &propertyCount, &values)))
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
		if (FAILED(m_service->GetCollectionCount(collection, &count)) || count == 0) return false;

		unsigned int returned = count;
		CollectionElementValue* elements = nullptr;
		if (FAILED(m_service->GetCollectionElements(collection, 0, &returned, &elements)) || !elements) return false;

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

	std::wstring ApplyClearProperty(const Command& command)
	{
		InstanceHandle target = 0;
		const std::wstring unresolved = m_tree.Resolve(command.target, target);
		if (!unresolved.empty()) return unresolved;

		unsigned int index = 0;
		if (!m_properties.IndexOf(target, command.property, index)) return L"property not found";

		const HRESULT hr = m_service->ClearProperty(target, index);
		if (hr != S_OK) return L"ClearProperty failed 0x" + Hex(hr);

		Log(L"  cleared " + command.target + L"." + command.property);
		return L"applied";
	}

	IXamlDiagnostics*& m_diagnostics;
	IVisualTreeService*& m_service;
	TapTree& m_tree;
	TapProperties& m_properties;
};
