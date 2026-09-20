#pragma once

#include "native_window.h"

#include <windows.h>
#include <atomic>
#include <condition_variable>
#include <cstdint>
#include <mutex>
#include <thread>

namespace ee
{
class Renderer
{
public:
    Renderer() = default;
    Renderer(const Renderer&) = delete;
    Renderer& operator=(const Renderer&) = delete;
    ~Renderer();

    bool Initialize(HWND parent, std::uint32_t width, std::uint32_t height) noexcept;
    void Resize(std::uint32_t width, std::uint32_t height) noexcept;

    [[nodiscard]] HWND ChildHwnd() const noexcept { return window_.Handle(); }

private:
    void RenderLoop() noexcept;
    void StopRenderThread() noexcept;

    NativeWindow window_;
    std::thread render_thread_;
    std::atomic_bool stop_requested_{false};
    std::atomic_bool resize_pending_{false};
    std::atomic_uint32_t width_{1};
    std::atomic_uint32_t height_{1};

    std::mutex initialize_mutex_;
    std::condition_variable initialize_cv_;
    bool initialize_complete_ = false;
    bool initialize_success_ = false;
};
}
