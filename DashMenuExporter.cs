//******************************************************************************************************
//  DashMenuExporter.cs - Gbtc
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
/// Exports a dash menu file containing hierarchical folder paths for stations and lines
/// derived from the power system model CSV files.
/// </summary>
/// <remarks>
/// <para>
/// The dash menu file is a text file containing folder-like paths organized into a hierarchy
/// for use in dashboard navigation. The structure follows:
/// </para>
/// <list type="bullet">
///   <item>/Templates/Assets/Substations/{StationId} - One entry per station</item>
///   <item>/Templates/Assets/{kV} kV Lines/{LineId} - Lines grouped by voltage level</item>
/// </list>
/// </remarks>
public static class DashMenuExporter
{
    // ========= Public API =========

    /// <summary>
    /// Exports a dash menu file containing hierarchical folder paths for stations and lines.
    /// </summary>
    /// <returns>Result containing export statistics including counts of paths generated.</returns>
    /// <exception cref="ArgumentException">Thrown when DashMenuPath setting is null or whitespace.</exception>
    /// <remarks>
    /// This method reads the previously exported power system model CSV files (stations, buses, lines)
    /// and generates a hierarchical dash menu file for dashboard navigation.
    /// </remarks>
    public static DashMenuExportResult Export()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(Settings.DashMenuPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(Settings.StationsCsvPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(Settings.BusesCsvPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(Settings.LinesCsvPath);

        // Load power system model data from CSV files
        List<StationRecord> stations = LoadStations();
        List<BusRecord> buses = LoadBuses();
        List<LineRecord> lines = LoadLines();

        // Build the hierarchical path list (order matters for proper menu structure)
        List<string> paths =
        [
            // Add root paths first
            "/",
            "/Templates",
            "/Templates/Assets",
            // Add substations section (must appear before line sections)
            "/Templates/Assets/Substations"
        ];

        int substationPaths = 0;

        // Add all substation paths
        foreach (StationRecord station in stations.OrderBy(s => s.StationId))
        {
            paths.Add($"/Templates/Assets/Substations/{station.StationId}");
            substationPaths++;
        }

        // Now add optional line sections (each controlled by a setting)
        int voltageLevelPaths = 0;
        int linePaths = 0;

        // Add voltage level group headers and lines (if enabled)
        if (Settings.IncludeVoltageGroupedLines)
        {
            // Get distinct voltage levels for line groupings
            HashSet<int> voltageLevels = new(lines.Select(line => line.NominalVoltageKV).Where(kv => kv > 0));

            foreach (int kv in voltageLevels.OrderDescending())
            {
                string voltageGroupPath = $"/Templates/Assets/{kv} kV Lines";
                paths.Add(voltageGroupPath);
                voltageLevelPaths++;

                // Add lines at this voltage level
                foreach (LineRecord line in lines.Where(line => line.NominalVoltageKV == kv).OrderBy(l => l.LineId))
                {
                    paths.Add($"{voltageGroupPath}/{line.LineId}");
                    linePaths++;
                }
            }
        }

        // Write the dash menu file (paths list maintains insertion order)
        WriteDashMenuFile(Settings.DashMenuPath, paths);

        return new DashMenuExportResult(
            TotalPaths: paths.Count,
            SubstationPaths: substationPaths,
            VoltageLevelGroups: voltageLevelPaths,
            LinePaths: linePaths,
            StationsLoaded: stations.Count,
            BusesLoaded: buses.Count,
            LinesLoaded: lines.Count,
            OutputPath: Settings.DashMenuPath
        );
    }

    // ========= Result Types =========

    /// <summary>
    /// Represents the results of a dash menu export operation.
    /// </summary>
    /// <param name="TotalPaths">The total number of paths written to the dash menu file.</param>
    /// <param name="SubstationPaths">The number of substation paths generated.</param>
    /// <param name="VoltageLevelGroups">The number of voltage level group headers generated.</param>
    /// <param name="LinePaths">The number of line paths generated within voltage groups.</param>
    /// <param name="StationsLoaded">The number of stations loaded from the CSV file.</param>
    /// <param name="BusesLoaded">The number of buses loaded from the CSV file.</param>
    /// <param name="LinesLoaded">The number of lines loaded from the CSV file.</param>
    /// <param name="OutputPath">The file path where the dash menu file was written.</param>
    public sealed record DashMenuExportResult(
        int TotalPaths,
        int SubstationPaths,
        int VoltageLevelGroups,
        int LinePaths,
        int StationsLoaded,
        int BusesLoaded,
        int LinesLoaded,
        string OutputPath);

    // ========= Data Models =========

    /// <summary>
    /// Represents a station record loaded from the stations CSV file.
    /// </summary>
    /// 
    private sealed record StationRecord(
        string StationId,
        decimal Latitude,
        decimal Longitude,
        int NominalVoltageKV);

    /// <summary>
    /// Represents a bus record loaded from the buses CSV file.
    /// </summary>
    private sealed record BusRecord(
        string BusId,
        string StationId,
        int NominalVoltageKV,
        string AdjacentBusIds);

    /// <summary>
    /// Represents a line record loaded from the lines CSV file.
    /// </summary>
    private sealed record LineRecord(
        string LineId,
        string FromTerminalMP,
        string ToTerminalMP,
        string FromBusId,
        string ToBusId,
        int NominalVoltageKV);

    // ========= CSV Loading =========

    /// <summary>
    /// Loads station records from the stations CSV file.
    /// </summary>
    private static List<StationRecord> LoadStations()
    {
        List<StationRecord> stations = [];
        string csvPath = Settings.StationsCsvPath;

        if (string.IsNullOrWhiteSpace(csvPath) || !File.Exists(csvPath))
            return stations;

        foreach (string rawLine in File.ReadLines(csvPath).Skip(1))
        {
            string line = rawLine.Trim();

            if (line.Length == 0)
                continue;

            string[] fields = ParseCSVLine(line);

            if (fields.Length < 4)
                continue;

            string stationId = fields[0].Trim();

            if (string.IsNullOrWhiteSpace(stationId))
                continue;

            _ = decimal.TryParse(fields[1].Trim(), out decimal latitude);
            _ = decimal.TryParse(fields[2].Trim(), out decimal longitude);
            _ = int.TryParse(fields[3].Trim(), out int nominalKV);

            stations.Add(new StationRecord(stationId, latitude, longitude, nominalKV));
        }

        return stations;
    }

    /// <summary>
    /// Loads bus records from the buses CSV file.
    /// </summary>
    private static List<BusRecord> LoadBuses()
    {
        List<BusRecord> buses = [];
        string csvPath = Settings.BusesCsvPath;

        if (string.IsNullOrWhiteSpace(csvPath) || !File.Exists(csvPath))
            return buses;

        foreach (string rawLine in File.ReadLines(csvPath).Skip(1))
        {
            string line = rawLine.Trim();

            if (line.Length == 0)
                continue;

            string[] fields = ParseCSVLine(line);

            if (fields.Length < 3)
                continue;

            string busId = fields[0].Trim();
            string stationId = fields[1].Trim();

            if (string.IsNullOrWhiteSpace(busId))
                continue;

            _ = int.TryParse(fields[2].Trim(), out int nominalKV);
            string adjacentBusIds = fields.Length > 3 ? fields[3].Trim() : string.Empty;

            buses.Add(new BusRecord(busId, stationId, nominalKV, adjacentBusIds));
        }

        return buses;
    }

    /// <summary>
    /// Loads line records from the lines CSV file.
    /// </summary>
    private static List<LineRecord> LoadLines()
    {
        List<LineRecord> lines = [];
        string csvPath = Settings.LinesCsvPath;

        if (string.IsNullOrWhiteSpace(csvPath) || !File.Exists(csvPath))
            return lines;

        foreach (string rawLine in File.ReadLines(csvPath).Skip(1))
        {
            string line = rawLine.Trim();

            if (line.Length == 0)
                continue;

            string[] fields = ParseCSVLine(line);

            if (fields.Length < 6)
                continue;

            string lineId = fields[0].Trim();

            if (string.IsNullOrWhiteSpace(lineId))
                continue;

            string fromTerminalMP = fields[1].Trim();
            string toTerminalMP = fields[2].Trim();
            string fromBusId = fields[3].Trim();
            string toBusId = fields[4].Trim();

            _ = int.TryParse(fields[5].Trim(), out int nominalKV);

            lines.Add(new LineRecord(lineId, fromTerminalMP, toTerminalMP, fromBusId, toBusId, nominalKV));
        }

        return lines;
    }

    // ========= CSV Parsing =========

    /// <summary>
    /// Parses a CSV line into fields, respecting quoted values that may contain commas.
    /// </summary>
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

    // ========= File Writing =========

    /// <summary>
    /// Writes the dash menu paths to a text file.
    /// </summary>
    private static void WriteDashMenuFile(string path, List<string> paths)
    {
        using FileStream stream = File.Create(path);
        using StreamWriter writer = new(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        foreach (string menuPath in paths)
            writer.WriteLine(menuPath);
    }
}
