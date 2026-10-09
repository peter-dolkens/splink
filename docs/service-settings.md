# SP PRO Service Settings register map

SP LINK's **Service Settings** are a separate block from the Configuration blocks
in splink's config map. Located by value-searching a SP LINK Service Settings export
against the register space on an SPMC482-AU (firmware 15.47): the whole 127-word block
begins at register **49665**, and the 39 named settings matched 39/39.

Blank rows in SP LINK's export are spacer words in the block (read as 0 or 0xFFFF)
and are omitted here. Values are raw 16-bit; converters (scaling/enums) not included.

| Register | Offset | Setting |
| --- | --- | --- |
| 49665 | +0 | FanType |
| 49666 | +1 | InitialSoC |
| 49667 | +2 | Shunt1UserZero |
| 49668 | +3 | Shunt2UserZero |
| 49669 | +4 | ACInputDisconnectionMonitor |
| 49670 | +5 | ExpansionCardEnabled |
| 49674 | +9 | SynchOverloadShutdownCountTrips |
| 49675 | +10 | EmulatedInductanceRatio |
| 49676 | +11 | ActiveAntiIslandingSensitivityPercent |
| 49677 | +12 | MultiPhaseValuesSplink |
| 49678 | +13 | AllowPowerOverride |
| 49679 | +14 | IndependentGridDisconnect |
| 49680 | +15 | AcSolarIntegralGainConnectedToSource |
| 49681 | +16 | AcSolarDifferentialGainConnectedToSource |
| 49682 | +17 | AcSolarFeedForwardGainConnectedToSource |
| 49683 | +18 | AcSolarProportionalGainConnectedToSource |
| 49684 | +19 | AcSolarIntegralGain |
| 49685 | +20 | AcSolarDifferentialGain |
| 49686 | +21 | AcSolarFeedForwardGain |
| 49687 | +22 | AcSolarProportionalVoltage0 |
| 49688 | +23 | AcSolarProportionalGain0 |
| 49689 | +24 | AcSolarProportionalVoltage1 |
| 49690 | +25 | AcSolarProportionalGain1 |
| 49691 | +26 | AcSolarProportionalVoltage2 |
| 49692 | +27 | AcSolarProportionalGain2 |
| 49693 | +28 | AcSolarProportionalVoltage3 |
| 49694 | +29 | AcSolarProportionalGain3 |
| 49695 | +30 | AcSolarProportionalVoltage4 |
| 49696 | +31 | AcSolarProportionalGain4 |
| 49697 | +32 | ServiceSettingReserved33 |
| 49698 | +33 | ServiceSettingReserved34 |
| 49699 | +34 | AemoRideThrough |
| 49700 | +35 | ActivePowerDisconnect |
| 49701 | +36 | AcSolarFilterRisingSync |
| 49702 | +37 | AcSolarFilterFallingSync |
| 49703 | +38 | AcSolarFilterRisingStandalone |
| 49704 | +39 | AcSolarFilterFallingStandalone |
| 49705 | +40 | DefaultExportLimit |
| 49706 | +41 | DefaultExportLimitRampRate |

## Verification

Confirmed against a second unit — SPMC482, firmware **16.11** — by reading 42 words from
49665 and checking the values against what is independently known about that installation:
`ExpansionCardEnabled` reads 1 and the ACC is fitted; `Shunt1UserZero` and `Shunt2UserZero`
read 0 and no shunts are present; `InitialSoC` reads 85. The decisive check is the spacers at
+6/+7/+8, which read `0000`, `FFFF`, `0000` — an arbitrary pattern the map predicts in
advance. So the block is at the same address across firmware 15.47 and 16.11.

`AllowPowerOverride` (+13) is worth singling out: it gates registers 8032 and 8033 in
[the Modbus map](modbus.md), which are otherwise writable. It reads 0 on the verified unit.

> **These are service settings, several of them grid-protection parameters** —
> anti-islanding sensitivity, ride-through, disconnection monitoring, export limits. splink is
> read-only by design and must stay that way here. Writing these is not a thing this project
> does, and getting them wrong has consequences beyond the inverter.

## Provenance

Recovered by **Stephen** from his own unit's SP LINK Service Settings export, value-searched
against the register space on an SPMC482-AU running firmware 15.47, and contributed for this
reference material. Reproduced as interface facts. Not affiliated with or endorsed by
Selectronic Australia Pty Ltd; "SP PRO" and "SP LINK" are their trademarks.