using System.Globalization;
using System.Runtime.CompilerServices;
using CsvEtl.Csv;
using CsvEtl.Schema;

namespace CsvEtl.Tests;

/// <summary>Set CSVETL_TEST_CULTURE=de-DE (or fr-FR, tr-TR ...) to run the WHOLE suite with that current culture.</summary>
internal static class TestEnv
{
    [ModuleInitializer]
    internal static void Init()
    {
        string? name = Environment.GetEnvironmentVariable("CSVETL_TEST_CULTURE");
        if (string.IsNullOrWhiteSpace(name)) return;
        var c = CultureInfo.GetCultureInfo(name);
        CultureInfo.DefaultThreadCurrentCulture = c;
        CultureInfo.DefaultThreadCurrentUICulture = c;
        CultureInfo.CurrentCulture = c;
        CultureInfo.CurrentUICulture = c;
    }
}

[CollectionDefinition("Serial", DisableParallelization = true)]
public sealed class SerialCollection;

internal static class T
{
    public static readonly CsvOptions Loose = new() { ReportRagged = false };

    public static string[][] Parse(string text, CsvOptions? o = null) =>
        CsvReader.ReadString(text, o ?? Loose).Select(r => r.Fields).ToArray();

    public static List<CsvRecord> Records(string text, CsvOptions? o = null) =>
        CsvReader.ReadString(text, o ?? new CsvOptions()).ToList();

    public static void WithCulture(string name, Action action)
    {
        var old = CultureInfo.CurrentCulture;
        var oldUi = CultureInfo.CurrentUICulture;
        try
        {
            var c = CultureInfo.GetCultureInfo(name);
            CultureInfo.CurrentCulture = c;
            CultureInfo.CurrentUICulture = c;
            action();
        }
        finally
        {
            CultureInfo.CurrentCulture = old;
            CultureInfo.CurrentUICulture = oldUi;
        }
    }

    public static string SamplePath(string file) => Path.Combine(AppContext.BaseDirectory, "samples", file);

    public static SchemaDefinition Schema(string json)
    {
        var r = SchemaLoader.Parse(json);
        Assert.True(r.IsValid, string.Join("; ", r.Errors));
        return r.Schema!;
    }

    public static SchemaDefinition SampleSchema() => Schema(File.ReadAllText(SamplePath("customers.schema.json")));

    /// <summary>Schema with a single column built from a JSON column body, e.g. "\"type\":\"int\",\"min\":1".</summary>
    public static Column Col(string body)
    {
        var s = Schema("{\"columns\":[{\"name\":\"c\"," + body + "}]}");
        return s.Columns[0];
    }

    public static string TempDir()
    {
        string d = Path.Combine(Path.GetTempPath(), "csvetl-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        return d;
    }
}
