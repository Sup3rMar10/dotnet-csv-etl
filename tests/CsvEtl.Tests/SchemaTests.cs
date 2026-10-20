using CsvEtl.Schema;

namespace CsvEtl.Tests;

public class SchemaLoaderTests
{
    private static IReadOnlyList<string> Errors(string json) => SchemaLoader.Parse(json).Errors;

    [Fact]
    public void SampleSchemaLoads()
    {
        var s = T.SampleSchema();
        Assert.Equal("customers", s.Name);
        Assert.Equal(9, s.Columns.Count);
        Assert.Equal(new[] { "customer_id" }, s.Key);
        Assert.Equal(new[] { "Cust ID", "CustomerID" }, s.Columns[0].Aliases);
    }

    [Fact]
    public void CommentsAndTrailingCommasAreAllowed() =>
        Assert.True(SchemaLoader.Parse("{ // c\n \"columns\": [ {\"name\":\"a\",}, ], }").IsValid);

    [Fact]
    public void InvalidJsonIsReportedNotThrown() =>
        Assert.Contains("not valid JSON", Assert.Single(Errors("{ nope")));

    [Fact]
    public void RootMustBeObject() => Assert.Contains("root must be a JSON object", Assert.Single(Errors("[]")));

    [Fact]
    public void ColumnsAreRequired() => Assert.Contains("$.columns", Assert.Single(Errors("{}")));

    [Fact]
    public void EmptyColumnsRejected() => Assert.Contains("at least one column", Assert.Single(Errors("{\"columns\":[]}")));

    [Fact]
    public void UnknownPropertyGetsDidYouMeanHint()
    {
        var e = Assert.Single(Errors("{\"columns\":[{\"name\":\"a\",\"requierd\":true}]}"));
        Assert.Contains("unknown property \"requierd\"", e);
        Assert.Contains("did you mean \"required\"", e);
    }

    [Fact]
    public void UnknownTopLevelProperty() =>
        Assert.Contains(Errors("{\"colums\":[],\"columns\":[{\"name\":\"a\"}]}"), e => e.Contains("colums") && e.Contains("columns"));

    [Fact]
    public void UnknownTypeIsReported() =>
        Assert.Contains(Errors("{\"columns\":[{\"name\":\"a\",\"type\":\"float\"}]}"), e => e.Contains("unknown type \"float\""));

    [Fact]
    public void WrongJsonTypesAreReportedWithPath() =>
        Assert.Contains(Errors("{\"columns\":[{\"name\":\"a\",\"required\":\"yes\"}]}"), e => e.Contains("$.columns[0] ('a').required"));

    [Theory]
    [InlineData("\"type\":\"enum\"", "needs a non-empty allowed list")]
    [InlineData("\"type\":\"string\",\"min\":1", "only valid for int, decimal and date")]
    [InlineData("\"type\":\"int\",\"minLength\":1", "only valid for string")]
    [InlineData("\"type\":\"int\",\"case\":\"upper\"", "only valid for string and enum")]
    [InlineData("\"type\":\"int\",\"allowed\":[\"1\"]", "only valid for string and enum")]
    [InlineData("\"type\":\"int\",\"min\":5,\"max\":1", "min 5 is greater than max 1")]
    [InlineData("\"minLength\":5,\"maxLength\":1", "minLength 5 is greater than maxLength 1")]
    [InlineData("\"type\":\"int\",\"min\":\"x\"", "must be an integer")]
    [InlineData("\"type\":\"date\",\"min\":\"01/02/2020\"", "ISO date")]
    [InlineData("\"type\":\"date\",\"formats\":[\"d\"]", "culture dependent")]
    [InlineData("\"type\":\"date\",\"formats\":[]", "must not be empty")]
    [InlineData("\"type\":\"string\",\"formats\":[\"yyyy\"]", "only valid for date")]
    [InlineData("\"regex\":\"([\"", "invalid pattern")]
    [InlineData("\"onInvalid\":\"default\"", "no default value is given")]
    [InlineData("\"onInvalid\":\"explode\"", "must be reject, null or default")]
    [InlineData("\"case\":\"shouty\"", "must be none, upper, lower or title")]
    [InlineData("\"type\":\"int\",\"default\":\"abc\"", "fails the column's own rules")]
    [InlineData("\"type\":\"decimal\",\"decimalSeparator\":\";\"", "must be")]
    [InlineData("\"type\":\"string\",\"decimalSeparator\":\",\"", "only valid for decimal")]
    [InlineData("\"type\":\"string\",\"trueValues\":[\"y\"]", "only valid for bool")]
    public void ContradictoryColumnsAreRejected(string body, string expectedFragment)
    {
        var errors = Errors("{\"columns\":[{\"name\":\"c\"," + body + "}]}");
        Assert.Contains(errors, e => e.Contains(expectedFragment));
    }

    [Fact]
    public void AliasCollisionAcrossColumnsIsRejected()
    {
        var errors = Errors("{\"columns\":[{\"name\":\"customer_id\"},{\"name\":\"b\",\"aliases\":[\"Customer ID\"]}]}");
        Assert.Contains(errors, e => e.Contains("collides with column 'customer_id'"));
    }

    [Fact]
    public void KeyMustNameAColumn() =>
        Assert.Contains(Errors("{\"key\":[\"nope\"],\"columns\":[{\"name\":\"a\"}]}"), e => e.Contains("'nope' is not a column name"));

    [Fact]
    public void AllErrorsAreCollectedNotJustTheFirst()
    {
        var errors = Errors("{\"columns\":[{\"name\":\"a\",\"type\":\"x\"},{\"name\":\"b\",\"regex\":\"([\"},{\"type\":\"int\"}]}");
        Assert.True(errors.Count >= 3, string.Join("\n", errors));
    }

    [Fact]
    public void DefaultsAreParsedThroughTheColumnRules()
    {
        var c = T.Col("\"type\":\"decimal\",\"default\":0.50,\"onInvalid\":\"default\"");
        Assert.Equal(0.50m, c.DefaultValue);
        Assert.Equal("0.50", ColumnEvaluator.Format(c, c.DefaultValue));
    }
}

public class ColumnEvaluatorTests
{
    private static Eval E(Column c, string? raw) => ColumnEvaluator.Evaluate(c, raw);

    [Theory]
    [InlineData("0", null)]
    [InlineData("-1", "min")]
    [InlineData("120", null)]
    [InlineData("121", "max")]
    [InlineData("+5", null)]
    [InlineData(" 7 ", null)]
    [InlineData("120.0", "type")]
    [InlineData("1,000", "type")]
    [InlineData("1 000", "type")]
    [InlineData("9223372036854775808", "type")]
    [InlineData("abc", "type")]
    [InlineData("0x10", "type")]
    public void IntBoundaries(string raw, string? rule)
    {
        var c = T.Col("\"type\":\"int\",\"min\":0,\"max\":120");
        Assert.Equal(rule, E(c, raw).Failure?.Rule);
    }

    [Fact]
    public void IntValueIsLong() => Assert.Equal(-42L, E(T.Col("\"type\":\"int\""), "-42").Value);

    [Theory]
    [InlineData("0", null)]
    [InlineData("-0.01", "min")]
    [InlineData("100", null)]
    [InlineData("100.00", null)]
    [InlineData("100.01", "max")]
    [InlineData(".5", null)]
    [InlineData("5.", null)]
    [InlineData("1e2", "type")]
    [InlineData("1,5", "type")]
    [InlineData("1,234.50", "type")]
    [InlineData("NaN", "type")]
    [InlineData("79228162514264337593543950336", "type")]
    public void DecimalBoundaries(string raw, string? rule)
    {
        var c = T.Col("\"type\":\"decimal\",\"min\":0,\"max\":100");
        Assert.Equal(rule, E(c, raw).Failure?.Rule);
    }

    [Fact]
    public void DecimalKeepsItsScale() =>
        Assert.Equal("12.50", ColumnEvaluator.Format(T.Col("\"type\":\"decimal\""), E(T.Col("\"type\":\"decimal\""), "12.50").Value));

    [Theory]
    [InlineData("1,5", "1.5")]
    [InlineData("-0,25", "-0.25")]
    [InlineData("12", "12")]
    public void CommaDecimalSeparatorColumn(string raw, string canonical)
    {
        var c = T.Col("\"type\":\"decimal\",\"decimalSeparator\":\",\"");
        var ev = E(c, raw);
        Assert.Null(ev.Failure);
        Assert.Equal(canonical, ColumnEvaluator.Format(c, ev.Value));
    }

    [Theory]
    [InlineData("1.5")]
    [InlineData("1.234,5")]
    public void CommaDecimalSeparatorColumnRejectsDotAndThousands(string raw) =>
        Assert.Equal("type", E(T.Col("\"type\":\"decimal\",\"decimalSeparator\":\",\""), raw).Failure?.Rule);

    [Theory]
    [InlineData("2020-01-01", null)]
    [InlineData("2019-12-31", "min")]
    [InlineData("2020-12-31", null)]
    [InlineData("2021-01-01", "max")]
    [InlineData("31/12/2020", null)]
    [InlineData("2020-02-30", "type")]
    [InlineData("2020-1-5", "type")]
    [InlineData("13/13/2020", "type")]
    [InlineData("2020-12-31T00:00", "type")]
    [InlineData("tomorrow", "type")]
    public void DateBoundariesAndFormatList(string raw, string? rule)
    {
        var c = T.Col("\"type\":\"date\",\"formats\":[\"yyyy-MM-dd\",\"dd/MM/yyyy\"],\"min\":\"2020-01-01\",\"max\":\"2020-12-31\"");
        Assert.Equal(rule, E(c, raw).Failure?.Rule);
    }

    [Theory]
    [InlineData("2024-02-29", true)]
    [InlineData("2023-02-29", false)]
    [InlineData("1900-02-29", false)]
    [InlineData("2000-02-29", true)]
    public void LeapDays(string raw, bool valid) =>
        Assert.Equal(valid, E(T.Col("\"type\":\"date\""), raw).Failure == null);

    [Fact]
    public void DateOutputFormatIsConfigurable()
    {
        var c = T.Col("\"type\":\"date\",\"formats\":[\"dd/MM/yyyy\"],\"outputFormat\":\"yyyyMMdd\"");
        Assert.Equal("20240305", ColumnEvaluator.Format(c, E(c, "05/03/2024").Value));
    }

    [Theory]
    [InlineData("true", true)]
    [InlineData("YES", true)]
    [InlineData(" y ", true)]
    [InlineData("1", true)]
    [InlineData("False", false)]
    [InlineData("n", false)]
    [InlineData("0", false)]
    public void BoolValues(string raw, bool expected) => Assert.Equal(expected, E(T.Col("\"type\":\"bool\""), raw).Value);

    [Theory]
    [InlineData("2")]
    [InlineData("maybe")]
    [InlineData("truee")]
    public void BoolRejectsOthers(string raw) => Assert.Equal("type", E(T.Col("\"type\":\"bool\""), raw).Failure?.Rule);

    [Fact]
    public void BoolCustomValues()
    {
        var c = T.Col("\"type\":\"bool\",\"trueValues\":[\"si\"],\"falseValues\":[\"no\"]");
        Assert.Equal(true, E(c, "SI").Value);
        Assert.Equal("type", E(c, "yes").Failure?.Rule);
    }

    [Fact]
    public void EnumMatchesCaseInsensitivelyAndReturnsCanonicalSpelling()
    {
        var c = T.Col("\"type\":\"enum\",\"allowed\":[\"Gold\",\"Silver\"]");
        Assert.Equal("Gold", E(c, " gOLD ").Value);
        Assert.Equal("allowed", E(c, "bronze").Failure?.Rule);
    }

    [Theory]
    [InlineData("a", "min-length")]
    [InlineData("ab", null)]
    [InlineData("abcd", null)]
    [InlineData("abcde", "max-length")]
    public void StringLengthBoundaries(string raw, string? rule) =>
        Assert.Equal(rule, E(T.Col("\"minLength\":2,\"maxLength\":4"), raw).Failure?.Rule);

    [Fact]
    public void StringAllowedIsCaseSensitiveUnlessCaseNormalised()
    {
        Assert.Equal("allowed", E(T.Col("\"allowed\":[\"A\",\"B\"]"), "a").Failure?.Rule);
        Assert.Equal("A", E(T.Col("\"allowed\":[\"A\",\"B\"],\"case\":\"upper\""), "a").Value);
    }

    [Theory]
    [InlineData("upper", "jOHN o'neil", "JOHN O'NEIL")]
    [InlineData("lower", "jOHN o'neil", "john o'neil")]
    [InlineData("title", "jOHN smith-JONES", "John Smith-Jones")]
    [InlineData("none", "jOHN", "jOHN")]
    public void CaseNormalisation(string mode, string raw, string expected) =>
        Assert.Equal(expected, E(T.Col($"\"case\":\"{mode}\""), raw).Value);

    [Theory]
    [InlineData("AB123", null)]
    [InlineData("AB12", "regex")]
    [InlineData("ab123", "regex")]
    [InlineData("AB1234", "regex")]
    public void RegexRule(string raw, string? rule) =>
        Assert.Equal(rule, E(T.Col("\"regex\":\"^[A-Z]{2}\\\\d{3}$\""), raw).Failure?.Rule);

    [Fact]
    public void RegexRunsAfterCaseNormalisation() =>
        Assert.Null(E(T.Col("\"case\":\"upper\",\"regex\":\"^[A-Z]+$\""), "abc").Failure);

    [Fact]
    public void CatastrophicRegexTimesOutAsAViolationInsteadOfHanging()
    {
        var c = T.Col("\"regex\":\"^(a+)+$\"");
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var ev = E(c, new string('a', 40) + "!");
        Assert.Equal("regex", ev.Failure?.Rule);
        Assert.Contains("timed out", ev.Failure!.Message);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t")]
    public void RequiredRejectsEmptyAndWhitespace(string raw) =>
        Assert.Equal("required", E(T.Col("\"required\":true"), raw).Failure?.Rule);

    [Fact]
    public void RequiredRejectsNullCell() => Assert.Equal("required", E(T.Col("\"required\":true"), null).Failure?.Rule);

    [Fact]
    public void OptionalEmptyIsEmptyNotAnError()
    {
        var ev = E(T.Col("\"type\":\"int\""), "  ");
        Assert.True(ev.Empty);
        Assert.Null(ev.Failure);
        Assert.Null(ev.Value);
    }

    [Fact]
    public void TrimCanBeSwitchedOff()
    {
        Assert.Equal("  x ", E(T.Col("\"trim\":false"), "  x ").Value);
        Assert.False(E(T.Col("\"trim\":false,\"required\":true"), "   ").Empty);
        Assert.Equal("type", E(T.Col("\"type\":\"int\",\"trim\":false"), " 5").Failure?.Rule);
    }

    [Fact]
    public void ErrorMessagesClipHugeValues()
    {
        var ev = E(T.Col("\"type\":\"int\""), new string('9', 5000) + "x");
        Assert.True(ev.Failure!.Message.Length < 200);
    }
}
