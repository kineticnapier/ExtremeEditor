#include "native_window.h"

#include <mutex>

namespace ee
{
namespace
{
constexpr wchar_t WindowClassName[] = L"ExtremeEditor.NativeRenderer.Window";
std::once_flag WindowClassOnce;
bool WindowClassReady = false;
}

NativeWindow::~NativeWindow()
{
    Destroy();
}

bool NativeWindow::EnsureWindowClass() noexcept
{
    std::call_once(WindowClassOnce, []
    {
        WNDCLASSEXW window_class{};
        window_class.cbSize = sizeof(window_class);
        window_class.style = CS_HREDRAW | CS_VREDRAW;
        window_class.lpfnWndProc = &NativeWindow::WindowProc;
        window_class.hInstance = GetModuleHandleW(nullptr);
        window_class.hCursor = LoadCursorW(nullptr, IDC_ARROW);
        window_class.hbrBackground = reinterpret_cast<HBRUSH>(COLOR_WINDOW + 1);
        window_class.lpszClassName = WindowClassName;

        ATOM atom = RegisterClassExW(&window_class);
        if (atom != 0 || GetLastError() == ERROR_CLASS_ALREADY_EXISTS)
            WindowClassReady = true;
    });

    return WindowClassReady;
}

LRESULT CALLBACK NativeWindow::WindowProc(HWND hwnd, UINT message, WPARAM wparam, LPARAM lparam) noexcept
{
    return DefWindowProcW(hwnd, message, wparam, lparam);
}

bool NativeWindow::Create(HWND parent, std::uint32_t width, std::uint32_t height) noexcept
{
    if (parent == nullptr || !EnsureWindowClass())
        return false;

    Destroy();

    hwnd_ = CreateWindowExW(
        0,
        WindowClassName,
        L"ExtremeEditor Native Renderer",
        WS_CHILD | WS_VISIBLE | WS_CLIPSIBLINGS | WS_CLIPCHILDREN,
        0,
        0,
        static_cast<int>(width),
        static_cast<int>(height),
        parent,
        nullptr,
        GetModuleHandleW(nullptr),
        nullptr);

    return hwnd_ != nullptr;
}

void NativeWindow::Destroy() noexcept
{
    if (hwnd_ == nullptr)
        return;

    DestroyWindow(hwnd_);
    hwnd_ = nullptr;
}

void NativeWindow::Resize(std::uint32_t width, std::uint32_t height) noexcept
{
    if (hwnd_ == nullptr)
        return;

    MoveWindow(hwnd_, 0, 0, static_cast<int>(width), static_cast<int>(height), TRUE);
}
}
