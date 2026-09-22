#pragma once

#include "extreme_editor_renderer.h"

#include <chrono>
#include <cstdint>
#include <mutex>
#include <unordered_map>
#include <vector>

namespace ee
{
class TrackTransformRuntime
{
public:
    using CellMap = std::unordered_map<std::int64_t, std::vector<std::uint32_t>>;

    void ResetBase(const std::vector<EeFloor>& floors) noexcept;
    bool SetTimeline(const EeTrackTransformEvent* events, std::uint32_t event_count) noexcept;
    void SetPlaybackAnchor(double chart_time, double chart_rate, std::uint32_t flags) noexcept;
    void Restore(std::vector<EeFloor>& floors, CellMap& cells) noexcept;
    void Update(std::vector<EeFloor>& floors, CellMap& cells) noexcept;

private:
    struct FloorTrack
    {
        std::int32_t floor = -1;
        std::vector<EeTrackTransformEvent> events;
    };

    static constexpr float CellSize = 32.0f;

    double CurrentChartTime() const noexcept;
    static float ApplyEase(std::uint32_t ease, double progress) noexcept;
    static float Evaluate(float start, float target, const EeTrackTransformEvent& item, double chart_time) noexcept;
    static int FastFloor(float value) noexcept;
    static std::int64_t CellKey(float x, float y) noexcept;
    static void ReindexFloor(
        std::uint32_t floor,
        float old_x,
        float old_y,
        float new_x,
        float new_y,
        CellMap& cells) noexcept;

    mutable std::mutex mutex_;
    std::vector<EeFloor> base_floors_;
    std::vector<FloorTrack> tracks_;
    bool active_ = false;
    bool playing_ = false;
    double anchor_chart_time_ = 0.0;
    double chart_rate_ = 1.0;
    std::chrono::steady_clock::time_point anchor_steady_{};
};
}
