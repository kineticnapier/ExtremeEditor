#include "renderer.h"
#include "d2d_backend.h"

#include <algorithm>
#include <chrono>

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

        if (ready)
        {
            const auto now = std::chrono::steady_clock::now();
            const double seconds = std::chrono::duration<double>(now - start).count();
            const HRESULT hr = backend.RenderFrame(seconds);
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
