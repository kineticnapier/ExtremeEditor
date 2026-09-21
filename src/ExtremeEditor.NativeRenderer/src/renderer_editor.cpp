#include "renderer.h"
#include "editor_hud_layout.h"

#include <algorithm>
#include <windowsx.h>

namespace ee
{
void Renderer::SetSelection(
    const std::int32_t* floors,
    std::uint32_t floor_count,
    std::int32_t primary_floor) noexcept
{
    std::lock_guard lock(scene_mutex_);

    selected_floors_.clear();
    if (floors != nullptr && floor_count > 0)
    {
        selected_floors_.reserve(floor_count);
        for (std::uint32_t i = 0; i < floor_count; ++i)
        {
            const std::int32_t floor = floors[i];
            if (floor < 0)
                continue;
            if (scene_ && static_cast<std::size_t>(floor) >= scene_->floors.size())
                continue;
            selected_floors_.push_back(floor);
        }
        std::sort(selected_floors_.begin(), selected_floors_.end());
        selected_floors_.erase(
            std::unique(selected_floors_.begin(), selected_floors_.end()),
            selected_floors_.end());
    }

    if (primary_floor >= 0 &&
        (!scene_ || static_cast<std::size_t>(primary_floor) < scene_->floors.size()))
    {
        selected_floor_ = primary_floor;
        if (!std::binary_search(selected_floors_.begin(), selected_floors_.end(), primary_floor))
        {
            selected_floors_.push_back(primary_floor);
            std::sort(selected_floors_.begin(), selected_floors_.end());
        }
    }
    else
    {
        selected_floor_ = selected_floors_.empty() ? -1 : selected_floors_.back();
    }
}

void Renderer::SetEditorActionCallback(
    EeEditorActionCallback callback,
    void* user_data) noexcept
{
    std::lock_guard lock(scene_mutex_);
    editor_action_callback_ = callback;
    editor_action_user_data_ = user_data;
}

int Renderer::HitTestEditorHud(int screen_x, int screen_y) noexcept
{
    std::lock_guard lock(scene_mutex_);
    if (!scene_ || playback_active_ || selected_floor_ < 0 ||
        static_cast<std::size_t>(selected_floor_) >= scene_->floors.size())
        return -1;

    const EeFloor& floor = scene_->floors[static_cast<std::size_t>(selected_floor_)];
    const float width = static_cast<float>(width_.load(std::memory_order_relaxed));
    const float height = static_cast<float>(height_.load(std::memory_order_relaxed));
    const float anchor_x = (floor.x - camera_x_) * zoom_ + width * 0.5f;
    const float anchor_y = (camera_y_ - floor.y) * zoom_ + height * 0.5f;

    const auto buttons = BuildEditorHudButtons(anchor_x, anchor_y);
    for (std::size_t i = 0; i < buttons.size(); ++i)
    {
        if (HitTestEditorHudButton(
                buttons[i],
                static_cast<float>(screen_x),
                static_cast<float>(screen_y)))
            return static_cast<int>(i);
    }
    return -1;
}

void Renderer::NotifyEditorAction(
    EeEditorAction action,
    std::int32_t floor,
    double value) noexcept
{
    EeEditorActionCallback callback = nullptr;
    void* user_data = nullptr;
    {
        std::lock_guard lock(scene_mutex_);
        callback = editor_action_callback_;
        user_data = editor_action_user_data_;
    }

    if (callback != nullptr)
        callback(user_data, static_cast<std::uint32_t>(action), floor, value);
}

bool Renderer::HandleEditorHudMessage(
    HWND hwnd,
    UINT message,
    WPARAM,
    LPARAM lparam,
    LRESULT& result) noexcept
{
    if (message == WM_MOUSEMOVE)
    {
        const int hovered = HitTestEditorHud(GET_X_LPARAM(lparam), GET_Y_LPARAM(lparam));
        {
            std::lock_guard lock(scene_mutex_);
            hover_hud_button_ = hovered;
        }
        if (!panning_)
            SetCursor(LoadCursorW(nullptr, hovered >= 0 ? IDC_HAND : IDC_ARROW));
        return false;
    }

    if (message != WM_LBUTTONDOWN)
        return false;

    const int index = HitTestEditorHud(GET_X_LPARAM(lparam), GET_Y_LPARAM(lparam));
    if (index < 0)
        return false;

    EeEditorAction action = EE_EDITOR_ACTION_NONE;
    double value = 0.0;
    std::int32_t floor = -1;
    {
        std::lock_guard lock(scene_mutex_);
        if (!scene_ || playback_active_ || selected_floor_ < 0 ||
            static_cast<std::size_t>(selected_floor_) >= scene_->floors.size())
            return false;

        const EeFloor& selected = scene_->floors[static_cast<std::size_t>(selected_floor_)];
        const float width = static_cast<float>(width_.load(std::memory_order_relaxed));
        const float height = static_cast<float>(height_.load(std::memory_order_relaxed));
        const float anchor_x = (selected.x - camera_x_) * zoom_ + width * 0.5f;
        const float anchor_y = (camera_y_ - selected.y) * zoom_ + height * 0.5f;
        const auto buttons = BuildEditorHudButtons(anchor_x, anchor_y);
        if (static_cast<std::size_t>(index) >= buttons.size())
            return false;

        const EditorHudButton& button = buttons[static_cast<std::size_t>(index)];
        action = button.action;
        value = button.value;
        floor = selected_floor_;
    }

    if (action == EE_EDITOR_ACTION_NONE)
        return false;

    SetFocus(hwnd);
    NotifyEditorAction(action, floor, value);
    result = 0;
    return true;
}
}
