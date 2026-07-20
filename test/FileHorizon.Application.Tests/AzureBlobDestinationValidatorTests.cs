using FileHorizon.Application.Configuration;

namespace FileHorizon.Application.Tests;

public class AzureBlobDestinationValidatorTests
{
    private readonly DestinationsOptionsValidator _validator = new();

    private static DestinationsOptions Options(Action<AzureBlobDestinationOptions>? configure = null)
    {
        var d = new AzureBlobDestinationOptions
        {
            Name = "archive",
            ContainerName = "incoming",
            BlobTechnical = new AzureBlobTechnicalOptions { AccountName = "myaccount" }
        };
        configure?.Invoke(d);
        return new DestinationsOptions { AzureBlob = [d] };
    }

    [Fact]
    public void Validate_ValidManagedIdentityConfig_Succeeds()
    {
        var result = _validator.Validate(null, Options());
        Assert.True(result.Succeeded);
    }

    [Fact]
    public void Validate_ValidConnectionStringConfig_Succeeds()
    {
        var result = _validator.Validate(null, Options(d =>
            d.BlobTechnical = new AzureBlobTechnicalOptions { ConnectionString = "UseDevelopmentStorage=true" }));
        Assert.True(result.Succeeded);
    }

    [Fact]
    public void Validate_MissingName_Fails()
    {
        var result = _validator.Validate(null, Options(d => d.Name = ""));
        Assert.False(result.Succeeded);
        Assert.Contains("Name must be specified", result.FailureMessage);
    }

    [Fact]
    public void Validate_MissingContainerName_Fails()
    {
        var result = _validator.Validate(null, Options(d => d.ContainerName = ""));
        Assert.False(result.Succeeded);
        Assert.Contains("ContainerName must be specified", result.FailureMessage);
    }

    [Theory]
    [InlineData("UPPER")]
    [InlineData("ab")]
    [InlineData("has_underscore")]
    [InlineData("double--hyphen")]
    [InlineData("-leadinghyphen")]
    public void Validate_InvalidContainerName_Fails(string containerName)
    {
        var result = _validator.Validate(null, Options(d => d.ContainerName = containerName));
        Assert.False(result.Succeeded);
        Assert.Contains("ContainerName", result.FailureMessage);
    }

    [Fact]
    public void Validate_InvalidAccessTier_Fails()
    {
        var result = _validator.Validate(null, Options(d => d.AccessTier = "Lukewarm"));
        Assert.False(result.Succeeded);
        Assert.Contains("AccessTier", result.FailureMessage);
    }

    [Theory]
    [InlineData("Hot")]
    [InlineData("cool")]
    [InlineData("Cold")]
    [InlineData("Archive")]
    public void Validate_ValidAccessTier_Succeeds(string tier)
    {
        var result = _validator.Validate(null, Options(d => d.AccessTier = tier));
        Assert.True(result.Succeeded);
    }

    [Fact]
    public void Validate_ProvidedStrategy_WithoutContentType_Fails()
    {
        var result = _validator.Validate(null, Options(d => d.ContentTypeStrategy = BlobContentTypeStrategy.Provided));
        Assert.False(result.Succeeded);
        Assert.Contains("ContentType must be specified", result.FailureMessage);
    }

    [Fact]
    public void Validate_NoAuthenticationMethod_Fails()
    {
        var result = _validator.Validate(null, Options(d => d.BlobTechnical = new AzureBlobTechnicalOptions()));
        Assert.False(result.Succeeded);
        Assert.Contains("ConnectionString", result.FailureMessage);
        Assert.Contains("AccountName", result.FailureMessage);
    }

    [Fact]
    public void Validate_InvalidServiceUri_Fails()
    {
        var result = _validator.Validate(null, Options(d =>
            d.BlobTechnical = new AzureBlobTechnicalOptions { ServiceUri = "not a uri" }));
        Assert.False(result.Succeeded);
        Assert.Contains("ServiceUri", result.FailureMessage);
    }

    [Fact]
    public void Validate_DuplicateName_AcrossKinds_Fails()
    {
        var options = Options();
        options.Local = [new LocalDestinationOptions { Name = "archive", RootPath = "C:/out" }];
        var result = _validator.Validate(null, options);
        Assert.False(result.Succeeded);
        Assert.Contains("duplicated", result.FailureMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Validate_NegativeMaxRetries_Fails()
    {
        var result = _validator.Validate(null, Options(d =>
            d.BlobTechnical = new AzureBlobTechnicalOptions { AccountName = "myaccount", MaxRetries = -1 }));
        Assert.False(result.Succeeded);
        Assert.Contains("MaxRetries", result.FailureMessage);
    }
}
