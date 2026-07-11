#pragma once

#include <string_view>

namespace iPhoneMirror::logging {

void initialize();
void write(std::string_view message);
void shutdown();

} // namespace iPhoneMirror::logging
