namespace PutridParrot.Kql.Tests;

public class DiagnosticTests
{
    private readonly KqlContext _kql = TestData.Context();

    [Fact]
    public void SyntaxError_ThrowsWithDiagnostics()
    {
        var ex = Assert.Throws<KqlException>(() => _kql.Execute("Orders | where"));
        Assert.NotEmpty(ex.Diagnostics);
    }

    [Fact]
    public void UnknownColumn_IsReported()
    {
        var diagnostics = _kql.Validate("Orders | where Nope > 1");
        Assert.Single(diagnostics);
        Assert.Contains("Nope", diagnostics[0].Message);
        Assert.Equal(15, diagnostics[0].Start);
    }

    [Fact]
    public void UnknownTable_IsReported()
    {
        Assert.NotEmpty(_kql.Validate("Missing | take 1"));
    }

    [Fact]
    public void ValidQuery_HasNoDiagnostics()
    {
        Assert.Empty(_kql.Validate("Orders | summarize count() by Region"));
    }

    [Fact]
    public void UnsupportedOperator_ThrowsNotSupportedWithPosition()
    {
        var ex = Assert.Throws<KqlNotSupportedException>(() => _kql.Execute("Orders | fork (take 1)"));
        Assert.Equal("fork", ex.Feature);
        Assert.Equal(9, ex.Position);
    }

    [Fact]
    public void UnsupportedPlugin_IsReported()
    {
        var ex = Assert.Throws<KqlNotSupportedException>(() => _kql.Execute("Orders | evaluate autocluster()"));
        Assert.Equal("evaluate autocluster", ex.Feature);
    }

    [Fact]
    public void Render_IsAcceptedAndIgnored()
    {
        Assert.Equal(6, _kql.Execute("Orders | render barchart").Count);
    }
}
