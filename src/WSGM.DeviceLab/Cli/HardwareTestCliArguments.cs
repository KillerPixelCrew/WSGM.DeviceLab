using System;
using WSGM.DeviceLab.Testing;

namespace WSGM.DeviceLab.Cli;

/// <summary>Exact command-line arguments for the attended hardware test.</summary>
internal sealed record HardwareTestCliArguments
{
    /// <summary>Expanded plugin package directory.</summary>
    internal required string PackageDirectory { get; init; }

    /// <summary>Inventory used for exact device matching.</summary>
    internal required string InventoryPath { get; init; }

    /// <summary>New isolated state directory for the attended run.</summary>
    internal required string StateDirectory { get; init; }

    /// <summary>The one explicitly selected semantic action.</summary>
    internal required AttendedPluginActionRequest Action { get; init; }

    /// <summary>Parses the package positional argument and the complete allowed option set.</summary>
    internal static bool TryParse(
        ReadOnlySpan<string> args,
        out HardwareTestCliArguments? parsed,
        out string error)
    {
        parsed = null;
        error = string.Empty;

        foreach (string argument in args)
        {
            if (string.Equals(argument, "--yes", StringComparison.OrdinalIgnoreCase))
            {
                error = "test hardware never accepts --yes.";
                return false;
            }
        }

        if (args.Length == 0 || string.IsNullOrWhiteSpace(args[0]) || LooksLikeOption(args[0]))
        {
            error = "test hardware requires exactly one package directory before its options.";
            return false;
        }

        string packageDirectory = args[0];
        string? inventoryPath = null;
        string? stateDirectory = null;
        string? actionName = null;
        string? capabilityId = null;
        string? instanceId = null;
        string? valueText = null;

        for (int index = 1; index < args.Length; index++)
        {
            string option = args[index];
            switch (option)
            {
                case "--from":
                case "-f":
                    if (inventoryPath is not null)
                    {
                        error = "test hardware accepts --from exactly once.";
                        return false;
                    }

                    if (!TryReadValue(args, ref index, option, out inventoryPath, out error))
                    {
                        return false;
                    }

                    break;

                case "--state-dir":
                    if (stateDirectory is not null)
                    {
                        error = "test hardware accepts --state-dir exactly once.";
                        return false;
                    }

                    if (!TryReadValue(args, ref index, option, out stateDirectory, out error))
                    {
                        return false;
                    }

                    break;

                case "--action":
                    if (actionName is not null)
                    {
                        error = "test hardware accepts --action exactly once.";
                        return false;
                    }

                    if (!TryReadValue(args, ref index, option, out actionName, out error))
                    {
                        return false;
                    }

                    break;

                case "--capability":
                    if (capabilityId is not null)
                    {
                        error = "test hardware accepts --capability exactly once.";
                        return false;
                    }

                    if (!TryReadValue(args, ref index, option, out capabilityId, out error))
                    {
                        return false;
                    }

                    break;

                case "--instance":
                    if (instanceId is not null)
                    {
                        error = "test hardware accepts --instance exactly once.";
                        return false;
                    }

                    if (!TryReadValue(args, ref index, option, out instanceId, out error))
                    {
                        return false;
                    }

                    break;

                case "--value":
                    if (valueText is not null)
                    {
                        error = "test hardware accepts --value exactly once.";
                        return false;
                    }

                    if (!TryReadValue(args, ref index, option, out valueText, out error))
                    {
                        return false;
                    }

                    break;

                default:
                    error = $"Unknown or trailing test hardware argument '{option}'.";
                    return false;
            }
        }

        if (string.IsNullOrWhiteSpace(inventoryPath))
        {
            error = "test hardware requires --from <inventory.json> exactly once.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(stateDirectory))
        {
            error = "test hardware requires --state-dir <new-directory> exactly once.";
            return false;
        }

        AttendedPluginActionRequest action;
        if (string.Equals(actionName, "capability", StringComparison.Ordinal))
        {
            if (string.IsNullOrWhiteSpace(capabilityId) || string.IsNullOrWhiteSpace(valueText))
            {
                error = "test hardware --action capability requires --capability <id> and --value <semantic-value>; --instance <id> is optional.";
                return false;
            }

            if (instanceId is not null && string.IsNullOrWhiteSpace(instanceId))
            {
                error = "test hardware --instance requires a nonempty value.";
                return false;
            }

            action = new AttendedPluginActionRequest
            {
                Kind = AttendedPluginActionKind.CapabilityValue,
                CapabilityId = capabilityId,
                InstanceId = instanceId,
                ValueText = valueText,
            };
        }
        else if (string.Equals(actionName, "haptic", StringComparison.Ordinal)
            || string.Equals(actionName, "haptic-sweep", StringComparison.Ordinal)
            || string.Equals(actionName, "controller", StringComparison.Ordinal))
        {
            if (capabilityId is not null || valueText is not null)
            {
                error = "--capability and --value apply only to --action capability; --instance may select an exact haptic or controller instance.";
                return false;
            }

            action = new AttendedPluginActionRequest
            {
                Kind = actionName switch
                {
                    "haptic" => AttendedPluginActionKind.HapticPulse,
                    "haptic-sweep" => AttendedPluginActionKind.HapticSweep,
                    _ => AttendedPluginActionKind.ControllerManagement,
                },
                InstanceId = instanceId,
            };
        }
        else
        {
            error = "test hardware requires exactly one --action capability, --action haptic, --action haptic-sweep, or --action controller.";
            return false;
        }

        parsed = new HardwareTestCliArguments
        {
            PackageDirectory = packageDirectory,
            InventoryPath = inventoryPath,
            StateDirectory = stateDirectory,
            Action = action,
        };
        return true;
    }

    private static bool TryReadValue(
        ReadOnlySpan<string> args,
        ref int index,
        string option,
        out string? value,
        out string error)
    {
        value = null;
        error = string.Empty;
        if (index + 1 >= args.Length || LooksLikeOption(args[index + 1]))
        {
            error = $"test hardware option {option} requires one value.";
            return false;
        }

        value = args[++index];
        if (string.IsNullOrWhiteSpace(value))
        {
            error = $"test hardware option {option} requires a nonempty value.";
            return false;
        }

        return true;
    }

    private static bool LooksLikeOption(string value) =>
        value.StartsWith("--", StringComparison.Ordinal)
        || (value.Length == 2 && value[0] == '-' && char.IsLetter(value[1]));
}
