using Microsoft.Extensions.Options;

using Moonlight.Api.Common;

namespace Moonlight.Api.Infrastructure.Storage;

public interface IPhotoStorage
{
    // Stores the file under a sub-folder of the public uploads root (doctor photos) and returns its public path (e.g. /uploads/doctors/abc.jpg).
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

// Patient record attachments: kept outside the public uploads root and only ever read back through the authorized endpoints.
public interface IRecordFileStorage
{
    /// <summary>Stores the file and returns its key (a file name).</summary>
    Task<string> SaveAsync(string fileName, Stream content, CancellationToken ct);

    Stream? Open(string key);

    void Delete(string key);
}

public sealed class LocalDiskRecordFileStorage(IOptions<StorageOptions> options, IHostEnvironment environment) : IRecordFileStorage
{
    public static string Root(StorageOptions options, IHostEnvironment environment) =>
        Path.IsPathRooted(options.RecordsPath) ? options.RecordsPath : Path.Combine(environment.ContentRootPath, options.RecordsPath);

    public async Task<string> SaveAsync(string fileName, Stream content, CancellationToken ct)
    {
        var directory = Root(options.Value, environment);
        Directory.CreateDirectory(directory);
        var key = Path.GetFileName(fileName);
        await using var file = File.Create(Path.Combine(directory, key));
        await content.CopyToAsync(file, ct);
        return key;
    }

    public Stream? Open(string key)
    {
        var path = Path.Combine(Root(options.Value, environment), Path.GetFileName(key));
        return File.Exists(path) ? File.OpenRead(path) : null;
    }

    public void Delete(string key)
    {
        var path = Path.Combine(Root(options.Value, environment), Path.GetFileName(key));
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }
}
