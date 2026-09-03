//******************************************************************************************************
//  Settings.cs - Gbtc
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
//  01/15/2026 - J. Ritchie Carroll
//       Generated original version of source code.
//
//******************************************************************************************************

namespace SynchroWaveConfigExporter;

/// <summary>
/// Represents application configuration settings.
/// </summary>
public static class Settings
{
    /// <summary>
    /// Gets name of the host service to load configuration from, e.g., 'openPDC', 'openHistorian', or 'SIEGate'.
    /// </summary>
    public static string HostService => s_section.HostService;

    /// <summary>
    /// Gets default installation path for the host service.
    /// </summary>
    public static string DefaultInstallPath => s_section.DefaultInstallPath;

    /// <summary>
    /// Gets output path for the STTP SEL signal mappings configuration CSV.
    /// </summary>
    public static string SttpSelConfigCsvPath => s_section.SttpSelConfigCsvPath;

    /// <summary>
    /// Gets output path for the power system model stations CSV.
    /// </summary>
    public static string StationsCsvPath => s_section.StationsCsvPath;

    /// <summary>
    /// Gets output path for the power system model buses CSV.
    /// </summary>
    public static string BusesCsvPath => s_section.BusesCsvPath;

    /// <summary>
    /// Gets output path for the power system model lines CSV.
    /// </summary>
    public static string LinesCsvPath => s_section.LinesCsvPath;

    /// <summary>
    /// Gets output path for the dash menu file.
    /// </summary>
    public static string DashMenuPath => s_section.DashMenuPath;

    /// <summary>
    /// Gets path to the user-maintained dash menu text file whose entries are appended to the
    /// automated dash menu output. A relative path is resolved against the application directory.
    /// </summary>
    public static string UserDashMenuPath => s_section.UserDashMenuPath;

    /// <summary>
    /// Gets value indicating whether 'MeasurementPoint' mappings should be persisted to 'AlternateTag' field.
    /// </summary>
    public static bool PersistAlternateTags => s_section.PersistAlternateTags;

    /// <summary>
    /// Gets other prefixes to be excluded from 'MeasurementPoint' mappings
    /// </summary>
    public static string[] ExcludedPrefixes => s_section.ExcludedPrefixes;

    /// <summary>
    /// Gets a value indicating whether power quantities should be mapped to 'MeasurementPoint' mappings.
    /// </summary>
    public static bool MapPowerQuantities => s_section.MapPowerQuantities;

    /// <summary>
    /// Gets a value indicating whether to include voltage-level grouped lines in the dash menu.
    /// </summary>
    public static bool IncludeVoltageGroupedLines => s_section.IncludeVoltageGroupedLines;

    /// <summary>
    /// Gets a value indicating whether a device's frequency and dF/dt signals are mapped to every
    /// measurement point derived from that device; otherwise (the default), they are mapped to a
    /// single point: the device's bus-voltage point when it has one, else its first point.
    /// </summary>
    /// <remarks>
    /// SynchroWave allows a source signal to appear only once in the signal mapping file, so the
    /// default is <c>false</c>; enabling this produces a mapping file SynchroWave rejects.
    /// </remarks>
    public static bool MapFrequencyToAllMeasurementPoints => s_section.MapFrequencyToAllMeasurementPoints;

    /// <summary>
    /// Gets a clean version of the given value by removing characters that are
    /// invalid in a time-series framework identifier, replacing spaces with
    /// underscores, and converting value to uppercase.
    /// </summary>
    /// <param name="value">The value to be cleaned.</param>
    /// <returns>The cleaned identifier.</returns>
    public static string GetCleanIdentifier(string value)
    {
        string cleanValue = value.Replace(" ", "_").ToUpperInvariant();
        return Regex.Replace(cleanValue, @"[^A-Z0-9\-!_\.@#\$]+", "");
    }

    private static dynamic s_section = null!;

    internal static void DefineSettings(ConfigSettings settings, string settingsCategory)
    {
        s_section = settings[settingsCategory];

        s_section.HostService = ("openHistorian", "Name of the host service to load configuration from, e.g., 'openPDC', 'openHistorian', or 'SIEGate'");
        s_section.DefaultInstallPath = (@"C:\Program Files\openHistorian\", "Default installation path for the host service");
        s_section.SttpSelConfigCsvPath = ("sel-sttpreader-signalmappings.csv", "STTP SEL configuration CSV output path");
        s_section.StationsCsvPath = ("sel-powersystemmodel_stations.csv", "Power system model stations CSV output path");
        s_section.BusesCsvPath = ("sel-powersystemmodel_buses.csv", "Power system model buses CSV output path");
        s_section.LinesCsvPath = ("sel-powersystemmodel_lines.csv", "Power system model lines CSV output path");
        s_section.DashMenuPath = ("dash-menu.txt", "Dash menu file output path");
        s_section.UserDashMenuPath = ("user-dash-menu.txt", "Path to a user-maintained dash menu text file whose entries are appended to the automated dash menu output; a relative path is resolved against the application directory");
        s_section.PersistAlternateTags = (false, "Indicates whether 'MeasurementPoint' mappings should be persisted to 'AlternateTag' field");
        s_section.ExcludedPrefixes = (new[] {"ETR", "EES", "ESI"}, "Other prefixes to be excluded from 'MeasurementPoint' mappings");
        s_section.MapPowerQuantities = (false, "Indicates whether power quantities should be mapped to 'MeasurementPoint' mappings");
        s_section.IncludeVoltageGroupedLines = (true, "Indicates whether to include voltage-level grouped lines in the dash menu");
        s_section.MapFrequencyToAllMeasurementPoints = (false, "Indicates whether a device's frequency and dF/dt signals are mapped to every measurement point derived from that device instead of only its bus-voltage point (else first point); leave false - SynchroWave allows each source signal to appear only once in the mapping file");
    }
}