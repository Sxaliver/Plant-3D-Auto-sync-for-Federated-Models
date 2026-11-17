using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using IWMF.PlantModelSync;
using Newtonsoft.Json.Linq;

internal static class Program
{
    private static int passed;
    private static void Check(bool ok, string name) { if (!ok) throw new Exception(name); Console.WriteLine("PASS " + name); passed++; }
    private static void Reject(Action action, string name) { bool rejected = false; try { action(); } catch { rejected = true; } Check(rejected, name); }
    private static async Task RejectAsync(Func<Task> action, string name) { bool rejected = false; try { await action(); } catch { rejected = true; } Check(rejected, name); }
    private sealed class Reader : IReferenceReader
    {
        public Dictionary<string, List<RefEdge>> Items = new Dictionary<string, List<RefEdge>>(StringComparer.OrdinalIgnoreCase);
        public List<RefEdge> Read(string path) => Items[path];
    }
    private sealed class Handler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, Task<HttpResponseMessage>> callback;
        public Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> action) { callback = action; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) { ct.ThrowIfCancellationRequested(); return callback(request); }
    }
    private static HttpResponseMessage Json(string text) => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(text, Encoding.UTF8, "application/json") };
    private const string Project = "11111111-2222-3333-4444-555555555555";
    public static int Main(string[] args)
    {
        try { Run(args.Length > 0 ? args[0] : Path.Combine(Path.GetTempPath(), "pms-tests-" + Guid.NewGuid().ToString("N"))).GetAwaiter().GetResult(); Console.WriteLine("TOTAL PASS " + passed); return 0; }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
    private static async Task Run(string dir)
    {
        Directory.CreateDirectory(dir);
        var folder = FolderLink.Parse("https://acc.autodesk.com/docs/files/projects/" + Project + "?folderUrn=urn%3Aadsk.wipprod%3Afs.folder%3Aco.test_123");
        Check(folder.Project == "b." + Project && folder.Folder.EndsWith("test_123"), "ACC encoded folder link");
        Check(FolderLink.Parse("https://docs.b360.autodesk.com/projects/" + Project + "/folders/urn%3Aadsk.wipprod%3Afs.folder%3Aco.abc/detail").Folder.EndsWith("abc"), "BIM360 path link");
        Check(FolderLink.Parse("https://forma.autodesk.com/files?projectId=b." + Project + "&folderUrn=urn:adsk.wipemea:fs.folder:co.xyz").Project == folder.Project, "Forma project query link");
        Reject(() => FolderLink.Parse("https://autodesk.com.evil.example/projects/" + Project), "Reject non-Autodesk host");
        Reject(() => FolderLink.Parse("https://acc.autodesk.com/share/abc"), "Reject ambiguous share link");
        Reject(() => Paths.CloudRelative("../secret.dwg"), "Reject cloud traversal");
        Reject(() => Paths.CloudRelative("C:\\source.dwg"), "Reject absolute cloud path");
        Reject(() => Paths.CloudRelative("a//b.dwg"), "Reject empty path segment");
        Reject(() => Paths.CloudRelative("a.txt"), "Reject non-DWG mapping");
        Check(Paths.CloudRelative("设备\\a.dwg") == "设备/a.dwg", "Unicode relative mapping");
        Check(Paths.Relative(dir, Path.Combine(dir, "a", "same.dwg")) == "a/same.dwg", "Preserve subdirectories");
        Check(Paths.Relative(dir, dir + "-other\\same.dwg") == "", "Sibling prefix is outside root");
        Check(Paths.Resolve(Path.Combine(dir, "sub", "a.dwg"), "..\\b.dwg") == Path.Combine(dir, "b.dwg"), "Resolve against parent DWG");
        var a = Path.Combine(dir, "assembly.dwg"); var b = Path.Combine(dir, "b.dwg"); var c = Path.Combine(dir, "c.dwg"); var missing = Path.Combine(dir, "missing.dwg");
        var reader = new Reader();
        reader.Items[a] = new List<RefEdge> { new RefEdge { Name = "B1", Saved = "b.dwg" }, new RefEdge { Name = "B2", Saved = "b.dwg", Overlay = true, Unloaded = true }, new RefEdge { Name = "Missing", Saved = "missing.dwg" } };
        reader.Items[b] = new List<RefEdge> { new RefEdge { Name = "C", Saved = "c.dwg" } };
        reader.Items[c] = new List<RefEdge> { new RefEdge { Name = "Cycle", Saved = "assembly.dwg" } };
        var scan = Scanner.Scan(a, dir, reader);
        Check(scan.Rows.Count == 3, "Deduplicate sources and exclude cyclic assembly");
        Check(scan.Rows.Single(x => x.LocalPath == b).Count == 2, "Preserve repeated reference count");
        Check(scan.Rows.Single(x => x.LocalPath == b).References.Contains("Overlay(卸载)"), "Preserve unloaded overlay metadata");
        Check(scan.Warnings.Any(x => x.Contains("循环")), "Cycle diagnosis");
        Check(scan.Warnings.Any(x => x.Contains("missing.dwg")), "Incomplete dependency diagnosis");
        var baseline = new Baseline { Hash = "H1", Version = "V1" };
        Check(Files.Conflict(baseline, "H2", "V2") != "", "Both sides changed blocks transfer");
        Check(Files.Conflict(baseline, "H1", "V2") == "", "Cloud-only change allowed");
        Check(Files.Conflict(baseline, "H2", "V1") == "", "Local-only change allowed");
        File.WriteAllBytes(b, Encoding.ASCII.GetBytes("AC1032old-fixture")); var initial = Files.Hash(b);
        var staged = Path.Combine(dir, "staged.tmp"); File.WriteAllBytes(staged, Encoding.ASCII.GetBytes("AC1032new-fixture"));
        var backup = Files.InstallDownload(staged, b, initial);
        Check(File.Exists(backup) && Files.Hash(backup) == initial, "Atomic replacement preserves backup");
        Check(Files.Hash(b) != initial, "New content installed");
        File.WriteAllBytes(staged, Encoding.ASCII.GetBytes("AC1032third-fixture"));
        Reject(() => Files.InstallDownload(staged, b, initial), "Reject local changes since preview");
        File.WriteAllText(staged, "<html>download error</html>", Encoding.UTF8);
        Reject(() => Files.InstallDownload(staged, b, Files.Hash(b)), "Reject HTML masquerading as DWG");
        File.WriteAllText(Path.ChangeExtension(b, ".dwl"), "fixture", Encoding.UTF8);
        Reject(() => Files.AssertClosed(b), "Respect CAD lock files");
        Check(ApsClient.ObjectPath("urn:adsk.objects:os.object:wip.dm.prod/a/b.dwg").EndsWith("a%2Fb.dwg"), "Encode storage object key");
        Reject(() => ApsClient.ApiUri("https://example.com/next"), "Pagination cannot leak access token");
        await DownloadTest(dir);
        await UploadTest(dir, folder);
        await PlanTest(dir, folder);
        await FailedUploadTest(file: Path.Combine(dir, "upload.dwg"), folder: folder);
    }
    private static async Task DownloadTest(string dir)
    {
        using (var client = new ApsClient(ct => Task.FromResult("fake-test-token"), new Handler(req => Task.FromResult(Json("{\"status\":\"complete\",\"url\":\"https://s3.example.test/object\"}"))), new Handler(req =>
        {
            Check(req.Headers.Authorization == null, "Signed download has no bearer token");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Encoding.ASCII.GetBytes("AC1032download-fixture")) });
        })))
        {
            var target = Path.Combine(dir, "download.tmp");
            await client.Download(new RemoteFile { Storage = "urn:adsk.objects:os.object:bucket/a" }, target, CancellationToken.None);
            Check(File.Exists(target), "Download writes staging file");
        }
        using (var client = new ApsClient(ct => Task.FromResult("fake"), new Handler(req => Task.FromResult(Json("{\"status\":\"pending\"}")))))
            await RejectAsync(() => client.Download(new RemoteFile { Storage = "urn:adsk.objects:os.object:bucket/a" }, Path.Combine(dir, "pending.tmp"), CancellationToken.None), "Pending download cannot replace source");
    }
    private static async Task UploadTest(string dir, FolderLink folder)
    {
        string file = Path.Combine(dir, "upload.dwg");
        var bytes = new byte[9 * 1024 * 1024]; Encoding.ASCII.GetBytes("AC1032").CopyTo(bytes, 0); File.WriteAllBytes(file, bytes);
        var calls = new List<string>(); int chunks = 0; bool completed = false;
        using (var client = new ApsClient(ct => Task.FromResult("fake-test-token"), new Handler(async req =>
        {
            var path = req.RequestUri.AbsolutePath; calls.Add(req.Method + " " + path);
            Check(req.Headers.Authorization?.Parameter == "fake-test-token", "API receives authorization");
            if (path.EndsWith("/contents")) return Json("{\"data\":[]}");
            if (path.EndsWith("/storage")) return Json("{\"data\":{\"id\":\"urn:adsk.objects:os.object:bucket/upload\"}}");
            if (path.EndsWith("/signeds3upload") && req.Method == HttpMethod.Get) return Json("{\"uploadKey\":\"test-key\",\"urls\":[\"https://s3.example.test/part\"]}");
            if (path.EndsWith("/signeds3upload")) { Check(chunks == 2, "Complete upload only after all parts"); completed = true; return Json("{}"); }
            if (path.EndsWith("/items"))
            {
                Check(completed, "Publish item only after S3 completion");
                var body = JObject.Parse(await req.Content.ReadAsStringAsync());
                Check((string)body["included"]?[0]?["relationships"]?["storage"]?["data"]?["id"] == "urn:adsk.objects:os.object:bucket/upload", "Published version uses uploaded storage");
                return Json("{\"data\":{\"id\":\"item-1\",\"relationships\":{\"tip\":{\"data\":{\"id\":\"version-1\"}}}}}");
            }
            if (path.EndsWith("/items/item-1")) return Json("{\"data\":{\"id\":\"item-1\",\"relationships\":{\"tip\":{\"data\":{\"id\":\"version-1\"}}}}}");
            if (path.EndsWith("/versions/version-1")) return Json("{\"data\":{\"relationships\":{\"storage\":{\"data\":{\"id\":\"urn:adsk.objects:os.object:bucket/upload\"}}}}}");
            throw new Exception("Unexpected endpoint: " + path);
        }), new Handler(async req =>
        {
            Check(req.Headers.Authorization == null, "Signed upload has no bearer token");
            var payload = await req.Content.ReadAsByteArrayAsync(); chunks++;
            Check(payload.Length == (chunks == 1 ? 8 * 1024 * 1024 : 1024 * 1024), "Chunk boundary " + chunks);
            return new HttpResponseMessage(HttpStatusCode.OK);
        })))
        {
            var uploaded = await client.Upload(folder, folder.Folder, "upload.dwg", file, Files.Hash(file), null, CancellationToken.None, null);
            Check(uploaded.Version == "version-1", "Upload verifies returned cloud version");
            Check(calls.Last().EndsWith("/versions/version-1"), "Success requires final version read");
        }
    }
    private static async Task PlanTest(string dir, FolderLink folder)
    {
        using (var client = new ApsClient(ct => Task.FromResult("fake"), new Handler(req => Task.FromResult(Json("{\"data\":[]}")))))
        {
            var service = new SyncService(client, new Settings());
            var plans = await service.Plan(folder, new[]
            {
                new XrefRow { LocalPath = Path.Combine(dir, "upload.dwg"), CloudPath = "same.dwg" },
                new XrefRow { LocalPath = Path.Combine(dir, "b.dwg"), CloudPath = "SAME.dwg" },
                new XrefRow { LocalPath = Path.Combine(dir, "upload.dwg"), CloudPath = "absent/a.dwg" }
            }, true, CancellationToken.None, null);
            Check(plans.Take(2).All(p => p.Blocked != null), "Many-to-one cloud mapping blocked");
            Check(plans[2].Blocked.Contains("父文件夹"), "No silent cloud folder creation");
            var pull = await service.Plan(folder, new[] { new XrefRow { LocalPath = Path.Combine(dir, "upload.dwg"), CloudPath = "absent.dwg" } }, false, CancellationToken.None, null);
            Check(pull[0].Blocked != null, "Missing cloud file cannot overwrite local");
        }
        int pages = 0;
        using (var client = new ApsClient(ct => Task.FromResult("fake"), new Handler(req =>
        {
            pages++;
            return Task.FromResult(Json(pages == 1 ? "{\"data\":[],\"links\":{\"next\":{\"href\":\"https://developer.api.autodesk.com/data/v1/page2\"}}}" : "{\"data\":[{\"type\":\"items\",\"id\":\"i\",\"attributes\":{\"displayName\":\"late.dwg\"}}]}"));
        })))
        {
            var index = await client.Index(folder, CancellationToken.None);
            Check(pages == 2 && index.Files.ContainsKey("late.dwg"), "Folder pagination includes later files");
        }
    }
    private static async Task FailedUploadTest(string file, FolderLink folder)
    {
        bool published = false;
        using (var client = new ApsClient(ct => Task.FromResult("fake"), new Handler(req =>
        {
            var path = req.RequestUri.AbsolutePath;
            if (path.EndsWith("/contents")) return Task.FromResult(Json("{\"data\":[]}"));
            if (path.EndsWith("/storage")) return Task.FromResult(Json("{\"data\":{\"id\":\"urn:adsk.objects:os.object:bucket/fail\"}}"));
            if (path.EndsWith("/signeds3upload") && req.Method == HttpMethod.Get) return Task.FromResult(Json("{\"uploadKey\":\"test\",\"urls\":[\"https://s3.example.test/fail\"]}"));
            published = true; return Task.FromResult(Json("{}"));
        }), new Handler(req => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden)))))
        {
            await RejectAsync(() => client.Upload(folder, folder.Folder, "fail.dwg", file, Files.Hash(file), null, CancellationToken.None, null), "Failed signed upload reports failure");
            Check(!published, "Failed transfer never publishes cloud item or version");
        }
    }
}
