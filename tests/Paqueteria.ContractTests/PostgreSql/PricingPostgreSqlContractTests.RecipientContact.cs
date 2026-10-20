using System.Security.Cryptography;
using System.Text;
using Locations.Application.Geocoding;
using Locations.Infrastructure.Geocoding;
using Locations.Infrastructure.Locations;
using Npgsql;
using NpgsqlTypes;
using Paqueteria.ContractTests.PostgreSql.Fixtures;
using Paqueteria.Infrastructure.Security.Pii;
using Pricing.Application.Quotes;

namespace Paqueteria.ContractTests.PostgreSql;

/// <summary>
/// NTF-RECIPIENT-NOTICES-2026-10-20 (E4 "se agrega como campo opcional en la nueva orden", E4-P2 "Llave propia para
/// avisos", E4-P17 "Dos copias, como el teléfono"; AI-08 NTF-002) against a real PostgreSQL/PostGIS: the destination
/// e-mail is sealed under the PII key and copied, with the phone, under the recipient-notice key; the origin never gets
/// either; the e-mail joins the Idempotency-Key fingerprint only when present; the AI-06 checks hold; the rows stay
/// under FORCE RLS; and neither the e-mail nor a notice copy reaches a quote, a snapshot, an idempotency answer, an
/// order, an audit row or an outbox payload, even after the order is created.
/// </summary>
public sealed partial class PricingPostgreSqlContractTests
{
    private const string TypedRecipientEmail = "  Destinatario.Ntf002@EXAMPLE.invalid ";
    private const string RecipientEmail = "Destinatario.Ntf002@example.invalid";

    [PostgreSqlContractFact]
    public async Task A_destination_email_is_sealed_twice_the_origin_never_and_nothing_leaks()
    {
        var data = SyntheticPricingData.Create();
        await SeedAsync(data);
        var vault = new Adp001FakeKeyVault();
        var piiEnvelope = new PiiEnvelopeProtector(vault);
        using var noticeKey = RSA.Create(3072);
        var noticeVersion = $"akv:recipient-notice-kek/{Guid.NewGuid():N}";
        var noticeSealer = new AzureKeyVaultLocationNoticeContactSealer(
            new RecipientNoticeEnvelopeSealer(new PublicNoticeKey(noticeKey.ExportParameters(false), noticeVersion)));
        await using var appDataSource = fixture.CreateAppDataSource(maxPoolSize: 8, applicationName: "NTF002.Quote.Contact");
        try
        {
            QuoteResult created;
            var command = CreateCommand(data, "ntf002-contact-quote-0001");
            command = command with { Destination = command.Destination with { Email = TypedRecipientEmail } };
            await using (var scope = CreateScope(
                             appDataSource,
                             noticeSealer: noticeSealer,
                             piiProtector: new AzureKeyVaultLocationPiiProtector(piiEnvelope)))
            {
                created = await scope.Service.CreateAsync(command, default);

                // The normalized e-mail is the one fingerprinted: the same address typed otherwise replays the quote.
                var replay = await scope.Service.CreateAsync(
                    command with { Destination = command.Destination with { Email = RecipientEmail } },
                    default);
                Assert.Equal(created.Id, replay.Id);
                foreach (var other in new string?[] { "otro.ntf002@example.invalid", "destinatario.ntf002@example.invalid", null })
                {
                    var conflict = await Assert.ThrowsAsync<QuoteValidationException>(() => scope.Service.CreateAsync(
                        command with { Destination = command.Destination with { Email = other } },
                        default));
                    Assert.Equal(QuoteValidationCode.IdempotencyConflict, conflict.Code);
                }

                Assert.Equal(2, scope.LocationResolver.CallCount);
            }

            Assert.DoesNotContain("ntf002", System.Text.Json.JsonSerializer.Serialize(created), StringComparison.OrdinalIgnoreCase);
            var origin = await ReadContactAsync(created.OriginLocationId);
            var destination = await ReadContactAsync(created.DestinationLocationId);

            Assert.Null(origin.Email);
            Assert.Null(origin.NoticePhone);
            Assert.Null(origin.NoticeEmail);
            Assert.Null(origin.NoticeKeyVersion);

            var destinationRow = new PiiBinding(data.OrganizationId, created.DestinationLocationId);
            Assert.Equal(vault.CurrentVersion, destination.PiiKeyVersion);
            Assert.Equal(
                RecipientEmail,
                await piiEnvelope.UnprotectAsync(destinationRow, "locations.email", destination.Email!, destination.PiiKeyVersion, default));
            Assert.Equal(noticeVersion, destination.NoticeKeyVersion);
            var worker = new PiiEnvelopeProtector(new PrivateNoticeKey(noticeKey, noticeVersion));
            Assert.Equal(
                "6672222222",
                await worker.UnprotectAsync(destinationRow, "locations.notice_phone", destination.NoticePhone!, noticeVersion, default));
            Assert.Equal(
                RecipientEmail,
                await worker.UnprotectAsync(destinationRow, "locations.notice_email", destination.NoticeEmail!, noticeVersion, default));
            // The PII key cannot open a notice copy, and the notice key cannot open the PII copy.
            await Assert.ThrowsAnyAsync<Exception>(() => piiEnvelope.UnprotectAsync(
                destinationRow, "locations.notice_email", destination.NoticeEmail!, noticeVersion, default));
            await Assert.ThrowsAnyAsync<Exception>(() => worker.UnprotectAsync(
                destinationRow, "locations.email", destination.Email!, destination.PiiKeyVersion, default));
            foreach (var ciphertext in new[] { destination.Email!, destination.NoticePhone!, destination.NoticeEmail! })
            {
                Assert.True(ciphertext.AsSpan().IndexOf(Encoding.UTF8.GetBytes(RecipientEmail)) < 0);
                Assert.True(ciphertext.AsSpan().IndexOf(Encoding.UTF8.GetBytes("6672222222")) < 0);
            }

            var order = await CreateOrderAsync(data, created.Id, "ntf002-contact-order-0001");
            Assert.Equal(created.Id, order.QuoteId);

            string[] secrets = [RecipientEmail, RecipientEmail.ToLowerInvariant(), TypedRecipientEmail.Trim(), "Destinatario.Ntf002"];
            Assert.Equal(0L, await Adp001FakeKeyVault.CountPlaintextInAuditAndOutboxAsync(fixture.AdminDataSource, data.OrganizationId, secrets));
            foreach (var secret in secrets)
            {
                Assert.Equal(0L, await ScalarAdminAsync<long>(
                    """
                    SELECT (SELECT count(*) FROM pricing.quotes q WHERE q.owner_org_id=@org AND to_jsonb(q)::text ILIKE '%' || @secret || '%')
                         + (SELECT count(*) FROM platform.idempotency_keys i WHERE i.owner_org_id=@org AND to_jsonb(i)::text ILIKE '%' || @secret || '%')
                         + (SELECT count(*) FROM orders.orders o WHERE o.owner_org_id=@org AND to_jsonb(o)::text ILIKE '%' || @secret || '%')
                         + (SELECT count(*) FROM platform.outbox_events e WHERE e.owner_org_id=@org AND e.payload::text ILIKE '%email%')
                         + (SELECT count(*) FROM platform.audit_logs a WHERE a.org_id=@org AND a.payload_redacted::text ILIKE '%email%')
                    """,
                    P("org", data.OrganizationId),
                    new NpgsqlParameter<string>("secret", NpgsqlDbType.Text) { TypedValue = secret }));
            }

            Assert.False(await ScalarAdminAsync<bool>(
                "SELECT request_snapshot_redacted ? 'email' OR request_snapshot_redacted::text ILIKE '%notice%' FROM pricing.quotes WHERE id=@id",
                P("id", created.Id)));
            // The LOCATION_CREATED audit rows name the rows only.
            Assert.Equal(2L, await ScalarAdminAsync<long>(
                "SELECT count(*) FROM platform.audit_logs WHERE org_id=@org AND action='LOCATION_CREATED' AND payload_redacted='{}'::jsonb",
                P("org", data.OrganizationId)));
        }
        finally
        {
            await CleanupOrdersAsync(data);
            await CleanupAsync(data);
        }
    }

    [PostgreSqlContractFact]
    public async Task Without_a_notice_key_the_email_is_sealed_alone_and_the_AI06_checks_hold()
    {
        var data = SyntheticPricingData.Create();
        await SeedAsync(data);
        await using var appDataSource = fixture.CreateAppDataSource(maxPoolSize: 8, applicationName: "NTF002.Quote.NoNoticeKey");
        try
        {
            QuoteResult withEmail;
            QuoteResult withoutEmail;
            await using (var scope = CreateScope(appDataSource))
            {
                var command = CreateCommand(data, "ntf002-no-notice-key-0001");
                withEmail = await scope.Service.CreateAsync(
                    command with { Destination = command.Destination with { Email = RecipientEmail } },
                    default);
                withoutEmail = await scope.Service.CreateAsync(CreateCommand(data, "ntf002-no-notice-key-0002"), default);
            }

            var sealedEmail = await ReadContactAsync(withEmail.DestinationLocationId);
            Assert.Equal(new DeterministicMockLocationPiiProtector().Protect(RecipientEmail, DeterministicMockLocationPiiProtector.KeyVersion), sealedEmail.Email);
            Assert.Null(sealedEmail.NoticePhone);
            Assert.Null(sealedEmail.NoticeEmail);
            Assert.Null(sealedEmail.NoticeKeyVersion);
            foreach (var id in new[] { withoutEmail.OriginLocationId, withoutEmail.DestinationLocationId, withEmail.OriginLocationId })
            {
                var row = await ReadContactAsync(id);
                Assert.Null(row.Email);
                Assert.Null(row.NoticePhone);
                Assert.Null(row.NoticeEmail);
                Assert.Null(row.NoticeKeyVersion);
            }

            // locations_notice_key_version_check and locations_notice_email_check (AI-06).
            foreach (var (sql, constraint) in new[]
                     {
                         ("UPDATE locations.locations SET notice_key_version='akv:recipient-notice-kek/0' WHERE id=@id", "locations_notice_key_version_check"),
                         ("UPDATE locations.locations SET notice_phone_ciphertext='\\x01' WHERE id=@id", "locations_notice_key_version_check"),
                         ("UPDATE locations.locations SET notice_email_ciphertext='\\x01',notice_key_version='v' WHERE id=@id", "locations_notice_email_check"),
                     })
            {
                var violation = await Assert.ThrowsAsync<PostgresException>(() =>
                    ExecuteAdminAsync(sql, P("id", withoutEmail.DestinationLocationId)));
                Assert.Equal(PostgresErrorCodes.CheckViolation, violation.SqlState);
                Assert.Equal(constraint, violation.ConstraintName);
            }

            await ExecuteAdminAsync(
                "UPDATE locations.locations SET notice_phone_ciphertext='\\x01',notice_key_version='v' WHERE id=@id",
                P("id", withoutEmail.DestinationLocationId));
            await ExecuteAdminAsync(
                "UPDATE locations.locations SET notice_email_ciphertext='\\x02',notice_key_version='v' WHERE id=@id",
                P("id", withEmail.DestinationLocationId));
        }
        finally
        {
            await CleanupAsync(data);
        }
    }

    [PostgreSqlContractFact]
    public async Task The_contact_columns_stay_under_force_rls_for_the_api_and_the_worker()
    {
        var data = SyntheticPricingData.Create();
        var foreign = Guid.NewGuid();
        await SeedAsync(data);
        await ExecuteAdminAsync(
            "INSERT INTO organizations.organizations(id,legal_name,display_name,organization_type) VALUES (@id,'NTF002 foreign','NTF002 foreign','BUSINESS')",
            P("id", foreign));
        await using var appDataSource = fixture.CreateAppDataSource(maxPoolSize: 8, applicationName: "NTF002.Quote.Rls");
        try
        {
            QuoteResult created;
            await using (var scope = CreateScope(appDataSource, noticeSealer: new DeterministicMockLocationNoticeContactSealer()))
            {
                var command = CreateCommand(data, "ntf002-rls-quote-0001");
                created = await scope.Service.CreateAsync(
                    command with { Destination = command.Destination with { Email = RecipientEmail } },
                    default);
            }

            const string read =
                "SELECT count(*) FROM locations.locations WHERE id=@id AND email_ciphertext IS NOT NULL AND notice_phone_ciphertext IS NOT NULL AND notice_email_ciphertext IS NOT NULL AND notice_key_version='ntf002-notice-mock-v1'";
            foreach (var (dataSource, role) in new[] { (fixture.AppDataSource, "paqueteria_app"), (fixture.WorkerDataSource, "paqueteria_worker") })
            {
                foreach (var (organization, expected) in new[] { (data.OrganizationId, 1L), (foreign, 0L) })
                {
                    await using var tenant = await TenantTransaction.BeginAsync(dataSource, role, data.ActorId, [organization]);
                    await using var command = new NpgsqlCommand(read, tenant.Connection, tenant.Transaction);
                    command.Parameters.Add(P("id", created.DestinationLocationId));
                    Assert.Equal(expected, await command.ExecuteScalarAsync());
                    await tenant.RollbackAsync();
                }

                await using var empty = await TenantTransaction.BeginAsync(dataSource, role, data.ActorId, []);
                await using var none = new NpgsqlCommand("SELECT count(*) FROM locations.locations", empty.Connection, empty.Transaction);
                Assert.Equal(0L, await none.ExecuteScalarAsync());
                await empty.RollbackAsync();
            }

            // A foreign tenant cannot write a notice copy into the row either.
            await using (var foreignWrite = await TenantTransaction.BeginAsync(fixture.AppDataSource, "paqueteria_app", data.ActorId, [foreign]))
            {
                await using var update = new NpgsqlCommand(
                    "UPDATE locations.locations SET notice_key_version=NULL,notice_phone_ciphertext=NULL,notice_email_ciphertext=NULL WHERE id=@id",
                    foreignWrite.Connection,
                    foreignWrite.Transaction);
                update.Parameters.Add(P("id", created.DestinationLocationId));
                Assert.Equal(0, await update.ExecuteNonQueryAsync());
                await foreignWrite.RollbackAsync();
            }

            Assert.Equal(
                // The table's runtime privileges are unchanged (AI-18 declared contract step 4): the new columns carry them.
                ["paqueteria_app:INSERT:True", "paqueteria_app:SELECT:True", "paqueteria_worker:INSERT:True", "paqueteria_worker:SELECT:True"],
                await ColumnPrivilegesAsync());
        }
        finally
        {
            await CleanupAsync(data);
            await ExecuteAdminAsync("DELETE FROM organizations.organizations WHERE id=@id", P("id", foreign));
        }
    }

    private async Task<string[]> ColumnPrivilegesAsync()
    {
        var result = new List<string>();
        foreach (var role in new[] { "paqueteria_app", "paqueteria_worker" })
        {
            foreach (var privilege in new[] { "INSERT", "SELECT" })
            {
                var all = await ScalarAdminAsync<bool>(
                    $"""
                    SELECT bool_and(has_column_privilege('{role}','locations.locations',c,'{privilege}'))
                    FROM unnest(ARRAY['email_ciphertext','notice_phone_ciphertext','notice_email_ciphertext','notice_key_version']) c
                    """);
                result.Add($"{role}:{privilege}:{all}");
            }
        }

        return [.. result];
    }

    private async Task<(byte[]? Email, byte[]? NoticePhone, byte[]? NoticeEmail, string? NoticeKeyVersion, string PiiKeyVersion)> ReadContactAsync(Guid locationId)
    {
        await using var command = fixture.AdminDataSource.CreateCommand(
            "SELECT email_ciphertext,notice_phone_ciphertext,notice_email_ciphertext,notice_key_version,pii_key_version FROM locations.locations WHERE id=@id");
        command.Parameters.Add(P("id", locationId));
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return (
            reader.IsDBNull(0) ? null : reader.GetFieldValue<byte[]>(0),
            reader.IsDBNull(1) ? null : reader.GetFieldValue<byte[]>(1),
            reader.IsDBNull(2) ? null : reader.GetFieldValue<byte[]>(2),
            reader.IsDBNull(3) ? null : reader.GetString(3),
            reader.GetString(4));
    }

    /// <summary>The API side of the recipient-notice key: the public part only.</summary>
    private sealed class PublicNoticeKey(RSAParameters publicKey, string version) : IRecipientNoticeKeyWrapClient
    {
        public Task<string> GetCurrentKeyVersionAsync(CancellationToken cancellationToken) => Task.FromResult(version);

        public Task<byte[]> WrapKeyAsync(string keyVersion, byte[] dataKey, CancellationToken cancellationToken)
        {
            using var rsa = RSA.Create(publicKey);
            return Task.FromResult(rsa.Encrypt(dataKey, RSAEncryptionPadding.OaepSHA256));
        }
    }

    /// <summary>The Worker side (AI-08 NTF-005), standing in for Key Vault unwrapKey with RSA-OAEP-256.</summary>
    private sealed class PrivateNoticeKey(RSA rsa, string version) : IPiiKeyWrapClient
    {
        public Task<string> GetCurrentKeyVersionAsync(CancellationToken cancellationToken) => Task.FromResult(version);

        public Task<byte[]> WrapKeyAsync(string keyVersion, byte[] dataKey, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<byte[]> UnwrapKeyAsync(string keyVersion, byte[] wrappedKey, CancellationToken cancellationToken) =>
            Task.FromResult(rsa.Decrypt(wrappedKey, RSAEncryptionPadding.OaepSHA256));
    }
}
