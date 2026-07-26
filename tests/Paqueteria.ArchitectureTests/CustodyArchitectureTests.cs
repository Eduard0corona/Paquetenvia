using Custody.Infrastructure.Persistence.Migrations;
using Custody.Domain;
using Paqueteria.ArchitectureTests.Architecture;

namespace Paqueteria.ArchitectureTests;

public sealed class CustodyArchitectureTests
{
    [Fact]
    public void Custody_has_exactly_the_four_canonical_layers()
    {
        Assert.Equal(
            ["Custody.Domain", "Custody.Application", "Custody.Infrastructure", "Custody.Endpoints"],
            SolutionCatalog.Custody.Components.Select(component => component.Name));
    }

    [Fact]
    public void S3_SDK_is_confined_to_infrastructure()
    {
        foreach (var component in new[]
                 {
                     SolutionCatalog.Custody.Domain,
                     SolutionCatalog.Custody.Application,
                     SolutionCatalog.Custody.Endpoints,
                 })
        {
            var metadata = ProjectMetadataReader.Read(component);
            Assert.DoesNotContain(metadata.PackageReferences, package =>
                package.Contains("AWSSDK", StringComparison.OrdinalIgnoreCase));
        }

        Assert.Contains(
            ProjectMetadataReader.Read(SolutionCatalog.Custody.Infrastructure).PackageReferences,
            package => package == "AWSSDK.S3");
    }

    [Fact]
    public void Domain_and_application_are_free_of_technical_storage_dependencies()
    {
        var forbidden = new[] { "Amazon", "Npgsql", "EntityFrameworkCore", "AspNetCore", "Minio" };
        var references = SolutionCatalog.Custody.Domain.Assembly.GetReferencedAssemblies()
            .Concat(SolutionCatalog.Custody.Application.Assembly.GetReferencedAssemblies())
            .Select(reference => reference.Name ?? string.Empty);
        Assert.DoesNotContain(references, reference =>
            forbidden.Any(value => reference.Contains(value, StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public void Endpoints_never_accept_file_bytes_or_publish_download_routes()
    {
        var source = File.ReadAllText(TestRepository.GetPath(
            "src/Modules/Custody/Custody.Endpoints/ProofEndpoints.cs"));
        Assert.DoesNotContain("multipart", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("IFormFile", source, StringComparison.Ordinal);
        Assert.DoesNotContain("MapGet", source, StringComparison.Ordinal);
        Assert.Contains("/proof-upload-sessions", source, StringComparison.Ordinal);
        Assert.Contains("/proofs", source, StringComparison.Ordinal);
        Assert.DoesNotContain("IProofDownloadService", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Adoption_migration_is_assertive_and_non_destructive()
    {
        var sql = AdoptCanonicalCustodyProofsBaseline.AdoptionSql;
        Assert.Contains("custody.proof_upload_sessions", sql, StringComparison.Ordinal);
        Assert.Contains("custody.proofs", sql, StringComparison.Ordinal);
        Assert.Contains("proofs_append_only", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("CREATE TABLE", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ALTER TABLE", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("DROP TABLE", sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Worker_transaction_uses_only_local_worker_role()
    {
        var source = File.ReadAllText(TestRepository.GetPath(
            "src/BuildingBlocks/Paqueteria.Infrastructure/Tenancy/WorkerTenantTransactionContext.cs"));
        Assert.Contains("SET LOCAL ROLE paqueteria_worker", source, StringComparison.Ordinal);
        Assert.DoesNotContain("SET ROLE paqueteria_worker", source, StringComparison.Ordinal);
        Assert.Contains("set_config('app.current_user_id'", source, StringComparison.Ordinal);
        Assert.Contains("set_config('app.current_org_ids'", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Custody_has_no_local_file_or_mutable_singleton_processing()
    {
        var root = TestRepository.GetPath("src/Modules/Custody");
        var source = string.Join(
            '\n',
            Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
                .Select(File.ReadAllText));
        Assert.DoesNotContain("FileStream", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Path.GetTempPath", source, StringComparison.Ordinal);
        Assert.DoesNotContain("IFormFile", source, StringComparison.Ordinal);
        Assert.DoesNotContain("multipart/form-data", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("{ObjectKey}", source, StringComparison.Ordinal);
        Assert.DoesNotContain("LogError(exception", source, StringComparison.Ordinal);
        Assert.DoesNotContain("static Dictionary<Guid", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Api_does_not_host_processor_and_worker_does_not_host_realtime_dispatchers()
    {
        var api = File.ReadAllText(TestRepository.GetPath("src/Paqueteria.Api/Program.cs"));
        var worker = File.ReadAllText(TestRepository.GetPath("src/Paqueteria.Worker/Program.cs"));
        Assert.DoesNotContain("ProofValidationWorker", api, StringComparison.Ordinal);
        Assert.DoesNotContain("IProofValidationProcessor", api, StringComparison.Ordinal);
        Assert.DoesNotContain("Realtime", worker, StringComparison.Ordinal);
    }

    [Fact]
    public void Session_vocabulary_remains_exactly_v06()
    {
        Assert.Equal(
            ["Created", "Uploaded", "Validating", "Ready", "Rejected", "Expired", "Consumed"],
            Enum.GetNames<ProofUploadSessionStatus>());
        Assert.Equal(
            ["PickupPhoto", "DeliveryPhoto", "Signature", "DeliveryCode", "ReturnPhoto"],
            Enum.GetNames<ProofType>());
    }
}
