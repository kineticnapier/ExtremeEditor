cmake_minimum_required(VERSION 3.24)

project(ExtremeEditorNativeRenderer LANGUAGES CXX)

enable_testing()

add_library(ExtremeEditor.NativeRenderer SHARED
    src/exports.cpp
    src/native_window.cpp
    src/renderer.cpp
    src/renderer_camera.cpp
    src/renderer_camera_runtime_context.cpp
    src/renderer_track.cpp
    src/renderer_editor.cpp
    src/renderer_diagnostics.cpp
    src/d2d_backend.cpp
    src/d2d_backend_compat.cpp
    src/d2d_backend_camera.cpp
    src/d2d_backend_editor.cpp
    src/floor_instanced_renderer.cpp
    src/floor_instanced_renderer_camera_transform.cpp
    src/icon_instanced_renderer.cpp
    src/icon_instanced_renderer_camera.cpp
    src/level_scene.cpp
    src/track_transform_runtime.cpp
)

target_compile_features(ExtremeEditor.NativeRenderer PRIVATE cxx_std_20)
target_compile_definitions(ExtremeEditor.NativeRenderer PRIVATE
    EE_RENDERER_BUILD=1
    UNICODE
    _UNICODE
    NOMINMAX
)
target_include_directories(ExtremeEditor.NativeRenderer
    PUBLIC
        ${CMAKE_CURRENT_SOURCE_DIR}/include
    PRIVATE
        ${CMAKE_CURRENT_SOURCE_DIR}/src
)

target_link_libraries(ExtremeEditor.NativeRenderer PRIVATE
    user32
    d3d11
    d3dcompiler
    dxgi
    d2d1
    dwrite
    windowscodecs
    ole32
)

if(MSVC)
    target_compile_options(ExtremeEditor.NativeRenderer PRIVATE /W4 /permissive-)
endif()

add_executable(native_renderer_tests
    tests/native_renderer_tests.cpp
)

target_compile_features(native_renderer_tests PRIVATE cxx_std_20)
target_compile_definitions(native_renderer_tests PRIVATE UNICODE _UNICODE NOMINMAX)
target_include_directories(native_renderer_tests PRIVATE ${CMAKE_CURRENT_SOURCE_DIR}/include)
target_link_libraries(native_renderer_tests PRIVATE ExtremeEditor.NativeRenderer)

add_executable(camera_runtime_reference_tests
    tests/camera_runtime_reference_tests.cpp
    src/renderer_camera.cpp
)

target_compile_features(camera_runtime_reference_tests PRIVATE cxx_std_20)
target_compile_definitions(camera_runtime_reference_tests PRIVATE UNICODE _UNICODE NOMINMAX)
target_include_directories(camera_runtime_reference_tests PRIVATE
    ${CMAKE_CURRENT_SOURCE_DIR}/include
    ${CMAKE_CURRENT_SOURCE_DIR}/src
)

target_link_libraries(camera_runtime_reference_tests PRIVATE
    user32
    d3d11
    d3dcompiler
    dxgi
    d2d1
    dwrite
    windowscodecs
    ole32
)

add_executable(camera_cull_transform_purity_tests
    tests/camera_cull_transform_purity_tests.cpp
)

target_compile_features(camera_cull_transform_purity_tests PRIVATE cxx_std_20)
target_compile_definitions(camera_cull_transform_purity_tests PRIVATE
    EE_NATIVE_SOURCE_DIR="${CMAKE_CURRENT_SOURCE_DIR}"
)

add_executable(track_transform_phase_diagnostics_tests
    tests/track_transform_phase_diagnostics_tests.cpp
)

target_compile_features(track_transform_phase_diagnostics_tests PRIVATE cxx_std_20)
target_compile_definitions(track_transform_phase_diagnostics_tests PRIVATE
    EE_NATIVE_SOURCE_DIR="${CMAKE_CURRENT_SOURCE_DIR}"
)

add_executable(track_transform_visual_lazy_tests
    tests/track_transform_visual_lazy_tests.cpp
)

target_compile_features(track_transform_visual_lazy_tests PRIVATE cxx_std_20)
target_compile_definitions(track_transform_visual_lazy_tests PRIVATE
    EE_NATIVE_SOURCE_DIR="${CMAKE_CURRENT_SOURCE_DIR}"
)

add_executable(track_transform_visual_cull_tests
    tests/track_transform_visual_cull_tests.cpp
)

target_compile_features(track_transform_visual_cull_tests PRIVATE cxx_std_20)
target_compile_definitions(track_transform_visual_cull_tests PRIVATE
    EE_NATIVE_SOURCE_DIR="${CMAKE_CURRENT_SOURCE_DIR}"
)

add_executable(track_transform_timeline_scaling_tests
    tests/track_transform_timeline_scaling_tests.cpp
    src/track_transform_runtime.cpp
)

target_compile_features(track_transform_timeline_scaling_tests PRIVATE cxx_std_20)
target_compile_definitions(track_transform_timeline_scaling_tests PRIVATE UNICODE _UNICODE NOMINMAX)
target_include_directories(track_transform_timeline_scaling_tests PRIVATE
    ${CMAKE_CURRENT_SOURCE_DIR}/include
    ${CMAKE_CURRENT_SOURCE_DIR}/src
)

add_executable(track_transform_update_scaling_tests
    tests/track_transform_update_scaling_tests.cpp
    src/track_transform_runtime.cpp
)

target_compile_features(track_transform_update_scaling_tests PRIVATE cxx_std_20)
target_compile_definitions(track_transform_update_scaling_tests PRIVATE UNICODE _UNICODE NOMINMAX)
target_include_directories(track_transform_update_scaling_tests PRIVATE
    ${CMAKE_CURRENT_SOURCE_DIR}/include
    ${CMAKE_CURRENT_SOURCE_DIR}/src
)

add_executable(track_transform_finished_scaling_tests
    tests/track_transform_finished_scaling_tests.cpp
    src/track_transform_runtime.cpp
)

target_compile_features(track_transform_finished_scaling_tests PRIVATE cxx_std_20)
target_compile_definitions(track_transform_finished_scaling_tests PRIVATE UNICODE _UNICODE NOMINMAX)
target_include_directories(track_transform_finished_scaling_tests PRIVATE
    ${CMAKE_CURRENT_SOURCE_DIR}/include
    ${CMAKE_CURRENT_SOURCE_DIR}/src
)

add_executable(track_transform_anchor_jitter_scaling_tests
    tests/track_transform_anchor_jitter_scaling_tests.cpp
    src/level_scene.cpp
    src/track_transform_runtime.cpp
)

target_compile_features(track_transform_anchor_jitter_scaling_tests PRIVATE cxx_std_20)
target_compile_definitions(track_transform_anchor_jitter_scaling_tests PRIVATE UNICODE _UNICODE NOMINMAX)
target_include_directories(track_transform_anchor_jitter_scaling_tests PRIVATE
    ${CMAKE_CURRENT_SOURCE_DIR}/include
    ${CMAKE_CURRENT_SOURCE_DIR}/src
)

add_test(NAME native_renderer_tests COMMAND native_renderer_tests)
add_test(NAME camera_runtime_reference_tests COMMAND camera_runtime_reference_tests)
add_test(NAME camera_cull_transform_purity_tests COMMAND camera_cull_transform_purity_tests)
add_test(NAME track_transform_phase_diagnostics_tests COMMAND track_transform_phase_diagnostics_tests)
add_test(NAME track_transform_visual_lazy_tests COMMAND track_transform_visual_lazy_tests)
add_test(NAME track_transform_visual_cull_tests COMMAND track_transform_visual_cull_tests)
add_test(NAME track_transform_timeline_scaling_tests COMMAND track_transform_timeline_scaling_tests)
add_test(NAME track_transform_update_scaling_tests COMMAND track_transform_update_scaling_tests)
add_test(NAME track_transform_finished_scaling_tests COMMAND track_transform_finished_scaling_tests)
add_test(NAME track_transform_anchor_jitter_scaling_tests COMMAND track_transform_anchor_jitter_scaling_tests)
