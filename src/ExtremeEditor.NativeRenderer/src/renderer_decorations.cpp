#include "renderer.h"

namespace ee
{
bool Renderer::SetStaticDecorations(
    const EeStaticDecoration* decorations,
    std::uint32_t decoration_count) noexcept
{
    std::lock_guard lock(scene_mutex_);
    return scene_ != nullptr && scene_->SetStaticDecorations(decorations, decoration_count);
}

void Renderer::ClearDecorationAssets() noexcept
{
    std::lock_guard lock(scene_mutex_);
    if (scene_ != nullptr)
        scene_->ClearDecorationAssets();
}

bool Renderer::SetDecorationAsset(
    std::uint32_t asset_id,
    const wchar_t* image_path) noexcept
{
    std::lock_guard lock(scene_mutex_);
    return scene_ != nullptr && scene_->SetDecorationAsset(asset_id, image_path);
}
}
