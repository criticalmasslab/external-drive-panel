using System.IO.Compression;
using ExternalDrivePanel;

foreach (var folder in new[] { false, true })
foreach (var home in new[] { false, true })
foreach (var favorite in new[] { false, true })
foreach (var exists in new[] { false, true })
foreach (var text in new[] { false, true })
{
    var entries = ItemMenuModel.Build(new(folder, home, favorite, exists, text));
    var expected = new List<ItemMenuCommand> { ItemMenuCommand.Open, folder ? ItemMenuCommand.Explore : ItemMenuCommand.OpenWith,
        ItemMenuCommand.Separator, ItemMenuCommand.ToggleFavorite, ItemMenuCommand.Compress, ItemMenuCommand.CopyPath, ItemMenuCommand.Properties };
    if (!folder && text) expected.Add(ItemMenuCommand.EditText);
    Require(entries.Select(e => e.Command).SequenceEqual(expected), "exact menu order across all 32 contexts");
    var toggle = entries.Single(e => e.Command == ItemMenuCommand.ToggleFavorite);
    Require(toggle.Label == (home || favorite ? "즐겨찾기에서 제거" : "즐겨찾기에 추가"), "favorite action");
    foreach (var entry in entries.Where(e => e.Command != ItemMenuCommand.Separator))
        Require(entry.Enabled == (entry.Command == ItemMenuCommand.CopyPath || entry.Command == ItemMenuCommand.ToggleFavorite && (home || favorite) || exists), "missing path disabled operations");
    Require(entries.Single(e => e.Command == ItemMenuCommand.Compress).Children!.Single().Command == ItemMenuCommand.Zip, "ZIP submenu only");
    Require(entries.Single(e => e.Command == ItemMenuCommand.Open).Gesture == "Enter"
        && entries.Single(e => e.Command == ItemMenuCommand.Properties).Gesture == "Alt+Enter"
        && entries.Single(e => e.Command == ItemMenuCommand.CopyPath).Gesture == "Ctrl+Shift+C", "gesture labels");
}
foreach (var extension in ItemMenuModel.TextExtensions.Split(' '))
    Require(ItemMenuModel.IsTextFile("file" + extension) && ItemMenuModel.IsTextFile("FILE" + extension.ToUpperInvariant()), "text extension case");
Require(!ItemMenuModel.IsTextFile("README") && !ItemMenuModel.IsTextFile("photo.png"), "non-text/extensionless");
Require(ItemMenuModel.SameFavorite(@"D:\", @"D:\Projects\ClientA\", @"d:\PROJECTS\clienta")
    && !ItemMenuModel.SameFavorite(@"D:\", @"D:\Projects\A", @"D:\Projects\AB")
    && !ItemMenuModel.SameFavorite(@"D:\", @"E:\A", @"E:\A"), "relative, case insensitive, trailing-slash favorite comparison");
Require(ShellActions.QuotedPath(@"D:\한글 폴더\file.txt") == "\"D:\\한글 폴더\\file.txt\"", "quoted clipboard format");
var batch = ItemMenuModel.BuildSelection([new(true, false, true, true, false), new(false, false, false, true, true)]);
Require(batch.Select(entry => entry.Command).SequenceEqual(new[] { ItemMenuCommand.Open, ItemMenuCommand.Separator,
    ItemMenuCommand.ToggleFavorite, ItemMenuCommand.Compress, ItemMenuCommand.CopyPath, ItemMenuCommand.Properties }), "mixed file/folder common menu");
Require(batch.Single(entry => entry.Command == ItemMenuCommand.ToggleFavorite).Label == "즐겨찾기에 추가", "mixed favorites offer add-all");
var missingBatch = ItemMenuModel.BuildSelection([new(false, true, true, true, true), new(false, true, true, false, true)]);
Require(missingBatch.Where(entry => entry.Enabled && entry.Command != ItemMenuCommand.Separator).Select(entry => entry.Command)
    .SequenceEqual(new[] { ItemMenuCommand.ToggleFavorite, ItemMenuCommand.CopyPath })
    && !missingBatch.Single(entry => entry.Command == ItemMenuCommand.Compress).Children!.Single().Enabled, "missing batch only removes/copies, including ZIP child guard");
Require(ItemMenuModel.BuildSelection([]).Count == 0, "empty selection has no menu");

var directory = Path.Combine(Path.GetTempPath(), "ExternalDeskPanel-Zip-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(directory);
try
{
    var file = Path.Combine(directory, "한글 문서.txt");
    File.WriteAllText(file, "한글 내용");
    var firstPath = ZipCompressionService.UniquePath(file, false);
    Require(Path.GetFileName(firstPath) == "한글 문서.zip", "file ZIP name");
    File.WriteAllText(firstPath, "do not overwrite");
    Require(Path.GetFileName(ZipCompressionService.UniquePath(file, false)) == "한글 문서 (2).zip", "number starts at 2");
    Directory.CreateDirectory(Path.Combine(directory, "한글 문서 (2).zip"));
    Require(Path.GetFileName(ZipCompressionService.UniquePath(file, false)) == "한글 문서 (3).zip", "existing folders also reserve names");
    var zip = ZipCompressionService.CreateArchive(file, false);
    using (var archive = ZipFile.OpenRead(zip))
    {
        Require(archive.Entries.Count == 1 && archive.Entries[0].FullName == "한글 문서.txt", "single file ZIP entry");
        using var reader = new StreamReader(archive.Entries[0].Open());
        Require(reader.ReadToEnd() == "한글 내용", "ZIP payload roundtrip");
    }
    Require(File.ReadAllText(firstPath) == "do not overwrite" && File.Exists(file), "source and existing ZIP preserved");
    var folder = Directory.CreateDirectory(Path.Combine(directory, "한글 폴더.v1")).FullName;
    Directory.CreateDirectory(Path.Combine(folder, "빈 하위폴더"));
    File.WriteAllText(Path.Combine(folder, "메모.txt"), "memo");
    var folderZip = ZipCompressionService.CreateArchive(folder, true);
    Require(Path.GetFileName(folderZip) == "한글 폴더.v1.zip", "folder dots preserved in ZIP name");
    using (var archive = ZipFile.OpenRead(folderZip))
    {
        var names = archive.Entries.Select(e => e.FullName.Replace('\\', '/')).ToArray();
        Require(names.Contains("한글 폴더.v1/메모.txt") && names.Contains("한글 폴더.v1/빈 하위폴더/"), "base directory and empty nested directory preserved");
    }
    var empty = Directory.CreateDirectory(Path.Combine(directory, "완전히 빈 폴더")).FullName;
    using (var archive = ZipFile.OpenRead(ZipCompressionService.CreateArchive(empty, true)))
        Require(archive.Entries.Single().FullName == "완전히 빈 폴더/", "empty root folder ZIP");

    var locked = Path.Combine(directory, "locked.txt");
    File.WriteAllText(locked, "locked");
    var partial = ZipCompressionService.UniquePath(locked, false);
    using (var handle = new FileStream(locked, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
    {
        var result = await new ZipCompressionService().TryCreateAsync(locked, false)!;
        Require(result.OutputPath is null && result.Error is not null && !File.Exists(partial), "failed compression removes partial ZIP");
    }
    using var gate = new ManualResetEventSlim();
    var service = new ZipCompressionService((source, isDirectory) => { gate.Wait(); return ZipCompressionService.CreateArchive(source, isDirectory); });
    var pending = service.TryCreateAsync(file, false)!;
    Require(service.TryCreateAsync(file.ToUpperInvariant(), false) is null, "same item cannot start twice");
    gate.Set();
    Require((await pending).OutputPath is not null, "background compression completes");
    var another = await service.TryCreateAsync(file, false)!;
    Require(another.OutputPath is not null, "running reservation released after completion");
    Console.WriteLine("PASS (no windows): menu context matrix, text types, relative favorites, quoted paths, ZIP unique names/no overwrite, Unicode payload, folders/empty folders, failure cleanup, background duplicate guard.");
}
finally { Directory.Delete(directory, true); }

static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
