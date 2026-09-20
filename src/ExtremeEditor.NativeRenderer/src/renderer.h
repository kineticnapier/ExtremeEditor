#pragma once

#include "native_window.h"
#include <windows.h>
#include <cstdint>

namespace ee
{
class Renderer
{
public:
    Renderer() = default;
    Renderer(const Renderer&) = delete;
    Renderer& operator=(const Renderer&) = delete;

    bool Initialize(HWND parent, std::uint32_t width, std::uint32_t height) noexcept;
    void Resize(std::uint32_t width, std::uint32_t height) noexcept;

    [[nodiscard]] HWND ChildHwnd() const noexcept { return window_.Handle(); }

private:
    NativeWindow window_;
};
}
