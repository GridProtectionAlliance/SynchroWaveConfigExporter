# SynchroWave STTP Configuration Exporter

A .NET console application that exports synchrophasor data configurations from Grid Protection Alliance (GPA) applications (such as openHistorian, openPDC, or SIEGate) to formats compatible with SEL SynchroWave visualization system.

## Overview

The SynchroWave STTP Configuration Exporter generates multiple CSV export files for use with SEL's SynchroWave Central visualization platform:

1. **STTP Signal Mappings** - Maps synchrophasor measurements to SEL SynchroWave Central signal identifiers for the IEEE 2664 data transport standard. This allows SEL SynchroWave Central to automatically injest all available measurements without manual configuration.
2. **Power System Model** - Exports station, bus, and line topology information.
3. **Dash Menu** - Creates hierarchical folder structures for organizing visualizations, e.g., by station and voltage level.

The exporter connects to an existing GPA application database (e.g., openHistorian) and automatically extracts device configurations, phasor definitions, and measurement metadata.

## Features

- **Automatic Database Discovery** - Reads configuration from installed GPA service registry keys
- **STTP Signal Mapping** - Generates `MeasurementPoint` identifiers compatible with SEL SynchroWave Central using intelligent character reduction and naming convention parsing to meet the 16-character limit
- **Power System Topology** - Extracts station locations, bus voltages, and transmission line connections
- **Dash Menu Generation** - Creates hierarchical visualization folder structures
- **Naming Convention Parsing** - Extracts station names, line names, and voltage levels from device-centic metadata
- **Alternate Tag Management** - Optionally persists generated mappings back to the source database for consitent future runs

## Requirements

- Access to an installed GPA application (openHistorian, openPDC, or SIEGate)
- Database access permissions to the GPA application's configuration database

## Build Instructions

### Prerequisites

- [.NET 9.0 SDK](https://dotnet.microsoft.com/download/dotnet/9.0) or later

### Building from Source

```bash
# Clone the repository
git clone https://github.com/GridProtectionAlliance/SynchroWaveConfigExporter.git
cd SynchroWaveConfigExporter

# Build the project
dotnet build -c Release

# Publish as a self-contained executable (optional)
dotnet publish -c Release -r win-x64 --self-contained
```

### Output Location

- **Build**: `bin/Release/net9.0/`
- **Publish**: `bin/Release/net9.0/win-x64/publish/`

## Installation

### Option 1: Run from Build Directory

```bash
cd bin/Release/net9.0
SynchroWaveConfigExporter.exe
```

### Option 2: Deploy Published Executable

Copy the contents of the `publish` directory to your desired installation location and run `SynchroWaveConfigExporter.exe`.

## Usage

### Basic Execution

Simply run the executable from a command prompt or PowerShell:

```bash
SynchroWaveConfigExporter.exe
```

The application will:
1. Locate the configured GPA host service via Windows registry
2. Read the database connection settings from the service's configuration file
3. Export all configured data to CSV files in the current directory
4. Display export statistics and any warnings

### Example Output

```
SynchroWave STTP Configuration Exporter
=======================================

Connected to database: openHistorian

Exporting SEL STTP Signal Mappings...
  Total measurements loaded: 45,123
  Measurements exported: 42,567
  Alternate tags generated: 42,567
  Invalid alternate tags (too long): 23

Exporting Power System Model...
  Total devices analyzed: 234
  Devices with phasors: 198
  Total phasors loaded: 1,234
  Coordinate groups found: 87
  DFR devices found: 45
  Line terminal, PMU devices found: 153
  Stations exported: 87
  Buses exported: 156
  Lines exported: 234

Exporting Dash Menu...
  Stations loaded: 87
  Buses loaded: 156
  Lines loaded: 234
  Substation paths: 87
  Voltage level groups: 4
  Line paths (by voltage): 234
  Total paths exported: 325

Export Complete!

Output Files:
  SEL Signal Mappings: sel-sttpreader-signalmappings.csv
  Stations: sel-powersystemmodel_stations.csv
  Buses: sel-powersystemmodel_buses.csv
  Lines: sel-powersystemmodel_lines.csv
  Dash Menu: dash-menu.txt
```

## Configuration

Configuration settings are stored in `defaults.ini` / `settings.ini` in the application directory. The settings files are automatically created on first run with default values in the `defaults.ini` file. You can modify these settings in `settings.ini` to customize the export behavior.`

### Available Settings

| Setting | Default Value | Description |
|---------|---------------|-------------|
| `HostService` | `openHistorian` | Name of the GPA host service to load configuration from (e.g., `openPDC`, `openHistorian`, `SIEGate`) |
| `DefaultInstallPath` | `C:\Program Files\openHistorian\` | Default installation path if registry lookup fails |
| `SttpSelConfigCsvPath` | `sel-sttpreader-signalmappings.csv` | Output path for STTP signal mappings CSV |
| `StationsCsvPath` | `sel-powersystemmodel_stations.csv` | Output path for stations CSV |
| `BusesCsvPath` | `sel-powersystemmodel_buses.csv` | Output path for buses CSV |
| `LinesCsvPath` | `sel-powersystemmodel_lines.csv` | Output path for lines CSV |
| `DashMenuPath` | `dash-menu.txt` | Output path for dash menu file |
| `PersistAlternateTags` | `false` | Whether to write `MeasurementPoint` mappings back to the `AlternateTag` field in the database |
| `ExcludedPrefixes` | `ETR, EES, ESI` | Comma-separated device prefixes to exclude from signal mappings |
| `MapPowerQuantities` | `false` | Whether to include power (MW, MVAR, MVA) calculations in signal mappings |
| `IncludeVoltageGroupedLines` | `true` | Whether to include voltage-level grouped lines in the dash menu |

### Example Configuration File

```ini
[System]
; Power system model buses CSV output path
BusesCsvPath=sel-powersystemmodel_buses.csv

; Dash menu file output path
DashMenuPath=dash-menu.txt

; Default installation path for the host service
DefaultInstallPath=C:\Program Files\openHistorian\

; Other prefixes to be excluded from 'MeasurementPoint' mappings
ExcludedPrefixes=[string[]]:ETR;EES;ESI

; Name of the host service to load configuration from, e.g., 'openPDC', 'openHistorian', or 'SIEGate'
HostService=openHistorian

; Indicates whether to include voltage-level grouped lines in the dash menu
IncludeVoltageGroupedLines=[bool]:True

; Power system model lines CSV output path
LinesCsvPath=sel-powersystemmodel_lines.csv

; Indicates whether power quantities should be mapped to 'MeasurementPoint' mappings
MapPowerQuantities=[bool]:False

; Indicates whether 'MeasurementPoint' mappings should be persisted to 'AlternateTag' field
PersistAlternateTags=[bool]:False

; Power system model stations CSV output path
StationsCsvPath=sel-powersystemmodel_stations.csv

; STTP SEL configuration CSV output path
SttpSelConfigCsvPath=sel-sttpreader-signalmappings.csv
```

## Customization for Different Utilities

The exporter includes utility-specific naming convention logic in `DeviceHelper.cs`. The default implementation includes patterns specific to Entergy's synchrophasor naming conventions.

### Key Methods to Customize

If you need to adapt this tool for a different utility's naming conventions, focus on the following methods in `DeviceHelper.cs`:

#### 1. Device Type Identification

```csharp
/// <summary>
/// Checks if a device is a PMU (line-terminal) device based on naming convention.
/// </summary>
public static bool IsPMUDevice(string? device)
```

**Default Pattern:** `STATION_P_NNN4` (where NNN = 3 letters, 4 = 1 digit)

**Customize for:** Your utility's PMU device naming pattern

---

```csharp
/// <summary>
/// Checks if a device is a DFR (Digital Fault Recorder) device.
/// </summary>
public static bool IsDFRDevice(string? device)
```

**Default Pattern:** Contains `_D_`

**Customize for:** Your utility's DFR device naming pattern

---

```csharp
/// <summary>
/// Determines whether the specified token is a non-name marker.
/// </summary>
public static bool IsNonNameMarker(string token)
```

**Default Markers:** `D`, `P`, `EPN`, `EPI`, `ENN`, `ENI`, `NPN`, `NPI`, `NNN`, `NNI`

**Customize for:** Your utility's device type codes and regional identifiers

#### 2. Station Name Extraction

```csharp
/// <summary>
/// Extracts the PMU base name (station name) from a device acronym.
/// </summary>
public static string? ExtractPMUBaseName(string? device)
```

**Default Logic:** Strips everything after `_P_` marker

**Customize for:** How your utility encodes station names in PMU device acronyms

---

```csharp
/// <summary>
/// Extracts station name from DFR device acronym.
/// </summary>
public static string? ExtractStationFromDFRAcronym(string? acronym)
```

**Default Pattern:** `STATION_UNITNUM_D_XXX` → extracts `STATION`

**Customize for:** Your utility's DFR naming convention

---

```csharp
/// <summary>
/// Parses "STATION-REMOTE {KV}KV" from the device Name field.
/// </summary>
public static string? ExtractStationFromName(string? name)
```

**Default Pattern:** `STATION-REMOTE` or `STATION - REMOTE`

**Customize for:** Your utility's device name format

#### 3. Line Name Extraction

```csharp
/// <summary>
/// Extracts the canonical line name from a phasor label or description.
/// </summary>
public static string? ExtractCanonicalLineName(string? phasorLabel, 
    string? description, string? signalType)
```

**Default Logic:** 
- Uses `PhasorLabel` for phasor measurements
- Parses description for calculated values
- Strips phase suffixes (`_IA`, `_VA`, etc.)

**Customize for:** How your utility names phasors and calculates power values

---

```csharp
/// <summary>
/// Extracts a line/phasor name from the description field.
/// </summary>
public static string? ExtractLineNameFromDescription(string? desc)
```

**Default Patterns:**
- `DEVICE-MW_X-LINENAME ...` (power calculations)
- `DEVICE LINENAME Calculated Value: 3-Phase...`
- `DEVICE LINENAME X Signal Type`

**Customize for:** Your utility's measurement description formats

#### 4. Line Parsing

```csharp
/// <summary>
/// Parses a line-terminal device to extract from-station, to-remote, and voltage.
/// </summary>
public static LineParse? ParseLineFromDeviceName(string? deviceName, 
    int fallbackKV = 0)
```

**Default Pattern:** `STATION-REMOTE {KV}KV` or `STATION - REMOTE {KV}KV`

**Customize for:** How your utility encodes line endpoints and voltage in device names

### Customization Workflow

1. **Analyze Your Data:**
   ```bash
   # Run the exporter and review sample device acronyms/names in the output
   SynchroWaveConfigExporter.exe
   ```

2. **Identify Patterns:**
   - Look at the "Sample device acronyms" section
   - Look at the "Sample device names" section
   - Note your utility's naming conventions

3. **Update `DeviceHelper.cs`:**
   - Modify the methods listed above to match your patterns
   - Add comments documenting your utility's conventions
   - Test incrementally

4. **Test Export:**
   ```bash
   # Re-run and verify the exported CSV files contain correct data
   SynchroWaveConfigExporter.exe
   ```

5. **Validate Results:**
   - Check that stations are correctly identified
   - Verify line names are properly extracted
   - Ensure voltage levels are accurate

### Example Customization

If your utility uses a pattern like `SUBSTATION_NAME_PMU_001` for PMU devices instead of `STATION_P_NNN4`:

```csharp
public static bool IsPMUDevice(string? device)
{
    if (string.IsNullOrWhiteSpace(device))
        return false;

    // Look for _PMU_ pattern followed by 3 digits
    int index = device.IndexOf("_PMU_", StringComparison.OrdinalIgnoreCase);

    if (index < 0)
        return false;

    int suffixStart = index + 5; // Position after "_PMU_"

    if (suffixStart + 3 != device.Length)
        return false;

    string suffix = device[suffixStart..];

    // Must be exactly 3 digits
    return suffix.Length == 3 &&
           char.IsDigit(suffix[0]) &&
           char.IsDigit(suffix[1]) &&
           char.IsDigit(suffix[2]);
}

public static string? ExtractPMUBaseName(string? device)
{
    if (string.IsNullOrWhiteSpace(device))
        return null;

    int index = device.IndexOf("_PMU_", StringComparison.OrdinalIgnoreCase);

    return index <= 0 ? null : device[..index];
}
```

## Troubleshooting

### No Stations Exported

**Possible Causes:**
- No devices have GPS coordinates defined
- Device naming doesn't match expected patterns
- No phasors have valid `BaseKV` values

**Solutions:**
1. Check device coordinates in the GPA configuration
2. Review sample device acronyms in the output
3. Customize `DeviceHelper.cs` for your naming conventions

### No Lines Exported

**Possible Causes:**
- Line terminal device names don't match expected format
- Missing or invalid `Name` field in device metadata

**Solutions:**
1. Verify device `Name` field format: `STATION-REMOTE {KV}KV`
2. Customize `ParseLineFromDeviceName()` in `DeviceHelper.cs`

### Database Connection Failed

**Possible Causes:**
- GPA service not installed on this machine
- Registry key not found
- Configuration file not accessible

**Solutions:**
1. Verify `HostService` setting matches your installed service
2. Check `DefaultInstallPath` points to correct location
3. Ensure the application runs with sufficient permissions

## Version History

- **v1.0.0** (January 2026) - Initial release
  - STTP signal mapping export
  - Power system model export (stations, buses, lines)
  - Dash menu generation
  - Entergy naming convention support
