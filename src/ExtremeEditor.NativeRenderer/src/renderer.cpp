#include "renderer.h"
#include "d2d_backend.h"

#include <algorithm>
#include <chrono>
#include <cmath>
#include <limits>
#include <vector>
#include <windowsx.h>

namespace ee
{
Renderer::~Renderer()
{
    StopRenderThread();
}

bool Renderer::Initialize(HWND parent, std::uint32_t width, std::uint32_t height) noexcept
{
    StopRenderThread();

    const std::uint32_t safe_width = std::max<std::uint32_t>(1u, width);
    const std::uint32_t safe_height = std::max<std::uint32_t>(1u, height);
    if (!window_.Create(parent, safe_width, safe_height, this))
        return false;

    width_.store(safe_width, std::memory_order_relaxed);
    height_.store(safe_height, std::memory_order_relaxed);
    resize_pending_.store(false, std::memory_order_relaxed);
    stop_requested_.store(false, std::memory_order_release);

    {
        std::lock_guard lock(initialize_mutex_);
        initialize_complete_ = false;
        initialize_success_ = false;
    }

    try
    {
        render_thread_ = std::thread(&Renderer::RenderLoop, this);
    }
    catch (...)
    {
        window_.Destroy();
        return false;
    }

    std::unique_lock lock(initialize_mutex_);
    const bool signaled = initialize_cv_.wait_for(
        lock,
        std::chrono::seconds(5),
        [this] { return initialize_complete_; });

    if (!signaled || !initialize_success_)
    {
        lock.unlock();
        StopRenderThread();
        window_.Destroy();
        return false;
    }

    return true;
}

void Renderer::Resize(std::uint32_t width, std::uint32_t height) noexcept
{
    const std::uint32_t safe_width = std::max<std::uint32_t>(1u, width);
    const std::uint32_t safe_height = std::max<std::uint32_t>(1u, height);

    window_.Resize(safe_width, safe_height);
    width_.store(safe_width, std::memory_order_relaxed);
    height_.store(safe_height, std::memory_order_relaxed);
    resize_pending_.store(true, std::memory_order_release);
}

bool Renderer::SetLevel(std::shared_ptr<LevelScene> scene) noexcept
{
    if (!scene || scene->floors.empty())
        return false;

    std::lock_guard lock(scene_mutex_);
    scene_ = std::move(scene);
    camera_x_ = scene_->floors.front().x;
    camera_y_ = scene_->floors.front().y;
    zoom_ = 28.0f;
    selected_floor_ = -1;
    ++scene_version_;
    return true;
}

void Renderer::FrameAll() noexcept
{
    std::lock_guard lock(scene_mutex_);
    if (!scene_)
        return;

    const float left = scene_->bounds_left;
    const float top = scene_->bounds_top;
    const float right = scene_->bounds_right;
    const float bottom = scene_->bounds_bottom;
    const float bounds_width = std::max(1.0f, right - left);
    const float bounds_height = std::max(1.0f, bottom - top);
    const float available_width = std::max(1.0f, static_cast<float>(width_.load(std::memory_order_relaxed)) - 80.0f);
    const float available_height = std::max(1.0f, static_cast<float>(height_.load(std::memory_order_relaxed)) - 80.0f);

    camera_x_ = (left + right) * 0.5f;
    camera_y_ = (top + bottom) * 0.5f;
    zoom_ = std::clamp(std::min(available_width / bounds_width, available_height / bounds_height), 0.05f, 400.0f);
}

void Renderer::ClearIconAssets() noexcept
{
    std::lock_guard lock(scene_mutex_);
    icon_assets_ = std::make_shared<IconAssetTable>();
    ++icon_assets_version_;
}

bool Renderer::SetIconAsset(
    std::uint32_t icon_id,
    const wchar_t* image_path,
    const wchar_t* outline_path) noexcept
{
    if (image_path == nullptr || *image_path == L'\0')
        return false;

    try
    {
        std::lock_guard lock(scene_mutex_);
        auto next = std::make_shared<IconAssetTable>(icon_assets_ ? *icon_assets_ : IconAssetTable{});
        IconAsset asset;
        asset.image_path = image_path;
        if (outline_path != nullptr)
            asset.outline_path = outline_path;
        (*next)[icon_id] = std::move(asset);
        icon_assets_ = std::move(next);
        ++icon_assets_version_;
        return true;
    }
    catch (...)
    {
        return false;
    }
}

void Renderer::SetSelectionChangedCallback(
    EeSelectionChangedCallback callback,
    void* user_data) noexcept
{
    std::lock_guard lock(scene_mutex_);
    selection_callback_ = callback;
    selection_user_data_ = user_data;
}

std::int32_t Renderer::SelectedFloor() const noexcept
{
    std::lock_guard lock(scene_mutex_);
    return selected_floor_;
}

std::int32_t Renderer::SelectFloorAt(int screen_x, int screen_y) noexcept
{
    std::lock_guard lock(scene_mutex_);
    if (!scene_ || zoom_ <= 0.0f)
    {
        selected_floor_ = -1;
        return selected_floor_;
    }

    const float width = static_cast<float>(width_.load(std::memory_order_relaxed));
    const float height = static_cast<float>(height_.load(std::memory_order_relaxed));
    const float world_x = (static_cast<float>(screen_x) - width * 0.5f) / zoom_ + camera_x_;
    const float world_y = camera_y_ - (static_cast<float>(screen_y) - height * 0.5f) / zoom_;
    const float radius = std::max(12.0f / zoom_, 0.856f);

    std::vector<std::uint32_t> candidates;
    scene_->Query(
        world_x - radius,
        world_y - radius,
        world_x + radius,
        world_y + radius,
        candidates);

    float best_distance_squared = radius * radius;
    std::int32_t best_floor = -1;
    for (std::uint32_t index : candidates)
    {
        if (index >= scene_->floors.size())
            continue;

        const EeFloor& floor = scene_->floors[index];
        const float dx = floor.x - world_x;
        const float dy = floor.y - world_y;
        const float distance_squared = dx * dx + dy * dy;
        if (distance_squared <= best_distance_squared)
        {
            best_distance_squared = distance_squared;
            best_floor = static_cast<std::int32_t>(index);
        }
    }

    selected_floor_ = best_floor;
    return selected_floor_;
}

void Renderer::NotifySelectionChanged(std::int32_t floor) noexcept
{
    EeSelectionChangedCallback callback = nullptr;
    void* user_data = nullptr;
    {
        std::lock_guard lock(scene_mutex_);
        callback = selection_callback_;
        user_data = selection_user_data_;
    }

    if (callback != nullptr)
        callback(user_data, floor);
}

LRESULT Renderer::HandleWindowMessage(HWND hwnd, UINT message, WPARAM wparam, LPARAM lparam) noexcept
{
    switch (message)
    {
    case WM_ERASEBKGND:
        return 1;

    case WM_MOUSEWHEEL:
    {
        POINT point{GET_X_LPARAM(lparam), GET_Y_LPARAM(lparam)};
        ScreenToClient(hwnd, &point);
        const int delta = GET_WHEEL_DELTA_WPARAM(wparam);
        if (delta == 0)
            return 0;

        std::lock_guard lock(scene_mutex_);
        const float width = static_cast<float>(width_.load(std::memory_order_relaxed));
        const float height = static_cast<float>(height_.load(std::memory_order_relaxed));
        const float before_x = (static_cast<float>(point.x) - width * 0.5f) / zoom_ + camera_x_;
        const float before_y = camera_y_ - (static_cast<float>(point.y) - height * 0.5f) / zoom_;
        const float steps = static_cast<float>(delta) / static_cast<float>(WHEEL_DELTA);
        const float new_zoom = std::clamp(zoom_ * std::pow(1.18f, steps), 0.05f, 400.0f);
        camera_x_ = before_x - (static_cast<float>(point.x) - width * 0.5f) / new_zoom;
        camera_y_ = before_y + (static_cast<float>(point.y) - height * 0.5f) / new_zoom;
        zoom_ = new_zoom;
        return 0;
    }

    case WM_MBUTTONDOWN:
    case WM_RBUTTONDOWN:
        SetFocus(hwnd);
        SetCapture(hwnd);
        panning_ = true;
        pan_button_ = message == WM_MBUTTONDOWN ? WM_MBUTTONUP : WM_RBUTTONUP;
        last_mouse_.x = GET_X_LPARAM(lparam);
        last_mouse_.y = GET_Y_LPARAM(lparam);
        return 0;

    case WM_MOUSEMOVE:
        if (panning_)
        {
            POINT current{GET_X_LPARAM(lparam), GET_Y_LPARAM(lparam)};
            const int dx = current.x - last_mouse_.x;
            const int dy = current.y - last_mouse_.y;
            last_mouse_ = current;

            std::lock_guard lock(scene_mutex_);
            camera_x_ -= static_cast<float>(dx) / zoom_;
            camera_y_ += static_cast<float>(dy) / zoom_;
            return 0;
        }
        break;

    case WM_MBUTTONUP:
    case WM_RBUTTONUP:
        if (panning_ && message == pan_button_)
        {
            panning_ = false;
            pan_button_ = 0;
            if (GetCapture() == hwnd)
                ReleaseCapture();
            return 0;
        }
        break;

    case WM_CAPTURECHANGED:
        panning_ = false;
        pan_button_ = 0;
        break;

    case WM_LBUTTONDOWN:
    {
        SetFocus(hwnd);
        const std::int32_t selected = SelectFloorAt(GET_X_LPARAM(lparam), GET_Y_LPARAM(lparam));
        NotifySelectionChanged(selected);
        return 0;
    }
    }

    return DefWindowProcW(hwnd, message, wparam, lparam);
}

void Renderer::RenderLoop() noexcept
{
    const HRESULT com_result = CoInitializeEx(nullptr, COINIT_MULTITHREADED);
    const bool com_initialized = SUCCEEDED(com_result);

    D2DBackend backend;
    bool ready = backend.Initialize(
        window_.Handle(),
        width_.load(std::memory_order_relaxed),
        height_.load(std::memory_order_relaxed));

    {
        std::lock_guard lock(initialize_mutex_);
        initialize_success_ = ready;
        initialize_complete_ = true;
    }
    initialize_cv_.notify_all();

    if (!ready)
    {
        if (com_initialized)
            CoUninitialize();
        return;
    }

    const auto start = std::chrono::steady_clock::now();

    while (!stop_requested_.load(std::memory_order_acquire))
    {
        if (resize_pending_.exchange(false, std::memory_order_acq_rel))
        {
            ready = backend.Resize(
                width_.load(std::memory_order_relaxed),
                height_.load(std::memory_order_relaxed));
        }

        std::shared_ptr<LevelScene> scene;
        std::shared_ptr<const IconAssetTable> icon_assets;
        float camera_x = 0.0f;
        float camera_y = 0.0f;
        float zoom = 28.0f;
        std::int32_t selected_floor = -1;
        std::uint64_t scene_version = 0;
        std::uint64_t icon_assets_version = 0;
        {
            std::lock_guard lock(scene_mutex_);
            scene = scene_;
            icon_assets = icon_assets_;
            camera_x = camera_x_;
            camera_y = camera_y_;
            zoom = zoom_;
            selected_floor = selected_floor_;
            scene_version = scene_version_;
            icon_assets_version = icon_assets_version_;
        }

        if (ready)
        {
            const auto now = std::chrono::steady_clock::now();
            const double seconds = std::chrono::duration<double>(now - start).count();
            const HRESULT hr = backend.RenderFrame(
                seconds,
                scene.get(),
                scene_version,
                icon_assets.get(),
                icon_assets_version,
                camera_x,
                camera_y,
                zoom,
                selected_floor);
            ready = SUCCEEDED(hr);
        }

        if (!ready && !stop_requested_.load(std::memory_order_acquire))
        {
            backend.Shutdown();
            std::this_thread::sleep_for(std::chrono::milliseconds(250));
            ready = backend.Initialize(
                window_.Handle(),
                width_.load(std::memory_order_relaxed),
                height_.load(std::memory_order_relaxed));
        }
    }

    backend.Shutdown();
    if (com_initialized)
        CoUninitialize();
}

void Renderer::StopRenderThread() noexcept
{
    stop_requested_.store(true, std::memory_order_release);
    if (render_thread_.joinable())
        render_thread_.join();
    window_.Destroy();
}
}
