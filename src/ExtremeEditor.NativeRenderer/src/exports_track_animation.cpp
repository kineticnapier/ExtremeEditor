#include "extreme_editor_renderer.h"
#include "renderer.h"

EeResult ee_renderer_set_track_animation_timeline(
    EeRendererHandle renderer,
    const EeTrackAnimationSegment* segments,
    uint32_t segment_count,
    const EeTrackAnimationTiming* timings,
    uint32_t timing_count)
{
    if (renderer == nullptr ||
        (segment_count > 0u && segments == nullptr) ||
        (timing_count > 0u && timings == nullptr))
        return EE_ERROR_INVALID_ARGUMENT;

    return static_cast<ee::Renderer*>(renderer)->SetTrackAnimationTimeline(
        segments,
        segment_count,
        timings,
        timing_count)
        ? EE_OK
        : EE_ERROR_INVALID_ARGUMENT;
}
