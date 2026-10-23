using System.IO;
using System.Diagnostics;
using System.Buffers.Binary;
using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ExternalDrivePanel;

internal static class Program
{
    [STAThread] private static int Main(string[] args)
    {
        var result = 0; var root = Path.Combine(Path.GetTempPath(), "ExternalDeskPanel-QuickLook-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root); Fixtures(root);
        if (args.Contains("--software")) RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Dispatcher.BeginInvoke(new Action(async () =>
        {
            try
            {
                if (args.Contains("--interactive"))
                {
                    var panel = Panel(root); panel.Title = "스페이스바 미리보기 — 테스트"; panel.Width = 640; panel.Height = 600; panel.ShowInTaskbar = true;
                    panel.Closed += (_, _) => app.Shutdown(); panel.Show(); return;
                }
                if (args.Length == 2 && args[0] == "--published") await TestPublished(root, args[1]);
                else await Test(root, !args.Contains("--no-media"), args.Contains("--pdf-only"), !args.Contains("--no-shell"));
                Console.WriteLine("PASS: native Windows PDF pages/zoom; images/text/encoding/limits; unknown/missing/corrupt fallbacks; WAV paused/no autoplay; cancellation/resource release; Space and Esc in four panel views; selection changes and multi-selection snapshots; existing thumbnail switch independent.");
            }
            catch (Exception ex) { Console.Error.WriteLine(ex); result = 1; }
            app.Shutdown();
        }));
        try { app.Run(); } finally { Directory.Delete(root, true); }
        return result;
    }
    private static DrivePanelWindow Panel(string root)
    {
        var config = new DriveConfig();
        foreach (var path in Directory.GetFiles(root).OrderBy(Path.GetFileName))
            config.Favorites.Add(new() { RelativePath = Path.GetFileName(path), DisplayName = Path.GetFileName(path), IsDirectory = false });
        return new(root, "Quick Look", new GlobalViewSettings(), new EjectService(), config);
    }
    private static async Task TestPublished(string root, string executable)
    {
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(PdfPreviewWorker.Switch); start.ArgumentList.Add(Convert.ToBase64String(Encoding.UTF8.GetBytes(Path.Combine(root, "두 페이지.pdf"))));
        using var child = Process.Start(start)!; _ = child.StandardError.ReadToEndAsync();
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var header = new byte[8]; await child.StandardOutput.BaseStream.ReadExactlyAsync(header, timeout.Token);
            Require(BinaryPrimitives.ReadInt32LittleEndian(header) == 0x51504631 && BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(4)) == 2, "single-file published worker reads native PDF without a GUI");
            await child.StandardInput.WriteLineAsync("PAGE 1 1200"); await child.StandardInput.FlushAsync();
            var lengthBytes = new byte[4]; await child.StandardOutput.BaseStream.ReadExactlyAsync(lengthBytes, timeout.Token);
            var length = BinaryPrimitives.ReadInt32LittleEndian(lengthBytes); Require(length is > 0 and < 32 * 1024 * 1024, "published worker bounded response");
            var packet = new byte[length]; await child.StandardOutput.BaseStream.ReadExactlyAsync(packet, timeout.Token);
            using var stream = new MemoryStream(packet); var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            Require(decoder.Frames[0].PixelHeight == 1200, "published worker really returns rendered page PNG");
            await child.StandardInput.WriteLineAsync("CLOSE"); await child.StandardInput.FlushAsync(); child.StandardInput.Close();
            await child.WaitForExitAsync(timeout.Token); Require(child.ExitCode == 0, "published worker closes cleanly");
            Console.WriteLine("PASS self-contained published worker: native PDF metadata/page PNG and graceful exit 0.");
        }
        finally { if (!child.HasExited) { child.Kill(); await child.WaitForExitAsync(); } }
    }
    private static async Task Test(string root, bool testMedia, bool pdfOnly, bool testShell)
    {
        Require(QuickPreviewPolicy.Kind("PICTURE.JPG") == QuickPreviewKind.Image && QuickPreviewPolicy.Kind("a.PDF") == QuickPreviewKind.Pdf,
            "case-insensitive file kinds");
        Require(QuickPreviewPolicy.Kind("a.html") == QuickPreviewKind.Text && QuickPreviewPolicy.Kind(".gitignore") == QuickPreviewKind.Text
            && QuickPreviewPolicy.Kind("run.exe") == QuickPreviewKind.Information && QuickPreviewPolicy.Kind("folder", true) == QuickPreviewKind.Information, "scripts are text; executables/folders are information only");
        foreach (var file in new[] { "한글.txt", "unicode.txt", "ansi.txt" })
            Require((await QuickPreviewFiles.ReadTextAsync(Path.Combine(root, file))).Content.Contains("안녕하세요"), "UTF8/UTF16/CP949 text");
        var large = await QuickPreviewFiles.ReadTextAsync(Path.Combine(root, "large.log"));
        Require(large.Truncated && large.Content.Length <= QuickPreviewFiles.MaxTextBytes, "bounded text read and multibyte boundary");
        var html = await QuickPreviewFiles.ReadTextAsync(Path.Combine(root, "script.html"));
        Require(html.Content.Contains("<script>"), "HTML kept as literal text");
        await using (var pdf = await WindowsPdfPreview.OpenAsync(Path.Combine(root, "두 페이지.pdf")))
        {
            Require(pdf.PageCount == 2, "native engine reads PDF page count");
            var first = await pdf.RenderAsync(0); var second = await pdf.RenderAsync(1);
            Require(first.IsFrozen && second.IsFrozen && first.PixelWidth <= 2048 && first.PixelHeight <= 2048, "native PDF bounded frozen page images");
            var p1 = new byte[4]; var p2 = new byte[4];
            first.CopyPixels(new Int32Rect(10, 10, 1, 1), p1, 4, 0); second.CopyPixels(new Int32Rect(10, 10, 1, 1), p2, 4, 0);
            Require(!p1.SequenceEqual(p2), "page navigation really renders different pages");
        }
        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel();
            try { await WindowsPdfPreview.OpenAsync(Path.Combine(root, "두 페이지.pdf"), cancelled.Token); throw new Exception("cancel ignored"); }
            catch (OperationCanceledException) { }
        }
        if (pdfOnly) return;
        var panel = Panel(root); panel.Width = 640; panel.Height = 600; panel.Show();
        var window = new QuickPreviewWindow { Owner = panel }; window.Show();
        ItemMenuTarget Target(string name) => new(Path.Combine(root, name), false);
        try
        {
            await window.ShowItemsAsync([Target("두 페이지.pdf")], 0); await Layout(window);
            Require(window.ShowInTaskbar, "minimized preview is reachable from the taskbar");
            var normalBounds = window.RestoreBounds;
            Click((Button)window.FindName("PreviewMaximizeButton")); await Layout(window);
            Require(window.WindowState == WindowState.Maximized && ((SymbolIcon)window.FindName("PreviewMaximizeIcon")).Kind == "Restore", "maximize button toggles to restore");
            Click((Button)window.FindName("PreviewMaximizeButton")); await Layout(window);
            Require(window.WindowState == WindowState.Normal && ((SymbolIcon)window.FindName("PreviewMaximizeIcon")).Kind == "Maximize", "restore button returns to normal");
            Require(Math.Abs(window.RestoreBounds.Width - normalBounds.Width) < 1 && Math.Abs(window.RestoreBounds.Height - normalBounds.Height) < 1, "maximize/restore preserves preview size");
            Click((Button)window.FindName("PreviewMinimizeButton")); await Layout(window);
            Require(window.WindowState == WindowState.Minimized && window.PdfPageCount == 2, "minimize preserves loaded PDF");
            SystemCommands.RestoreWindow(window); await Layout(window);
            Require(window.WindowState == WindowState.Normal, "minimized preview restores without reloading");
            Require(window.PdfPageCount == 2 && ((Image)window.FindName("PreviewImage")).Source is not null, "PDF popup render");
            await window.NavigatePdfAsync(1);
            await Layout(window);
            Require(window.PdfPageIndex == 1 && ((TextBlock)window.FindName("PagePosition")).Text.Contains("2 / 2"), "PDF next page");
            Require(((TextBlock)window.FindName("PreviewStatus")).Text.Contains("2 / 2"), "rendered page status follows the selected page");
            var image = (Image)window.FindName("PreviewImage"); var fitWidth = image.Width;
            Click((Button)window.FindName("ZoomInButton")); Require(image.Width > fitWidth, "zoom really enlarges PDF image");
            Click((Button)window.FindName("FitButton")); Require(Math.Abs(image.Width - fitWidth) < 1, "fit restores PDF size");
            await Layout(window);
            Save(window, "pdf-preview.png");
            await window.ShowItemsAsync([Target("사진.png")], 0); await Layout(window);
            Require(((Image)window.FindName("PreviewImage")).Source is BitmapSource && window.PdfPageCount == 0, "image popup and PDF release");
            Save(window, "image-preview.png");
            await window.ShowItemsAsync([Target("한글.txt")], 0); await Layout(window);
            Require(((TextBox)window.FindName("TextPreview")).Text.Contains("안녕하세요") && ((TextBox)window.FindName("TextPreview")).IsReadOnly, "read-only text popup");
            Save(window, "text-preview.png");
            foreach (var name in testShell ? new[] { "binary.xyz", "missing.txt", "broken.pdf", "broken.png" } : new[] { "missing.txt", "broken.pdf" })
            {
                await window.ShowItemsAsync([Target(name)], 0);
                Require(((Border)window.FindName("InformationPanel")).IsVisible, "unsupported/missing/corrupt file remains safe: " + name);
            }
            if (testMedia)
            {
                await window.ShowItemsAsync([Target("broken.mp4")], 0);
                await Wait(() => ((Border)window.FindName("InformationPanel")).IsVisible, "bad media reports a codec/file failure without crashing");
            }
            var pending = window.ShowItemsAsync([Target("두 페이지.pdf")], 0);
            await window.ShowItemsAsync([Target("한글.txt")], 0); await pending;
            Require(window.CurrentPath!.EndsWith("한글.txt") && ((TextBox)window.FindName("TextPreview")).IsVisible && window.PdfPageCount == 0, "stale PDF completion cannot replace newer text");
            if (testMedia)
            {
            await window.ShowItemsAsync([Target("silence.wav")], 0);
            await Wait(() => ((Button)window.FindName("PlayButton")).IsEnabled || ((Border)window.FindName("InformationPanel")).IsVisible, "media initializes or reports codec fallback");
            Require(((Button)window.FindName("PlayButton")).Content.ToString() == "재생", "media never autoplays");
            Console.WriteLine(((Button)window.FindName("PlayButton")).IsEnabled ? "PASS Windows WAV codec initialized paused." : "NOTE: WAV codec unavailable; fallback shown.");
            await window.ShowItemsAsync([Target("한글.txt")], 0);
            using (var exclusive = new FileStream(Path.Combine(root, "silence.wav"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                Require(exclusive.Length > 0, "leaving media releases its file handle");
            }
        }
        finally { window.Close(); }

        var controller = (QuickPreviewController)typeof(DrivePanelWindow).GetField("_quickPreview", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(panel)!;
        foreach (var home in new[] { true, false })
        foreach (var details in new[] { false, true })
        {
            Call(panel, home ? "ShowFavorites" : "NavigateTo", home ? [true] : [root, true]);
            if ((panel.EffectiveViewMode == GlobalViewMode.Details) != details) Click((Button)panel.FindName("ViewModeButton"));
            await Layout(panel);
            var view = (System.Windows.Controls.Primitives.Selector)panel.FindName(details ? "DetailsGrid" : home ? "FavoriteItemsList" : "IconItemsList");
            var selected = view.Items.Cast<object>().First(item => ItemMenuTarget.FromItem(item)!.Path.EndsWith("사진.png"));
            view.SelectedItem = selected; panel.Activate(); view.Focus(); await Layout(panel);
            Require(view.FocusVisualStyle is null, "content view has no dotted keyboard-focus adorner");
            if (view.ItemContainerGenerator.ContainerFromItem(selected) is Control container)
                Require(container.FocusVisualStyle is null, "selected item has no dotted keyboard-focus adorner");
            var args = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(panel), 0, Key.Space) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
            panel.RaiseEvent(args);
            Require(args.Handled && controller.CurrentWindow is not null, "Space opens popup in each of four views");
            await controller.CurrentWindow!.CurrentLoad;
            Require(controller.CurrentWindow.CurrentPath!.EndsWith("사진.png"), "same selected item previewed");
            var existing = controller.CurrentWindow;
            Click((Button)existing.FindName("PreviewMinimizeButton")); await Layout(panel);
            panel.Activate(); view.Focus(); await Layout(panel);
            var restoreKey = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(panel), 0, Key.Space) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
            panel.RaiseEvent(restoreKey); await Layout(existing);
            Require(restoreKey.Handled && controller.CurrentWindow == existing && existing.WindowState == WindowState.Normal,
                "Space restores minimized preview rather than closing or replacing it");
            var key = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(controller.CurrentWindow), 0, Key.Space) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
            controller.CurrentWindow.RaiseEvent(key);
            Require(controller.CurrentWindow is null && view.SelectedItem == selected, "Space closes without changing selection");
        }
        var list = (ListBox)panel.FindName("IconItemsList");
        if (panel.EffectiveViewMode == GlobalViewMode.Details) Click((Button)panel.FindName("ViewModeButton"));
        await Layout(panel); list.SelectedItems.Clear(); list.SelectedItems.Add(list.Items[0]); list.SelectedItems.Add(list.Items[1]);
        await controller.OpenAsync(list);
        Require(((TextBlock)controller.CurrentWindow!.FindName("FilePosition")).Text.EndsWith("/ 2"), "multi-selection preview is limited to selected items");
        Click((Button)controller.CurrentWindow.FindName("PreviewCloseButton"));
        Require(controller.CurrentWindow is null, "caption close button closes preview");
        list.SelectedItems.Clear(); list.SelectedItem = list.Items[0]; await controller.OpenAsync(list);
        list.SelectedItem = list.Items[1]; await controller.CurrentWindow!.CurrentLoad;
        Require(controller.CurrentWindow.CurrentPath == ItemMenuTarget.FromItem(list.Items[1])!.Path, "selection change updates existing popup");
        await controller.CurrentWindow.ShowItemsAsync([Target("두 페이지.pdf")], 0);
        await controller.CloseAsync();
        using (var released = new FileStream(Path.Combine(root, "두 페이지.pdf"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            Require(released.Length > 0, "preview PDF handle is released before ejecting");
        await controller.OpenAsync(list);
        panel.Close(); Require(controller.CurrentWindow is null, "owner close cleans up preview");
    }
    private static void Fixtures(string root)
    {
        var visual = new DrawingVisual(); using (var draw = visual.RenderOpen())
        { draw.DrawRectangle(Brushes.SteelBlue, null, new Rect(0, 0, 480, 320)); draw.DrawEllipse(Brushes.Gold, null, new Point(340, 80), 40, 40); draw.DrawGeometry(Brushes.SeaGreen, null, Geometry.Parse("M0,320 L170,70 300,280 370,160 480,320 Z")); }
        var bitmap = new RenderTargetBitmap(480, 320, 96, 96, PixelFormats.Pbgra32); bitmap.Render(visual);
        var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap)); using (var stream = File.Create(Path.Combine(root, "사진.png"))) png.Save(stream);
        File.WriteAllText(Path.Combine(root, "한글.txt"), "안녕하세요.\n스페이스바 미리보기입니다.\n읽기 전용 — 원본 파일은 바뀌지 않습니다.", new UTF8Encoding(true));
        File.WriteAllText(Path.Combine(root, "unicode.txt"), "안녕하세요 UTF16", Encoding.Unicode);
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance); File.WriteAllText(Path.Combine(root, "ansi.txt"), "안녕하세요 ANSI", Encoding.GetEncoding(949));
        File.WriteAllText(Path.Combine(root, "script.html"), "<script>throw new Error('must not execute');</script>");
        File.WriteAllText(Path.Combine(root, "large.log"), new string('a', QuickPreviewFiles.MaxTextBytes - 1) + "한글...", new UTF8Encoding(false));
        File.WriteAllBytes(Path.Combine(root, "binary.xyz"), [0, 1, 2, 0, 254]);
        File.WriteAllText(Path.Combine(root, "broken.pdf"), "not a PDF"); File.WriteAllText(Path.Combine(root, "broken.png"), "not an image");
        File.WriteAllText(Path.Combine(root, "broken.mp4"), "not media");
        Pdf(Path.Combine(root, "두 페이지.pdf"));
        using var wav = new BinaryWriter(File.Create(Path.Combine(root, "silence.wav")));
        wav.Write(Encoding.ASCII.GetBytes("RIFF")); wav.Write(16036); wav.Write(Encoding.ASCII.GetBytes("WAVEfmt ")); wav.Write(16); wav.Write((short)1); wav.Write((short)1); wav.Write(8000); wav.Write(16000); wav.Write((short)2); wav.Write((short)16); wav.Write(Encoding.ASCII.GetBytes("data")); wav.Write(16000); wav.Write(new byte[16000]);
    }
    private static void Pdf(string path)
    {
        var streams = new[] { "0.3 0.5 0.8 rg 0 0 300 400 re f 1 1 1 rg BT /F1 22 Tf 35 220 Td (Windows PDF - Page 1) Tj ET", "0.3 0.7 0.4 rg 0 0 300 400 re f 1 1 1 rg BT /F1 22 Tf 35 220 Td (Windows PDF - Page 2) Tj ET" };
        string[] objects = ["<< /Type /Catalog /Pages 2 0 R >>", "<< /Type /Pages /Kids [3 0 R 5 0 R] /Count 2 >>", "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 300 400] /Resources << /Font << /F1 7 0 R >> >> /Contents 4 0 R >>", $"<< /Length {streams[0].Length} >>\nstream\n{streams[0]}\nendstream", "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 300 400] /Resources << /Font << /F1 7 0 R >> >> /Contents 6 0 R >>", $"<< /Length {streams[1].Length} >>\nstream\n{streams[1]}\nendstream", "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>"];
        var content = new StringBuilder("%PDF-1.4\n"); var offsets = new List<int> { 0 };
        for (var i = 0; i < objects.Length; i++) { offsets.Add(content.Length); content.Append($"{i + 1} 0 obj\n{objects[i]}\nendobj\n"); }
        var xref = content.Length; content.Append($"xref\n0 {objects.Length + 1}\n0000000000 65535 f \n");
        foreach (var offset in offsets.Skip(1)) content.Append($"{offset:0000000000} 00000 n \n");
        content.Append($"trailer\n<< /Size {objects.Length + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n"); File.WriteAllText(path, content.ToString(), Encoding.ASCII);
    }
    private static void Save(Window window, string name)
    {
        var dpi = VisualTreeHelper.GetDpi(window); var image = new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth * dpi.DpiScaleX), (int)Math.Ceiling(window.ActualHeight * dpi.DpiScaleY), dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        image.Render(window); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image)); using var stream = File.Create(Path.Combine(AppContext.BaseDirectory, name)); encoder.Save(stream);
    }
    private static async Task Layout(Window window) => await window.Dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.ApplicationIdle);
    private static async Task Wait(Func<bool> condition, string message) { var until = DateTime.UtcNow.AddSeconds(10); while (!condition() && DateTime.UtcNow < until) await Task.Delay(50); Require(condition(), message); }
    private static void Call(object target, string name, params object[] args) => target.GetType().GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(target, args);
    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static void Require(bool value, string message) { if (!value) throw new Exception(message); }
}
