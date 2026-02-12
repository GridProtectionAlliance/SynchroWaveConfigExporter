//******************************************************************************************************
//  SttpConfigExporter.cs - Gbtc
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
//  01/21/2026 - J. Ritchie Carroll
//       Generated original version of source code.
//
//******************************************************************************************************
// ReSharper disable ConvertIfStatementToReturnStatement
// ReSharper disable ConvertIfStatementToSwitchStatement
// ReSharper disable InvertIf

namespace SynchroWaveConfigExporter;

/// <summary>
/// Derives synchrophasor measurement configuration to SEL-compatible CSV format.
/// </summary>
/// <remarks>
/// Generates human-readable 16-character measurement point names from device acronyms,
/// groups measurements by phasor/line identity for consistent naming, and maps signal types
/// to SEL quantity descriptors. Manages AlternateTag generation and persistence with strict
/// permanence rules (never modifies existing tags). Supports PMU devices, multi-line substations,
/// calculated values, and frequency measurements.
/// </remarks>
public static class SttpConfigExporter
{
    // ========= Public API =========

    /// <summary>
    /// Exports synchrophasor measurement configuration to SEL-compatible CSV format.
    /// </summary>
    /// <param name="connection">Active database connection to query measurements from.</param>
    /// <param name="companyAcronym">Company acronym prefix to strip from device names.</param>
    /// <returns>Result containing export statistics including total loaded, exported, invalid, and generated counts.</returns>
    /// <exception cref="ArgumentNullException">Thrown when connection is null.</exception>
    /// <exception cref="ArgumentException">Thrown when SttpSelConfigCsvPath setting is null or whitespace.</exception>
    public static ConfigExportResult Export(DbConnection connection, string companyAcronym)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentException.ThrowIfNullOrWhiteSpace(Settings.SttpSelConfigCsvPath);

        // 1) Load all measurements
        List<MeasurementRow> rows = LoadActiveMeasurements(connection);

        // 2) Apply permanence rules + plan new AlternateTags (only when missing)
        //    Generate/persist BASE tags only (no suffixing).
        AlternateTagPlan plan = BuildAlternateTagPlan(rows, companyAcronym);

        // 3) Persist only newly generated AlternateTags (never modify existing)
        if (Settings.PersistAlternateTags && plan.Updates.Count > 0)
            PersistAlternateTagUpdates(connection, plan.Updates);

        // 4) Build config rows using phasor/line grouping for consistent MeasurementPoint assignment
        List<SelConfigRow> configRows = BuildConfigRowsWithLineGrouping(rows, plan);

        // 5) Write output CSV
        WriteSelCSV(Settings.SttpSelConfigCsvPath, configRows);

        return new ConfigExportResult(
            TotalLoaded: rows.Count,
            Exported: configRows.Count,
            InvalidAlternateTagTooLong: plan.InvalidForExportSignalIDs.Count,
            AlternateTagsGenerated: plan.Updates.Count
        );
    }

    // ========= Config row building with line grouping =========

    /// <summary>
    /// Builds config rows by first grouping measurements by their phasor/line identity,
    /// then assigning consistent MeasurementPoint names to each group.
    /// Processes phasor measurements first to establish measurement points, then processes
    /// non-phasor measurements (frequency, dF/dt, calculated values) which may share
    /// measurement points with their associated phasors.
    /// </summary>
    private static List<SelConfigRow> BuildConfigRowsWithLineGrouping(List<MeasurementRow> rows, AlternateTagPlan plan)
    {
        List<SelConfigRow> configRows = [];

        // Track used measurement point + quantity combinations (case-insensitive)
        // Ensures each MeasurementPoint + Quantity pair appears only once in output
        HashSet<string> usedCombinations = new(StringComparer.OrdinalIgnoreCase);

        // Track assigned MeasurementPoint names by line group key (Device + PhasorLabel or description-derived line name)
        // This ensures all measurements from the same line share the same MeasurementPoint
        Dictionary<string, string> lineGroupMeasurementPointMap = new(StringComparer.OrdinalIgnoreCase);

        // Track used measurement points globally for uniqueness
        HashSet<string> usedMeasurementPoints = new(StringComparer.OrdinalIgnoreCase);

        // Build a lookup of line names to their assigned measurement points per device
        // This helps calculated values find their parent phasor's measurement point
        Dictionary<string, string> deviceLineNameMeasurementPointMap = new(StringComparer.OrdinalIgnoreCase);

        // Track phasor measurement points by device - used for frequency/dF/dt assignment
        // Key: device, Value: list of measurement points associated with phasors for that device
        Dictionary<string, List<string>> devicePhasorMeasurementPointsMap = new(StringComparer.OrdinalIgnoreCase);

        // First pass: Process phasor measurements to establish measurement points
        // This ensures frequency/dF/dt can find existing phasor measurement points
        foreach (MeasurementRow row in rows)
        {
            if (plan.InvalidForExportSignalIDs.Contains(row.SignalID))
                continue;

            // Only process phasor types in first pass
            if (!IsPhasorType(row.SignalType))
                continue;

            string? quantity = MapQuantity(row);

            if (quantity is null)
                continue;

            // Get the base measurement point from AlternateTag or generated plan
            string? baseMeasurementPoint = row.AlternateTag;

            if (string.IsNullOrWhiteSpace(baseMeasurementPoint))
                baseMeasurementPoint = plan.SignalIDMeasurementPointMap.GetValueOrDefault(row.SignalID);

            if (string.IsNullOrWhiteSpace(baseMeasurementPoint))
                continue;

            if (baseMeasurementPoint.Length > 16)
                continue;

            // Determine the line group key for this measurement
            string lineGroupKey = GetLineGroupKey(row);
            string device = row.Device ?? string.Empty;

            // Extract the canonical line name for lookup purposes
            string? lineName = ExtractCanonicalLineName(row);
            string lineDeviceKey = $"{device}|{lineName ?? ""}";

            // Check if we already have a MeasurementPoint for this line group
            if (!lineGroupMeasurementPointMap.TryGetValue(lineGroupKey, out string? assignedMP))
            {
                // Create a new measurement point for this phasor group
                assignedMP = DetermineLineGroupMeasurementPoint(
                    row, baseMeasurementPoint, usedMeasurementPoints);

                lineGroupMeasurementPointMap[lineGroupKey] = assignedMP;
                usedMeasurementPoints.Add(assignedMP);

                // Register this line name -> MP mapping for future lookups
                if (!string.IsNullOrWhiteSpace(lineName))
                    deviceLineNameMeasurementPointMap[lineDeviceKey] = assignedMP;

                // Track phasor measurement points by device (for frequency/dF/dt lookup)
                if (!devicePhasorMeasurementPointsMap.TryGetValue(device, out List<string>? mpList))
                {
                    mpList = [];
                    devicePhasorMeasurementPointsMap[device] = mpList;
                }

                if (!mpList.Contains(assignedMP, StringComparer.OrdinalIgnoreCase))
                    mpList.Add(assignedMP);
            }

            // Create a composite key for MeasurementPoint + Quantity combination
            string combinationKey = $"{assignedMP}|{quantity}";

            // Check if this exact MeasurementPoint + Quantity combination already exists
            if (!usedCombinations.Add(combinationKey))
                continue;

            // This MeasurementPoint + Quantity combination is new, add it
            configRows.Add(new SelConfigRow(
                DeviceAcronym: row.Device ?? string.Empty,
                Description: row.Description ?? string.Empty,
                MeasurementPoint: assignedMP,
                Quantity: quantity
            ));
        }

        // Second pass: Process all non-phasor measurements (frequency, dF/dt, calculated values, etc.)
        // Phasor measurement points are now fully populated, so frequency/dF/dt can find their associated phasor MPs
        foreach (MeasurementRow row in rows)
        {
            if (plan.InvalidForExportSignalIDs.Contains(row.SignalID))
                continue;

            // Skip phasor types - already processed in first pass
            if (IsPhasorType(row.SignalType))
                continue;

            string? quantity = MapQuantity(row);

            if (quantity is null)
                continue;

            // Get the base measurement point from AlternateTag or generated plan
            string? baseMeasurementPoint = row.AlternateTag;

            if (string.IsNullOrWhiteSpace(baseMeasurementPoint))
                baseMeasurementPoint = plan.SignalIDMeasurementPointMap.GetValueOrDefault(row.SignalID);

            if (string.IsNullOrWhiteSpace(baseMeasurementPoint))
                continue;

            if (baseMeasurementPoint.Length > 16)
                continue;

            // Determine the line group key for this measurement
            string lineGroupKey = GetLineGroupKey(row);
            string device = row.Device ?? string.Empty;

            // Extract the canonical line name for lookup purposes
            string? lineName = ExtractCanonicalLineName(row);
            string lineDeviceKey = $"{device}|{lineName ?? ""}";

            // Check if we already have a MeasurementPoint for this line group
            if (!lineGroupMeasurementPointMap.TryGetValue(lineGroupKey, out string? assignedMP))
            {
                if (IsFrequencyType(row.SignalType))
                {
                    // For frequency/dF/dt, try to find an existing phasor measurement point for this device
                    assignedMP = FindPhasorMeasurementPointForDevice(device, devicePhasorMeasurementPointsMap);
                }
                else if (IsCalculatedValue(row) && !string.IsNullOrWhiteSpace(lineName))
                {
                    // For calculated values, try to find an existing measurement point for this line
                    assignedMP = FindExistingMeasurementPointForLine(device, lineName, deviceLineNameMeasurementPointMap);
                }

                // If no existing MP found, create a new one
                if (string.IsNullOrWhiteSpace(assignedMP))
                {
                    assignedMP = DetermineLineGroupMeasurementPoint(
                        row, baseMeasurementPoint, usedMeasurementPoints);
                }

                lineGroupMeasurementPointMap[lineGroupKey] = assignedMP;
                usedMeasurementPoints.Add(assignedMP);

                // Register this line name -> MP mapping for future lookups
                if (!string.IsNullOrWhiteSpace(lineName))
                    deviceLineNameMeasurementPointMap[lineDeviceKey] = assignedMP;
            }

            // Create a composite key for MeasurementPoint + Quantity combination
            string combinationKey = $"{assignedMP}|{quantity}";

            // Check if this exact MeasurementPoint + Quantity combination already exists
            // Skip duplicates to avoid multiple rows with same MP + Quantity in output
            if (!usedCombinations.Add(combinationKey))
                continue;

            // This MeasurementPoint + Quantity combination is new, add it
            configRows.Add(new SelConfigRow(
                DeviceAcronym: row.Device ?? string.Empty,
                Description: row.Description ?? string.Empty,
                MeasurementPoint: assignedMP,
                Quantity: quantity
            ));
        }

        return configRows;
    }

    /// <summary>
    /// Finds a phasor measurement point for a device to use for frequency/dF/dt.
    /// Returns the first phasor measurement point if available, null otherwise.
    /// This handles both single-line and multi-line devices by using the first encountered phasor.
    /// </summary>
    private static string? FindPhasorMeasurementPointForDevice(
        string device,
        Dictionary<string, List<string>> devicePhasorMeasurementPointsMap)
    {
        if (string.IsNullOrWhiteSpace(device))
            return null;

        if (!devicePhasorMeasurementPointsMap.TryGetValue(device, out List<string>? mpList))
            return null;

        // If there's exactly one phasor measurement point for this device, use it
        // If there are multiple, we can't determine which one to use, so return null
        // (frequency will get its own measurement point)
        if (mpList.Count == 1)
            return mpList[0];

        // Multiple phasor groups - could pick the first one, but that might be arbitrary
        // For now, if there are multiple, use the first one encountered
        // This handles cases like DRIVER_SOLAR where there's effectively one line
        if (mpList.Count > 0)
            return mpList[0];

        return null;
    }

    /// <summary>
    /// Checks if a measurement row represents a calculated value (power, 3-phase, etc.)
    /// </summary>
    private static bool IsCalculatedValue(MeasurementRow row)
    {
        string desc = row.Description ?? string.Empty;

        return desc.Contains("-MW_", StringComparison.OrdinalIgnoreCase) ||
               desc.Contains("Calculated Value:", StringComparison.OrdinalIgnoreCase) ||
               desc.Contains("Power Calculation", StringComparison.OrdinalIgnoreCase) ||
               desc.Contains("3-Phase", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Extracts the canonical line name from a measurement, normalizing variations.
    /// e.g.: "BOGALUSA_LN_A" and "BOGALUSA_LN_B" both map to base "BOGALUSA_LN"
    /// Phase suffixes like _IA, _VA are stripped since they indicate phase, not line.
    /// </summary>
    private static string? ExtractCanonicalLineName(MeasurementRow row)
    {
        return DeviceHelper.ExtractCanonicalLineName(row.PhasorLabel, row.Description, row.SignalType);
    }

    /// <summary>
    /// Finds an existing measurement point for a line by exact device and line name match.
    /// Does not match across different line suffixes (e.g., BOGALUSA_LN_A vs BOGALUSA_LN_B).
    /// </summary>
    private static string? FindExistingMeasurementPointForLine(
        string device,
        string lineName,
        Dictionary<string, string> deviceLineNameMeasurementPointMap)
    {
        // Direct lookup first - exact line name match
        string key = $"{device}|{lineName}";

        // For lines with suffixes (like BOGALUSA_LN_A, BOGALUSA_LN_B), 
        // we should NOT match across different suffixes - they are different measurement points
        // Only match if there's an exact base+suffix match or the exact line name
        return deviceLineNameMeasurementPointMap.GetValueOrDefault(key);
    }

    /// <summary>
    /// Gets a unique key identifying the phasor/line group for a measurement.
    /// Measurements with the same line group key should share the same MeasurementPoint.
    /// PMU devices group all measurements together. Phasor measurements group by
    /// Device + PhasorLabel (with phase suffix stripped).
    /// Calculated values group by Device + extracted line name.
    /// Frequency/dF/dt measurements get special handling to find associated phasors.
    /// </summary>
    private static string GetLineGroupKey(MeasurementRow row)
    {
        string device = row.Device ?? string.Empty;

        // Check if this is a PMU device (has _P_ followed by 3 alphas and 1 number)
        // PMU devices measure a single line, so ALL measurements share the same MeasurementPoint
        if (IsPMUDevice(device))
        {
            // All PMU measurements group together by device
            return $"{device}|PMU";
        }

        // For phasor measurements (VPHA/VPHM/IPHA/IPHM), use Device + PhasorLabel (with phase suffix stripped)
        if (IsPhasorType(row.SignalType))
        {
            string phasorLabel = (row.PhasorLabel ?? string.Empty).Trim();

            if (!string.IsNullOrWhiteSpace(phasorLabel))
            {
                // Strip phase suffixes like _IA, _IB, _IC, _I1, _I2, _I0, _VA, _VB, _VC, _V1, _V2, _V0
                string baseLabel = StripPhaseSuffix(phasorLabel);
                return $"{device}|PHASOR|{baseLabel}";
            }
        }

        // For calculated values (power calculations, 3-phase, etc.), try to extract line name from description
        // and use the FULL line name (including suffix) to group with corresponding phasors
        string desc = row.Description ?? string.Empty;
        string? lineName = ExtractLineNameFromDescription(desc);

        if (!string.IsNullOrWhiteSpace(lineName))
        {
            // Strip phase suffixes from extracted line names too
            string baseLineName = StripPhaseSuffix(lineName);

            // Use PHASOR key type so calculated values group with their phasors
            return $"{device}|PHASOR|{baseLineName}";
        }

        // For frequency/dfdt, try to find a matching phasor group for this device
        // This will be resolved later in BuildConfigRowsWithLineGrouping by checking
        // if there's a single phasor group for this device
        if (IsFrequencyType(row.SignalType))
            return $"{device}|FREQ";

        // Fallback: use device + signal ID for truly unique signals
        return $"{device}|{row.SignalID}";
    }

    /// <summary>
    /// Determines the MeasurementPoint name for a line group.
    /// For PMU devices, uses simplified naming based on device name prefix.
    /// For line-specific measurements, incorporates the line name with underscore separator.
    /// Ensures uniqueness using hash-based suffix if needed.
    /// </summary>
    private static string DetermineLineGroupMeasurementPoint(
        MeasurementRow row,
        string baseMeasurementPoint,
        HashSet<string> usedMeasurementPoints)
    {
        string device = row.Device ?? string.Empty;

        // For PMU devices, use a simplified naming based on device name prefix
        // PMU devices measure a single line, so we use just the station name
        if (IsPMUDevice(device))
        {
            string? pmuBase = ExtractPMUBaseName(device);

            if (!string.IsNullOrWhiteSpace(pmuBase))
            {
                // Normalize to alphanumeric only
                string pmuMP = NormalizeAlphaNumeric(pmuBase);

                // If already <= 16, use it directly (checking for uniqueness)
                if (pmuMP.Length <= 16 && !usedMeasurementPoints.Contains(pmuMP))
                    return pmuMP;

                // Need to compress - remove vowels
                if (pmuMP.Length > 16)
                    pmuMP = CompressWordVowelsWhole(pmuMP);

                // Final length enforcement - must be <= 16
                if (pmuMP.Length > 16)
                    pmuMP = pmuMP[..16];

                if (!usedMeasurementPoints.Contains(pmuMP))
                    return pmuMP;

                // If still not unique, fall through to hash-based uniqueness
                return MakeUnique16_NameFocused(pmuMP, device, usedMeasurementPoints);
            }
        }

        // Extract line name first - we need to know if this is a line-specific measurement
        string? lineName = null;

        // For phasor measurements, use the PhasorLabel (with phase suffix stripped)
        if (IsPhasorType(row.SignalType))
        {
            string? rawLabel = row.PhasorLabel;

            if (!string.IsNullOrWhiteSpace(rawLabel))
                lineName = StripPhaseSuffix(rawLabel);
        }

        // For calculated values, extract from description (also strip phase suffix)
        if (string.IsNullOrWhiteSpace(lineName))
        {
            string? extracted = ExtractLineNameFromDescription(row.Description ?? string.Empty);

            if (!string.IsNullOrWhiteSpace(extracted))
                lineName = StripPhaseSuffix(extracted);
        }

        // If we have a line name, ALWAYS build a name that incorporates it
        // This ensures consistent naming even for the first line at a station
        if (!string.IsNullOrWhiteSpace(lineName))
        {
            string lineMP = BuildMeasurementPointWithLine(baseMeasurementPoint, lineName);

            // Ensure we don't exceed 16 characters
            if (lineMP.Length > 16)
                lineMP = lineMP[..16];

            if (lineMP.Length <= 16)
            {
                // Check if this exact lineMP is already used (shouldn't happen for different lines)
                if (!usedMeasurementPoints.Contains(lineMP))
                    return lineMP;

                // If somehow it's already used, fall through to uniqueness handling
            }
        }

        // No line name - check if the base measurement point is already in use
        if (!usedMeasurementPoints.Contains(baseMeasurementPoint))
            return baseMeasurementPoint;

        // Fallback to hash-based uniqueness
        return MakeUnique16_NameFocused(baseMeasurementPoint, row.SignalID, usedMeasurementPoints);
    }

    /// <summary>
    /// Builds a MeasurementPoint name that incorporates the line name with underscore separator.
    /// Format: STATION_LINENAME (e.g.: ADMSCREEK1_WPEC) or STATION_LINE_X (e.g.: ADMSCREK1_BGLS_A)
    /// Compresses names by removing vowels when needed to fit 16-character limit.
    /// Preserves important keywords like UNIT and maintains line suffix integrity.
    /// </summary>
    private static string BuildMeasurementPointWithLine(string baseName, string lineName)
    {
        // Normalize the line name
        string normalizedLine = NormalizeAlphaNumeric(lineName);

        if (string.IsNullOrEmpty(normalizedLine))
            return baseName;

        // Extract line suffix if present (e.g.: BOGALUSA_LN_A -> base=BOGALUS, suffix=A)
        // After normalization: BOGALUSA_LN_A becomes BOGALUSALNA
        string? lineSuffix = null;
        string lineBase = normalizedLine;

        // Check for LN pattern followed by single character suffix
        // Pattern: ...LNA, ...LNB, ...LN1, etc.
        int index = normalizedLine.LastIndexOf("LN", StringComparison.OrdinalIgnoreCase);

        if (index >= 0 && index + 2 < normalizedLine.Length)
        {
            // Get everything after LN
            string afterLN = normalizedLine[(index + 2)..];

            // If it's a single character (A, B, C, 1, 2, etc.), treat as suffix
            if (afterLN.Length == 1)
            {
                lineBase = normalizedLine[..index];
                lineSuffix = afterLN;
            }
            else if (afterLN.Length > 1)
            {
                // Multiple chars after LN - take the last character as suffix
                char lastChar = afterLN[^1];

                if (char.IsLetterOrDigit(lastChar))
                {
                    lineBase = normalizedLine[..^1];
                    lineSuffix = lastChar.ToString();
                }
            }
        }

        // Compress the line base (remove vowels) but preserve important keywords like UNIT
        string compressedLine = CompressLineNamePreservingKeywords(lineBase);

        const int MaxLen = 16;

        if (!string.IsNullOrEmpty(lineSuffix))
        {
            // Format: BASE_LINE_X
            // Fixed parts: 2 underscores + suffix character
            int fixedChars = 2 + lineSuffix.Length;
            int availableForBaseAndLine = MaxLen - fixedChars;

            const int MinLineLen = 3;
            const int MinBaseLen = 4;

            int lineLen = Math.Min(compressedLine.Length, availableForBaseAndLine - MinBaseLen);
            int baseLen = availableForBaseAndLine - lineLen;

            if (lineLen < MinLineLen && compressedLine.Length >= MinLineLen)
            {
                lineLen = MinLineLen;
                baseLen = availableForBaseAndLine - lineLen;
            }

            baseLen = Math.Min(baseLen, baseName.Length);
            lineLen = Math.Min(lineLen, compressedLine.Length);

            string finalBase = baseName;

            if (baseName.Length > baseLen)
            {
                finalBase = CompressWordVowelsWhole(baseName);
                if (finalBase.Length > baseLen)
                    finalBase = finalBase[..baseLen];
            }

            string finalLine = compressedLine.Length > lineLen ? compressedLine[..lineLen] : compressedLine;
            string result = $"{finalBase}_{finalLine}_{lineSuffix}";

            if (result.Length <= MaxLen)
                return result;

            int excess = result.Length - MaxLen;

            if (finalBase.Length <= MinBaseLen + excess)
                return result;

            finalBase = finalBase[..^excess];
            result = $"{finalBase}_{finalLine}_{lineSuffix}";

            return result;
        }
        else
        {
            // Format: BASE_LINE (no suffix)
            const int FixedChars = 1; // underscore
            const int AvailableForBaseAndLine = MaxLen - FixedChars;
            const int MinLineLen = 3;
            const int MinBaseLen = 4;

            int lineLen = Math.Min(compressedLine.Length, AvailableForBaseAndLine - MinBaseLen);
            int baseLen = AvailableForBaseAndLine - lineLen;

            if (lineLen < MinLineLen && compressedLine.Length >= MinLineLen)
            {
                lineLen = MinLineLen;
                baseLen = AvailableForBaseAndLine - lineLen;
            }

            baseLen = Math.Min(baseLen, baseName.Length);
            lineLen = Math.Min(lineLen, compressedLine.Length);

            string finalBase = baseName;

            if (baseName.Length > baseLen)
            {
                finalBase = CompressWordVowelsWhole(baseName);
                if (finalBase.Length > baseLen)
                    finalBase = finalBase[..baseLen];
            }

            string finalLine = compressedLine.Length > lineLen ? compressedLine[..lineLen] : compressedLine;

            return $"{finalBase}_{finalLine}";
        }
    }

    /// <summary>
    /// Compresses a line name by removing vowels but preserving important keywords like UNIT
    /// and short names (3 chars or less) that would become unrecognizable.
    /// </summary>
    private static string CompressLineNamePreservingKeywords(string lineName)
    {
        // Short names (3 chars or less) should not be compressed - they become unrecognizable
        // e.g.: "KEO" -> "K" is not readable, keep as "KEO"
        if (lineName.Length <= 3)
            return lineName;

        // Check if line name contains UNIT - preserve it
        int index = lineName.IndexOf("UNIT", StringComparison.OrdinalIgnoreCase);

        if (index >= 0)
        {
            // Compress the part before UNIT, keep UNIT and everything after
            string beforeUnit = index > 0 ? lineName[..index] : string.Empty;
            string unitAndAfter = lineName[index..];

            string compressedBefore = CompressWordVowelsWhole(beforeUnit);
            return compressedBefore + unitAndAfter;
        }

        // No special keywords, just compress normally
        return CompressWordVowelsWhole(lineName);
    }

    // ========= Result type =========

    /// <summary>
    /// Represents the result of a configuration export operation.
    /// </summary>
    /// <param name="TotalLoaded">Total number of measurements loaded from the database.</param>
    /// <param name="Exported">Number of measurements successfully exported to CSV.</param>
    /// <param name="InvalidAlternateTagTooLong">Number of measurements with AlternateTag exceeding 16 characters (invalid for SEL export).</param>
    /// <param name="AlternateTagsGenerated">Number of new AlternateTag values generated and persisted to database.</param>
    public sealed record ConfigExportResult(
        int TotalLoaded,
        int Exported,
        int InvalidAlternateTagTooLong,
        int AlternateTagsGenerated);

    // ========= Data model =========

    /// <summary>
    /// Represents a measurement record loaded from the ActiveMeasurement view.
    /// </summary>
    /// <param name="SignalID">Unique identifier for the measurement signal.</param>
    /// <param name="PointTag">Full point tag identifier.</param>
    /// <param name="AlternateTag">User-defined or system-generated alternate tag (max 16 chars for SEL export).</param>
    /// <param name="Device">Device acronym/identifier.</param>
    /// <param name="Description">Human-readable description of the measurement.</param>
    /// <param name="SignalType">Type of signal (VPHA, VPHM, IPHA, IPHM, FREQ, DFDT, etc.).</param>
    /// <param name="EngineeringUnits">Engineering units for the measurement value.</param>
    /// <param name="Phase">Phase identifier (A, B, C, +, -, 0, 1, 2).</param>
    /// <param name="PhasorType">Type of phasor (V for voltage, I for current).</param>
    /// <param name="PhasorLabel">Label identifying the phasor or line name.</param>
    private sealed record MeasurementRow(
        string SignalID,
        string PointTag,
        string? AlternateTag,
        string? Device,
        string? Description,
        string? SignalType,
        string? EngineeringUnits,
        string? Phase,
        string? PhasorType,
        string? PhasorLabel);

    /// <summary>
    /// Represents a single row in the SEL configuration CSV output.
    /// </summary>
    /// <param name="DeviceAcronym">Device acronym/identifier.</param>
    /// <param name="Description">Human-readable description of the measurement.</param>
    /// <param name="MeasurementPoint">16-character measurement point name (SEL-compatible).</param>
    /// <param name="Quantity">SEL quantity descriptor (e.g., PhaseA.Voltage.Magnitude, Frequency).</param>
    private sealed record SelConfigRow(
        string DeviceAcronym,
        string Description,
        string MeasurementPoint,
        string Quantity);

    /// <summary>
    /// Represents a planned update to the AlternateTag field in the database.
    /// </summary>
    /// <param name="SignalID">Unique identifier for the measurement signal to update.</param>
    /// <param name="AlternateTag">New AlternateTag value to persist (16 chars or less).</param>
    private sealed record AlternateTagUpdate(
        string SignalID,
        string AlternateTag);

    /// <summary>
    /// Plans AlternateTag generation and persistence following strict permanence rules.
    /// Tracks new tags to generate, existing invalid tags, and provides mapping for export.
    /// </summary>
    private sealed class AlternateTagPlan
    {
        // DB writes: only new tags for previously empty AlternateTag
        public List<AlternateTagUpdate> Updates { get; } = [];

        // Map of signal ID to generated measurement points for rows with empty/null AlternateTag field
        public Dictionary<string, string> SignalIDMeasurementPointMap { get; } = new(StringComparer.OrdinalIgnoreCase);

        // Signal IDs with existing AlternateTag > 16 characters => invalid for SEL export
        public HashSet<string> InvalidForExportSignalIDs { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    // ========= DB read =========

    /// <summary>
    /// Retrieves a list of active measurements from the database.
    /// </summary>
    /// <param name="connection">
    /// The database connection to use for retrieving the active measurements.
    /// </param>
    /// <returns>
    /// A list of <see cref="MeasurementRow"/> objects representing the active measurements.
    /// </returns>
    /// <exception cref="ArgumentNullException">
    /// Thrown if the <paramref name="connection"/> is <c>null</c>.
    /// </exception>
    /// <exception cref="DbException">
    /// Thrown if an error occurs while executing the database query.
    /// </exception>
    private static List<MeasurementRow> LoadActiveMeasurements(DbConnection connection)
    {
        const string SQL = """
                       SELECT
                           SignalID,
                           PointTag,
                           AlternateTag,
                           Device,
                           Description,
                           SignalType,
                           EngineeringUnits,
                           Phase,
                           PhasorType,
                           PhasorLabel
                       FROM ActiveMeasurement
                       """;

        using DbCommand command = connection.CreateCommand();
        command.CommandText = SQL;

        using DbDataReader reader = command.ExecuteReader();
        List<MeasurementRow> rows = [];

        while (reader.Read())
        {
            // Treat Guid as string for simplicity
            string signalID = Convert.ToString(reader["SignalID"], CultureInfo.InvariantCulture) ?? string.Empty;
            string pointTag = Convert.ToString(reader["PointTag"], CultureInfo.InvariantCulture) ?? string.Empty;

            string? alternateTag = reader["AlternateTag"] is DBNull ? null : Convert.ToString(reader["AlternateTag"], CultureInfo.InvariantCulture);
            string? device = reader["Device"] is DBNull ? null : Convert.ToString(reader["Device"], CultureInfo.InvariantCulture);
            string? description = reader["Description"] is DBNull ? null : Convert.ToString(reader["Description"], CultureInfo.InvariantCulture);
            string? signalType = reader["SignalType"] is DBNull ? null : Convert.ToString(reader["SignalType"], CultureInfo.InvariantCulture);
            string? engineeringUnits = reader["EngineeringUnits"] is DBNull ? null : Convert.ToString(reader["EngineeringUnits"], CultureInfo.InvariantCulture);
            string? phase = reader["Phase"] is DBNull ? null : Convert.ToString(reader["Phase"], CultureInfo.InvariantCulture);
            string? phasorType = reader["PhasorType"] is DBNull ? null : Convert.ToString(reader["PhasorType"], CultureInfo.InvariantCulture);
            string? phasorLabel = reader["PhasorLabel"] is DBNull ? null : Convert.ToString(reader["PhasorLabel"], CultureInfo.InvariantCulture);

            rows.Add(new MeasurementRow(
                SignalID: signalID,
                PointTag: pointTag,
                AlternateTag: string.IsNullOrWhiteSpace(alternateTag) ? null : alternateTag.Trim(),
                Device: string.IsNullOrWhiteSpace(device) ? null : device.Trim(),
                Description: string.IsNullOrWhiteSpace(description) ? null : description.Trim(),
                SignalType: string.IsNullOrWhiteSpace(signalType) ? null : signalType.Trim(),
                EngineeringUnits: string.IsNullOrWhiteSpace(engineeringUnits) ? null : engineeringUnits.Trim(),
                Phase: string.IsNullOrWhiteSpace(phase) ? null : phase.Trim(),
                PhasorType: string.IsNullOrWhiteSpace(phasorType) ? null : phasorType.Trim(),
                PhasorLabel: string.IsNullOrWhiteSpace(phasorLabel) ? null : phasorLabel.Trim()
            ));
        }

        return rows;
    }

    // ========= AlternateTag planning (permanence + duplicates policy) =========

    /// <summary>
    /// Builds an alternate tag plan for the provided measurement rows, ensuring compliance with
    /// permanence rules and handling duplicate policies. This method generates new alternate tags
    /// only for rows where the <c>AlternateTag</c> is null or empty, without modifying existing tags.
    /// </summary>
    /// <param name="rows">
    /// A list of measurement rows containing signal information, including existing alternate tags.
    /// </param>
    /// <param name="companyAcronym">
    /// The company acronym used as part of the alternate tag generation process.
    /// </param>
    /// <returns>
    /// An <see cref="AlternateTagPlan"/> object containing the results of the alternate tag planning process,
    /// including any newly generated tags and invalid tags.
    /// </returns>
    private static AlternateTagPlan BuildAlternateTagPlan(List<MeasurementRow> rows, string companyAcronym)
    {
        AlternateTagPlan plan = new();

        // Stable ordering defines what "first encountered" means for duplicates
        List<MeasurementRow> ordered = rows.OrderBy(row => row.Device).ThenBy(row => row.PointTag).ThenBy(r => r.SignalID).ToList();

        // Identify existing AlternateTags that are too long (>16 chars) - these are invalid for SEL export
        foreach (MeasurementRow row in ordered)
        {
            if (string.IsNullOrWhiteSpace(row.AlternateTag))
                continue;

            string alternateTag = row.AlternateTag!.Trim();

            if (alternateTag.Length > 16)
            {
                // User-defined for other purposes; invalid for SEL export. Do not modify in DB.
                plan.InvalidForExportSignalIDs.Add(row.SignalID);
            }
        }

        // Generate AlternateTag ONLY for rows where AlternateTag is empty/null.
        // We generate a base 16-char name WITHOUT adding suffix (suffixing is per-quantity during export).
        // If no meaningful name can be derived, skip the row (don't persist anything).
        for (int i = 0; i < ordered.Count; i++)
        {
            MeasurementRow row = ordered[i];

            if (plan.InvalidForExportSignalIDs.Contains(row.SignalID))
                continue;

            if (!string.IsNullOrWhiteSpace(row.AlternateTag))
                continue; // Already has valid AlternateTag, don't modify

            string? baseName = BuildHumanBaseTag16(row.PointTag, companyAcronym);

            // If no meaningful name can be derived, skip this row entirely
            if (string.IsNullOrWhiteSpace(baseName))
                continue;

            // Enforce 16 character hard limit
            if (baseName.Length > 16)
                baseName = baseName[..16];

            plan.SignalIDMeasurementPointMap[row.SignalID] = baseName;
            plan.Updates.Add(new AlternateTagUpdate(row.SignalID, baseName));

            // Update in-memory record so downstream can rely on AlternateTag being set
            int index = rows.IndexOf(row);

            if (index >= 0)
                rows[index] = row with { AlternateTag = baseName };

            ordered[i] = row with { AlternateTag = baseName };
        }

        return plan;
    }

    // ========= Persist updates (only for NEW tags) =========

    /// <summary>
    /// Persists newly generated alternate tags for measurements in the database, adhering to permanence rules.
    /// </summary>
    /// <param name="connection">
    /// The database connection used to execute the update commands.
    /// </param>
    /// <param name="updates">
    /// A list of alternate tag updates, where each update specifies a SignalID and its corresponding new AlternateTag.
    /// </param>
    /// <remarks>
    /// This method updates the <c>AlternateTag</c> field in the database only if it is currently <c>NULL</c> or empty.
    /// Existing alternate tags are never modified, ensuring strict adherence to permanence rules.
    /// </remarks>
    /// <exception cref="ArgumentNullException">
    /// Thrown if <paramref name="connection"/> or <paramref name="updates"/> is <c>null</c>.
    /// </exception>
    /// <exception cref="DbException">
    /// Thrown if an error occurs while executing the database commands.
    /// </exception>
    private static void PersistAlternateTagUpdates(DbConnection connection, List<AlternateTagUpdate> updates)
    {
        // Only write if AlternateTag is NULL/empty (per permanence rules).
        const string SQL = """
                           UPDATE Measurement
                           SET AlternateTag = @AlternateTag
                           WHERE SignalID = @SignalID AND
                                 (AlternateTag IS NULL OR AlternateTag = '')
                           """;

        using DbCommand command = connection.CreateCommand();
        command.CommandText = SQL;

        DbParameter signalID = command.CreateParameter();
        signalID.ParameterName = "@SignalID";
        command.Parameters.Add(signalID);

        DbParameter alternateTag = command.CreateParameter();
        alternateTag.ParameterName = "@AlternateTag";
        command.Parameters.Add(alternateTag);

        foreach (AlternateTagUpdate update in updates)
        {
            signalID.Value = update.SignalID;
            alternateTag.Value = update.AlternateTag;
            command.ExecuteNonQuery();
        }
    }

    // ========= 16-char base name builder (name-focused, no type encoding) =========

    /// <summary>
    /// 
    /// </summary>
    /// <param name="pointTag"></param>
    /// <param name="companyAcronym"></param>
    /// <returns></returns>
    private static string? BuildHumanBaseTag16(string pointTag, string companyAcronym)
    {
        // Goal: Create memorable <=16 character identifier derived from device acronym.
        // Avoids encoding signal type (IA/MAG/etc.) because SEL Quantity field supplies type.
        // Does NOT include line/phasor names - those are added separately in BuildMeasurementPointWithLine.
        // Returns null if no meaningful name can be derived.
        if (string.IsNullOrWhiteSpace(pointTag))
            return null;

        string raw = pointTag.Trim().ToUpperInvariant();
        string[] excludedPrefixes = [companyAcronym, ..Settings.ExcludedPrefixes];

        foreach (string excludedPrefix in excludedPrefixes)
        {
            // Strip excluded prefixes if present (e.g., "ACME_STATION_1-..." -> "STATION_1-...")
            string prefix = excludedPrefix.Trim().ToUpperInvariant();

            if (!string.IsNullOrWhiteSpace(prefix))
            {
                // Check for prefix followed by underscore
                string prefixWithUnderscore = $"{prefix}_";

                if (raw.StartsWith(prefixWithUnderscore, StringComparison.OrdinalIgnoreCase))
                    raw = raw[prefixWithUnderscore.Length..];

                // Also check for prefix followed by dash
                string prefixWithDash = $"{prefix}-";

                if (raw.StartsWith(prefixWithDash, StringComparison.OrdinalIgnoreCase))
                    raw = raw[prefixWithDash.Length..];

                // Finally, check for prefix in the raw
                if (raw.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    raw = raw[prefix.Length..];
            }
        }

        // Split into left/right around '-'
        string[] dashParts = raw.Split('-', 2);
        string left = dashParts[0];

        // Tokenize left on '_'
        string[] leftTokens = left.Split(['_'], StringSplitOptions.RemoveEmptyEntries);

        // Left-side: find "station-ish" tokens and unit number
        List<string> nameTokens = [];
        string? unit = null;

        int i = 0;

        while (i < leftTokens.Length && IsSystemPrefix(leftTokens[i]))
            i++;

        for (; i < leftTokens.Length; i++)
        {
            string t = leftTokens[i].Trim();

            if (t.Length == 0)
                continue;

            if (unit is null && IsUnitToken(t))
            {
                unit = t;
                continue;
            }

            if (IsNonNameMarker(t))
                break;

            if (IsNameToken(t))
                nameTokens.Add(t);
        }

        // If no name tokens found, we can't build a meaningful name
        if (nameTokens.Count == 0)
            return null;

        // Build station name from device acronym only - DO NOT include right-side hints
        // The line/phasor name will be added later by BuildMeasurementPointWithLine
        string station = BuildStationFromTokens(nameTokens, unit);

        string packed = NormalizeAlphaNumeric(station);

        if (packed.Length == 0)
            return null;

        return packed.Length <= 16 ? packed : packed[..16];
    }

    /// <summary>
    /// Builds a station identifier from the provided name tokens and unit information.
    /// </summary>
    /// <param name="nameTokens">
    /// A list of tokens representing parts of the station name. Each token is typically derived 
    /// from the device acronym and represents a meaningful segment of the station's identity.
    /// </param>
    /// <param name="unit">
    /// An optional unit identifier, such as a numeric or alphanumeric value, that further 
    /// distinguishes the station. This value is appended to the station identifier if provided.
    /// </param>
    /// <returns>
    /// A compressed and normalized station identifier constructed by combining the name tokens 
    /// and unit. The identifier is designed to be concise and human-readable, with vowels stripped 
    /// from all but the last token to ensure clarity.
    /// </returns>
    private static string BuildStationFromTokens(List<string> nameTokens, string? unit)
    {
        // Example: GRAND + GULF + 1 => GRNDGULF1
        // Rule: vowel-strip all but last token (keeps last token readable)
        StringBuilder sb = new();

        for (int i = 0; i < nameTokens.Count; i++)
        {
            string t = nameTokens[i].ToUpperInvariant();
            sb.Append(i < nameTokens.Count - 1 ? CompressWordVowels(t) : t);
        }

        if (!string.IsNullOrWhiteSpace(unit))
            sb.Append(unit);

        return sb.ToString();
    }

    /// <summary>
    /// Removes vowels from the specified word, except for the first character, 
    /// to create a compressed version of the word.
    /// </summary>
    /// <param name="word">The word to compress by removing vowels.</param>
    /// <returns>
    /// A compressed version of the word with vowels removed, except for the first character.
    /// If the word has a length of 2 or less, it is returned unchanged.
    /// </returns>
    private static string CompressWordVowels(string word)
    {
        if (word.Length <= 2)
            return word;

        StringBuilder sb = new(word.Length);
        sb.Append(word[0]);

        for (int i = 1; i < word.Length; i++)
        {
            char c = word[i];

            if ("AEIOU".Contains(c))
                continue;

            sb.Append(c);
        }

        return sb.ToString();
    }

    /// <summary>
    /// Compresses a word by removing all vowels except the first character.
    /// </summary>
    /// <param name="s">The input string to be compressed.</param>
    /// <returns>
    /// A compressed version of the input string with vowels removed, 
    /// except for the first character. If the input string has a length 
    /// of 2 or less, it is returned unchanged.
    /// </returns>
    private static string CompressWordVowelsWhole(string s)
    {
        if (s.Length <= 2)
            return s;

        StringBuilder sb = new(s.Length);
        sb.Append(s[0]);

        for (int i = 1; i < s.Length; i++)
        {
            char c = s[i];

            if ("AEIOU".Contains(c))
                continue;

            sb.Append(c);
        }

        return sb.ToString();
    }

    /// <summary>
    /// Normalizes the input string by removing all non-alphanumeric characters
    /// and converting it to uppercase.
    /// </summary>
    /// <param name="s">The input string to normalize.</param>
    /// <returns>A normalized string containing only uppercase alphanumeric characters.</returns>
    private static string NormalizeAlphaNumeric(string s)
    {
        StringBuilder sb = new(s.Length);

        foreach (char ch in s.ToUpperInvariant())
        {
            if (ch is >= 'A' and <= 'Z' or >= '0' and <= '9')
                sb.Append(ch);
        }

        return sb.ToString();
    }

    /// <summary>
    /// Determines whether the specified token represents a system prefix.
    /// </summary>
    /// <param name="token">The token to evaluate.</param>
    /// <returns>
    /// <c>true</c> if the token is a recognized system prefix (e.g., "PMU", "PDC", "SUB", or "SITE");
    /// otherwise, <c>false</c>.
    /// </returns>
    private static bool IsSystemPrefix(string token)
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
    private static bool IsUnitToken(string token)
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
    private static bool IsNameToken(string token)
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

    // ========= Per-quantity uniqueness suffixing =========

    /// <summary>
    /// Generates a unique, alphanumeric string with a maximum length of 16 characters, 
    /// ensuring it does not conflict with existing entries in the provided set of used names.
    /// </summary>
    /// <param name="baseName16">
    /// The base name to use as the starting point for generating a unique name. 
    /// This value must already be alphanumeric and no longer than 16 characters.
    /// </param>
    /// <param name="stableKey">
    /// A stable key used to generate deterministic suffixes for ensuring uniqueness.
    /// </param>
    /// <param name="used">
    /// A set of names that are already in use, against which the generated name must be checked for uniqueness.
    /// </param>
    /// <returns>
    /// A unique, alphanumeric string with a maximum length of 16 characters that does not conflict with the provided set of used names.
    /// </returns>
    private static string MakeUnique16_NameFocused(string baseName16, string stableKey, HashSet<string> used)
    {
        // baseName16 should already be <= 16 and alphanumeric only.
        // Add suffix only when needed within this 'used' set (per quantity).
        // Strategy: Keep base name intact, add lowercase suffix using progressively longer suffixes
        // until a unique name is found.
        string baseName = NormalizeAlphaNumeric(baseName16);

        if (baseName.Length > 16)
            baseName = baseName[..16];

        if (!used.Contains(baseName))
            return baseName;

        uint seed = StableUInt32(stableKey);

        // Try progressively longer suffixes until we find a unique one
        for (int suffixLen = 2; suffixLen <= 6; suffixLen++)
        {
            int keep = 16 - suffixLen;

            if (keep < 1)
                keep = 1;

            // Truncate base if needed, but NEVER repeat it
            string prefix = baseName.Length <= keep ? baseName : baseName[..keep];

            int attempts = suffixLen switch { 2 => 64, 3 => 128, 4 => 256, _ => 512 };

            for (uint i = 0; i < attempts; i++)
            {
                uint value = seed + i;
                string suffix = Base32Suffix(value, suffixLen).ToLowerInvariant();
                string candidate = prefix + suffix;

                // Candidate should be exactly prefix + suffix, no padding/repetition
                if (candidate.Length > 16)
                    candidate = candidate[..16];

                if (!used.Contains(candidate))
                    return candidate;
            }
        }

        return Hash16(stableKey);
    }

    /// <summary>
    /// Generates a Base32-encoded suffix of a specified length from a given numeric value.
    /// </summary>
    /// <param name="value">The numeric value to encode into a Base32 suffix.</param>
    /// <param name="len">The desired length of the resulting Base32 suffix.</param>
    /// <returns>A Base32-encoded string of the specified length, derived from the input value.</returns>
    /// <remarks>
    /// The method uses a custom Base32 alphabet consisting of uppercase letters (excluding 'I' and 'O') 
    /// and digits (excluding '0' and '1') to ensure readability. It supports suffix lengths up to 8 characters.
    /// </remarks>
    private static string Base32Suffix(uint value, int len)
    {
        const string Alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";

        // Support longer suffixes (up to 8 chars)
        Span<char> chars = stackalloc char[8];

        for (int i = chars.Length - 1; i >= 0; i--)
        {
            chars[i] = Alphabet[(int)(value % (uint)Alphabet.Length)];
            value /= (uint)Alphabet.Length;
        }

        int start = chars.Length - len;

        if (start < 0)
            start = 0;

        return new string(chars[start..]);
    }

    /// <summary>
    /// Computes a stable 32-bit unsigned integer hash for the specified string.
    /// </summary>
    /// <param name="s">The input string to hash.</param>
    /// <returns>A 32-bit unsigned integer hash value derived from the input string.</returns>
    private static uint StableUInt32(string s)
    {
        byte[] data = SHA256.HashData(Encoding.UTF8.GetBytes(s));
        return BitConverter.ToUInt32(data, 0);
    }

    /// <summary>
    /// Generates a 16-character alphanumeric hash-based string derived from the specified stable key.
    /// </summary>
    /// <param name="stableKey">The input string used as a stable key for generating the hash.</param>
    /// <returns>A 16-character alphanumeric string that is unique and non-repeating, derived from the input key.</returns>
    private static string Hash16(string stableKey)
    {
        // Generate a readable hash-based 16-character name that doesn't repeat patterns
        // Uses two different hash seeds to create distinct 8-character segments
        uint a = StableUInt32(stableKey);
        uint b = StableUInt32($"{stableKey}|B");

        // Use 8 chars from first hash, 8 from second - no repetition
        string part1 = Base32Suffix(a, 8);
        string part2 = Base32Suffix(b, 8);

        return (part1 + part2)[..16];
    }

    // ========= Quantity mapping =========

    /// <summary>
    /// Maps the quantity of a measurement based on its signal type, phase, and description.
    /// </summary>
    /// <param name="row">
    /// The <see cref="MeasurementRow"/> containing the measurement details, such as signal type, phase, and description.
    /// </param>
    /// <returns>
    /// A string representing the mapped quantity, or <c>null</c> if no mapping is applicable.
    /// </returns>
    private static string? MapQuantity(MeasurementRow row)
    {
        string signalType = (row.SignalType ?? string.Empty).Trim().ToUpperInvariant();

        if (signalType == "FREQ")
            return "Frequency";

        if (signalType == "DFDT")
            return "Frequency.DxDt";

        string? phase = NormalizePhase(row.Phase);
        string desc = row.Description ?? string.Empty;

        // If phase is null for phasor types, check description for hints
        if (string.IsNullOrWhiteSpace(phase))
        {
            if (Contains(desc, " A ") || Contains(desc, "_A_") || Contains(desc, "_A-"))
                phase = "A";
            else if (Contains(desc, " B ") || Contains(desc, "_B_") || Contains(desc, "_B-"))
                phase = "B";
            else if (Contains(desc, " C ") || Contains(desc, "_C_") || Contains(desc, "_C-"))
                phase = "C";
            else if (Contains(desc, " 0 ") || Contains(desc, "_0_") || Contains(desc, "_0-") || Contains(desc, "ZERO"))
                phase = "0";
            else if (Contains(desc, " - ") || Contains(desc, "_-_") || Contains(desc, "_--") || Contains(desc, "NEG"))
                phase = "1";
            else if (Contains(desc, " + ") || Contains(desc, "_+_") || Contains(desc, "_+-") || Contains(desc, "POS"))
                phase = "2";
        }

        if (signalType == "VPHM" && phase is not null)
            return $"Phase{phase}.Voltage.Magnitude";

        if (signalType == "VPHA" && phase is not null)
            return $"Phase{phase}.Voltage.Angle";

        if (signalType == "IPHM" && phase is not null)
            return $"Phase{phase}.Current.Magnitude";

        if (signalType == "IPHA" && phase is not null)
            return $"Phase{phase}.Current.Angle";

        // NOTE: Only enable if Power System Model is not defined in SynchroWave.
        // When enabled, note that values in SynchroWave are base units (W, VAR, VA),
        // so these scaled Mega-quantities will show up unscaled.
        if (Settings.MapPowerQuantities)
        {
            if (Contains(desc, "APPARENT POWER") && phase is not null)
                return $"Phase{phase}.Power.Apparent";

            if (Contains(desc, "REACTIVE POWER") && phase is not null)
                return $"Phase{phase}.Power.Reactive";

            if (Contains(desc, "ACTIVE POWER") && phase is not null)
                return $"Phase{phase}.Power.Real";

            if (Contains(desc, "3-PHASE MVAR"))
                return "ThreePhase.Power.Reactive";

            if (Contains(desc, "3-PHASE MVA"))
                return "ThreePhase.Power.Apparent";

            if (Contains(desc, "3-PHASE MW"))
                return "ThreePhase.Power.Real";
        }

        // These calculations are typically handled from within SynchroWave and are voltage focused
        //if (Contains(desc, "ANGLE DIFFERENCE"))
        //    return "PAD";

        return null;
    }

    /// <summary>
    /// Determines whether a specified substring occurs within a given string, 
    /// using a case-insensitive comparison.
    /// </summary>
    /// <param name="haystack">The string to search within.</param>
    /// <param name="needle">The substring to search for.</param>
    /// <returns>
    /// <c>true</c> if the <paramref name="needle"/> is found within the <paramref name="haystack"/>; 
    /// otherwise, <c>false</c>.
    /// </returns>
    private static bool Contains(string haystack, string needle)
    {
        return haystack.Contains(needle, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Normalizes the phase identifier to a standardized format.
    /// </summary>
    /// <param name="phase">The phase identifier to normalize. Can be <c>null</c>.</param>
    /// <returns>
    /// A standardized phase identifier as a string, or <c>null</c> if the input is invalid or unrecognized.
    /// </returns>
    /// <remarks>
    /// This method trims and converts the input phase identifier to uppercase, mapping it to a predefined set of values:
    /// <list type="bullet">
    /// <item><description>"A", "B", "C", "0", "+", "-", "1", and "2" are recognized and returned as-is or mapped to their standardized equivalents.</description></item>
    /// <item><description>Unrecognized or <c>null</c> values result in a <c>null</c> return value.</description></item>
    /// </list>
    /// </remarks>
    private static string? NormalizePhase(string? phase)
    {
        return phase?.Trim().ToUpperInvariant() switch
        {
            "A" => "A",
            "B" => "B",
            "C" => "C",
            "0" => "0",
            "+" => "1",
            "-" => "2",
            "1" => "1",
            "2" => "2",
            _ => null
        };
    }

    // ========= CSV writing =========

    /// <summary>
    /// Writes the SEL-compatible CSV configuration file for synchrophasor measurements.
    /// </summary>
    /// <param name="path">
    /// The file path where the CSV configuration will be written.
    /// </param>
    /// <param name="rows">
    /// The list of configuration rows to be written to the CSV file. Each row contains
    /// details such as device acronym, description, measurement point, and quantity.
    /// </param>
    /// <remarks>
    /// The method generates a CSV file with a predefined structure, ensuring that each row
    /// adheres to the SEL-compatible format. It writes the header row followed by the data rows,
    /// encoding the file in UTF-8 without a BOM (Byte Order Mark).
    /// </remarks>
    private static void WriteSelCSV(string path, List<SelConfigRow> rows)
    {
        using FileStream stream = File.Create(path);
        using StreamWriter writer = new(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        writer.WriteLine("DeviceAcronym,Description,MeasurementPoint,Quantity");

        foreach (SelConfigRow row in rows)
        {
            writer.WriteLine(string.Join(",",
                CSVField(row.DeviceAcronym),
                CSVField(row.Description),
                CSVField(row.MeasurementPoint),
                CSVField(row.Quantity)
            ));
        }
    }
}
