using Iron.Api.Permissions;
using Iron.Api.Responses;
using Iron.Api.Routing;
using Iron.Files.Contracts;
using Iron.Files.Data;
using Iron.Files.Services;
using Iron.Kernel;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Iron.Files.Api;

public sealed record FileResponse(Guid Id, string Name, string ContentType, long Size, string Sha256, string? RecordType, Guid? RecordId, Guid CreatedBy, DateTimeOffset CreatedAt)
{
    public static FileResponse From(StoredFile file)
    {
        ArgumentNullException.ThrowIfNull(file);
        return new FileResponse(file.Id, file.Name, file.ContentType, file.Size, file.Sha256, file.RecordType, file.RecordId, file.CreatedBy, file.CreatedAt);
    }
}

/// <summary>
/// <c>POST /api/v1/files</c> (multipart, field <c>file</c>, optional <c>record_type</c> and <c>record_id</c>),
/// <c>GET /api/v1/files/{id}</c>, <c>GET /api/v1/files/{id}/content</c>, <c>DELETE /api/v1/files/{id}</c>.
/// Record-level checks (TF-160 AC2) arrive with record policies (TF-035); S1 checks the file permissions.
/// </summary>
internal static class FilesEndpoints
{
    public const string ChecksumHeader = "X-Checksum-SHA256";

    public static void Map(IEndpointRouteBuilder endpoints)
    {
        var files = endpoints.MapApiGroup("files");
        files.MapPost("/", UploadAsync).RequirePermission(FilesPermissions.FilesUpload).WithName("UploadFile").WithSummary("Upload a file (multipart form, field \"file\").");
        files.MapGet("/{id:guid}", GetAsync).RequirePermission(FilesPermissions.FilesRead).WithName("GetFile").WithSummary("The file's details.");
        files.MapGet("/{id:guid}/content", DownloadAsync).RequirePermission(FilesPermissions.FilesRead).WithName("DownloadFile").WithSummary("The file's content, verified against its stored hash.");
        files.MapDelete("/{id:guid}", DeleteAsync).RequirePermission(FilesPermissions.FilesDelete).WithName("DeleteFile").WithSummary("Delete a file (kept for retention).");
    }

    private static async Task<IResult> UploadAsync(HttpContext http, FileService service, CancellationToken cancellationToken)
    {
        if (!http.Request.HasFormContentType)
        {
            return ApiResults.Problem(Error.Validation("files.upload.not_multipart", "Send the file as multipart/form-data in the field \"file\"."), http);
        }

        var form = await http.Request.ReadFormAsync(cancellationToken);
        var upload = form.Files.GetFile("file");
        if (upload is null)
        {
            return ApiResults.Problem(Error.Validation("files.upload.no_file", "The form has no field \"file\".", new FieldError("file", "required", "A file is required.")), http);
        }

        var recordType = form["record_type"].ToString();
        Guid? recordId = Guid.TryParse(form["record_id"].ToString(), out var parsedRecordId) ? parsedRecordId : null;
        await using var content = upload.OpenReadStream();
        var result = await service.UploadAsync(upload.FileName, upload.ContentType, upload.Length, content, string.IsNullOrWhiteSpace(recordType) ? null : recordType, recordId, cancellationToken);
        return ApiResults.From(result, http, file => TypedResults.Created($"{ApiRouting.Prefix}/files/{file.Id}", FileResponse.From(file)));
    }

    private static async Task<IResult> GetAsync(Guid id, HttpContext http, FileService service, CancellationToken cancellationToken)
    {
        var file = await service.FindAsync(id, cancellationToken);
        return file is null ? ApiResults.Problem(Error.NotFound(FilesErrors.FileNotFound, "There is no such file."), http) : TypedResults.Ok(FileResponse.From(file));
    }

    private static async Task<IResult> DownloadAsync(Guid id, HttpContext http, FileService service, ICurrentPrincipal principal, CancellationToken cancellationToken)
    {
        var opened = await service.OpenAsync(id, cancellationToken);
        if (opened.IsFailure)
        {
            return ApiResults.Problem(opened.Error, http);
        }

        var download = opened.Value;
        http.Response.Headers[ChecksumHeader] = download.File.Sha256;
        http.Response.Headers.ContentLength = download.File.Size;
        return TypedResults.Stream(
            async body =>
            {
                await using (download.Content)
                {
                    await download.Content.CopyToAsync(body, cancellationToken);
                }

                // The hash is only known once everything was read. A mismatch is logged and audited, and the response is cut short.
                if (!await service.CompleteDownloadAsync(download, principal.Id, CancellationToken.None))
                {
                    http.Abort();
                }
            },
            download.File.ContentType,
            download.File.Name);
    }

    private static async Task<IResult> DeleteAsync(Guid id, HttpContext http, FileService service, CancellationToken cancellationToken)
    {
        var result = await service.DeleteAsync(id, cancellationToken);
        return ApiResults.From(result, http, () => TypedResults.NoContent());
    }
}
