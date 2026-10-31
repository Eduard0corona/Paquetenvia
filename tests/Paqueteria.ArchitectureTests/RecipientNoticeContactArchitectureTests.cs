using System.Reflection;
using Locations.Infrastructure.Persistence.Migrations;
using Paqueteria.ArchitectureTests.Architecture;
using Paqueteria.Infrastructure.Database.Baseline;
using Paqueteria.Infrastructure.Security.Pii;

namespace Paqueteria.ArchitectureTests;

/// <summary>
/// NTF-RECIPIENT-NOTICES-2026-10-20 (E4-P2 "Llave propia para avisos"; AI-24 <c>recipient_notices.contact.roles</c>;
/// AI-08 NTF-002 and NTF-005): the API can only wrap with the recipient-notice key and never opens a notice copy; the
/// Worker only unwraps with it (AI-08 NTF-005, through one opener and one resolver) and never reaches the PII key; only
/// the Locations module, its lane step and the baseline assertions name the new columns; and AI-06 carries the lane
/// step's DDL byte for byte.
/// </summary>
public sealed class RecipientNoticeContactArchitectureTests
{
    private static readonly string[] NoticeColumns = ["notice_phone_ciphertext", "notice_email_ciphertext", "notice_key_version"];

    [Fact]
    public void The_recipient_notice_key_contract_has_no_way_to_open_a_copy()
    {
        Assert.Equal(
            ["GetCurrentKeyVersionAsync", "WrapKeyAsync"],
            typeof(IRecipientNoticeKeyWrapClient).GetInterfaces().Append(typeof(IRecipientNoticeKeyWrapClient))
                .SelectMany(type => type.GetMethods())
                .Select(method => method.Name)
                .Order(StringComparer.Ordinal));
        Assert.Equal(["SealAsync"], typeof(IRecipientNoticeEnvelopeSealer).GetMethods().Select(method => method.Name));
        Assert.False(typeof(IPiiKeyWrapClient).IsAssignableFrom(typeof(AzureKeyVaultRecipientNoticeKeyWrapClient)));
        Assert.False(typeof(IPiiEnvelopeProtector).IsAssignableFrom(typeof(RecipientNoticeEnvelopeSealer)));
        Assert.DoesNotContain(
            typeof(AzureKeyVaultRecipientNoticeKeyWrapClient).GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static),
            method => method.Name.Contains("Unwrap", StringComparison.OrdinalIgnoreCase) ||
                method.Name.Contains("Decrypt", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The only sources that unwrap with the notice key or open a notice copy (AI-08 NTF-005).</summary>
    private static readonly string[] NoticeCopyOpeners =
    [
        // The Worker's unwrap-only Key Vault client and envelope opener.
        "src/BuildingBlocks/Paqueteria.Infrastructure/Security/Pii/RecipientNoticeKeyOpener.cs",
        // The Worker's contact resolver: it opens a copy in memory after the reader's transaction committed.
        "src/Modules/Notifications/Notifications.Infrastructure/RecipientNotices/RecipientNoticeContacts.cs",
    ];

    [Fact]
    public void Only_the_worker_opener_unwraps_the_recipient_notice_key_and_only_its_resolver_opens_a_copy()
    {
        var users = SourceFiles("src")
            .Where(file => file.Text.Contains("RecipientNoticeKey", StringComparison.Ordinal) ||
                           file.Text.Contains("IRecipientNoticeEnvelopeSealer", StringComparison.Ordinal) ||
                           file.Text.Contains("IRecipientNoticeEnvelopeOpener", StringComparison.Ordinal) ||
                           file.Text.Contains("NoticeKeyProtector", StringComparison.Ordinal) ||
                           file.Text.Contains("LocationNoticeContact", StringComparison.Ordinal) ||
                           file.Text.Contains("RecipientNoticeContactCopies", StringComparison.Ordinal))
            .ToArray();

        Assert.NotEmpty(users);
        Assert.All(users.Where(file => !NoticeCopyOpeners.Contains(file.Path)), file =>
        {
            Assert.DoesNotContain(".UnwrapKeyAsync(", file.Text, StringComparison.Ordinal);
            Assert.DoesNotContain(".UnwrapKey(", file.Text, StringComparison.Ordinal);
            Assert.DoesNotContain(".Decrypt(", file.Text, StringComparison.Ordinal);
            Assert.DoesNotContain(".DecryptAsync(", file.Text, StringComparison.Ordinal);
            Assert.DoesNotContain(".UnprotectAsync(", file.Text, StringComparison.Ordinal);
            Assert.DoesNotContain("opener.OpenAsync(", file.Text, StringComparison.Ordinal);
            Assert.DoesNotContain("CryptographyClient", file.Text, StringComparison.Ordinal);
            Assert.DoesNotContain("IPiiKeyWrapClient", file.Text, StringComparison.Ordinal);
        });

        // Nothing outside those two files ever reaches the opener or the unwrap client, under any name.
        Assert.Equal(
            NoticeCopyOpeners.Order(StringComparer.Ordinal),
            SourceFiles("src")
                .Where(file => file.Text.Contains("IRecipientNoticeEnvelopeOpener", StringComparison.Ordinal) ||
                               file.Text.Contains("IRecipientNoticeKeyUnwrapClient", StringComparison.Ordinal))
                .Select(file => file.Path)
                .Order(StringComparer.Ordinal));

        // The Worker opener only unwraps: no wrap, no current version, no PII key client, no decrypt.
        Assert.Equal(["UnwrapKeyAsync"], typeof(IRecipientNoticeKeyUnwrapClient).GetMethods().Select(method => method.Name));
        Assert.Equal(["OpenAsync"], typeof(IRecipientNoticeEnvelopeOpener).GetMethods().Select(method => method.Name));
        var opener = File.ReadAllText(TestRepository.GetPath(NoticeCopyOpeners[0]));
        Assert.Contains("client.UnwrapKeyAsync(KeyWrapAlgorithm.RsaOaep256, wrappedKey, cancellationToken)", opener, StringComparison.Ordinal);
        var withoutUnwrap = opener.Replace("UnwrapKeyAsync", "U", StringComparison.Ordinal);
        foreach (var forbidden in new[] { "WrapKey", "GetKeyAsync", "DecryptAsync", "IPiiKeyWrapClient", "PiiEnvelopeProtector", "IPiiEnvelopeProtector" })
        {
            Assert.DoesNotContain(forbidden, withoutUnwrap, StringComparison.Ordinal);
        }

        // The API side is unchanged: the Key Vault notice client reads the key only (GET) and wraps in process with its
        // public part.
        var client = File.ReadAllText(TestRepository.GetPath("src/BuildingBlocks/Paqueteria.Infrastructure/Security/Pii/RecipientNoticeKey.cs"));
        Assert.Contains("_keyClient.GetKeyAsync(", client, StringComparison.Ordinal);
        Assert.Contains("rsa.Encrypt(dataKey, RSAEncryptionPadding.OaepSHA256)", client, StringComparison.Ordinal);
        Assert.DoesNotContain("WrapKeyAsync(KeyWrapAlgorithm", client, StringComparison.Ordinal);
        Assert.DoesNotContain("ExportParameters(true)", client, StringComparison.Ordinal);
        Assert.DoesNotContain(".UnwrapKeyAsync(", client, StringComparison.Ordinal);
    }

    [Fact]
    public void The_worker_unwraps_only_the_notice_key_and_registers_neither_the_pii_key_nor_the_locations_module()
    {
        // AI-08 NTF-005: the Worker composes the Locations notice contact reader and, through the Notifications lane, the
        // unwrap-only opener of the notice key; never the PII key, the API's wrap side or the Locations module.
        var worker = File.ReadAllText(TestRepository.GetPath("src/Paqueteria.Worker/Program.cs"));
        Assert.DoesNotContain("AddLocationsInfrastructure", worker, StringComparison.Ordinal);
        Assert.DoesNotContain("AddAzureKeyVaultPiiProtection", worker, StringComparison.Ordinal);
        Assert.DoesNotContain("AddAzureKeyVaultRecipientNoticeKey", worker, StringComparison.Ordinal);
        Assert.DoesNotContain("PiiProtection", worker, StringComparison.Ordinal);
        Assert.Contains("builder.Services.AddLocationsRecipientNoticeContactReader(builder.Configuration);", worker, StringComparison.Ordinal);
        var notifications = File.ReadAllText(TestRepository.GetPath("src/Modules/Notifications/Notifications.Infrastructure/DependencyInjection.cs"));
        Assert.Contains("services.AddRecipientNoticeKeyOpener(configuration,", notifications, StringComparison.Ordinal);
        Assert.DoesNotContain("AddAzureKeyVaultPiiProtection", notifications, StringComparison.Ordinal);
        Assert.DoesNotContain("AddAzureKeyVaultRecipientNoticeKey", notifications, StringComparison.Ordinal);
        Assert.DoesNotContain(
            SolutionCatalog.Worker.Assembly.GetReferencedAssemblies(),
            reference => reference.Name == "Locations.Endpoints");

        // The Locations registration the Worker calls adds the reader only, and the reader reads the notice columns only.
        var reader = File.ReadAllText(TestRepository.GetPath(
            "src/Modules/Locations/Locations.Infrastructure/RecipientNotices/PostgreSqlRecipientNoticeContactReader.cs"));
        Assert.Contains("SELECT notice_key_version,notice_phone_ciphertext,notice_email_ciphertext", reader, StringComparison.Ordinal);
        foreach (var forbidden in new[] { "address_ciphertext", "contact_name_ciphertext", ",phone_ciphertext", ",email_ciphertext", "pii_key_version", "IPii", "Protector" })
        {
            Assert.DoesNotContain(forbidden, reader, StringComparison.Ordinal);
        }

        // The API registers the Locations module, whose selector keeps the notice key Disabled unless configured, and
        // never the opener or the Notifications lane.
        var api = File.ReadAllText(TestRepository.GetPath("src/Paqueteria.Api/Program.cs"));
        Assert.Contains("AddLocationsInfrastructure", api, StringComparison.Ordinal);
        Assert.DoesNotContain("AddRecipientNoticeKeyOpener", api, StringComparison.Ordinal);
        Assert.DoesNotContain("AddNotificationsInfrastructure", api, StringComparison.Ordinal);
    }

    [Fact]
    public void Only_the_locations_module_its_lane_step_and_the_baseline_assertions_name_the_notice_columns()
    {
        var users = SourceFiles("src")
            .Concat(SourceFiles("tools"))
            .Where(file => NoticeColumns.Any(column => file.Text.Contains(column, StringComparison.Ordinal)))
            .Select(file => file.Path)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.All(users, path => Assert.True(
            path.StartsWith("src/Modules/Locations/", StringComparison.Ordinal) ||
            path == "src/BuildingBlocks/Paqueteria.Infrastructure/Database/Baseline/DatabaseBaselineAssertions.cs",
            path));
        Assert.Contains("src/Modules/Locations/Locations.Infrastructure/Persistence/LocationsDbContext.cs", users);
        Assert.Contains(
            "src/Modules/Locations/Locations.Infrastructure/Persistence/Migrations/20261020000100_AddRecipientNoticeContact.cs",
            users);
        Assert.Contains("src/BuildingBlocks/Paqueteria.Infrastructure/Database/Baseline/DatabaseBaselineAssertions.cs", users);
        // The Locations endpoints, readers and every other module never name them.
        Assert.DoesNotContain(users, path => path.StartsWith("src/Modules/Locations/Locations.Endpoints/", StringComparison.Ordinal));
        Assert.DoesNotContain(users, path => path.EndsWith("LocationPiiReaders.cs", StringComparison.Ordinal));
    }

    [Fact]
    public void AI06_carries_the_lane_step_ddl_byte_for_byte_and_the_baseline_assertions_expect_its_catalog()
    {
        var schema = File.ReadAllText(TestRepository.GetPath("docs/normative/v0.6/database/AI-06_SCHEMA.sql"));
        Assert.Equal(1, Count(schema, AddRecipientNoticeContact.CanonicalDdl));
        Assert.Equal(1, Count(schema, "ALTER TABLE locations.locations"));
        // The DDL follows the CREATE TABLE and its indexes, before the row level security loop.
        Assert.True(schema.IndexOf("CREATE INDEX locations_owner_city_idx", StringComparison.Ordinal) <
            schema.IndexOf(AddRecipientNoticeContact.CanonicalDdl, StringComparison.Ordinal));
        Assert.True(schema.IndexOf(AddRecipientNoticeContact.CanonicalDdl, StringComparison.Ordinal) <
            schema.IndexOf("FORCE ROW LEVEL SECURITY', item", StringComparison.Ordinal));

        Assert.Equal(AddRecipientNoticeContact.MigrationId, DatabaseBaselineAssertions.RecipientNoticeContactMigrationId);
        Assert.Equal(
            AddRecipientNoticeContact.Columns.Order(StringComparer.Ordinal).Select(column => column.StartsWith("notice_key", StringComparison.Ordinal)
                ? $"{column}:text:YES:"
                : $"{column}:bytea:YES:"),
            DatabaseBaselineAssertions.RecipientNoticeContactColumns);
        Assert.Equal(
            [
                $"{AddRecipientNoticeContact.NoticeEmailCheckName}:{AddRecipientNoticeContact.NoticeEmailCheckDefinition}:true",
                $"{AddRecipientNoticeContact.NoticeKeyVersionCheckName}:{AddRecipientNoticeContact.NoticeKeyVersionCheckDefinition}:true",
            ],
            DatabaseBaselineAssertions.RecipientNoticeContactChecks);
        Assert.Contains(AddRecipientNoticeContact.CanonicalDdl, AddRecipientNoticeContact.RenderUpSql(), StringComparison.Ordinal);
    }

    private static int Count(string value, string fragment) => value.Split(fragment, StringSplitOptions.None).Length - 1;

    private static IEnumerable<(string Path, string Text)> SourceFiles(string relativeRoot) =>
        Directory.EnumerateFiles(TestRepository.GetPath(relativeRoot), "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal) &&
                           !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Select(path => (Path.GetRelativePath(TestRepository.GetPath("."), path).Replace('\\', '/'), File.ReadAllText(path)));
}
