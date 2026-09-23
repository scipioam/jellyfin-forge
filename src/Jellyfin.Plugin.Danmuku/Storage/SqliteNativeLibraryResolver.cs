using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Loader;

namespace Jellyfin.Plugin.Danmuku.Storage;

public enum SqliteNativeResolutionState
{
    /// <summary>The resolver was never invoked (for example because the provider was already initialized).</summary>
    NotAttempted,

    /// <summary>The plugin-bundled native asset was found and loaded.</summary>
    Resolved,

    /// <summary>No bundled asset exists for the current runtime identifier.</summary>
    AssetNotFound,

    /// <summary>A bundled asset exists but could not be loaded.</summary>
    LoadFailed,

    /// <summary>The resolver could not be attached to the SQLitePCLRaw provider assembly.</summary>
    ResolverUnavailable
}

public sealed record SqliteNativeResolution(
    string LibraryName,
    SqliteNativeResolutionState State,
    string? Path,
    string? Detail);

/// <summary>
/// Thrown when the plugin-bundled SQLite native library cannot be used. Storage must not
/// start in this state: falling back to a same-named library installed next to the
/// Jellyfin server would mean depending on an accidental Server dependency.
/// </summary>
public sealed class SqliteNativeLibraryUnavailableException : InvalidOperationException
{
    public SqliteNativeLibraryUnavailableException(
        string message,
        SqliteNativeResolutionState state,
        string libraryName,
        string? expectedPath,
        string? detail)
        : base(message)
    {
        State = state;
        LibraryName = libraryName;
        ExpectedPath = expectedPath;
        Detail = detail;
    }

    public SqliteNativeResolutionState State { get; }

    public string LibraryName { get; }

    public string? ExpectedPath { get; }

    public string? Detail { get; }
}

/// <summary>
/// Explicitly resolves the SQLite native library to the asset shipped inside the plugin
/// package instead of relying on a same-named library that happens to be installed next
/// to the Jellyfin server. Missing or unloadable assets are hard failures.
/// </summary>
/// <remarks>
/// The <c>DllImport("e_sqlite3")</c> declarations live in
/// <c>SQLitePCLRaw.provider.e_sqlite3</c>, so the resolver must be registered on that
/// assembly (not on the plugin assembly). Registration happens in a module initializer so
/// it runs when the plugin assembly is loaded, well before the provider is first used,
/// and is retried from <see cref="SqliteConnectionFactory"/> in case the provider assembly
/// was not resolvable that early.
/// Only linux-x64 and linux-arm64 assets are shipped; on other platforms the probe fails
/// with <see cref="SqliteNativeResolutionState.AssetNotFound"/> and storage stays disabled,
/// which is the intended hard-fail behavior rather than using the Server library.
/// </remarks>
public static class SqliteNativeLibraryResolver
{
    private const string ProviderLibraryName = "e_sqlite3";

    private static readonly object Sync = new();
    private static readonly Lazy<string?> ResolverAssetPath = new(
        ResolveViaAssemblyDependencyResolver,
        LazyThreadSafetyMode.ExecutionAndPublication);

    private static bool _registered;
    private static string? _registrationError;
    private static SqliteNativeResolution? _lastResolution;
    private static string? _resolvedPath;
    private static IntPtr _resolvedHandle;

#pragma warning disable CA2255 // A Jellyfin plugin assembly needs a load-time hook for its native resolver.
    [ModuleInitializer]
    internal static void Initialize() => EnsureRegistered();
#pragma warning restore CA2255

    /// <summary>Gets a value indicating whether the resolver is attached to the provider assembly.</summary>
    public static bool IsRegistered => _registered;

    /// <summary>Gets the registration failure detail, if any.</summary>
    public static string? RegistrationError => _registrationError;

    /// <summary>Gets the outcome of the most recent resolution attempt, if any.</summary>
    public static SqliteNativeResolution? LastResolution => _lastResolution;

    /// <summary>
    /// Attaches the resolver to the SQLitePCLRaw provider assembly. Safe to call multiple
    /// times; failures are recorded instead of thrown so plugin load is never affected.
    /// </summary>
    public static void EnsureRegistered()
    {
        if (_registered)
        {
            return;
        }

        lock (Sync)
        {
            if (_registered)
            {
                return;
            }

            try
            {
                var providerAssembly = typeof(SQLitePCL.SQLite3Provider_e_sqlite3).Assembly;
                NativeLibrary.SetDllImportResolver(providerAssembly, Resolve);
                _registered = true;
                _registrationError = null;
            }
            catch (Exception exception)
            {
                _registrationError = $"{exception.GetType().Name}: {exception.Message}";
            }
        }
    }

    /// <summary>
    /// Verifies and loads the plugin-bundled asset for the current process and caches it.
    /// Throws <see cref="SqliteNativeLibraryUnavailableException"/> when the asset is
    /// missing, cannot be loaded, or the resolver is not attached; the Server-provided
    /// library is never used as a fallback.
    /// </summary>
    public static string EnsureBundledAssetLoaded()
    {
        lock (Sync)
        {
            if (_resolvedPath is not null)
            {
                return _resolvedPath;
            }

            EnsureRegistered();
            var pluginDirectory = GetPluginDirectory();
            if (!_registered)
            {
                var resolution = new SqliteNativeResolution(
                    ProviderLibraryName,
                    SqliteNativeResolutionState.ResolverUnavailable,
                    EnumerateCandidatePaths(pluginDirectory, ProviderLibraryName).FirstOrDefault(),
                    _registrationError ?? "The SQLitePCLRaw provider assembly resolver could not be registered.");
                _lastResolution = resolution;
                throw CreateException(resolution, pluginDirectory);
            }

            var path = LoadBundledAssetCore(pluginDirectory, ProviderLibraryName, recordResolution: true, out var handle);
            _resolvedHandle = handle;
            _resolvedPath = path;
            return path;
        }
    }

    /// <summary>
    /// Verifies and loads the bundled asset from an explicit plugin directory without
    /// caching. Used by the probe seam and by tests to exercise real file layouts.
    /// </summary>
    public static string LoadBundledAsset(string pluginDirectory, string libraryName = ProviderLibraryName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginDirectory);
        lock (Sync)
        {
            // Explicit-directory probes do not participate in the process-wide
            // LastResolution diagnostics, which describe the real plugin directory.
            return LoadBundledAssetCore(pluginDirectory, libraryName, recordResolution: false, out _);
        }
    }

    /// <summary>
    /// DllImport resolver callback. Returns a handle for the bundled asset. For SQLite
    /// libraries it never returns <see cref="IntPtr.Zero"/>: an unavailable asset throws
    /// <see cref="SqliteNativeLibraryUnavailableException"/> so the process cannot silently
    /// fall back to the Server-provided library. Other library names fall through to the
    /// default resolution logic.
    /// </summary>
    public static IntPtr Resolve(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (!IsSqliteLibrary(libraryName))
        {
            return IntPtr.Zero;
        }

        lock (Sync)
        {
            EnsureBundledAssetLoaded();
            return _resolvedHandle;
        }
    }

    /// <summary>
    /// Gets the bundled asset path used for the current process. When no asset exists,
    /// <paramref name="assetPath"/> receives the primary expected location.
    /// </summary>
    public static bool TryGetBundledAssetPath(string libraryName, out string assetPath)
    {
        var candidates = EnumerateCandidatePaths(GetPluginDirectory(), libraryName).ToArray();
        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate))
            {
                assetPath = candidate;
                return true;
            }
        }

        assetPath = candidates.Length > 0 ? candidates[0] : string.Empty;
        return false;
    }

    private static string LoadBundledAssetCore(string pluginDirectory, string libraryName, bool recordResolution, out IntPtr handle)
    {
        handle = IntPtr.Zero;
        var candidates = EnumerateCandidatePaths(pluginDirectory, libraryName).ToArray();
        string? failedCandidate = null;
        string? failureDetail = null;

        foreach (var candidate in candidates)
        {
            if (!File.Exists(candidate))
            {
                continue;
            }

            if (NativeLibrary.TryLoad(candidate, out handle))
            {
                if (recordResolution)
                {
                    _lastResolution = new SqliteNativeResolution(
                        libraryName,
                        SqliteNativeResolutionState.Resolved,
                        candidate,
                        null);
                }

                return candidate;
            }

            failedCandidate = candidate;
            failureDetail = $"NativeLibrary.TryLoad failed for '{candidate}'.";
        }

        var resolution = failureDetail is not null
            ? new SqliteNativeResolution(
                libraryName,
                SqliteNativeResolutionState.LoadFailed,
                failedCandidate,
                failureDetail)
            : new SqliteNativeResolution(
                libraryName,
                SqliteNativeResolutionState.AssetNotFound,
                candidates.Length > 0 ? candidates[0] : null,
                "No bundled SQLite runtime asset exists for the current runtime identifier.");
        if (recordResolution)
        {
            _lastResolution = resolution;
        }

        throw CreateException(resolution, pluginDirectory);
    }

    private static SqliteNativeLibraryUnavailableException CreateException(SqliteNativeResolution resolution, string pluginDirectory)
    {
        var message = resolution.State switch
        {
            SqliteNativeResolutionState.LoadFailed => FormattableString.Invariant(
                $"Danmuku cannot use its own SQLite native library: the bundled asset '{resolution.Path}' exists but could not be loaded (state: LoadFailed). Detail: {resolution.Detail} The plugin package may be corrupt or built for an incompatible platform; the Server-provided SQLite library will not be used."),
            SqliteNativeResolutionState.ResolverUnavailable => FormattableString.Invariant(
                $"Danmuku cannot use its own SQLite native library: the DllImport resolver could not be attached to SQLitePCLRaw.provider.e_sqlite3 (state: ResolverUnavailable). Detail: {resolution.Detail} The Server-provided SQLite library will not be used."),
            _ => FormattableString.Invariant(
                $"Danmuku cannot use its own SQLite native library: no bundled asset for '{resolution.LibraryName}' was found (state: AssetNotFound). Expected asset: '{resolution.Path}'. Plugin directory: '{pluginDirectory}'. Reinstall the plugin package including its runtimes assets; the Server-provided SQLite library will not be used.")
        };

        return new SqliteNativeLibraryUnavailableException(
            message,
            resolution.State,
            resolution.LibraryName,
            resolution.Path,
            resolution.Detail);
    }

    private static bool IsSqliteLibrary(string libraryName)
    {
        var stem = Path.GetFileNameWithoutExtension(Path.GetFileName(libraryName));
        return string.Equals(stem, "e_sqlite3", StringComparison.Ordinal)
            || string.Equals(stem, "libe_sqlite3", StringComparison.Ordinal);
    }

    private static IEnumerable<string> EnumerateCandidatePaths(string pluginDirectory, string libraryName)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var fullDirectory = Path.GetFullPath(pluginDirectory);
        var directoryPrefix = fullDirectory.EndsWith(Path.DirectorySeparatorChar)
            ? fullDirectory
            : fullDirectory + Path.DirectorySeparatorChar;

        // Prefer the path resolved from the plugin's own deps.json, which is how the
        // packaged plugin maps e_sqlite3 to its bundled RID-specific asset. It only
        // applies when probing the real plugin directory.
        var resolverPath = ResolverAssetPath.Value;
        if (!string.IsNullOrEmpty(resolverPath)
            && resolverPath.StartsWith(directoryPrefix, StringComparison.Ordinal)
            && seen.Add(resolverPath))
        {
            yield return resolverPath;
        }

        var fileNames = GetCandidateFileNames(libraryName).ToArray();
        var runtimeIdentifiers = GetCandidateRuntimeIdentifiers().ToArray();

        foreach (var runtimeIdentifier in runtimeIdentifiers)
        {
            foreach (var fileName in fileNames)
            {
                var candidate = Path.Combine(fullDirectory, "runtimes", runtimeIdentifier, "native", fileName);
                if (seen.Add(candidate))
                {
                    yield return candidate;
                }
            }
        }
    }

    private static string? ResolveViaAssemblyDependencyResolver()
    {
        try
        {
            var location = typeof(Plugin).Assembly.Location;
            if (string.IsNullOrEmpty(location) || !File.Exists(location))
            {
                return null;
            }

            var resolver = new AssemblyDependencyResolver(location);
            return resolver.ResolveUnmanagedDllToPath(ProviderLibraryName)
                ?? resolver.ResolveUnmanagedDllToPath(GetPlatformFileName());
        }
        catch (Exception)
        {
            // Fall back to the RID-derived candidates below.
            return null;
        }
    }

    private static string GetPlatformFileName() =>
        OperatingSystem.IsWindows()
            ? "e_sqlite3.dll"
            : OperatingSystem.IsMacOS()
                ? "libe_sqlite3.dylib"
                : "libe_sqlite3.so";

    private static IEnumerable<string> GetCandidateFileNames(string libraryName)
    {
        var platformFileName = GetPlatformFileName();
        yield return platformFileName;

        var requestedFileName = Path.GetFileName(libraryName);
        if (!string.Equals(requestedFileName, platformFileName, StringComparison.Ordinal)
            && Path.HasExtension(requestedFileName))
        {
            yield return requestedFileName;
        }
    }

    private static IEnumerable<string> GetCandidateRuntimeIdentifiers()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var portable = GetPortableRuntimeIdentifier();
        if (portable is not null && seen.Add(portable))
        {
            yield return portable;
        }

        var reported = RuntimeInformation.RuntimeIdentifier;
        if (!string.IsNullOrWhiteSpace(reported) && seen.Add(reported))
        {
            yield return reported;
        }
    }

    private static string? GetPortableRuntimeIdentifier()
    {
        var platform = OperatingSystem.IsLinux()
            ? "linux"
            : OperatingSystem.IsWindows()
                ? "win"
                : OperatingSystem.IsMacOS()
                    ? "osx"
                    : null;
        if (platform is null)
        {
            return null;
        }

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

    private static string GetPluginDirectory()
    {
        var location = typeof(Plugin).Assembly.Location;
        if (string.IsNullOrEmpty(location))
        {
            return AppContext.BaseDirectory;
        }

        return Path.GetDirectoryName(location) ?? AppContext.BaseDirectory;
    }
}
