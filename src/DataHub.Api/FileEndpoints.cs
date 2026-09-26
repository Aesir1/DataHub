using DataHub.Application.Abstractions;
using DataHub.Application.Documents;
using DataHub.Auth;
using DataHub.Domain.Documents;
using DataHub.Domain.Exceptions;
using DataHub.Infrastructure.Storage;
using Microsoft.AspNetCore.Mvc;

namespace DataHub.Api;

/// <summary>
/// The only way file bytes reach or leave S3 from a browser: MinIO has no public route. The web BFF forwards
/// /api/documents/... and /api/branding/... here, so the browser only ever talks to the web origin.
/// </summary>
public static class FileEndpoints
{
    public static IEndpointRouteBuilder MapFileEndpoints(this IEndpointRouteBuilder app)
    {
        var documents = app.MapGroup("/documents/{id:guid}/content");

        documents.MapPut(string.Empty, UploadAsync)
            .RequireAuthorization(Permissions.Documents.Write)
            .WithMetadata(new RequestSizeLimitAttribute(Document.MaxSizeBytes));

        documents.MapGet(string.Empty, DownloadAsync)
            .RequireAuthorization(Permissions.Documents.Read);

        // Logo and login background: public by design, but only the branding bucket and only reads.
        app.MapGet("/branding/{**key}", BrandingAsync).AllowAnonymous();
        return app;
    }

    private static async Task<IResult> UploadAsync(Guid id, HttpRequest request, DocumentService service, CancellationToken ct)
    {
        if (request.ContentLength is > Document.MaxSizeBytes)
        {
            return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
        }

        // S3 needs the length up front; the body is at most 25 MB (RequestSizeLimit).
        // ponytail: buffers in memory, switch to a temp file or multipart upload if the limit grows.
        using var buffer = new MemoryStream();
        await request.Body.CopyToAsync(buffer, ct);
        buffer.Position = 0;
        return await Handle(async () =>
        {
            await service.UploadContentAsync(id, buffer, request.GetTypedHeaders().ContentType?.MediaType.Value ?? string.Empty, ct);
            return Results.NoContent();
        });
    }

    private static Task<IResult> DownloadAsync(Guid id, DocumentService service, CancellationToken ct) =>
        Handle(async () =>
        {
            var (document, content) = await service.OpenContentAsync(id, ct);
            return Results.Stream(content.Content, document.ContentType, document.FileName);
        });

    private static async Task<IResult> BrandingAsync(string key, IObjectStorage storage, HttpResponse response, CancellationToken ct)
    {
        var content = await storage.GetAsync(BucketInitializer.BrandingBucket, key, ct);
        if (content is null)
        {
            return Results.NotFound();
        }

        response.Headers.CacheControl = "public, max-age=300";
        return Results.Stream(content.Content, content.ContentType);
    }

    private static async Task<IResult> Handle(Func<Task<IResult>> action)
    {
        try
        {
            return await action();
        }
        catch (NotFoundException)
        {
            return Results.NotFound();
        }
        catch (ForbiddenException)
        {
            return Results.Forbid();
        }
        catch (ConflictException ex)
        {
            return Results.Problem(ex.Message, statusCode: StatusCodes.Status409Conflict);
        }
        catch (Domain.Exceptions.ValidationException ex)
        {
            return Results.ValidationProblem(ex.Errors.ToDictionary(e => e.Key, e => e.Value));
        }
    }
}
