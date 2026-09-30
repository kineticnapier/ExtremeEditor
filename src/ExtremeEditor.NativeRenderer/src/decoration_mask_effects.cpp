#include "decoration_mask_effects.h"

#include <d2d1effects.h>
#include <d2d1effects_2.h>

namespace ee
{
Microsoft::WRL::ComPtr<ID2D1Effect> CreateDecorationMaskCoverage(
    ID2D1DeviceContext* context,
    ID2D1Effect* mask,
    std::uint32_t viewport_width,
    std::uint32_t viewport_height,
    bool visible_outside) noexcept
{
    if (!context || !mask || viewport_width == 0u || viewport_height == 0u)
        return {};

    const D2D1_RECT_F viewport = D2D1::RectF(
        0.0f,
        0.0f,
        static_cast<float>(viewport_width),
        static_cast<float>(viewport_height));

    Microsoft::WRL::ComPtr<ID2D1Effect> transparent_flood;
    Microsoft::WRL::ComPtr<ID2D1Effect> viewport_base;
    Microsoft::WRL::ComPtr<ID2D1Effect> mask_union;
    Microsoft::WRL::ComPtr<ID2D1Effect> inside_mask;
    if (FAILED(context->CreateEffect(CLSID_D2D1Flood, transparent_flood.GetAddressOf())) ||
        FAILED(transparent_flood->SetValue(
            D2D1_FLOOD_PROP_COLOR,
            D2D1::Vector4F(0.0f, 0.0f, 0.0f, 0.0f))) ||
        FAILED(context->CreateEffect(CLSID_D2D1Crop, viewport_base.GetAddressOf())) ||
        FAILED(viewport_base->SetValue(D2D1_CROP_PROP_RECT, viewport)) ||
        FAILED(context->CreateEffect(CLSID_D2D1Composite, mask_union.GetAddressOf())) ||
        FAILED(mask_union->SetInputCount(2u)) ||
        FAILED(mask_union->SetValue(
            D2D1_COMPOSITE_PROP_MODE,
            D2D1_COMPOSITE_MODE_SOURCE_OVER)) ||
        FAILED(context->CreateEffect(CLSID_D2D1Crop, inside_mask.GetAddressOf())) ||
        FAILED(inside_mask->SetValue(D2D1_CROP_PROP_RECT, viewport)))
    {
        return {};
    }

    viewport_base->SetInputEffect(0, transparent_flood.Get());
    mask_union->SetInputEffect(0, viewport_base.Get());
    mask_union->SetInputEffect(1, mask);
    inside_mask->SetInputEffect(0, mask_union.Get());

    if (!visible_outside)
        return inside_mask;

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
    inverted->SetInputEffect(0, inside_mask.Get());
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
