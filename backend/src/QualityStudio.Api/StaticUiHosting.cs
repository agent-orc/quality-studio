using System.Text.RegularExpressions;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.FileProviders;
using Microsoft.Net.Http.Headers;

namespace QualityStudio.Api;

/// <summary>
/// Serves the built browser bundle from the API host so one container answers UI and API on one port.
/// The development server keeps its own proxy; nothing here participates in it.
/// </summary>
public static partial class StaticUiHosting
{
    /// <summary>Configuration key for an explicit bundle directory; relative paths resolve against the content root.</summary>
    public const string RootPathKey = "QualityStudio:Ui:RootPath";

    /// <summary>Directory the container image copies the browser bundle into.</summary>
    public const string DefaultDirectoryName = "wwwroot";

    private const string IndexFile = "index.html";
    private static readonly TimeSpan FingerprintedMaxAge = TimeSpan.FromDays(365);
    private static readonly TimeSpan PlainMaxAge = TimeSpan.FromHours(1);

    /// <summary>
    /// The bundle directory, or null when this host ships without a UI. A directory only counts as a
    /// bundle when it carries <c>index.html</c>; an empty <c>wwwroot</c> is treated as "no UI".
    /// </summary>
    public static string? ResolveRoot(IConfiguration configuration, IHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(environment);
        var configured = configuration[RootPathKey];
        var candidate = string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(environment.ContentRootPath, DefaultDirectoryName)
            : Path.IsPathRooted(configured)
                ? configured
                : Path.Combine(environment.ContentRootPath, configured);
        var root = Path.GetFullPath(candidate);
        return File.Exists(Path.Combine(root, IndexFile)) ? root : null;
    }

    /// <summary>
    /// Adds the bundle middleware: fingerprinted assets are served immutable, <c>index.html</c> is
    /// revalidated, and browser navigations that match no file fall back to <c>index.html</c> so the
    /// client router owns deep links. Requests below <c>/api</c> are never rewritten, so an unknown
    /// API route stays a 404 instead of turning into an HTML page.
    /// </summary>
    public static WebApplication UseStaticUi(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(StaticUiHosting).FullName!);
        var root = ResolveRoot(app.Configuration, app.Environment);
        if (root is null)
        {
            logger.LogInformation(new EventId(1600, "StaticUiAbsent"),
                "No browser bundle found; the API serves {ApiOnly} only", "/api");
            return app;
        }

        var provider = new PhysicalFileProvider(root);
        app.Lifetime.ApplicationStopped.Register(provider.Dispose);
        var contentTypes = new FileExtensionContentTypeProvider();
        contentTypes.Mappings[".webmanifest"] = "application/manifest+json";
        app.Use(async (context, next) =>
        {
            if (IsClientRouteNavigation(context, provider)) context.Request.Path = "/" + IndexFile;
            await next();
        });
        app.UseStaticFiles(new StaticFileOptions
        {
            FileProvider = provider,
            ContentTypeProvider = contentTypes,
            OnPrepareResponse = ApplyCacheHeaders,
        });
        logger.LogInformation(new EventId(1601, "StaticUiMounted"),
            "Serving the browser bundle from {BundleRoot}", root);
        return app;
    }

    private static bool IsClientRouteNavigation(HttpContext context, IFileProvider provider)
    {
        if (!HttpMethods.IsGet(context.Request.Method) && !HttpMethods.IsHead(context.Request.Method)) return false;
        if (context.Request.Path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase)) return false;
        if (!AcceptsHtml(context.Request)) return false;
        var path = context.Request.Path.Value;
        if (string.IsNullOrEmpty(path) || path == "/") return true;
        return !provider.GetFileInfo(path).Exists;
    }

    private static bool AcceptsHtml(HttpRequest request)
    {
        var accept = request.Headers.Accept;
        if (accept.Count == 0) return true;
        foreach (var value in accept)
        {
            if (value is null) continue;
            if (value.Contains("text/html", StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    private static void ApplyCacheHeaders(StaticFileResponseContext context)
    {
        var headers = context.Context.Response.GetTypedHeaders();
        if (string.Equals(context.File.Name, IndexFile, StringComparison.OrdinalIgnoreCase))
        {
            // The shell names the fingerprinted assets, so it must never be served from a stale cache.
            headers.CacheControl = new CacheControlHeaderValue { NoCache = true, MustRevalidate = true };
            return;
        }

        if (FingerprintedAsset().IsMatch(context.File.Name))
        {
            var immutable = new CacheControlHeaderValue { Public = true, MaxAge = FingerprintedMaxAge };
            immutable.Extensions.Add(new NameValueHeaderValue("immutable"));
            headers.CacheControl = immutable;
            return;
        }

        headers.CacheControl = new CacheControlHeaderValue { Public = true, MaxAge = PlainMaxAge };
    }

    // The Angular builder appends an uppercase base36 content hash to every emitted asset.
    [GeneratedRegex(@"-[A-Z0-9]{8,}\.[A-Za-z0-9]+$", RegexOptions.CultureInvariant)]
    private static partial Regex FingerprintedAsset();
}
