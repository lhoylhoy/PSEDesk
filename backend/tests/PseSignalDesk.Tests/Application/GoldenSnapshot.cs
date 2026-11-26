using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PseSignalDesk.Tests.Application;

/// <summary>
/// Approval-style snapshots for the golden-output tests. A snapshot is an ordinary file in
/// <c>tests/PseSignalDesk.Tests/Application/Golden</c>, so a change in a number shows up as a
/// reviewable diff instead of a silent drift. Set <c>PSEDESK_UPDATE_GOLDEN=1</c> to record a new
/// baseline; every other run compares against the checked-in file.
/// </summary>
internal static class GoldenSnapshot
{
    public const string UpdateVariable = "PSEDESK_UPDATE_GOLDEN";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, JsonOptions);

    public static string PathFor(string fileName, string sourceFile)
    {
        Assert.False(string.IsNullOrWhiteSpace(sourceFile), "the goldens need the test source path");

        return Path.Combine(Path.GetDirectoryName(sourceFile)!, "Golden", fileName);
    }

    public static void AssertMatches(
        string fileName,
        string actual,
        [CallerFilePath] string sourceFile = "")
    {
        var path = PathFor(fileName, sourceFile);

        if (Environment.GetEnvironmentVariable(UpdateVariable) == "1")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, actual);
            return;
        }

        Assert.True(
            File.Exists(path),
            $"The golden snapshot '{fileName}' is missing. Re-run the suite with {UpdateVariable}=1 to " +
            "record it, then review the diff before committing it.");

        Assert.Equal(File.ReadAllText(path), actual);
    }
}
