using System.Text.Json;

namespace WolfLeash.Configuration;

public sealed class DenStorage(IConfiguration configuration)
{
    public string DirectoryPath { get; } = configuration["DataPath"] ??
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "wolf-den");

    public async Task WriteAsync<T>(string name, T value)
    {
        Directory.CreateDirectory(DirectoryPath);
        var destination = Path.Combine(DirectoryPath, name);
        var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            await using (var file = new FileStream(temporary, options))
            {
                await JsonSerializer.SerializeAsync(file, value, new JsonSerializerOptions { WriteIndented = true });
                await file.FlushAsync();
                file.Flush(flushToDisk: true);
            }
            File.Move(temporary, destination, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
