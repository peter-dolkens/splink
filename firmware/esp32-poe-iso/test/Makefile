# Host tests for the pure-protocol code. No ESP-IDF, no hardware:
#   make -C firmware/esp32-poe-iso/test run
CFLAGS ?= -std=c11 -Wall -Wextra -Werror -O1 -I../components/sppro/include
SRC = test_sppro.c ../components/sppro/sppro_proto.c ../components/sppro/sppro_names.c

run: test_sppro
	./test_sppro

test_sppro: $(SRC)
	$(CC) $(CFLAGS) $(SRC) -o $@ -lm

clean:
	rm -f test_sppro
.PHONY: run clean
