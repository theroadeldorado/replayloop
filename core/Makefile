# Quick local build of the core and its tests (Linux/macOS).
# Release builds for the apps go through CMakeLists.txt.
CXX ?= g++
CXXFLAGS ?= -std=c++17 -O2 -g -Wall -Wextra -Wpedantic -Wshadow -Wconversion -Wno-sign-conversion
CPPFLAGS += -Iinclude

SRC := $(wildcard src/*.cpp)
OBJ := $(patsubst src/%.cpp,build/obj/%.o,$(SRC))

.PHONY: all test clean
all: build/libswingcore.a build/swingcore_tests

build/obj/%.o: src/%.cpp $(wildcard include/swingcore/*.h)
	@mkdir -p $(dir $@)
	$(CXX) $(CPPFLAGS) $(CXXFLAGS) -fPIC -c $< -o $@

build/libswingcore.a: $(OBJ)
	ar rcs $@ $^

build/swingcore_tests: tests/test_main.cpp build/libswingcore.a
	$(CXX) $(CPPFLAGS) $(CXXFLAGS) $< build/libswingcore.a -o $@

test: build/swingcore_tests
	./build/swingcore_tests

clean:
	rm -rf build
