using System;
using System.Collections.Generic;
using System.Linq;
using WSGM.DeviceLab.Inventory;

namespace WSGM.DeviceLab.Capture;

/// <summary>
/// Produces the shareable form of a machine inventory.
/// </summary>
/// <remarks>
/// The private capture keeps everything, because a maintainer diagnosing their own machine needs the
/// real identifiers. Only the shareable projection is redacted, and it is a separate value rather
/// than an in-place edit so the two cannot be confused: a function that redacted in place would leave
/// no way to tell whether the object in hand is safe to send.
/// </remarks>
internal static class InventoryRedaction
{
    /// <summary>
    /// Returns a copy of the inventory safe to send to someone else.
    /// </summary>
    /// <param name="inventory">The private inventory.</param>
    /// <param name="removed">What was redacted, for the bundle's redaction manifest.</param>
    /// <returns>The shareable inventory.</returns>
    public static MachineInventory ToShareable(
        MachineInventory inventory,
        out IReadOnlyList<RedactionSummary> removed)
    {
        ArgumentNullException.ThrowIfNull(inventory);

        CaptureRedactor redactor = new();
        inventory = MachineInventoryNormalizer.Normalize(inventory);

        MachineInventory shareable = inventory with
        {
            GraphicsAdapters = [.. inventory.GraphicsAdapters.Select(adapter => adapter with
            {
                InstanceId = redactor.Redact(adapter.InstanceId),
            })],
            UsbInterfaces = [.. inventory.UsbInterfaces.Select(i => i with
            {
                InstanceId = redactor.Redact(i.InstanceId),
                LocationPath = Tokenize(redactor, i.LocationPath),
                DeviceLevelLocationPath = Tokenize(redactor, i.DeviceLevelLocationPath),
            })],
            SerialEndpoints = [.. inventory.SerialEndpoints.Select(endpoint => endpoint with
            {
                InstanceId = redactor.Redact(endpoint.InstanceId),
                Name = endpoint.Name is null ? null : redactor.Redact(endpoint.Name),
                LocationPath = Tokenize(redactor, endpoint.LocationPath),
                AssociationId = Tokenize(redactor, endpoint.AssociationId),
            })],
            Sensors = [.. inventory.Sensors.Select(sensor => sensor with
            {
                InstanceId = redactor.Redact(sensor.InstanceId),
                Name = sensor.Name is null ? null : redactor.Redact(sensor.Name),
                AssociationId = Tokenize(redactor, sensor.AssociationId),
                DeviceLevelLocationPath = Tokenize(redactor, sensor.DeviceLevelLocationPath),
            })],
            InputBackends = [.. inventory.InputBackends.Select(backend => backend with
            {
                Endpoints = [.. backend.Endpoints.Select(endpoint => endpoint with
                {
                    EndpointId = redactor.TokenizeSessionIdentifier(
                        $"{backend.Backend}:{endpoint.EndpointId}"),
                    InstanceId = endpoint.InstanceId is null ? null : redactor.Redact(endpoint.InstanceId),
                    Name = endpoint.Name is null ? null : redactor.Redact(endpoint.Name),
                    AssociationId = Tokenize(redactor, endpoint.AssociationId),
                })],
            })],
            NativeBinaries = [.. inventory.NativeBinaries.Select(binary => binary with
            {
                Path = System.IO.Path.GetFileName(binary.Name) ?? string.Empty,
                Name = System.IO.Path.GetFileName(binary.Name) ?? string.Empty,
            })],
            Processes = [.. inventory.Processes.Select(process => process with
            {
                SessionToken = process.ProcessId is { } processId
                    ? redactor.TokenizeSessionIdentifier($"process:{processId}")
                    : Tokenize(redactor, process.SessionToken),
                ProcessId = null,
                Path = null,
                CommandLine = null,
                LoadedModulePaths = [.. process.LoadedModulePaths.Select(path =>
                    System.IO.Path.GetFileName(path) ?? string.Empty)],
            })],
            Services = [.. inventory.Services.Select(service => service with
            {
                ProcessToken = service.ProcessId is { } processId
                    ? redactor.TokenizeSessionIdentifier($"process:{processId}")
                    : Tokenize(redactor, service.ProcessToken),
                ProcessId = null,
                PathName = null,
            })],
            ScheduledTasks = [.. inventory.ScheduledTasks.Select(task => task with
            {
                Path = redactor.Redact(task.Path),
            })],
            Providers = [.. inventory.Providers.Select(provider => provider with
            {
                HostProcessToken = provider.HostProcessId is { } processId
                    ? redactor.TokenizeSessionIdentifier($"process:{processId}")
                    : Tokenize(redactor, provider.HostProcessToken),
                HostProcessId = null,
                ModulePath = provider.ModulePath is null
                    ? null
                    : System.IO.Path.GetFileName(provider.ModulePath),
            })],
            TopologyGenerations = [.. inventory.TopologyGenerations.Select(observation => observation with
            {
                InstanceId = redactor.Redact(observation.InstanceId),
                AssociationId = Tokenize(redactor, observation.AssociationId),
            })],
        };

        removed = redactor.Summarize();
        return shareable;
    }

    private static string? Tokenize(CaptureRedactor redactor, string? value) => value is null
        ? null
        : redactor.TokenizeSessionIdentifier(value);
}
