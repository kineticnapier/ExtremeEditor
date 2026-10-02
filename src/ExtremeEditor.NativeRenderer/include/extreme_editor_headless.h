#pragma once

#include "extreme_editor_renderer.h"

#ifdef __cplusplus
extern "C" {
#endif

// Experimental headless extension. The base renderer ABI remains version 20;
// this additive surface is intentionally kept separate while the DMDOD B0
// contract settles.
EE_RENDERER_API EeResult ee_renderer_create_headless(
    const EeRendererCreateInfo* info,
    EeRendererHandle* out_renderer);

// Render one deterministic RGB888 frame. scene_time drives chart state while
// visual_time drives unscaled/realtime visual loops such as Glow/Blink/Rainbow.
// row_stride is bytes per output row and must be at least width * 3.
EE_RENDERER_API EeResult ee_renderer_render_rgb(
    EeRendererHandle renderer,
    double scene_time,
    double visual_time,
    uint8_t* rgb,
    uint32_t rgb_size,
    uint32_t row_stride);

#ifdef __cplusplus
}
#endif
