using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using allstarr.Services.Admin;

namespace allstarr.Middleware;

/// <summary>
/// Middleware that only serves static files on the admin port (5275).
/// This keeps the admin UI isolated from the main proxy port.
/// </summary>
public class AdminStaticFilesMiddleware
{
    private const long MaxIndexBytes = 4 * 1024 * 1024;
    private readonly RequestDelegate _next;
    private readonly IWebHostEnvironment _env;
    private readonly string _adminBasePath;
    private const int AdminPort = 5275;
    private readonly string _webRootPath;
    private readonly string _webRootPathWithSeparator;
    private static readonly Regex BasePathMetaTag = new(
        @"<meta\b(?=[^>]*\bname\s*=\s*[""']allstarr-base-path[""'])[^>]*>",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex MetaContentAttribute = new(
        @"(?<prefix>\bcontent\s*=\s*)(?<quote>[""'])(?<value>.*?)\k<quote>",
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.Singleline);
    private static readonly Regex GeneratedAssetUrl = new(
        @"(?<quote>[""'])/(?<asset>_app/|favicon\.svg(?=[?#""']))",
        RegexOptions.Compiled);
    private static readonly Regex InlineScript = new(
        @"<script\b(?<attributes>[^>]*)>(?<body>.*?)</script\s*>",
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.Singleline);
    private static readonly Regex CspMetaTag = new(
        @"<meta\b(?=[^>]*\bhttp-equiv\s*=\s*[""']content-security-policy[""'])[^>]*>",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex CspContentAttribute = new(
        @"(?<prefix>\bcontent\s*=\s*)(?<quote>[""'])(?<value>.*?)\k<quote>",
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.Singleline);
    private static readonly Regex CspScriptDirective = new(
        @"(?<directive>\bscript-src\b)(?<spacing>\s+)(?<sources>[^;]*)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public AdminStaticFilesMiddleware(
        RequestDelegate next,
        IWebHostEnvironment env,
        AdminBasePath? basePath = null)
    {
        _next = next;
        _env = env;
        _adminBasePath = basePath?.Value ?? string.Empty;
        var webRoot = string.IsNullOrWhiteSpace(_env.WebRootPath)
            ? Path.Combine(_env.ContentRootPath, "wwwroot")
            : _env.WebRootPath;
        _webRootPath = Path.GetFullPath(webRoot);
        _webRootPathWithSeparator = _webRootPath.EndsWith(Path.DirectorySeparatorChar)
            ? _webRootPath
            : _webRootPath + Path.DirectorySeparatorChar;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var port = context.Connection.LocalPort;

        if (port == AdminPort)
        {
            var path = context.Request.Path.Value ?? "/";

            if (!HttpMethods.IsGet(context.Request.Method) && !HttpMethods.IsHead(context.Request.Method))
            {
                await _next(context);
                return;
            }

            if (path == "/" || path == "/index.html")
            {
                var indexPath = Path.Combine(_webRootPath, "index.html");
                if (File.Exists(indexPath))
                {
                    SetRevalidationHeaders(context.Response);
                    context.Response.ContentType = "text/html";
                    if (_adminBasePath.Length == 0)
                    {
                        await context.Response.SendFileAsync(indexPath);
                    }
                    else
                    {
                        var html = await ReadBoundedTextAsync(indexPath, context.RequestAborted);
                        if (html is null)
                        {
                            context.Response.StatusCode = StatusCodes.Status500InternalServerError;
                            return;
                        }

                        var transformed = TransformIndexHtml(html, _adminBasePath);
                        context.Response.ContentLength = Encoding.UTF8.GetByteCount(transformed);
                        if (HttpMethods.IsGet(context.Request.Method))
                            await context.Response.WriteAsync(transformed, Encoding.UTF8, context.RequestAborted);
                    }
                    return;
                }
            }

            // Canonicalize and enforce root boundary to block traversal attempts.
            var candidatePath = ResolveStaticFilePath(path);
            if (candidatePath == null)
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }

            if (File.Exists(candidatePath))
            {
                if (path.StartsWith("/_app/immutable/", StringComparison.Ordinal) &&
                    !string.Equals(Path.GetExtension(candidatePath), ".css", StringComparison.OrdinalIgnoreCase))
                {
                    context.Response.Headers.CacheControl = "public, max-age=31536000, immutable";
                }
                else
                {
                    SetRevalidationHeaders(context.Response);
                }
                var contentType = GetContentType(candidatePath);
                context.Response.ContentType = contentType;
                await context.Response.SendFileAsync(candidatePath);
                return;
            }
        }

        // Not admin port or file not found - continue pipeline
        await _next(context);
    }

    internal static string TransformIndexHtml(string html, string adminBasePath)
    {
        ArgumentNullException.ThrowIfNull(html);
        ArgumentException.ThrowIfNullOrEmpty(adminBasePath);

        var transformed = BasePathMetaTag.Replace(
            html,
            match => MetaContentAttribute.Replace(
                match.Value,
                content => $"{content.Groups["prefix"].Value}{content.Groups["quote"].Value}{adminBasePath}{content.Groups["quote"].Value}",
                1),
            1);
        transformed = GeneratedAssetUrl.Replace(
            transformed,
            match => $"{match.Groups["quote"].Value}{adminBasePath}/{match.Groups["asset"].Value}");

        return ReplaceChangedInlineScriptHashes(html, transformed);
    }

    private static async Task<string?> ReadBoundedTextAsync(string path, CancellationToken cancellationToken)
    {
        var length = new FileInfo(path).Length;
        if (length > MaxIndexBytes)
            return null;

        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 16 * 1024,
            options: FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var reader = new StreamReader(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), detectEncodingFromByteOrderMarks: true);
        var builder = new StringBuilder((int)Math.Min(length, MaxIndexBytes));
        var buffer = new char[16 * 1024];
        var characterLimit = (int)MaxIndexBytes;
        while (true)
        {
            var read = await reader.ReadAsync(buffer.AsMemory(), cancellationToken);
            if (read == 0)
                break;
            if (builder.Length > characterLimit - read)
                return null;
            builder.Append(buffer, 0, read);
        }

        return builder.ToString();
    }

    private static string ReplaceChangedInlineScriptHashes(string original, string transformed)
    {
        var originalScripts = InlineScript.Matches(original)
            .Cast<Match>()
            .Where(match => !HasScriptSource(match.Groups["attributes"].Value))
            .Select(match => match.Groups["body"].Value)
            .ToArray();
        var transformedScripts = InlineScript.Matches(transformed)
            .Cast<Match>()
            .Where(match => !HasScriptSource(match.Groups["attributes"].Value))
            .Select(match => match.Groups["body"].Value)
            .ToArray();

        var changed = new List<(string OldHash, string NewHash)>();
        for (var index = 0; index < Math.Min(originalScripts.Length, transformedScripts.Length); index++)
        {
            if (string.Equals(originalScripts[index], transformedScripts[index], StringComparison.Ordinal))
                continue;

            changed.Add((Sha256Base64(originalScripts[index]), Sha256Base64(transformedScripts[index])));
        }

        if (changed.Count == 0)
            return transformed;

        return CspMetaTag.Replace(
            transformed,
            meta => CspContentAttribute.Replace(
                meta.Value,
                content =>
                {
                    var csp = content.Groups["value"].Value;
                    csp = CspScriptDirective.Replace(csp, directive =>
                    {
                        var sources = directive.Groups["sources"].Value;
                        foreach (var (oldHash, newHash) in changed)
                        {
                            var oldToken = $"'sha256-{oldHash}'";
                            var newToken = $"'sha256-{newHash}'";
                            if (sources.Contains(oldToken, StringComparison.Ordinal))
                                sources = sources.Replace(oldToken, newToken, StringComparison.Ordinal);
                        }

                        return $"{directive.Groups["directive"].Value}{directive.Groups["spacing"].Value}{sources}";
                    },
                    1);
                    return $"{content.Groups["prefix"].Value}{content.Groups["quote"].Value}{csp}{content.Groups["quote"].Value}";
                },
                1),
            1);
    }

    private static bool HasScriptSource(string attributes) =>
        Regex.IsMatch(attributes, @"\bsrc\s*=", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static string Sha256Base64(string value) =>
        Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static void SetRevalidationHeaders(HttpResponse response)
    {
        // Entry HTML and shared static media must revalidate across container updates.
        response.Headers.CacheControl = "no-store";
        response.Headers.Pragma = "no-cache";
        response.Headers.Expires = "0";
    }

    private string? ResolveStaticFilePath(string requestPath)
    {
        var relativePath = requestPath.TrimStart('/');
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            return null;
        }

        try
        {
            var normalizedRelativePath = relativePath.Replace('/', Path.DirectorySeparatorChar);
            var candidatePath = Path.GetFullPath(Path.Combine(_webRootPath, normalizedRelativePath));

            if (string.Equals(candidatePath, _webRootPath, GetPathComparison()))
            {
                return null;
            }

            if (!candidatePath.StartsWith(_webRootPathWithSeparator, GetPathComparison()))
            {
                return null;
            }

            return candidatePath;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static StringComparison GetPathComparison()
    {
        return OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
    }

    private static string GetContentType(string filePath)
    {
        var ext = Path.GetExtension(filePath).ToLowerInvariant();
        return ext switch
        {
            ".html" => "text/html",
            ".css" => "text/css",
            ".js" => "application/javascript",
            ".json" => "application/json",
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".gif" => "image/gif",
            ".svg" => "image/svg+xml",
            ".ico" => "image/x-icon",
            _ => "application/octet-stream"
        };
    }
}
