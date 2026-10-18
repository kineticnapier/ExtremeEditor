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
    if (selected_floor < 0 || static_cast<std::size_t>(selected_floor) >= scene.floors.size() || !d2d_context_)
        return;

    // The target bitmap changes whenever the swap chain is recreated or resized.
    // Reset the cached device-dependent HUD resources at the same boundary.
    static thread_local ID2D1Bitmap1* hud_target = nullptr;
    if (hud_target != target_bitmap_.Get())
    {
        hud_target = target_bitmap_.Get();
        hud_subtext_brush_.Reset();
        hud_text_brush_.Reset();
        hud_accent_brush_.Reset();
        hud_destructive_brush_.Reset();
        hud_button_brush_.Reset();
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

    if (!EnsureTextFormat(dwrite_factory_.Get(), hud_text_format_, 16.0f, DWRITE_FONT_WEIGHT_BOLD) ||
        !EnsureTextFormat(dwrite_factory_.Get(), hud_subtext_format_, 9.0f, DWRITE_FONT_WEIGHT_SEMI_BOLD))
        return;

    auto ensure_brush = [this](ComPtr<ID2D1SolidColorBrush>& brush, D2D1_COLOR_F color) noexcept -> bool
    {
        if (brush)
            return true;
        return SUCCEEDED(d2d_context_->CreateSolidColorBrush(color, brush.GetAddressOf()));
    };

    if (!ensure_brush(hud_button_brush_, D2D1::ColorF(0xF8F7F2, 0.98f)) ||
        !ensure_brush(hud_destructive_brush_, D2D1::ColorF(0xFA575C, 0.98f)) ||
        !ensure_brush(hud_accent_brush_, D2D1::ColorF(0x38D870, 0.95f)) ||
        !ensure_brush(hud_text_brush_, D2D1::ColorF(0x111318, 1.0f)) ||
        !ensure_brush(hud_subtext_brush_, D2D1::ColorF(0xF22E43, 1.0f)))
        return;

    zoom = std::clamp(zoom, 0.05f, 400.0f);
    const float hud_scale = EditorHudScaleForZoom(zoom);
    if (hud_scale == 0.0f)
        return;
    const EeFloor& floor = scene.floors[static_cast<std::size_t>(selected_floor)];
    const float anchor_x = (floor.x - camera_x) * zoom + static_cast<float>(width_) * 0.5f;
    const float anchor_y = (camera_y - floor.y) * zoom + static_cast<float>(height_) * 0.5f;

    // Editor chrome shares the floor's world-to-screen zoom. Scale about the
    // projected floor anchor so the relative button positions, shapes, text,
    // and borders change proportionally with the rendered tile.
    const D2D1::Matrix3x2F hud_transform = D2D1::Matrix3x2F::Scale(
        D2D1::SizeF(hud_scale, hud_scale),
        D2D1::Point2F(anchor_x, anchor_y));
    // The selection outline is drawn by DrawSceneOverlaysCamera.
    // Do not cover the selected tile with a permanent green floor-number badge.
    d2d_context_->SetTransform(hud_transform);

    const auto buttons = BuildEditorHudButtons(anchor_x, anchor_y);
    constexpr float inverse_sqrt_two = 0.70710678f;
    for (const EditorHudButton& button : buttons)
    {
        ID2D1SolidColorBrush* fill = button.destructive
            ? hud_destructive_brush_.Get()
            : button.accent
                ? hud_accent_brush_.Get()
                : hud_button_brush_.Get();

        if (button.shape == EditorHudShape::Diamond)
        {
            const float half = button.half_width * inverse_sqrt_two;
            d2d_context_->SetTransform(
                D2D1::Matrix3x2F::Rotation(
                    45.0f,
                    D2D1::Point2F(button.center_x, button.center_y)) *
                hud_transform);
            const D2D1_RECT_F rect = D2D1::RectF(
                button.center_x - half,
                button.center_y - half,
                button.center_x + half,
                button.center_y + half);
            d2d_context_->FillRectangle(rect, fill);
            d2d_context_->DrawRectangle(rect, border_brush_.Get(), 1.0f);
            d2d_context_->SetTransform(hud_transform);
        }
        else
        {
            const D2D1_ROUNDED_RECT rounded = D2D1::RoundedRect(
                D2D1::RectF(
                    button.center_x - button.half_width,
                    button.center_y - button.half_height,
                    button.center_x + button.half_width,
                    button.center_y + button.half_height),
                5.0f,
                5.0f);
            d2d_context_->FillRoundedRectangle(rounded, fill);
            d2d_context_->DrawRoundedRectangle(rounded, border_brush_.Get(), 1.0f);
        }
        stats.draw_calls += 2u;

        ID2D1Brush* main_text_brush = button.destructive
            ? static_cast<ID2D1Brush*>(planet_outline_brush_.Get())
            : static_cast<ID2D1Brush*>(hud_text_brush_.Get());
        const float top = button.center_y - (button.sublabel[0] == L'\0' ? 13.0f : 16.0f);
        const float bottom = button.center_y + (button.sublabel[0] == L'\0' ? 13.0f : 5.0f);
        DrawCenteredText(
            d2d_context_.Get(),
            button.label,
            hud_text_format_.Get(),
            main_text_brush,
            D2D1::RectF(
                button.center_x - button.half_width,
                top,
                button.center_x + button.half_width,
                bottom));
        ++stats.draw_calls;

        if (button.sublabel[0] != L'\0')
        {
            DrawCenteredText(
                d2d_context_.Get(),
                button.sublabel,
                hud_subtext_format_.Get(),
                hud_subtext_brush_.Get(),
                D2D1::RectF(
                    button.center_x - button.half_width,
                    button.center_y + 3.0f,
                    button.center_x + button.half_width,
                    button.center_y + 18.0f));
            ++stats.draw_calls;
        }
    }

    d2d_context_->SetTransform(D2D1::Matrix3x2F::Identity());
}
}
