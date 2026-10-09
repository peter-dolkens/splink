// Host-side tests for the pure-protocol code. These compile and run on a development machine,
// so the wire format and the arithmetic are verified without hardware in the loop.
//
// The decode vector is real: raw words captured from a live SPMC482 alongside what the C#
// bridge decoded from the same registers at the same moment. If this file passes, the firmware
// and splink agree on that inverter's readings.
#include "sppro.h"

#include <math.h>
#include <stdio.h>
#include <string.h>

static int failures = 0;

static void check(bool ok, const char *what) {
    printf("  %-58s %s\n", what, ok ? "ok" : "FAIL");
    if (!ok) failures++;
}

static void check_near(double got, double want, double tol, const char *what) {
    bool ok = fabs(got - want) <= tol;
    printf("  %-58s %s (got %.6f want %.6f)\n", what, ok ? "ok" : "FAIL", got, want);
    if (!ok) failures++;
}

int main(void) {
    printf("CRC-16/KERMIT\n");
    // The standard check value for this CRC, and the one the C# tests assert.
    check(sppro_crc16((const uint8_t *)"123456789", 9) == 0x2189, "\"123456789\" -> 0x2189");

    printf("framing\n");
    uint8_t frame[SPPRO_HEADER_LEN];
    check(sppro_build_read(frame, 0xA000, 1), "build read of the link port");
    const uint8_t want[6] = { 0x51, 0x00, 0x00, 0xA0, 0x00, 0x00 };
    check(memcmp(frame, want, 6) == 0, "header is 51 00 00 A0 00 00");
    check(sppro_crc_ok(frame, sizeof frame), "header carries a valid CRC residue");
    check(!sppro_build_read(frame, 0xA000, 0), "zero words is rejected");
    check(!sppro_build_read(frame, 0xA000, 257), "257 words is rejected");

    printf("response parsing\n");
    // A two-word response, built the way the device would: echoed header, data, data CRC.
    uint8_t resp[SPPRO_HEADER_LEN + 4 + SPPRO_CRC_LEN];
    sppro_build_read(resp, 0xA000, 2);
    const uint16_t payload[2] = { 0x1234, 0xABCD };
    for (int i = 0; i < 2; i++) {
        resp[SPPRO_HEADER_LEN + i * 2]     = (uint8_t)(payload[i] & 0xFF);
        resp[SPPRO_HEADER_LEN + i * 2 + 1] = (uint8_t)(payload[i] >> 8);
    }
    uint16_t dcrc = sppro_crc16(resp + SPPRO_HEADER_LEN, 4);
    resp[SPPRO_HEADER_LEN + 4] = (uint8_t)dcrc;
    resp[SPPRO_HEADER_LEN + 5] = (uint8_t)(dcrc >> 8);

    uint16_t out[2] = { 0, 0 };
    check(sppro_parse_response(resp, sizeof resp, out, 2), "well-formed response accepted");
    check(out[0] == 0x1234 && out[1] == 0xABCD, "words come back little-endian");
    check(!sppro_parse_response(resp, sizeof resp, out, 1), "declared word count must match");
    resp[9] ^= 0xFF;
    check(!sppro_parse_response(resp, sizeof resp, out, 2), "corrupted data is rejected");

    printf("fast decode against a live capture\n");
    // Captured from an SPMC482 (firmware 16.11) with splink reading the same registers.
    const uint16_t scale_words[6] = { 5300, 2934, 1050, 16000, 530, 180 };
    sppro_scale_t scale;
    check(sppro_scale_from_words(scale_words, 6, &scale), "scale factors parsed");

    const uint16_t run0[2]  = { 65448, 65535 };
    const uint16_t run1[12] = { 88, 0, 3, 2, 22, 22, 0, 25148, 0, 33, 65503, 65535 };
    sppro_fast_t f;
    sppro_decode_fast(run0, run1, &scale, &f);

    // The right-hand values are what the C# bridge published for this same instant.
    check_near(f.inverter_ac_kw,      -0.052,  0.0005, "inverter AC kW");
    check_near(f.ac_load_kw,           0.052,  0.0005, "AC load kW");
    check_near(f.battery_soc_percent, 98.234,  0.001,  "battery SoC %");
    check_near(f.dc_amps,              1.61,   0.005,  "DC amps");
    check_near(f.battery_amps,        -1.61,   0.005,  "battery amps");
    check(f.soc_valid, "SoC is marked valid");
    check(strcmp(f.charger_state, "Short Term Float") == 0, "charger state string");
    check(strcmp(f.inverter_mode, "On") == 0, "inverter mode string");
    check(strcmp(f.ac_source_status, "AC Source Not Present") == 0, "AC source status string");

    printf("sentinels\n");
    uint16_t run1_unknown[12];
    memcpy(run1_unknown, run1, sizeof run1);
    run1_unknown[41 - SPPRO_FAST_RUN1_OFF] = 0xFFFF;   // SoC "not known"
    sppro_decode_fast(run0, run1_unknown, &scale, &f);
    check(!f.soc_valid, "0xFFFF SoC reports unknown rather than 255.996%");

    printf("\n%s\n", failures ? "FAILURES" : "all passed");
    return failures ? 1 : 0;
}
