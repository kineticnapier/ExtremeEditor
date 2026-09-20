#include "renderer.h"

namespace ee
{
bool Renderer::Initialize(HWND parent, std::uint32_t width, std::uint32_t height) noexcept
{
    return window_.Create(parent, width, height);
}

void Renderer::Resize(std::uint32_t width, std::uint32_t height) noexcept
{
    window_.Resize(width, height);
}
}
