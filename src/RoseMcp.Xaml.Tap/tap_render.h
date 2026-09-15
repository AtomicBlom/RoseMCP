#pragma once

// The four reads that need a concrete projected type, declared in tap_surface.h and defined here.
//
// Deliberately not self-contained, like tap_overlay.h and for the same reason: it is included after
// the provider has defined its aliases, because the try_as<> chains below are the whole content and
// each names types that exist under both XAML roots but are not the same type under each. Everything
// else RoseTap does is xamlOM and compiles once; this is the residue that cannot.
//
// Every one of these turns a handle into an IInspectable, asks the projection for a typed value, and
// gives back a string or another handle. Nothing projected leaves the file, which is what allows the
// object that calls them to be compiled without any of this.

// A double as XAML would write it: no trailing zeros, and no decimal point when it is whole.
//
// Exact rather than rounded, unlike the rulers' own formatting, because this renders a property
// value somebody may compare against their markup.
static std::wstring RoseTapRenderNumber(double value)
{
	wchar_t buffer[32];
	swprintf_s(buffer, L"%g", value);
	return buffer;
}

static bool RoseTapRenderCornerRadius(IXamlDiagnostics* diagnostics, InstanceHandle handle, std::wstring& rendered)
{
	if (!diagnostics || handle == 0) return false;

	::IInspectable* raw = nullptr;
	if (FAILED(diagnostics->GetIInspectableFromHandle(handle, &raw)) || !raw) return false;

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
	rendered = RoseTapRenderNumber(radius.TopLeft) + L"," + RoseTapRenderNumber(radius.TopRight)
		+ L"," + RoseTapRenderNumber(radius.BottomRight) + L"," + RoseTapRenderNumber(radius.BottomLeft);

	return true;
}

/// The handle round-trips through GetIInspectableFromHandle, which is the reverse of what the
/// overlay uses to identify a clicked element. Only SolidColorBrush is rendered: it is the one
/// with an unambiguous textual form, and the overwhelming majority of what a hot reload sets. A
/// gradient or a brush behind a ThemeResource is left as its handle rather than being flattened
/// into a colour that would misrepresent it -- naming the resource key would be the better answer
/// there, and is a separate piece of work.
static bool RoseTapRenderBrush(IXamlDiagnostics* diagnostics, const wchar_t* valueText, std::wstring& rendered)
{
	if (!diagnostics || !valueText || !valueText[0]) return false;

	const InstanceHandle valueHandle = static_cast<InstanceHandle>(_wcstoui64(valueText, nullptr, 10));
	if (valueHandle == 0) return false;

	::IInspectable* raw = nullptr;
	if (FAILED(diagnostics->GetIInspectableFromHandle(valueHandle, &raw)) || !raw) return false;

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

static bool RoseTapResourcesOf(IXamlDiagnostics* diagnostics, InstanceHandle owner, InstanceHandle& dictionary)
{
	if (!diagnostics) return false;

	::IInspectable* raw = nullptr;
	if (FAILED(diagnostics->GetIInspectableFromHandle(owner, &raw)) || !raw) return false;

	winrt::Windows::Foundation::IInspectable instance{ nullptr };
	winrt::attach_abi(instance, raw); // adopt the ref

	const auto element = instance.try_as<xaml::FrameworkElement>();
	if (!element) return false;

	const auto resources = element.Resources();
	if (!resources) return false;

	const HRESULT hr = diagnostics->GetHandleFromIInspectable(
		reinterpret_cast<::IInspectable*>(winrt::get_abi(resources)), &dictionary);

	return SUCCEEDED(hr) && dictionary != 0;
}

static bool RoseTapKeyHandle(IXamlDiagnostics* diagnostics, const std::wstring& key, InstanceHandle& handle)
{
	if (!diagnostics || key.empty()) return false;

	const auto boxed = winrt::box_value(winrt::hstring{ key });
	const HRESULT hr = diagnostics->GetHandleFromIInspectable(
		reinterpret_cast<::IInspectable*>(winrt::get_abi(boxed)), &handle);

	return SUCCEEDED(hr) && handle != 0;
}
