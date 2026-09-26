// Minimal, dependency-free JSON value used for session/annotation persistence
// and for exchanging structured data across the C ABI.
#pragma once

#include <cstdint>
#include <stdexcept>
#include <string>
#include <utility>
#include <vector>

namespace swingcore::json {

class ParseError : public std::runtime_error {
public:
    ParseError(const std::string& msg, size_t at)
        : std::runtime_error(msg + " at offset " + std::to_string(at)), offset(at) {}
    size_t offset;
};

class Value {
public:
    enum class Type { Null, Bool, Number, String, Array, Object };
    using Array = std::vector<Value>;
    using Member = std::pair<std::string, Value>;
    using Object = std::vector<Member>;  // insertion ordered for stable output

    Value() = default;
    Value(std::nullptr_t) {}
    Value(bool b) : type_(Type::Bool), bool_(b) {}
    Value(int n) : type_(Type::Number), num_(n) {}
    Value(int64_t n) : type_(Type::Number), num_(static_cast<double>(n)) {}
    Value(double n) : type_(Type::Number), num_(n) {}
    Value(float n) : type_(Type::Number), num_(n) {}
    Value(const char* s) : type_(Type::String), str_(s) {}
    Value(std::string s) : type_(Type::String), str_(std::move(s)) {}
    Value(Array a) : type_(Type::Array), arr_(std::move(a)) {}

    static Value object() { Value v; v.type_ = Type::Object; return v; }
    static Value array() { Value v; v.type_ = Type::Array; return v; }

    Type type() const { return type_; }
    bool isNull() const { return type_ == Type::Null; }
    bool isBool() const { return type_ == Type::Bool; }
    bool isNumber() const { return type_ == Type::Number; }
    bool isString() const { return type_ == Type::String; }
    bool isArray() const { return type_ == Type::Array; }
    bool isObject() const { return type_ == Type::Object; }

    bool asBool(bool def = false) const { return isBool() ? bool_ : def; }
    double asNumber(double def = 0) const { return isNumber() ? num_ : def; }
    int64_t asInt(int64_t def = 0) const { return isNumber() ? static_cast<int64_t>(num_) : def; }
    std::string asString(const std::string& def = {}) const { return isString() ? str_ : def; }

    const Array& items() const;  // empty for non-arrays
    Array& items();              // converts Null to Array
    const Object& members() const;

    // Object access. The const form returns a shared Null for missing keys.
    const Value& operator[](const std::string& key) const;
    Value& operator[](const std::string& key);  // converts Null to Object, inserts
    bool contains(const std::string& key) const;
    bool erase(const std::string& key);

    void push_back(Value v) { items().push_back(std::move(v)); }
    size_t size() const;

    std::string dump(int indent = -1) const;
    static Value parse(const std::string& text);

private:
    void dumpTo(std::string& out, int indent, int depth) const;

    Type type_ = Type::Null;
    bool bool_ = false;
    double num_ = 0;
    std::string str_;
    Array arr_;
    Object obj_;
};

}  // namespace swingcore::json
