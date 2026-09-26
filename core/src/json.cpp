#include "swingcore/json.h"

#include <cmath>
#include <cstdio>
#include <cstdlib>

namespace swingcore::json {

namespace {

const Value kNull;
const Value::Array kEmptyArray;
const Value::Object kEmptyObject;

void escapeString(std::string& out, const std::string& s) {
    out += '"';
    for (unsigned char c : s) {
        switch (c) {
            case '"': out += "\\\""; break;
            case '\\': out += "\\\\"; break;
            case '\b': out += "\\b"; break;
            case '\f': out += "\\f"; break;
            case '\n': out += "\\n"; break;
            case '\r': out += "\\r"; break;
            case '\t': out += "\\t"; break;
            default:
                if (c < 0x20) {
                    char buf[8];
                    std::snprintf(buf, sizeof buf, "\\u%04x", c);
                    out += buf;
                } else {
                    out += static_cast<char>(c);
                }
        }
    }
    out += '"';
}

void appendUtf8(std::string& out, uint32_t cp) {
    if (cp < 0x80) {
        out += static_cast<char>(cp);
    } else if (cp < 0x800) {
        out += static_cast<char>(0xC0 | (cp >> 6));
        out += static_cast<char>(0x80 | (cp & 0x3F));
    } else if (cp < 0x10000) {
        out += static_cast<char>(0xE0 | (cp >> 12));
        out += static_cast<char>(0x80 | ((cp >> 6) & 0x3F));
        out += static_cast<char>(0x80 | (cp & 0x3F));
    } else {
        out += static_cast<char>(0xF0 | (cp >> 18));
        out += static_cast<char>(0x80 | ((cp >> 12) & 0x3F));
        out += static_cast<char>(0x80 | ((cp >> 6) & 0x3F));
        out += static_cast<char>(0x80 | (cp & 0x3F));
    }
}

class Parser {
public:
    explicit Parser(const std::string& text) : s_(text) {}

    Value parseDocument() {
        Value v = parseValue(0);
        skipWs();
        if (pos_ != s_.size()) fail("trailing characters");
        return v;
    }

private:
    [[noreturn]] void fail(const std::string& msg) const { throw ParseError(msg, pos_); }

    void skipWs() {
        while (pos_ < s_.size() && (s_[pos_] == ' ' || s_[pos_] == '\t' || s_[pos_] == '\n' || s_[pos_] == '\r')) ++pos_;
    }

    bool consume(const char* lit) {
        size_t n = std::char_traits<char>::length(lit);
        if (s_.compare(pos_, n, lit) == 0) {
            pos_ += n;
            return true;
        }
        return false;
    }

    Value parseValue(int depth) {
        if (depth > 256) fail("nesting too deep");
        skipWs();
        if (pos_ >= s_.size()) fail("unexpected end of input");
        char c = s_[pos_];
        if (c == '{') return parseObject(depth);
        if (c == '[') return parseArray(depth);
        if (c == '"') return Value(parseString());
        if (consume("true")) return Value(true);
        if (consume("false")) return Value(false);
        if (consume("null")) return Value();
        if (c == '-' || (c >= '0' && c <= '9')) return parseNumber();
        fail("unexpected character");
    }

    Value parseNumber() {
        const char* begin = s_.c_str() + pos_;
        char* end = nullptr;
        double d = std::strtod(begin, &end);
        if (end == begin) fail("invalid number");
        pos_ += static_cast<size_t>(end - begin);
        return Value(d);
    }

    uint32_t parseHex4() {
        if (pos_ + 4 > s_.size()) fail("truncated unicode escape");
        uint32_t v = 0;
        for (int i = 0; i < 4; ++i) {
            char h = s_[pos_++];
            v <<= 4;
            if (h >= '0' && h <= '9') v |= static_cast<uint32_t>(h - '0');
            else if (h >= 'a' && h <= 'f') v |= static_cast<uint32_t>(h - 'a' + 10);
            else if (h >= 'A' && h <= 'F') v |= static_cast<uint32_t>(h - 'A' + 10);
            else fail("invalid unicode escape");
        }
        return v;
    }

    std::string parseString() {
        ++pos_;  // opening quote
        std::string out;
        while (true) {
            if (pos_ >= s_.size()) fail("unterminated string");
            char c = s_[pos_++];
            if (c == '"') break;
            if (c != '\\') {
                out += c;
                continue;
            }
            if (pos_ >= s_.size()) fail("unterminated escape");
            char e = s_[pos_++];
            switch (e) {
                case '"': out += '"'; break;
                case '\\': out += '\\'; break;
                case '/': out += '/'; break;
                case 'b': out += '\b'; break;
                case 'f': out += '\f'; break;
                case 'n': out += '\n'; break;
                case 'r': out += '\r'; break;
                case 't': out += '\t'; break;
                case 'u': {
                    uint32_t cp = parseHex4();
                    if (cp >= 0xD800 && cp <= 0xDBFF && consume("\\u")) {
                        uint32_t lo = parseHex4();
                        if (lo >= 0xDC00 && lo <= 0xDFFF) cp = 0x10000 + ((cp - 0xD800) << 10) + (lo - 0xDC00);
                    }
                    appendUtf8(out, cp);
                    break;
                }
                default: fail("invalid escape");
            }
        }
        return out;
    }

    Value parseArray(int depth) {
        ++pos_;
        Value v = Value::array();
        skipWs();
        if (pos_ < s_.size() && s_[pos_] == ']') {
            ++pos_;
            return v;
        }
        while (true) {
            v.push_back(parseValue(depth + 1));
            skipWs();
            if (pos_ >= s_.size()) fail("unterminated array");
            char c = s_[pos_++];
            if (c == ']') break;
            if (c != ',') fail("expected ',' or ']'");
        }
        return v;
    }

    Value parseObject(int depth) {
        ++pos_;
        Value v = Value::object();
        skipWs();
        if (pos_ < s_.size() && s_[pos_] == '}') {
            ++pos_;
            return v;
        }
        while (true) {
            skipWs();
            if (pos_ >= s_.size() || s_[pos_] != '"') fail("expected key");
            std::string key = parseString();
            skipWs();
            if (pos_ >= s_.size() || s_[pos_++] != ':') fail("expected ':'");
            v[key] = parseValue(depth + 1);
            skipWs();
            if (pos_ >= s_.size()) fail("unterminated object");
            char c = s_[pos_++];
            if (c == '}') break;
            if (c != ',') fail("expected ',' or '}'");
        }
        return v;
    }

    const std::string& s_;
    size_t pos_ = 0;
};

}  // namespace

const Value::Array& Value::items() const { return isArray() ? arr_ : kEmptyArray; }

Value::Array& Value::items() {
    if (isNull()) type_ = Type::Array;
    if (!isArray()) throw std::logic_error("json value is not an array");
    return arr_;
}

const Value::Object& Value::members() const { return isObject() ? obj_ : kEmptyObject; }

const Value& Value::operator[](const std::string& key) const {
    if (isObject())
        for (const auto& m : obj_)
            if (m.first == key) return m.second;
    return kNull;
}

Value& Value::operator[](const std::string& key) {
    if (isNull()) type_ = Type::Object;
    if (!isObject()) throw std::logic_error("json value is not an object");
    for (auto& m : obj_)
        if (m.first == key) return m.second;
    obj_.emplace_back(key, Value());
    return obj_.back().second;
}

bool Value::contains(const std::string& key) const {
    if (!isObject()) return false;
    for (const auto& m : obj_)
        if (m.first == key) return true;
    return false;
}

bool Value::erase(const std::string& key) {
    if (!isObject()) return false;
    for (auto it = obj_.begin(); it != obj_.end(); ++it) {
        if (it->first == key) {
            obj_.erase(it);
            return true;
        }
    }
    return false;
}

size_t Value::size() const {
    if (isArray()) return arr_.size();
    if (isObject()) return obj_.size();
    return 0;
}

std::string Value::dump(int indent) const {
    std::string out;
    dumpTo(out, indent, 0);
    return out;
}

void Value::dumpTo(std::string& out, int indent, int depth) const {
    auto newline = [&](int d) {
        if (indent < 0) return;
        out += '\n';
        out.append(static_cast<size_t>(indent * d), ' ');
    };
    switch (type_) {
        case Type::Null: out += "null"; break;
        case Type::Bool: out += bool_ ? "true" : "false"; break;
        case Type::Number: {
            if (!std::isfinite(num_)) {
                out += "null";
            } else if (num_ == std::floor(num_) && std::fabs(num_) < 9.007199254740992e15) {
                out += std::to_string(static_cast<int64_t>(num_));
            } else {
                char buf[32];
                std::snprintf(buf, sizeof buf, "%.9g", num_);
                out += buf;
            }
            break;
        }
        case Type::String: escapeString(out, str_); break;
        case Type::Array: {
            out += '[';
            for (size_t i = 0; i < arr_.size(); ++i) {
                if (i) out += ',';
                newline(depth + 1);
                arr_[i].dumpTo(out, indent, depth + 1);
            }
            if (!arr_.empty()) newline(depth);
            out += ']';
            break;
        }
        case Type::Object: {
            out += '{';
            for (size_t i = 0; i < obj_.size(); ++i) {
                if (i) out += ',';
                newline(depth + 1);
                escapeString(out, obj_[i].first);
                out += indent < 0 ? ":" : ": ";
                obj_[i].second.dumpTo(out, indent, depth + 1);
            }
            if (!obj_.empty()) newline(depth);
            out += '}';
            break;
        }
    }
}

Value Value::parse(const std::string& text) { return Parser(text).parseDocument(); }

}  // namespace swingcore::json
