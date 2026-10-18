#include "d2d_backend.h"
#include "editor_hud_layout.h"

#include <algorithm>
#include <cmath>
#include <cwchar>
#include <string>

namespace ee
{
namespace
{
using Microsoft::WRL::ComPtr;

bool EnsureTextFormat(
    IDWriteFactory* factory,
    ComPtr<IDWriteTextFormat>& format,
    float size,
    DWRITE_FONT_WEIGHT weight) noexcept
{
    if (format)
        return true;
    if (factory == nullptr)
        return false;

    HRESULT hr = factory->CreateTextFormat(
        L"Segoe UI",
        nullptr,
        weight,
        DWRITE_FONT_STYLE_NORMAL,
        DWRITE_FONT_STRETCH_NORMAL,
        size,
        L"en-us",
        format.GetAddressOf());
    if (FAILED(hr))
        return false;

    format->SetTextAlignment(DWRITE_TEXT_ALIGNMENT_CENTER);
    format->SetParagraphAlignment(DWRITE_PARAGRAPH_ALIGNMENT_CENTER);
    return true;
}

void DrawCenteredText(
    ID2D1DeviceContext* context,
    const wchar_t* text,
    IDWriteTextFormat* format,
    ID2D1Brush* brush,
    const D2D1_RECT_F& rect) noexcept
{
    if (context == nullptr || text == nullptr || *text == L'\0' || format == nullptr || brush == nullptr)
        return;

    context->DrawText(
        text,
        static_cast<UINT32>(std::wcslen(text)),
        format,
        rect,
        brush,
        D2D1_DRAW_TEXT_OPTIONS_CLIP,
        DWRITE_MEASURING_MODE_NATURAL);
}
}

// Draw only viewport-visible floor labels, and only when explicitly enabled.
// The label is an overlay (not chart content); neither song playback nor
// headless export includes these numbers.
void D2DBackend::DrawFloorNumbers(
    const LevelScene& scene,
    float camera_x,
    float camera_y,
    float zoom,
    float camera_rotation,
    RenderFrameStats& stats) noexcept
{
    if (!d2d_context_ || !show_floor_numbers_ || zoom < 16.0f)
        return;

    if (!dwrite_factory_)
    {
        HRESULT hr = DWriteCreateFactory(
            DWRITE_FACTORY_TYPE_SHARED,
            __uuidof(IDWriteFactory),
            reinterpret_cast<IUnknown**>(dwrite_factory_.GetAddressOf()));
        if (FAILED(hr))
            return;
    }
    if (!EnsureTextFormat(
            dwrite_factory_.Get(), floor_numbers_text_format_,
            14.0f, DWRITE_FONT_WEIGHT_BOLD))
        return;

    // Brushes belong to the active D2D render target. They must be recreated
    // after swap-chain/target recreation, just like the direction HUD brushes.
    static thread_local ID2D1Bitmap1* floor_numbers_target = nullptr;
    if (floor_numbers_target != target_bitmap_.Get())
    {
        floor_numbers_target = target_bitmap_.Get();
        floor_numbers_brush_.Reset();
        floor_numbers_shadow_brush_.Reset();
    }
    if (!floor_numbers_brush_ &&
        FAILED(d2d_context_->CreateSolidColorBrush(
            D2D1::ColorF(0xF014C8, 1.0f), floor_numbers_brush_.GetAddressOf())))
        return;
    if (!floor_numbers_shadow_brush_ &&
        FAILED(d2d_context_->CreateSolidColorBrush(
            D2D1::ColorF(0x160D1B, 0.85f), floor_numbers_shadow_brush_.GetAddressOf())))
        return;

    const float scale = zoom / 28.0f;
    const float c = std::cos(camera_rotation);
    const float sin_r = std::sin(camera_rotation);
    const float half_w = static_cast<float>(width_) * 0.5f;
    const float half_h = static_cast<float>(height_) * 0.5f;

    for (const std::uint32_t index : visible_candidates_)
    {
        if (index >= scene.floors.size())
            continue;

        const EeFloor& floor = scene.floors[index];
        const float dx = floor.x - camera_x;
        const float dy = floor.y - camera_y;
        const float anchor_x = (c * dx + sin_r * dy) * zoom + half_w;
        const float anchor_y = (sin_r * dx - c * dy) * zoom + half_h;
        // The number follows the same camera zoom as the underlying tile.
        d2d_context_->SetTransform(D2D1::Matrix3x2F::Scale(
            D2D1::SizeF(scale, scale), D2D1::Point2F(anchor_x, anchor_y)));

        const std::wstring label = std::to_wstring(index);
        const D2D1_RECT_F text_rect = D2D1::RectF(
            anchor_x - 30.0f, anchor_y - 14.0f,
            anchor_x + 30.0f, anchor_y + 14.0f);
        const D2D1_RECT_F shadow_rect = D2D1::RectF(
            text_rect.left + 1.0f, text_rect.top + 1.0f,
            text_rect.right + 1.0f, text_rect.bottom + 1.0f);
        DrawCenteredText(
            d2d_context_.Get(), label.c_str(), floor_numbers_text_format_.Get(),
            floor_numbers_shadow_brush_.Get(), shadow_rect);
        DrawCenteredText(
            d2d_context_.Get(), label.c_str(), floor_numbers_text_format_.Get(),
            floor_numbers_brush_.Get(), text_rect);
        stats.draw_calls += 2u;
    }
    d2d_context_->SetTransform(D2D1::Matrix3x2F::Identity());
}

void D2DBackend::DrawEditorHud(
    const LevelScene& scene,
    float camera_x,
    float camera_y,
    float zoom,
    std::int32_t selected_floor,
    RenderFrameStats& stats) noexcept
{
    if (selected_floor < 0 ||
        static_cast<std::size_t>(selected_floor) >= scene.floors.size() ||
        !d2d_context_)
        return;

    zoom = std::clamp(zoom, 0.05f, 400.0f);
    const float hud_scale = EditorHudScaleForZoom(zoom);
    if (hud_scale == 0.0f)
        return;

    // Direct2D brushes are target dependent; rebuild after swap-chain resize.
    static thread_local ID2D1Bitmap1* hud_target = nullptr;
    if (hud_target != target_bitmap_.Get())
    {
        hud_target = target_bitmap_.Get();
        hud_text_brush_.Reset();
        hud_accent_brush_.Reset();
        hud_button_brush_.Reset();
        hud_shadow_brush_.Reset();
    }

    if (!dwrite_factory_)
    {
        HRESULT hr = DWriteCreateFactory(
            DWRITE_FACTORY_TYPE_SHARED,
            __uuidof(IDWriteFactory),
            reinterpret_cast<IUnknown**>(dwrite_factory_.GetAddressOf()));
        if (FAILED(hr))
            return;
    }

    if (!EnsureTextFormat(
            dwrite_factory_.Get(), hud_text_format_, 10.5f, DWRITE_FONT_WEIGHT_BOLD))
        return;

    auto ensure_brush = [this](
        ComPtr<ID2D1SolidColorBrush>& brush,
        D2D1_COLOR_F color) noexcept -> bool
    {
        if (brush)
            return true;
        return SUCCEEDED(d2d_context_->CreateSolidColorBrush(
            color, brush.GetAddressOf()));
    };

    if (!ensure_brush(hud_button_brush_, D2D1::ColorF(0xF6F5F2, 0.98f)) ||
        !ensure_brush(hud_accent_brush_, D2D1::ColorF(0xDCE9FC, 1.0f)) ||
        !ensure_brush(hud_text_brush_, D2D1::ColorF(0x151A23, 1.0f)) ||
        !ensure_brush(hud_shadow_brush_, D2D1::ColorF(0x07090D, 0.48f)))
        return;

    const EeFloor& floor = scene.floors[static_cast<std::size_t>(selected_floor)];
    const float anchor_x =
        (floor.x - camera_x) * zoom + static_cast<float>(width_) * 0.5f;
    const float anchor_y =
        (camera_y - floor.y) * zoom + static_cast<float>(height_) * 0.5f;
    const D2D1::Matrix3x2F hud_transform = D2D1::Matrix3x2F::Scale(
        D2D1::SizeF(hud_scale, hud_scale),
        D2D1::Point2F(anchor_x, anchor_y));

    // Selection is already outlined by DrawSceneOverlaysCamera; the HUD
    // should not paint over the tile itself.
    d2d_context_->SetTransform(hud_transform);

    const auto buttons = BuildEditorHudButtons(anchor_x, anchor_y);
    for (std::size_t i = 0; i < buttons.size(); ++i)
    {
        const EditorHudButton& button = buttons[i];
        const D2D1_RECT_F rect = D2D1::RectF(
            button.center_x - button.half_width,
            button.center_y - button.half_height,
            button.center_x + button.half_width,
            button.center_y + button.half_height);
        const D2D1_ROUNDED_RECT rounded = D2D1::RoundedRect(
            rect, EditorHudCornerRadius, EditorHudCornerRadius);
        const D2D1_ROUNDED_RECT shadow = D2D1::RoundedRect(
            D2D1::RectF(
                rect.left + 0.6f, rect.top + 1.0f,
                rect.right + 0.6f, rect.bottom + 1.0f),
            EditorHudCornerRadius, EditorHudCornerRadius);

        d2d_context_->FillRoundedRectangle(shadow, hud_shadow_brush_.Get());
        d2d_context_->FillRoundedRectangle(
            rounded, static_cast<int>(i) == editor_hud_hover_
                ? hud_accent_brush_.Get()
                : hud_button_brush_.Get());
        d2d_context_->DrawRoundedRectangle(rounded, border_brush_.Get(), 1.0f);
        DrawCenteredText(
            d2d_context_.Get(),
            button.label,
            hud_text_format_.Get(),
            hud_text_brush_.Get(),
            rect);
        stats.draw_calls += 4u;
    }

    d2d_context_->SetTransform(D2D1::Matrix3x2F::Identity());
}
}
