namespace Apps2Samsung.Cli.Tests;

public sealed class SignOptionsTests
{
    [Fact]
    public void Sign_takes_the_file_the_output_and_the_tv()
    {
        var (o, error) = CliOptions.Parse(new[] { "sign", "app.wgt", "-o", "out.wgt", "--tv", "192.168.1.50" });
        Assert.Null(error);
        Assert.Equal("sign", o.Command);
        Assert.Equal(new[] { "app.wgt" }, o.Arguments);
        Assert.Equal("out.wgt", o.OutputPath);
        Assert.Equal("192.168.1.50", o.Tv);
    }

    [Fact]
    public void Sign_takes_duids_instead_of_a_tv_and_the_long_output_flag()
    {
        var (o, error) = CliOptions.Parse(new[] { "sign", "app.tpk", "--output", "out.tpk", "--duids", "ABCDEF123", "--partner" });
        Assert.Null(error);
        Assert.Equal("out.tpk", o.OutputPath);
        Assert.Equal("ABCDEF123", o.ManualDuids);
        Assert.True(o.Partner);
        Assert.Null(o.Tv);
    }

    [Fact]
    public void Output_flag_needs_a_value()
    {
        var (_, error) = CliOptions.Parse(new[] { "sign", "app.wgt", "-o" });
        Assert.NotNull(error);
    }

    [Fact]
    public void Sign_has_its_own_help()
    {
        Assert.Contains("apps2samsung-cli sign <file> -o", Help.For("sign"));
        Assert.Contains("sign <file> -o <out>", Help.Usage);
    }
}
