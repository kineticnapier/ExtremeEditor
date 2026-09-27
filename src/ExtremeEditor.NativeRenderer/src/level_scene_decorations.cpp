#include "level_scene.h"

#include <memory>
#include <utility>

namespace ee
{
bool LevelScene::SetStaticDecorations(
    const EeStaticDecoration* decorations,
    std::uint32_t decoration_count) noexcept
{
    if (decoration_count > 0 && decorations == nullptr)
        return false;

    try
    {
        auto next = std::make_shared<std::vector<EeStaticDecoration>>();
        if (decoration_count > 0)
            next->assign(decorations, decorations + decoration_count);

        std::lock_guard lock(decoration_mutex_);
        static_decorations_ = std::move(next);
        ++static_decorations_version_;
        return true;
    }
    catch (...)
    {
        return false;
    }
}

void LevelScene::ClearDecorationAssets() noexcept
{
    std::lock_guard lock(decoration_mutex_);
    decoration_assets_ = std::make_shared<DecorationAssetTable>();
    ++decoration_assets_version_;
}

bool LevelScene::SetDecorationAsset(
    std::uint32_t asset_id,
    const wchar_t* image_path) noexcept
{
    if (image_path == nullptr || *image_path == L'\0')
        return false;

    try
    {
        std::lock_guard lock(decoration_mutex_);
        auto next = std::make_shared<DecorationAssetTable>(
            decoration_assets_ ? *decoration_assets_ : DecorationAssetTable{});
        DecorationAsset asset;
        asset.image_path = image_path;
        (*next)[asset_id] = std::move(asset);
        decoration_assets_ = std::move(next);
        ++decoration_assets_version_;
        return true;
    }
    catch (...)
    {
        return false;
    }
}

DecorationRenderState LevelScene::GetDecorationRenderState() const noexcept
{
    std::lock_guard lock(decoration_mutex_);
    DecorationRenderState state;
    state.decorations = static_decorations_;
    state.assets = decoration_assets_;
    state.decorations_version = static_decorations_version_;
    state.assets_version = decoration_assets_version_;
    return state;
}
}
