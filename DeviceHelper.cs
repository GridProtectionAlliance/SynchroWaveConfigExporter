//******************************************************************************************************
//  DeviceHelper.cs - Gbtc
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
// ReSharper disable InvertIf

namespace SynchroWaveConfigExporter;

/// <summary>
/// Shared helper methods for device identification and line name extraction.
/// Used by both ConfigExporter and PowerSystemModelExporter.
/// </summary>
public static class DeviceHelper
{

    // ========= Signal Type Identification =========

    /// <summary>
    /// Checks if a signal type is a phasor type (VPHA, VPHM, IPHA, IPHM).
    /// </summary>
    /// <param name="signalType">The signal type to check.</param>
    /// <returns><c>true</c> if the signal type is a phasor type; otherwise, <c>false</c>.</returns>
    public static bool IsPhasorType(string? signalType)
    {
        return (signalType ?? string.Empty).Trim().ToUpperInvariant() is "VPHA" or "VPHM" or "IPHA" or "IPHM";
    }

    /// <summary>
    /// Checks if a signal type is a frequency type (FREQ, DFDT).
    /// </summary>
    /// <param name="signalType">The signal type to check.</param>
    /// <returns><c>true</c> if the signal type is a frequency type; otherwise, <c>false</c>.</returns>
    public static bool IsFrequencyType(string? signalType)
    {
        return (signalType ?? string.Empty).Trim().ToUpperInvariant() is "FREQ" or "DFDT";
    }
    // ========= Device Type Identification =========

    // TODO:
    // Many of the following methods are specific to Entergy's synchrophasor naming
    // conventions and will need to be adapted for other utilities or data sources:

    /// <summary>
    /// Checks if a device is a PMU (line-terminal) device based on naming convention.
    /// PMU devices have _P_ followed by 3 letters and 1 digit (e.g.: _P_NNN4, _P_ENN8).
    /// </summary>
    /// <param name="device">The device acronym to check.</param>
    /// <returns><c>true</c> if the device matches the PMU naming pattern; otherwise, <c>false</c>.</returns>
    /// <remarks>
    /// This method uses Entergy-specific naming conventions where PMU devices are identified
    /// by the pattern "_P_" followed by exactly 3 letters and 1 digit.
    /// </remarks>
    public static bool IsPMUDevice(string? device)
    {
        if (string.IsNullOrWhiteSpace(device))
            return false;

        // Look for _P_ pattern followed by exactly 3 letters and 1 digit
        int index = device.IndexOf("_P_", StringComparison.OrdinalIgnoreCase);

        if (index < 0)
            return false;

        // Check what follows _P_
        int suffixStart = index + 3; // Position after "_P_"

        if (suffixStart + 4 != device.Length)
            return false; // Must be exactly 4 characters after _P_

        string suffix = device[suffixStart..];

        // Must be 3 letters followed by 1 digit
        return suffix.Length == 4 &&
               char.IsLetter(suffix[0]) &&
               char.IsLetter(suffix[1]) &&
               char.IsLetter(suffix[2]) &&
               char.IsDigit(suffix[3]);
    }

    /// <summary>
    /// Checks if a device is a DFR (Digital Fault Recorder) device.
    /// DFR devices have _D_ in the acronym (e.g.: GRAND_GULF_1_D_EPN8).
    /// </summary>
    /// <param name="device">The device acronym to check.</param>
    /// <returns><c>true</c> if the device contains "_D_"; otherwise, <c>false</c>.</returns>
    public static bool IsDFRDevice(string? device)
    {
        return !string.IsNullOrWhiteSpace(device) && device.Contains("_D_", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Determines whether the specified token is a marker that indicates the end of meaningful name tokens.
    /// </summary>
    /// <param name="token">The token to evaluate.</param>
    /// <returns>
    /// <c>true</c> if the token is considered a non-name marker; otherwise, <c>false</c>.
    /// </returns>
    /// <remarks>
    /// A non-name marker is a token that signifies the end of name-related tokens during processing.
    /// Examples include specific predefined markers like "D", "P", or tokens starting with "EPN".
    /// </remarks>
    public static bool IsNonNameMarker(string token)
    {
        token = token.Trim().ToUpperInvariant();

        if (token is "D" or "P")
            return true;

        if (token.StartsWith("EPN", StringComparison.OrdinalIgnoreCase))
            return true;

        if (token.StartsWith("EPI", StringComparison.OrdinalIgnoreCase))
            return true;

        if (token.StartsWith("ENN", StringComparison.OrdinalIgnoreCase))
            return true;

        if (token.StartsWith("ENI", StringComparison.OrdinalIgnoreCase))
            return true;

        if (token.StartsWith("NPN", StringComparison.OrdinalIgnoreCase))
            return true;

        if (token.StartsWith("NPI", StringComparison.OrdinalIgnoreCase))
            return true;

        if (token.StartsWith("NNN", StringComparison.OrdinalIgnoreCase))
            return true;

        if (token.StartsWith("NNI", StringComparison.OrdinalIgnoreCase))
            return true;

        return false;
    }

    // ========= Station Name Extraction =========

    /// <summary>
    /// Extracts the PMU base name (station name) from a device acronym by removing the _P_XXX# suffix.
    /// e.g.: "GOSLIN_ALDEN_P_NNN4" -> "GOSLIN_ALDEN"
    /// </summary>
    /// <param name="device">The device acronym to parse.</param>
    /// <returns>The station name portion before "_P_", or <c>null</c> if the pattern is not found.</returns>
    public static string? ExtractPMUBaseName(string? device)
    {
        if (string.IsNullOrWhiteSpace(device))
            return null;

        int index = device.IndexOf("_P_", StringComparison.OrdinalIgnoreCase);

        return index <= 0 ? null : device[..index];
    }

    /// <summary>
    /// Extracts station name from DFR device acronym: "GRAND_GULF_1_D_EPN8" => "GRAND GULF".
    /// Walks tokens right-to-left from the "_D_" marker, skipping the unit number.
    /// </summary>
    /// <param name="acronym">The DFR device acronym to parse.</param>
    /// <returns>The station name with underscores replaced by spaces, or <c>null</c> if parsing fails.</returns>
    /// <remarks>
    /// This method looks for the "_D_" marker, then extracts the station name from the prefix,
    /// automatically removing any trailing unit number (e.g., "_1", "_2").
    /// </remarks>
    public static string? ExtractStationFromDFRAcronym(string? acronym)
    {
        if (string.IsNullOrWhiteSpace(acronym))
            return null;

        // Find "_D_" marker
        int index = acronym.IndexOf("_D_", StringComparison.OrdinalIgnoreCase);

        if (index <= 0)
            return null;

        // Everything before "_D_" is "STATION_UNITNUM" or just "STATION_UNITNUM"
        string prefix = acronym[..index];

        // Walk backwards to strip the trailing unit number (e.g., "_1", "_2")
        int lastUnderscore = prefix.LastIndexOf('_');

        if (lastUnderscore > 0)
        {
            string possibleUnit = prefix[(lastUnderscore + 1)..];

            if (int.TryParse(possibleUnit, out _))
                prefix = prefix[..lastUnderscore];
        }

        return string.IsNullOrWhiteSpace(prefix) ? null : prefix.Replace('_', ' ');
    }

    /// <summary>
    /// Extracts station name from any device acronym with underscore-separated pattern.
    /// Handles patterns like "STATION_NAME_1_Q_XXX", "STATION_P_XXX", etc.
    /// Looks for common markers (_Q_, _P_, _D_, _I_) and extracts the station prefix.
    /// </summary>
    /// <param name="acronym">The device acronym to parse.</param>
    /// <returns>The station name with underscores replaced by spaces, or <c>null</c> if no valid station name is found.</returns>
    /// <remarks>
    /// Searches for device type markers (_D_, _P_, _Q_, _I_) and extracts the station name
    /// from the prefix, automatically removing trailing unit numbers.
    /// </remarks>
    public static string? ExtractStationFromAnyAcronym(string? acronym)
    {
        if (string.IsNullOrWhiteSpace(acronym))
            return null;

        // Look for common device type markers and extract station name before them
        // Patterns: _D_ (DFR), _P_ (PMU line terminal), _Q_ (solar/inverter), _I_ (other PMU)
        string[] markers = ["_D_", "_P_", "_Q_", "_I_"];

        foreach (string marker in markers)
        {
            int index = acronym.IndexOf(marker, StringComparison.OrdinalIgnoreCase);

            if (index <= 0)
                continue;

            string prefix = acronym[..index];

            // Strip trailing unit number (e.g., "_1", "_2")
            int lastUnderscore = prefix.LastIndexOf('_');

            if (lastUnderscore > 0)
            {
                string possibleUnit = prefix[(lastUnderscore + 1)..];

                if (int.TryParse(possibleUnit, out _))
                    prefix = prefix[..lastUnderscore];
            }

            if (!string.IsNullOrWhiteSpace(prefix))
                return prefix.Replace('_', ' ');
        }

        return null;
    }

    /// <summary>
    /// Parses "STATION-REMOTE {KV}KV" or "STATION - REMOTE {KV}KV" => "STATION" from
    /// the device Name field. Handles variations with spaces around the dash separator.
    /// </summary>
    /// <param name="name">The device name to parse.</param>
    /// <returns>The station name before the dash separator, or <c>null</c> if the pattern is not found.</returns>
    public static string? ExtractStationFromName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return null;

        // Handle both "STATION-REMOTE" and "STATION - REMOTE" formats
        // by looking for dash with optional surrounding spaces
        Match match = Regex.Match(name, @"^(.+?)\s*-\s*(.+)$");

        if (!match.Success)
            return null;

        string station = match.Groups[1].Value.Trim();

        return string.IsNullOrWhiteSpace(station) ? null : station;
    }

    // ========= Line Name Extraction =========

    /// <summary>
    /// Extracts the canonical line name from a phasor label or description, normalizing variations.
    /// e.g.: "BOGALUSA_LN_A" and "BOGALUSA_LN_B" both map to base "BOGALUSA_LN" with suffix "A"/"B"
    /// Phase suffixes like _IA, _VA are stripped since they indicate phase, not line.
    /// </summary>
    /// <param name="phasorLabel">The phasor label (e.g., from PhasorLabel field).</param>
    /// <param name="description">The measurement description (fallback).</param>
    /// <param name="signalType">The signal type (VPHA, VPHM, IPHA, IPHM, etc.).</param>
    /// <returns>The canonical line name with phase suffixes removed, or <c>null</c> if no valid line name is found.</returns>
    /// <remarks>
    /// For phasor measurements, this method uses the PhasorLabel field. For calculated values,
    /// it extracts the line name from the description field. Phase suffixes (e.g., _IA, _VA)
    /// are automatically stripped to produce a normalized line identifier.
    /// </remarks>
    public static string? ExtractCanonicalLineName(string? phasorLabel, string? description, string? signalType)
    {
        // For phasor measurements, use PhasorLabel (with phase suffix stripped)
        if (IsPhasorType(signalType))
        {
            string? label = phasorLabel?.Trim();

            if (!string.IsNullOrWhiteSpace(label))
                return StripPhaseSuffix(label);
        }

        // For calculated values, extract from description (also strip phase suffix)
        string? extracted = ExtractLineNameFromDescription(description ?? string.Empty);

        return string.IsNullOrWhiteSpace(extracted) ? null : StripPhaseSuffix(extracted);
    }

    /// <summary>
    /// Extracts a line/phasor name from the description field.
    /// e.g.: "ADAMS_CREEK_1_D_EPN8 WPEC A Current Magnitude" => "WPEC"
    /// e.g.: "ADAMS_CREEK_1_D_EPN8-MW_A-WPEC Active Power Calculation" => "WPEC"
    /// e.g.: "RAY_BRASWELL_2_D_EPN6 EAST_BUS A Voltage Magnitude" => "EAST_BUS"
    /// </summary>
    /// <param name="desc">The measurement description to parse.</param>
    /// <returns>The extracted line name, or <c>null</c> if no valid line name is found.</returns>
    /// <remarks>
    /// This method handles multiple description formats:
    /// <list type="bullet">
    /// <item><description>Power calculations: "DEVICE-MW_X-LINENAME ..."</description></item>
    /// <item><description>3-phase calculations: "DEVICE LINENAME Calculated Value: 3-Phase..."</description></item>
    /// <item><description>Standard phasor descriptions: "DEVICE LINENAME X Signal Type"</description></item>
    /// </list>
    /// </remarks>
    public static string? ExtractLineNameFromDescription(string? desc)
    {
        if (string.IsNullOrWhiteSpace(desc))
            return null;

        // Pattern 1: "DEVICE-MW_X-LINENAME ..." (power calculations)
        int index = desc.IndexOf("-MW_", StringComparison.OrdinalIgnoreCase);

        if (index >= 0)
        {
            // Find the line name after "-MW_X-"
            int afterMWIndex = index + 5; // Skip "-MW_X"

            if (afterMWIndex < desc.Length && desc[afterMWIndex] == '-')
                afterMWIndex++; // Skip the dash after MW_X

            if (afterMWIndex < desc.Length)
            {
                int space = desc.IndexOf(' ', afterMWIndex);
                int endIndex = space >= 0 ? space : desc.Length;

                if (endIndex > afterMWIndex)
                {
                    string lineName = desc[afterMWIndex..endIndex].Trim();

                    if (!string.IsNullOrWhiteSpace(lineName) && lineName.Length >= 2)
                        return lineName;
                }
            }
        }

        // Pattern 2: "DEVICE LINENAME Calculated Value: 3-Phase..." (3-phase calculations)
        index = desc.IndexOf(" Calculated Value:", StringComparison.OrdinalIgnoreCase);

        if (index > 0)
        {
            // Extract the part between device and "Calculated Value:"
            int firstSpace = desc.IndexOf(' ');

            if (firstSpace > 0 && firstSpace < index)
            {
                string middle = desc[(firstSpace + 1)..index].Trim();

                if (!string.IsNullOrWhiteSpace(middle) && middle.Length >= 2)
                    return middle;
            }
        }

        // Pattern 3: "DEVICE LINENAME X Signal Type" (standard phasor descriptions)
        // e.g.: "RAY_BRASWELL_2_D_EPN6 EAST_BUS A Voltage Magnitude" => "EAST_BUS"
        // Find the first space after the device acronym, then the line name follows
        index = desc.IndexOf(' ');

        if (index <= 0 || index >= desc.Length - 1)
            return null;

        string remainder = desc[(index + 1)..].Trim();

        // The line name is typically the first token before phase/signal type indicators
        string[] tokens = remainder.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        if (tokens.Length <= 0)
            return null;

        string candidate = tokens[0];

        // Skip if it looks like a phase indicator or signal type
        if (!IsPhaseOrSignalIndicator(candidate) && candidate.Length >= 2)
            return candidate;

        // Try combining first two tokens if first one is short
        // BUT not if the second token is a phase indicator
        if (tokens.Length <= 1 || candidate.Length >= 3)
            return null;

        // Don't combine if second token is a phase indicator
        return IsPhaseOrSignalIndicator(tokens[1]) ? null : $"{candidate}_{tokens[1]}";
    }

    /// <summary>
    /// Strips phase suffixes from phasor labels.
    /// e.g.: "AUTOTRAN_1____IA" -> "AUTOTRAN_1", "115kV_BUS_____VC" -> "115kV_BUS"
    /// e.g.: "BOGALUSA_LN_A" -> "BOGALUSA_LN_A" (not a phase suffix - no I or V before the letter)
    /// e.g.: "230_NORREL_LN_IB" -> "230_NORREL_LN"
    /// </summary>
    /// <param name="label">The phasor label to process.</param>
    /// <returns>The label with phase suffixes removed.</returns>
    /// <remarks>
    /// Recognizes and removes phase suffixes such as _IA, _IB, _IC, _I1, _I2, _I0, _VA, _VB, _VC, _V1, _V2, _V0.
    /// These suffixes indicate current/voltage phase identifiers rather than line identifiers.
    /// Handles multiple consecutive underscores before the suffix.
    /// </remarks>
    public static string StripPhaseSuffix(string label)
    {
        if (string.IsNullOrWhiteSpace(label))
            return label;

        string upper = label.ToUpperInvariant();

        // Check for phase suffixes: _IA, _IB, _IC, _I1, _I2, _I0, _VA, _VB, _VC, _V1, _V2, _V0
        // These are current/voltage phase indicators, not line identifiers
        // Also handle multiple underscores before the suffix (e.g.: _____VC)
        string[] phaseSuffixes = ["IA", "IB", "IC", "I1", "I2", "I0", "VA", "VB", "VC", "V1", "V2", "V0"];

        foreach (string suffix in phaseSuffixes)
        {
            // Check if the label ends with this phase suffix (e.g.: "_IA" or "_____IA")
            if (!upper.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                continue;

            // Find where the underscores start before the suffix
            int suffixStart = label.Length - suffix.Length;

            // Walk backwards to find all consecutive underscores
            int underscoreStart = suffixStart;

            while (underscoreStart > 0 && label[underscoreStart - 1] == '_')
                underscoreStart--;

            // Must have at least one underscore before the suffix
            if (underscoreStart < suffixStart)
                return label[..underscoreStart];
        }

        return label;
    }

    /// <summary>
    /// Checks if a token is a phase or signal type indicator that shouldn't be used as line name.
    /// </summary>
    /// <param name="token">The token to check.</param>
    /// <returns><c>true</c> if the token is a phase or signal type indicator; otherwise, <c>false</c>.</returns>
    public static bool IsPhaseOrSignalIndicator(string token)
    {
        return token.ToUpperInvariant() is 
            "A" or "B" or "C" or "+" or "-" or "0" or "1" or "2" or 
            "CURRENT" or "VOLTAGE" or "MAGNITUDE" or "ANGLE" or "PHASE" or 
            "FREQUENCY" or "CALCULATED" or "VALUE" or "CALCULATION" or 
            "ACTIVE" or "REACTIVE" or "APPARENT" or "POWER" or "3-PHASE" or 
            "THREEPHASE" or "THREE" or "MW" or "MVA" or "MVAR";
    }

    // ========= Line Parsing for Power System Model =========

    /// <summary>
    /// Result of parsing a line-terminal device for line identification.
    /// </summary>
    /// <param name="FromStation">The local station name (where this device is located).</param>
    /// <param name="ToRemote">The remote station/line name.</param>
    /// <param name="NominalKV">The nominal voltage level in kV.</param>
    public sealed record LineParse(
        string FromStation,
        string ToRemote,
        int NominalKV);

    /// <summary>
    /// Parses a line-terminal device to extract from-station, to-remote, and voltage.
    /// Uses the device Name field "STATION-REMOTE {KV}KV" as the primary source.
    /// </summary>
    /// <param name="deviceName">The device Name field (e.g., "GRAND GULF-BAXTER WILSON 500KV").</param>
    /// <param name="fallbackKV">Fallback voltage to use if not found in name (e.g., from phasor data). Default is 0.</param>
    /// <returns>A <see cref="LineParse"/> record containing the parsed line information, or <c>null</c> if parsing fails.</returns>
    /// <remarks>
    /// This method handles both "STATION-REMOTE NNN KV" and "STATION - REMOTE NNN KV" formats.
    /// It automatically strips trailing descriptors like "L1", "L2", "T1 T2" and unit numbers from the remote name.
    /// </remarks>
    public static LineParse? ParseLineFromDeviceName(string? deviceName, int fallbackKV = 0)
    {
        if (string.IsNullOrWhiteSpace(deviceName))
            return null;

        // Handle both "STATION-REMOTE NNN KV" and "STATION - REMOTE NNN KV" formats
        Match nameMatch = Regex.Match(deviceName, @"^(.+?)\s*-\s*(.+)$");

        if (!nameMatch.Success)
            return null;

        string fromStation = nameMatch.Groups[1].Value.Trim();
        string remainder = nameMatch.Groups[2].Value.Trim();

        // Extract KV from the end of the remainder: "REMOTE {NNN}KV" or "REMOTE {NNN}kV"
        int nominalKV = 0;
        string remote = remainder;

        // Try to parse "{NNN}KV" or "{NNN}kV" suffix (with or without space before KV)
        Match kvMatch = Regex.Match(remainder, @"(\d+)\s*[kK][vV]\s*$");

        if (kvMatch.Success)
        {
            nominalKV = int.Parse(kvMatch.Groups[1].Value, CultureInfo.InvariantCulture);
            remote = remainder[..kvMatch.Index].Trim();
        }

        // Use fallback KV if not found in name
        if (nominalKV == 0)
            nominalKV = fallbackKV;

        if (nominalKV == 0 || string.IsNullOrWhiteSpace(fromStation) || string.IsNullOrWhiteSpace(remote))
            return null;

        // Strip trailing descriptors like "L1", "L2", "T1 T2" from remote name
        // (e.g., "CHURCHILL L1" => "CHURCHILL", "STARTUP T1 T2" => "STARTUP")
        remote = Regex.Replace(remote, @"\s+[LT]\d+(\s+[LT]\d+)*\s*$", string.Empty, RegexOptions.IgnoreCase).Trim();

        // Strip trailing unit number like "AUTO1" => "AUTO" if followed by digit
        remote = Regex.Replace(remote, @"(\D)(\d+)$", "$1").Trim();

        return new LineParse(fromStation, remote, nominalKV);
    }

    // ========= Naming Helpers =========

    /// <summary>
    /// Normalizes a station or line name to a valid identifier.
    /// "DAYTON BULK" => "DAYTON_BULK", "EL DORADO" => "EL_DORADO"
    /// </summary>
    /// <param name="name">The name to normalize.</param>
    /// <returns>A normalized identifier with spaces replaced by underscores and only alphanumeric characters and underscores retained.</returns>
    public static string NormalizeToID(string name)
    {
        // "DAYTON BULK" => "DAYTON_BULK", "EL DORADO" => "EL_DORADO"
        string id = name.Trim().Replace(' ', '_').ToUpperInvariant();

        // Strip any non-alphanumeric/underscore characters
        StringBuilder sb = new(id.Length);

        foreach (char c in id)
        {
            if (c is >= 'A' and <= 'Z' or >= '0' and <= '9' or '_')
                sb.Append(c);
        }

        return sb.ToString();
    }

    /// <summary>
    /// Builds a stable, deterministic line ID from two endpoint station identifiers.
    /// The endpoints are sorted alphabetically so that the same line viewed from
    /// either end produces the same ID.
    /// </summary>
    /// <param name="stationA">The first station identifier.</param>
    /// <param name="remoteB">The second station identifier.</param>
    /// <returns>A deterministic line ID in the format "STATION_A_STATION_B" where stations are alphabetically sorted.</returns>
    /// <remarks>
    /// The alphabetical sorting ensures that a line between stations A and B will have the same ID
    /// regardless of which station is specified first.
    /// </remarks>
    public static string BuildLineID(string stationA, string remoteB)
    {
        string a = stationA.ToUpperInvariant();
        string b = remoteB.ToUpperInvariant();

        return string.Compare(a, b, StringComparison.Ordinal) <= 0
            ? $"{a}_{b}"
            : $"{b}_{a}";
    }

    /// <summary>
    /// Formats a string value for inclusion in a CSV file by escaping special characters
    /// such as commas, double quotes, and newlines. If necessary, the value is enclosed
    /// in double quotes, and any double quotes within the value are escaped by doubling them.
    /// </summary>
    /// <param name="value">
    /// The string value to format for CSV output. If <c>null</c>, it is treated as an empty string.
    /// </param>
    /// <returns>
    /// A properly formatted CSV field string. If the input contains special characters,
    /// the returned string will be enclosed in double quotes with appropriate escaping.
    /// </returns>
    public static string CSVField(string? value)
    {
        value ??= string.Empty;
        bool mustQuote = value.Contains(',') || value.Contains('"') || value.Contains('\n') || value.Contains('\r');
        return mustQuote ? $"\"{value.Replace("\"", "\"\"")}\"" : value;
    }
}
