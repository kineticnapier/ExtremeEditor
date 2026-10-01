#include "track_animation_runtime.h"

#include <algorithm>
#include <cmath>
#include <limits>

namespace ee
{
namespace
{
constexpr float Pi = 3.14159265358979323846f;
constexpr float DegToRad = Pi / 180.0f;

float UnitFromHash(std::uint32_t value) noexcept
{
    value ^= value >> 16u;
    value *= 0x7feb352du;
    value ^= value >> 15u;
    value *= 0x846ca68bu;
    value ^= value >> 16u;
    return static_cast<float>(value & 0x00ffffffu) / static_cast<float>(0x00ffffffu);
}
}

void TrackAnimationRuntime::ResetBase(const std::vector<EeFloor>& floors) noexcept
{
    std::lock_guard lock(mutex_);
    base_floors_ = floors;
    segments_.clear();
    timings_.clear();
    segment_by_floor_.assign(floors.size(), NoSegment);
    floor_states_.assign(floors.size(), TrackAnimationFloorState{});
    active_ = false;
    playing_ = false;
    anchor_chart_time_ = 0.0;
    chart_rate_ = 1.0;
    anchor_steady_ = std::chrono::steady_clock::now();
}

bool TrackAnimationRuntime::SetTimeline(
    const EeTrackAnimationSegment* segments,
    std::uint32_t segment_count,
    const EeTrackAnimationTiming* timings,
    std::uint32_t timing_count) noexcept
{
    if ((segment_count > 0u && segments == nullptr) ||
        (timing_count > 0u && timings == nullptr))
        return false;

    std::lock_guard lock(mutex_);
    if (timing_count != base_floors_.size() && timing_count != 0u)
        return false;

    try
    {
        segments_.assign(segments, segments + segment_count);
        timings_.assign(timings, timings + timing_count);
        segment_by_floor_.assign(base_floors_.size(), NoSegment);
        floor_states_.assign(base_floors_.size(), TrackAnimationFloorState{});

        std::stable_sort(
            segments_.begin(),
            segments_.end(),
            [](const EeTrackAnimationSegment& a, const EeTrackAnimationSegment& b)
            {
                if (a.start_floor != b.start_floor)
                    return a.start_floor < b.start_floor;
                return a.source_index < b.source_index;
            });

        for (std::size_t i = 0; i < segments_.size(); ++i)
        {
            const EeTrackAnimationSegment& item = segments_[i];
            if (item.start_floor < 0 || item.end_floor < item.start_floor)
                continue;
            const std::uint32_t first = static_cast<std::uint32_t>(item.start_floor);
            const std::uint32_t last = static_cast<std::uint32_t>(item.end_floor);
            if (first >= segment_by_floor_.size())
                continue;
            for (std::uint32_t floor = first;
                 floor <= last && floor < segment_by_floor_.size();
                 ++floor)
            {
                segment_by_floor_[floor] = i;
                if (floor == std::numeric_limits<std::uint32_t>::max())
                    break;
            }
        }
        active_ = !segments_.empty() && timings_.size() == base_floors_.size();
        return true;
    }
    catch (...)
    {
        segments_.clear();
        timings_.clear();
        segment_by_floor_.assign(base_floors_.size(), NoSegment);
        floor_states_.assign(base_floors_.size(), TrackAnimationFloorState{});
        active_ = false;
        return false;
    }
}

void TrackAnimationRuntime::SetPlaybackAnchor(
    double chart_time,
    double chart_rate,
    std::uint32_t flags) noexcept
{
    std::lock_guard lock(mutex_);
    anchor_chart_time_ = std::isfinite(chart_time) ? chart_time : 0.0;
    chart_rate_ = std::isfinite(chart_rate) && chart_rate > 0.0 ? chart_rate : 1.0;
    playing_ = (flags & EE_PLAYBACK_FLAG_ACTIVE) != 0u &&
               (flags & EE_PLAYBACK_FLAG_PLAYING) != 0u;
    anchor_steady_ = std::chrono::steady_clock::now();
}

double TrackAnimationRuntime::CurrentChartTime() const noexcept
{
    if (!playing_)
        return anchor_chart_time_;
    const double elapsed = std::chrono::duration<double>(
        std::chrono::steady_clock::now() - anchor_steady_).count();
    return anchor_chart_time_ + elapsed * chart_rate_;
}

float TrackAnimationRuntime::Clamp01(double value) noexcept
{
    return static_cast<float>(std::clamp(value, 0.0, 1.0));
}

float TrackAnimationRuntime::OutSine(double progress) noexcept
{
    return std::sin(Clamp01(progress) * Pi * 0.5f);
}

float TrackAnimationRuntime::OutQuad(double progress) noexcept
{
    const float p = Clamp01(progress);
    const float q = 1.0f - p;
    return 1.0f - q * q;
}

float TrackAnimationRuntime::Lerp(float a, float b, float t) noexcept
{
    return a + (b - a) * t;
}

TrackAnimationRuntime::RandomSample TrackAnimationRuntime::MakeRandom(
    std::uint32_t floor,
    std::int32_t source_index,
    float extent) noexcept
{
    const std::uint32_t seed = floor * 0x9e3779b9u ^
        static_cast<std::uint32_t>(source_index) * 0x85ebca6bu ^ 0xc2b2ae35u;
    const float x = (UnitFromHash(seed ^ 0x13a5ba1du) * 2.0f - 1.0f) * extent;
    const float y = (UnitFromHash(seed ^ 0x7f4a7c15u) * 2.0f - 1.0f) * extent;
    const float r = (UnitFromHash(seed ^ 0x94d049bbu) * 2.0f - 1.0f) * 75.0f * DegToRad;
    return RandomSample{x, y, r};
}

std::int64_t TrackAnimationRuntime::CellKey(float x, float y) noexcept
{
    const auto cx = static_cast<std::int32_t>(std::floor(x / CellSize));
    const auto cy = static_cast<std::int32_t>(std::floor(y / CellSize));
    return (static_cast<std::int64_t>(cx) << 32) ^ static_cast<std::uint32_t>(cy);
}

void TrackAnimationRuntime::Reindex(
    std::uint32_t floor,
    float old_x,
    float old_y,
    float new_x,
    float new_y,
    TrackTransformRuntime::CellMap& cells) noexcept
{
    const std::int64_t old_key = CellKey(old_x, old_y);
    const std::int64_t new_key = CellKey(new_x, new_y);
    if (old_key == new_key)
        return;

    if (auto it = cells.find(old_key); it != cells.end())
    {
        auto& bucket = it->second;
        bucket.erase(std::remove(bucket.begin(), bucket.end(), floor), bucket.end());
        if (bucket.empty())
            cells.erase(it);
    }
    cells[new_key].push_back(floor);
}

bool TrackAnimationRuntime::EvaluateForFloor(
    std::uint32_t floor,
    double chart_time,
    const EeFloor& move_resolved,
    const EeFloor* previous_resolved,
    const EeFloor* next_resolved,
    EeFloor& output,
    TrackAnimationFloorState& state) const noexcept
{
    std::lock_guard lock(mutex_);
    output = move_resolved;
    state = TrackAnimationFloorState{};
    if (!active_ || floor >= base_floors_.size() || floor >= timings_.size() ||
        floor >= segment_by_floor_.size())
        return false;

    const std::size_t segment_index = segment_by_floor_[floor];
    if (segment_index == NoSegment || segment_index >= segments_.size())
        return false;

    const EeTrackAnimationSegment& segment = segments_[segment_index];
    const EeTrackAnimationTiming& timing = timings_[floor];
    const EeFloor& base = base_floors_[floor];
    const float speed = timing.speed > 0.000001f ? timing.speed : 1.0f;
    const float appear_ref = segment.appear_reference_speed > 0.000001f
        ? segment.appear_reference_speed : speed;
    const float disappear_ref = segment.disappear_reference_speed > 0.000001f
        ? segment.disappear_reference_speed : speed;
    const double ahead = std::max(0.0f, segment.beats_ahead) * speed / appear_ref;
    const double behind = std::max(0.0f, segment.beats_behind) * speed / disappear_ref;
    const double beat = std::max(0.000001f, timing.beat_seconds_no_pitch);
    const double pitch = std::max(0.000001f, segment.pitch);

    auto set_position = [&](float x, float y)
    {
        // Animate Position and MoveTrack PositionX/Y are distinct DOTween keys.
        // Preserve the move-axis contribution relative to the PositionTrack base.
        output.x = x + (move_resolved.x - base.x);
        output.y = y + (move_resolved.y - base.y);
        state.active_position = true;
    };
    auto set_scale = [&](float x, float y)
    {
        output.transform_scale_x = x + (move_resolved.transform_scale_x - base.transform_scale_x);
        output.transform_scale_y = y + (move_resolved.transform_scale_y - base.transform_scale_y);
        state.active_scale = true;
        output.track_transform_flags |= EE_TRACK_TRANSFORM_ENABLED;
    };
    auto set_rotation = [&](float rotation)
    {
        const float delta = rotation - move_resolved.transform_rotation;
        output.entry_angle += delta;
        output.icon_angle += delta;
        output.transform_rotation = rotation;
        state.active_rotation = true;
        output.track_transform_flags |= EE_TRACK_TRANSFORM_ENABLED;
    };
    auto set_opacity = [&](float opacity)
    {
        output.transform_opacity = opacity;
        state.active_opacity = true;
        output.track_transform_flags |= EE_TRACK_TRANSFORM_ENABLED;
    };

    if (segment.appear_type != EE_TRACK_APPEAR_NONE)
    {
        const bool drop = segment.appear_type == EE_TRACK_APPEAR_DROP ||
                          segment.appear_type == EE_TRACK_APPEAR_RISE;
        const double appear_start = std::max(
            timing.entry_time - (drop ? 2.0 : 1.0) * ahead * beat,
            0.0);
        const double appear_duration = drop
            ? ahead * beat / pitch
            : std::min(0.5 * beat / pitch, 0.5);

        if (chart_time < appear_start + std::max(appear_duration, 0.000001))
        {
            const double elapsed = chart_time - appear_start;
            const float linear = Clamp01(elapsed / std::max(appear_duration, 0.000001));
            const float sine = OutSine(elapsed / std::max(appear_duration, 0.000001));
            const float quad = OutQuad(elapsed / std::max(appear_duration, 0.000001));
            const float extent = segment.appear_type == EE_TRACK_APPEAR_ASSEMBLE_FAR ? 8.0f : 4.0f;
            const RandomSample random = MakeRandom(floor, segment.source_index, extent);

            switch (segment.appear_type)
            {
                case EE_TRACK_APPEAR_ASSEMBLE:
                case EE_TRACK_APPEAR_ASSEMBLE_FAR:
                    set_position(base.x + random.x * (1.0f - sine), base.y + random.y * (1.0f - sine));
                    set_rotation(base.transform_rotation + random.rotation * (1.0f - sine));
                    break;
                case EE_TRACK_APPEAR_EXTEND:
                {
                    const float from_x = previous_resolved != nullptr ? previous_resolved->x : base.x;
                    const float from_y = previous_resolved != nullptr ? previous_resolved->y : base.y;
                    set_position(Lerp(from_x, base.x, sine), Lerp(from_y, base.y, sine));
                    set_scale(sine, sine);
                    state.extend_anim = sine;
                    break;
                }
                case EE_TRACK_APPEAR_GROW:
                    set_scale(quad, quad);
                    break;
                case EE_TRACK_APPEAR_GROW_SPIN:
                    set_scale(quad, quad);
                    set_rotation(base.transform_rotation - (1.0f - sine) * Pi);
                    break;
                case EE_TRACK_APPEAR_FADE:
                    set_opacity(linear);
                    break;
                case EE_TRACK_APPEAR_DROP:
                case EE_TRACK_APPEAR_RISE:
                {
                    const float sign = segment.appear_type == EE_TRACK_APPEAR_DROP ? 1.0f : -1.0f;
                    set_position(base.x, base.y + sign * 8.0f * (1.0f - linear));
                    const double scale_duration = std::max(appear_duration / 8.0, 0.000001);
                    const float scale = OutQuad(elapsed / scale_duration);
                    set_scale(scale, scale);
                    break;
                }
                default:
                    break;
            }
        }
    }

    if (segment.disappear_type != EE_TRACK_DISAPPEAR_NONE && floor + 1u < timings_.size())
    {
        const double disappear_start = timings_[floor + 1u].entry_time + behind * beat;
        const double disappear_duration = std::min(0.5 * beat / pitch, 0.5);
        if (chart_time >= disappear_start)
        {
            const double elapsed = chart_time - disappear_start;
            const float linear = Clamp01(elapsed / std::max(disappear_duration, 0.000001));
            const float sine = OutSine(elapsed / std::max(disappear_duration, 0.000001));
            const float quad = OutQuad(elapsed / std::max(disappear_duration, 0.000001));
            const float extent = segment.disappear_type == EE_TRACK_DISAPPEAR_SCATTER_FAR ? 8.0f : 4.0f;
            const RandomSample random = MakeRandom(floor, segment.source_index ^ 0x5a5a5a5a, extent);

            switch (segment.disappear_type)
            {
                case EE_TRACK_DISAPPEAR_SCATTER:
                case EE_TRACK_DISAPPEAR_SCATTER_FAR:
                    set_position(
                        move_resolved.x + random.x * sine,
                        move_resolved.y + random.y * sine);
                    set_rotation(Lerp(move_resolved.transform_rotation, random.rotation, sine));
                    state.sorting_offset = -1;
                    break;
                case EE_TRACK_DISAPPEAR_RETRACT:
                    if (next_resolved != nullptr)
                    {
                        set_position(
                            Lerp(move_resolved.x, next_resolved->x, sine),
                            Lerp(move_resolved.y, next_resolved->y, sine));
                        set_scale(1.0f - sine, 1.0f - sine);
                        state.sorting_offset = -3;
                    }
                    break;
                case EE_TRACK_DISAPPEAR_SHRINK:
                    set_scale(1.0f - quad, 1.0f - quad);
                    break;
                case EE_TRACK_DISAPPEAR_SHRINK_SPIN:
                    set_scale(1.0f - quad, 1.0f - quad);
                    set_rotation(base.transform_rotation - sine * Pi);
                    break;
                case EE_TRACK_DISAPPEAR_FADE:
                    set_opacity(1.0f - linear);
                    break;
                default:
                    break;
            }
        }
    }

    return true;
}

void TrackAnimationRuntime::Update(
    std::vector<EeFloor>& floors,
    TrackTransformRuntime::CellMap& cells) noexcept
{
    std::lock_guard lock(mutex_);
    if (!active_ || floors.empty())
        return;

    const double chart_time = CurrentChartTime();
    const std::size_t count = std::min({floors.size(), base_floors_.size(), timings_.size()});
    std::vector<EeFloor> move_snapshot(floors.begin(), floors.begin() + count);

    for (std::size_t i = 0; i < count; ++i)
    {
        if (i >= segment_by_floor_.size() || segment_by_floor_[i] == NoSegment)
        {
            floor_states_[i] = TrackAnimationFloorState{};
            continue;
        }

        const EeFloor old = floors[i];
        const EeFloor* previous = i > 0 ? &floors[i - 1] : nullptr;
        const EeFloor* next = i + 1 < count ? &move_snapshot[i + 1] : nullptr;
        EeFloor resolved{};
        TrackAnimationFloorState state{};
        EvaluateForFloor(
            static_cast<std::uint32_t>(i),
            chart_time,
            move_snapshot[i],
            previous,
            next,
            resolved,
            state);
        Reindex(static_cast<std::uint32_t>(i), old.x, old.y, resolved.x, resolved.y, cells);
        floors[i] = resolved;
        floor_states_[i] = state;
    }
}

TrackAnimationFloorState TrackAnimationRuntime::FloorState(std::uint32_t floor) const noexcept
{
    std::lock_guard lock(mutex_);
    if (floor >= floor_states_.size())
        return TrackAnimationFloorState{};
    return floor_states_[floor];
}
}
