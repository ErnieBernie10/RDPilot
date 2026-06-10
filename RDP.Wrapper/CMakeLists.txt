cmake_minimum_required(VERSION 3.15)
project(freerdp_wrapper VERSION 1.0.0 LANGUAGES C)

include(GNUInstallDirs)

find_package(PkgConfig REQUIRED)
pkg_check_modules(FREERDP REQUIRED IMPORTED_TARGET freerdp3 freerdp-client3 winpr3)

add_library(freerdp_wrapper SHARED freerdp_wrapper.c)

target_link_libraries(freerdp_wrapper PRIVATE PkgConfig::FREERDP)

set_target_properties(freerdp_wrapper PROPERTIES
    VERSION ${PROJECT_VERSION}
    SOVERSION 1
    PUBLIC_HEADER freerdp_wrapper.h
)

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
