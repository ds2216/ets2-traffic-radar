cmake_minimum_required(VERSION 3.15)
project(radar_launcher CXX)

add_library(radar_launcher SHARED radar_launcher.cpp radar_launcher.def)
target_compile_features(radar_launcher PRIVATE cxx_std_17)
target_compile_definitions(radar_launcher PRIVATE UNICODE _UNICODE)
target_include_directories(radar_launcher PRIVATE ${CMAKE_CURRENT_SOURCE_DIR}/../vendor/ets2la-plugin/vendor/scs_sdk/include)
target_link_libraries(radar_launcher PRIVATE shell32 ole32)
set_target_properties(radar_launcher PROPERTIES MSVC_RUNTIME_LIBRARY "MultiThreaded")
