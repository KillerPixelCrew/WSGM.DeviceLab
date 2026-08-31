using System;
using System.Collections.Generic;
using System.Linq;

namespace WSGM.DeviceLab.Inventory;

/// <summary>Deterministic bounds and structural checks shared by live and fixture inventories.</summary>
internal static class MachineInventoryNormalizer
{
    /// <summary>Canonicalizes every Stage 1 lane without inventing missing observations.</summary>
    /// <param name="inventory">Raw read-only observations.</param>
    /// <returns>Bounded deterministic inventory.</returns>
    public static MachineInventory Normalize(MachineInventory inventory)
    {
        ArgumentNullException.ThrowIfNull(inventory);
        return inventory with
        {
            GraphicsAdapters = OrderAndTake(OrEmpty(inventory.GraphicsAdapters), item => item.InstanceId),
            UsbInterfaces = OrderAndTake(OrEmpty(inventory.UsbInterfaces), item => item.InstanceId),
            WmiClasses = OrEmpty(inventory.WmiClasses)
                .OrderBy(item => item.Namespace, StringComparer.Ordinal)
                .ThenBy(item => item.ClassName, StringComparer.Ordinal)
                .Take(InventoryLimits.MaximumEndpointsPerLane)
                .ToArray(),
            SerialEndpoints = OrEmpty(inventory.SerialEndpoints)
                .Select(NormalizeSerial)
                .OrderBy(item => item.InstanceId, StringComparer.Ordinal)
                .Take(InventoryLimits.MaximumEndpointsPerLane)
                .ToArray(),
            Sensors = OrEmpty(inventory.Sensors)
                .Select(NormalizeSensor)
                .GroupBy(item => item.Api)
                .SelectMany(group => group
                    .OrderBy(item => item.InstanceId, StringComparer.Ordinal)
                    .Take(InventoryLimits.MaximumEndpointsPerLane))
                .OrderBy(item => item.Api)
                .ThenBy(item => item.InstanceId, StringComparer.Ordinal)
                .ToArray(),
            InputBackends = OrEmpty(inventory.InputBackends)
                .Select(backend => backend with
                {
                    Endpoints = OrEmpty(backend.Endpoints)
                        .Select(NormalizeInputEndpoint)
                        .OrderBy(endpoint => endpoint.EndpointId, StringComparer.Ordinal)
                        .Take(InventoryLimits.MaximumEndpointsPerLane)
                        .ToArray(),
                })
                .GroupBy(backend => (backend.Backend, backend.View))
                .Select(group => group
                    .OrderBy(backend => backend.Access)
                    .First())
                .OrderBy(backend => backend.Backend)
                .ThenBy(backend => backend.View)
                .Take(InventoryLimits.MaximumInputBackendViews)
                .ToArray(),
            NativeBinaries = OrEmpty(inventory.NativeBinaries)
                .Select(binary => binary with
                {
                    Exports = OrEmpty(binary.Exports)
                        .Distinct(StringComparer.Ordinal)
                        .Order(StringComparer.Ordinal)
                        .Take(InventoryLimits.MaximumNativeExports)
                        .ToArray(),
                })
                .OrderBy(binary => binary.Path, StringComparer.OrdinalIgnoreCase)
                .Take(InventoryLimits.MaximumSystemEntriesPerLane)
                .ToArray(),
            Processes = OrEmpty(inventory.Processes)
                .Select(process => process with
                {
                    LoadedModulePaths = OrEmpty(process.LoadedModulePaths)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .Order(StringComparer.OrdinalIgnoreCase)
                        .Take(InventoryLimits.MaximumEndpointsPerLane)
                        .ToArray(),
                })
                .OrderBy(process => process.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(process => process.ProcessId)
                .Take(InventoryLimits.MaximumSystemEntriesPerLane)
                .ToArray(),
            Services = OrEmpty(inventory.Services)
                .OrderBy(service => service.Name, StringComparer.OrdinalIgnoreCase)
                .Take(InventoryLimits.MaximumSystemEntriesPerLane)
                .ToArray(),
            ScheduledTasks = OrEmpty(inventory.ScheduledTasks)
                .OrderBy(task => task.Path, StringComparer.OrdinalIgnoreCase)
                .Take(InventoryLimits.MaximumSystemEntriesPerLane)
                .ToArray(),
            Providers = OrEmpty(inventory.Providers)
                .OrderBy(provider => provider.Kind, StringComparer.Ordinal)
                .ThenBy(provider => provider.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(provider => provider.Context, StringComparer.OrdinalIgnoreCase)
                .Take(InventoryLimits.MaximumSystemEntriesPerLane)
                .ToArray(),
            ResourceConflicts = OrEmpty(inventory.ResourceConflicts)
                .OrderBy(conflict => conflict.ResourceId, StringComparer.Ordinal)
                .ThenBy(conflict => conflict.Owner, StringComparer.OrdinalIgnoreCase)
                .Take(InventoryLimits.MaximumSystemEntriesPerLane)
                .ToArray(),
            TopologyGenerations = OrEmpty(inventory.TopologyGenerations)
                .OrderByDescending(observation => observation.Generation)
                .ThenBy(observation => observation.InstanceId, StringComparer.Ordinal)
                .Take(InventoryLimits.MaximumEndpointsPerLane)
                .OrderBy(observation => observation.Generation)
                .ThenBy(observation => observation.InstanceId, StringComparer.Ordinal)
                .ToArray(),
            CollectionIssues = OrEmpty(inventory.CollectionIssues)
                .OrderBy(issue => issue.Lane, StringComparer.Ordinal)
                .ThenBy(issue => issue.Error, StringComparer.Ordinal)
                .Take(InventoryLimits.MaximumEndpointsPerLane)
                .ToArray(),
        };
    }

    private static SerialEndpointInventory NormalizeSerial(SerialEndpointInventory endpoint)
    {
        bool malformed = false;
        List<SerialFramingCandidate> candidates = [];
        foreach (SerialFramingCandidate candidate in OrEmpty(endpoint.FramingCandidates))
        {
            if (candidate.BaudRate is 0 or > 16_000_000
                || candidate.DataBits is < 5 or > 8
                || candidate.Parity is > 4
                || candidate.StopBits is > 2)
            {
                malformed = true;
                continue;
            }

            candidates.Add(candidate);
        }

        malformed |= candidates.Count > InventoryLimits.MaximumFramingCandidates;

        return endpoint with
        {
            Access = malformed ? InventoryAccess.Malformed : endpoint.Access,
            FramingCandidates = candidates
                .OrderBy(candidate => candidate.BaudRate)
                .ThenBy(candidate => candidate.DataBits)
                .ThenBy(candidate => candidate.Parity)
                .ThenBy(candidate => candidate.StopBits)
                .ThenBy(candidate => candidate.Source, StringComparer.Ordinal)
                .Take(InventoryLimits.MaximumFramingCandidates)
                .ToArray(),
        };
    }

    private static SensorEndpointInventory NormalizeSensor(SensorEndpointInventory sensor)
    {
        uint[] intervals = OrEmpty(sensor.SupportedReportIntervalsMilliseconds)
            .Distinct()
            .Order()
            .Take(InventoryLimits.MaximumSensorIntervals)
            .ToArray();
        if (sensor.MinimumReportIntervalMilliseconds is { } minimum
            && !intervals.Contains(minimum))
        {
            intervals = intervals.Length < InventoryLimits.MaximumSensorIntervals
                ? intervals.Append(minimum).Order().ToArray()
                : intervals[..^1].Append(minimum).Order().ToArray();
        }

        return sensor with { SupportedReportIntervalsMilliseconds = intervals };
    }

    private static InputEndpointInventory NormalizeInputEndpoint(InputEndpointInventory endpoint)
    {
        bool malformed = endpoint.DescriptorAccess is InventoryAccess.Malformed
            || (endpoint.DescriptorAccess is InventoryAccess.Available
                && endpoint.ReportDescriptorSha256 is null)
            || InvalidReportLength(endpoint.InputReportBytes)
            || InvalidReportLength(endpoint.OutputReportBytes)
            || InvalidReportLength(endpoint.FeatureReportBytes)
            || (endpoint.ReportDescriptorSha256 is { } sha256 && !IsSha256(sha256));
        return endpoint with
        {
            DescriptorAccess = malformed ? InventoryAccess.Malformed : endpoint.DescriptorAccess,
            ReportDescriptorSha256 = malformed
                ? null
                : endpoint.ReportDescriptorSha256?.ToLowerInvariant(),
        };
    }

    private static bool InvalidReportLength(int? bytes) => bytes is <= 0 or > ushort.MaxValue;

    private static bool IsSha256(string value) => value.Length == 64 && value.All(Uri.IsHexDigit);

    private static IReadOnlyList<T> OrderAndTake<T>(IEnumerable<T> items, Func<T, string> key) =>
        items.OrderBy(key, StringComparer.Ordinal)
            .Take(InventoryLimits.MaximumEndpointsPerLane)
            .ToArray();

    // Missing JSON collections deserialize as null under the strict source-generated context even
    // though production-created records use empty initializers. Normalization owns that boundary.
    private static IEnumerable<T> OrEmpty<T>(IEnumerable<T>? items) => items ?? [];
}
