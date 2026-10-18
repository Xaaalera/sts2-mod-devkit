using Sts2.Devkit;
using Xunit;

namespace Sts2.Devkit.Tests;

public sealed class AtomicJsonFileTests
{
    [Fact]
    public async Task Publication_replaces_complete_json_without_exposing_the_temporary_file()
    {
        var directory = Path.Combine(Path.GetTempPath(), "sts2-publication-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        try
        {
            var destination = Path.Combine(directory, "response.json");
            await File.WriteAllTextAsync(destination, "{\"generation\":1}");
            await AtomicJsonFile.WriteAsync(destination, "{\"generation\":2}");
            Assert.Equal("{\"generation\":2}", await File.ReadAllTextAsync(destination));
            Assert.False(File.Exists(destination + ".tmp"));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    [Trait("Platform", "Windows")]
    public async Task Short_read_lock_delays_publication_without_losing_the_original_or_new_response()
    {
        var directory = Path.Combine(Path.GetTempPath(), "sts2-publication-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        try
        {
            var destination = Path.Combine(directory, "response.json");
            await File.WriteAllTextAsync(destination, "old");
            Task publication;
            using (var reader = new FileStream(destination, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                publication = AtomicJsonFile.WriteAsync(destination, "new");
                await Task.Delay(20);
                Assert.False(publication.IsCompleted);
                using var text = new StreamReader(reader, leaveOpen: true);
                Assert.Equal("old", await text.ReadToEndAsync());
            }
            await publication;
            Assert.Equal("new", await File.ReadAllTextAsync(destination));
        }
        finally { Directory.Delete(directory, true); }
    }
}
