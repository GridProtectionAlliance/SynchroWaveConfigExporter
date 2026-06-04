# SEL SynchroWave Configuration Model — Reference & Generation Guidance

> Purpose: establish a precise, shared understanding of **what the SEL SynchroWave
> Operations configuration files mean**, how they relate to one another, and how the
> `SynchroWaveConfigExporter` derives them from a GSF Time-Series Library (TSL)
> openPDC, openHistorian or SIEGate database — so the generated power-system model is correct for SEL's
> analytics (power monitor, one-line, phasor scope, etc.).
>
> Sources: the SEL *Synchrowave Operations and Platform* Instruction Manual (IM), the
> SEL-provided example configuration files, and the `SynchroWaveConfigExporter` source.

---

## 1. Overview

SynchroWave is **not** a device/measurement system like the GPA TSL stack — it is a
**signal + power-system-model** system. Every value flowing through it is a **Signal**,
defined as exactly two parts (IM § "SEL Signals Application"):

```
Signal  =  MeasurementPoint  .  Quantity
           └ physical location┘  └ signal type (data type) ┘
```

The **Power System Model** (5 CSV files: stations, buses, lines, poles, segments) is a
*data provider* on the signal bus whose only job is to **associate those signals with
power equipment** so the analytics know that current `X` and voltage `Y` belong to the
same line terminal. The **bus** is the hinge of that association: at a line terminal,
power is computed from the line's **current** (carried by the *line* terminal MP) and the
terminal's **voltage** (carried by the *adjacent bus*). That is why SEL calls the bus a
*notional* link "between lines and measurements" — it is largely invisible in the UI but
indispensable to the math.

The source openHistorian database uses the standard GSF schema, which has **no
station/bus/line tables**; the exporter infers the power-system topology from device,
phasor, and signal-type metadata. This document explains the model, maps each SEL rule to
its semantics, and specifies the derivation the exporter implements.

---

## 2. The SEL signal model: Measurement Point + Quantity

From the IM ("SEL Signals Application" and the STTP Reader section):

- **Measurement Point (MP)** — "Represents the **physical location** at which the signal
  was measured." It is a *named* identifier. SEL's canonical form is dotted/hierarchical,
  e.g. `SubstationA.TransmissionLine1`. A flat, compressed token (for example, to satisfy
  a downstream length limit) is equally legal — "Measurement points, quantity … and asset
  names can include letters, numbers, periods, and underscores."
- **Quantity (a.k.a. Data Type)** — "Represents the **type** of signal measured." SEL
  defines a fixed vocabulary of **Standard Quantities** (IM Table 2.1). The phase modifier
  `P ∈ {A,B,C,0,1,2}` where 0/1/2 = zero/positive/negative sequence:

  | Meaning | Quantity name |
  |---|---|
  | Frequency / ROCOF | `Frequency`, `Frequency.DxDt` |
  | L-N voltage phasor / mag / angle | `Phase[P].Voltage`, `Phase[P].Voltage.Magnitude`, `Phase[P].Voltage.Angle` |
  | L-L voltage mag / ROCOV | `LL.Voltage.Magnitude`, `LL.Voltage.DxDt` |
  | Current phasor / mag / angle | `Phase[P].Current`, `Phase[P].Current.Magnitude`, `Phase[P].Current.Angle` |
  | Phase angle difference | `PAD` |
  | 1-φ power (S/P/Q/pf) | `Phase[P].Power.{Apparent,Real,Reactive,Factor}` |
  | 3-φ power (S/P/Q/pf) | `ThreePhase.Power.{Apparent,Real,Reactive,Factor}` |
  | Sample validity | `Availability` |

The **full signal name is `MP.Quantity`**:

```
MeasurementPoint = SubstationA.TransmissionLine1
Quantity         = PhaseA.Voltage.Magnitude
Signal           = SubstationA.TransmissionLine1.PhaseA.Voltage.Magnitude
```

### Why the MP groups many quantities
One MP carries *all* the channels measured at that location: per-phase + sequence,
voltage + current, magnitude + angle, frequency, power, availability. In the exporter's
`sel-sttpreader-signalmappings.csv` one MP repeats across many rows, one row per Quantity.
**A signal is only published if BOTH MP and Quantity are filled**; rows with blanks are
ignored. This is the STTP Reader's job: map each incoming `DeviceAcronym`/`Description`
(auto-filled from the STTP stream's Station+Tag) to an `MP` + `Quantity`.

> **Implication for naming:** the `MeasurementPoint` value used in the signal mappings is
> the *same string* later referenced as `FromTerminalMP`/`ToTerminalMP` in `lines.csv`.
> The model files do not invent MPs — they **reference** the MPs minted by the signal
> mapping. Any terminal MP in `lines.csv` must exist in the signal mappings, or the line
> terminal has no data behind it; the exporter maintains this by construction.

---

## 3. The Power System Model files

Five CSV files (IM "Power System Model Application"). Column names below follow the
**authoritative example CSVs SEL ingests**, which is why the bus adjacency column is
`AdjacentBusIds` — the IM *prose* writes it "Adjacent BusId," but the manual prose is not
authoritative. The SEL-native `poles.csv`/`segments.csv` templates confirm the `XxxId`
casing convention used throughout. (The SEL-native templates put a space after each comma;
the tool omits it, which the in-use example files show SEL accepts.)

### 3.1 `stations.csv` — substations (display + GIS anchor)
| Column | Meaning |
|---|---|
| `StationId` | Unique id; **also the display name**. |
| `Latitude`, `Longitude` | GIS location (degrees). |
| `NominalVoltageKV` | **Maximum** voltage level at the station (highest of its buses). |

### 3.2 `buses.csv` — buses (the notional voltage node)
| Column | Meaning |
|---|---|
| `BusId` | Unique id; display name. |
| `StationId` | **FK → `stations.csv`** (must match exactly). |
| `NominalVoltageKV` | Nominal voltage of this bus. |
| `AdjacentBusIds` | `;`-separated list of buses connected by **zero impedance / breakers only**. "**If there is no measurement at the adjacent bus, ignore the adjacent bus.**" |

A station has one bus per voltage level present.

### 3.3 `lines.csv` — transmission lines (the topology + measurement binding)
| Column | Meaning |
|---|---|
| `LineId` | Unique id; display name. |
| `NominalVoltageKV` | Nominal voltage of the line. |
| `FromBusId`, `ToBusId` | **FK → `buses.csv`**: the buses at the two line terminals. |
| `FromTerminalMP`, `ToTerminalMP` | The MP at each terminal — "typically used for line **current** measurements **near the buses** `FromBusId` and `ToBusId`, **respectively**." |

Key rules from the manual:
- The word **"respectively"** binds `FromTerminalMP`↔`FromBusId` and
  `ToTerminalMP`↔`ToBusId`. **Each terminal's MP and bus live on the same side.**
- A row describes **one two-terminal line**. A multi-terminal line is expressed as one
  row **per unique pair of terminals** (a 3-terminal line = 2 rows, not 3).

### 3.4 `poles.csv` / `segments.csv` — GIS geometry only (optional)
`poles.csv` = `LineId, PoleId, Latitude, Longitude` (towers; stations are the end poles).
`segments.csv` = `LineId, FromPoleId, ToPoleId` (drawn connectors; must start/end at
station poles). Both are optional and **not** required for analytics — they only shape the
GIS one-line drawing. The exporter does not need to populate them.

### 3.5 How the files relate

```
   stations.csv                         buses.csv                         lines.csv
   ┌───────────┐  StationId (FK)  ┌──────────────────┐  BusId (FK)   ┌─────────────────────────┐
   │ StationId │◄─────────────────│ BusId            │◄──────────────│ FromBusId   ToBusId     │
   │ Lat/Lon   │                  │ StationId        │               │ FromTerminalMP          │
   │ NomKV(max)│                  │ NominalVoltageKV │               │ ToTerminalMP            │
   └───────────┘                  │ AdjacentBusIds  ─┼─┐(self-ref    │ NominalVoltageKV        │
                                  └──────────────────┘ │ ;-list)     └───────────┬─────────────┘
                                            ▲          │                         │ MP (string, FK by name)
                                            └──────────┘                         ▼
                                                                      sel-sttpreader-signalmappings.csv
                                                                      ┌──────────────────────────────┐
                                                                      │ DeviceAcronym, Description,  │
                                                                      │ MeasurementPoint, Quantity   │
                                                                      └──────────────────────────────┘
```

The bus is the **only** thing tying a line terminal to a substation, and it does so
*through the measurements*: the terminal MP supplies the **current**, the adjacent bus
supplies the **voltage**, and SEL's Power Monitor multiplies them to get the per-terminal
power (IM "Power Calculation"):

> "Single- and three-phase powers … are calculated **at each terminal** of a transmission
> line from current and voltage phasor measurements at that terminal. **Current**
> measurements are associated with the **Line Asset** MP. **Voltage** measurements can be
> associated with either the Line Asset MP **or the adjacent Bus** to the Line Asset."

This is the literal meaning of SEL's note that the bus is "a *notional* link between lines
and measurements that is not shown to the user" — and the license to **generate buses
freely** to carry that link.

---

## 4. The source GSF/TSL model and how it maps

The source openHistorian database uses the standard GSF schema. Relevant tables:
`Measurement`, `Phasor`, `Device`, `SignalType`.

There is **no** Station/Bus/Line table — the topology must be *inferred*. The signals that
make inference possible:

| GSF concept | Field(s) | SEL concept it feeds |
|---|---|---|
| **Device** (PMU/DFR), `Acronym`, `Name`, `Latitude`, `Longitude` | identity + location | **Station** (group devices by GPS); device-type markers split PMU vs DFR |
| **Device.Name** for PMU, e.g. `STATION-REMOTE {KV}KV` | `STATION-REMOTE {KV}KV` | **Line** endpoints + nominal kV (both ends in one string) |
| **Phasor** `Type` (V/I), `Phase`, `BaseKV`, `Label` | per-channel def | **Quantity**, bus voltage levels, DFR line names (Label) |
| **Phasor.DestinationPhasorID** | I-phasor → its V-phasor | the **current↔voltage pairing** = line-current ↔ bus-voltage at a terminal |
| **SignalType** (`IPHM/IPHA/VPHM/VPHA/FREQ/…`) | channel type | maps to Standard **Quantity** names |
| **Measurement.AlternateTag** | optional | optional persistence target for the minted MP |

### Two device archetypes

**PMU line-terminal device** — one device = **one line terminal**:
```
Acronym: STATION_REMOTE_P_<id>   Name: "STATION-REMOTE 500KV"   @(lat, lon)
  Phasors:  IA(I,A,500)→VA   IB(I,B,500)→VB   IC(I,C,500)→VC   I1(I,+,500)→V1
            VA(V,A,500)      VB(V,B,500)      VC(V,C,500)      V1(V,+,500)
```
Reading: a 500 kV line **STATION→REMOTE**, measured **at STATION**. The current phasors
are the *line* current; each `DestinationPhasorID` points at the matching *bus* voltage.
This is SEL's terminal model verbatim: current = Line Asset MP, voltage = adjacent
(STATION 500) Bus.

**DFR device** — one device = **many line terminals at one station**:
```
Acronym: STATION_<n>_D_<id>   @ STATION
  Label=REMOTE_1 (V/I, 500)   Label=REMOTE_2 (V/I, 230)   Label=REMOTE_3 (V/I, 230)   …
```
Each distinct `Label` is a separate terminal/line off the station, at its own kV (so a
station can have both a 500 and 230 bus). I-phasors again carry `DestinationPhasorID` →
V-phasor.

### Structural facts the derivation relies on
1. **Most lines are measured at only one end.** In typical PMU/DFR deployments the large
   majority of lines are instrumented at a single terminal; only a minority are genuinely
   two-ended (measured at both terminals). For the single-ended majority the From/To label
   is arbitrary, and the only thing that matters is keeping each terminal's MP with its
   own bus.
2. Therefore the From/To distinction is, for almost every line, **arbitrary** — there is a
   *measured* terminal and an *unmeasured* remote. The correct output is: the measured
   terminal fully specified on one side (MP + its bus), the remote as a bus-only anchor on
   the other side (or blank).
3. The current↔voltage pairing needed for power is already explicit in
   `Phasor.DestinationPhasorID`; the exporter follows it both for kV resolution
   (`ResolveVoltageKV`) and to keep a terminal's MP and bus together.

---

## 5. The modeling rules, in SEL terms

The rules SEL described are all corollaries of §2–§3. Stated by their *notion* (the
authoritative phrasing — not numbered in code):

- **A terminal's MP must have a corresponding bus on the same side; the MP drives the
  bus.** A populated `FromTerminalMP` requires a `FromBusId` (same for To). Rationale: the
  bus is the voltage half of the terminal's power calc; an MP with no bus is a current
  with no voltage to pair against. The bus is *generated to serve the MP*.
- **From-bus and To-bus must never be the same; leave the unused side blank.** A line is an
  *inter-bus* element. `FromBusId == ToBusId` is not a line — it is a single-terminal asset
  (generator, transformer, unit, internal tap). Such assets get one populated side only.
- **Each MP appears once in `lines.csv` (in From or To, across the whole file).** An MP is a
  specific physical terminal; it cannot be the terminal of two different lines. Duplicates
  mean two rows are competing to describe the same terminal.
- **A `BusId` may not equal any From/To MP string.** Buses and MPs share the model's name
  space and must stay disjoint, or a reference is ambiguous.
- **A line with two buses must reference two distinct *substations*.** Stronger than the
  bus-equality rule: even different `BusId`s that resolve to the *same* `StationId` are
  invalid as a line's two ends.
- **Every station must own at least one bus.** A station with no bus anchors no
  measurement and has no reason to exist; conversely every bus's `StationId` must be a real
  station.

Not every measurement point is a line current. Per SEL guidance, points that are **not**
line currents are anchored to the model as follows:
- **Bus-voltage MPs** → a `buses.csv` row whose **`BusId` matches the bus MP name**, so
  SynchroWave can use that measured voltage to compute power on lines that reference the bus
  as From/To (when the line's own voltage is unavailable).
- **Transformers** → modeled as a **line between the two bus voltage levels** (same
  substation, different kV).
- **Generators** → a **line with only one of From/To filled** (single-ended).

See §7 "Anchoring non-line measurements" for the implementation.

---

## 6. Deriving the model: the measured-terminal approach

The unit of work is the **measured terminal**, not the "line." Each PMU device is one
terminal; each distinct DFR `Label` is one terminal. Framing the derivation this way keeps
each terminal's MP and bus together by construction and removes any reliance on an
arbitrary From/To side.

For every measured terminal:

1. **Local station** = device's station (GPS group). **kV** = resolve via the terminal's
   I-phasor `DestinationPhasorID` → V-phasor `BaseKV` (`ResolveVoltageKV`).
2. **Local bus** = the station's bus at that kV — **generated if absent** (`EnsureBus`); it
   is notional, and SEL endorses generating it. This guarantees the MP always has a
   same-side bus.
3. **Terminal MP** = the terminal's MP from the signal mappings (the line **current** MP).
4. **Remote** = the parsed counterpart (PMU `Name` remote token; DFR `Label`).
5. Emit one line row, **measured terminal always on one fixed side** (e.g. always `From`):
   - `FromTerminalMP = MP`, `FromBusId = LOCAL bus` (never split — same side, always).
   - `ToTerminalMP = ` (blank — we do not measure there), and:
     - if **remote is a distinct real station** (≠ local station): `ToBusId = REMOTE bus`
       as an anchor (generated notionally if the remote station exists in the model);
     - if remote resolves to the **local station** or to a generator/transformer/unit:
       leave the To side **blank** → single-terminal asset, not a self-loop.
6. **Dedupe by MP** so each MP appears once; if two terminals would emit the same MP they
   are the same terminal — keep one canonical line, drop the duplicate.
7. **Two-ended lines:** if a second terminal with its own MP is found for the same physical
   line, populate the To side with *that* terminal's MP **and its own bus** — each side
   independently keeps MP+bus together.
8. **`LineId`** is a stable, alphabetically-built id (`MakeUniqueLineID` keeps it unique),
   but **From/To side is not derived from it** — side is "measured terminal vs remote
   anchor."

### Pipeline (`PowerSystemModelExporter.Export`)

The model exporter runs **after** STTP signal-mapping generation and receives the mappings
in memory (keyed on the database's clean `Phasor.Label`), so there is no CSV re-read or
free-text description parsing.

1. `LoadDeviceRecords` — enabled, non-concentrator devices with GPS + their phasors; builds
   the `id → PhasorRecord` map for `DestinationPhasorID` resolution.
2. `BuildTerminalsAndDFRLines` — from the signal mappings, pick each device's **terminal
   MP** and extract DFR line names from the phasor labels.
3. `DeriveStations` — group devices by GPS (rounded), extract a station name, nominal
   kV = max resolved kV. Devices/groups with no name or no resolvable voltage are skipped
   and **named** in the run report.
4. `CollectBusMeasurementPoints` + `BuildCanonicalBusMap` — gather bus-voltage MPs and, where
   a station+voltage has exactly one, make that MP the **canonical** bus name so lines
   reference the measured bus.
5. `DeriveBuses` — one bus per station + distinct voltage level (canonical name when known,
   otherwise notional).
6. `DeriveLines` — the measured-terminal model above for PMU devices, DFR labels, and
   transformers (`DeriveTransformerLines`); pairs each terminal MP with its own bus via
   `EnsureBus`/`EnsureNamedBus`/`ResolveTerminalBus`, matches remotes via `FindKnownStation`,
   and dedupes by MP.
7. `AddBusMeasurementPointRows` + `PruneUnreferencedNotionalBuses` — emit a bus per bus-MP,
   then drop notional buses left unreferenced once lines bind to their specific measured
   buses.
8. `ReconcileStationVoltages` — set each station's `NominalVoltageKV` to the max of its buses.
9. `ComputeAdjacentBuses` — adjacency from line endpoints.
10. Diagnostics — `AnalyzeOrphanMeasurementPoints`, `CheckModelInvariants`, `FindPossibleTypos`,
    `AnalyzePowerCalcCoverage` (see §8–§9). The run prints all results.

Invariants asserted before writing `lines.csv` (`CheckModelInvariants`; no row that violates
a rule is written): `FromBusId != ToBusId`; `station(FromBusId) != station(ToBusId)` when
both set; a populated MP implies a same-side bus; each MP used ≤ once; no `BusId` equals any
MP. The `stations`/`buses` outputs are then the **transitive closure** of buses actually
referenced (every referenced bus exists; every station has ≥1 bus; station
`NominalVoltageKV` = max of its buses; stations with no surviving bus are dropped).

---

## 7. Anchoring non-line measurements (orphan handling)

Measurement points that are not line currents still need a place in the model so their data
is usable. Per SEL guidance, the exporter anchors them as follows:

- **Bus-voltage MPs → a bus named after the MP.** `CollectBusMeasurementPoints` gathers DFR
  bus labels (e.g. `EAST_BUS`, `N_BUS`) with their station/kV; each becomes a `buses.csv`
  row whose `BusId` equals the MP. Where a station+voltage has exactly one bus MP
  (`BuildCanonicalBusMap`), that name becomes the **canonical** bus so line terminals
  reference the measured bus (`CanonicalBusID`/`EnsureBus`), enabling SynchroWave's power
  calc to draw voltage from it. Stations with several buses at one voltage keep the notional
  bus for line anchoring and emit each bus MP as its own anchored bus.
- **Transformers → a same-substation, two-voltage line.** `DeriveTransformerLines` pairs a
  device's high-side (`_HS`) and low-side (`_LS`) windings of the same transformer (distinct
  kV) into one line between the two voltage buses. The same-substation invariant is relaxed
  to allow different-voltage buses (only identical From/To buses are rejected). Pairing is
  restricted to the **same device** because, in practice, HS/LS windings are frequently
  split across devices with inconsistent naming (sometimes at equal kV, occasionally with a
  malformed `BaseKV`) — so cross-device pairing would risk wrong topology. Unpaired
  transformer windings are anchored as single-ended lines.
- **Generators → single-ended lines.** A PMU/inverter device whose name yields no remote
  becomes a line with only the From terminal filled.

`AnalyzeOrphanMeasurementPoints` **reports** (does not fabricate) any measurement point left
anchored to neither a line terminal nor a bus, and **classifies** each as either:
- a *redundant duplicate* — a point that was a real terminal candidate but lost to another
  point at the same already-modeled terminal (tracked via the set of all terminal candidates
  returned by `DeriveLines`); or
- a *genuine gap* — a complete **voltage + current** terminal that was never modelable.

A V+I measurement point absent from the lines file is the sharp signal of a real gap,
because a point carrying both quantities is a complete line/transformer terminal. Genuine
gaps are listed by name in the run output for a follow-up modeling decision rather than being
turned into speculative lines.

---

## 8. Pairing line currents to the correct bus voltage (`DestinationPhasorID`)

SEL computes a terminal's power from its **current** and a **voltage**; the voltage may come
from the line measurement point itself or the **adjacent bus**. `Phasor.DestinationPhasorID`
authoritatively pairs each current phasor with its voltage phasor, so when that voltage is a
bus, the line terminal must reference *that specific bus*. A naive (station, kV) heuristic
cannot do this at a substation with several buses at one voltage — each feeder's current
pairs (via `DestinationPhasorID`) with a specific one, and emitting a single notional bus
would leave SEL no voltage to compute power.

`BuildDeviceLineVoltagePairing` reads the phasor graph per device: current label →
(resolved kV via its own `DestinationPhasorID`, paired-bus measurement point). DFR line and
transformer terminals take their kV from that pairing and, when the paired voltage is a bus,
set their `From/To BusId` to that bus's measurement point (`ResolveTerminalBus`/
`EnsureNamedBus`). PMU points carry their own voltage and keep the canonical bus. Notional
buses left unreferenced once feeders bind to specific measured buses are pruned
(`PruneUnreferencedNotionalBuses`).

**Lines and buses are identified from `Phasor.Label`, not the free-text description.** The
clean, database-authoritative `Phasor.Label` is threaded end to end:
`SttpConfigExporter.Export` returns its per-signal mappings —
`SignalMapping(DeviceAcronym, Description, MeasurementPoint, Quantity, SignalID, PhasorLabel)`
— and `Program` hands them to `PowerSystemModelExporter.Export`. The model exporter builds
its terminal points, DFR line/bus lists, and measurement-point info from those mappings
(`BuildTerminalsAndDFRLines`, `BuildMeasurementPointInfo`), keyed on the clean `PhasorLabel`.
There is **no CSV re-read and no `(Device, Description)` join** — `Description` is free text
and only *effectively* unique; even `PointTag` is not guaranteed unique, only `SignalID` is.
Only phasor measurements define lines/buses; label-less calculated-power rows are skipped so
they cannot mis-parse into spurious lines.

### Voltage sources a terminal's current can pair with

The voltage a terminal's current pairs with (per `DestinationPhasorID`) is one of:
1. **its own measurement point** (a V+I terminal);
2. a measured **bus** point (a `BUS`-labeled voltage); or
3. a **co-bus line voltage** — at many EHV (e.g. 500/230 kV) substations the bus voltage is
   sensed through the *line* VTs, so the voltage points are labeled by the remote line name,
   not "BUS." The transformer/generator terminals on that bus then draw the bus voltage from
   a sibling line terminal that shares the same bus.

Because the model already co-locates the transformer/generator terminals on the same bus as
those line terminals, SEL has a voltage for every terminal. `AnalyzePowerCalcCoverage`
therefore treats a bus as voltage-bearing when it is a measured voltage point **or** any line
terminal on it carries voltage, and reports terminals with no usable voltage source.

> This relies on SEL using a line terminal's voltage as the shared adjacent-bus voltage for
> other terminals on that bus (the IM's "voltage … on the adjacent Bus to the Line Asset").
> Worth a one-line confirmation with SEL, since at these substations the source senses the
> bus voltage only through line VTs, with no dedicated bus VT.

---

## 9. Diagnostics emitted each run

The exporter surfaces (rather than silently swallows) the conditions that need human
attention:

- **Model invariants** (`CheckModelInvariants`) — validates the output against every rule in
  §5; no `lines.csv` row that violates a rule is written.
- **Orphan classification** (`AnalyzeOrphanMeasurementPoints`) — unanchored MPs split into
  *redundant duplicates* vs *genuine gaps* (V+I terminals never modeled), with genuine gaps
  listed by name (see §7).
- **Power-calc coverage** (`AnalyzePowerCalcCoverage`) — line terminals whose current has no
  usable voltage source (see §8).
- **Skipped stations/devices** (`DeriveStations`) — coordinate groups skipped for no name or
  no resolvable voltage, reported by name so the root cause is visible.
- **Possible source-data typos** (`FindPossibleTypos`) — distinct spellings of one substation
  in the source data silently split a physical line across two `LineId`s. The exporter does
  **not** auto-merge near-spellings (that risks fusing genuinely distinct stations); instead
  it reports, per run, any unresolved remote endpoint within one character-edit of a known
  station (and any two stations within one edit, via `WithinOneEdit`), so the source data can
  be corrected. Fixing the one misspelled acronym/label in the source collapses the split
  pair into a single correct line on the next run.

---

## 10. Appendix — quick reference

- **Signal** = `MeasurementPoint.Quantity`; both required to publish.
- **Bus** = notional voltage node; carries the **voltage** half of a terminal's power calc;
  freely generable; invisible in UI.
- **Line terminal MP** = the **current** half; pairs with its own bus on the **same** side.
- Source truth for endpoints: PMU `Device.Name` (`LOCAL-REMOTE kV`), DFR `Phasor.Label`
  (remote per terminal), I→V pairing via `Phasor.DestinationPhasorID`.
- Modeling reality: typically **one measured end per line**; From/To is otherwise arbitrary,
  so the derivation fixes the measured terminal on one side and treats the remote as a
  bus-only anchor.
- The model exporter consumes the STTP signal mappings in memory and keys everything on the
  clean `Phasor.Label`; it must run **after** signal-mapping generation (it already does).
