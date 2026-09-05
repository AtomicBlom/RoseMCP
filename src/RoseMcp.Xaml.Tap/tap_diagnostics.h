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
