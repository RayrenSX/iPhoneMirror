#include "Protocol/Plist.h"

#include <charconv>
#include <cerrno>
#include <cmath>
#include <cstdlib>
#include <format>
#include <limits>

namespace iPhoneMirror::plist {
namespace {

struct Tag {
    std::string name;
    bool closing{};
    bool self_closing{};
};

void append_utf8(std::string& out, std::uint32_t codepoint) {
    if (codepoint <= 0x7f) {
        out.push_back(static_cast<char>(codepoint));
    } else if (codepoint <= 0x7ff) {
        out.push_back(static_cast<char>(0xc0 | (codepoint >> 6)));
        out.push_back(static_cast<char>(0x80 | (codepoint & 0x3f)));
    } else if (codepoint <= 0xffff) {
        out.push_back(static_cast<char>(0xe0 | (codepoint >> 12)));
        out.push_back(static_cast<char>(0x80 | ((codepoint >> 6) & 0x3f)));
        out.push_back(static_cast<char>(0x80 | (codepoint & 0x3f)));
    } else if (codepoint <= 0x10ffff) {
        out.push_back(static_cast<char>(0xf0 | (codepoint >> 18)));
        out.push_back(static_cast<char>(0x80 | ((codepoint >> 12) & 0x3f)));
        out.push_back(static_cast<char>(0x80 | ((codepoint >> 6) & 0x3f)));
        out.push_back(static_cast<char>(0x80 | (codepoint & 0x3f)));
    }
}

std::string unescape_xml(std::string_view text) {
    std::string result;
    result.reserve(text.size());
    for (std::size_t i = 0; i < text.size();) {
        if (text[i] != '&') {
            result.push_back(text[i++]);
            continue;
        }
        const auto end = text.find(';', i + 1);
        if (end == std::string_view::npos) {
            throw ParseError("unterminated XML entity");
        }
        const auto entity = text.substr(i + 1, end - i - 1);
        if (entity == "amp") result.push_back('&');
        else if (entity == "lt") result.push_back('<');
        else if (entity == "gt") result.push_back('>');
        else if (entity == "quot") result.push_back('"');
        else if (entity == "apos") result.push_back('\'');
        else if (!entity.empty() && entity.front() == '#') {
            std::uint32_t value{};
            const bool hex = entity.size() > 2 && (entity[1] == 'x' || entity[1] == 'X');
            const auto digits = entity.substr(hex ? 2 : 1);
            const int base = hex ? 16 : 10;
            const auto [ptr, error] = std::from_chars(
                digits.data(), digits.data() + digits.size(), value, base);
            if (error != std::errc{} || ptr != digits.data() + digits.size()) {
                throw ParseError("invalid numeric XML entity");
            }
            append_utf8(result, value);
        } else {
            throw ParseError("unsupported XML entity");
        }
        i = end + 1;
    }
    return result;
}

class Parser {
public:
    explicit Parser(std::string_view input) : input_(input) {}

    Value parse() {
        skip_misc();
        Tag first = read_tag();
        Value result;
        if (first.name == "plist" && !first.closing) {
            result = parse_value();
            expect_end("plist");
        } else {
            result = parse_value_from_tag(first);
        }
        skip_misc();
        if (position_ != input_.size()) {
            throw ParseError("trailing content after plist root");
        }
        return result;
    }

private:
    std::string_view input_;
    std::size_t position_{};

    void skip_whitespace() {
        while (position_ < input_.size()) {
            const char c = input_[position_];
            if (c != ' ' && c != '\t' && c != '\r' && c != '\n') break;
            ++position_;
        }
    }

    void skip_misc() {
        while (true) {
            skip_whitespace();
            if (input_.substr(position_).starts_with("<?")) {
                const auto end = input_.find("?>", position_ + 2);
                if (end == std::string_view::npos) throw ParseError("unterminated XML declaration");
                position_ = end + 2;
                continue;
            }
            if (input_.substr(position_).starts_with("<!--")) {
                const auto end = input_.find("-->", position_ + 4);
                if (end == std::string_view::npos) throw ParseError("unterminated XML comment");
                position_ = end + 3;
                continue;
            }
            if (input_.substr(position_).starts_with("<!")) {
                const auto end = input_.find('>', position_ + 2);
                if (end == std::string_view::npos) throw ParseError("unterminated XML directive");
                position_ = end + 1;
                continue;
            }
            break;
        }
    }

    Tag read_tag() {
        skip_misc();
        if (position_ >= input_.size() || input_[position_] != '<') {
            throw ParseError("expected XML tag");
        }
        const auto end = input_.find('>', position_ + 1);
        if (end == std::string_view::npos) throw ParseError("unterminated XML tag");

        auto body = input_.substr(position_ + 1, end - position_ - 1);
        position_ = end + 1;
        while (!body.empty() && (body.front() == ' ' || body.front() == '\t')) body.remove_prefix(1);
        while (!body.empty() && (body.back() == ' ' || body.back() == '\t')) body.remove_suffix(1);

        Tag tag;
        if (!body.empty() && body.front() == '/') {
            tag.closing = true;
            body.remove_prefix(1);
        }
        if (!body.empty() && body.back() == '/') {
            tag.self_closing = true;
            body.remove_suffix(1);
            while (!body.empty() && (body.back() == ' ' || body.back() == '\t')) body.remove_suffix(1);
        }
        const auto separator = body.find_first_of(" \t\r\n");
        tag.name.assign(body.substr(0, separator));
        if (tag.name.empty()) throw ParseError("empty XML tag name");
        return tag;
    }

    bool next_is_end(std::string_view name) {
        skip_misc();
        if (!input_.substr(position_).starts_with("</")) return false;
        const auto saved = position_;
        const Tag tag = read_tag();
        position_ = saved;
        return tag.closing && tag.name == name;
    }

    void expect_end(std::string_view name) {
        const Tag tag = read_tag();
        if (!tag.closing || tag.name != name) {
            throw ParseError(std::format("expected closing tag </{}>", name));
        }
    }

    std::string parse_text(const Tag& tag) {
        if (tag.self_closing) return {};
        const auto marker = std::string("</") + tag.name + ">";
        const auto end = input_.find(marker, position_);
        if (end == std::string_view::npos) {
            throw ParseError(std::format("missing closing tag for <{}>", tag.name));
        }
        const auto text = input_.substr(position_, end - position_);
        if (text.find('<') != std::string_view::npos) {
            throw ParseError(std::format("unexpected child element in <{}>", tag.name));
        }
        position_ = end + marker.size();
        return unescape_xml(text);
    }

    Value parse_value() {
        return parse_value_from_tag(read_tag());
    }

    Value parse_value_from_tag(const Tag& tag) {
        if (tag.closing) throw ParseError("unexpected closing tag");
        if (tag.name == "dict") {
            std::map<std::string, Value, std::less<>> dictionary;
            if (!tag.self_closing) {
                while (!next_is_end("dict")) {
                    const Tag key_tag = read_tag();
                    if (key_tag.name != "key" || key_tag.closing) {
                        throw ParseError("plist dictionary entry does not start with <key>");
                    }
                    const std::string key = parse_text(key_tag);
                    dictionary.insert_or_assign(key, parse_value());
                }
                expect_end("dict");
            }
            return Value::Dict(std::move(dictionary));
        }
        if (tag.name == "array") {
            std::vector<Value> array;
            if (!tag.self_closing) {
                while (!next_is_end("array")) array.push_back(parse_value());
                expect_end("array");
            }
            return Value::Array(std::move(array));
        }
        if (tag.name == "string" || tag.name == "date") return Value::String(parse_text(tag));
        if (tag.name == "data") return Value::Data(parse_text(tag));
        if (tag.name == "integer") {
            const std::string text = parse_text(tag);
            errno = 0;
            char* end{};
            const auto value = std::strtoll(text.c_str(), &end, 0);
            if (errno == ERANGE || end != text.c_str() + text.size()) throw ParseError("invalid plist integer");
            return Value::Integer(value);
        }
        if (tag.name == "real") {
            const std::string text = parse_text(tag);
            char* end{};
            const double value = std::strtod(text.c_str(), &end);
            if (end != text.c_str() + text.size() || !std::isfinite(value)) {
                throw ParseError("invalid plist real");
            }
            return Value::Real(value);
        }
        if (tag.name == "true") {
            if (!tag.self_closing) expect_end("true");
            return Value::Bool(true);
        }
        if (tag.name == "false") {
            if (!tag.self_closing) expect_end("false");
            return Value::Bool(false);
        }
        throw ParseError(std::format("unsupported plist element <{}>", tag.name));
    }
};

void serialize_value(const Value& value, std::string& out) {
    switch (value.type) {
    case Type::Null:
        out += "<string/>";
        break;
    case Type::Boolean:
        out += value.boolean ? "<true/>" : "<false/>";
        break;
    case Type::Integer:
        out += std::format("<integer>{}</integer>", value.integer);
        break;
    case Type::Real:
        out += std::format("<real>{}</real>", value.real);
        break;
    case Type::String:
        out += "<string>" + escape_xml(value.string) + "</string>";
        break;
    case Type::Data:
        out += "<data>" + value.string + "</data>";
        break;
    case Type::Array:
        out += "<array>";
        for (const auto& item : value.array) serialize_value(item, out);
        out += "</array>";
        break;
    case Type::Dictionary:
        out += "<dict>";
        for (const auto& [key, item] : value.dictionary) {
            out += "<key>" + escape_xml(key) + "</key>";
            serialize_value(item, out);
        }
        out += "</dict>";
        break;
    }
}

} // namespace

Value Value::Bool(bool value) { Value result; result.type = Type::Boolean; result.boolean = value; return result; }
Value Value::Integer(std::int64_t value) { Value result; result.type = Type::Integer; result.integer = value; return result; }
Value Value::Real(double value) { Value result; result.type = Type::Real; result.real = value; return result; }
Value Value::String(std::string value) { Value result; result.type = Type::String; result.string = std::move(value); return result; }
Value Value::Data(std::string value) { Value result; result.type = Type::Data; result.string = std::move(value); return result; }
Value Value::Array(std::vector<Value> value) { Value result; result.type = Type::Array; result.array = std::move(value); return result; }
Value Value::Dict(std::map<std::string, Value, std::less<>> value) { Value result; result.type = Type::Dictionary; result.dictionary = std::move(value); return result; }

const Value* Value::find(std::string_view key) const {
    if (type != Type::Dictionary) return nullptr;
    const auto iterator = dictionary.find(key);
    return iterator == dictionary.end() ? nullptr : &iterator->second;
}

std::string Value::string_or(std::string fallback) const {
    return type == Type::String || type == Type::Data ? string : std::move(fallback);
}

std::int64_t Value::integer_or(std::int64_t fallback) const {
    return type == Type::Integer ? integer : fallback;
}

bool Value::bool_or(bool fallback) const {
    return type == Type::Boolean ? boolean : fallback;
}

Value parse_xml(std::string_view xml) { return Parser(xml).parse(); }

std::string escape_xml(std::string_view text) {
    std::string result;
    result.reserve(text.size());
    for (const char c : text) {
        switch (c) {
        case '&': result += "&amp;"; break;
        case '<': result += "&lt;"; break;
        case '>': result += "&gt;"; break;
        case '"': result += "&quot;"; break;
        case '\'': result += "&apos;"; break;
        default: result.push_back(c); break;
        }
    }
    return result;
}

std::string to_xml(const Value& root) {
    std::string result =
        "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n"
        "<!DOCTYPE plist PUBLIC \"-//Apple//DTD PLIST 1.0//EN\" "
        "\"http://www.apple.com/DTDs/PropertyList-1.0.dtd\">\n"
        "<plist version=\"1.0\">";
    serialize_value(root, result);
    result += "</plist>";
    return result;
}

} // namespace iPhoneMirror::plist
