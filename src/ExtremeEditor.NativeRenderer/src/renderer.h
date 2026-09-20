#pragma once

#include "icon_assets.h"
#include "level_scene.h"
#include "native_window.h"

#include <windows.h>
#include <atomic>
#include <condition_variable>
#include <cstdint>
#include <memory>
#include <mutex>
#include <string>
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
    void ClearIconAssets() noexcept;
    bool SetIconAsset(std::uint32_t icon_id, const wchar_t* image_path, const wchar_t* outline_path) noexcept;

    [[nodiscard]] HWND ChildHwnd() const noexcept { return window_.Handle(); }
    [[nodiscard]] std::int32_t SelectedFloor() const noexcept;
    LRESULT HandleWindowMessage(HWND hwnd, UINT message, WPARAM wparam, LPARAM lparam) noexcept;

private:
    void RenderLoop() noexcept;
    void StopRenderThread() noexcept;
    void SelectFloorAt(int screen_x, int screen_y) noexcept;

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

    mutable std::mutex scene_mutex_;
    std::shared_ptr<LevelScene> scene_;
    std::shared_ptr<const IconAssetTable> icon_assets_ = std::make_shared<IconAssetTable>();
    float camera_x_ = 0.0f;
    float camera_y_ = 0.0f;
    float zoom_ = 28.0f;
    std::int32_t selected_floor_ = -1;
    std::uint64_t scene_version_ = 0;
    std::uint64_t icon_assets_version_ = 0;

    bool panning_ = false;
    UINT pan_button_ = 0;
    POINT last_mouse_{};
};
}
