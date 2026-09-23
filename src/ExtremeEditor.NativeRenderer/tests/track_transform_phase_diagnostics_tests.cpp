#include <fstream>
#include <iostream>
#include <sstream>
#include <string>

#ifndef EE_NATIVE_SOURCE_DIR
#error EE_NATIVE_SOURCE_DIR must be defined
#endif

int main()
{
    const std::string path = std::string(EE_NATIVE_SOURCE_DIR) + "/src/track_transform_runtime.h";
    std::ifstream input(path, std::ios::binary);
    if (!input)
    {
        std::cerr << "RED setup failure: unable to open " << path << '\n';
        return 2;
    }

    std::ostringstream buffer;
    buffer << input.rdbuf();
    const std::string source = buffer.str();

    const char* required[] =
    {
        "struct TrackTransformUpdateMetrics",
        "double total_ms",
        "double clock_ms",
        "double evaluate_ms",
        "double apply_ms",
        "double spatial_remove_ms",
        "double spatial_insert_ms",
        "TrackTransformUpdateMetrics Update("
    };

    bool missing = false;
    for (const char* token : required)
    {
        if (source.find(token) == std::string::npos)
        {
            std::cerr << "RED: track-transform phase diagnostics missing: " << token << '\n';
            missing = true;
        }
    }

    if (missing)
        return 1;

    std::cout << "track transform phase diagnostics contract passed\n";
    return 0;
}
