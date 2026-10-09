// SP PRO native protocol — the parts an MQTT bridge needs.
//
// Ported from the C# implementation in src/SpLink.Protocol, which is the verified reference.
// The arithmetic is kept identical on purpose: a value read here and the same value read by
// splink must agree bit for bit, or the two are not reporting the same system.
//
// Nothing in this header depends on ESP-IDF, so it builds and is tested on a host as well as
// on target. See test/ for the vectors.
#ifndef SPPRO_H
#define SPPRO_H

#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

// --- CRC-16/KERMIT -------------------------------------------------------------------------
// Reflected 0x1021 -> 0x8408, init 0, no final XOR, appended little-endian. A frame carrying
// its own CRC has a residue of zero over its full length, which is how responses are checked.
uint16_t sppro_crc16(const uint8_t *data, size_t len);
bool sppro_crc_ok(const uint8_t *frame_with_crc, size_t len);

// --- Framing -------------------------------------------------------------------------------
#define SPPRO_OP_READ   0x51u   // 'Q'
#define SPPRO_OP_WRITE  0x57u   // 'W'
#define SPPRO_HEADER_LEN 8
#define SPPRO_CRC_LEN    2
#define SPPRO_MAX_WORDS  256

// Header: op, wordCount-1, 32-bit little-endian address, CRC over those six bytes.
// Writes 8 bytes into `out`. Returns false if word_count is outside 1..256.
bool sppro_build_read(uint8_t out[SPPRO_HEADER_LEN], uint32_t address, int word_count);

// Builds a write frame: header, then word_count little-endian words, then a CRC over the data
// bytes only. `out` must hold SPPRO_HEADER_LEN + word_count*2 + SPPRO_CRC_LEN.
//
// This exists for one reason: logging in requires writing the challenge response. There is no
// other write path in this firmware and there should not be one -- see README.
bool sppro_build_write(uint8_t *out, uint32_t address, const uint16_t *words, int word_count);

// Total length of the response to a read of word_count words.
static inline size_t sppro_response_len(int word_count) {
    return (size_t)SPPRO_HEADER_LEN + (size_t)word_count * 2 + SPPRO_CRC_LEN;
}

// Validates a complete read response and copies out its data words.
// Checks the echoed header, the declared word count and the CRC residue.
bool sppro_parse_response(const uint8_t *frame, size_t len, uint16_t *words, int word_count);

// --- Registers -----------------------------------------------------------------------------
#define SPPRO_REG_LINK_PORT      40960u     // 0xA000, 1 word; 0xFFFF means a login is required
#define SPPRO_REG_LOGIN_CHALLENGE 0x1F0000u  // read 8 words, write the 8-word answer back here
#define SPPRO_REG_LOGIN_RESULT    0x1F0010u  // 1 word; 1 on success
#define SPPRO_REG_COMMON_SCALE   41000u   // 6 words
#define SPPRO_REG_NOW_BLOCK      41048u   // 85 words

// The fast window: everything a shedding decision needs, as two runs rather than all 85 words.
// Word 0-1 is inverter AC power; 34-45 carries load power, charger/inverter/source state, SoC
// and the DC currents.
#define SPPRO_FAST_RUN0_OFF   0
#define SPPRO_FAST_RUN0_LEN   2
#define SPPRO_FAST_RUN1_OFF   34
#define SPPRO_FAST_RUN1_LEN   12

// --- Scaling -------------------------------------------------------------------------------
// Model-specific factors, read from the device rather than assumed.
typedef struct {
    int16_t ac_volts;
    int16_t ac_current;
    int16_t dc_volts;
    int16_t dc_current;
    int16_t temperature;
    int16_t internal_volts;
} sppro_scale_t;

bool sppro_scale_from_words(const uint16_t *words, int count, sppro_scale_t *out);

// --- Fast reading --------------------------------------------------------------------------
typedef struct {
    double ac_load_kw;
    double inverter_ac_kw;
    double battery_soc_percent;   // valid only when soc_valid
    bool   soc_valid;             // raw 0xFFFF means "unknown", not 255.996%
    double battery_amps;
    double dc_amps;
    const char *charger_state;
    const char *inverter_mode;
    const char *ac_source_status;
} sppro_fast_t;

// Decodes the fast window. `run0` is 2 words from offset 0, `run1` is 12 words from offset 34.
void sppro_decode_fast(const uint16_t *run0, const uint16_t *run1,
                       const sppro_scale_t *scale, sppro_fast_t *out);

const char *sppro_charger_state_name(uint16_t raw);
const char *sppro_inverter_mode_name(uint16_t raw);
const char *sppro_ac_source_status_name(uint16_t raw);

// --- Login ---------------------------------------------------------------------------------
#define SPPRO_PASSWORD_LEN 32
#define SPPRO_CHALLENGE_LEN 16

// MD5 over (16-byte challenge || password space-padded to 32 ASCII bytes), packed as 8 words.
// `out_words` receives 8 words. Returns false if the password exceeds 32 characters.
bool sppro_login_response(const uint8_t challenge[SPPRO_CHALLENGE_LEN],
                          const char *password, uint16_t out_words[8]);

#ifdef __cplusplus
}
#endif
#endif  // SPPRO_H
