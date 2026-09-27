using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Paqueteria.Application.Security;
using Paqueteria.Infrastructure.Security;

namespace Paqueteria.UnitTests.Security;

/// <summary>
/// REG-PENDING-MEMBERSHIP-STORAGE: the email lookup key fails the start outside Development and Testing
/// whenever a PostgreSQL provider is enabled, a malformed key fails everywhere, and the hash is keyed,
/// versioned and stable for the normalized address.
/// </summary>
public sealed class EmailLookupHashingTests
{
    private static readonly string Key = Convert.ToBase64String(Encoding.UTF8.GetBytes("unit-test-email-lookup-key-0123456789"));
    private static readonly string OtherKey = Convert.ToBase64String(Encoding.UTF8.GetBytes("unit-test-email-lookup-key-9876543210"));

    [Theory]
    [InlineData("Production", "Tenancy:Provider")]
    [InlineData("Staging", "IdentityBootstrap:Provider")]
    [InlineData("DevSynthetic", "Tenancy:Provider")]
    public void A_missing_key_fails_the_start_outside_Development_and_Testing(string environment, string provider)
    {
        var failure = Assert.Throws<OptionsValidationException>(() =>
            Resolve(new Dictionary<string, string?> { [provider] = "PostgreSql" }, environment));
        Assert.Contains("EmailLookup:Keys is required", string.Join(' ', failure.Failures), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Development")]
    [InlineData("Testing")]
    public void Development_and_Testing_may_run_without_a_key_and_the_hasher_is_then_unavailable(string environment)
    {
        using var provider = Build(new Dictionary<string, string?> { ["Tenancy:Provider"] = "PostgreSql" }, environment);
        var hasher = provider.GetRequiredService<IEmailLookupHasher>();
        Assert.False(hasher.IsAvailable);
        Assert.Throws<EmailLookupUnavailableException>(() => hasher.HashForStorage("a@b.c"));
    }

    [Fact]
    public void Without_a_PostgreSql_provider_production_does_not_need_the_key()
    {
        Assert.Empty(Resolve(new Dictionary<string, string?>(), "Production").Keys);
    }

    [Theory]
    [InlineData("1", "not base64!")]
    [InlineData("1", "c2hvcnQ=")]
    [InlineData("0", null)]
    [InlineData("40000", null)]
    [InlineData("2", null)]
    public void A_malformed_key_ring_fails_in_every_environment(string version, string? value)
    {
        var settings = new Dictionary<string, string?>
        {
            ["EmailLookup:CurrentKeyVersion"] = "1",
            [$"EmailLookup:Keys:{version}"] = value ?? Key,
        };
        Assert.Throws<OptionsValidationException>(() => Resolve(settings, "Development"));
    }

    [Fact]
    public void The_hash_is_keyed_versioned_and_stable_and_lookup_covers_every_version()
    {
        var settings = new Dictionary<string, string?>
        {
            ["EmailLookup:CurrentKeyVersion"] = "2",
            ["EmailLookup:Keys:1"] = Key,
            ["EmailLookup:Keys:2"] = OtherKey,
            ["Tenancy:Provider"] = "PostgreSql",
        };
        using var provider = Build(settings, "Production");
        var hasher = provider.GetRequiredService<IEmailLookupHasher>();

        var stored = hasher.HashForStorage("persona@example.com");
        Assert.Equal(2, stored.KeyVersion);
        Assert.Equal(32, stored.Hash.Length);
        Assert.Equal(stored.Hash, hasher.HashForStorage("persona@example.com").Hash);
        Assert.NotEqual(stored.Hash, hasher.HashForStorage("otra@example.com").Hash);

        var lookup = hasher.HashForLookup("persona@example.com");
        Assert.Equal([2, 1], lookup.Select(hash => hash.KeyVersion));
        Assert.Equal(stored.Hash, lookup[0].Hash);
        Assert.NotEqual(lookup[0].Hash, lookup[1].Hash);
        // Never a plain digest of the address: without the key the bytes cannot be recomputed.
        Assert.NotEqual(
            System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes("persona@example.com")),
            stored.Hash);
    }

    [Theory]
    [InlineData("  Persona@Example.COM ", "persona@example.com")]
    [InlineData("JOSÉ@EXAMPLE.MX", "josé@example.mx")]
    [InlineData("ÁNGEL@example.mx", "ángel@example.mx")]
    public void Normalization_trims_composes_and_lowercases(string input, string expected)
    {
        Assert.True(EmailLookupNormalizer.TryNormalize(input, out var normalized));
        Assert.Equal(expected, normalized);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("@example.com")]
    [InlineData("persona@")]
    [InlineData("persona")]
    [InlineData("a@b@c")]
    [InlineData("per sona@example.com")]
    [InlineData("persona@exa\u0000mple.com")]
    public void Unusable_addresses_are_refused(string? input)
    {
        Assert.False(EmailLookupNormalizer.TryNormalize(input, out _));
    }

    [Fact]
    public void An_unpaired_surrogate_is_refused()
    {
        // Built in code: theory data would be serialized and the lone surrogate replaced.
        Assert.False(EmailLookupNormalizer.TryNormalize(new string('\ud800', 1) + "@example.com", out _));
    }

    private static EmailLookupOptions Resolve(IEnumerable<KeyValuePair<string, string?>> settings, string environmentName)
    {
        using var provider = Build(settings, environmentName);
        return provider.GetRequiredService<IOptions<EmailLookupOptions>>().Value;
    }

    private static ServiceProvider Build(IEnumerable<KeyValuePair<string, string?>> settings, string environmentName)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        return new ServiceCollection()
            .AddEmailLookupHashing(configuration, new TestHostEnvironment(environmentName))
            .BuildServiceProvider();
    }

    private sealed class TestHostEnvironment(string environmentName) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = environmentName;
        public string ApplicationName { get; set; } = nameof(EmailLookupHashingTests);
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
