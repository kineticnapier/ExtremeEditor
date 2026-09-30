#pragma once

#include <d2d1_1.h>
#include <wrl/client.h>

#include <cstdint>

namespace ee
{
Microsoft::WRL::ComPtr<ID2D1Effect> CreateDecorationMaskCoverage(
    ID2D1DeviceContext* context,
    ID2D1Effect* mask,
    std::uint32_t viewport_width,
    std::uint32_t viewport_height,
    bool visible_outside) noexcept;
}
