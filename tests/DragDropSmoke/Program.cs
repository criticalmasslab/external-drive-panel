using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Threading;
using ExternalDrivePanel;

internal static class Program
{
    [STAThread]
    private static int Main()
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var exit = 0;
        app.Dispatcher.BeginInvoke(new Action(async () =>
        {
            try { await Run(); }
            catch (Exception ex) { Console.Error.WriteLine(ex); exit = 1; }
            finally { app.Shutdown(); }
        }));
        app.Run();
        return exit;
    }

    private static async Task Run()
    {
        // All file operations use a uniquely created test directory, never user files.
        var sandbox = Path.Combine(Path.GetTempPath(), "ExternalDrivePanel-Drop-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(sandbox);
        try
        {
            var root = Directory.CreateDirectory(Path.Combine(sandbox, "panel")).FullName;
            var external = Directory.CreateDirectory(Path.Combine(sandbox, "external")).FullName;
            var destination = Directory.CreateDirectory(Path.Combine(root, "destination")).FullName;
            var local = Path.Combine(root, "local.txt");
            var source = Path.Combine(external, "hello.txt");
            File.WriteAllText(local, "local");
            File.WriteAllText(source, "new");
            var favorites = new FavoriteCollection();
            var prompts = new Prompts();
            var surface = new Grid { Background = System.Windows.Media.Brushes.White };
            var list = new ListBox();
            surface.Children.Add(list);
            var window = new Window { Content = new AdornerDecorator { Child = surface }, Width = 500, Height = 400 };
            var styleSource = new DrivePanelWindow(root, "Test resources", new GlobalViewSettings(), new EjectService());
            window.Resources.MergedDictionaries.Add(styleSource.Resources);
            window.Show();
            var refreshes = 0;
            string? currentFolder = null;
            using var drops = new DragDropService(surface, root, favorites, _ => refreshes++, prompts, () => currentFolder);

            Require(FileCopyService.IsWithin(@"D:\folder\item", @"D:\")
                && !FileCopyService.IsWithin(@"C:\folder\item", @"D:\")
                && !FileCopyService.IsWithin(@"C:\panel-other\item", @"C:\panel"), "drive/path boundary");
            await drops.ProcessDropAsync([local, local.ToUpperInvariant(), destination, destination + "\\"], null);
            Require(favorites.Items.Count == 2, "multiple same-drive favorites and normalized deduplication");
            await drops.ProcessDropAsync([source], null);
            Require(prompts.Messages.Last().Contains("폴더 위에") && !File.Exists(Path.Combine(destination, "hello.txt")), "blank guidance only");
            var beforeMessages = prompts.Messages.Count;
            await drops.ProcessDropAsync([source], new DropTarget(local, false));
            Require(prompts.Messages.Count == beforeMessages && prompts.Confirmations == 0, "file target ignored");
            prompts.Confirm = false;
            await drops.ProcessDropAsync([source], new DropTarget(destination, true));
            Require(!File.Exists(Path.Combine(destination, "hello.txt")), "copy declined");
            prompts.Confirm = true;
            var target = new DropTarget(destination, true);
            await drops.ProcessDropAsync([source], target);
            var copied = Path.Combine(destination, "hello.txt");
            Require(File.ReadAllText(copied) == "new" && File.Exists(source) && refreshes == 1, "copy + source retained + refresh");
            File.WriteAllText(source, "updated");
            prompts.Choice = CopyConflictChoice.Skip;
            await drops.ProcessDropAsync([source], target);
            Require(File.ReadAllText(copied) == "new", "skip preserves destination");
            prompts.Choice = CopyConflictChoice.Overwrite;
            await drops.ProcessDropAsync([source], target);
            Require(File.ReadAllText(copied) == "updated", "overwrite");
            var later = Path.Combine(external, "later.txt");
            File.WriteAllText(later, "later");
            prompts.Choice = CopyConflictChoice.Cancel;
            await drops.ProcessDropAsync([source, later], target);
            Require(!File.Exists(Path.Combine(destination, "later.txt")), "cancel stops remaining batch");
            currentFolder = destination;
            await drops.ProcessDropAsync([later], null);
            Require(File.ReadAllText(Path.Combine(destination, "later.txt")) == "later", "folder-view blank drop copies into current folder");
            var confirmations = prompts.Confirmations;
            await drops.ProcessDropAsync([source], new DropTarget(local, false));
            Require(prompts.Confirmations == confirmations, "file item does not fall back to current folder");
            currentFolder = null;
            await drops.ProcessDropAsync([later], null);
            Require(prompts.Confirmations == confirmations && prompts.Messages.Last().Contains("폴더 위에"), "home blank drop still only informs");

            var folder = Directory.CreateDirectory(Path.Combine(external, "tree")).FullName;
            Directory.CreateDirectory(Path.Combine(folder, "nested"));
            File.WriteAllText(Path.Combine(folder, "nested", "child.txt"), "nested");
            await drops.ProcessDropAsync([folder], target);
            Require(File.ReadAllText(Path.Combine(destination, "tree", "nested", "child.txt")) == "nested", "recursive copy");
            File.WriteAllText(Path.Combine(folder, "second.txt"), "second");
            prompts.Choice = CopyConflictChoice.Skip;
            await drops.ProcessDropAsync([folder], target);
            Require(!File.Exists(Path.Combine(destination, "tree", "second.txt")), "skip folder subtree");
            prompts.Choice = CopyConflictChoice.Overwrite;
            await drops.ProcessDropAsync([folder], target);
            Require(File.Exists(Path.Combine(destination, "tree", "second.txt")), "merge folder");

            // Real WPF item containers and routed drag events; no Explorer mouse injection.
            var favorite = new FavoriteItem(destination, true);
            var item = new ListBoxItem { DataContext = favorite, Content = "Target folder" };
            list.Items.Add(item);
            window.UpdateLayout();
            Require(DragDropService.FindTarget(item).Target == target, "favorite folder hit test");
            RaiseDrag(item, source, DragDrop.PreviewDragOverEvent);
            Require(DragDropService.GetIsDropTarget(item), "folder hover highlight");
            Require(HasHighlight(item), "highlight drawn above item template");
            RaiseDrag(surface, source, DragDrop.PreviewDragLeaveEvent);
            Require(!DragDropService.GetIsDropTarget(item), "highlight cleared on leave");
            Require(!HasHighlight(item), "highlight layer removed on leave");
            var otherWindow = new Window { Width = 100, Height = 100, Content = "Focus test" };
            foreach (var selected in new[] { false, true })
            {
                item.IsSelected = selected;
                otherWindow.Show();
                otherWindow.Activate();
                await Task.Delay(80);
                Require(!(bool)item.GetValue(System.Windows.Controls.Primitives.Selector.IsSelectionActiveProperty),
                    "selection is genuinely inactive before hover");
                var originalBackground = item.Background;
                RaiseDrag(item, source, DragDrop.PreviewDragEnterEvent);
                Require(HasHighlight(item) && item.IsSelected == selected, "inactive selected/unselected item highlights on enter");
                RaiseDrag(surface, source, DragDrop.PreviewDragLeaveEvent);
                Require(!HasHighlight(item) && ReferenceEquals(item.Background, originalBackground)
                    && item.IsSelected == selected, "original inactive selection/background preserved");
            }
            otherWindow.Close();
            item.DataContext = FileSystemEntry.FromPath(destination);
            Require(DragDropService.FindTarget(item).Target == target, "browser folder hit test");
            var row = new DataGridRow { DataContext = FileSystemEntry.FromPath(destination) };
            Require(DragDropService.FindTarget(row).Target == target, "details row hit test");
            var grid = new DataGrid { AutoGenerateColumns = true, ItemsSource = new[] { FileSystemEntry.FromPath(destination) } };
            surface.Children.Add(grid);
            window.UpdateLayout();
            var liveRow = (DataGridRow)grid.ItemContainerGenerator.ContainerFromIndex(0);
            liveRow.IsSelected = true;
            var rowBackground = liveRow.Background;
            RaiseDrag(liveRow, source, DragDrop.PreviewDragOverEvent);
            Require(HasHighlight(liveRow), "details selection cells cannot cover highlight layer");
            RaiseDrag(liveRow, local, DragDrop.PreviewDropEvent);
            Require(!HasHighlight(liveRow) && ReferenceEquals(liveRow.Background, rowBackground), "drop clears row overlay without changing background");
            surface.Children.Remove(grid);
            RaiseDrag(item, local, DragDrop.PreviewDropEvent);
            Require(favorites.Items.Count == 2, "routed same-drive Drop deduplicates");

            var workerThread = 0;
            await Task.Run(() => new FileCopyService().Copy([source], destination, (_, _) =>
            {
                workerThread = Environment.CurrentManagedThreadId;
                return CopyConflictChoice.Skip;
            }, CancellationToken.None));
            Require(workerThread != Environment.CurrentManagedThreadId, "file copy runs off UI thread");
            var selfRejected = false;
            try { new FileCopyService().Copy([folder], folder, (_, _) => CopyConflictChoice.Overwrite, CancellationToken.None); }
            catch (IOException) { selfRejected = true; }
            Require(selfRejected, "self/descendant copy rejected");
            var mismatch = Path.Combine(external, "destination");
            File.WriteAllText(mismatch, "must not replace a directory");
            var mismatchRejected = false;
            try { new FileCopyService().Copy([mismatch], root, (_, _) => CopyConflictChoice.Overwrite, CancellationToken.None); }
            catch (IOException) { mismatchRejected = true; }
            Require(mismatchRejected && File.Exists(copied), "type mismatch preserves directory contents");

            // Exercise all real conflict dialog buttons with test-owned windows.
            foreach (var (label, expected) in new[] { ("덮어쓰기", CopyConflictChoice.Overwrite), ("건너뛰기", CopyConflictChoice.Skip), ("취소", CopyConflictChoice.Cancel) })
            {
                var foregroundWindow = new Window { Width = 120, Height = 120, Content = "Foreground test" };
                foregroundWindow.Show();
                foregroundWindow.Activate();
                var dialog = new CopyConflictWindow(window, source, copied);
                dialog.ContentRendered += (_, _) =>
                {
                    var stack = (StackPanel)((ScrollViewer)dialog.Content).Content;
                    var buttons = (StackPanel)stack.Children[1];
                    buttons.Children.OfType<Button>().Single(b => (string)b.Content == label)
                        .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                };
                DialogActivation.Show(window, () =>
                {
                    Require(window.Topmost, "modal owner elevated before showing conflict dialog");
                    Require(window.IsActive, "modal owner activated from another window");
                    return dialog.ShowDialog();
                });
                Require(dialog.Choice == expected, "conflict button: " + label);
                Require(!window.Topmost, "owner topmost restored after modal");
                foregroundWindow.Close();
            }
            window.Topmost = true;
            DialogActivation.Show(window, () => true);
            Require(window.Topmost, "preexisting topmost state preserved");
            window.Topmost = false;
            try { DialogActivation.Show<bool>(window, () => throw new IOException("test")); }
            catch (IOException) { }
            Require(!window.Topmost, "topmost restored when dialog fails");
            window.Close();
            styleSource.Close();
            Console.WriteLine("PASS: home/folder blank targets, copy/conflicts, inactive selection overlay and restoration, details overlay, foreground activation, modal topmost restoration, and all conflict buttons.");
        }
        finally { Directory.Delete(sandbox, recursive: true); }
    }

    private static void RaiseDrag(UIElement element, string path, RoutedEvent routedEvent)
    {
        var constructor = typeof(DragEventArgs).GetConstructors(BindingFlags.NonPublic | BindingFlags.Instance)
            .Single(c => c.GetParameters().Length == 5);
        var args = (DragEventArgs)constructor.Invoke([new DataObject(DataFormats.FileDrop, new[] { path }),
            DragDropKeyStates.None, DragDropEffects.Copy, element, new Point(1, 1)]);
        args.RoutedEvent = routedEvent;
        element.RaiseEvent(args);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("FAIL: " + message);
    }

    private static bool HasHighlight(UIElement element) => AdornerLayer.GetAdornerLayer(element)
        ?.GetAdorners(element)?.Any(adorner => adorner.GetType().Name == "DropTargetAdorner") == true;

    private sealed class Prompts : IDropDialogs
    {
        public List<string> Messages { get; } = [];
        public bool Confirm = true;
        public int Confirmations;
        public CopyConflictChoice Choice = CopyConflictChoice.Overwrite;
        public void Inform(string message) => Messages.Add(message);
        public bool ConfirmCopy(string destination) { Confirmations++; return Confirm; }
        public CopyConflictChoice ResolveConflict(string source, string target) => Choice;
    }
}
