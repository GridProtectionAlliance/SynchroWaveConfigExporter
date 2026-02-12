//******************************************************************************************************
//  Program.cs - Gbtc
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

namespace SynchroWaveConfigExporter;

internal class Program
{
    public const string ConfigFileNameTemplate = "{0}.exe.config";

    // The following code attempts to acquire database connection information from the host openHistorian
    // service assumed to be on the same machine as the SynchroWave STTP config exporter application.

    private static string GetConfigFilePath()
    {
        #pragma warning disable CA1416 // Application currently targets Windows only
        using RegistryKey? gpaKey = Registry.LocalMachine.OpenSubKey($@"SOFTWARE\Grid Protection Alliance\{Settings.HostService}");
        string installPath = gpaKey?.GetValue("InstallPath") as string ?? Settings.DefaultInstallPath;
        #pragma warning restore CA1416

        return Path.Combine(installPath, string.Format(ConfigFileNameTemplate, Settings.HostService));
    }

    private static string GetCoreDataProviderString(string dataProviderString)
    {
        // TODO: Currently only providing data provider string translations for SQL Server and SQLite, add more as needed:
        const string SqlClientDataProviderString = "AssemblyName=Microsoft.Data.SqlClient; ConnectionType=Microsoft.Data.SqlClient.SqlConnection";
        const string SqliteDataProviderString = "AssemblyName=Microsoft.Data.Sqlite; ConnectionType=Microsoft.Data.Sqlite.SqliteConnection";

        if (dataProviderString.Contains("System.Data.SQLite"))
            return SqliteDataProviderString;

        if (dataProviderString.Contains("System.Data.SqlClient"))
            return SqlClientDataProviderString;

        return dataProviderString;
    }

    private static AdoDataConnection OpenConnection()
    {
        string configFilePath = GetConfigFilePath();

        if (!File.Exists(configFilePath))
            throw new InvalidOperationException($"{Settings.HostService} configuration database cannot be opened, config file \"{configFilePath}\" cannot be found.");

        // Load needed database settings from target config file
        XDocument serviceConfig = XDocument.Load(configFilePath);

        string? connectionString = serviceConfig
            .Descendants("systemSettings")
            .SelectMany(systemSettings => systemSettings.Elements("add"))
            .Where(element => "ConnectionString".Equals((string)element.Attribute("name")!, StringComparison.OrdinalIgnoreCase))
            .Select(element => (string)element.Attribute("value")!)
            .FirstOrDefault();

        ArgumentNullException.ThrowIfNull(connectionString);

        Dictionary<string, string> connectionSettings = connectionString.ParseKeyValuePairs();

        connectionSettings["Trusted_Connection"] = "true";
        connectionSettings["TrustServerCertificate"] = "true";

        connectionString = connectionSettings.JoinKeyValuePairs();

        string? dataProviderString = serviceConfig
            .Descendants("systemSettings")
            .SelectMany(systemSettings => systemSettings.Elements("add"))
            .Where(element => "DataProviderString".Equals((string)element.Attribute("name")!, StringComparison.OrdinalIgnoreCase))
            .Select(element => (string)element.Attribute("value")!)
            .FirstOrDefault();

        ArgumentNullException.ThrowIfNull(dataProviderString);

        // Open database
        return new AdoDataConnection(connectionString, GetCoreDataProviderString(dataProviderString));
    }

    private static string GetCompanyAcronym()
    {
        string configFilePath = GetConfigFilePath();

        if (!File.Exists(configFilePath))
            throw new InvalidOperationException($"{Settings.HostService} configuration database cannot be opened, config file \"{configFilePath}\" cannot be found.");

        // Load needed database settings from target config file
        XDocument serviceConfig = XDocument.Load(configFilePath);

        string? companyAcronym = serviceConfig
            .Descendants("systemSettings")
            .SelectMany(systemSettings => systemSettings.Elements("add"))
            .Where(element => "CompanyAcronym".Equals((string)element.Attribute("name")!, StringComparison.OrdinalIgnoreCase))
            .Select(element => (string)element.Attribute("value")!)
            .FirstOrDefault();
        
        return companyAcronym ?? "GPA";
    }

    private static void LoadSettings()
    {
        // Define settings for the application
        ConfigSettings settings = new()
        {
            SQLite = ConfigurationOperation.Disabled,
            INIFile = ConfigurationOperation.ReadWrite,
            ConfiguredINIPath = FilePath.GetAbsolutePath("")
        };

        // Define settings for service components
        Settings.DefineSettings(settings, ConfigSettings.SystemSettingsCategory);

        // Bind settings to configuration sources
        settings.Bind(new ConfigurationBuilder()
            .ConfigureGemstoneDefaults(settings));

        settings.Save(true);
    }
    
    public static void Main(string[] args)
    {
        LoadSettings();

        Console.WriteLine("SynchroWave STTP Configuration Exporter");
        Console.WriteLine("=======================================");
        Console.WriteLine();

        try
        {
            using AdoDataConnection database = OpenConnection();

            Console.WriteLine($"Connected to database: {Settings.HostService}");
            Console.WriteLine();

            // Export SEL STTP signal mappings
            Console.WriteLine("Exporting SEL STTP Signal Mappings...");
            SttpConfigExporter.ConfigExportResult configResult = SttpConfigExporter.Export(database.Connection, GetCompanyAcronym());

            Console.WriteLine($"  Total measurements loaded: {configResult.TotalLoaded:N0}");
            Console.WriteLine($"  Measurements exported: {configResult.Exported:N0}");
            Console.WriteLine($"  Alternate tags generated: {configResult.AlternateTagsGenerated:N0}");
            Console.WriteLine($"  Invalid alternate tags (too long): {configResult.InvalidAlternateTagTooLong:N0}");
            Console.WriteLine();

            // Export power system model (stations, buses, lines)
            Console.WriteLine("Exporting Power System Model...");
            PowerSystemModelExporter.ModelExportResult modelResult = PowerSystemModelExporter.Export(database.Connection);

            Console.WriteLine($"  Total devices analyzed: {modelResult.TotalDevicesAnalyzed:N0}");
            Console.WriteLine($"  Devices with phasors: {modelResult.DevicesWithPhasors:N0}");
            Console.WriteLine($"  Total phasors loaded: {modelResult.TotalPhasorsLoaded:N0}");
            Console.WriteLine($"  Coordinate groups found: {modelResult.CoordinateGroupsFound:N0}");
            Console.WriteLine($"  DFR devices found: {modelResult.DFRDevicesFound:N0}");
            Console.WriteLine($"  Line terminal, PMU devices found: {modelResult.LineTerminalDevicesFound:N0}");
            Console.WriteLine($"  Stations skipped (no name): {modelResult.StationsSkippedNoName:N0}");
            Console.WriteLine($"  Stations skipped (no voltage): {modelResult.StationsSkippedNoVoltage:N0}");
            Console.WriteLine($"  Stations exported: {modelResult.StationsExported:N0}");
            Console.WriteLine($"  Buses exported: {modelResult.BusesExported:N0}");
            Console.WriteLine($"  Lines exported: {modelResult.LinesExported:N0}");

            if (modelResult.SampleDeviceAcronyms.Count > 0)
            {
                Console.WriteLine();
                Console.WriteLine("  Sample device acronyms:");
                
                foreach (string acronym in modelResult.SampleDeviceAcronyms)
                    Console.WriteLine($"    - {acronym}");
            }

            if (modelResult.SampleDeviceNames.Count > 0)
            {
                Console.WriteLine();
                Console.WriteLine("  Sample device names:");
                
                foreach (string name in modelResult.SampleDeviceNames)
                    Console.WriteLine($"    - {name}");
            }

            if (modelResult.SamplePhasorBaseKVs.Count > 0)
            {
                Console.WriteLine();
                Console.WriteLine($"  Sample phasor BaseKV values: {string.Join(", ", modelResult.SamplePhasorBaseKVs)}");
            }

            Console.WriteLine();

            // Export dash menu (hierarchical folder paths for stations and lines)
            Console.WriteLine("Exporting Dash Menu...");
            DashMenuExporter.DashMenuExportResult dashMenuResult = DashMenuExporter.Export();

            Console.WriteLine($"  Stations loaded: {dashMenuResult.StationsLoaded:N0}");
            Console.WriteLine($"  Buses loaded: {dashMenuResult.BusesLoaded:N0}");
            Console.WriteLine($"  Lines loaded: {dashMenuResult.LinesLoaded:N0}");
            Console.WriteLine($"  Substation paths: {dashMenuResult.SubstationPaths:N0}");
            
            if (Settings.IncludeVoltageGroupedLines)
            {
                Console.WriteLine($"  Voltage level groups: {dashMenuResult.VoltageLevelGroups:N0}");
                Console.WriteLine($"  Line paths (by voltage): {dashMenuResult.LinePaths:N0}");
            }
            
            Console.WriteLine($"  Total paths exported: {dashMenuResult.TotalPaths:N0}");
            Console.WriteLine();

            Console.WriteLine("Export Complete!");
            Console.WriteLine();
            Console.WriteLine("Output Files:");
            Console.WriteLine($"  SEL Signal Mappings: {Settings.SttpSelConfigCsvPath}");
            Console.WriteLine($"  Stations: {modelResult.StationsPath}");
            Console.WriteLine($"  Buses: {modelResult.BusesPath}");
            Console.WriteLine($"  Lines: {modelResult.LinesPath}");
            Console.WriteLine($"  Dash Menu: {dashMenuResult.OutputPath}");
            Console.WriteLine();

            // Highlight potential issues
            if (modelResult.StationsExported == 0)
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine("WARNING: No stations were exported!");
                
                if (modelResult.TotalDevicesAnalyzed == 0)
                    Console.WriteLine("  - No devices were loaded from the database.");
                else if (modelResult.DevicesWithPhasors == 0)
                    Console.WriteLine("  - No devices have phasors attached.");
                else if (modelResult.TotalPhasorsLoaded == 0)
                    Console.WriteLine("  - No phasors were loaded from the database.");
                else if (modelResult.CoordinateGroupsFound == 0)
                    Console.WriteLine("  - No devices have valid GPS coordinates.");
                else
                {
                    if (modelResult.StationsSkippedNoName > 0)
                        Console.WriteLine($"  - {modelResult.StationsSkippedNoName} group(s) skipped: could not extract station name from device acronym/name.");
                    if (modelResult.StationsSkippedNoVoltage > 0)
                        Console.WriteLine($"  - {modelResult.StationsSkippedNoVoltage} group(s) skipped: no phasors with valid voltage (BaseKV > 0).");
                    
                    if (modelResult is { DFRDevicesFound: 0, LineTerminalDevicesFound: 0 })
                        Console.WriteLine("  - No devices match standard patterns.");
                }
                
                Console.ResetColor();
                Console.WriteLine();
            }

            if (modelResult is { BusesExported: 0, StationsExported: > 0 })
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine("WARNING: No buses were exported despite having stations!");
                Console.WriteLine("  - Check that devices can be matched to stations by coordinates.");
                Console.ResetColor();
                Console.WriteLine();
            }

            if (modelResult is { LinesExported: 0, LineTerminalDevicesFound: > 0 })
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine($"WARNING: Found {modelResult.LineTerminalDevicesFound} line terminal devices but no lines were exported!");
                Console.WriteLine("  - Check that line terminal device names can be parsed (format: 'STATION-REMOTE {KV}KV').");
                Console.ResetColor();
                Console.WriteLine();
            }

            if (dashMenuResult.TotalPaths == 0)
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine("WARNING: No dash menu paths were exported!");
                Console.WriteLine("  - Check that the power system model CSV files exist and contain data.");
                Console.ResetColor();
                Console.WriteLine();
            }
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"ERROR: {ex.Message}");
            Console.WriteLine();
            Console.WriteLine("Stack Trace:");
            Console.WriteLine(ex.StackTrace);
            Console.ResetColor();
            Environment.ExitCode = 1;
        }
    }
}