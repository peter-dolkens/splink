#include "sppro.h"

#include <stdio.h>

// The strings must match src/SpLink.Protocol/SpProLiveData.cs exactly. Home Assistant keys
// state history on the published string, so a bridge that says "Short-term float" where splink
// says "Short Term Float" splits one sensor's history in two.
//
// Unknown codes format into a small static buffer. That is not reentrant, and it does not need
// to be: one task reads the inverter. If that ever stops being true this must become a caller-
// supplied buffer rather than quietly returning shared memory.
static const char *unknown(uint16_t value) {
    static char buf[20];
    snprintf(buf, sizeof buf, "Unknown (%u)", (unsigned)value);
    return buf;
}

const char *sppro_charger_state_name(uint16_t raw) {
    switch (raw) {
        case 0: return "Initial";
        case 1: return "Bulk";
        case 2: return "Absorb";
        case 3: return "Short Term Float";
        case 4: return "Return to Float";
        case 5: return "Equalise";
        case 6: return "Long Term Float";
        default: return unknown(raw);
    }
}

const char *sppro_inverter_mode_name(uint16_t raw) {
    switch (raw) {
        case 0: return "Idle";
        case 1: return "Econo";
        case 2: return "On";
        case 3: return "Sync";
        default: return unknown(raw);
    }
}

// The settings-version-22 table: codes 2-5 and 7 all collapse to "in tolerance", where older
// firmware distinguished lockout and capacity-limit states.
const char *sppro_ac_source_status_name(uint16_t raw) {
    switch (raw) {
        case 0: return "AC Source Not Present";
        case 1: return "E-N Link Not Detected";
        case 2: case 3: case 4: case 5: case 7: return "AC Source in Tolerance";
        case 6: return "Outside operating range";
        case 8: return "Volts too high for freq";
        case 9: return "Disconnected by DRM 0";
        default: return unknown(raw);
    }
}
