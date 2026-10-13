using Npgsql;
using OpsFlow.Infrastructure.Persistence;

namespace OpsFlow.Tests;

public class ConnectionStringTests
{
    private static NpgsqlConnectionStringBuilder Parse(string url) => new(PostgresConnectionString.Normalize(url));

    [Fact]
    public void A_neon_style_url_becomes_an_npgsql_connection_string()
    {
        var b = Parse("postgresql://app_owner:s3cret@ep-cool-123.eu-central-1.aws.neon.tech/neondb?sslmode=require&channel_binding=require");

        Assert.Equal("ep-cool-123.eu-central-1.aws.neon.tech", b.Host);
        Assert.Equal(5432, b.Port);
        Assert.Equal("neondb", b.Database);
        Assert.Equal("app_owner", b.Username);
        Assert.Equal("s3cret", b.Password);
        Assert.Equal(SslMode.Require, b.SslMode);
        Assert.Equal(ChannelBinding.Require, b.ChannelBinding);
    }

    [Fact]
    public void Percent_encoded_characters_in_the_password_are_decoded()
    {
        var b = Parse("postgres://user:p%40ss%3Aw%2Ford%23@db.example.com:6543/my%20db");

        Assert.Equal("p@ss:w/ord#", b.Password);
        Assert.Equal(6543, b.Port);
        Assert.Equal("my db", b.Database);
    }

    [Theory]
    [InlineData("disable", SslMode.Disable)]
    [InlineData("prefer", SslMode.Prefer)]
    [InlineData("require", SslMode.Require)]
    [InlineData("verify-full", SslMode.VerifyFull)]
    public void The_ssl_mode_is_honoured(string mode, SslMode expected) =>
        Assert.Equal(expected, Parse($"postgres://u:p@h/d?sslmode={mode}").SslMode);

    [Fact]
    public void A_url_without_a_password_is_accepted()
    {
        var b = Parse("postgres://user@localhost/opsflow");

        Assert.Equal("user", b.Username);
        Assert.True(string.IsNullOrEmpty(b.Password));
    }

    [Theory]
    [InlineData("Host=localhost;Database=opsflow;Username=postgres;Password=postgres")]
    [InlineData("Host=db;Database=opsflow;Username=u;Password=p@ss/word;SSL Mode=Require")]
    public void Key_value_strings_pass_through_untouched(string connectionString) =>
        Assert.Equal(connectionString, PostgresConnectionString.Normalize(connectionString));

    [Fact]
    public void Prepare_turns_off_the_gss_probe_unless_the_caller_chose_otherwise()
    {
        Assert.Equal(GssEncryptionMode.Disable,
            new NpgsqlConnectionStringBuilder(PostgresConnectionString.Prepare("Host=h;Database=d;Username=u;Password=p")).GssEncryptionMode);
        Assert.Equal(GssEncryptionMode.Disable,
            new NpgsqlConnectionStringBuilder(PostgresConnectionString.Prepare("postgres://u:p@h/d?sslmode=require")).GssEncryptionMode);
        Assert.Equal(GssEncryptionMode.Prefer,
            new NpgsqlConnectionStringBuilder(PostgresConnectionString.Prepare("Host=h;Database=d;Username=u;Password=p;Gss Encryption Mode=Prefer")).GssEncryptionMode);
    }

    [Theory]
    [InlineData("postgres://")]
    [InlineData("postgresql:///onlypath")]
    [InlineData("postgres://u:p@h/d?sslmode=bogus")]
    [InlineData("postgres://u:p@h/d?channel_binding=bogus")]
    public void Broken_urls_fail_loudly_instead_of_connecting_somewhere_unexpected(string url) =>
        Assert.Throws<FormatException>(() => PostgresConnectionString.Normalize(url));
}
