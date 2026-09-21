#include "native_window.h"
#include "renderer.h"

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
        window_class.hbrBackground = nullptr;
        window_class.lpszClassName = WindowClassName;

        ATOM atom = RegisterClassExW(&window_class);
        if (atom != 0 || GetLastError() == ERROR_CLASS_ALREADY_EXISTS)
            WindowClassReady = true;
    });

    return WindowClassReady;
}

LRESULT CALLBACK NativeWindow::WindowProc(HWND hwnd, UINT message, WPARAM wparam, LPARAM lparam) noexcept
{
    Renderer* owner = reinterpret_cast<Renderer*>(GetWindowLongPtrW(hwnd, GWLP_USERDATA));
    if (message == WM_NCCREATE)
    {
        auto* create = reinterpret_cast<CREATESTRUCTW*>(lparam);
        owner = static_cast<Renderer*>(create->lpCreateParams);
        SetWindowLongPtrW(hwnd, GWLP_USERDATA, reinterpret_cast<LONG_PTR>(owner));
    }

    if (owner != nullptr)
    {
        LRESULT editor_result = 0;
        if (owner->HandleEditorHudMessage(hwnd, message, wparam, lparam, editor_result))
            return editor_result;
        return owner->HandleWindowMessage(hwnd, message, wparam, lparam);
    }

    return DefWindowProcW(hwnd, message, wparam, lparam);
}

bool NativeWindow::Create(HWND parent, std::uint32_t width, std::uint32_t height, Renderer* owner) noexcept
{
    if (parent == nullptr || owner == nullptr || !EnsureWindowClass())
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
        owner);

    return hwnd_ != nullptr;
}

void NativeWindow::Destroy() noexcept
{
    if (hwnd_ == nullptr)
        return;

    SetWindowLongPtrW(hwnd_, GWLP_USERDATA, 0);
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
