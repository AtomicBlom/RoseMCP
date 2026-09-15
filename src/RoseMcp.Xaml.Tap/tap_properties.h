#pragma once

// Reading one element's property chain: what is set on it, where each value came from, and what
// a property's declared type is.
//
// GetPropertyValuesChain is the whole of the framework's involvement, and it answers three different
// questions that were being asked from three different places. Kept together because they share the
// awkward parts of that call -- the chain has to be freed field by field, a value's location belongs
// to the source object rather than to the property, and the two value reads xamlOM declines to
// stringify have to be fetched a second way.
//
// The two interface pointers are held as references to the tap's own, rather than copied. There is
// one authoritative pointer per interface, assigned when the tap is sited and released when it gives
// them back; a copy here would be a second lifetime to keep in step, and the way that fails is a
// stale pointer used after the release rather than a compile error.

#include "tap_surface.h"

#include <ostream>
#include <sstream>
#include <string>

class TapProperties
{
public:
	TapProperties(IXamlDiagnostics*& diagnostics, IVisualTreeService*& service)
		: m_diagnostics(diagnostics), m_service(service)
	{
	}

	bool IndexOf(InstanceHandle handle, const std::wstring& name, unsigned int& index)
	{
		std::wstring ignored;
		return IndexOf(handle, name, index, ignored);
	}

	// Also reports the property's own declared value type. That is the one authoritative answer to
	// "what does this property want", and the apply side needs it: a value built as the wrong type is
	// created quite happily and only fails at SetProperty, with an E_FAIL that names nothing.
	bool IndexOf(InstanceHandle handle, const std::wstring& name, unsigned int& index, std::wstring& valueType)
	{
		unsigned int sourceCount = 0;
		unsigned int propertyCount = 0;
		PropertyChainSource* sources = nullptr;
		PropertyChainValue* values = nullptr;
		const HRESULT hr = m_service->GetPropertyValuesChain(handle, &sourceCount, &sources, &propertyCount, &values);
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

	// One element's property chain: every effective (non-overridden) value with its type, provenance
	// (default/style/local/...), and the source location that set it, plus an element row carrying its
	// type and its own declaration site. Source locations are populated only when the app carries XAML
	// source info; otherwise those fields are empty and the caller degrades to provenance alone.
	//
	// The property rows, into whatever sink the caller has. An std::ostream rather than a file, so
	// the same builder serves properties.tsv and the pipe reply (#50) -- one builder, because two
	// would drift and the provenance column is exactly what would drift.
	//
	// Returns false when the property chain could not be read at all, which the caller reports
	// differently from "read it and there was nothing".
	bool Emit(std::ostream& file, InstanceHandle handle, bool includeDefaults, unsigned int& written)
	{
		written = 0;

		unsigned int sourceCount = 0;
		unsigned int valueCount = 0;
		PropertyChainSource* sources = nullptr;
		PropertyChainValue* values = nullptr;
		const HRESULT hr = m_service->GetPropertyValuesChain(handle, &sourceCount, &sources, &valueCount, &values);
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
				if (!isNull && (value.MetadataBits & IsValueHandle) != 0 && RoseTapRenderBrush(m_diagnostics, valueText, rendered))
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
					&& RoseTapRenderCornerRadius(m_diagnostics, handle, rendered))
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

	// The same rows as a string, for the pipe. A leading status line, because "could not read the chain"
	// and "read it and there were no rows" are different answers and an empty reply says both.
	std::string Reply(InstanceHandle handle, bool includeDefaults)
	{
		std::ostringstream rows;
		unsigned int written = 0;
		if (!Emit(rows, handle, includeDefaults, written)) return "error\n";

		Log(L"pipe: served " + std::to_wstring(written) + L" propert(y/ies) for handle " + std::to_wstring(handle));
		return "ok\n" + rows.str();
	}

private:
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

	IXamlDiagnostics*& m_diagnostics;
	IVisualTreeService*& m_service;
};
