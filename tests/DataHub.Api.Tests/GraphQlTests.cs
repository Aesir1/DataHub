using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace DataHub.Api.Tests;

/// <summary>GraphQL end to end in process: auth, policies, Products CRUD with typed errors, outbox → consumer → audit, queues, documents.</summary>
public sealed class GraphQlTests(ApiFactory api)
{
    private const string Create = """
        mutation($name: String!, $sku: String!, $price: Decimal!) {
          createProduct(input: { name: $name, sku: $sku, price: $price }) {
            product { id name sku price rowVersion createdBy }
            errors { __typename ... on ValidationError { message fields { field messages } } ... on ConflictError { message code } }
          }
        }
        """;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Sku() => $"T-{Random.Shared.Next(100_000, 999_999)}";

    [Fact]
    public async Task Anonymous_request_is_401()
    {
        using var client = api.CreateClient();
        using var response = await client.PostAsJsonAsync("/graphql", new { query = "{ viewer { email } }" }, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Token_for_another_audience_is_401()
    {
        using var client = api.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", ApiFactory.Token("x@local.test", ["user"], audience: "webhook"));
        using var response = await client.PostAsJsonAsync("/graphql", new { query = "{ viewer { email } }" }, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Viewer_is_provisioned_with_role_permissions()
    {
        using var client = api.ClientFor("viewer@local.test", "user");

        var viewer = (await client.Gql("{ viewer { id email roles permissions } }")).Data()["viewer"]!;

        viewer["email"]!.GetValue<string>().ShouldBe("viewer@local.test");
        Guid.Parse(viewer["id"]!.GetValue<string>()).Version.ShouldBe(7);
        viewer["permissions"]!.AsArray().Select(p => p!.GetValue<string>()).ShouldContain("products.write");
        viewer["permissions"]!.AsArray().Select(p => p!.GetValue<string>()).ShouldNotContain("queues.manage");
    }

    [Fact]
    public async Task Product_crud_with_typed_errors_and_audit_through_rabbitmq()
    {
        using var client = api.ClientFor("crud@local.test", "user");
        var sku = Sku();

        // Create
        var created = (await client.Gql(Create, new { name = "Reefer", sku, price = 10.5m })).Data()["createProduct"]!;
        created["errors"].Json().ShouldBe("null", created.Json());
        var product = created["product"]!;
        var id = product["id"]!.GetValue<string>();
        product["createdBy"]!.GetValue<string>().ShouldBe("crud@local.test");

        // Conflict + validation are typed errors, not exceptions
        var duplicate = (await client.Gql(Create, new { name = "Reefer", sku, price = 1m })).Data()["createProduct"]!;
        duplicate["errors"]![0]!["__typename"]!.GetValue<string>().ShouldBe("ConflictError");
        var invalid = (await client.Gql(Create, new { name = string.Empty, sku = "bad sku", price = -1m })).Data()["createProduct"]!;
        invalid["errors"]![0]!["__typename"]!.GetValue<string>().ShouldBe("ValidationError");
        invalid["errors"]![0]!["fields"]!.AsArray().Select(f => f!["field"]!.GetValue<string>()).ShouldBe(["Name", "Sku", "Price"], ignoreOrder: true);

        // Read
        var read = (await client.Gql("query($id: UUID!) { product(id: $id) { name price } }", new { id })).Data()["product"]!;
        read["price"]!.GetValue<decimal>().ShouldBe(10.5m);

        // Update, then a stale update gets ConcurrencyError
        const string update = """
            mutation($id: UUID!, $v: UnsignedInt!) {
              updateProduct(input: { id: $id, name: "Reefer XL", price: 12, isArchived: false, rowVersion: $v }) {
                product { name rowVersion updatedBy }
                errors { __typename }
              }
            }
            """;
        var version = product["rowVersion"]!.GetValue<uint>();
        var updated = (await client.Gql(update, new { id, v = version })).Data()["updateProduct"]!;
        updated["product"]!["name"]!.GetValue<string>().ShouldBe("Reefer XL", updated.Json());
        var stale = (await client.Gql(update, new { id, v = version })).Data()["updateProduct"]!;
        stale["errors"]![0]!["__typename"]!.GetValue<string>().ShouldBe("ConcurrencyError");

        // Delete, then NotFound
        const string delete = "mutation($id: UUID!) { deleteProduct(input: { id: $id }) { uuid errors { __typename } } }";
        (await client.Gql(delete, new { id })).Data()["deleteProduct"]!["errors"].Json().ShouldBe("null");
        (await client.Gql(delete, new { id })).Data()["deleteProduct"]!["errors"]![0]!["__typename"]!.GetValue<string>().ShouldBe("NotFoundError");

        // Outbox → domain-events → ProductAuditConsumer → productAudit query (with DataLoader-resolved product)
        JsonArray? audit = null;
        for (var i = 0; i < 100 && audit is not { Count: 3 }; i++)
        {
            await Task.Delay(200, Ct);
            audit = (await client.Gql("query($id: UUID!) { productAudit(productId: $id) { nodes { change name product { sku } } } }", new { id })).Data()["productAudit"]!["nodes"]!.AsArray();
        }

        audit.ShouldNotBeNull();
        audit.Select(a => a!["change"]!.GetValue<string>()).ShouldBe(["DELETED", "UPDATED", "CREATED"]);
        audit[2]!["product"]!["sku"]!.GetValue<string>().ShouldBe(sku);
    }

    [Fact]
    public async Task Archived_products_are_hidden_by_the_repository_override()
    {
        using var client = api.ClientFor("archive@local.test", "user");
        var sku = Sku();
        var product = (await client.Gql(Create, new { name = "Old", sku, price = 1m })).Data()["createProduct"]!["product"]!;
        await client.Gql(
            "mutation($id: UUID!, $v: UnsignedInt!) { updateProduct(input: { id: $id, name: \"Old\", price: 1, isArchived: true, rowVersion: $v }) { errors { __typename } } }",
            new { id = product["id"]!.GetValue<string>(), v = product["rowVersion"]!.GetValue<uint>() });

        var found = (await client.Gql("query($sku: String!) { products(where: { sku: { eq: $sku } }) { totalCount } }", new { sku })).Data()["products"]!;

        found["totalCount"]!.GetValue<int>().ShouldBe(0);
    }

    [Fact]
    public async Task Queue_admin_requires_platform_admin()
    {
        using var user = api.ClientFor("plain@local.test", "user");
        var denied = await user.Gql("{ queues { name } }");
        denied["errors"]![0]!["extensions"]!["code"]!.GetValue<string>().ShouldBe("AUTH_NOT_AUTHORIZED");

        using var admin = api.ClientFor("admin@local.test", "user", "platform-admin");
        JsonArray? queues = null;
        for (var i = 0; i < 30 && queues?.Any(q => q!["name"]!.GetValue<string>() == "product-audit") != true; i++)
        {
            await Task.Delay(500, Ct); // management API stats lag a few seconds behind declarations
            queues = (await admin.Gql("{ queues { name messages consumers } }"))["data"]?["queues"]?.AsArray();
        }

        queues!.Select(q => q!["name"]!.GetValue<string>()).ShouldContain("product-audit");
        var purged = await admin.Gql("mutation { purgeQueue(input: { name: \"product-audit.dlq\" }) { unsignedInt } }");
        purged["errors"].Json().ShouldBe("null", purged.Json());
    }

    [Fact]
    public async Task Document_content_goes_through_the_api_and_only_to_its_owner()
    {
        using var owner = api.ClientFor("docs@local.test", "user");
        var upload = (await owner.Gql("""
            mutation { requestDocumentUpload(input: { fileName: "a.txt", contentType: "text/plain", sizeBytes: 5 }) {
              documentUploadPayload { uploadUrl document { id status downloadUrl } } errors { __typename } } }
            """)).Data()["requestDocumentUpload"]!;
        var payload = upload["documentUploadPayload"]!;
        var id = payload["document"]!["id"]!.GetValue<string>();
        payload["uploadUrl"]!.GetValue<string>().ShouldBe($"/api/documents/{id}/content");
        payload["document"]!["status"]!.GetValue<string>().ShouldBe("PENDING");
        payload["document"]!["downloadUrl"].Json().ShouldBe("null");

        // The web BFF strips /api and forwards to the Api with the user's token.
        var content = $"/documents/{id}/content";
        using var other = api.ClientFor("other@local.test", "user");
        using var anonymous = api.CreateClient();
        (await anonymous.PutAsync(content, Text("hello"), Ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await other.PutAsync(content, Text("hello"), Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await owner.PutAsync(content, Text("hello", "image/png"), Ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await owner.PutAsync(content, Text("hello"), Ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        var confirmed = (await owner.Gql($$"""mutation { confirmDocumentUpload(input: { id: "{{id}}" }) { document { downloadUrl } } }"""))
            .Data()["confirmDocumentUpload"]!["document"]!;
        confirmed["downloadUrl"]!.GetValue<string>().ShouldBe($"/api/documents/{id}/content");

        using var download = await owner.GetAsync(content, Ct);
        (await download.Content.ReadAsStringAsync(Ct)).ShouldBe("hello");
        download.Content.Headers.ContentType!.MediaType.ShouldBe("text/plain");
        download.Content.Headers.ContentDisposition!.FileName.ShouldBe("a.txt");
        (await other.GetAsync(content, Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await anonymous.GetAsync(content, Ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        (await other.Gql("{ documents { totalCount } }")).Data()["documents"]!["totalCount"]!.GetValue<int>().ShouldBe(0);
        (await owner.Gql("{ documents { totalCount } }")).Data()["documents"]!["totalCount"]!.GetValue<int>().ShouldBe(1);
    }

    [Fact]
    public async Task Branding_is_served_anonymously_but_only_from_the_branding_bucket()
    {
        var storage = api.Services.GetRequiredService<Application.Abstractions.IObjectStorage>();
        await storage.PutAsync("branding", "logo.svg", new MemoryStream("<svg/>"u8.ToArray()), "image/svg+xml", Ct);
        using var anonymous = api.CreateClient();

        using var logo = await anonymous.GetAsync("/branding/logo.svg", Ct);
        (await logo.Content.ReadAsStringAsync(Ct)).ShouldBe("<svg/>");
        logo.Content.Headers.ContentType!.MediaType.ShouldBe("image/svg+xml");
        (await anonymous.GetAsync("/branding/missing.png", Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await anonymous.GetAsync("/branding/../documents/x", Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    private static StringContent Text(string body, string contentType = "text/plain") => new(body, System.Text.Encoding.UTF8, contentType);

    [Fact]
    public async Task Temperatures_resolve_their_container_through_a_dataloader()
    {
        using var client = api.ClientFor("temps@local.test", "user");
        await using (var db = new Infrastructure.Persistence.AppDbContext(new Microsoft.EntityFrameworkCore.DbContextOptionsBuilder<Infrastructure.Persistence.AppDbContext>()
            .UseNpgsql(api.AppConnectionString).Options))
        {
            var container = new Domain.Containers.Container { Code = $"DL{Random.Shared.Next(100000, 999999)}" };
            container.Temperatures.Add(new Domain.Containers.Temperature { TimestampUtc = DateTimeOffset.UtcNow, Celsius = 4m });
            db.Containers.Add(container);
            await db.SaveChangesAsync(Ct);

            var nodes = (await client.Gql("query($id: UUID!) { temperatures(containerId: $id) { nodes { celsius container { code } } } }", new { id = container.Id })).Data()["temperatures"]!["nodes"]!;
            nodes[0]!["container"]!["code"]!.GetValue<string>().ShouldBe(container.Code);
        }
    }
}
