using CsvEtl.Csv;
using CsvEtl.Pipeline;
using CsvEtl.Schema;

namespace CsvEtl.Tests;

/// <summary>Tests added after mutation testing showed that rule messages, bool formatting and a few edges were not pinned down.</summary>
public class MessageAndEdgeTests
{
    [Theory]
    [InlineData("\"required\":true", "", "value is required but empty")]
    [InlineData("\"regex\":\"^a$\"", "b", "value 'b' does not match /^a$/")]
    [InlineData("\"minLength\":3", "ab", "length 2 is shorter than minLength 3")]
    [InlineData("\"maxLength\":3", "abcd", "length 4 is longer than maxLength 3")]
    [InlineData("\"allowed\":[\"x\",\"y\"]", "z", "value 'z' is not one of: x, y")]
    [InlineData("\"type\":\"enum\",\"allowed\":[\"x\",\"y\"]", "z", "value 'z' is not one of: x, y")]
    [InlineData("\"type\":\"int\"", "q", "'q' is not an integer")]
    [InlineData("\"type\":\"decimal\"", "q", "'q' is not a decimal number (decimal separator '.')")]
    [InlineData("\"type\":\"decimal\",\"decimalSeparator\":\",\"", "q", "'q' is not a decimal number (decimal separator ',')")]
    [InlineData("\"type\":\"date\",\"formats\":[\"yyyy-MM-dd\",\"dd/MM/yyyy\"]", "q", "'q' is not a date in any of: yyyy-MM-dd, dd/MM/yyyy")]
    [InlineData("\"type\":\"bool\"", "q", "'q' is not a boolean (true: true/yes/y/1/t; false: false/no/n/0/f)")]
    [InlineData("\"type\":\"int\",\"min\":5", "4", "value 4 is less than min 5")]
    [InlineData("\"type\":\"int\",\"max\":5", "6", "value 6 is greater than max 5")]
    [InlineData("\"type\":\"decimal\",\"max\":1.5", "1.51", "value 1.51 is greater than max 1.5")]
    [InlineData("\"type\":\"date\",\"min\":\"2020-01-01\"", "2019-12-31", "value 2019-12-31 is less than min 2020-01-01")]
    public void FailureMessagesNameTheValueAndTheLimit(string body, string raw, string message) =>
        Assert.Equal(message, ColumnEvaluator.Evaluate(T.Col(body), raw).Failure!.Message);

    [Fact]
    public void ValuesAreClippedInMessagesOnlyAboveSixtyCharacters()
    {
        var c = T.Col("\"type\":\"int\"");
        Assert.Equal($"'{new string('x', 60)}' is not an integer", ColumnEvaluator.Evaluate(c, new string('x', 60)).Failure!.Message);
        Assert.Equal($"'{new string('x', 60)}...' is not an integer", ColumnEvaluator.Evaluate(c, new string('x', 61)).Failure!.Message);
    }

    [Fact]
    public void FormatWritesLowercaseBooleans()
    {
        var c = T.Col("\"type\":\"bool\"");
        Assert.Equal("true", ColumnEvaluator.Format(c, true));
        Assert.Equal("false", ColumnEvaluator.Format(c, false));
    }

    [Fact]
    public void FormatFallsBackToInvariantToStringForOtherTypes() =>
        Assert.Equal("5", ColumnEvaluator.Format(T.Col("\"type\":\"int\""), 5));

    [Fact]
    public void BoolValuesSurviveTheWholePipelineWithoutADefault()
    {
        var schema = T.Schema("{\"columns\":[{\"name\":\"b\",\"type\":\"bool\"}]}");
        var clean = new StringWriter();
        var jsonl = new StringWriter();
        EtlRunner.Run(new StringReader("b\nno\nyes\n\n0\n1\n"), schema, new EtlOptions(),
            new ICleanSink[] { new CleanCsvSink(clean), new JsonlSink(jsonl) });
        Assert.Equal("b\nfalse\ntrue\nfalse\ntrue\n", clean.ToString());
        Assert.Equal("{\"b\":false}\n{\"b\":true}\n{\"b\":false}\n{\"b\":true}\n", jsonl.ToString());
    }

    [Fact]
    public void KeyTrackerInOffModeRemembersNothing()
    {
        var t = new KeyTracker(DedupeMode.Off, 10);
        Assert.True(t.TryAdd("k"));
        Assert.True(t.TryAdd("k"));
        Assert.Equal(0, t.Count);
        Assert.False(t.Saturated);
    }

    [Fact]
    public void EqualBoundsAreValid()
    {
        Assert.True(SchemaLoader.Parse("{\"columns\":[{\"name\":\"a\",\"minLength\":3,\"maxLength\":3}]}").IsValid);
        Assert.True(SchemaLoader.Parse("{\"columns\":[{\"name\":\"a\",\"type\":\"int\",\"min\":5,\"max\":5}]}").IsValid);
    }
}
