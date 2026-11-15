using Microsoft.Extensions.Options;

using Moonlight.Api.Common;

namespace Moonlight.Api.Infrastructure.Storage;

public interface IPhotoStorage
{
    // Stores the file under a sub-folder (doctor photos by default, "records" for patient attachments) and returns its public path (e.g. /uploads/doctors/abc.jpg).
    Task<string> SaveAsync(string fileName, Stream content, CancellationToken ct, string folder = "doctors");
}

// Local disk for development; an S3-compatible implementation replaces it in production (M6).
public sealed class LocalDiskPhotoStorage(IOptions<StorageOptions> options, IHostEnvironment environment) : IPhotoStorage
{
    public static string Root(StorageOptions options, IHostEnvironment environment) =>
        Path.IsPathRooted(options.Path) ? options.Path : Path.Combine(environment.ContentRootPath, options.Path);

    public async Task<string> SaveAsync(string fileName, Stream content, CancellationToken ct, string folder = "doctors")
    {
        var safeFolder = Path.GetFileName(folder);
        var directory = Path.Combine(Root(options.Value, environment), safeFolder);
        Directory.CreateDirectory(directory);
        await using var file = File.Create(Path.Combine(directory, Path.GetFileName(fileName)));
        await content.CopyToAsync(file, ct);
        return $"{options.Value.RequestPath.TrimEnd('/')}/{safeFolder}/{Path.GetFileName(fileName)}";
    }
}
