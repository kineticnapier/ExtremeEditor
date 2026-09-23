#include <fstream>
#include <iostream>
#include <sstream>
#include <string>

#ifndef EE_NATIVE_SOURCE_DIR
#error EE_NATIVE_SOURCE_DIR must be defined
#endif

namespace
{
std::string ReadSource(const char* relative_path)
{
    const std::string path = std::string(EE_NATIVE_SOURCE_DIR) + relative_path;
    std::ifstream input(path, std::ios::binary);
    if (!input)
    {
        std::cerr << "RED setup failure: unable to open " << path << '\n';
        return {};
    }

    std::ostringstream buffer;
    buffer << input.rdbuf();
    return buffer.str();
}

std::string FunctionBody(const std::string& source, const std::string& signature)
{
    const std::size_t start = source.find(signature);
    if (start == std::string::npos)
        return {};

    const std::size_t open = source.find('{', start);
    if (open == std::string::npos)
        return {};

    int depth = 0;
    for (std::size_t i = open; i < source.size(); ++i)
    {
        if (source[i] == '{')
            ++depth;
        else if (source[i] == '}' && --depth == 0)
            return source.substr(open, i - open + 1);
    }
    return {};
}
}

int main()
{
    const std::string scene_header = ReadSource("/src/level_scene.h");
    const std::string scene = ReadSource("/src/level_scene.cpp");
    if (scene_header.empty() || scene.empty())
        return 2;

    bool failed = false;

    if (scene_header.find("AppendVisualTransformCandidates(") == std::string::npos)
    {
        std::cerr
            << "RED: LevelScene must expose conservative visual-transform cull candidates "
            << "for scaled floors whose centers are outside the viewport.\n";
        failed = true;
    }

    if (scene_header.find("visual_transform_cells_") == std::string::npos)
    {
        std::cerr
            << "RED: conservative visual culling must use a dedicated spatial index "
            << "instead of scanning every visual-only track each frame.\n";
        failed = true;
    }

    const std::string query = FunctionBody(scene, "void LevelScene::Query(");
    if (query.empty() || query.find("AppendVisualTransformCandidates(") == std::string::npos)
    {
        std::cerr
            << "RED: LevelScene::Query must supplement center-based spatial culling "
            << "with conservative visual-transform candidates.\n";
        failed = true;
    }

    const std::string timeline = FunctionBody(scene, "bool LevelScene::SetTrackTransformTimeline(");
    if (timeline.empty() || timeline.find("RebuildVisualTransformCullIndex(") == std::string::npos)
    {
        std::cerr
            << "RED: visual-transform cull bounds must be rebuilt when the timeline changes.\n";
        failed = true;
    }

    if (failed)
        return 1;

    std::cout << "visual transform cull coverage contract passed\n";
    return 0;
}
