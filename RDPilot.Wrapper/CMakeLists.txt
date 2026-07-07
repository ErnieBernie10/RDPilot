cmake_minimum_required(VERSION 3.15)
project(freerdp_wrapper VERSION 1.0.0 LANGUAGES C)

include(GNUInstallDirs)

find_package(WinPR CONFIG QUIET)
if(NOT TARGET winpr)
    find_package(WinPR3 CONFIG QUIET)
endif()

find_package(FreeRDP CONFIG QUIET)
if(NOT TARGET freerdp)
    find_package(FreeRDP3 CONFIG QUIET)
endif()

find_package(FreeRDP-Client CONFIG QUIET)
if(NOT TARGET freerdp-client)
    find_package(FreeRDP-Client3 CONFIG QUIET)
endif()

set(FREERDP_CONFIG_FOUND FALSE)
if(TARGET freerdp AND TARGET freerdp-client AND TARGET winpr)
    set(FREERDP_CONFIG_FOUND TRUE)
endif()

if(NOT FREERDP_CONFIG_FOUND)
    find_package(PkgConfig QUIET)
    if(PkgConfig_FOUND)
        pkg_check_modules(FREERDP QUIET IMPORTED_TARGET freerdp3 freerdp-client3 winpr3)
    endif()
endif()

if(NOT FREERDP_CONFIG_FOUND AND NOT FREERDP_FOUND)
    find_path(FREERDP_INCLUDE_DIR
        NAMES freerdp/freerdp.h
        PATH_SUFFIXES freerdp3 include/freerdp3
    )
    find_path(WINPR_INCLUDE_DIR
        NAMES winpr/wtypes.h
        PATH_SUFFIXES winpr3 include/winpr3
    )
    find_library(FREERDP_LIBRARY NAMES freerdp3 freerdp)
    find_library(FREERDP_CLIENT_LIBRARY NAMES freerdp-client3 freerdp-client)
    find_library(WINPR_LIBRARY NAMES winpr3 winpr)

    if(NOT FREERDP_INCLUDE_DIR OR NOT WINPR_INCLUDE_DIR OR NOT FREERDP_LIBRARY OR NOT FREERDP_CLIENT_LIBRARY OR NOT WINPR_LIBRARY)
        message(FATAL_ERROR "FreeRDP 3 development files were not found. Install freerdp3, freerdp-client3, and winpr3, or configure with a toolchain such as vcpkg that exposes their include and library paths.")
    endif()
endif()

add_library(freerdp_wrapper SHARED
    freerdp_wrapper.c
    freerdp_wrapper_input.c
    freerdp_wrapper_clipboard.c
)

target_compile_definitions(freerdp_wrapper PRIVATE FREERDP_WRAPPER_EXPORTS)

if(MSVC)
    target_compile_options(freerdp_wrapper PRIVATE /W4)
elseif(CMAKE_C_COMPILER_ID MATCHES "GNU|Clang")
    target_compile_options(freerdp_wrapper PRIVATE -Wall -Wextra -Wpedantic)
endif()

if(WIN32 AND CMAKE_C_COMPILER_ID MATCHES "GNU|Clang")
    target_compile_definitions(freerdp_wrapper PRIVATE __STDC_NO_THREADS__=1)
endif()

if(FREERDP_CONFIG_FOUND)
    target_link_libraries(freerdp_wrapper PRIVATE freerdp freerdp-client winpr)
elseif(TARGET PkgConfig::FREERDP)
    target_link_libraries(freerdp_wrapper PRIVATE PkgConfig::FREERDP)
else()
    target_include_directories(freerdp_wrapper PRIVATE ${FREERDP_INCLUDE_DIR} ${WINPR_INCLUDE_DIR})
    target_link_libraries(freerdp_wrapper PRIVATE ${FREERDP_LIBRARY} ${FREERDP_CLIENT_LIBRARY} ${WINPR_LIBRARY})
endif()

set_target_properties(freerdp_wrapper PROPERTIES
    VERSION ${PROJECT_VERSION}
    SOVERSION 1
    PUBLIC_HEADER freerdp_wrapper.h
)

if(WIN32)
    set_target_properties(freerdp_wrapper PROPERTIES PREFIX "")
endif()

configure_file(
    freerdp_wrapper.pc.in
    ${CMAKE_CURRENT_BINARY_DIR}/freerdp_wrapper.pc
    @ONLY
)

install(TARGETS freerdp_wrapper
    LIBRARY DESTINATION ${CMAKE_INSTALL_LIBDIR}
    PUBLIC_HEADER DESTINATION ${CMAKE_INSTALL_INCLUDEDIR}
)

install(FILES ${CMAKE_CURRENT_BINARY_DIR}/freerdp_wrapper.pc
    DESTINATION ${CMAKE_INSTALL_LIBDIR}/pkgconfig
)
