#include "renderer.h"
#include "d2d_backend.h"

#include <algorithm>
#include <chrono>
#include <cmath>

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
    if (!window_.Create(parent, safe_width, safe_height))
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

void Renderer::RenderLoop() noexcept
{
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
        return;

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
        float camera_x = 0.0f;
        float camera_y = 0.0f;
        float zoom = 28.0f;
        std::uint64_t scene_version = 0;
        {
            std::lock_guard lock(scene_mutex_);
            scene = scene_;
            camera_x = camera_x_;
            camera_y = camera_y_;
            zoom = zoom_;
            scene_version = scene_version_;
        }

        if (ready)
        {
            const auto now = std::chrono::steady_clock::now();
            const double seconds = std::chrono::duration<double>(now - start).count();
            const HRESULT hr = backend.RenderFrame(
                seconds,
                scene.get(),
                scene_version,
                camera_x,
                camera_y,
                zoom);
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
}

void Renderer::StopRenderThread() noexcept
{
    stop_requested_.store(true, std::memory_order_release);
    if (render_thread_.joinable())
        render_thread_.join();
    window_.Destroy();
}
}
