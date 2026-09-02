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

    // ======================================================================================
    //
    //    TODO:
    //      Many of the following methods are specific to Entergy's synchrophasor naming
    //      conventions and will need to be adapted for other utilities or data sources:
    //
    // ======================================================================================

    // ========= Device Type Identification =========

    /// <summary>
    /// Checks if a device is a PMU (line-terminal) device based on naming convention.
    /// PMU devices have _P_ or _Q_ followed by 3 letters and 1 digit (e.g.: _P_NNN4, _Q_ENN8).
    /// </summary>
    /// <param name="device">The device acronym to check.</param>
    /// <returns><c>true</c> if the device matches the PMU naming pattern; otherwise, <c>false</c>.</returns>
    /// <remarks>
    /// This method uses Entergy-specific naming conventions where PMU devices are identified
    /// by the pattern "_P_" or _Q_ followed by exactly 3 letters and 1 digit.
    /// </remarks>
    public static bool IsPMUDevice(string? device)
    {
        if (string.IsNullOrWhiteSpace(device))
            return false;

        // Look for _P_ pattern followed by exactly 3 letters and 1 digit
        int index = device.IndexOf("_P_", StringComparison.OrdinalIgnoreCase);

        // Look for _Q_ pattern if _P_ not found (some solar/inverter devices use _Q_ instead of _P_)
        if (index < 0)
            index = device.IndexOf("_Q_", StringComparison.OrdinalIgnoreCase);

        if (index < 0)
            return false;

        // Check what follows _P_ or _Q_
        int suffixStart = index + 3; // Position after "_P_" or "_Q_"

        if (suffixStart + 4 != device.Length)
            return false; // Must be exactly 4 characters after _P_ or _Q_

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
    /// DFR devices have _D_ in the acronym (e.g.: MAPLE_RIDGE_1_D_EPN8).
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

    /// <summary>
    /// Determines whether the specified token represents a system prefix.
    /// </summary>
    /// <param name="token">The token to evaluate.</param>
    /// <returns>
    /// <c>true</c> if the token is a recognized system prefix (e.g., "PMU", "PDC", "SUB", or "SITE");
    /// otherwise, <c>false</c>.
    /// </returns>
    public static bool IsSystemPrefix(string token)
    {
        token = token.Trim().ToUpperInvariant();
        return token is "PMU" or "PDC" or "SUB" or "SITE";
    }

    /// <summary>
    /// Determines whether the specified token represents a unit identifier.
    /// </summary>
    /// <param name="token">The token to evaluate.</param>
    /// <returns>
    /// <c>true</c> if the token is a valid unit identifier (a non-negative integer between 0 and 99); otherwise, <c>false</c>.
    /// </returns>
    public static bool IsUnitToken(string token)
    {
        if (!int.TryParse(token, NumberStyles.None, CultureInfo.InvariantCulture, out int n))
            return false;

        return n is >= 0 and <= 99;
    }

    /// <summary>
    /// Determines whether the specified token is a valid name token.
    /// </summary>
    /// <param name="token">The token to evaluate.</param>
    /// <returns>
    /// <c>true</c> if the token is a valid name token; otherwise, <c>false</c>.
    /// A valid name token is at least three characters long and consists only of alphabetic characters.
    /// </returns>
    public static bool IsNameToken(string token)
    {
        token = token.Trim();

        if (token.Length < 3)
            return false;

        foreach (char c in token)
        {
            if (c is (< 'A' or > 'Z') and (< 'a' or > 'z'))
                return false;
        }

        return true;
    }

    // ========= Station Name Extraction =========

    /// <summary>
    /// Extracts the PMU base name (station name) from a device acronym by removing the _P_XXX# suffix.
    /// e.g.: "MORGAN_TATE_P_NNN4" -> "MORGAN_TATE"
    /// </summary>
    /// <param name="device">The device acronym to parse.</param>
    /// <returns>The station name portion before "_P_", or <c>null</c> if the pattern is not found.</returns>
    public static string? ExtractPMUBaseName(string? device)
    {
        if (string.IsNullOrWhiteSpace(device))
            return null;

        int index = device.IndexOf("_P_", StringComparison.OrdinalIgnoreCase);

        if (index < 0)
            index = device.IndexOf("_Q_", StringComparison.OrdinalIgnoreCase); // Some solar/inverter devices use _Q_ instead of _P_

        return index <= 0 ? null : device[..index];
    }

    /// <summary>
    /// Extracts station name from DFR device acronym: "MAPLE_RIDGE_1_D_EPN8" => "MAPLE RIDGE".
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
    /// Extracts the station name shared by several device-derived names as their longest common
    /// leading token sequence, e.g.: ["MAPLE RIDGE CEDAR JUNCTION", "MAPLE RIDGE NORTH", "MAPLE RIDGE XFMR"] => "MAPLE RIDGE".
    /// </summary>
    /// <param name="names">The station name candidates (space-separated tokens) extracted from the devices at one location.</param>
    /// <returns>The common leading tokens joined by spaces, or <c>null</c> when the names share no leading token.</returns>
    /// <remarks>
    /// Some DFRs (e.g., SEL relays configured as "flattened" standalone devices instead of a
    /// PDC-style parent/child hierarchy) are represented by several devices at one station whose
    /// acronyms follow "STATION_ELEMENT_D_XXX#", where ELEMENT names the measured line, bus or
    /// transformer. The station name is the prefix all of those devices share.
    /// </remarks>
    public static string? ExtractCommonStationName(IEnumerable<string?> names)
    {
        string[]? common = null;

        foreach (string? name in names)
        {
            if (string.IsNullOrWhiteSpace(name))
                continue;

            string[] tokens = name.Split(' ', StringSplitOptions.RemoveEmptyEntries);

            if (common is null)
            {
                common = tokens;
                continue;
            }

            int length = 0;

            while (length < common.Length && length < tokens.Length && common[length].Equals(tokens[length], StringComparison.OrdinalIgnoreCase))
                length++;

            common = common[..length];

            if (common.Length == 0)
                return null;
        }

        return common is { Length: > 0 } ? string.Join(' ', common) : null;
    }

    /// <summary>
    /// Extracts the element name a DFR device acronym carries beyond its station name, e.g.: for
    /// "MAPLE_RIDGE_CEDAR_JUNCTION_D_EPN8" at station "MAPLE RIDGE" => "CEDAR JUNCTION"; for a conventional
    /// "MAPLE_RIDGE_2_D_EPN8" at station "MAPLE RIDGE" => <c>null</c>.
    /// </summary>
    /// <param name="acronym">The DFR device acronym to parse.</param>
    /// <param name="stationName">The station name (or identifier) the device belongs to.</param>
    /// <returns>The element name with underscores replaced by spaces, or <c>null</c> when the acronym names only the station.</returns>
    /// <remarks>
    /// For "flattened" standalone DFR devices, the element typically names the remote station of
    /// the measured line terminal, so it can serve as the line's remote endpoint when the phasor
    /// label itself (e.g., a circuit number such as "L123") does not identify one.
    /// </remarks>
    public static string? ExtractElementFromDFRAcronym(string? acronym, string? stationName)
    {
        string? extracted = ExtractStationFromDFRAcronym(acronym);

        if (string.IsNullOrWhiteSpace(extracted) || string.IsNullOrWhiteSpace(stationName))
            return null;

        string[] tokens = extracted.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        string[] stationTokens = stationName.Replace('_', ' ').Split(' ', StringSplitOptions.RemoveEmptyEntries);

        if (tokens.Length <= stationTokens.Length)
            return null;

        for (int i = 0; i < stationTokens.Length; i++)
        {
            if (!tokens[i].Equals(stationTokens[i], StringComparison.OrdinalIgnoreCase))
                return null;
        }

        return string.Join(' ', tokens[stationTokens.Length..]);
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
    /// e.g.: "OAKDALE_LN_A" and "OAKDALE_LN_B" both map to base "OAKDALE_LN" with suffix "A"/"B"
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
    /// e.g.: "WILLOW_CREEK_1_D_EPN8 WCEC A Current Magnitude" => "WCEC"
    /// e.g.: "WILLOW_CREEK_1_D_EPN8-MW_A-WCEC Active Power Calculation" => "WCEC"
    /// e.g.: "LELAND_PARK_2_D_EPN6 EAST_BUS A Voltage Magnitude" => "EAST_BUS"
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
        // e.g.: "LELAND_PARK_2_D_EPN6 EAST_BUS A Voltage Magnitude" => "EAST_BUS"
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
    /// e.g.: "OAKDALE_LN_A" -> "OAKDALE_LN_A" (not a phase suffix - no I or V before the letter)
    /// e.g.: "230_KENDALL_LN_IB" -> "230_KENDALL_LN"
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
    /// Removes a leading voltage prefix from a normalized label, e.g. "230_EAST_BUS" -&gt; "EAST_BUS"
    /// or "115KV_N_BUS" -&gt; "N_BUS", so bus labels that carry a voltage prefix in one source but not
    /// another still match.
    /// </summary>
    /// <param name="label">The label to strip.</param>
    /// <returns>The label without any leading voltage-level prefix.</returns>
    public static string StripVoltagePrefix(string label)
    {
        Match match = Regex.Match(label, @"^\d+(KV)?_(.+)$", RegexOptions.IgnoreCase);
        return match.Success ? match.Groups[2].Value : label;
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

    /// <summary>
    /// Determines whether a measurement description denotes a calculated value (power, 3-phase, etc.)
    /// rather than a raw phasor measurement.
    /// </summary>
    /// <param name="description">The measurement description to evaluate.</param>
    /// <returns><c>true</c> if the description indicates a calculated value; otherwise, <c>false</c>.</returns>
    public static bool IsCalculatedValue(string? description)
    {
        string desc = description ?? string.Empty;

        return desc.Contains("-MW_", StringComparison.OrdinalIgnoreCase) ||
               desc.Contains("Calculated Value:", StringComparison.OrdinalIgnoreCase) ||
               desc.Contains("Power Calculation", StringComparison.OrdinalIgnoreCase) ||
               desc.Contains("3-Phase", StringComparison.OrdinalIgnoreCase);
    }

    // ========= Bus & Transformer Identification =========

    /// <summary>Determines whether a phasor label/line name denotes a bus (a voltage node).</summary>
    /// <param name="name">The phasor label or line name to evaluate.</param>
    /// <returns><c>true</c> if the name denotes a bus; otherwise, <c>false</c>.</returns>
    public static bool IsBusLabel(string? name)
    {
        return !string.IsNullOrWhiteSpace(name) && name.Contains("BUS", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Determines whether a phasor label/line name denotes a transformer terminal: a high-side
    /// (<c>_HS</c>) or low-side (<c>_LS</c>) marker, or an explicit transformer name (XFMR/AUTOTRAN).
    /// </summary>
    /// <param name="name">The phasor label or line name to evaluate.</param>
    /// <returns><c>true</c> if the name denotes a transformer terminal; otherwise, <c>false</c>.</returns>
    public static bool IsTransformerLabel(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return false;

        string upper = name.Trim().ToUpperInvariant();

        return upper.EndsWith("_HS", StringComparison.Ordinal) ||
               upper.EndsWith("_LS", StringComparison.Ordinal) ||
               upper.Contains("XFMR", StringComparison.Ordinal) ||
               upper.Contains("AUTOTRAN", StringComparison.Ordinal);
    }

    /// <summary>
    /// Returns "HS", "LS", or empty string for a transformer terminal's winding side.
    /// </summary>
    /// <param name="name">The transformer terminal label.</param>
    /// <returns>"HS" for a high-side winding, "LS" for a low-side winding, or an empty string.</returns>
    public static string TransformerSide(string name)
    {
        string upper = name.Trim().ToUpperInvariant();

        if (upper.EndsWith("_HS", StringComparison.Ordinal))
            return "HS";

        if (upper.EndsWith("_LS", StringComparison.Ordinal))
            return "LS";

        return string.Empty;
    }

    /// <summary>
    /// Returns a transformer identity key from a terminal label by removing the winding-side
    /// suffix, e.g. "AUTO_1_HS" and "AUTO_1_LS" both yield "AUTO_1".
    /// </summary>
    /// <param name="name">The transformer terminal label.</param>
    /// <returns>The transformer identity key with any winding-side suffix removed.</returns>
    public static string TransformerKey(string name)
    {
        string id = NormalizeToID(name);

        if (id.EndsWith("_HS", StringComparison.Ordinal) || id.EndsWith("_LS", StringComparison.Ordinal))
            id = id[..^3];

        return id;
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
    /// <param name="deviceName">The device Name field (e.g., "MAPLE RIDGE-CEDAR JUNCTION 500KV").</param>
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
        // (e.g., "WESTBROOK L1" => "WESTBROOK", "STARTUP T1 T2" => "STARTUP")
        remote = Regex.Replace(remote, @"\s+[LT]\d+(\s+[LT]\d+)*\s*$", string.Empty, RegexOptions.IgnoreCase).Trim();

        // Strip trailing unit number like "AUTO1" => "AUTO" if followed by digit
        remote = Regex.Replace(remote, @"(\D)(\d+)$", "$1").Trim();

        return new LineParse(fromStation, remote, nominalKV);
    }

    // ========= Naming Helpers =========

    /// <summary>
    /// Normalizes a station or line name to a valid identifier.
    /// "FAIRMONT BULK" => "FAIRMONT_BULK", "GREEN VALLEY" => "GREEN_VALLEY"
    /// </summary>
    /// <param name="name">The name to normalize.</param>
    /// <returns>A normalized identifier with spaces replaced by underscores and only alphanumeric characters and underscores retained.</returns>
    public static string NormalizeToID(string name)
    {
        // "FAIRMONT BULK" => "FAIRMONT_BULK", "GREEN VALLEY" => "GREEN_VALLEY"
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
