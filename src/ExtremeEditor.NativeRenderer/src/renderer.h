#pragma once

#include "decoration_assets.h"
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
struct HeadlessRendererState;

class Renderer
{
public:
    Renderer() = default;
    Renderer(const Renderer&) = delete;
    Renderer& operator=(const Renderer&) = delete;
    ~Renderer();

    bool Initialize(HWND parent, std::uint32_t width, std::uint32_t height) noexcept;
    bool InitializeHeadless(std::uint32_t width, std::uint32_t height) noexcept;
    bool RenderHeadlessRgb(
        double scene_time,
        double visual_time,
        std::uint8_t* rgb,
        std::uint32_t rgb_size,
        std::uint32_t row_stride) noexcept;
    void Resize(std::uint32_t width, std::uint32_t height) noexcept;
    bool SetLevel(std::shared_ptr<LevelScene> scene) noexcept;
    bool UpdateFloorIcons(
        std::uint32_t start_floor,
        const EeFloorIconState* states,
        std::uint32_t state_count) noexcept;
    bool SetStaticDecorations(const EeStaticDecoration* decorations, std::uint32_t decoration_count) noexcept;
    void ClearDecorationAssets() noexcept;
    bool SetDecorationAsset(std::uint32_t asset_id, const wchar_t* image_path) noexcept;
    void FrameAll() noexcept;
    void CenterAt(float world_x, float world_y) noexcept;
    void ClearIconAssets() noexcept;
    bool SetIconAsset(
        std::uint32_t icon_id,
        const wchar_t* image_path,
        const wchar_t* outline_path,
        const EeSpriteMetadata& image_metadata,
        const EeSpriteMetadata& outline_metadata) noexcept;
    void SetSelection(const std::int32_t* floors, std::uint32_t floor_count, std::int32_t primary_floor) noexcept;
    void SetSelectionChangedCallback(EeSelectionChangedCallback callback, void* user_data) noexcept;
    void SetEditorActionCallback(EeEditorActionCallback callback, void* user_data) noexcept;
    bool SetPlaybackTimeline(const EePlaybackTiming* timings, std::uint32_t timing_count) noexcept;
    bool SetCameraTimeline(const EeCameraEvent* events, std::uint32_t event_count) noexcept;
    bool SetTrackTransformTimeline(const EeTrackTransformEvent* events, std::uint32_t event_count) noexcept;
    bool SetTrackVisualTimeline(const EeTrackVisualEvent* events, std::uint32_t event_count) noexcept;
    bool SetTrackAnimationTimeline(
        const EeTrackAnimationSegment* segments,
        std::uint32_t segment_count,
        const EeTrackAnimationTiming* timings,
        std::uint32_t timing_count) noexcept;
    void SetPlaybackAnchor(double chart_time, double chart_rate, std::uint32_t flags) noexcept;
    void SetTrackPlaybackAnchor(double chart_time, double chart_rate, std::uint32_t flags) noexcept;
    void SetFollowPlayer(bool enabled) noexcept;
    void SetFollowPlayerChangedCallback(EeFollowPlayerChangedCallback callback, void* user_data) noexcept;
    bool GetDiagnostics(EeRendererDiagnostics& diagnostics) noexcept;

    [[nodiscard]] HWND ChildHwnd() const noexcept { return window_.Handle(); }
    [[nodiscard]] std::int32_t SelectedFloor() const noexcept;
    bool HandleEditorHudMessage(HWND hwnd, UINT message, WPARAM wparam, LPARAM lparam, LRESULT& result) noexcept;
    LRESULT HandleWindowMessage(HWND hwnd, UINT message, WPARAM wparam, LPARAM lparam) noexcept;

private:
    void RenderLoop() noexcept;
    void StopRenderThread(bool shutdown_diagnostic = false) noexcept;
    std::int32_t SelectFloorAt(int screen_x, int screen_y) noexcept;
    int HitTestEditorHud(int screen_x, int screen_y) noexcept;
    void NotifySelectionChanged(std::int32_t floor, std::uint32_t modifiers) noexcept;
    void NotifyEditorAction(EeEditorAction action, std::int32_t floor, double value) noexcept;
    void DisableFollowForManualPan() noexcept;
    void NotifyFollowPlayerChanged(bool enabled) noexcept;

    NativeWindow window_;
    std::thread render_thread_;
    std::shared_ptr<HeadlessRendererState> headless_state_;
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
    std::shared_ptr<const std::vector<EeStaticDecoration>> static_decorations_ =
        std::make_shared<std::vector<EeStaticDecoration>>();
    std::shared_ptr<const DecorationAssetTable> decoration_assets_ =
        std::make_shared<DecorationAssetTable>();
    std::shared_ptr<const std::vector<EePlaybackTiming>> playback_timings_ =
        std::make_shared<std::vector<EePlaybackTiming>>();
    std::shared_ptr<const std::vector<EeCameraEvent>> camera_events_ =
        std::make_shared<std::vector<EeCameraEvent>>();
    float camera_x_ = 0.0f;
    float camera_y_ = 0.0f;
    float zoom_ = 28.0f;
    std::int32_t selected_floor_ = -1;
    std::vector<std::int32_t> selected_floors_;
    int hover_hud_button_ = -1;
    std::uint64_t scene_version_ = 0;
    std::uint64_t icon_assets_version_ = 0;
    std::uint64_t static_decorations_version_ = 0;
    std::uint64_t decoration_assets_version_ = 0;
    std::uint32_t pending_icon_start_floor_ = 0;
    std::vector<EeFloorIconState> pending_icon_states_;
    EeSelectionChangedCallback selection_callback_ = nullptr;
    void* selection_user_data_ = nullptr;
    EeEditorActionCallback editor_action_callback_ = nullptr;
    void* editor_action_user_data_ = nullptr;
    EeFollowPlayerChangedCallback follow_callback_ = nullptr;
    void* follow_user_data_ = nullptr;

    bool playback_active_ = false;
    bool playback_playing_ = false;
    bool follow_player_ = true;
    double playback_anchor_chart_time_ = 0.0;
    double playback_chart_rate_ = 1.0;
    std::chrono::steady_clock::time_point playback_anchor_steady_{};

    std::atomic_uint64_t frame_counter_{0};
    std::atomic<double> last_frame_ms_{0.0};
    std::atomic<double> max_frame_ms_{0.0};
    std::atomic<double> last_render_ms_{0.0};
    mutable std::mutex diagnostics_mutex_;
    std::uint64_t diagnostics_last_frame_counter_ = 0;
    std::chrono::steady_clock::time_point diagnostics_last_sample_{};

    bool panning_ = false;
    UINT pan_button_ = 0;
    POINT last_mouse_{};
};
}
