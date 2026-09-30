#include "decoration_mask_effects.h"

#include <d2d1effects.h>

namespace ee
{
Microsoft::WRL::ComPtr<ID2D1Effect> CreateDecorationMaskCoverage(
    ID2D1DeviceContext* context,
    ID2D1Effect* mask,
    std::uint32_t viewport_width,
    std::uint32_t viewport_height,
    bool visible_outside) noexcept
{
    (void)viewport_width;
    (void)viewport_height;

    if (!context || !mask)
        return {};
    if (!visible_outside)
    {
        Microsoft::WRL::ComPtr<ID2D1Effect> result = mask;
        return result;
    }

    Microsoft::WRL::ComPtr<ID2D1Effect> inverted;
    if (FAILED(context->CreateEffect(CLSID_D2D1ColorMatrix, inverted.GetAddressOf())))
        return {};

    const D2D1_MATRIX_5X4_F matrix =
    {
        1.0f, 0.0f, 0.0f, 0.0f,
        0.0f, 1.0f, 0.0f, 0.0f,
        0.0f, 0.0f, 1.0f, 0.0f,
        0.0f, 0.0f, 0.0f, -1.0f,
        0.0f, 0.0f, 0.0f, 1.0f
    };
    inverted->SetInputEffect(0, mask);
    if (FAILED(inverted->SetValue(D2D1_COLORMATRIX_PROP_COLOR_MATRIX, matrix)) ||
        FAILED(inverted->SetValue(
            D2D1_COLORMATRIX_PROP_ALPHA_MODE,
            D2D1_COLORMATRIX_ALPHA_MODE_PREMULTIPLIED)))
    {
        return {};
    }
    return inverted;
}
}
