using System.Text.Json;
using ExternalDrivePanel;

// No Application, Window or dispatcher: these checks run without creating a UI.
var directory = Path.Combine(Path.GetTempPath(), "ExternalDeskPanel-ViewLogic-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(directory);
try
{
    foreach (var global in Enum.GetValues<GlobalViewMode>())
    {
        Require(PanelViewPolicy.EffectiveMode(global, null) == global, "global fallback");
        foreach (var individual in Enum.GetValues<GlobalViewMode>())
            Require(PanelViewPolicy.EffectiveMode(global, individual) == individual, "drive override priority");
    }
    var config = new AppConfig
    {
        Drives =
        {
            ["1"] = new DriveConfig { Registered = true, ViewMode = GlobalViewMode.Details },
            ["2"] = new DriveConfig { Registered = true, Hidden = true, ViewMode = GlobalViewMode.Details },
            ["3"] = new DriveConfig { Registered = false, ViewMode = GlobalViewMode.Details }
        }
    };
    var globalView = new GlobalViewSettings();
    var applications = 0;
    globalView.ViewModeApplied += (_, _) => { applications++; PanelViewPolicy.ApplyGlobally(config, globalView.ViewMode); };
    globalView.SetViewMode(GlobalViewMode.Icon); // Already the default, still clears overrides.
    Require(applications == 1 && config.Drives["1"].ViewMode is null && config.Drives["2"].ViewMode is null
        && config.Drives["3"].ViewMode == GlobalViewMode.Details, "explicit same-mode applies to all registered drives, including hidden/offline");
    config.Drives["1"].ViewMode = GlobalViewMode.Details;
    globalView.IncreaseIconSize();
    globalView.DecreaseIconSize();
    globalView.ToggleHiddenItems();
    Require(applications == 1 && config.Drives["1"].ViewMode == GlobalViewMode.Details, "size/hidden do not clear overrides");
    globalView.SetViewMode(GlobalViewMode.Icon);
    Require(applications == 2 && config.Drives["1"].ViewMode is null, "repeated same-mode click applies again");

    var store = new ConfigStore(Path.Combine(directory, "config.json"));
    config.Drives["1"].ViewMode = GlobalViewMode.Icon;
    config.Drives["2"].ViewMode = GlobalViewMode.Details;
    Require(store.Save(config), "save modes");
    var json = File.ReadAllText(store.FilePath);
    using (var doc = JsonDocument.Parse(json))
    {
        Require(doc.RootElement.GetProperty("drives").GetProperty("1").GetProperty("viewMode").GetString() == "icons", "drive icon spelling");
        Require(doc.RootElement.GetProperty("globalView").GetProperty("viewMode").GetString() == "icon", "existing global spelling stays compatible");
    }
    var restored = store.Load();
    Require(restored.Drives["1"].ViewMode == GlobalViewMode.Icon && restored.Drives["2"].ViewMode == GlobalViewMode.Details, "mode roundtrip");
    restored.Drives["1"].ViewMode = null;
    Require(store.Save(restored), "save inherited mode");
    using (var doc = JsonDocument.Parse(File.ReadAllText(store.FilePath)))
        Require(!doc.RootElement.GetProperty("drives").GetProperty("1").TryGetProperty("viewMode", out _), "no null field: inherited mode omitted");
    Require(store.Load().Drives["1"].ViewMode is null && !File.Exists(store.FilePath + ".bak"), "older file without drive viewMode loads");

    Require(PanelViewPolicy.ShowEmptyFavorites(true, 0) && !PanelViewPolicy.ShowEmptyFavorites(false, 0)
        && !PanelViewPolicy.ShowEmptyFavorites(true, 1) && !PanelViewPolicy.ShowEmptyFavorites(false, 1), "empty favorite condition");
    var file = Path.Combine(directory, "alpha.txt");
    File.WriteAllText(file, "abc");
    var folder = Directory.CreateDirectory(Path.Combine(directory, "folder")).FullName;
    var missing = Path.Combine(directory, "missing");
    var favorites = new[] { new FavoriteItem(folder, true, "First folder"), new FavoriteItem(file, false, "Second file"), new FavoriteItem(missing, true) };
    var rows = favorites.Select(favorite => new FavoriteDetailsRow(favorite)).ToArray();
    Require(rows.Select(row => row.Favorite).SequenceEqual(favorites), "row creation retains insertion order");
    Require(rows[0].ModifiedDate is not null && rows[0].Size is null && rows[0].Type == "파일 폴더", "folder metadata");
    Require(rows[1].Name == "Second file" && rows[1].DisplayName == "📄 Second file" && rows[1].Size == 3
        && rows[1].ModifiedDate == File.GetLastWriteTime(file) && rows[1].Type == "TXT", "file metadata uses extension only and same icon format");
    foreach (var (name, expected) in new[] { ("photo.jPg", "JPG"), ("document.pdf", "PDF"), ("archive.tar.gz", "GZ"), ("README", "파일") })
    {
        var typedPath = Path.Combine(directory, name);
        File.WriteAllText(typedPath, "fixture");
        Require(FileSystemEntry.FromPath(typedPath).Type == expected
            && new FavoriteDetailsRow(new FavoriteItem(typedPath, false)).Type == expected,
            "folder/search/favorite rows share extension-only type: " + name);
    }
    Require(rows[2].ModifiedDate is null && rows[2].Size is null && rows[2].SizeDisplay == "" && rows[2].Type == "(경로 없음)", "missing favorite row data");
    Console.WriteLine("PASS (no windows): effective modes, explicit/repeated global apply, shared settings isolation, JSON compatibility/omission, empty-home condition, favorite metadata/order/missing paths.");
}
finally { Directory.Delete(directory, true); }

static void Require(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}
