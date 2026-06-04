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
    /// <param name="signalMappings">The STTP signal mappings produced by <see cref="SttpConfigExporter"/>, carrying each measurement point with its database-authoritative phasor label and signal id.</param>
    /// <returns>Summary of export results including counts of derived entities.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="connection"/> or <paramref name="signalMappings"/> is <c>null</c>.</exception>
    /// <exception cref="ArgumentException">Thrown when any of the required CSV output paths in <see cref="Settings"/> are null or empty.</exception>
    public static ModelExportResult Export(DbConnection connection, IReadOnlyList<SttpConfigExporter.SignalMapping> signalMappings)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(signalMappings);
        ArgumentException.ThrowIfNullOrWhiteSpace(Settings.StationsCsvPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(Settings.BusesCsvPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(Settings.LinesCsvPath);

        // 1) Load raw device + phasor data from DB
        (List<DeviceRecord> devices, Dictionary<int, PhasorRecord> idPhasorMap) = LoadDeviceRecords(connection);

        // Collect sample data for diagnostics
        List<string> sampleAcronyms = devices.Take(10).Select(device => device.Acronym).ToList();
        List<string> sampleNames = devices.Take(10).Select(device => device.Name).ToList();
        List<int> sampleBaseKVs = idPhasorMap.Values.Take(10).Select(phasor => phasor.BaseKV).Distinct().ToList();

        // 2) Build terminal measurement points and per-device DFR line/bus info directly from the
        //    STTP signal mappings. Each mapping carries the database's clean phasor label, so line
        //    and bus names need no CSV re-read or free-text description parsing.
        (Dictionary<string, string> terminalMPs, Dictionary<string, List<DFRLineInfo>> deviceDFRLinesMap) = BuildTerminalsAndDFRLines(signalMappings);

        // 3) Derive stations by grouping devices on GPS coordinates
        (List<StationRow> stations, int coordGroupsFound, int skippedNoName, int skippedNoVoltage, List<string> skippedNoNameDetails, List<string> skippedNoVoltageDetails) = DeriveStations(devices, idPhasorMap);

        // Build lookup: StationId => StationRow
        Dictionary<string, StationRow> idStationMap = stations.ToDictionary(
            station => station.StationID, station => station, StringComparer.OrdinalIgnoreCase);

        // 4) Collect bus-voltage measurement points and build the canonical bus map: a bus is named
        //    after its voltage measurement point when exactly one exists at a station and voltage,
        //    so connected lines reference the measured bus.
        Dictionary<string, Dictionary<string, int>> deviceLabelVoltages = BuildDeviceLabelVoltages(devices, idPhasorMap);

        List<(string Station, int KV, string MeasurementPoint)> busMeasurementPoints =
            CollectBusMeasurementPoints(devices, idStationMap, deviceDFRLinesMap, deviceLabelVoltages, idPhasorMap);

        Dictionary<string, string> canonicalBus = BuildCanonicalBusMap(busMeasurementPoints);

        // 5) Derive buses: one per station + distinct voltage level (named via the canonical map)
        List<BusRow> buses = DeriveBuses(devices, idStationMap, idPhasorMap, canonicalBus);

        // Build lookup: BusId => BusRow
        Dictionary<string, BusRow> idBusMap = buses.ToDictionary(
            bus => bus.BusID, bus => bus, StringComparer.OrdinalIgnoreCase);

        // 6) Derive lines from measured terminals (PMU devices, DFR phasor labels, and transformers).
        //    Each terminal's measurement point is paired with its own bus on the same side, and
        //    notional buses are generated on demand to serve as that anchor.
        (List<LineRow> lines, int dfrLinesAdded, HashSet<string> usedTerminalMPs, HashSet<string> unmatchedRemotes, HashSet<string> candidateTerminalMPs) =
            DeriveLines(devices, idStationMap, idBusMap, buses, terminalMPs, deviceDFRLinesMap, idPhasorMap, canonicalBus);

        // 7) Ensure every bus-voltage measurement point has a matching bus identifier
        AddBusMeasurementPointRows(busMeasurementPoints, idStationMap, idBusMap, buses);

        HashSet<string> busMeasurementPointNames = new(busMeasurementPoints.Select(point => point.MeasurementPoint), StringComparer.OrdinalIgnoreCase);

        // 7b) Drop notional buses left unreferenced once lines bind to their specific measured buses
        PruneUnreferencedNotionalBuses(buses, idBusMap, lines, busMeasurementPointNames);

        // 8) Reconcile station nominal voltages with any buses generated during derivation
        ReconcileStationVoltages(stations, buses);

        // 9) Compute adjacent bus IDs from line connections
        ComputeAdjacentBuses(buses, lines);

        // 10) Report measurement points anchored to neither a line terminal nor a bus, then validate
        Dictionary<string, MeasurementPointInfo> allMeasurementPoints = BuildMeasurementPointInfo(signalMappings);

        HashSet<string> anchoredMeasurementPoints = new(usedTerminalMPs, StringComparer.OrdinalIgnoreCase);

        foreach (BusRow bus in buses)
            anchoredMeasurementPoints.Add(bus.BusID);

        (int orphanCount, string orphanSummary, List<string> orphanGenuineGaps) =
            AnalyzeOrphanMeasurementPoints(allMeasurementPoints, anchoredMeasurementPoints, candidateTerminalMPs);
        List<string> invariantViolations = CheckModelInvariants(stations, buses, lines);
        List<string> possibleTypos = FindPossibleTypos(unmatchedRemotes, idStationMap);
        List<string> powerCalcGaps = AnalyzePowerCalcCoverage(lines, allMeasurementPoints, busMeasurementPointNames);

        // 9) Sort stations and buses for stable, readable output files
        stations.Sort((left, right) => string.Compare(left.StationID, right.StationID, StringComparison.OrdinalIgnoreCase));
        buses.Sort((left, right) =>
        {
            int byStation = string.Compare(left.StationID, right.StationID, StringComparison.OrdinalIgnoreCase);
            return byStation != 0 ? byStation : left.NominalVoltageKV.CompareTo(right.NominalVoltageKV);
        });

        // 10) Write output CSVs
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
            SkippedNoNameDetails: skippedNoNameDetails,
            SkippedNoVoltageDetails: skippedNoVoltageDetails,
            OrphanMeasurementPoints: orphanCount,
            OrphanMeasurementPointSummary: orphanSummary,
            OrphanGenuineGaps: orphanGenuineGaps,
            InvariantViolations: invariantViolations,
            PossibleTypos: possibleTypos,
            PowerCalcGaps: powerCalcGaps,
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
    /// <param name="SkippedNoNameDetails">Details of coordinate groups skipped because no station name could be extracted.</param>
    /// <param name="SkippedNoVoltageDetails">Details (station + devices) of stations skipped because no voltage could be resolved.</param>
    /// <param name="OrphanMeasurementPoints">The number of signal-mapping measurement points anchored to neither a line terminal nor a bus.</param>
    /// <param name="OrphanMeasurementPointSummary">A classification breakdown of the unanchored measurement points (redundant duplicates vs genuine gaps).</param>
    /// <param name="OrphanGenuineGaps">Unanchored complete (voltage + current) terminals that were never modelable — the actionable gaps.</param>
    /// <param name="InvariantViolations">Power system model rule violations detected in the derived model (empty when consistent).</param>
    /// <param name="PossibleTypos">Likely source-data spelling inconsistencies (remote names or stations within one edit of each other).</param>
    /// <param name="PowerCalcGaps">Line terminals whose current has no usable voltage source (no own voltage and a notional bus), so SEL cannot compute power.</param>
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
        List<string> SkippedNoNameDetails,
        List<string> SkippedNoVoltageDetails,
        int OrphanMeasurementPoints,
        string OrphanMeasurementPointSummary,
        List<string> OrphanGenuineGaps,
        List<string> InvariantViolations,
        List<string> PossibleTypos,
        List<string> PowerCalcGaps,
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
        /// Gets or sets the nominal voltage level in kV (the maximum voltage level found at this
        /// station, reconciled against any buses generated during line derivation).
        /// </summary>
        public required int NominalVoltageKV { get; set; }
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
        const string DeviceSQL = 
            """
            SELECT ID, Acronym, ISNULL(Name, Acronym) AS Name, Latitude, Longitude,
                IsConcentrator, ParentID
            FROM Device
            WHERE Enabled <> 0 AND 
                Latitude IS NOT NULL AND
                Longitude IS NOT NULL
            """;

        const string PhasorSQL = 
            """
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

    // ========= Terminal and DFR line/bus derivation from signal mappings =========

    /// <summary>
    /// Builds, from the STTP signal mappings, the per-device terminal measurement point and the
    /// per-DFR-device list of line/bus terminals. Line and bus names come from each mapping's clean
    /// phasor label (database-authoritative), so no CSV re-read or free-text description parsing is
    /// needed, and only phasor measurements (those carrying a phasor label) define lines and buses,
    /// so calculated-power rows cannot mis-parse into spurious lines.
    /// </summary>
    /// <param name="signalMappings">The STTP signal mappings produced by the configuration export.</param>
    /// <returns>
    /// A tuple containing a dictionary mapping device acronyms to their terminal measurement point,
    /// and a dictionary mapping DFR device acronyms to their distinct line/bus terminals.
    /// </returns>
    /// <remarks>
    /// The terminal measurement point prefers PhaseA.Voltage.Magnitude, then Phase1.Voltage.Magnitude,
    /// then any voltage magnitude, then any voltage signal. Terminal voltage levels are resolved later
    /// from the phasor graph, so they are left unset (0) here.
    /// </remarks>
    private static (Dictionary<string, string> TerminalMPs, Dictionary<string, List<DFRLineInfo>> DeviceDFRLinesMap) BuildTerminalsAndDFRLines(
        IReadOnlyList<SttpConfigExporter.SignalMapping> signalMappings)
    {
        Dictionary<string, string> terminalMPs = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, List<DFRLineInfo>> deviceDFRLinesMap = new(StringComparer.OrdinalIgnoreCase);

        // Group mappings by device, ignoring rows without a device or measurement point.
        Dictionary<string, List<SttpConfigExporter.SignalMapping>> deviceMappings = new(StringComparer.OrdinalIgnoreCase);

        foreach (SttpConfigExporter.SignalMapping mapping in signalMappings)
        {
            if (string.IsNullOrWhiteSpace(mapping.DeviceAcronym) || string.IsNullOrWhiteSpace(mapping.MeasurementPoint))
                continue;

            if (!deviceMappings.TryGetValue(mapping.DeviceAcronym, out List<SttpConfigExporter.SignalMapping>? list))
            {
                list = [];
                deviceMappings[mapping.DeviceAcronym] = list;
            }

            list.Add(mapping);
        }

        foreach ((string device, List<SttpConfigExporter.SignalMapping> mappings) in deviceMappings)
        {
            // Terminal MP: a voltage magnitude point identifies the device's terminal.
            string? best = mappings
                .Where(mapping => mapping.Quantity.Equals("PhaseA.Voltage.Magnitude", StringComparison.OrdinalIgnoreCase))
                .Select(mapping => mapping.MeasurementPoint)
                .FirstOrDefault();

            best ??= mappings
                .Where(mapping => mapping.Quantity.Equals("Phase1.Voltage.Magnitude", StringComparison.OrdinalIgnoreCase))
                .Select(mapping => mapping.MeasurementPoint)
                .FirstOrDefault();

            best ??= mappings
                .Where(mapping => mapping.Quantity.Contains("Voltage.Magnitude", StringComparison.OrdinalIgnoreCase))
                .Select(mapping => mapping.MeasurementPoint)
                .FirstOrDefault();

            best ??= mappings
                .Where(mapping => mapping.Quantity.Contains("Voltage", StringComparison.OrdinalIgnoreCase))
                .Select(mapping => mapping.MeasurementPoint)
                .FirstOrDefault();

            if (!string.IsNullOrWhiteSpace(best))
                terminalMPs[device] = best;

            if (!IsDFRDevice(device))
                continue;

            // Each distinct phasor label at the device is a line/bus terminal. Rows without a phasor
            // label (e.g., calculated power values) do not define lines/buses and are skipped.
            HashSet<string> seenLineNames = new(StringComparer.OrdinalIgnoreCase);

            foreach (SttpConfigExporter.SignalMapping mapping in mappings)
            {
                if (string.IsNullOrWhiteSpace(mapping.PhasorLabel))
                    continue;

                string lineName = StripPhaseSuffix(mapping.PhasorLabel);

                if (string.IsNullOrWhiteSpace(lineName) || !seenLineNames.Add(lineName))
                    continue;

                if (!deviceDFRLinesMap.TryGetValue(device, out List<DFRLineInfo>? lineList))
                {
                    lineList = [];
                    deviceDFRLinesMap[device] = lineList;
                }

                // Voltage is resolved from the phasor graph during derivation, so it is left at 0 here.
                lineList.Add(new DFRLineInfo(lineName, mapping.MeasurementPoint, 0));
            }
        }

        return (terminalMPs, deviceDFRLinesMap);
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
    private static (List<StationRow> Stations, int CoordinateGroupsFound, int SkippedNoName, int SkippedNoVoltage, List<string> SkippedNoNameDetails, List<string> SkippedNoVoltageDetails) DeriveStations(List<DeviceRecord> devices, Dictionary<int, PhasorRecord> idPhasorMap)
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
        List<string> skippedNoNameDetails = [];
        List<string> skippedNoVoltageDetails = [];
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
                // (compound names like "MAPLE RIDGE" are better than truncated names)
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
                skippedNoNameDetails.Add($"devices [{string.Join(", ", group.Select(device => device.Acronym))}] @ {group[0].Latitude:F2},{group[0].Longitude:F2}");
                continue;
            }

            string stationID = NormalizeToID(stationName);

            if (string.IsNullOrWhiteSpace(stationID))
            {
                skippedNoName++;
                skippedNoNameDetails.Add($"\"{stationName}\" devices [{string.Join(", ", group.Select(device => device.Acronym))}]");
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
                skippedNoVoltageDetails.Add($"{stationID} (devices: {string.Join(", ", group.Select(device => device.Acronym))})");
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

        return (stations.OrderBy(station => station.StationID, StringComparer.OrdinalIgnoreCase).ToList(), coordinateGroupsFound, skippedNoName, skippedNoVoltage, skippedNoNameDetails, skippedNoVoltageDetails);
    }

    // ========= Bus derivation =========

    /// <summary>
    /// Derives buses from stations and distinct resolved voltage levels found at each station.
    /// For current phasors, voltage is resolved via DestinationPhasorID to the associated voltage phasor.
    /// </summary>
    /// <param name="devices">The list of device records to analyze.</param>
    /// <param name="idStationMap">A dictionary mapping station IDs to station records.</param>
    /// <param name="idPhasorMap">A dictionary mapping phasor IDs to phasor records (for voltage resolution).</param>
    /// <param name="canonicalBus">A map from "station|kv" to the preferred bus identifier (a bus-voltage measurement point name when unique, otherwise the notional bus name).</param>
    /// <returns>A list of derived bus records, sorted by station ID and then by voltage level.</returns>
    /// <remarks>
    /// Each unique combination of station and voltage level produces one bus record.
    /// The bus identifier follows the format "{StationID}_{VoltageKV}_BUS".
    /// </remarks>
    private static List<BusRow> DeriveBuses(
        List<DeviceRecord> devices,
        Dictionary<string, StationRow> idStationMap,
        Dictionary<int, PhasorRecord> idPhasorMap,
        Dictionary<string, string> canonicalBus)
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
                    BusID = CanonicalBusID(stationID, kv, canonicalBus),
                    StationID = stationID,
                    NominalVoltageKV = kv
                });
            }
        }

        return buses.OrderBy(bus => bus.StationID).ThenBy(b => b.NominalVoltageKV).ToList();
    }

    // ========= Line derivation =========

    /// <summary>
    /// A single measured line terminal: the local station where a device sits, the remote
    /// endpoint it observes, the resolved voltage, and the measurement point carrying that
    /// terminal's current. Each PMU device yields one terminal; each distinct DFR phasor
    /// label yields one terminal.
    /// </summary>
    /// <param name="LocalStationID">Station identifier where the measuring device is located.</param>
    /// <param name="RemoteRaw">Normalized remote endpoint name parsed from the device name or phasor label.</param>
    /// <param name="NominalKV">Resolved nominal voltage level in kV for this terminal.</param>
    /// <param name="MeasurementPoint">Measurement point identifier carrying this terminal's signals.</param>
    /// <param name="FromDFR">Whether this terminal was derived from a DFR device (vs. a PMU device).</param>
    /// <param name="LocalBusOverride">When the terminal's current is paired (via DestinationPhasorID) with a specific bus voltage, the measurement point of that bus; otherwise empty (use the canonical station/voltage bus).</param>
    private sealed record Terminal(
        string LocalStationID,
        string RemoteRaw,
        int NominalKV,
        string MeasurementPoint,
        bool FromDFR,
        string LocalBusOverride);

    /// <summary>
    /// Collects the terminals that share a single two-station line so each measured end keeps
    /// its own measurement point paired with its own station's bus.
    /// </summary>
    private sealed class PairGroup(string stationA, string stationB)
    {
        public string StationA { get; } = stationA;
        public string StationB { get; } = stationB;
        public List<Terminal> Terminals { get; } = [];
    }

    /// <summary>
    /// Derives transmission lines from measured terminals (one per PMU device, one per distinct
    /// DFR phasor label). A terminal's measurement point (the line current) and that terminal's
    /// own bus are always emitted on the same side; the remote endpoint contributes a bus-only
    /// anchor on the opposite side only when it resolves to a distinct, known station.
    /// </summary>
    /// <param name="devices">The list of device records to analyze.</param>
    /// <param name="idStationMap">A dictionary mapping station IDs to station records.</param>
    /// <param name="idBusMap">A dictionary mapping bus IDs to bus records (extended on demand).</param>
    /// <param name="buses">The bus list, appended to when a notional bus is generated on demand.</param>
    /// <param name="terminalMPs">A dictionary mapping device acronyms to terminal measurement point identifiers.</param>
    /// <param name="deviceDFRLinesMap">A dictionary mapping DFR device acronyms to per-label line information.</param>
    /// <param name="idPhasorMap">A dictionary mapping phasor IDs to phasor records (for voltage resolution).</param>
    /// <param name="canonicalBus">A map from "station|kv" to the preferred bus identifier (a bus-voltage measurement point name when unique, otherwise the notional bus name).</param>
    /// <returns>The derived line records (sorted by line ID), the count derived from DFR terminals, the set of measurement points used as line terminals, the set of remote endpoint names that did not resolve to a known station, and the set of all measurement points considered as terminal candidates (emitted or dropped).</returns>
    /// <remarks>
    /// This keeps each terminal's measurement point paired with its bus, never produces a line
    /// whose two ends share a bus or substation, and lets each measurement point appear at most
    /// once across the file. Buses are generated on demand because the bus is a notional link
    /// between a line terminal and its substation voltage and is not shown directly to the user.
    /// </remarks>
    private static (List<LineRow> Lines, int DFRDerived, HashSet<string> UsedMeasurementPoints, HashSet<string> UnmatchedRemotes, HashSet<string> CandidateTerminalMPs) DeriveLines(
        List<DeviceRecord> devices,
        Dictionary<string, StationRow> idStationMap,
        Dictionary<string, BusRow> idBusMap,
        List<BusRow> buses,
        Dictionary<string, string> terminalMPs,
        Dictionary<string, List<DFRLineInfo>> deviceDFRLinesMap,
        Dictionary<int, PhasorRecord> idPhasorMap,
        Dictionary<string, string> canonicalBus)
    {
        // Authoritative current-to-voltage pairing from the phasor graph (DestinationPhasorID):
        // gives each DFR line current its correct voltage level and, when paired with a bus, the
        // specific bus measurement point its power must be computed against.
        Dictionary<string, Dictionary<string, (int Kv, string PairedBusMP)>> lineVoltagePairing =
            BuildDeviceLineVoltagePairing(devices, deviceDFRLinesMap, idPhasorMap);

        // 1) Collect measured terminals. PMU terminals are collected first so that, on a
        //    measurement-point collision, the PMU terminal is the one that is kept.
        List<Terminal> terminals = [];

        foreach (DeviceRecord device in devices)
        {
            if (!IsPMUDevice(device.Acronym))
                continue;

            // Anchor the terminal to the station the device physically sits at (by coordinates),
            // falling back to the parsed local name; require it to be a real station so its bus is valid.
            string? station = FindStationForDevice(device, idStationMap);
            string mp = terminalMPs.GetValueOrDefault(device.Acronym) ?? string.Empty;

            int deviceKV = device.Phasors
                .Select(phasor => ResolveVoltageKV(phasor, idPhasorMap))
                .Where(kv => kv > 0)
                .DefaultIfEmpty(0)
                .Max();

            LineParse? parsedLine = ParseLineFromDeviceName(device.Name, deviceKV);

            string local = station ?? (parsedLine is null ? string.Empty : NormalizeToID(parsedLine.FromStation));

            if (string.IsNullOrWhiteSpace(local) || !idStationMap.ContainsKey(local) || string.IsNullOrWhiteSpace(mp))
                continue;

            // PMU/inverter measurement points carry their own voltage and current together, so the
            // terminal uses the canonical station bus (no specific bus override).
            if (parsedLine is not null && parsedLine.NominalKV > 0)
            {
                terminals.Add(new Terminal(local, NormalizeToID(parsedLine.ToRemote), parsedLine.NominalKV, mp, FromDFR: false, LocalBusOverride: string.Empty));
            }
            else if (deviceKV > 0)
            {
                // A PMU/inverter device whose name does not parse a remote (e.g., a solar/wind
                // generation source) is a line with only one end filled, so its point is anchored.
                terminals.Add(new Terminal(local, string.Empty, deviceKV, mp, FromDFR: false, LocalBusOverride: string.Empty));
            }
        }

        foreach (DeviceRecord device in devices)
        {
            if (!IsDFRDevice(device.Acronym))
                continue;

            string? local = FindStationForDevice(device, idStationMap);

            if (local is null || !deviceDFRLinesMap.TryGetValue(device.Acronym, out List<DFRLineInfo>? dfrLines))
                continue;

            foreach (DFRLineInfo dfrLine in dfrLines)
            {
                string remote = NormalizeToID(dfrLine.LineName);

                // Bus labels become buses (handled separately) and transformer labels become
                // transformer lines (handled separately); neither is a line to another station.
                if (string.IsNullOrWhiteSpace(remote) || IsBusLabel(dfrLine.LineName) || IsTransformerLabel(dfrLine.LineName))
                    continue;

                // Prefer the voltage and paired bus from the phasor graph (authoritative) over the
                // description-derived voltage and the station/voltage bus heuristic.
                int pairedKv = 0;
                string pairedBusMP = string.Empty;

                if (lineVoltagePairing.TryGetValue(device.Acronym, out Dictionary<string, (int Kv, string PairedBusMP)>? deviceLineInfo) &&
                    deviceLineInfo.TryGetValue(NormalizeToID(dfrLine.LineName), out (int Kv, string PairedBusMP) lineInfo))
                {
                    pairedKv = lineInfo.Kv;
                    pairedBusMP = lineInfo.PairedBusMP;
                }

                int kv = pairedKv > 0
                    ? pairedKv
                    : dfrLine.VoltageKV > 0
                        ? dfrLine.VoltageKV
                        : device.Phasors.Select(phasor => ResolveVoltageKV(phasor, idPhasorMap)).Where(v => v > 0).DefaultIfEmpty(0).Max();

                string mp = string.IsNullOrWhiteSpace(dfrLine.MeasurementPoint)
                    ? terminalMPs.GetValueOrDefault(device.Acronym) ?? string.Empty
                    : dfrLine.MeasurementPoint;

                if (string.IsNullOrWhiteSpace(mp) || kv <= 0)
                    continue;

                terminals.Add(new Terminal(local, remote, kv, mp, FromDFR: true, LocalBusOverride: pairedBusMP));
            }
        }

        // Every measurement point considered as a line terminal (emitted or not). An orphan that
        // is in this set was a real terminal candidate that lost to another point at the same
        // terminal (a redundant duplicate); an orphan not in this set was never modelable as a line.
        HashSet<string> candidateTerminalMPs = new(terminals.Select(terminal => terminal.MeasurementPoint), StringComparer.OrdinalIgnoreCase);

        // 2) Each measurement point identifies one physical terminal: keep its first occurrence.
        List<Terminal> distinctTerminals = [];
        HashSet<string> seenMPs = new(StringComparer.OrdinalIgnoreCase);

        foreach (Terminal terminal in terminals)
        {
            if (seenMPs.Add(terminal.MeasurementPoint))
                distinctTerminals.Add(terminal);
        }

        // 3) Group terminals that share a real two-station pair; everything else is single-ended.
        Dictionary<string, PairGroup> pairGroups = new(StringComparer.OrdinalIgnoreCase);
        List<Terminal> singleEnded = [];

        // Remote endpoint names that did not resolve to any known station. Some are genuine
        // external/unmodeled stations; ones that closely resemble a known station are likely
        // source-data spelling typos and are reported as such.
        HashSet<string> unmatchedRemotes = new(StringComparer.OrdinalIgnoreCase);

        foreach (Terminal terminal in distinctTerminals)
        {
            string? remoteStation = string.IsNullOrWhiteSpace(terminal.RemoteRaw)
                ? null
                : FindKnownStation(terminal.RemoteRaw, idStationMap);

            if (remoteStation is not null && !remoteStation.Equals(terminal.LocalStationID, StringComparison.OrdinalIgnoreCase))
            {
                // Order the pair the same way BuildLineID does so the key is stable from either end.
                string upperLocal = terminal.LocalStationID.ToUpperInvariant();
                string upperRemote = remoteStation.ToUpperInvariant();
                (string stationA, string stationB) = string.Compare(upperLocal, upperRemote, StringComparison.Ordinal) <= 0
                    ? (upperLocal, upperRemote)
                    : (upperRemote, upperLocal);

                string key = $"{stationA}_{stationB}";

                if (!pairGroups.TryGetValue(key, out PairGroup? group))
                {
                    group = new PairGroup(stationA, stationB);
                    pairGroups[key] = group;
                }

                group.Terminals.Add(terminal);
            }
            else
            {
                if (!string.IsNullOrWhiteSpace(terminal.RemoteRaw) && remoteStation is null)
                    unmatchedRemotes.Add(terminal.RemoteRaw);

                singleEnded.Add(terminal);
            }
        }

        List<LineRow> lines = [];
        HashSet<string> usedLineIDs = new(StringComparer.OrdinalIgnoreCase);
        HashSet<string> usedMPs = new(StringComparer.OrdinalIgnoreCase);
        int dfrDerived = 0;

        // 3a) Two-station lines: each present terminal keeps its MP with its own station's bus;
        //     an unmeasured end contributes only its anchor bus.
        foreach ((string lineID, PairGroup group) in pairGroups)
        {
            int kv = group.Terminals.Max(terminal => terminal.NominalKV);

            Terminal? termA = group.Terminals.FirstOrDefault(terminal => terminal.LocalStationID.Equals(group.StationA, StringComparison.OrdinalIgnoreCase));
            Terminal? termB = group.Terminals.FirstOrDefault(terminal => terminal.LocalStationID.Equals(group.StationB, StringComparison.OrdinalIgnoreCase));

            string fromMP = termA?.MeasurementPoint ?? string.Empty;
            string toMP = termB?.MeasurementPoint ?? string.Empty;

            lines.Add(new LineRow(
                LineID: MakeUniqueLineID(lineID, usedLineIDs),
                FromTerminalMP: fromMP,
                ToTerminalMP: toMP,
                FromBusID: ResolveTerminalBus(termA?.LocalBusOverride, group.StationA, kv, idStationMap, idBusMap, buses, canonicalBus),
                ToBusID: ResolveTerminalBus(termB?.LocalBusOverride, group.StationB, kv, idStationMap, idBusMap, buses, canonicalBus),
                NominalVoltageKV: kv));

            if (!string.IsNullOrWhiteSpace(fromMP))
                usedMPs.Add(fromMP);

            if (!string.IsNullOrWhiteSpace(toMP))
                usedMPs.Add(toMP);

            if ((termA?.FromDFR ?? false) || (termB?.FromDFR ?? false))
                dfrDerived++;
        }

        // 3b) Single-ended lines: measured terminal (MP + its bus) on the From side, remote blank.
        foreach (Terminal terminal in singleEnded)
        {
            string baseID = string.IsNullOrWhiteSpace(terminal.RemoteRaw) || terminal.RemoteRaw.Equals(terminal.LocalStationID, StringComparison.OrdinalIgnoreCase)
                ? terminal.LocalStationID
                : BuildLineID(terminal.LocalStationID, terminal.RemoteRaw);

            lines.Add(new LineRow(
                LineID: MakeUniqueLineID(baseID, usedLineIDs),
                FromTerminalMP: terminal.MeasurementPoint,
                ToTerminalMP: string.Empty,
                FromBusID: ResolveTerminalBus(terminal.LocalBusOverride, terminal.LocalStationID, terminal.NominalKV, idStationMap, idBusMap, buses, canonicalBus),
                ToBusID: string.Empty,
                NominalVoltageKV: terminal.NominalKV));

            usedMPs.Add(terminal.MeasurementPoint);

            if (terminal.FromDFR)
                dfrDerived++;
        }

        // 4) Transformer lines: pair a same-device HS/LS transformer into a line between its two
        //    voltage buses (a transformer is a line between two voltage levels at one substation).
        dfrDerived += DeriveTransformerLines(devices, idStationMap, idBusMap, buses, deviceDFRLinesMap, terminalMPs, idPhasorMap, canonicalBus, usedMPs, lines, usedLineIDs, candidateTerminalMPs, lineVoltagePairing);

        return (lines.OrderBy(line => line.LineID, StringComparer.OrdinalIgnoreCase).ToList(), dfrDerived, usedMPs, unmatchedRemotes, candidateTerminalMPs);
    }

    /// <summary>
    /// Ensures a bus exists for the given station and voltage, creating it on demand (the bus
    /// is a notional voltage anchor for line terminals). Returns the bus identifier, or an
    /// empty string when the station is unknown or the voltage is invalid. When a single
    /// bus-voltage measurement point exists at the station and voltage, the bus is named after
    /// that measurement point so SynchroWave can use the measured voltage on connected lines.
    /// </summary>
    private static string EnsureBus(
        string stationID,
        int kv,
        Dictionary<string, StationRow> idStationMap,
        Dictionary<string, BusRow> idBusMap,
        List<BusRow> buses,
        Dictionary<string, string> canonicalBus)
    {
        if (string.IsNullOrWhiteSpace(stationID) || kv <= 0 || !idStationMap.ContainsKey(stationID))
            return string.Empty;

        string busID = CanonicalBusID(stationID, kv, canonicalBus);

        if (idBusMap.ContainsKey(busID))
            return busID;

        BusRow bus = new()
        {
            BusID = busID,
            StationID = stationID,
            NominalVoltageKV = kv
        };

        idBusMap[busID] = bus;
        buses.Add(bus);

        return busID;
    }

    /// <summary>
    /// Resolves the bus identifier for a station and voltage: the single bus-voltage measurement
    /// point name at that station/voltage when one exists (so connected lines reference the
    /// measured bus), otherwise the notional "{StationId}_{kv}_BUS" name.
    /// </summary>
    private static string CanonicalBusID(string stationID, int kv, Dictionary<string, string> canonicalBus)
    {
        return canonicalBus.GetValueOrDefault($"{stationID}|{kv}", $"{stationID}_{kv}_BUS");
    }

    /// <summary>
    /// For each device, maps a (phase-suffix-stripped, normalized) current phasor label to its
    /// resolved voltage level and — when that current's paired voltage (via
    /// <see cref="PhasorRecord.DestinationPhasorID"/>) is a bus — the measurement point of that bus.
    /// This is the authoritative current-to-voltage pairing the SEL power monitor needs: a line
    /// terminal's bus must be the bus whose voltage the terminal's current is actually paired with,
    /// which a station-and-voltage heuristic cannot determine when a station has several buses at
    /// one voltage.
    /// </summary>
    private static Dictionary<string, Dictionary<string, (int Kv, string PairedBusMP)>> BuildDeviceLineVoltagePairing(
        List<DeviceRecord> devices,
        Dictionary<string, List<DFRLineInfo>> deviceDFRLinesMap,
        Dictionary<int, PhasorRecord> idPhasorMap)
    {
        Dictionary<string, Dictionary<string, (int, string)>> result = new(StringComparer.OrdinalIgnoreCase);

        foreach (DeviceRecord device in devices)
        {
            // Bus label (normalized) -> bus measurement point for this device. The signal-mapping
            // description label can differ from the Phasor.Label that DestinationPhasorID resolves
            // to (e.g., description "115kV_BUS"/"230_EAST_BUS" vs Phasor.Label "OP_BUS"/"EAST_BUS"),
            // so several keys are registered to make the lookup robust.
            Dictionary<string, string> busLabelToMP = new(StringComparer.OrdinalIgnoreCase);
            HashSet<string> distinctBusMPs = new(StringComparer.OrdinalIgnoreCase);

            if (deviceDFRLinesMap.TryGetValue(device.Acronym, out List<DFRLineInfo>? dfrLines))
            {
                foreach (DFRLineInfo dfrLine in dfrLines)
                {
                    if (!IsBusLabel(dfrLine.LineName) || string.IsNullOrWhiteSpace(dfrLine.MeasurementPoint))
                        continue;

                    distinctBusMPs.Add(dfrLine.MeasurementPoint);

                    string busKey = NormalizeToID(dfrLine.LineName);

                    foreach (string key in new[] { busKey, StripVoltagePrefix(busKey) })
                    {
                        if (key.Length > 0 && !busLabelToMP.ContainsKey(key))
                            busLabelToMP[key] = dfrLine.MeasurementPoint;
                    }
                }
            }

            // When a device has exactly one bus, every bus-voltage phasor label maps to that bus
            // point — this covers Phasor.Label spellings that the description-derived keys missed.
            if (distinctBusMPs.Count == 1)
            {
                string onlyBusMP = distinctBusMPs.First();

                foreach (PhasorRecord phasor in device.Phasors)
                {
                    if (!IsBusLabel(phasor.Label))
                        continue;

                    string key = NormalizeToID(StripPhaseSuffix(phasor.Label));

                    if (key.Length > 0)
                        busLabelToMP.TryAdd(key, onlyBusMP);
                }
            }

            Dictionary<string, (int, string)> labelInfo = new(StringComparer.OrdinalIgnoreCase);

            foreach (PhasorRecord phasor in device.Phasors)
            {
                if (char.ToUpperInvariant(phasor.Type) != 'I')
                    continue;

                string key = NormalizeToID(StripPhaseSuffix(phasor.Label));

                if (key.Length == 0)
                    continue;

                int kv = ResolveVoltageKV(phasor, idPhasorMap);
                string pairedBusMP = string.Empty;

                if (phasor.DestinationPhasorID.HasValue &&
                    idPhasorMap.TryGetValue(phasor.DestinationPhasorID.Value, out PhasorRecord? destinationPhasor))
                {
                    string destinationKey = NormalizeToID(StripPhaseSuffix(destinationPhasor.Label));

                    // The current is paired with a bus voltage (not its own line voltage).
                    if (!destinationKey.Equals(key, StringComparison.OrdinalIgnoreCase) && IsBusLabel(destinationPhasor.Label))
                    {
                        pairedBusMP = busLabelToMP.GetValueOrDefault(destinationKey, string.Empty);

                        if (string.IsNullOrWhiteSpace(pairedBusMP))
                            pairedBusMP = busLabelToMP.GetValueOrDefault(StripVoltagePrefix(destinationKey), string.Empty);
                    }
                }

                if (labelInfo.TryGetValue(key, out (int Kv, string PairedBusMP) existing))
                {
                    labelInfo[key] = (
                        Math.Max(existing.Kv, kv),
                        string.IsNullOrWhiteSpace(existing.PairedBusMP) ? pairedBusMP : existing.PairedBusMP);
                }
                else
                {
                    labelInfo[key] = (kv, pairedBusMP);
                }
            }

            result[device.Acronym] = labelInfo;
        }

        return result;
    }

    /// <summary>
    /// Ensures a bus row exists for an explicitly named bus — the bus-voltage measurement point that
    /// a line current is paired with — creating it on demand. Returns the bus identifier, or an
    /// empty string when the station or voltage is invalid.
    /// </summary>
    private static string EnsureNamedBus(
        string busID,
        string stationID,
        int kv,
        Dictionary<string, StationRow> idStationMap,
        Dictionary<string, BusRow> idBusMap,
        List<BusRow> buses)
    {
        if (string.IsNullOrWhiteSpace(busID) || string.IsNullOrWhiteSpace(stationID) || kv <= 0 || !idStationMap.ContainsKey(stationID))
            return string.Empty;

        if (!idBusMap.ContainsKey(busID))
        {
            BusRow bus = new() { BusID = busID, StationID = stationID, NominalVoltageKV = kv };
            idBusMap[busID] = bus;
            buses.Add(bus);
        }

        return busID;
    }

    /// <summary>
    /// Resolves a line terminal's bus: the specific bus its current is paired with when known
    /// (created on demand), otherwise the canonical station/voltage bus.
    /// </summary>
    private static string ResolveTerminalBus(
        string? busOverride,
        string stationID,
        int kv,
        Dictionary<string, StationRow> idStationMap,
        Dictionary<string, BusRow> idBusMap,
        List<BusRow> buses,
        Dictionary<string, string> canonicalBus)
    {
        return string.IsNullOrWhiteSpace(busOverride) ? 
            EnsureBus(stationID, kv, idStationMap, idBusMap, buses, canonicalBus) : 
            EnsureNamedBus(busOverride, stationID, kv, idStationMap, idBusMap, buses);
    }

    // ========= Possible-typo diagnostics =========

    /// <summary>
    /// Flags remote endpoint names that did not match a station but are within one edit of a known
    /// station (e.g. a doubled letter), and pairs of known stations that are within one edit of each
    /// other. These are likely source-data spelling inconsistencies that split one physical asset
    /// across two identifiers; they are reported for review rather than merged automatically.
    /// </summary>
    /// <param name="unmatchedRemotes">Remote endpoint names that did not resolve to a known station.</param>
    /// <param name="idStationMap">A dictionary mapping station IDs to station records.</param>
    /// <returns>A list of human-readable "looks like a typo" messages.</returns>
    private static List<string> FindPossibleTypos(IEnumerable<string> unmatchedRemotes, Dictionary<string, StationRow> idStationMap)
    {
        // Restrict to reasonably long names so short, legitimately-similar identifiers are not flagged.
        const int MinLength = 5;

        List<string> stations = idStationMap.Keys.Where(station => station.Length >= MinLength).ToList();
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        List<string> messages = [];

        foreach (string remote in unmatchedRemotes.Where(remote => remote.Length >= MinLength).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            foreach (string station in stations)
            {
                if (remote.Equals(station, StringComparison.OrdinalIgnoreCase))
                    continue;

                if (!WithinOneEdit(remote, station))
                    continue;
                
                string message = $"line endpoint \"{remote}\" closely resembles station \"{station}\"";

                if (seen.Add(message))
                    messages.Add(message);
            }
        }

        for (int i = 0; i < stations.Count; i++)
        {
            for (int j = i + 1; j < stations.Count; j++)
            {
                if (!WithinOneEdit(stations[i], stations[j]))
                    continue;
                
                string message = $"stations \"{stations[i]}\" and \"{stations[j]}\" closely resemble each other";

                if (seen.Add(message))
                    messages.Add(message);
            }
        }

        return messages;
    }

    /// <summary>
    /// Returns <c>true</c> when two identifiers differ by at most one single-character edit
    /// (insertion, deletion, or substitution), compared case-insensitively.
    /// </summary>
    private static bool WithinOneEdit(string a, string b)
    {
        a = a.ToUpperInvariant();
        b = b.ToUpperInvariant();

        int lengthA = a.Length, lengthB = b.Length;

        if (Math.Abs(lengthA - lengthB) > 1)
            return false;

        if (lengthA == lengthB)
        {
            int differences = 0;

            for (int i = 0; i < lengthA; i++)
            {
                if (a[i] != b[i] && ++differences > 1)
                    return false;
            }

            return differences == 1;
        }

        // Lengths differ by one: confirm the shorter is the longer with a single character removed.
        string shorter = lengthA < lengthB ? a : b;
        string longer = lengthA < lengthB ? b : a;

        int s = 0, l = 0;
        bool skipped = false;

        while (s < shorter.Length && l < longer.Length)
        {
            if (shorter[s] == longer[l])
            {
                s++;
                l++;
            }
            else if (skipped)
            {
                return false;
            }
            else
            {
                skipped = true;
                l++;
            }
        }

        return true;
    }

    // ========= Bus measurement points and transformers =========

    /// <summary>
    /// Builds, per device acronym, a map from a phasor label (phase suffix stripped, uppercased)
    /// to the maximum resolved voltage level for that label. Used to assign the correct voltage to
    /// bus and transformer terminals whose descriptions do not carry an explicit kV.
    /// </summary>
    private static Dictionary<string, Dictionary<string, int>> BuildDeviceLabelVoltages(
        List<DeviceRecord> devices,
        Dictionary<int, PhasorRecord> idPhasorMap)
    {
        Dictionary<string, Dictionary<string, int>> result = new(StringComparer.OrdinalIgnoreCase);

        foreach (DeviceRecord device in devices)
        {
            Dictionary<string, int> labelKV = new(StringComparer.OrdinalIgnoreCase);

            foreach (PhasorRecord phasor in device.Phasors)
            {
                string label = StripPhaseSuffix(phasor.Label).Trim().ToUpperInvariant();

                if (label.Length == 0)
                    continue;

                int kv = ResolveVoltageKV(phasor, idPhasorMap);

                if (kv > 0 && kv > labelKV.GetValueOrDefault(label))
                    labelKV[label] = kv;
            }

            result[device.Acronym] = labelKV;
        }

        return result;
    }

    /// <summary>
    /// Resolves the voltage for a DFR line/label entry, preferring the per-label phasor voltage,
    /// then the voltage parsed from the description, then the device's maximum voltage.
    /// </summary>
    private static int ResolveLabelVoltage(
        DeviceRecord device,
        DFRLineInfo dfrLine,
        Dictionary<string, Dictionary<string, int>> deviceLabelVoltages,
        Dictionary<int, PhasorRecord> idPhasorMap)
    {
        if (deviceLabelVoltages.TryGetValue(device.Acronym, out Dictionary<string, int>? labelKV) &&
            labelKV.TryGetValue(dfrLine.LineName.Trim().ToUpperInvariant(), out int labelVoltage) && labelVoltage > 0)
            return labelVoltage;

        if (dfrLine.VoltageKV > 0)
            return dfrLine.VoltageKV;

        return device.Phasors
            .Select(phasor => ResolveVoltageKV(phasor, idPhasorMap))
            .Where(kv => kv > 0)
            .DefaultIfEmpty(0)
            .Max();
    }

    /// <summary>
    /// Collects bus-voltage measurement points (DFR labels denoting a bus) as (station, kV,
    /// measurement point) tuples for use as named buses.
    /// </summary>
    private static List<(string Station, int KV, string MeasurementPoint)> CollectBusMeasurementPoints(
        List<DeviceRecord> devices,
        Dictionary<string, StationRow> idStationMap,
        Dictionary<string, List<DFRLineInfo>> deviceDFRLinesMap,
        Dictionary<string, Dictionary<string, int>> deviceLabelVoltages,
        Dictionary<int, PhasorRecord> idPhasorMap)
    {
        List<(string, int, string)> result = [];

        foreach (DeviceRecord device in devices)
        {
            string? station = FindStationForDevice(device, idStationMap);

            if (station is null || !deviceDFRLinesMap.TryGetValue(device.Acronym, out List<DFRLineInfo>? dfrLines))
                continue;

            foreach (DFRLineInfo dfrLine in dfrLines)
            {
                if (!IsBusLabel(dfrLine.LineName) || string.IsNullOrWhiteSpace(dfrLine.MeasurementPoint))
                    continue;

                int kv = ResolveLabelVoltage(device, dfrLine, deviceLabelVoltages, idPhasorMap);

                if (kv > 0)
                    result.Add((station, kv, dfrLine.MeasurementPoint));
            }
        }

        return result;
    }

    /// <summary>
    /// Builds the canonical bus map: for each station and voltage that has exactly one bus-voltage
    /// measurement point, the bus is named after that point so connected lines reference the
    /// measured bus. Stations/voltages with multiple bus points keep the notional bus name.
    /// </summary>
    private static Dictionary<string, string> BuildCanonicalBusMap(List<(string Station, int KV, string MeasurementPoint)> busMeasurementPoints)
    {
        Dictionary<string, HashSet<string>> byStationVoltage = new(StringComparer.OrdinalIgnoreCase);

        foreach ((string station, int kv, string mp) in busMeasurementPoints)
        {
            string key = $"{station}|{kv}";

            if (!byStationVoltage.TryGetValue(key, out HashSet<string>? set))
            {
                set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                byStationVoltage[key] = set;
            }

            set.Add(mp);
        }

        Dictionary<string, string> canonical = new(StringComparer.OrdinalIgnoreCase);

        foreach ((string key, HashSet<string> mps) in byStationVoltage)
        {
            if (mps.Count == 1)
                canonical[key] = mps.First();
        }

        return canonical;
    }

    /// <summary>
    /// Ensures a bus row exists for every bus-voltage measurement point, named after the point
    /// (SEL guidance: a bus measurement point must have a matching bus identifier). The single
    /// point per station/voltage is already created by line derivation via the canonical map;
    /// this adds the remaining points (where several buses exist at one station/voltage).
    /// </summary>
    private static void AddBusMeasurementPointRows(
        List<(string Station, int KV, string MeasurementPoint)> busMeasurementPoints,
        Dictionary<string, StationRow> idStationMap,
        Dictionary<string, BusRow> idBusMap,
        List<BusRow> buses)
    {
        foreach ((string station, int kv, string mp) in busMeasurementPoints)
        {
            if (!idStationMap.ContainsKey(station) || idBusMap.ContainsKey(mp))
                continue;

            BusRow bus = new()
            {
                BusID = mp,
                StationID = station,
                NominalVoltageKV = kv
            };

            idBusMap[mp] = bus;
            buses.Add(bus);
        }
    }

    /// <summary>
    /// A transformer winding terminal collected during transformer line derivation.
    /// </summary>
    private sealed record TransformerTerminal(
        string DeviceAcronym,
        string Station,
        string Key,
        string Side,
        int NominalKV,
        string MeasurementPoint,
        string BusOverride);

    /// <summary>
    /// Derives transformer lines. A transformer is modeled as a line between two bus voltage
    /// levels at the same substation. High-side and low-side terminals measured by the same
    /// device with distinct voltages are paired into one two-terminal transformer line; any other
    /// transformer terminal is anchored as a single-ended line.
    /// </summary>
    /// <returns>The number of transformer lines added.</returns>
    private static int DeriveTransformerLines(
        List<DeviceRecord> devices,
        Dictionary<string, StationRow> idStationMap,
        Dictionary<string, BusRow> idBusMap,
        List<BusRow> buses,
        Dictionary<string, List<DFRLineInfo>> deviceDFRLinesMap,
        Dictionary<string, string> terminalMPs,
        Dictionary<int, PhasorRecord> idPhasorMap,
        Dictionary<string, string> canonicalBus,
        HashSet<string> usedMeasurementPoints,
        List<LineRow> lines,
        HashSet<string> usedLineIDs,
        HashSet<string> candidateTerminalMPs,
        Dictionary<string, Dictionary<string, (int Kv, string PairedBusMP)>> lineVoltagePairing)
    {
        Dictionary<string, Dictionary<string, int>> deviceLabelVoltages = BuildDeviceLabelVoltages(devices, idPhasorMap);

        // Collect transformer terminals (one per transformer winding), de-duplicated by point.
        List<TransformerTerminal> terminals = [];
        HashSet<string> seen = new(usedMeasurementPoints, StringComparer.OrdinalIgnoreCase);

        foreach (DeviceRecord device in devices)
        {
            string? station = FindStationForDevice(device, idStationMap);

            if (station is null || !deviceDFRLinesMap.TryGetValue(device.Acronym, out List<DFRLineInfo>? dfrLines))
                continue;

            foreach (DFRLineInfo dfrLine in dfrLines)
            {
                if (!IsTransformerLabel(dfrLine.LineName))
                    continue;

                string mp = string.IsNullOrWhiteSpace(dfrLine.MeasurementPoint) ? 
                    terminalMPs.GetValueOrDefault(device.Acronym) ?? string.Empty : 
                    dfrLine.MeasurementPoint;

                if (string.IsNullOrWhiteSpace(mp) || !seen.Add(mp))
                    continue;

                int kv = ResolveLabelVoltage(device, dfrLine, deviceLabelVoltages, idPhasorMap);

                if (kv <= 0)
                    continue;

                string busOverride = string.Empty;

                if (lineVoltagePairing.TryGetValue(device.Acronym, out Dictionary<string, (int Kv, string PairedBusMP)>? deviceLineInfo) &&
                    deviceLineInfo.TryGetValue(NormalizeToID(dfrLine.LineName), out (int Kv, string PairedBusMP) lineInfo))
                    busOverride = lineInfo.PairedBusMP;

                candidateTerminalMPs.Add(mp);
                terminals.Add(new TransformerTerminal(device.Acronym, station, TransformerKey(dfrLine.LineName), TransformerSide(dfrLine.LineName), kv, mp, busOverride));
            }
        }

        // Group by the same device and transformer identity so windings of one transformer pair up.
        Dictionary<string, List<TransformerTerminal>> groups = new(StringComparer.OrdinalIgnoreCase);

        foreach (TransformerTerminal terminal in terminals)
        {
            string key = $"{terminal.DeviceAcronym}|{terminal.Key}";

            if (!groups.TryGetValue(key, out List<TransformerTerminal>? list))
            {
                list = [];
                groups[key] = list;
            }

            list.Add(terminal);
        }

        int added = 0;

        foreach ((string _, List<TransformerTerminal> group) in groups)
        {
            TransformerTerminal? high = group.Where(terminal => terminal.Side == "HS").OrderByDescending(terminal => terminal.NominalKV).FirstOrDefault();
            TransformerTerminal? low = group.Where(terminal => terminal.Side == "LS").OrderBy(terminal => terminal.NominalKV).FirstOrDefault();

            List<TransformerTerminal> remaining = group;

            // A clean two-voltage transformer: pair the high and low windings into one line
            // between the two bus voltage levels at the substation.
            if (high is not null && low is not null && high.NominalKV != low.NominalKV)
            {
                lines.Add(new LineRow(
                    LineID: MakeUniqueLineID($"{high.Station}_{high.Key}_XFMR", usedLineIDs),
                    FromTerminalMP: high.MeasurementPoint,
                    ToTerminalMP: low.MeasurementPoint,
                    FromBusID: ResolveTerminalBus(high.BusOverride, high.Station, high.NominalKV, idStationMap, idBusMap, buses, canonicalBus),
                    ToBusID: ResolveTerminalBus(low.BusOverride, low.Station, low.NominalKV, idStationMap, idBusMap, buses, canonicalBus),
                    NominalVoltageKV: high.NominalKV));

                usedMeasurementPoints.Add(high.MeasurementPoint);
                usedMeasurementPoints.Add(low.MeasurementPoint);
                added++;

                remaining = group.Where(terminal => terminal != high && terminal != low).ToList();
            }

            // Any remaining transformer terminal is anchored on its own (single-ended) line.
            foreach (TransformerTerminal terminal in remaining)
            {
                string sideSuffix = terminal.Side.Length > 0 ? $"_{terminal.Side}" : string.Empty;

                lines.Add(new LineRow(
                    LineID: MakeUniqueLineID($"{terminal.Station}_{terminal.Key}{sideSuffix}", usedLineIDs),
                    FromTerminalMP: terminal.MeasurementPoint,
                    ToTerminalMP: string.Empty,
                    FromBusID: ResolveTerminalBus(terminal.BusOverride, terminal.Station, terminal.NominalKV, idStationMap, idBusMap, buses, canonicalBus),
                    ToBusID: string.Empty,
                    NominalVoltageKV: terminal.NominalKV));

                usedMeasurementPoints.Add(terminal.MeasurementPoint);
                added++;
            }
        }

        return added;
    }

    /// <summary>
    /// Resolves a remote endpoint name to a known station only on a confident match (exact, or
    /// equal after removing underscores and case). Loose substring matching is intentionally
    /// avoided here so that an asset name (such as a generator or unit) is not mistaken for a
    /// remote station, which would create a line whose two ends share a substation.
    /// </summary>
    private static string? FindKnownStation(string remoteID, Dictionary<string, StationRow> idStationMap)
    {
        if (string.IsNullOrWhiteSpace(remoteID))
            return null;

        if (idStationMap.ContainsKey(remoteID))
            return remoteID;

        string normalizedRemote = remoteID.Replace("_", string.Empty).ToUpperInvariant();

        foreach (string stationID in idStationMap.Keys)
        {
            if (stationID.Replace("_", string.Empty).ToUpperInvariant() == normalizedRemote)
                return stationID;
        }

        return null;
    }

    /// <summary>
    /// Returns the base line identifier if unused, otherwise appends a numeric suffix to keep
    /// every line identifier unique (line identifiers are also their display names).
    /// </summary>
    private static string MakeUniqueLineID(string baseID, HashSet<string> usedLineIDs)
    {
        if (usedLineIDs.Add(baseID))
            return baseID;

        for (int n = 2; ; n++)
        {
            string candidate = $"{baseID}_{n}";

            if (usedLineIDs.Add(candidate))
                return candidate;
        }
    }

    /// <summary>
    /// Sets each station's nominal voltage to the maximum voltage among its buses, per the
    /// power system model rule that a station's nominal voltage is the highest of its buses.
    /// Accounts for buses generated on demand during line derivation.
    /// </summary>
    /// <param name="stations">The station records to reconcile.</param>
    /// <param name="buses">The bus records (including any generated on demand).</param>
    private static void ReconcileStationVoltages(List<StationRow> stations, List<BusRow> buses)
    {
        Dictionary<string, int> maxByStation = new(StringComparer.OrdinalIgnoreCase);

        foreach (BusRow bus in buses)
        {
            if (bus.NominalVoltageKV > maxByStation.GetValueOrDefault(bus.StationID))
                maxByStation[bus.StationID] = bus.NominalVoltageKV;
        }

        foreach (StationRow station in stations)
        {
            if (maxByStation.TryGetValue(station.StationID, out int maxKV) && maxKV > station.NominalVoltageKV)
                station.NominalVoltageKV = maxKV;
        }
    }

    /// <summary>
    /// A measurement point's representative description and which quantity families it carries.
    /// </summary>
    private sealed record MeasurementPointInfo(string Description, bool HasVoltage, bool HasCurrent);

    /// <summary>
    /// Builds, from the STTP signal mappings, each distinct measurement point with a representative
    /// description and whether it carries voltage and/or current quantities. Used to report and
    /// classify measurement points that are not associated with the model.
    /// </summary>
    /// <param name="signalMappings">The STTP signal mappings produced by the configuration export.</param>
    /// <returns>A dictionary mapping each measurement point to its description and quantity content.</returns>
    private static Dictionary<string, MeasurementPointInfo> BuildMeasurementPointInfo(IReadOnlyList<SttpConfigExporter.SignalMapping> signalMappings)
    {
        Dictionary<string, string> descriptions = new(StringComparer.OrdinalIgnoreCase);
        HashSet<string> hasVoltage = new(StringComparer.OrdinalIgnoreCase);
        HashSet<string> hasCurrent = new(StringComparer.OrdinalIgnoreCase);

        foreach (SttpConfigExporter.SignalMapping mapping in signalMappings)
        {
            string mp = mapping.MeasurementPoint;

            if (string.IsNullOrWhiteSpace(mp))
                continue;

            if (!descriptions.ContainsKey(mp))
                descriptions[mp] = mapping.Description;

            if (mapping.Quantity.Contains("Voltage", StringComparison.OrdinalIgnoreCase))
                hasVoltage.Add(mp);
            else if (mapping.Quantity.Contains("Current", StringComparison.OrdinalIgnoreCase))
                hasCurrent.Add(mp);
        }

        Dictionary<string, MeasurementPointInfo> result = new(StringComparer.OrdinalIgnoreCase);

        foreach ((string mp, string description) in descriptions)
            result[mp] = new MeasurementPointInfo(description, hasVoltage.Contains(mp), hasCurrent.Contains(mp));

        return result;
    }

    /// <summary>
    /// Classifies measurement points anchored to neither a line terminal nor a bus. Distinguishes
    /// "redundant duplicates" — points that were a real terminal candidate but lost to another
    /// point at the same, already-modeled terminal (correct to omit) — from "genuine gaps":
    /// complete (voltage + current) terminals that were never modelable, which usually signals a
    /// source-data issue such as a device with no resolvable voltage.
    /// </summary>
    /// <param name="allMeasurementPoints">All measurement points with description and quantity content.</param>
    /// <param name="anchoredMeasurementPoints">Points used as a line terminal or present as a bus identifier.</param>
    /// <param name="candidateTerminalMPs">Points considered as a line/transformer terminal (emitted or dropped).</param>
    /// <returns>The unanchored count, a category summary, and the explicit list of genuine V+I gaps.</returns>
    private static (int Count, string Summary, List<string> GenuineGaps) AnalyzeOrphanMeasurementPoints(
        Dictionary<string, MeasurementPointInfo> allMeasurementPoints,
        HashSet<string> anchoredMeasurementPoints,
        HashSet<string> candidateTerminalMPs)
    {
        int redundant = 0, genuineVI = 0, currentOnly = 0, voltageOnly = 0, other = 0, count = 0;
        List<string> genuineGaps = [];

        foreach ((string mp, MeasurementPointInfo info) in allMeasurementPoints)
        {
            if (anchoredMeasurementPoints.Contains(mp))
                continue;

            count++;

            if (candidateTerminalMPs.Contains(mp))
            {
                // Was a real terminal candidate; its terminal is already represented by another point.
                redundant++;
            }
            else if (info is { HasVoltage: true, HasCurrent: true })
            {
                // A complete terminal that was never modelable — the actionable gap.
                genuineVI++;
                genuineGaps.Add($"{mp} (V+I) — {info.Description}");
            }
            else if (info.HasCurrent)
            {
                currentOnly++;
            }
            else if (info.HasVoltage)
            {
                voltageOnly++;
            }
            else
            {
                other++;
            }
        }

        string summary = $"redundant duplicates={redundant}, genuine gaps (V+I, unmodeled)={genuineVI}, " +
                         $"current-only={currentOnly}, voltage-only={voltageOnly}, other={other}";

        return (count, summary, genuineGaps);
    }

    /// <summary>
    /// Removes notional buses (those not named after a measured bus-voltage point) that no line
    /// references, provided their station retains at least one bus. These appear when a station has
    /// several measured buses at one voltage: the per-voltage notional bus is created but every line
    /// references a specific measured bus instead, leaving the notional bus empty and unused.
    /// </summary>
    private static void PruneUnreferencedNotionalBuses(
        List<BusRow> buses,
        Dictionary<string, BusRow> idBusMap,
        List<LineRow> lines,
        HashSet<string> busMeasurementPointNames)
    {
        HashSet<string> referenced = new(StringComparer.OrdinalIgnoreCase);

        foreach (LineRow line in lines)
        {
            if (!string.IsNullOrWhiteSpace(line.FromBusID))
                referenced.Add(line.FromBusID);

            if (!string.IsNullOrWhiteSpace(line.ToBusID))
                referenced.Add(line.ToBusID);
        }

        HashSet<string> stationsWithKeeper = new(StringComparer.OrdinalIgnoreCase);

        foreach (BusRow bus in buses)
        {
            if (referenced.Contains(bus.BusID) || busMeasurementPointNames.Contains(bus.BusID))
                stationsWithKeeper.Add(bus.StationID);
        }

        buses.RemoveAll(bus =>
        {
            bool prune = !busMeasurementPointNames.Contains(bus.BusID) &&
                         !referenced.Contains(bus.BusID) &&
                         stationsWithKeeper.Contains(bus.StationID);

            if (prune)
                idBusMap.Remove(bus.BusID);

            return prune;
        });
    }

    /// <summary>
    /// Identifies line terminals that cannot yield a power calculation because their current has no
    /// usable voltage source. A terminal's current can be paired with a voltage when its own
    /// measurement point carries voltage, or when its bus has an observable voltage — either the bus
    /// is itself a measured voltage point, or some line terminal on that bus carries voltage (a line
    /// VT senses the bus voltage, which SEL uses as the adjacent-bus voltage for the other terminals
    /// on that bus). Reports the terminals with no voltage source at all so coverage is visible.
    /// </summary>
    private static List<string> AnalyzePowerCalcCoverage(
        List<LineRow> lines,
        Dictionary<string, MeasurementPointInfo> allMeasurementPoints,
        HashSet<string> busMeasurementPointNames)
    {
        // A bus's voltage is observable if the bus is a measured voltage point, or if any line
        // terminal sitting on it carries its own voltage (its VT senses the shared bus voltage).
        HashSet<string> busHasVoltage = new(busMeasurementPointNames, StringComparer.OrdinalIgnoreCase);

        foreach (LineRow line in lines)
        {
            if (!string.IsNullOrWhiteSpace(line.FromBusID) && !string.IsNullOrWhiteSpace(line.FromTerminalMP) && hasOwnVoltage(line.FromTerminalMP))
                busHasVoltage.Add(line.FromBusID);

            if (!string.IsNullOrWhiteSpace(line.ToBusID) && !string.IsNullOrWhiteSpace(line.ToTerminalMP) && hasOwnVoltage(line.ToTerminalMP))
                busHasVoltage.Add(line.ToBusID);
        }

        List<string> gaps = [];

        foreach (LineRow line in lines)
        {
            checkSide(line.LineID, "From", line.FromTerminalMP, line.FromBusID);
            checkSide(line.LineID, "To", line.ToTerminalMP, line.ToBusID);
        }

        return gaps;

        bool hasOwnVoltage(string mp)
        {
            return allMeasurementPoints.TryGetValue(mp, out MeasurementPointInfo? info) && info.HasVoltage;
        }

        void checkSide(string lineID, string side, string mp, string busID)
        {
            if (string.IsNullOrWhiteSpace(mp))
                return;

            if (hasOwnVoltage(mp) || (!string.IsNullOrWhiteSpace(busID) && busHasVoltage.Contains(busID)))
                return;

            gaps.Add($"{lineID} [{side}]: {mp} current has no voltage source (bus {(string.IsNullOrWhiteSpace(busID) ? "<none>" : busID)})");
        }
    }

    /// <summary>
    /// Validates the derived model against the SEL power system model rules and returns a list
    /// of human-readable violation messages (empty when the model is consistent): a terminal
    /// measurement point must have a same-side bus; a line's two ends must be different buses at
    /// different stations; each measurement point appears at most once; bus identifiers and
    /// measurement points stay disjoint; every station has a bus and every bus has a station.
    /// </summary>
    /// <param name="stations">The station records.</param>
    /// <param name="buses">The bus records.</param>
    /// <param name="lines">The line records.</param>
    /// <returns>A list of violation messages, empty when the model is consistent.</returns>
    private static List<string> CheckModelInvariants(List<StationRow> stations, List<BusRow> buses, List<LineRow> lines)
    {
        List<string> violations = [];

        HashSet<string> stationIDs = new(stations.Select(station => station.StationID), StringComparer.OrdinalIgnoreCase);
        HashSet<string> busIDs = new(buses.Select(bus => bus.BusID), StringComparer.OrdinalIgnoreCase);
        HashSet<string> stationsWithBus = new(buses.Select(bus => bus.StationID), StringComparer.OrdinalIgnoreCase);

        foreach (string stationID in stationIDs)
        {
            if (!stationsWithBus.Contains(stationID))
                violations.Add($"station has no bus: {stationID}");
        }

        foreach (BusRow bus in buses)
        {
            if (!stationIDs.Contains(bus.StationID))
                violations.Add($"bus references missing station: {bus.BusID}");
        }

        Dictionary<string, int> mpUses = new(StringComparer.OrdinalIgnoreCase);

        foreach (LineRow line in lines)
        {
            bool hasFromBus = !string.IsNullOrWhiteSpace(line.FromBusID);
            bool hasToBus = !string.IsNullOrWhiteSpace(line.ToBusID);

            if (hasFromBus && hasToBus && line.FromBusID.Equals(line.ToBusID, StringComparison.OrdinalIgnoreCase))
                violations.Add($"line from-bus equals to-bus: {line.LineID}");

            // Note: a line whose two buses are at the same substation but different voltages is a
            // valid transformer; only identical From/To buses (above) are rejected.

            if (!string.IsNullOrWhiteSpace(line.FromTerminalMP) && !hasFromBus)
                violations.Add($"from measurement point without a bus: {line.LineID}");

            if (!string.IsNullOrWhiteSpace(line.ToTerminalMP) && !hasToBus)
                violations.Add($"to measurement point without a bus: {line.LineID}");

            foreach (string busID in new[] { line.FromBusID, line.ToBusID })
            {
                if (!string.IsNullOrWhiteSpace(busID) && !busIDs.Contains(busID))
                    violations.Add($"line references missing bus {busID}: {line.LineID}");
            }

            foreach (string mp in new[] { line.FromTerminalMP, line.ToTerminalMP })
            {
                if (string.IsNullOrWhiteSpace(mp))
                    continue;

                mpUses[mp] = mpUses.GetValueOrDefault(mp) + 1;

                if (busIDs.Contains(mp))
                    violations.Add($"measurement point equals a bus id: {mp}");
            }
        }

        foreach ((string mp, int uses) in mpUses)
        {
            if (uses > 1)
                violations.Add($"measurement point used {uses} times: {mp}");
        }

        return violations;
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

    // ========= CSV writing =========

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