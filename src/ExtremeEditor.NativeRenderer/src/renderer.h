#pragma once

#include "level_scene.h"
#include "native_window.h"

#include <windows.h>
#include <atomic>
#include <condition_variable>
#include <cstdint>
#include <memory>
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
    bool SetLevel(std::shared_ptr<LevelScene> scene) noexcept;
    void FrameAll() noexcept;

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

    std::mutex scene_mutex_;
    std::shared_ptr<LevelScene> scene_;
    float camera_x_ = 0.0f;
    float camera_y_ = 0.0f;
    float zoom_ = 28.0f;
    std::uint64_t scene_version_ = 0;
};
}
