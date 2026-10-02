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
    move_resolved_floors_ = floors;
    last_outputs_ = floors;
    has_last_output_.assign(floors.size(), false);
    competing_events_by_floor_.clear();
    active_ = false;
    cull_margin_ = 0.0f;
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
        if (segment_count == 0u)
            segments_.clear();
        else
            segments_.assign(segments, segments + segment_count);
        if (timing_count == 0u)
            timings_.clear();
        else
            timings_.assign(timings, timings + timing_count);
        segment_by_floor_.assign(base_floors_.size(), NoSegment);
        floor_states_.assign(base_floors_.size(), TrackAnimationFloorState{});
        move_resolved_floors_ = base_floors_;
        last_outputs_ = base_floors_;
        has_last_output_.assign(base_floors_.size(), false);
        cull_margin_ = 0.0f;

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
                if (item.appear_type == EE_TRACK_APPEAR_EXTEND && floor > 0u)
                {
                    cull_margin_ = std::max(
                        cull_margin_,
                        std::hypot(
                            base_floors_[floor].x - base_floors_[floor - 1u].x,
                            base_floors_[floor].y - base_floors_[floor - 1u].y));
                }
                if (item.disappear_type == EE_TRACK_DISAPPEAR_RETRACT &&
                    floor + 1u < base_floors_.size())
                {
                    cull_margin_ = std::max(
                        cull_margin_,
                        std::hypot(
                            base_floors_[floor + 1u].x - base_floors_[floor].x,
                            base_floors_[floor + 1u].y - base_floors_[floor].y));
                }
                if (floor == std::numeric_limits<std::uint32_t>::max())
                    break;
            }

            if (item.appear_type == EE_TRACK_APPEAR_ASSEMBLE ||
                item.appear_type == EE_TRACK_APPEAR_ASSEMBLE_FAR ||
                item.appear_type == EE_TRACK_APPEAR_DROP ||
                item.appear_type == EE_TRACK_APPEAR_RISE ||
                item.disappear_type == EE_TRACK_DISAPPEAR_SCATTER ||
                item.disappear_type == EE_TRACK_DISAPPEAR_SCATTER_FAR)
            {
                cull_margin_ = std::max(cull_margin_, 8.0f);
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
        move_resolved_floors_ = base_floors_;
        last_outputs_ = base_floors_;
        has_last_output_.assign(base_floors_.size(), false);
        active_ = false;
        cull_margin_ = 0.0f;
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

void TrackAnimationRuntime::SetCompetingTimeline(
    const EeTrackTransformEvent* events,
    std::uint32_t event_count) noexcept
{
    std::lock_guard lock(mutex_);
    competing_events_by_floor_.clear();
    if (events == nullptr || event_count == 0u)
        return;

    try
    {
        for (std::uint32_t i = 0; i < event_count; ++i)
        {
            const EeTrackTransformEvent& item = events[i];
            if (item.floor < 0 ||
                (item.flags & (EE_TRACK_TRANSFORM_ROTATION | EE_TRACK_TRANSFORM_OPACITY)) == 0u)
                continue;
            competing_events_by_floor_[static_cast<std::uint32_t>(item.floor)].push_back(item);
        }
        for (auto& [floor, items] : competing_events_by_floor_)
        {
            (void)floor;
            std::stable_sort(
                items.begin(),
                items.end(),
                [](const EeTrackTransformEvent& left, const EeTrackTransformEvent& right)
                {
                    if (left.start_time != right.start_time)
                        return left.start_time < right.start_time;
                    return left.reserved < right.reserved;
                });
        }
    }
    catch (...)
    {
        competing_events_by_floor_.clear();
    }
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

bool TrackAnimationRuntime::SameTransformState(const EeFloor& left, const EeFloor& right) noexcept
{
    return left.x == right.x && left.y == right.y &&
           left.entry_angle == right.entry_angle && left.icon_angle == right.icon_angle &&
           left.transform_scale_x == right.transform_scale_x &&
           left.transform_scale_y == right.transform_scale_y &&
           left.transform_rotation == right.transform_rotation &&
           left.transform_opacity == right.transform_opacity &&
           left.track_transform_flags == right.track_transform_flags;
}

EeFloor TrackAnimationRuntime::CaptureMoveResolved(
    std::size_t floor,
    const EeFloor& candidate) noexcept
{
    if (floor >= move_resolved_floors_.size())
        return candidate;

    if (floor >= has_last_output_.size() || !has_last_output_[floor] ||
        floor >= last_outputs_.size() || !SameTransformState(candidate, last_outputs_[floor]))
    {
        move_resolved_floors_[floor] = candidate;
    }
    return move_resolved_floors_[floor];
}

bool TrackAnimationRuntime::OwnsSharedChannel(
    std::uint32_t floor,
    double animation_start,
    std::int32_t animation_source_index,
    std::uint32_t channel,
    double chart_time) const noexcept
{
    const auto found = competing_events_by_floor_.find(floor);
    if (found == competing_events_by_floor_.end())
        return true;

    const EeTrackTransformEvent* last = nullptr;
    for (const EeTrackTransformEvent& item : found->second)
    {
        if (item.start_time > chart_time)
            break;
        if ((item.flags & channel) != 0u)
            last = &item;
    }
    if (last == nullptr)
        return true;
    if (last->start_time > animation_start)
        return false;
    if (last->start_time < animation_start)
        return true;
    const std::uint32_t animation_source = animation_source_index >= 0
        ? static_cast<std::uint32_t>(animation_source_index)
        : 0u;
    return last->reserved <= animation_source;
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
    output.track_extend_anim = 1.0f;
    output.track_sorting_offset = 0;
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
    bool animate_rotation_owns = true;
    bool animate_opacity_owns = true;

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
        if (!animate_rotation_owns)
            return;
        const float delta = rotation - move_resolved.transform_rotation;
        output.entry_angle += delta;
        output.icon_angle += delta;
        output.transform_rotation = rotation;
        state.active_rotation = true;
        output.track_transform_flags |= EE_TRACK_TRANSFORM_ENABLED;
    };
    auto set_opacity = [&](float opacity)
    {
        if (!animate_opacity_owns)
            return;
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
        animate_rotation_owns = OwnsSharedChannel(
            floor, appear_start, segment.source_index, EE_TRACK_TRANSFORM_ROTATION, chart_time);
        animate_opacity_owns = OwnsSharedChannel(
            floor, appear_start, segment.source_index, EE_TRACK_TRANSFORM_OPACITY, chart_time);

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
                    output.track_extend_anim = sine;
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

    if (segment.disappear_type != EE_TRACK_DISAPPEAR_NONE && floor + 1u < timings_.size())
    {
        const double disappear_start = timings_[floor + 1u].entry_time + behind * beat;
        const double disappear_duration = std::min(0.5 * beat / pitch, 0.5);
        if (chart_time >= disappear_start)
        {
            animate_rotation_owns = OwnsSharedChannel(
                floor, disappear_start, segment.source_index, EE_TRACK_TRANSFORM_ROTATION, chart_time);
            animate_opacity_owns = OwnsSharedChannel(
                floor, disappear_start, segment.source_index, EE_TRACK_TRANSFORM_OPACITY, chart_time);
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
                    set_rotation(Lerp(
                        move_resolved.transform_rotation,
                        move_resolved.transform_rotation + random.rotation,
                        sine));
                    state.sorting_offset = -1;
                    output.track_sorting_offset = -1;
                    break;
                case EE_TRACK_DISAPPEAR_RETRACT:
                    if (next_resolved != nullptr)
                    {
                        set_position(
                            Lerp(move_resolved.x, next_resolved->x, sine),
                            Lerp(move_resolved.y, next_resolved->y, sine));
                        set_scale(1.0f - sine, 1.0f - sine);
                        state.sorting_offset = -3;
                        output.track_sorting_offset = -3;
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
    for (std::size_t i = 0; i < count; ++i)
        CaptureMoveResolved(i, floors[i]);

    for (std::size_t i = 0; i < count; ++i)
    {
        if (i >= segment_by_floor_.size() || segment_by_floor_[i] == NoSegment)
        {
            const EeFloor old = floors[i];
            floors[i] = move_resolved_floors_[i];
            floors[i].track_extend_anim = 1.0f;
            floors[i].track_sorting_offset = 0;
            Reindex(static_cast<std::uint32_t>(i), old.x, old.y, floors[i].x, floors[i].y, cells);
            floor_states_[i] = TrackAnimationFloorState{};
            last_outputs_[i] = floors[i];
            has_last_output_[i] = true;
            continue;
        }

        const EeFloor old = floors[i];
        const EeFloor* previous = i > 0 ? &floors[i - 1] : nullptr;
        const EeFloor* next = i + 1 < count ? &move_resolved_floors_[i + 1] : nullptr;
        EeFloor resolved{};
        TrackAnimationFloorState state{};
        EvaluateForFloor(
            static_cast<std::uint32_t>(i),
            chart_time,
            move_resolved_floors_[i],
            previous,
            next,
            resolved,
            state);
        Reindex(static_cast<std::uint32_t>(i), old.x, old.y, resolved.x, resolved.y, cells);
        floors[i] = resolved;
        floor_states_[i] = state;
        last_outputs_[i] = resolved;
        has_last_output_[i] = true;
    }
}

void TrackAnimationRuntime::UpdateVisible(
    std::vector<EeFloor>& floors,
    TrackTransformRuntime::CellMap& cells,
    const std::vector<std::uint32_t>& visible_floors) noexcept
{
    std::lock_guard lock(mutex_);
    if (!active_ || floors.empty())
        return;

    const double chart_time = CurrentChartTime();
    for (const std::uint32_t floor : visible_floors)
    {
        if (floor >= floors.size() || floor >= base_floors_.size() ||
            floor >= timings_.size() || floor >= segment_by_floor_.size() ||
            segment_by_floor_[floor] == NoSegment)
            continue;

        const EeFloor old = floors[floor];
        const EeFloor move_resolved = CaptureMoveResolved(floor, old);
        EeFloor previous_value{};
        const EeFloor* previous = nullptr;
        if (floor > 0u)
        {
            const std::uint32_t previous_floor = floor - 1u;
            const EeFloor previous_move = CaptureMoveResolved(previous_floor, floors[previous_floor]);
            TrackAnimationFloorState ignored{};
            const EeFloor* previous_previous = previous_floor > 0u
                ? &move_resolved_floors_[previous_floor - 1u]
                : nullptr;
            EvaluateForFloor(
                previous_floor,
                chart_time,
                previous_move,
                previous_previous,
                &move_resolved,
                previous_value,
                ignored);
            previous = &previous_value;
        }

        EeFloor next_value{};
        const EeFloor* next = nullptr;
        if (floor + 1u < move_resolved_floors_.size())
        {
            const std::uint32_t next_floor = floor + 1u;
            const EeFloor next_move = CaptureMoveResolved(next_floor, floors[next_floor]);
            double capture_time = chart_time;
            const std::size_t segment_index = segment_by_floor_[floor];
            if (segment_index < segments_.size() &&
                segments_[segment_index].disappear_type == EE_TRACK_DISAPPEAR_RETRACT)
            {
                const EeTrackAnimationSegment& segment = segments_[segment_index];
                const EeTrackAnimationTiming& timing = timings_[floor];
                const float speed = timing.speed > 0.000001f ? timing.speed : 1.0f;
                const float reference = segment.disappear_reference_speed > 0.000001f
                    ? segment.disappear_reference_speed
                    : speed;
                const double effective_beats = std::max(0.0f, segment.beats_behind) * speed / reference;
                capture_time = timings_[next_floor].entry_time +
                    effective_beats * std::max(0.000001f, timing.beat_seconds_no_pitch);
            }
            TrackAnimationFloorState ignored{};
            const EeFloor* next_next = next_floor + 1u < move_resolved_floors_.size()
                ? &move_resolved_floors_[next_floor + 1u]
                : nullptr;
            EvaluateForFloor(
                next_floor,
                capture_time,
                next_move,
                &move_resolved,
                next_next,
                next_value,
                ignored);
            next = &next_value;
        }
        EeFloor resolved{};
        TrackAnimationFloorState state{};
        EvaluateForFloor(floor, chart_time, move_resolved, previous, next, resolved, state);
        Reindex(floor, old.x, old.y, resolved.x, resolved.y, cells);
        floors[floor] = resolved;
        floor_states_[floor] = state;
        last_outputs_[floor] = resolved;
        has_last_output_[floor] = true;
    }
}

TrackAnimationFloorState TrackAnimationRuntime::FloorState(std::uint32_t floor) const noexcept
{
    std::lock_guard lock(mutex_);
    if (floor >= floor_states_.size())
        return TrackAnimationFloorState{};
    return floor_states_[floor];
}

float TrackAnimationRuntime::CullMargin() const noexcept
{
    std::lock_guard lock(mutex_);
    return active_ ? cull_margin_ : 0.0f;
}
}
