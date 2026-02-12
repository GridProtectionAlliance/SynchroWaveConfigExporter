//******************************************************************************************************
//  PowerSystemModelExporter.cs - Gbtc
//
//  Copyright © 2026, Grid Protection Alliance.  All Rights Reserved.
//
//  Licensed to the Grid Protection Alliance (GPA) under one or more contributor license agreements. See
//  the NOTICE file distributed with this work for additional information regarding copyright ownership.
//  The GPA licenses this file to you under the MIT License (MIT), the "License"; you may not use this
//  file except in compliance with the License. You may obtain a copy of the License at:
//
//      http://opensource.org/licenses/MIT
//
//  Unless agreed to in writing, the subject software distributed under the License is distributed on an
//  "AS-IS" BASIS, WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied. Refer to the
//  License for the specific language governing permissions and limitations.
//
//  Code Modification History:
//  ----------------------------------------------------------------------------------------------------
//  02/10/2026 - J. Ritchie Carroll
//       Generated original version of source code.
//
//******************************************************************************************************
// ReSharper disable NotAccessedPositionalProperty.Local

namespace SynchroWaveConfigExporter;

/// <summary>
/// Derives SynchroWave power system model CSV configurations (stations, buses, lines)
/// from the openHistorian device-centric database schema.
/// </summary>
/// <remarks>
/// <para>
/// The openHistorian database is device-centric: it knows about PMU/DFR devices, their
/// phasor measurements, and GPS coordinates, but has no explicit concept of "station",
/// "bus", or "transmission line". This exporter infers the power system topology by:
/// </para>
/// <list type="number">
///   <item>Grouping devices by GPS coordinates to derive stations.</item>
///   <item>Collecting distinct voltage levels per station to derive buses. For current
///         phasors, Phasor.DestinationPhasorID is followed to the associated voltage
///         phasor to resolve the correct BaseKV.</item>
///   <item>Parsing line-terminal PMU device names (format: "STATION-REMOTE {KV}KV")
///         to derive transmission lines between buses.</item>
///   <item>For DFR devices, extracting line names from phasor labels in the signal
///         mappings CSV to derive additional lines.</item>
///   <item>Cross-referencing the existing SEL signal mappings CSV to resolve terminal
///         measurement point identifiers for each line endpoint.</item>
/// </list>
/// </remarks>
public static class PowerSystemModelExporter
{
    // ========= Public API =========

    /// <summary>
    /// Exports SynchroWave power system model CSV files (stations, buses, lines) derived
    /// from the openHistorian database.
    /// </summary>
    /// <param name="connection">Open database connection to the openHistorian instance.</param>
    /// <returns>Summary of export results including counts of derived entities.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="connection"/> is <c>null</c>.</exception>
    /// <exception cref="ArgumentException">Thrown when any of the required CSV output paths in <see cref="Settings"/> are null or empty.</exception>
    public static ModelExportResult Export(DbConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentException.ThrowIfNullOrWhiteSpace(Settings.StationsCsvPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(Settings.BusesCsvPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(Settings.LinesCsvPath);

        // 1) Load raw device + phasor data from DB
        (List<DeviceRecord> devices, Dictionary<int, PhasorRecord> idPhasorMap) = LoadDeviceRecords(connection);

        // Collect sample data for diagnostics
        List<string> sampleAcronyms = devices.Take(10).Select(device => device.Acronym).ToList();
        List<string> sampleNames = devices.Take(10).Select(device => device.Name).ToList();
        List<int> sampleBaseKVs = idPhasorMap.Values.Take(10).Select(phasor => phasor.BaseKV).Distinct().ToList();

        // 2) Load existing signal mappings for terminal measurement point lookup and DFR line extraction
        (Dictionary<string, string> terminalMPs, Dictionary<string, List<DFRLineInfo>> deviceDFRLinesMap) = LoadTerminalMeasurementPointsAndDFRLines();

        // 3) Derive stations by grouping devices on GPS coordinates
        (List<StationRow> stations, int coordGroupsFound, int skippedNoName, int skippedNoVoltage) = DeriveStations(devices, idPhasorMap);

        // Build lookup: StationId => StationRow
        Dictionary<string, StationRow> idStationMap = stations.ToDictionary(
            station => station.StationID, station => station, StringComparer.OrdinalIgnoreCase);

        // 4) Derive buses: station + distinct voltage levels
        List<BusRow> buses = DeriveBuses(devices, idStationMap, idPhasorMap);

        // Build lookup: BusId => BusRow
        Dictionary<string, BusRow> idBusMap = buses.ToDictionary(
            bus => bus.BusID, bus => bus, StringComparer.OrdinalIgnoreCase);

        // 5) Derive lines from line-terminal, PMU-style, devices
        List<LineRow> lines = DeriveLines(devices, idStationMap, idBusMap, terminalMPs, idPhasorMap);

        // 6) Derive additional lines from DFR style devices using phasor labels
        int dfrLinesAdded = DeriveDFRLines(devices, idStationMap, idBusMap, terminalMPs, idPhasorMap, deviceDFRLinesMap, lines);

        // 7) Compute adjacent bus IDs from line connections
        ComputeAdjacentBuses(buses, lines);

        // 8) Write output CSVs
        string stationsPath = Settings.StationsCsvPath;
        string busesPath = Settings.BusesCsvPath;
        string linesPath = Settings.LinesCsvPath;

        WriteStationsCSV(stationsPath, stations);
        WriteBusesCSV(busesPath, buses);
        WriteLinesCSV(linesPath, lines);

        return new ModelExportResult(
            StationsExported: stations.Count,
            BusesExported: buses.Count,
            LinesExported: lines.Count,
            TotalDevicesAnalyzed: devices.Count,
            DevicesWithPhasors: devices.Count(device => device.Phasors.Count > 0),
            TotalPhasorsLoaded: idPhasorMap.Count,
            CoordinateGroupsFound: coordGroupsFound,
            LineTerminalDevicesFound: devices.Count(device => IsPMUDevice(device.Acronym)),
            DFRDevicesFound: devices.Count(device => IsDFRDevice(device.Acronym)),
            DFRLinesAdded: dfrLinesAdded,
            StationsSkippedNoName: skippedNoName,
            StationsSkippedNoVoltage: skippedNoVoltage,
            StationsPath: stationsPath,
            BusesPath: busesPath,
            LinesPath: linesPath,
            SampleDeviceAcronyms: sampleAcronyms,
            SampleDeviceNames: sampleNames,
            SamplePhasorBaseKVs: sampleBaseKVs
        );
    }

    // ========= Result types =========

    /// <summary>
    /// Represents the results of a power system model export operation.
    /// </summary>
    /// <param name="StationsExported">The number of station records exported to the stations CSV file.</param>
    /// <param name="BusesExported">The number of bus records exported to the buses CSV file.</param>
    /// <param name="LinesExported">The number of transmission line records exported to the lines CSV file.</param>
    /// <param name="TotalDevicesAnalyzed">The total number of devices analyzed from the database.</param>
    /// <param name="DevicesWithPhasors">The number of devices that have at least one phasor measurement.</param>
    /// <param name="TotalPhasorsLoaded">The total number of phasor records loaded from the database.</param>
    /// <param name="CoordinateGroupsFound">The number of distinct GPS coordinate groups found (before filtering).</param>
    /// <param name="LineTerminalDevicesFound">The number of line-terminal, PMU-style devices found.</param>
    /// <param name="DFRDevicesFound">The number of DFR-style devices found.</param>
    /// <param name="DFRLinesAdded">The number of additional transmission lines derived from DFR devices.</param>
    /// <param name="StationsSkippedNoName">The number of potential stations skipped due to inability to extract a valid station name.</param>
    /// <param name="StationsSkippedNoVoltage">The number of potential stations skipped due to no valid voltage level being found.</param>
    /// <param name="StationsPath">The file path where the stations CSV file was written.</param>
    /// <param name="BusesPath">The file path where the buses CSV file was written.</param>
    /// <param name="LinesPath">The file path where the lines CSV file was written.</param>
    /// <param name="SampleDeviceAcronyms">A sample list of device acronyms (up to 10) for diagnostic purposes.</param>
    /// <param name="SampleDeviceNames">A sample list of device names (up to 10) for diagnostic purposes.</param>
    /// <param name="SamplePhasorBaseKVs">A sample list of distinct phasor base voltage levels (up to 10) for diagnostic purposes.</param>
    public sealed record ModelExportResult(
        int StationsExported,
        int BusesExported,
        int LinesExported,
        int TotalDevicesAnalyzed,
        int DevicesWithPhasors,
        int TotalPhasorsLoaded,
        int CoordinateGroupsFound,
        int LineTerminalDevicesFound,
        int DFRDevicesFound,
        int DFRLinesAdded,
        int StationsSkippedNoName,
        int StationsSkippedNoVoltage,
        string StationsPath,
        string BusesPath,
        string LinesPath,
        List<string> SampleDeviceAcronyms,
        List<string> SampleDeviceNames,
        List<int> SamplePhasorBaseKVs);

    // ========= Data models =========

    /// <summary>
    /// Represents a device record loaded from the openHistorian database, including its
    /// associated phasor measurements.
    /// </summary>
    /// <param name="ID">The unique device identifier from the database.</param>
    /// <param name="Acronym">The device acronym (typically includes station name and device type markers).</param>
    /// <param name="Name">The device name (typically in format "STATION-REMOTE {KV}KV" for line-terminal devices).</param>
    /// <param name="Latitude">The GPS latitude coordinate of the device location.</param>
    /// <param name="Longitude">The GPS longitude coordinate of the device location.</param>
    /// <param name="IsConcentrator">Indicates whether this device is a concentrator (aggregates child devices).</param>
    /// <param name="ParentID">The ID of the parent device if this is a child device; otherwise, <c>null</c>.</param>
    /// <param name="Phasors">The list of phasor measurements associated with this device.</param>
    private sealed record DeviceRecord(
        int ID,
        string Acronym,
        string Name,
        decimal Latitude,
        decimal Longitude,
        bool IsConcentrator,
        int? ParentID,
        List<PhasorRecord> Phasors);

    /// <summary>
    /// Represents a phasor measurement record loaded from the openHistorian database.
    /// </summary>
    /// <param name="ID">The unique phasor identifier from the database.</param>
    /// <param name="DeviceID">The ID of the device this phasor belongs to.</param>
    /// <param name="Label">The phasor label (typically identifies the line or bus name).</param>
    /// <param name="Type">The phasor type: 'V' for voltage or 'I' for current.</param>
    /// <param name="Phase">The phase identifier (e.g., 'A', 'B', 'C', '+', '-', '0').</param>
    /// <param name="BaseKV">The base voltage level in kV for this phasor.</param>
    /// <param name="DestinationPhasorID">For current phasors, the ID of the associated voltage phasor that defines the voltage level; otherwise, <c>null</c>.</param>
    /// <remarks>
    /// For current phasors (Type='I'), the DestinationPhasorID is used to resolve the actual voltage level
    /// by following the reference to the associated voltage phasor.
    /// </remarks>
    private sealed record PhasorRecord(
        int ID,
        int DeviceID,
        string Label,
        char Type,    // 'V' or 'I'
        char Phase,
        int BaseKV,
        int? DestinationPhasorID);

    /// <summary>
    /// Represents a physical station derived from device GPS coordinates and phasor data.
    /// </summary>
    /// <remarks>
    /// Stations are derived by grouping devices with the same GPS coordinates (rounded to
    /// ~1.1km precision) and extracting station names from device acronyms or names.
    /// </remarks>
    private sealed class StationRow
    {
        /// <summary>
        /// Gets the unique station identifier (normalized from station name).
        /// </summary>
        public required string StationID { get; init; }
        
        /// <summary>
        /// Gets the GPS latitude coordinate of the station.
        /// </summary>
        public required decimal Latitude { get; init; }
        
        /// <summary>
        /// Gets the GPS longitude coordinate of the station.
        /// </summary>
        public required decimal Longitude { get; init; }
        
        /// <summary>
        /// Gets the nominal voltage level in kV (the maximum voltage level found at this station).
        /// </summary>
        public required int NominalVoltageKV { get; init; }
    }

    /// <summary>
    /// Represents an electrical bus at a specific voltage level within a station.
    /// </summary>
    /// <remarks>
    /// Buses are derived from stations by enumerating all distinct voltage levels found
    /// at each station. A station may have multiple buses at different voltage levels.
    /// </remarks>
    private sealed class BusRow
    {
        /// <summary>
        /// Gets the unique bus identifier in format "{StationID}_{NominalVoltageKV}_BUS".
        /// </summary>
        public required string BusID { get; init; }
        
        /// <summary>
        /// Gets the station identifier this bus belongs to.
        /// </summary>
        public required string StationID { get; init; }
        
        /// <summary>
        /// Gets the nominal voltage level of this bus in kV.
        /// </summary>
        public required int NominalVoltageKV { get; init; }
        
        /// <summary>
        /// Gets or sets a semicolon-separated list of adjacent bus IDs.
        /// Adjacent buses are those connected via transmission lines.
        /// </summary>
        public string AdjacentBusIDs { get; set; } = string.Empty;
    }

    /// <summary>
    /// Represents a transmission line connecting two buses.
    /// </summary>
    /// <param name="LineID">The unique line identifier in format "{StationA}_{StationB}" (alphabetically sorted).</param>
    /// <param name="FromTerminalMP">The terminal measurement point identifier at the 'from' end of the line.</param>
    /// <param name="ToTerminalMP">The terminal measurement point identifier at the 'to' end of the line.</param>
    /// <param name="FromBusID">The bus identifier at the 'from' end of the line.</param>
    /// <param name="ToBusID">The bus identifier at the 'to' end of the line.</param>
    /// <param name="NominalVoltageKV">The nominal voltage level of this transmission line in kV.</param>
    /// <remarks>
    /// Lines are derived from line-terminal PMU devices and DFR devices. The 'from' and 'to'
    /// designations are determined by alphabetical ordering of the station names to ensure
    /// consistent line identifiers regardless of which end is encountered first.
    /// </remarks>
    private sealed record LineRow(
        string LineID,
        string FromTerminalMP,
        string ToTerminalMP,
        string FromBusID,
        string ToBusID,
        int NominalVoltageKV);

    /// <summary>
    /// Tracks the two endpoints of a transmission line during line derivation.
    /// Used internally to aggregate information from devices at both ends of a line.
    /// </summary>
    /// <remarks>
    /// Since a transmission line may have PMU/DFR devices at both endpoints, this class
    /// is used to collect endpoint information from multiple device records before creating
    /// the final <see cref="LineRow"/> record.
    /// </remarks>
    private sealed class LineEndpoints
    {
        /// <summary>
        /// Gets or sets the terminal measurement point identifier at the 'from' end.
        /// </summary>
        public string? FromMP { get; set; }
        
        /// <summary>
        /// Gets or sets the terminal measurement point identifier at the 'to' end.
        /// </summary>
        public string? ToMP { get; set; }
        
        /// <summary>
        /// Gets or sets the bus identifier at the 'from' end.
        /// </summary>
        public string? FromBusID { get; set; }
        
        /// <summary>
        /// Gets or sets the bus identifier at the 'to' end.
        /// </summary>
        public string? ToBusID { get; set; }
        
        /// <summary>
        /// Gets the nominal voltage level of this line in kV.
        /// </summary>
        public int NominalKV { get; init; }
    }

    /// <summary>
    /// Information about a DFR line extracted from the signal mappings CSV.
    /// </summary>
    /// <param name="LineName">The line name extracted from the phasor label or description.</param>
    /// <param name="MeasurementPoint">The terminal measurement point identifier for this line at the DFR location.</param>
    /// <param name="VoltageKV">The voltage level in kV extracted from the description field, or 0 if not found.</param>
    private sealed record DFRLineInfo(
        string LineName,
        string MeasurementPoint,
        int VoltageKV);
    
    // ========= DB loading =========

    /// <summary>
    /// Loads all enabled, non-concentrator devices with valid GPS coordinates and their
    /// associated phasor measurements from the openHistorian database.
    /// </summary>
    /// <param name="connection">The open database connection.</param>
    /// <returns>
    /// A tuple containing:
    /// <list type="bullet">
    /// <item><description>A list of device records with their associated phasors</description></item>
    /// <item><description>A dictionary mapping phasor IDs to phasor records (for DestinationPhasorID resolution)</description></item>
    /// </list>
    /// </returns>
    /// <remarks>
    /// Concentrator devices are excluded as they aggregate child devices rather than representing
    /// physical measurement points. Only devices with valid latitude and longitude coordinates
    /// are included.
    /// </remarks>
    private static (List<DeviceRecord> Devices, Dictionary<int, PhasorRecord> IDPhasorMap) LoadDeviceRecords(DbConnection connection)
    {
        // Load all enabled, non-concentrator devices with valid coordinates
        const string DeviceSQL = """
            SELECT ID, Acronym, ISNULL(Name, Acronym) AS Name, Latitude, Longitude,
                IsConcentrator, ParentID
            FROM Device
            WHERE Enabled <> 0 AND 
                Latitude IS NOT NULL AND
                Longitude IS NOT NULL
            """;

        const string PhasorSQL = """
            SELECT ID, DeviceID, Label, Type, Phase, BaseKV, DestinationPhasorID
            FROM Phasor
            ORDER BY DeviceID, SourceIndex
            """;

        // Load phasors into a dictionary keyed by DeviceID, and a global ID lookup
        Dictionary<int, List<PhasorRecord>> deviceIDPhasorsMap = [];
        Dictionary<int, PhasorRecord> idPhasorMap = [];

        using (DbCommand command = connection.CreateCommand())
        {
            command.CommandText = PhasorSQL;

            using DbDataReader reader = command.ExecuteReader();

            while (reader.Read())
            {
                int deviceID = Convert.ToInt32(reader["DeviceID"]);

                PhasorRecord phasor = new(
                    ID: Convert.ToInt32(reader["ID"]),
                    DeviceID: deviceID,
                    Label: Convert.ToString(reader["Label"], CultureInfo.InvariantCulture) ?? string.Empty,
                    Type: (Convert.ToString(reader["Type"], CultureInfo.InvariantCulture) ?? "V")[0],
                    Phase: (Convert.ToString(reader["Phase"], CultureInfo.InvariantCulture) ?? "+")[0],
                    BaseKV: Convert.ToInt32(reader["BaseKV"]),
                    DestinationPhasorID: reader["DestinationPhasorID"] is DBNull ? null : Convert.ToInt32(reader["DestinationPhasorID"])
                );

                // Index by DeviceID for attaching to DeviceRecords
                if (!deviceIDPhasorsMap.TryGetValue(deviceID, out List<PhasorRecord>? list))
                {
                    list = [];
                    deviceIDPhasorsMap[deviceID] = list;
                }

                list.Add(phasor);

                // Global index by phasor ID for DestinationPhasorID resolution
                idPhasorMap[phasor.ID] = phasor;
            }
        }

        // Load devices
        List<DeviceRecord> devices = [];

        using (DbCommand command = connection.CreateCommand())
        {
            command.CommandText = DeviceSQL;

            using DbDataReader reader = command.ExecuteReader();

            while (reader.Read())
            {
                int id = Convert.ToInt32(reader["ID"]);
                bool isConcentrator = Convert.ToBoolean(reader["IsConcentrator"]);

                // Skip concentrators as they aggregate child devices, not physical stations
                if (isConcentrator)
                    continue;

                int? parentID = reader["ParentID"] is DBNull ? null : Convert.ToInt32(reader["ParentID"]);

                devices.Add(new DeviceRecord(
                    ID: id,
                    Acronym: (Convert.ToString(reader["Acronym"], CultureInfo.InvariantCulture) ?? string.Empty).Trim(),
                    Name: (Convert.ToString(reader["Name"], CultureInfo.InvariantCulture) ?? string.Empty).Trim(),
                    Latitude: Convert.ToDecimal(reader["Latitude"]),
                    Longitude: Convert.ToDecimal(reader["Longitude"]),
                    IsConcentrator: isConcentrator,
                    ParentID: parentID,
                    Phasors: deviceIDPhasorsMap.GetValueOrDefault(id) ?? []
                ));
            }
        }

        return (devices, idPhasorMap);
    }

    // ========= Terminal measurement point lookup =========

    /// <summary>
    /// Loads the existing SEL signal mappings CSV to build:
    /// 1. A lookup from DeviceAcronym to the base MeasurementPoint used for voltage phasor signals.
    /// 2. For DFR devices, a lookup of line names extracted from the Description field.
    /// </summary>
    /// <returns>
    /// A tuple containing:
    /// <list type="bullet">
    /// <item><description>A dictionary mapping device acronyms to terminal measurement point identifiers</description></item>
    /// <item><description>A dictionary mapping DFR device acronyms to lists of line information extracted from descriptions</description></item>
    /// </list>
    /// </returns>
    /// <remarks>
    /// <para>
    /// The terminal measurement point is determined by finding voltage magnitude measurements
    /// in the signal mappings, with preference given to PhaseA.Voltage.Magnitude, then
    /// Phase1.Voltage.Magnitude, then any Voltage.Magnitude measurement.
    /// </para>
    /// <para>
    /// For DFR devices, line names are extracted from the Description field and grouped
    /// by device acronym for later use in line derivation.
    /// </para>
    /// </remarks>
    private static (Dictionary<string, string> TerminalMPs, Dictionary<string, List<DFRLineInfo>> DeviceDFRLinesMap) LoadTerminalMeasurementPointsAndDFRLines()
    {
        Dictionary<string, string> terminalMPs = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, List<DFRLineInfo>> deviceDFRLinesMap = new(StringComparer.OrdinalIgnoreCase);

        string csvPath = Settings.SttpSelConfigCsvPath;

        if (string.IsNullOrWhiteSpace(csvPath) || !File.Exists(csvPath))
            return (terminalMPs, deviceDFRLinesMap);

        // Read signal mappings and find voltage magnitude entries (PhaseA.Voltage.Magnitude
        // preferred, falling back to Phase1.Voltage.Magnitude, then any Voltage.Magnitude)
        // for each device acronym. The MeasurementPoint on those rows is the terminal MP.
        Dictionary<string, List<(string MP, string Quantity, string Description)>> deviceCandidateMap = new(StringComparer.OrdinalIgnoreCase);

        foreach (string rawLine in File.ReadLines(csvPath).Skip(1))
        {
            string line = rawLine.Trim();

            if (line.Length == 0)
                continue;

            string[] fields = ParseCSVLine(line);

            if (fields.Length < 4)
                continue;

            string deviceAcronym = fields[0].Trim();
            string description = fields.Length > 1 ? fields[1].Trim() : string.Empty;
            string measurementPoint = fields[2].Trim();
            string quantity = fields[3].Trim();

            if (string.IsNullOrWhiteSpace(deviceAcronym) || string.IsNullOrWhiteSpace(measurementPoint))
                continue;

            if (!deviceCandidateMap.TryGetValue(deviceAcronym, out List<(string, string, string)>? list))
            {
                list = [];
                deviceCandidateMap[deviceAcronym] = list;
            }

            list.Add((measurementPoint, quantity, description));
        }

        // For each device, pick the best terminal MP and extract DFR line info
        foreach ((string device, List<(string MP, string Quantity, string Description)> candidates) in deviceCandidateMap)
        {
            // Extract terminal MP (voltage-related quantities for terminal identification)
            string? best = candidates
                .Where(candidate => candidate.Quantity.Equals("PhaseA.Voltage.Magnitude", StringComparison.OrdinalIgnoreCase))
                .Select(candidate => candidate.MP)
                .FirstOrDefault();

            best ??= candidates
                .Where(candidate => candidate.Quantity.Equals("Phase1.Voltage.Magnitude", StringComparison.OrdinalIgnoreCase))
                .Select(candidate => candidate.MP)
                .FirstOrDefault();

            best ??= candidates
                .Where(candidate => candidate.Quantity.Contains("Voltage.Magnitude", StringComparison.OrdinalIgnoreCase))
                .Select(candidate => candidate.MP)
                .FirstOrDefault();

            best ??= candidates
                .Where(candidate => candidate.Quantity.Contains("Voltage", StringComparison.OrdinalIgnoreCase))
                .Select(candidate => candidate.MP)
                .FirstOrDefault();

            if (!string.IsNullOrWhiteSpace(best))
                terminalMPs[device] = best;

            // For DFR devices, extract line information from descriptions
            if (!IsDFRDevice(device))
                continue;

            // Group by measurement point to find distinct lines
            HashSet<string> seenLineNames = new(StringComparer.OrdinalIgnoreCase);

            foreach ((string mp, string _, string desc) in candidates)
            {
                // Extract line name from description using shared helper
                string? lineName = ExtractLineNameFromDescription(desc);

                if (string.IsNullOrWhiteSpace(lineName))
                    continue;

                // Strip phase suffix to get canonical line name
                lineName = StripPhaseSuffix(lineName);

                if (string.IsNullOrWhiteSpace(lineName) || !seenLineNames.Add(lineName))
                    continue;

                // Try to extract voltage from the description (e.g., "115kV" or "230KV")
                int voltageKV = ExtractVoltageFromDescription(desc);

                if (!deviceDFRLinesMap.TryGetValue(device, out List<DFRLineInfo>? lineList))
                {
                    lineList = [];
                    deviceDFRLinesMap[device] = lineList;
                }

                lineList.Add(new DFRLineInfo(lineName, mp, voltageKV));
            }
        }

        return (terminalMPs, deviceDFRLinesMap);
    }

    /// <summary>
    /// Extracts voltage level (kV) from a description string.
    /// </summary>
    /// <param name="desc">The description string to parse (e.g., "115kV_BUS" or "230KV LINE").</param>
    /// <returns>The voltage level in kV, or 0 if no valid voltage is found.</returns>
    /// <remarks>
    /// Recognizes patterns like "115kV", "230KV", or "500 kV" (case-insensitive, with optional space).
    /// </remarks>
    private static int ExtractVoltageFromDescription(string desc)
    {
        if (string.IsNullOrWhiteSpace(desc))
            return 0;

        // Look for pattern like "115kV" or "230KV" or "500 kV"
        Match match = Regex.Match(desc, @"(\d+)\s*[kK][vV]", RegexOptions.IgnoreCase);

        if (match.Success && int.TryParse(match.Groups[1].Value, out int kv))
            return kv;

        return 0;
    }

    // ========= Phasor voltage resolution =========

    /// <summary>
    /// Resolves the effective voltage level (kV) for a phasor by following the
    /// DestinationPhasorID relationship when available.
    /// </summary>
    /// <param name="phasor">The phasor record to resolve the voltage for.</param>
    /// <param name="idPhasorMap">A dictionary mapping phasor IDs to phasor records.</param>
    /// <returns>The resolved voltage level in kV.</returns>
    /// <remarks>
    /// <para>
    /// For voltage phasors (Type='V'), the BaseKV is used directly.
    /// </para>
    /// <para>
    /// For current phasors (Type='I') with a non-null DestinationPhasorID, the
    /// associated voltage phasor's BaseKV is used instead as this is the canonical
    /// way to determine what voltage level a current measurement belongs to.
    /// </para>
    /// <para>
    /// Falls back to the phasor's own BaseKV if no destination is available.
    /// </para>
    /// </remarks>
    private static int ResolveVoltageKV(PhasorRecord phasor, Dictionary<int, PhasorRecord> idPhasorMap)
    {
        // Voltage phasors: BaseKV is authoritative
        if (phasor.Type == 'V')
            return phasor.BaseKV;

        // Current phasors: follow DestinationPhasorID to the associated voltage phasor
        if (phasor.DestinationPhasorID.HasValue &&
            idPhasorMap.TryGetValue(phasor.DestinationPhasorID.Value, out PhasorRecord? destinationPhasor) &&
            destinationPhasor is { Type: 'V', BaseKV: > 0 })
        {
            return destinationPhasor.BaseKV;
        }

        // Fallback: use the current phasor's own BaseKV (may be 0 or approximate)
        return phasor.BaseKV;
    }

    // ========= Station derivation =========

    /// <summary>
    /// Derives stations by grouping devices on GPS coordinates. Station names are
    /// extracted from the device Acronym field for DFR devices (preferred as they
    /// represent the physical station), or from the device Name field for line-terminal
    /// devices, or from any device acronym as a fallback.
    /// </summary>
    /// <param name="devices">The list of device records to analyze.</param>
    /// <param name="idPhasorMap">A dictionary mapping phasor IDs to phasor records (for voltage resolution).</param>
    /// <returns>
    /// A tuple containing:
    /// <list type="bullet">
    /// <item><description>A list of derived station records</description></item>
    /// <item><description>The number of distinct coordinate groups found</description></item>
    /// <item><description>The number of stations skipped due to inability to extract a valid name</description></item>
    /// <item><description>The number of stations skipped due to no valid voltage level being found</description></item>
    /// </list>
    /// </returns>
    /// <remarks>
    /// <para>
    /// GPS coordinates are rounded to 2 decimal places (~1.1km precision) to group devices
    /// at the same physical location despite minor GPS coordinate variations.
    /// </para>
    /// <para>
    /// Station name extraction priority:
    /// 1. DFR device acronyms (most authoritative for station identification)
    /// 2. Line-terminal device names (extracted from "STATION-REMOTE" format)
    /// 3. Any device acronym with recognizable pattern
    /// 4. Device Name field as fallback
    /// 5. Device acronym with trailing digits stripped (last resort)
    /// </para>
    /// <para>
    /// The nominal voltage for each station is the maximum resolved voltage level across
    /// all phasors at that location.
    /// </para>
    /// </remarks>
    private static (List<StationRow> Stations, int CoordinateGroupsFound, int SkippedNoName, int SkippedNoVoltage) DeriveStations(List<DeviceRecord> devices, Dictionary<int, PhasorRecord> idPhasorMap)
    {
        // Coordinate key: (rounded lat, rounded lon) round to 2 decimal places (~1.1km)
        // to handle GPS variations between devices at the same station
        Dictionary<string, List<DeviceRecord>> coordinateDeviceMap = new(StringComparer.OrdinalIgnoreCase);

        foreach (DeviceRecord device in devices)
        {
            string key = CoordinateKey(device.Latitude, device.Longitude);

            if (!coordinateDeviceMap.TryGetValue(key, out List<DeviceRecord>? list))
            {
                list = [];
                coordinateDeviceMap[key] = list;
            }

            list.Add(device);
        }

        int coordinateGroupsFound = coordinateDeviceMap.Count;
        int skippedNoName = 0;
        int skippedNoVoltage = 0;
        List<StationRow> stations = [];

        foreach ((string _, List<DeviceRecord> group) in coordinateDeviceMap)
        {
            // Extract station name - prefer DFR devices as they directly represent
            // the physical station. Line-terminal (PMU) devices are used as second choice.
            // Any other device acronym pattern is used as fallback.
            string? stationName = null;

            // First pass: look for DFR device names (most authoritative)
            foreach (DeviceRecord device in group)
            {
                if (!IsDFRDevice(device.Acronym))
                    continue;

                string? extracted = ExtractStationFromDFRAcronym(device.Acronym);

                if (string.IsNullOrWhiteSpace(extracted))
                    continue;

                // For DFR devices, prefer the name that appears most frequently or is longest
                // (compound names like "GRAND GULF" are better than truncated names)
                if (stationName is null || extracted.Length > stationName.Length)
                    stationName = extracted;
            }

            // Second pass: fall back to line-terminal device names if no DFR device found
            if (stationName is null)
            {
                foreach (DeviceRecord device in group)
                {
                    if (!IsPMUDevice(device.Acronym))
                        continue;

                    string? extracted = ExtractStationFromName(device.Name);

                    if (string.IsNullOrWhiteSpace(extracted))
                        continue;

                    // For line-terminal devices, prefer longer names (compound names)
                    if (stationName is null || extracted.Length > stationName.Length)
                        stationName = extracted;
                }
            }

            // Third pass: fall back to any device acronym that has a recognizable pattern
            // This handles _Q_ (solar/inverter), _I_ (other PMU types), etc.
            if (stationName is null)
            {
                foreach (DeviceRecord device in group)
                {
                    string? extracted = ExtractStationFromAnyAcronym(device.Acronym);

                    if (string.IsNullOrWhiteSpace(extracted))
                        continue;

                    // Prefer longer names (compound names)
                    if (stationName is null || extracted.Length > stationName.Length)
                        stationName = extracted;
                }
            }

            // Fourth pass: try to extract from device Name field (for devices without standard acronym patterns)
            if (stationName is null)
            {
                foreach (DeviceRecord device in group)
                {
                    string? extracted = ExtractStationFromName(device.Name);

                    if (string.IsNullOrWhiteSpace(extracted))
                        continue;

                    if (stationName is null || extracted.Length > stationName.Length)
                        stationName = extracted;
                }
            }

            // Fifth pass: last resort - use the device acronym directly (strip any trailing numbers)
            if (stationName is null)
            {
                foreach (DeviceRecord device in group)
                {
                    // Try to use the acronym directly, stripping trailing digits
                    string acronym = device.Acronym.Trim();
                    
                    if (string.IsNullOrWhiteSpace(acronym))
                        continue;

                    // Strip trailing digits
                    acronym = Regex.Replace(acronym, @"\d+$", string.Empty).Trim();
                    
                    // Replace underscores with spaces for display
                    string extracted = acronym.Replace('_', ' ').Trim();

                    if (string.IsNullOrWhiteSpace(extracted))
                        continue;
                    
                    if (stationName is null || extracted.Length > stationName.Length)
                        stationName = extracted;
                }
            }

            if (string.IsNullOrWhiteSpace(stationName))
            {
                skippedNoName++;
                continue;
            }

            string stationID = NormalizeToID(stationName);

            if (string.IsNullOrWhiteSpace(stationID))
            {
                skippedNoName++;
                continue;
            }

            // Use coordinates from first device in group
            decimal latitude = group[0].Latitude;
            decimal longitude = group[0].Longitude;

            // Determine nominal voltage: maximum resolved kV across all phasors at this station.
            // For current phasors, follows DestinationPhasorID to the associated voltage phasor.
            int maxKV = group
                .SelectMany(device => device.Phasors)
                .Select(phasor => ResolveVoltageKV(phasor, idPhasorMap))
                .Where(kv => kv > 0)
                .DefaultIfEmpty(0)
                .Max();

            // If no phasors with valid kV, skip this station (can't determine voltage level)
            if (maxKV == 0)
            {
                skippedNoVoltage++;
                continue;
            }

            // Avoid duplicate station IDs (can happen with coordinate rounding)
            if (stations.Any(s => s.StationID.Equals(stationID, StringComparison.OrdinalIgnoreCase)))
                continue;

            stations.Add(new StationRow
            {
                StationID = stationID,
                Latitude = latitude,
                Longitude = longitude,
                NominalVoltageKV = maxKV
            });
        }

        return (stations.OrderBy(station => station.StationID, StringComparer.OrdinalIgnoreCase).ToList(), coordinateGroupsFound, skippedNoName, skippedNoVoltage);
    }

    // ========= Bus derivation =========

    /// <summary>
    /// Derives buses from stations and distinct resolved voltage levels found at each station.
    /// For current phasors, voltage is resolved via DestinationPhasorID to the associated voltage phasor.
    /// </summary>
    /// <param name="devices">The list of device records to analyze.</param>
    /// <param name="idStationMap">A dictionary mapping station IDs to station records.</param>
    /// <param name="idPhasorMap">A dictionary mapping phasor IDs to phasor records (for voltage resolution).</param>
    /// <returns>A list of derived bus records, sorted by station ID and then by voltage level.</returns>
    /// <remarks>
    /// Each unique combination of station and voltage level produces one bus record.
    /// The bus identifier follows the format "{StationID}_{VoltageKV}_BUS".
    /// </remarks>
    private static List<BusRow> DeriveBuses(
        List<DeviceRecord> devices,
        Dictionary<string, StationRow> idStationMap,
        Dictionary<int, PhasorRecord> idPhasorMap)
    {
        // Map each device to its station via coordinates
        Dictionary<string, HashSet<int>> stationVoltagesMap = new(StringComparer.OrdinalIgnoreCase);

        foreach (DeviceRecord device in devices)
        {
            string? stationID = FindStationForDevice(device, idStationMap);

            if (stationID is null)
                continue;

            if (!stationVoltagesMap.TryGetValue(stationID, out HashSet<int>? voltages))
            {
                voltages = [];
                stationVoltagesMap[stationID] = voltages;
            }

            // Resolve voltage levels through DestinationPhasorID for current phasors
            foreach (PhasorRecord record in device.Phasors)
            {
                int kv = ResolveVoltageKV(record, idPhasorMap);

                if (kv > 0)
                    voltages.Add(kv);
            }
        }

        List<BusRow> buses = [];

        foreach ((string stationID, HashSet<int> voltages) in stationVoltagesMap)
        {
            foreach (int kv in voltages.Order())
            {
                buses.Add(new BusRow
                {
                    BusID = $"{stationID}_{kv}_BUS",
                    StationID = stationID,
                    NominalVoltageKV = kv
                });
            }
        }

        return buses.OrderBy(bus => bus.StationID).ThenBy(b => b.NominalVoltageKV).ToList();
    }

    // ========= Line derivation =========

    /// <summary>
    /// Derives transmission lines from line-terminal (_P_) PMU devices. Parses the
    /// device Name "STATION-REMOTE {KV}KV" to identify from/to station connections,
    /// matches them to buses, and looks up terminal measurement points from the
    /// existing signal mappings.
    /// </summary>
    /// <param name="devices">The list of device records to analyze.</param>
    /// <param name="idStationMap">A dictionary mapping station IDs to station records.</param>
    /// <param name="idBusMap">A dictionary mapping bus IDs to bus records.</param>
    /// <param name="terminalMPs">A dictionary mapping device acronyms to terminal measurement point identifiers.</param>
    /// <param name="idPhasorMap">A dictionary mapping phasor IDs to phasor records (for voltage resolution).</param>
    /// <returns>A list of derived transmission line records, sorted by line ID.</returns>
    /// <remarks>
    /// <para>
    /// Line endpoints are determined by alphabetical ordering of station names to ensure
    /// consistent line identifiers regardless of which device is encountered first.
    /// </para>
    /// <para>
    /// Terminal measurement points are looked up from the signal mappings CSV. If multiple
    /// devices represent the same line (one at each end), their information is merged into
    /// a single line record.
    /// </para>
    /// </remarks>
    private static List<LineRow> DeriveLines(
        List<DeviceRecord> devices,
        Dictionary<string, StationRow> idStationMap,
        Dictionary<string, BusRow> idBusMap,
        Dictionary<string, string> terminalMPs,
        Dictionary<int, PhasorRecord> idPhasorMap)
    {
        // Track line endpoints by line ID - each line may have two device records
        // (one at each end), so we need to merge them
        Dictionary<string, LineEndpoints> lineEndpoints = new(StringComparer.OrdinalIgnoreCase);

        foreach (DeviceRecord device in devices)
        {
            if (!IsPMUDevice(device.Acronym))
                continue;

            // Parse the device Name to extract station, remote, and voltage
            // Get fallback voltage from phasor data
            int fallbackKV = device.Phasors
                .Select(phasor => ResolveVoltageKV(phasor, idPhasorMap))
                .Where(kv => kv > 0)
                .DefaultIfEmpty(0)
                .Max();

            DeviceHelper.LineParse? parsedLine = ParseLineFromDeviceName(device.Name, fallbackKV);

            if (parsedLine is null)
                continue;

            string fromStationID = NormalizeToID(parsedLine.FromStation);
            string remoteStationID = NormalizeToID(parsedLine.ToRemote);

            if (string.IsNullOrWhiteSpace(fromStationID))
                continue;   

            // Build a stable line ID from the two endpoints (alphabetical order)
            string lineID = BuildLineID(fromStationID, remoteStationID);

            // Look up from-bus at the device's voltage level (where this device is located)
            string localBusID = $"{fromStationID}_{parsedLine.NominalKV}_BUS";

            // Try to match remote name to a known station for the remote bus
            string? matchedRemoteStationID = FindMatchingStation(remoteStationID, idStationMap);
            string remoteBusID = matchedRemoteStationID is null ? 
                string.Empty :
                $"{matchedRemoteStationID}_{parsedLine.NominalKV}_BUS";

            // Determine terminal MP from signal mappings
            string mp = terminalMPs.GetValueOrDefault(device.Acronym) ?? string.Empty;

            // Track this endpoint
            if (!lineEndpoints.TryGetValue(lineID, out LineEndpoints? endpoints))
            {
                endpoints = new LineEndpoints { NominalKV = parsedLine.NominalKV };
                lineEndpoints[lineID] = endpoints;
            }

            // Determine which endpoint this device represents by comparing station IDs
            // The "from" end is the one at the alphabetically first station
            bool isFromEnd = string.Compare(fromStationID, remoteStationID, StringComparison.OrdinalIgnoreCase) <= 0;

            if (isFromEnd)
            {
                // This device is at the "from" end of the line
                if (string.IsNullOrWhiteSpace(endpoints.FromMP))
                    endpoints.FromMP = mp;

                if (string.IsNullOrWhiteSpace(endpoints.FromBusID) && idBusMap.ContainsKey(localBusID))
                    endpoints.FromBusID = localBusID;

                // Also set ToBusID from the remote if we know it
                if (string.IsNullOrWhiteSpace(endpoints.ToBusID) && !string.IsNullOrWhiteSpace(remoteBusID) && idBusMap.ContainsKey(remoteBusID))
                    endpoints.ToBusID = remoteBusID;
            }
            else
            {
                // This device is at the "to" end of the line (alphabetically second station)
                if (string.IsNullOrWhiteSpace(endpoints.ToMP))
                    endpoints.ToMP = mp;

                if (string.IsNullOrWhiteSpace(endpoints.ToBusID) && idBusMap.ContainsKey(localBusID))
                    endpoints.ToBusID = localBusID;

                // Also set FromBusID from the remote if we know it
                if (string.IsNullOrWhiteSpace(endpoints.FromBusID) && !string.IsNullOrWhiteSpace(remoteBusID) && idBusMap.ContainsKey(remoteBusID))
                    endpoints.FromBusID = remoteBusID;
            }
        }

        // Convert endpoints to LineRow records
        List<LineRow> lines = [];

        foreach ((string lineID, LineEndpoints endpoints) in lineEndpoints)
        {
            lines.Add(new LineRow(
                LineID: lineID,
                FromTerminalMP: endpoints.FromMP ?? string.Empty,
                ToTerminalMP: endpoints.ToMP ?? string.Empty,
                FromBusID: endpoints.FromBusID ?? string.Empty,
                ToBusID: endpoints.ToBusID ?? string.Empty,
                NominalVoltageKV: endpoints.NominalKV
            ));
        }

        return lines.OrderBy(line => line.LineID, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// Derives additional transmission lines from DFR devices using phasor labels
    /// extracted from the signal mappings CSV.
    /// </summary>
    /// <param name="devices">The list of device records to analyze.</param>
    /// <param name="idStationMap">A dictionary mapping station IDs to station records.</param>
    /// <param name="idBusMap">A dictionary mapping bus IDs to bus records.</param>
    /// <param name="terminalMPs">A dictionary mapping device acronyms to terminal measurement point identifiers.</param>
    /// <param name="idPhasorMap">A dictionary mapping phasor IDs to phasor records (for voltage resolution).</param>
    /// <param name="deviceDFRLinesMap">A dictionary mapping DFR device acronyms to lists of line information.</param>
    /// <param name="existingLines">The list of existing line records to augment (lines are added to this list).</param>
    /// <returns>The number of DFR-derived lines added to the existing lines list.</returns>
    /// <remarks>
    /// <para>
    /// DFR line names are extracted from phasor labels in the signal mappings CSV and
    /// typically represent the remote station name. Lines that look like buses or transformers
    /// (containing "BUS", "AUTOTRAN", or "XFMR" in the name) are excluded.
    /// </para>
    /// <para>
    /// Voltage levels are extracted from signal descriptions if available, otherwise
    /// resolved from the device's phasor data.
    /// </para>
    /// <para>
    /// Lines already present in the existing lines list (from PMU derivation) are skipped
    /// to avoid duplicates.
    /// </para>
    /// </remarks>
    private static int DeriveDFRLines(
        List<DeviceRecord> devices,
        Dictionary<string, StationRow> idStationMap,
        Dictionary<string, BusRow> idBusMap,
        Dictionary<string, string> terminalMPs,
        Dictionary<int, PhasorRecord> idPhasorMap,
        Dictionary<string, List<DFRLineInfo>> deviceDFRLinesMap,
        List<LineRow> existingLines)
    {
        // Track existing line IDs to avoid duplicates
        HashSet<string> existingLineIDs = new(existingLines.Select(l => l.LineID), StringComparer.OrdinalIgnoreCase);
        int linesAdded = 0;

        foreach (DeviceRecord device in devices)
        {
            if (!IsDFRDevice(device.Acronym))
                continue;

            // Get the station this DFR is at
            string? stationID = FindStationForDevice(device, idStationMap);

            if (stationID is null)
                continue;

            // Get DFR lines extracted from signal mappings
            if (!deviceDFRLinesMap.TryGetValue(device.Acronym, out List<DFRLineInfo>? dfrLines))
                continue;

            foreach (DFRLineInfo lineInfo in dfrLines)
            {
                // The line name from DFR phasor labels is typically just the remote station name
                // (e.g., "WPEC", "BOGALUSA_LN", "EAST_BUS")
                string remoteID = NormalizeToID(lineInfo.LineName);

                if (string.IsNullOrWhiteSpace(remoteID))
                    continue;

                // Skip if this looks like a bus or transformer (not a line to another station)
                if (remoteID.Contains("BUS", StringComparison.OrdinalIgnoreCase) ||
                    remoteID.Contains("AUTOTRAN", StringComparison.OrdinalIgnoreCase) ||
                    remoteID.Contains("XFMR", StringComparison.OrdinalIgnoreCase))
                    continue;

                // Build line ID
                string lineID = BuildLineID(stationID, remoteID);

                // Skip if this line already exists (from PMU derivation)
                if (existingLineIDs.Contains(lineID))
                    continue;

                // Determine voltage level - use from line info if available, otherwise from device phasors
                int nominalKV = lineInfo.VoltageKV;

                if (nominalKV == 0)
                {
                    nominalKV = device.Phasors
                        .Select(p => ResolveVoltageKV(p, idPhasorMap))
                        .Where(kv => kv > 0)
                        .DefaultIfEmpty(0)
                        .Max();
                }

                if (nominalKV == 0)
                    continue;

                // Build bus IDs
                string localBusID = $"{stationID}_{nominalKV}_BUS";

                // Try to find matching remote station
                string? matchedRemoteID = FindMatchingStation(remoteID, idStationMap);
                string remoteBusID = matchedRemoteID is not null ? 
                    $"{matchedRemoteID}_{nominalKV}_BUS" : 
                    string.Empty;

                // Determine terminal MP
                string mp = lineInfo.MeasurementPoint;

                if (string.IsNullOrWhiteSpace(mp))
                    mp = terminalMPs.GetValueOrDefault(device.Acronym) ?? string.Empty;

                // Determine from/to based on alphabetical order
                bool isFromEnd = string.Compare(stationID, remoteID, StringComparison.OrdinalIgnoreCase) <= 0;

                string fromMP = isFromEnd ? mp : string.Empty;
                string toMP = isFromEnd ? string.Empty : mp;
                string fromBusID = isFromEnd && idBusMap.ContainsKey(localBusID) ? localBusID : (idBusMap.ContainsKey(remoteBusID) ? remoteBusID : string.Empty);
                string toBusID = !isFromEnd && idBusMap.ContainsKey(localBusID) ? localBusID : (idBusMap.ContainsKey(remoteBusID) ? remoteBusID : string.Empty);

                // Swap if needed to maintain from/to consistency
                if (!isFromEnd)
                    (fromBusID, toBusID) = (toBusID, fromBusID);

                existingLines.Add(new LineRow(
                    LineID: lineID,
                    FromTerminalMP: fromMP,
                    ToTerminalMP: toMP,
                    FromBusID: fromBusID,
                    ToBusID: toBusID,
                    NominalVoltageKV: nominalKV
                ));

                existingLineIDs.Add(lineID);
                linesAdded++;
            }
        }

        // Re-sort the lines list after adding DFR lines
        existingLines.Sort((a, b) => string.Compare(a.LineID, b.LineID, StringComparison.OrdinalIgnoreCase));

        return linesAdded;
    }

    // ========= Adjacent bus computation =========

    /// <summary>
    /// Computes the AdjacentBusIDs for each bus based on line connections.
    /// Two buses are adjacent if they are connected by at least one line.
    /// </summary>
    /// <param name="buses">The list of bus records to update with adjacent bus information.</param>
    /// <param name="lines">The list of transmission line records defining bus connections.</param>
    /// <remarks>
    /// The <see cref="BusRow.AdjacentBusIDs"/> property is populated with a semicolon-separated
    /// list of adjacent bus identifiers, sorted alphabetically.
    /// </remarks>
    private static void ComputeAdjacentBuses(List<BusRow> buses, List<LineRow> lines)
    {
        Dictionary<string, HashSet<string>> adjacency = new(StringComparer.OrdinalIgnoreCase);

        foreach (LineRow line in lines)
        {
            if (string.IsNullOrWhiteSpace(line.FromBusID) || string.IsNullOrWhiteSpace(line.ToBusID))
                continue;

            if (line.FromBusID.Equals(line.ToBusID, StringComparison.OrdinalIgnoreCase))
                continue;

            AddAdjacent(adjacency, line.FromBusID, line.ToBusID);
            AddAdjacent(adjacency, line.ToBusID, line.FromBusID);
        }

        foreach (BusRow bus in buses)
        {
            if (adjacency.TryGetValue(bus.BusID, out HashSet<string>? neighbors))
                bus.AdjacentBusIDs = string.Join(";", neighbors.Order(StringComparer.OrdinalIgnoreCase));
        }
    }

    /// <summary>
    /// Adds an adjacent bus relationship to the adjacency dictionary.
    /// </summary>
    /// <param name="adjacency">
    /// A dictionary representing the adjacency relationships between buses, 
    /// where the key is a bus identifier and the value is a set of adjacent bus identifiers.
    /// </param>
    /// <param name="busA">The identifier of the first bus.</param>
    /// <param name="busB">The identifier of the second bus to be added as adjacent to the first bus.</param>
    private static void AddAdjacent(Dictionary<string, HashSet<string>> adjacency, string busA, string busB)
    {
        if (!adjacency.TryGetValue(busA, out HashSet<string>? set))
        {
            set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            adjacency[busA] = set;
        }

        set.Add(busB);
    }

    // ========= Station matching helpers =========

    /// <summary>
    /// Finds the station for a device by matching its coordinates to known stations.
    /// </summary>
    /// <param name="dev">The device record to find a station for.</param>
    /// <param name="idStationMap">A dictionary mapping station IDs to station records.</param>
    /// <returns>The station ID if a matching station is found; otherwise, <c>null</c>.</returns>
    /// <remarks>
    /// Stations are matched by comparing rounded GPS coordinates (2 decimal places precision).
    /// </remarks>
    private static string? FindStationForDevice(DeviceRecord dev, Dictionary<string, StationRow> idStationMap)
    {
        string coordinateKey = CoordinateKey(dev.Latitude, dev.Longitude);

        foreach ((string stationID, StationRow station) in idStationMap)
        {
            if (CoordinateKey(station.Latitude, station.Longitude) == coordinateKey)
                return stationID;
        }

        return null;
    }

    /// <summary>
    /// Attempts to match a remote station identifier to a known station.
    /// Uses fuzzy matching to handle name variations (e.g., "BAXTER WILSON" in device
    /// Name matching station "BAXTER_WILSON").
    /// </summary>
    /// <param name="remoteID">The remote station identifier to match.</param>
    /// <param name="idStationMap">A dictionary mapping station IDs to station records.</param>
    /// <returns>The matched station ID if found; otherwise, <c>null</c>.</returns>
    /// <remarks>
    /// <para>
    /// Matching is attempted in the following order:
    /// 1. Exact match on station ID
    /// 2. Normalized match (ignoring underscores, spaces, and case)
    /// 3. Partial match (one name contains the other)
    /// </para>
    /// </remarks>
    private static string? FindMatchingStation(string remoteID, Dictionary<string, StationRow> idStationMap)
    {
        if (string.IsNullOrWhiteSpace(remoteID))
            return null;

        // Exact match
        if (idStationMap.ContainsKey(remoteID))
            return remoteID;

        // Try partial matching: check if any known station ID matches when normalized
        string normalizedRemote = remoteID.Replace("_", string.Empty).Replace(" ", string.Empty).ToUpperInvariant();

        foreach (string stationID in idStationMap.Keys)
        {
            string normalizedStation = stationID.Replace("_", string.Empty).Replace(" ", string.Empty).ToUpperInvariant();

            if (normalizedStation == normalizedRemote)
                return stationID;

            // Check if station contains remote or remote contains station
            // (handles partial matches like "MABELVALE" matching "MABELVALE_SES")
            if (normalizedStation.Contains(normalizedRemote, StringComparison.OrdinalIgnoreCase) ||
                normalizedRemote.Contains(normalizedStation, StringComparison.OrdinalIgnoreCase))
                return stationID;
        }

        return null;
    }

    /// <summary>
    /// Produces a coordinate grouping key by rounding to 2 decimal places (~1.1km precision).
    /// This handles GPS variations between devices at the same physical station.
    /// </summary>
    /// <param name="lat">The latitude coordinate.</param>
    /// <param name="lon">The longitude coordinate.</param>
    /// <returns>A string key in format "LAT|LON" with coordinates rounded to 2 decimal places.</returns>
    private static string CoordinateKey(decimal lat, decimal lon)
    {
        return $"{Math.Round(lat, 2):F2}|{Math.Round(lon, 2):F2}";
    }

    // ========= CSV parsing/writing =========

    /// <summary>
    /// Parses a CSV line into fields, respecting quoted values that may contain commas.
    /// </summary>
    /// <param name="line">The CSV line to parse.</param>
    /// <returns>An array of field values extracted from the line.</returns>
    /// <remarks>
    /// Handles double-quoted fields containing commas and preserves the content within quotes.
    /// </remarks>
    private static string[] ParseCSVLine(string line)
    {
        List<string> fields = [];
        bool inQuotes = false;
        StringBuilder current = new();

        foreach (char c in line)
        {
            switch (c)
            {
                case '"':
                    inQuotes = !inQuotes;
                    break;
                case ',' when !inQuotes:
                    fields.Add(current.ToString());
                    current.Clear();
                    break;
                default:
                    current.Append(c);
                    break;
            }
        }

        fields.Add(current.ToString());

        return [.. fields];
    }

    /// <summary>
    /// Writes the stations list to a CSV file.
    /// </summary>
    /// <param name="path">The file path to write to.</param>
    /// <param name="stations">The list of station records to write.</param>
    /// <remarks>
    /// The CSV file includes headers: StationId, Latitude, Longitude, NominalVoltageKV.
    /// The file is written with UTF-8 encoding without BOM.
    /// </remarks>
    private static void WriteStationsCSV(string path, List<StationRow> stations)
    {
        using FileStream stream = File.Create(path);
        using StreamWriter writer = new(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        writer.WriteLine("StationId,Latitude,Longitude,NominalVoltageKV");

        foreach (StationRow station in stations)
        {
            writer.WriteLine(string.Join(",",
                CSVField(station.StationID),
                station.Latitude.ToString(CultureInfo.InvariantCulture),
                station.Longitude.ToString(CultureInfo.InvariantCulture),
                station.NominalVoltageKV.ToString(CultureInfo.InvariantCulture)
            ));
        }
    }

    /// <summary>
    /// Writes the buses list to a CSV file.
    /// </summary>
    /// <param name="path">The file path to write to.</param>
    /// <param name="buses">The list of bus records to write.</param>
    /// <remarks>
    /// The CSV file includes headers: BusId, StationId, NominalVoltageKV, AdjacentBusIds.
    /// The file is written with UTF-8 encoding without BOM.
    /// </remarks>
    private static void WriteBusesCSV(string path, List<BusRow> buses)
    {
        using FileStream stream = File.Create(path);
        using StreamWriter writer = new(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        writer.WriteLine("BusId,StationId,NominalVoltageKV,AdjacentBusIds");

        foreach (BusRow bus in buses)
        {
            writer.WriteLine(string.Join(",",
                CSVField(bus.BusID),
                CSVField(bus.StationID),
                bus.NominalVoltageKV.ToString(CultureInfo.InvariantCulture),
                CSVField(bus.AdjacentBusIDs)
            ));
        }
    }

    /// <summary>
    /// Writes the lines list to a CSV file.
    /// </summary>
    /// <param name="path">The file path to write to.</param>
    /// <param name="lines">The list of transmission line records to write.</param>
    /// <remarks>
    /// The CSV file includes headers: LineId, FromTerminalMP, ToTerminalMP, FromBusId, ToBusId, NominalVoltageKV.
    /// The file is written with UTF-8 encoding without BOM.
    /// </remarks>
    private static void WriteLinesCSV(string path, List<LineRow> lines)
    {
        using FileStream stream = File.Create(path);
        using StreamWriter writer = new(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        writer.WriteLine("LineId,FromTerminalMP,ToTerminalMP,FromBusId,ToBusId,NominalVoltageKV");

        foreach (LineRow line in lines)
        {
            writer.WriteLine(string.Join(",",
                CSVField(line.LineID),
                CSVField(line.FromTerminalMP),
                CSVField(line.ToTerminalMP),
                CSVField(line.FromBusID),
                CSVField(line.ToBusID),
                line.NominalVoltageKV.ToString(CultureInfo.InvariantCulture)
            ));
        }
    }
}