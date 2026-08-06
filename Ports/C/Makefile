CC ?= cc
CFLAGS ?= -std=c11 -Wall -Wextra -O2 -g
CPPFLAGS += -Iinclude -D_POSIX_C_SOURCE=200809L -D_DEFAULT_SOURCE
LDLIBS += -lssl -lcrypto -lpthread

BUILD_DIR := build

LIB_SOURCES := $(wildcard src/*.c)
LIB_OBJECTS := $(patsubst src/%.c,$(BUILD_DIR)/%.o,$(LIB_SOURCES))
LIB := $(BUILD_DIR)/liboft.a

TEST_SOURCES := $(wildcard tests/*.c)
TEST_OBJECTS := $(patsubst tests/%.c,$(BUILD_DIR)/tests/%.o,$(TEST_SOURCES))
TEST_BIN := $(BUILD_DIR)/oft_tests

.PHONY: all lib test clean

all: lib

lib: $(LIB)

$(LIB): $(LIB_OBJECTS)
	ar rcs $@ $^

$(BUILD_DIR)/%.o: src/%.c | $(BUILD_DIR)
	$(CC) $(CPPFLAGS) $(CFLAGS) -c $< -o $@

$(BUILD_DIR)/tests/%.o: tests/%.c | $(BUILD_DIR)/tests
	$(CC) $(CPPFLAGS) -Isrc $(CFLAGS) -c $< -o $@

$(TEST_BIN): $(LIB) $(TEST_OBJECTS)
	$(CC) $(CFLAGS) $(TEST_OBJECTS) $(LIB) -o $@ $(LDFLAGS) $(LDLIBS)

test: $(TEST_BIN)
	./$(TEST_BIN)

$(BUILD_DIR) $(BUILD_DIR)/tests:
	mkdir -p $@

clean:
	rm -rf $(BUILD_DIR)
