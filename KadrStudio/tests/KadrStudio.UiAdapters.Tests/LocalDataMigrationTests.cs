using KadrStudio.Services;

namespace KadrStudio.UiAdapters.Tests;

public sealed class LocalDataMigrationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "kadr-migration-" + Guid.NewGuid().ToString("N"));
    public LocalDataMigrationTests() => Directory.CreateDirectory(_root);
    public void Dispose() => Directory.Delete(_root, true);

    [Fact]
    public async Task Copies_unicode_data_and_preserves_originals()
    {
        var source = Path.Combine(_root, "Исходные данные");
        Directory.CreateDirectory(Path.Combine(source, "Settings"));
        Directory.CreateDirectory(Path.Combine(source, "empty"));
        File.WriteAllText(Path.Combine(source, "Settings", "настройки.json"), "{\"value\":42}");
        var destination = Path.Combine(_root, "installed");
        await LocalDataMigration.CopyAsync(source, destination);
        Assert.Equal(File.ReadAllText(Path.Combine(source, "Settings", "настройки.json")),
            File.ReadAllText(Path.Combine(destination, "Settings", "настройки.json")));
        Assert.True(Directory.Exists(Path.Combine(destination, "empty")));
    }

    [Theory]
    [InlineData("existing")]
    [InlineData("nested")]
    [InlineData("same")]
    public async Task Refuses_merging_or_overlapping_roots(string kind)
    {
        var source = Path.Combine(_root, "source");
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "keep"), "original");
        var destination = kind == "same" ? source : kind == "nested" ? Path.Combine(source, "child") : Path.Combine(_root, "existing");
        if (kind == "existing") Directory.CreateDirectory(destination);
        await Assert.ThrowsAsync<IOException>(() => LocalDataMigration.CopyAsync(source, destination));
        Assert.Equal("original", File.ReadAllText(Path.Combine(source, "keep")));
    }

    [Fact]
    public async Task Cancellation_leaves_source_and_does_not_publish_destination()
    {
        var source = Path.Combine(_root, "source");
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "keep"), "original");
        var destination = Path.Combine(_root, "destination");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => LocalDataMigration.CopyAsync(source, destination, new CancellationToken(true)));
        Assert.False(Directory.Exists(destination));
        Assert.Equal("original", File.ReadAllText(Path.Combine(source, "keep")));
    }

    [Fact]
    public async Task Locked_source_does_not_publish_partial_migration()
    {
        var source = Path.Combine(_root, "source");
        Directory.CreateDirectory(source);
        var path = Path.Combine(source, "keep");
        File.WriteAllText(path, "original");
        var destination = Path.Combine(_root, "destination");
        using (var locked = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            await Assert.ThrowsAnyAsync<IOException>(() => LocalDataMigration.CopyAsync(source, destination));
        Assert.False(Directory.Exists(destination));
        Assert.Equal("original", File.ReadAllText(path));
        await LocalDataMigration.CopyAsync(source, destination);
        Assert.Equal("original", File.ReadAllText(Path.Combine(destination, "keep")));
    }

    [Fact]
    public async Task Volume_roots_are_rejected_before_enumeration()
    {
        var destination = Path.Combine(_root, "unpublished");
        await Assert.ThrowsAsync<IOException>(() => LocalDataMigration.CopyAsync(Path.GetPathRoot(_root)!, destination));
        Assert.False(Directory.Exists(destination));
    }
}
