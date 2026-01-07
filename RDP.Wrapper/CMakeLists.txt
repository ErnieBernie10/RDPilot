cmake_minimum_required(VERSION 3.15)
project(freerdp_wrapper C)

find_package(PkgConfig REQUIRED)
pkg_check_modules(FREERDP REQUIRED freerdp3 winpr3)

add_library(freerdp_wrapper SHARED freerdp_wrapper.c)

target_include_directories(freerdp_wrapper PRIVATE ${FREERDP_INCLUDE_DIRS})
target_link_libraries(freerdp_wrapper PRIVATE ${FREERDP_LIBRARIES})
