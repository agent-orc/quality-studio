using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace QualityStudio.Api.Tests;

public sealed partial class ApiSmokeTests
{
    [Fact]
    public async Task Files_tree_exposes_real_directories_without_changing_canonical_file_ids()
    {
        await WriteExplorerFixture();
        using var client = application!.CreateClient();
        var canonical = await client.GetFromJsonAsync<JsonElement>("/api/tree", TestContext.Current.CancellationToken);
        var canonicalFiles = FlattenTree(canonical.GetProperty("nodes"))
            .Where(node => node.GetProperty("level").GetString() == "file")
            .GroupBy(node => node.GetProperty("path").GetString()!, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Select(node => node.GetProperty("id").GetString()!)
                .Order(StringComparer.Ordinal).First(), StringComparer.Ordinal);

        var physical = await client.GetFromJsonAsync<JsonElement>("/api/tree?view=files", TestContext.Current.CancellationToken);
        var root = Assert.Single(physical.GetProperty("nodes").EnumerateArray());
        Assert.Equal("repository", root.GetProperty("level").GetString());
        Assert.Equal(".", root.GetProperty("path").GetString());
        var nodes = FlattenTree(physical.GetProperty("nodes")).ToArray();
        Assert.Equal(nodes.Length, nodes.Select(node => node.GetProperty("path").GetString()).Distinct().Count());
        Assert.Contains(nodes, node => node.GetProperty("path").GetString() == "apps/backend/service" &&
            node.GetProperty("name").GetString() == "service" && node.GetProperty("level").GetString() == "folder");
        var frontend = Assert.Single(nodes, node => node.GetProperty("path").GetString() == "frontend");
        Assert.Equal(["src", "style-reference"], frontend.GetProperty("children").EnumerateArray()
            .Select(node => node.GetProperty("name").GetString()!).ToArray());
        Assert.DoesNotContain(nodes, node => node.GetProperty("level").GetString() is "project" or "module" or "namespace");
        var files = nodes.Where(node => node.GetProperty("level").GetString() == "file").ToArray();
        Assert.Equal(canonicalFiles.Count, files.Length);
        Assert.All(files, file =>
        {
            Assert.Equal(canonicalFiles[file.GetProperty("path").GetString()!], file.GetProperty("id").GetString());
            Assert.Empty(file.GetProperty("children").EnumerateArray());
        });
        Assert.DoesNotContain(files, file => file.GetProperty("path").GetString() == "bin/Generated.cs");

        var selected = await client.GetFromJsonAsync<JsonElement>("/api/tree?view=files&path=apps/backend",
            TestContext.Current.CancellationToken);
        Assert.Equal("apps/backend", Assert.Single(selected.GetProperty("nodes").EnumerateArray()).GetProperty("path").GetString());
    }

    [Fact]
    public async Task Files_tree_pages_bind_cursors_snapshots_and_etags_to_the_selected_view()
    {
        await WriteExplorerFixture();
        using var client = application!.CreateClient();
        using var initial = await client.GetAsync("/api/tree/v2?view=files", TestContext.Current.CancellationToken);
        var rootPage = await initial.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        var root = Assert.Single(rootPage.GetProperty("nodes").EnumerateArray());
        var rootId = Uri.EscapeDataString(root.GetProperty("id").GetString()!);
        var snapshot = rootPage.GetProperty("snapshotEtag").GetString()!;
        Assert.StartsWith("files:", snapshot, StringComparison.Ordinal);
        var selectedSnapshot = Uri.EscapeDataString(snapshot);
        var childrenPath = $"/api/tree/v2?view=files&parentId={rootId}&snapshot={selectedSnapshot}&limit=1";
        var first = await client.GetFromJsonAsync<JsonElement>(childrenPath, TestContext.Current.CancellationToken);
        var child = Assert.Single(first.GetProperty("nodes").EnumerateArray());
        Assert.Equal(root.GetProperty("id").GetString(), child.GetProperty("parentId").GetString());
        Assert.Empty(child.GetProperty("children").EnumerateArray());
        var cursor = first.GetProperty("nextCursor").GetString()!;
        Assert.StartsWith("tree-v2-files:", cursor, StringComparison.Ordinal);
        var next = await client.GetFromJsonAsync<JsonElement>(childrenPath + "&cursor=" + Uri.EscapeDataString(cursor),
            TestContext.Current.CancellationToken);
        Assert.NotEqual(child.GetProperty("id").GetString(),
            Assert.Single(next.GetProperty("nodes").EnumerateArray()).GetProperty("id").GetString());

        using var canonical = await client.GetAsync("/api/tree/v2", TestContext.Current.CancellationToken);
        Assert.NotEqual(canonical.Headers.ETag, initial.Headers.ETag);
        using var cachedRequest = new HttpRequestMessage(HttpMethod.Get, "/api/tree/v2?view=files");
        cachedRequest.Headers.IfNoneMatch.Add(initial.Headers.ETag!);
        using var cached = await client.SendAsync(cachedRequest, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotModified, cached.StatusCode);
        using var mixedSnapshot = await client.GetAsync("/api/tree/v2?snapshot=" + selectedSnapshot,
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, mixedSnapshot.StatusCode);
        using var mixedCursor = await client.GetAsync("/api/tree/v2?cursor=" + Uri.EscapeDataString(cursor),
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, mixedCursor.StatusCode);
    }

    [Fact]
    public async Task Files_tree_search_and_path_lookup_use_the_same_physical_projection()
    {
        await WriteExplorerFixture();
        using var client = application!.CreateClient();
        var result = await client.GetFromJsonAsync<JsonElement>(
            "/api/tree/v2/search?view=files&query=apps/backend/service&limit=20", TestContext.Current.CancellationToken);
        var hits = result.GetProperty("nodes").EnumerateArray().ToArray();
        var folder = Assert.Single(hits, node => node.GetProperty("path").GetString() == "apps/backend/service");
        var file = Assert.Single(hits, node => node.GetProperty("path").GetString() == "apps/backend/service/Service.cs");
        Assert.Equal("folder", folder.GetProperty("level").GetString());
        Assert.Equal(folder.GetProperty("id").GetString(), file.GetProperty("parentId").GetString());
        Assert.False(file.GetProperty("hasChildren").GetBoolean());
        Assert.All(hits, node => Assert.Empty(node.GetProperty("children").EnumerateArray()));
        using var missing = await client.GetAsync("/api/tree?view=files&path=missing-folder", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        using var invalidView = await client.GetAsync("/api/tree/v2?view=unknown", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, invalidView.StatusCode);
    }

    private async Task WriteExplorerFixture()
    {
        foreach (var (path, content) in new[]
        {
            ("apps/backend/service/Service.cs", "namespace Example; public class Service { public void Run() {} }"),
            ("frontend/angular.json", """
                {"projects":{"frontend":{"root":"","sourceRoot":"src"},"style-reference":{"root":"style-reference","sourceRoot":"style-reference"}}}
                """),
            ("frontend/src/app/shell/app.ts", "export class App {}"),
            ("frontend/src/app/shared/ui/control.ts", "export class Control {}"),
            ("frontend/style-reference/main.ts", "export const reference = true;"),
        })
        {
            var target = Path.Combine(repositoryRoot, path.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            await File.WriteAllTextAsync(target, content, TestContext.Current.CancellationToken);
        }
    }
}
