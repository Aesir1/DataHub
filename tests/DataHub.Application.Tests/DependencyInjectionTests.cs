using DataHub.Application.Documents;
using DataHub.Application.Ingestion;
using DataHub.Application.Products;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace DataHub.Application.Tests;

public class DependencyInjectionTests
{
    [Fact]
    public void Registers_use_case_services()
    {
        var services = new ServiceCollection().AddApplication(Substitute.For<IConfiguration>());

        services.ShouldContain(d => d.ServiceType == typeof(ProductService));
        services.ShouldContain(d => d.ServiceType == typeof(DocumentService));
        services.ShouldContain(d => d.ServiceType == typeof(IngestTemperaturesService));
        services.ShouldContain(d => d.ServiceType == typeof(TimeProvider));
    }
}
