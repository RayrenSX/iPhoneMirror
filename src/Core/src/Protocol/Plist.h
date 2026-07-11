#pragma once

#include <cstdint>
#include <map>
#include <stdexcept>
#include <string>
#include <string_view>
#include <vector>

namespace iPhoneMirror::plist {

enum class Type {
    Null,
    Boolean,
    Integer,
    Real,
    String,
    Data,
    Array,
    Dictionary,
};

struct Value {
    Type type{Type::Null};
    bool boolean{};
    std::int64_t integer{};
    double real{};
    std::string string;
    std::vector<Value> array;
    std::map<std::string, Value, std::less<>> dictionary;

    static Value Bool(bool value);
    static Value Integer(std::int64_t value);
    static Value Real(double value);
    static Value String(std::string value);
    static Value Data(std::string base64);
    static Value Array(std::vector<Value> value);
    static Value Dict(std::map<std::string, Value, std::less<>> value);

    [[nodiscard]] const Value* find(std::string_view key) const;
    [[nodiscard]] std::string string_or(std::string fallback = {}) const;
    [[nodiscard]] std::int64_t integer_or(std::int64_t fallback = 0) const;
    [[nodiscard]] bool bool_or(bool fallback = false) const;
};

class ParseError final : public std::runtime_error {
public:
    using std::runtime_error::runtime_error;
};

[[nodiscard]] Value parse_xml(std::string_view xml);
[[nodiscard]] std::string to_xml(const Value& root);
[[nodiscard]] std::string escape_xml(std::string_view text);

} // namespace iPhoneMirror::plist

