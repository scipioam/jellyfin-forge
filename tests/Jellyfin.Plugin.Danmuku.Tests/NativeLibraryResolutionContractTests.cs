using System.Reflection;
using System.Runtime.InteropServices;
using Jellyfin.Plugin.Danmuku.Storage;
using Xunit;

namespace Jellyfin.Plugin.Danmuku.Tests;

public sealed class NativeLibraryResolutionContractTests
{
    [Fact]
    public void ResolverLoadsBundledAssetFromPluginDirectory()
    {
        SqliteNativeLibraryResolver.EnsureRegistered();
        SqliteNativeLibraryResolver.EnsureRegistered();
        Assert.True(SqliteNativeLibraryResolver.IsRegistered, SqliteNativeLibraryResolver.RegistrationError);

        var providerAssembly = typeof(SQLitePCL.SQLite3Provider_e_sqlite3).Assembly;
        var handle = SqliteNativeLibraryResolver.Resolve("e_sqlite3", providerAssembly, null);
        Assert.NotEqual(IntPtr.Zero, handle);

        var resolution = SqliteNativeLibraryResolver.LastResolution;
        Assert.NotNull(resolution);
        Assert.Equal(SqliteNativeResolutionState.Resolved, resolution.State);

        var pluginDirectory = Path.GetDirectoryName(typeof(Plugin).Assembly.Location);
        Assert.False(string.IsNullOrEmpty(pluginDirectory));
        var expectedRelativePath = Path.Combine("runtimes", GetCurrentRuntimeIdentifier(), "native", GetCurrentNativeFileName());

        Assert.NotNull(resolution.Path);
        Assert.StartsWith(pluginDirectory!, resolution.Path!, StringComparison.Ordinal);
        Assert.Contains(expectedRelativePath, resolution.Path!, StringComparison.Ordinal);
        Assert.True(File.Exists(resolution.Path!));
    }

    [Fact]
    public void BundledAssetCanBeLoadedByTheNativeLoader()
    {
        SqliteNativeLibraryResolver.EnsureRegistered();

        Assert.True(SqliteNativeLibraryResolver.TryGetBundledAssetPath("e_sqlite3", out var assetPath));
        Assert.True(File.Exists(assetPath));
        Assert.True(NativeLibrary.TryLoad(assetPath, out var handle));
        Assert.NotEqual(IntPtr.Zero, handle);

        // The startup probe returns the same asset path as the resolver.
        Assert.Equal(assetPath, SqliteNativeLibraryResolver.EnsureBundledAssetLoaded());
    }

    [Fact]
    public void ResolverIsAttachedToTheProviderAssemblyAndUnknownLibrariesFallThrough()
    {
        SqliteNativeLibraryResolver.EnsureRegistered();
        Assert.True(SqliteNativeLibraryResolver.IsRegistered, SqliteNativeLibraryResolver.RegistrationError);
        Assert.Null(SqliteNativeLibraryResolver.RegistrationError);

        var providerAssembly = typeof(SQLitePCL.SQLite3Provider_e_sqlite3).Assembly;
        Assert.Equal("SQLitePCLRaw.provider.e_sqlite3", providerAssembly.GetName().Name);

        // The provider declares DllImport("e_sqlite3"). Calling the resolver with that
        // assembly returns the plugin-bundled asset even when the process already has a
        // same-named library loaded (NativeLibrary.TryLoad can serve an already loaded
        // library from its cache without consulting the resolver, so the resolver is
        // asserted directly here and through its recorded resolution).
        var handle = SqliteNativeLibraryResolver.Resolve("e_sqlite3", providerAssembly, null);
        Assert.NotEqual(IntPtr.Zero, handle);
        Assert.Equal(SqliteNativeResolutionState.Resolved, SqliteNativeLibraryResolver.LastResolution?.State);

        // Non-SQLite names still fall through to the default resolution logic.
        Assert.Equal(IntPtr.Zero, SqliteNativeLibraryResolver.Resolve("definitely-not-a-sqlite-library", providerAssembly, null));
    }

    [Fact]
    public void ProviderInteropUsesTheBundledAssetWhenTheProcessLoadsSqlite()
    {
        SqliteNativeLibraryResolver.EnsureRegistered();
        Assert.True(SqliteNativeLibraryResolver.TryGetBundledAssetPath("e_sqlite3", out var expectedPath));

        // Exercises the real SQLitePCLRaw provider P/Invoke path.
        using var connection = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=:memory:");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "select sqlite_version();";
        Assert.NotNull(command.ExecuteScalar());

        var resolution = SqliteNativeLibraryResolver.LastResolution;
        Assert.NotNull(resolution);
        Assert.Equal(SqliteNativeResolutionState.Resolved, resolution.State);
        Assert.Equal(expectedPath, resolution.Path);
    }

    [Fact]
    public void CorruptBundledAssetFailsProbeAndConnectionOpenWithoutFallback()
    {
        var directory = CreateTempDirectory();
        try
        {
            var assetPath = WriteAsset(directory, "not a shared library");

            var probeException = Assert.Throws<SqliteNativeLibraryUnavailableException>(
                () => SqliteNativeLibraryResolver.LoadBundledAsset(directory));
            Assert.Equal(SqliteNativeResolutionState.LoadFailed, probeException.State);
            Assert.Contains(assetPath, probeException.Message, StringComparison.Ordinal);
            Assert.Contains("LoadFailed", probeException.Message, StringComparison.Ordinal);

            var databasePath = Path.Combine(directory, "probe.db");
            var factory = new SqliteConnectionFactory(databasePath, new DirectoryNativeLibraryProbe(directory));
            var connectionException = Assert.Throws<SqliteNativeLibraryUnavailableException>(() => factory.CreateOpenConnection());
            Assert.Equal(SqliteNativeResolutionState.LoadFailed, connectionException.State);
            Assert.Contains(assetPath, connectionException.Message, StringComparison.Ordinal);
            Assert.False(File.Exists(databasePath));
        }
        finally
        {
            DeleteTempDirectory(directory);
        }
    }

    [Fact]
    public void MissingBundledAssetFailsProbeWithTheExpectedAbsolutePath()
    {
        var directory = CreateTempDirectory();
        try
        {
            var exception = Assert.Throws<SqliteNativeLibraryUnavailableException>(
                () => SqliteNativeLibraryResolver.LoadBundledAsset(directory));

            Assert.Equal(SqliteNativeResolutionState.AssetNotFound, exception.State);
            var expectedPath = Path.Combine(directory, "runtimes", GetCurrentRuntimeIdentifier(), "native", GetCurrentNativeFileName());
            Assert.Contains(expectedPath, exception.Message, StringComparison.Ordinal);
            Assert.Contains("AssetNotFound", exception.Message, StringComparison.Ordinal);
            Assert.False(File.Exists(expectedPath));

            var factory = new SqliteConnectionFactory(Path.Combine(directory, "missing.db"), new DirectoryNativeLibraryProbe(directory));
            var connectionException = Assert.Throws<SqliteNativeLibraryUnavailableException>(() => factory.CreateOpenConnection());
            Assert.Contains(expectedPath, connectionException.Message, StringComparison.Ordinal);
        }
        finally
        {
            DeleteTempDirectory(directory);
        }
    }

    private static string WriteAsset(string pluginDirectory, string content)
    {
        var assetPath = Path.Combine(pluginDirectory, "runtimes", GetCurrentRuntimeIdentifier(), "native", GetCurrentNativeFileName());
        Directory.CreateDirectory(Path.GetDirectoryName(assetPath)!);
        File.WriteAllText(assetPath, content);
        return assetPath;
    }

    private static string CreateTempDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "danmuku-native-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static void DeleteTempDirectory(string directory)
    {
        try
        {
            Directory.Delete(directory, recursive: true);
        }
        catch (IOException)
        {
            // Best effort cleanup only.
        }
        catch (UnauthorizedAccessException)
        {
            // Best effort cleanup only.
        }
    }

    private static string GetCurrentRuntimeIdentifier()
    {
        var platform = OperatingSystem.IsWindows() ? "win" : OperatingSystem.IsMacOS() ? "osx" : "linux";
        var architecture = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => "x64",
            Architecture.X86 => "x86",
            Architecture.Arm64 => "arm64",
            Architecture.Arm => "arm",
            _ => RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant()
        };

        return $"{platform}-{architecture}";
    }

    private static string GetCurrentNativeFileName() =>
        OperatingSystem.IsWindows() ? "e_sqlite3.dll" : OperatingSystem.IsMacOS() ? "libe_sqlite3.dylib" : "libe_sqlite3.so";

    /// <summary>
    /// Probe backed by a real directory layout; used to exercise missing/corrupt assets
    /// without touching the packaged files.
    /// </summary>
    private sealed class DirectoryNativeLibraryProbe : ISqliteNativeLibraryProbe
    {
        private readonly string _pluginDirectory;

        public DirectoryNativeLibraryProbe(string pluginDirectory)
        {
            _pluginDirectory = pluginDirectory;
        }

        public string EnsureAvailable() => SqliteNativeLibraryResolver.LoadBundledAsset(_pluginDirectory);
    }
}
