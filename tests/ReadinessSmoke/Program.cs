using System.Diagnostics;
using System.IO;
using ExternalDrivePanel;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        if (args.Length == 3 && args[0] == "--guard-probe")
        {
            using var guard = SingleInstanceGuard.TryAcquire(args[1]);
            return (guard is null) == bool.Parse(args[2]) ? 0 : 1;
        }
        var root = Path.Combine(Path.GetTempPath(), "ExternalDeskPanel-Readiness-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "config.json");
            using (var guard = SingleInstanceGuard.TryAcquire(path))
            {
                Require(guard is not null && SingleInstanceGuard.TryAcquire(path) is null, "same config cannot acquire two leases");
                await Probe(path, true);
                using var independent = SingleInstanceGuard.TryAcquire(Path.Combine(root, "other.json"));
                Require(independent is not null, "isolated configs do not block tests");
            }
            await Probe(path, false);
            var store = new ConfigStore(path);
            var config = new AppConfig(); config.Drives["1"] = new();
            var saves = new List<Task<bool>>();
            for (var i = 0; i < 30; i++)
            { config.Drives["1"].LastKnownLabel = i.ToString(); saves.Add(store.SaveAsync(config)); }
            Require((await Task.WhenAll(saves)).All(success => success), "queued saves all complete safely");
            Require(store.Load().Drives["1"].LastKnownLabel == "29", "latest immutable snapshot wins");
            config.Drives["1"].LastKnownLabel = "exit";
            Require(store.Save(config) && store.Load().Drives["1"].LastKnownLabel == "exit", "final sync flush follows async queue");

            var a = Path.Combine(root, "A"); var b = Path.Combine(root, "B");
            var active = FileOperationRegistry.Begin([Path.Combine(a, "source.txt"), b]);
            using (FileOperationRegistry.Pause(a))
            {
                Require(FileOperationRegistry.IsBusy(a) && FileOperationRegistry.IsBusy(b), "source and destination are tracked");
                var idle = FileOperationRegistry.WaitForIdleAsync(a);
                Require(!idle.IsCompleted, "ejection waits for existing writer");
                var rejected = false;
                try { using var forbidden = FileOperationRegistry.Begin([a]); }
                catch (IOException) { rejected = true; }
                Require(rejected, "paused volume rejects new writers");
                using var unrelated = FileOperationRegistry.Begin([Path.Combine(root, "C")]);
                active.Dispose(); active.Dispose(); await idle;
            }
            Require(!FileOperationRegistry.IsBusy(), "leases are idempotent and released");
            using (FileOperationRegistry.Pause())
            {
                var rejected = false;
                try { using var forbidden = FileOperationRegistry.Begin([b]); } catch (IOException) { rejected = true; }
                Require(rejected, "exit blocks every new writer");
            }
            using var started = new ManualResetEventSlim();
            using var finish = new ManualResetEventSlim();
            var zip = new ZipCompressionService((source, directory) => { started.Set(); finish.Wait(); return source + ".zip"; });
            var compression = zip.TryCreateAsync(Path.Combine(a, "item.txt"), false)!;
            Require(started.Wait(TimeSpan.FromSeconds(5)) && FileOperationRegistry.IsBusy(a), "ZIP reserves lifecycle before work");
            var zipIdle = FileOperationRegistry.WaitForIdleAsync(a);
            Require(!zipIdle.IsCompleted && zip.TryCreateAsync(Path.Combine(a, "item.txt"), false) is null, "ZIP duplicate prevention and shutdown wait");
            finish.Set(); await compression; await zipIdle;
            var failing = new ZipCompressionService((_, _) => throw new IOException("fixture"));
            Require((await failing.TryCreateAsync(Path.Combine(a, "bad"), false)!).Error == "fixture"
                && !FileOperationRegistry.IsBusy(), "ZIP failure releases lifecycle");

            var favorites = new FavoriteCollection(); var changes = 0;
            favorites.Changed += (_, _) => changes++;
            using (favorites.BeginUpdate())
            using (favorites.BeginUpdate())
                for (var i = 0; i < 100; i++) favorites.Add(Path.Combine(a, i.ToString()), false);
            Require(changes == 1 && favorites.Items.Count == 100, "nested favorite batch emits one refresh/save notification");
            favorites.RemoveTree(a);
            Require(changes == 2 && favorites.Items.Count == 0, "bulk deletion emits one notification");
            Console.WriteLine("PASS readiness: cross-process single instance/release; 30 ordered immutable saves/final flush; root/global pause and source+destination lifetime; ZIP wait/duplicate/failure cleanup; batched favorites.");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        finally { Directory.Delete(root, true); }
    }
    private static async Task Probe(string path, bool blocked)
    {
        var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true };
        start.ArgumentList.Add("--guard-probe"); start.ArgumentList.Add(path); start.ArgumentList.Add(blocked.ToString());
        using var child = Process.Start(start)!;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await child.WaitForExitAsync(timeout.Token);
        Require(child.ExitCode == 0, "separate process observes the config lease correctly");
    }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
