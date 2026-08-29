using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using WSGM.Device.Sdk.Glyphs;

namespace WSGM.DeviceLab.Packaging;

/// <summary>One profile accepted by the SDK's single glyph loader.</summary>
internal sealed record ImportedGlyphProfileSummary
{
    /// <summary>Stable package-scoped profile identifier.</summary>
    public required string ProfileId { get; init; }

    /// <summary>Package-authored profile revision.</summary>
    public required int Revision { get; init; }

    /// <summary>Exact immutable source revision.</summary>
    public required string SourceRevision { get; init; }

    /// <summary>Confined package-relative attribution notice.</summary>
    public required string NoticePath { get; init; }

    /// <summary>Number of hash-addressed artwork files imported.</summary>
    public required int AssetCount { get; init; }

    /// <summary>Number of explicit physical-control mappings.</summary>
    public required int ControlCount { get; init; }

    /// <summary>Number of logical-to-physical aliases.</summary>
    public required int AliasCount { get; init; }
}

/// <summary>Direct SDK glyph-import result with no generated artifact layer.</summary>
internal sealed record GlyphPackageImportReport
{
    /// <summary>Whether every discovered profile, artwork file, and notice passed.</summary>
    public required bool Valid { get; init; }

    /// <summary>Stable package-validation issues.</summary>
    public IReadOnlyList<PluginPackageValidationIssue> Issues { get; init; } = [];

    /// <summary>Profiles accepted by the same loader used by the runtime.</summary>
    public IReadOnlyList<ImportedGlyphProfileSummary> Profiles { get; init; } = [];
}

/// <summary>Runs the SDK's single bounded glyph loader directly over a plugin package.</summary>
internal static class GlyphPackageImportWorkflow
{
    /// <summary>Validates and imports every directly enumerated glyph profile.</summary>
    /// <param name="sourceDirectory">Existing plugin package source.</param>
    /// <param name="cancellationToken">Cancels bounded source capture and import.</param>
    /// <returns>Accepted profile summaries and deterministic errors.</returns>
    public static GlyphPackageImportReport Import(
        string sourceDirectory,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceDirectory);
        List<PluginPackageValidationIssue> issues = [];
        string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(sourceDirectory));
        cancellationToken.ThrowIfCancellationRequested();
        DeviceLabPackageSnapshot snapshot;
        try
        {
            snapshot = DeviceLabPackageSnapshot.Capture(root, issues, cancellationToken);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException
            or InvalidDataException
            or IOException)
        {
            return Failure("invalid-root", string.Empty, exception.GetType().Name);
        }

        using DeviceLabPackageSnapshot captured = snapshot;
        if (issues.Count > 0)
        {
            return Report([], issues);
        }

        cancellationToken.ThrowIfCancellationRequested();
        GlyphPackageImportResult imported = GlyphPackageImporter.Import(
            new SnapshotGlyphPackageSource(captured, cancellationToken));
        cancellationToken.ThrowIfCancellationRequested();
        if (!imported.IsValid)
        {
            return Report(
                [],
                imported.Errors.Select(error => new PluginPackageValidationIssue(
                    $"glyph-{error.Code.ToString().ToLowerInvariant()}",
                    error.Path,
                    $"{error.ProfileId}: {error.Message}"))
                    .OrderBy(issue => issue.Path, StringComparer.Ordinal)
                    .ThenBy(issue => issue.Code, StringComparer.Ordinal)
                    .ToArray());
        }

        if (imported.Profiles.Count == 0)
        {
            return Failure(
                "no-glyph-profiles",
                "glyphs/profiles",
                "The fixed glyph profile directory contains no profiles to import.");
        }

        ImportedGlyphProfileSummary[] profiles = imported.Profiles.Select(profile =>
            new ImportedGlyphProfileSummary
            {
                ProfileId = profile.Manifest.ProfileId,
                Revision = profile.Manifest.Revision,
                SourceRevision = profile.Manifest.SourceRevision,
                NoticePath = profile.Manifest.NoticePath,
                AssetCount = profile.Assets.Count,
                ControlCount = profile.Manifest.Controls.Count,
                AliasCount = profile.Manifest.Aliases.Count,
            }).OrderBy(profile => profile.ProfileId, StringComparer.Ordinal).ToArray();
        return Report(profiles, []);
    }

    private static GlyphPackageImportReport Failure(string code, string path, string message) =>
        Report([], [new PluginPackageValidationIssue(code, path, message)]);

    private static GlyphPackageImportReport Report(
        IReadOnlyList<ImportedGlyphProfileSummary> profiles,
        IReadOnlyList<PluginPackageValidationIssue> issues) => new()
        {
            Valid = issues.Count == 0,
            Issues = issues,
            Profiles = profiles,
        };
}
