#pragma once

// The parts that read the live tree through the XAML Diagnostics ABI, and nothing above it.
//
// xamlOM.h is shared by Windows.UI.Xaml and Microsoft.UI.Xaml verbatim -- the interfaces, the
// handles and the enums are the same declarations -- so everything here serves UWP and WinUI 3 with
// no alias and no flag. It is the projections, one layer up, that differ.
//
// TreeNode lives here rather than in the channel because it holds an InstanceHandle. That is not a
// filing preference: putting it next door fails to compile, which is what keeps the channel honestly
// framework-free.

#include <windows.h>
#include <unknwn.h>
#include <ocidl.h>

#undef GetCurrentTime

#include <xamlOM.h>

#include <string>

// Where a property's effective value came from -- the bridge from a live value to how it was set.
static std::wstring Provenance(BaseValueSource source)
{
	switch (source)
	{
		case BaseValueSourceDefault: return L"Default";
		case BaseValueSourceBuiltInStyle: return L"BuiltInStyle";
		case BaseValueSourceStyle: return L"Style";
		case BaseValueSourceLocal: return L"Local";
		case Inherited: return L"Inherited";
		case DefaultStyleTrigger: return L"DefaultStyleTrigger";
		case TemplateTrigger: return L"TemplateTrigger";
		case StyleTrigger: return L"StyleTrigger";
		case ImplicitStyleReference: return L"ImplicitStyleReference";
		case ParentTemplate: return L"ParentTemplate";
		case ParentTemplateTrigger: return L"ParentTemplateTrigger";
		case Animation: return L"Animation";
		case Coercion: return L"Coercion";
		case BaseValueSourceVisualState: return L"VisualState";
		default: return L"Unknown";
	}
}

// The UIElement properties backed by a composition Visual. They read as BaseValueSourceLocal the
// moment the framework touches one, whatever the XAML says, so an element whose whole declaration is
// two attributes reported six local sets that do not exist -- crowding out, in that same answer, the
// one property whose absence explained why the element was not hit-testable.
//
// A fixed list rather than a rule, because that is what this is: these six were added to UIElement
// together and there is no flag distinguishing them. They are still reported when the caller asks for
// defaults, since "everything on this element" is a legitimate question; they are just not evidence
// of what the markup sets, which is what the default view is for.
static bool IsComposition(const wchar_t* propertyName)
{
	if (!propertyName) return false;

	static const wchar_t* const composition[] = {
		L"CenterPoint", L"Rotation", L"RotationAxis", L"Scale", L"TransformMatrix", L"Translation",
	};

	for (const auto* candidate : composition)
	{
		if (wcscmp(propertyName, candidate) == 0) return true;
	}

	return false;
}

// Gives back everything GetPropertyValuesChain allocated: a BSTR per string field, and the two
// arrays themselves.
//
// Here rather than beside either caller because both the property read and the edits ask for a
// chain, and two copies of a free would be two chances to miss a field -- the leak that produces is
// one BSTR per property per call, in somebody else's application.
static void FreePropertyChain(
	PropertyChainSource* sources, unsigned int sourceCount,
	PropertyChainValue* values, unsigned int valueCount)
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

// One element of the visual tree, captured as it is announced.
struct TreeNode
{
	InstanceHandle Handle;
	InstanceHandle Parent;
	unsigned int ChildIndex;
	std::wstring Type;
	std::wstring Name;

	// Where the element was declared, from VisualElement::SrcInfo -- the field that separates the
	// app's own markup from a control template's parts, and so the basis of "just my XAML".
	std::wstring File;
	unsigned int Line;
	unsigned int Column;
};
