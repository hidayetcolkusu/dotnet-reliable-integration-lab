using System.Text.Json;

namespace IntegrationLab.Tests.Support;

/// <summary>
/// Single source of truth for container images, shared with compose.yaml. The test
/// fixture refuses to run against anything but the pinned digests.
/// </summary>
public sealed record LabImages(string SqlPinned, string RabbitPinned, string TraceViewerPinned)
{
    public static LabImages Load()
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "lab-images.json"),
            FindRepoFile(Path.Combine("config", "lab-images.json")),
        };

        foreach (var candidate in candidates)
        {
            if (!File.Exists(candidate))
            {
                continue;
            }

            using var document = JsonDocument.Parse(File.ReadAllText(candidate));
            var root = document.RootElement;
            return new LabImages(
                root.GetProperty("sqlServer").GetProperty("pinned").GetString()!,
                root.GetProperty("rabbitMq").GetProperty("pinned").GetString()!,
                root.GetProperty("traceViewer").GetProperty("pinned").GetString()!);
        }

        throw new InvalidOperationException("config/lab-images.json was not found next to the tests or in the repo.");
    }

    public static string FindRepoFile(string relativePath)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "dotnet-reliable-integration-lab.slnx")))
        {
            directory = directory.Parent;
        }

        return directory is null
            ? relativePath
            : Path.Combine(directory.FullName, relativePath);
    }
}
