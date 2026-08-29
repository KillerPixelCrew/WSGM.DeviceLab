using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;
using WSGM.DeviceLab.Capture;

namespace WSGM.DeviceLab.Fixtures;

/// <summary>Version and directory contract for plain, reviewable fixture trees.</summary>
internal static class FixtureSchema
{
    /// <summary>Current fixture manifest version.</summary>
    public const int CurrentVersion = 1;

    /// <summary>Manifest filename at the root of a fixture directory.</summary>
    public const string ManifestPath = "fixture.json";

    /// <summary>Directory containing sanitized replay inputs.</summary>
    public const string InputPrefix = "input/";

    /// <summary>Directory containing expected semantic outputs.</summary>
    public const string ExpectedPrefix = "expected/";

    /// <summary>Maximum number of input plus expected artifacts in one fixture.</summary>
    public const int MaximumArtifacts = 4096;
}

/// <summary>Metadata for one simulator-only fixture directory.</summary>
internal sealed record FixtureManifest
{
    /// <summary>Schema version of this manifest.</summary>
    public required int SchemaVersion { get; init; }

    /// <summary>Stable fixture identifier.</summary>
    public required string FixtureId { get; init; }

    /// <summary>SHA-256 of the sanitized <c>.wsgmcap</c> this fixture came from.</summary>
    public required string SourceCaptureSha256 { get; init; }

    /// <summary>Tool and version that extracted the fixture.</summary>
    public required string ExtractorVersion { get; init; }

    /// <summary>Closed execution policy; a fixture can only be replayed by a simulator.</summary>
    public FixtureReplayPolicy ReplayPolicy { get; init; } = FixtureReplayPolicy.SimulatorOnly;

    /// <summary>Sanitized inputs under <c>input/</c>.</summary>
    public IReadOnlyList<FixtureArtifact> Inputs { get; init; } = [];

    /// <summary>Reviewable semantic outputs under <c>expected/</c>.</summary>
    public IReadOnlyList<FixtureArtifact> ExpectedOutputs { get; init; } = [];

}

/// <summary>The only execution environment admitted by a fixture manifest.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<FixtureReplayPolicy>))]
internal enum FixtureReplayPolicy
{
    /// <summary>Replay against an in-memory simulator with no hardware transport.</summary>
    SimulatorOnly,
}

/// <summary>One file in a plain fixture directory.</summary>
internal sealed record FixtureArtifact
{
    /// <summary>Canonical relative path below <c>input/</c> or <c>expected/</c>.</summary>
    public required string Path { get; init; }

    /// <summary>Media type used to select a deterministic decoder.</summary>
    public required string MediaType { get; init; }

    /// <summary>Exact byte length.</summary>
    public required long Length { get; init; }

    /// <summary>Lowercase hexadecimal SHA-256 digest.</summary>
    public required string Sha256 { get; init; }
}

/// <summary>Validates fixture metadata without opening a device or hardware transport.</summary>
internal static class FixtureSchemaValidator
{
    /// <summary>Validates one fixture manifest.</summary>
    /// <param name="manifest">Manifest to validate.</param>
    /// <returns>Every validation failure found.</returns>
    public static IReadOnlyList<CaptureValidationError> Validate(FixtureManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        List<CaptureValidationError> errors = [];
        if (manifest.SchemaVersion != FixtureSchema.CurrentVersion)
        {
            errors.Add(new("fixture.schemaVersion", "Unsupported fixture schema version."));
        }

        ValidateIdentifier(manifest.FixtureId, "fixture.fixtureId", errors);
        ValidateIdentifier(manifest.ExtractorVersion, "fixture.extractorVersion", errors);
        ValidateSha256(manifest.SourceCaptureSha256, "fixture.sourceCaptureSha256", errors);

        if (manifest.ReplayPolicy is not FixtureReplayPolicy.SimulatorOnly)
        {
            errors.Add(new("fixture.replayPolicy", "Fixture replay must remain simulator-only."));
        }

        if (manifest.Inputs.Count + manifest.ExpectedOutputs.Count > FixtureSchema.MaximumArtifacts)
        {
            errors.Add(new("fixture.artifacts",
                $"A fixture may contain at most {FixtureSchema.MaximumArtifacts} artifacts."));
        }

        ValidateArtifacts(manifest.Inputs, FixtureSchema.InputPrefix, errors);
        ValidateArtifacts(manifest.ExpectedOutputs, FixtureSchema.ExpectedPrefix, errors);

        HashSet<string> allPaths = new(StringComparer.OrdinalIgnoreCase);
        foreach (FixtureArtifact artifact in manifest.Inputs.Concat(manifest.ExpectedOutputs))
        {
            if (!allPaths.Add(artifact.Path))
            {
                errors.Add(new(artifact.Path, "Fixture path is duplicated."));
            }
        }

        return errors;
    }

    private static void ValidateArtifacts(
        IReadOnlyList<FixtureArtifact> artifacts,
        string requiredPrefix,
        ICollection<CaptureValidationError> errors)
    {
        foreach (FixtureArtifact artifact in artifacts)
        {
            if (!CaptureBundleLayout.IsSafeRelativePath(artifact.Path)
                || !artifact.Path.StartsWith(requiredPrefix, StringComparison.Ordinal))
            {
                errors.Add(new(artifact.Path, $"Fixture artifact must be below '{requiredPrefix}'."));
            }

            if (artifact.Length < 0 || artifact.Length > CaptureSchema.MaximumBlobBytes)
            {
                errors.Add(new(artifact.Path,
                    $"Fixture artifact length must be between 0 and {CaptureSchema.MaximumBlobBytes}."));
            }

            ValidateIdentifier(artifact.MediaType, artifact.Path, errors);
            ValidateSha256(artifact.Sha256, artifact.Path, errors);
        }
    }

    private static void ValidateIdentifier(
        string? value,
        string path,
        ICollection<CaptureValidationError> errors)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > CaptureSchema.MaximumIdentifierLength)
        {
            errors.Add(new(path,
                $"Identifier must contain 1 to {CaptureSchema.MaximumIdentifierLength} characters."));
        }
    }

    private static void ValidateSha256(
        string hash,
        string path,
        ICollection<CaptureValidationError> errors)
    {
        if (hash.Length != 64 || hash.Any(c => c is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
        {
            errors.Add(new(path, "SHA-256 must be 64 lowercase hexadecimal characters."));
        }
    }
}
