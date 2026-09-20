#pragma once

#include <windows.h>
#include <cstdint>

namespace ee
{
class Renderer;

class NativeWindow
{
public:
    NativeWindow() = default;
    NativeWindow(const NativeWindow&) = delete;
    NativeWindow& operator=(const NativeWindow&) = delete;
    ~NativeWindow();

    bool Create(HWND parent, std::uint32_t width, std::uint32_t height, Renderer* owner) noexcept;
    void Destroy() noexcept;
    void Resize(std::uint32_t width, std::uint32_t height) noexcept;

    [[nodiscard]] HWND Handle() const noexcept { return hwnd_; }

private:
    static bool EnsureWindowClass() noexcept;
    static LRESULT CALLBACK WindowProc(HWND hwnd, UINT message, WPARAM wparam, LPARAM lparam) noexcept;

    HWND hwnd_ = nullptr;
};
}
