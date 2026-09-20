#pragma once

#include "icon_assets.h"
#include "level_scene.h"
#include "native_window.h"

#include <windows.h>
#include <atomic>
#include <chrono>
#include <condition_variable>
#include <cstdint>
#include <memory>
#include <mutex>
#include <string>
#include <thread>
#include <vector>

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
    void SetSelectionChangedCallback(EeSelectionChangedCallback callback, void* user_data) noexcept;
    bool SetPlaybackTimeline(const EePlaybackTiming* timings, std::uint32_t timing_count) noexcept;
    void SetPlaybackAnchor(double chart_time, double chart_rate, std::uint32_t flags) noexcept;
    void SetFollowPlayer(bool enabled) noexcept;
    void SetFollowPlayerChangedCallback(EeFollowPlayerChangedCallback callback, void* user_data) noexcept;
    bool GetDiagnostics(EeRendererDiagnostics& diagnostics) noexcept;

    [[nodiscard]] HWND ChildHwnd() const noexcept { return window_.Handle(); }
    [[nodiscard]] std::int32_t SelectedFloor() const noexcept;
    LRESULT HandleWindowMessage(HWND hwnd, UINT message, WPARAM wparam, LPARAM lparam) noexcept;

private:
    void RenderLoop() noexcept;
    void StopRenderThread() noexcept;
    std::int32_t SelectFloorAt(int screen_x, int screen_y) noexcept;
    void NotifySelectionChanged(std::int32_t floor) noexcept;
    void DisableFollowForManualPan() noexcept;
    void NotifyFollowPlayerChanged(bool enabled) noexcept;

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
    std::shared_ptr<const std::vector<EePlaybackTiming>> playback_timings_ =
        std::make_shared<std::vector<EePlaybackTiming>>();
    float camera_x_ = 0.0f;
    float camera_y_ = 0.0f;
    float zoom_ = 28.0f;
    std::int32_t selected_floor_ = -1;
    std::uint64_t scene_version_ = 0;
    std::uint64_t icon_assets_version_ = 0;
    EeSelectionChangedCallback selection_callback_ = nullptr;
    void* selection_user_data_ = nullptr;
    EeFollowPlayerChangedCallback follow_callback_ = nullptr;
    void* follow_user_data_ = nullptr;

    bool playback_active_ = false;
    bool playback_playing_ = false;
    bool follow_player_ = true;
    double playback_anchor_chart_time_ = 0.0;
    double playback_chart_rate_ = 1.0;
    std::chrono::steady_clock::time_point playback_anchor_steady_{};

    std::atomic_uint64_t frame_counter_{0};
    std::atomic<double> native_fps_{0.0};
    std::atomic<double> last_frame_ms_{0.0};
    std::atomic<double> max_frame_ms_{0.0};
    std::atomic<double> last_render_ms_{0.0};
    std::atomic<double> last_cull_ms_{0.0};
    std::atomic_uint32_t visible_candidates_{0};
    std::atomic_uint32_t floor_draws_{0};
    std::atomic_uint32_t icon_draws_{0};
    std::atomic_uint32_t draw_calls_{0};

    bool panning_ = false;
    UINT pan_button_ = 0;
    POINT last_mouse_{};
};
}
