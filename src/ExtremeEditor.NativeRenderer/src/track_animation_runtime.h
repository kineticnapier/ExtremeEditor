#pragma once

#include "extreme_editor_renderer.h"
#include "floor_base.h"
#include "track_transform_runtime.h"

#include <chrono>
#include <cstddef>
#include <cstdint>
#include <mutex>
#include <unordered_map>
#include <vector>

namespace ee
{
struct TrackAnimationFloorState
{
    float extend_anim = 1.0f;
    std::int32_t sorting_offset = 0;
    bool active_position = false;
    bool active_scale = false;
    bool active_rotation = false;
    bool active_opacity = false;
};

class TrackAnimationRuntime
{
public:
    void ResetBase(const std::vector<EeFloor>& floors) noexcept;
    void ResetBase(FloorBase base) noexcept;
    const EeFloor* BaseData() const noexcept;
    std::size_t OwnedFloorBufferBytes() const noexcept;
    bool SetTimeline(
        const EeTrackAnimationSegment* segments,
        std::uint32_t segment_count,
        const EeTrackAnimationTiming* timings,
        std::uint32_t timing_count) noexcept;
    void SetCompetingTimeline(
        const EeTrackTransformEvent* events,
        std::uint32_t event_count) noexcept;
    void SetPlaybackAnchor(double chart_time, double chart_rate, std::uint32_t flags) noexcept;
    void Update(std::vector<EeFloor>& floors, TrackTransformRuntime::CellMap& cells) noexcept;
    void UpdateVisible(
        std::vector<EeFloor>& floors,
        TrackTransformRuntime::CellMap& cells,
        const std::vector<std::uint32_t>& visible_floors) noexcept;
    TrackAnimationFloorState FloorState(std::uint32_t floor) const noexcept;
    float CullMargin() const noexcept;
    bool EvaluateForFloor(
        std::uint32_t floor,
        double chart_time,
        const EeFloor& move_resolved,
        const EeFloor* previous_resolved,
        const EeFloor* next_resolved,
        EeFloor& output,
        TrackAnimationFloorState& state) const noexcept;

private:
    struct RandomSample
    {
        float x = 0.0f;
        float y = 0.0f;
        float rotation = 0.0f;
    };

    static constexpr std::size_t NoSegment = static_cast<std::size_t>(-1);
    static constexpr float CellSize = 32.0f;

    double CurrentChartTime() const noexcept;
    static float Clamp01(double value) noexcept;
    static float OutSine(double progress) noexcept;
    static float OutQuad(double progress) noexcept;
    static float Lerp(float a, float b, float t) noexcept;
    static RandomSample MakeRandom(std::uint32_t floor, std::int32_t source_index, float extent) noexcept;
    static std::int64_t CellKey(float x, float y) noexcept;
    static void Reindex(
        std::uint32_t floor,
        float old_x,
        float old_y,
        float new_x,
        float new_y,
        TrackTransformRuntime::CellMap& cells) noexcept;
    // The fields SameTransformState compares, copied bit for bit. A last
    // output is only ever compared, so it does not need a whole EeFloor.
    struct TransformState
    {
        float x = 0.0f;
        float y = 0.0f;
        float entry_angle = 0.0f;
        float icon_angle = 0.0f;
        float transform_scale_x = 0.0f;
        float transform_scale_y = 0.0f;
        float transform_rotation = 0.0f;
        float transform_opacity = 0.0f;
        std::uint32_t track_transform_flags = 0u;

        static TransformState Of(const EeFloor& floor) noexcept
        {
            return {floor.x, floor.y, floor.entry_angle, floor.icon_angle,
                floor.transform_scale_x, floor.transform_scale_y,
                floor.transform_rotation, floor.transform_opacity,
                floor.track_transform_flags};
        }
    };

    static bool SameTransformState(const EeFloor& left, const TransformState& right) noexcept;
    EeFloor CaptureMoveResolved(std::size_t floor, const EeFloor& candidate) noexcept;
    bool OwnsSharedChannel(
        std::uint32_t floor,
        double animation_start,
        std::int32_t animation_source_index,
        std::uint32_t channel,
        double chart_time) const noexcept;

    mutable std::recursive_mutex mutex_;
    void ReleaseFloorBuffers() noexcept;
    const std::vector<EeFloor>& BaseFloors() const noexcept { return FloorsOf(base_); }

    FloorBase base_;
    std::vector<EeTrackAnimationSegment> segments_;
    std::vector<EeTrackAnimationTiming> timings_;
    std::vector<std::size_t> segment_by_floor_;
    std::vector<TrackAnimationFloorState> floor_states_;
    std::vector<EeFloor> move_resolved_floors_;
    std::vector<TransformState> last_outputs_;
    std::vector<bool> has_last_output_;
    std::unordered_map<std::uint32_t, std::vector<EeTrackTransformEvent>> competing_events_by_floor_;
    bool active_ = false;
    float cull_margin_ = 0.0f;
    bool playing_ = false;
    double anchor_chart_time_ = 0.0;
    double chart_rate_ = 1.0;
    std::chrono::steady_clock::time_point anchor_steady_{};
};
}
