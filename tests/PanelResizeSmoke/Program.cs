using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Input;
using System.Windows.Threading;
using ExternalDrivePanel;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        TestModel();
        var folder = Path.Combine(Path.GetTempPath(), "ExternalDeskPanel-Resize-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var snapshots = Enumerable.Range(1, 3).Select(i => new DriveSnapshot((uint)i,
                Directory.CreateDirectory(Path.Combine(folder, "drive" + i)).FullName + Path.DirectorySeparatorChar,
                "Resize test " + i, DriveType.Removable)).ToArray();
            var store = new ConfigStore(Path.Combine(folder, "config.json"));
            var config = new AppConfig { NotifyNewDrive = false, DriveOrder = ["1", "2", "3"] };
            foreach (var snapshot in snapshots)
            {
                var favorites = new List<FavoriteConfig>();
                for (var i = 1; i <= 24; i++)
                {
                    var name = $"Resize item {i:00}";
                    Directory.CreateDirectory(Path.Combine(snapshot.Root, name));
                    favorites.Add(new FavoriteConfig { RelativePath = name, DisplayName = name });
                }
                config.Drives[snapshot.Serial.ToString()] = new() { Registered = true, Favorites = favorites };
            }
            store.Save(config);
            var app = new App(store, () => snapshots);
            var code = 0;
            app.Startup += (_, _) => app.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(async () =>
            {
                try
                {
                    var top = Field<TopBarWindow>(app, "_topBar");
                    Require(app.IsSoftwareRendering == args.Contains("--software-rendering"), "diagnostic renderer follows this launch only");
                    if (app.IsSoftwareRendering)
                        Require(System.Windows.Media.RenderOptions.ProcessRenderMode == RenderMode.SoftwareOnly, "software diagnostic really bypasses WPF hardware rendering");
                    Console.WriteLine($"RENDERER: {(app.IsSoftwareRendering ? "SoftwareOnly (diagnostic)" : "Default")}");
                    top.Title = "Resize smoke — controls";
                    var panels = Field<Dictionary<uint, DrivePanelWindow>>(app, "_panels");
                    foreach (var pair in panels) pair.Value.Title = "Resize smoke — drive " + pair.Key;
                    if (args.Contains("--interactive"))
                    {
                        // Make only this disposable fixture discoverable to desktop automation.
                        top.ShowInTaskbar = true;
                        foreach (var panel in panels.Values) panel.ShowInTaskbar = true;
                        var interactiveWindows = new Window[] { top }.Concat(panels.Values).ToArray();
                        LayoutResizeHit? activeHit = null;
                        var anchorRight = 0; var anchorBottom = 0; var frames = 0; var driftRight = 0; var driftBottom = 0;
                        System.Windows.Media.CompositionTarget.Rendering += ObserveInteractiveFrame;
                        void ObserveInteractiveFrame(object? sender, EventArgs e)
                        {
                            if (activeHit is null) return;
                            frames++;
                            if (activeHit.Edges.HasFlag(ResizeEdges.Left))
                                driftRight = Math.Max(driftRight, interactiveWindows.Max(window => Math.Abs(Bounds(window).Right - anchorRight)));
                            driftBottom = Math.Max(driftBottom, Math.Abs(Bounds(panels.OrderBy(pair => pair.Key).Last().Value).Bottom - anchorBottom));
                        }
                        foreach (var window in new Window[] { top }.Concat(panels.Values))
                        {
                            window.AddHandler(Mouse.PreviewMouseDownEvent, new MouseButtonEventHandler((_, e) =>
                            {
                                var point = window.PointToScreen(e.GetPosition(window));
                                Console.WriteLine($"DOWN {window.Title}: {point}; hit={Field<PanelLayoutState>(app, "_panelLayout").HitTest(point.X, point.Y)}; handled={e.Handled}; captured={window.IsMouseCaptured}");
                                if (window.IsMouseCaptured)
                                {
                                    activeHit = Field<PanelLayoutState>(app, "_panelLayout").HitTest(point.X, point.Y);
                                    anchorRight = Bounds(top).Right;
                                    anchorBottom = Bounds(panels.OrderBy(pair => pair.Key).Last().Value).Bottom;
                                    frames = driftRight = driftBottom = 0;
                                }
                            }), true);
                            window.AddHandler(Mouse.PreviewMouseUpEvent, new MouseButtonEventHandler((_, _) =>
                            {
                                if (activeHit is null) return;
                                ObserveInteractiveFrame(null, EventArgs.Empty);
                                Console.WriteLine($"DRAG TRACE: {frames} rendered frames; fixed right drift={driftRight}px; fixed bottom drift={driftBottom}px.");
                                activeHit = null;
                            }), true);
                        }
                        top.Closed += (_, _) => System.Windows.Media.CompositionTarget.Rendering -= ObserveInteractiveFrame;
                        top.Closed += (_, _) => app.Dispatcher.BeginInvoke(new Action(() => Call(app, "ExitApplication")));
                        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
                        var last = "";
                        timer.Tick += (_, _) =>
                        {
                            var current = string.Join("; ", new Window[] { top }.Concat(panels.Values).Select(window => $"{window.Title}: {Bounds(window)}"));
                            if (current != last) { Console.WriteLine(current); last = current; }
                        };
                        timer.Start(); Console.WriteLine("READY: disposable three-drive resize fixture; config is isolated.");
                        return;
                    }
                    var state = Field<PanelLayoutState>(app, "_panelLayout");
                    var original = state.Cell;
                    var gesture = state.Begin(new(ResizeEdges.Left | ResizeEdges.Bottom), original.X, original.Y + original.Height);
                    state.Drag(gesture, original.X - 80, original.Y + original.Height + 60);
                    Call(app, "RepositionWindows");
                    var manual = state.Cell;
                    Require(manual.Width == original.Width + 80 && manual.Height == original.Height + 60, "outer resize expands aggregate bounds");
                    var first = state.Panels()[0];
                    var boundary = first.Y + first.Height;
                    state.Drag(state.Begin(new(ResizeEdges.None, 0), first.X + 20, boundary), first.X + 20, boundary + 40);
                    Call(app, "RepositionWindows");
                    var rectangles = state.Panels().ToArray();
                    await Task.Delay(2300); // Includes the app's real polling tick.
                    Require(state.Cell == manual && state.Panels().SequenceEqual(rectangles), "polling preserves user bounds and split ratios");
                    foreach (var pair in panels.OrderBy(pair => pair.Key).Zip(rectangles)) RequireBounds(pair.First.Value, pair.Second);
                    RequireBounds(top, new LayoutCalculator().CalculateTopBar(manual));
                    var positionMessages = 0;
                    HwndSourceHook countPositions = (IntPtr hwnd, int message, IntPtr wp, IntPtr lp, ref bool handled) =>
                    { if (message == 0x47) positionMessages++; return IntPtr.Zero; };
                    var sources = new Window[] { top }.Concat(panels.Values).Select(window => HwndSource.FromHwnd(new WindowInteropHelper(window).Handle)).ToArray();
                    foreach (var source in sources) source.AddHook(countPositions);
                    for (var attempt = 0; attempt < 20; attempt++) Call(app, "RepositionWindows");
                    foreach (var source in sources) source.RemoveHook(countPositions);
                    Require(positionMessages == 0, "unchanged geometry produces zero native resize/repaint messages");
                    await TestContinuousResize(app, top, panels.Values.ToArray());
                    Exception? applyError = null;
                    _ = app.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() =>
                    {
                        var dialog = app.Windows.OfType<ZoneSettingsWindow>().Single();
                        try
                        {
                            ((TextBox)dialog.FindName("MarginAllText")).Text = "1";
                            ((TextBox)dialog.FindName("MarginAllText")).Text = "0";
                            Require(dialog.IsVisible, "live settings keep the dialog open");
                        }
                        catch (Exception ex) { applyError = ex; }
                        finally { dialog.Close(); }
                    }));
                    Call(app, "TopBar_OnSettingsRequested", top, EventArgs.Empty);
                    if (applyError is not null) throw applyError;
                    Require(state.Cell == original && Math.Abs(state.Panels()[0].Height - state.Panels()[1].Height) < .1,
                        "settings Apply reset restores zone bounds and equal split");
                    Console.WriteLine("PASS WPF: outer bounds + divider resize, native contiguous placement, real polling preservation, zone Apply reset.");
                }
                catch (Exception ex) { Console.Error.WriteLine(ex); code = 1; }
                if (!args.Contains("--interactive") || code != 0) Call(app, "ExitApplication");
            }));
            app.Run(); return code;
        }
        finally { Directory.Delete(folder, true); }
    }

    private static void TestModel()
    {
        var fixedEdgeErrors = 0;
        foreach (var origin in new[] { -100.5, -.5, .5, 100.5 })
        {
            var probe = new PanelLayoutState();
            probe.Configure(new(origin, origin, 601, 1042), 3, SplitDirection.Vertical);
            var boundaryY = probe.Panels()[1].Y;
            var drag = probe.Begin(new(ResizeEdges.None, 1), origin + 30, boundaryY);
            for (var delta = -100; delta <= 100; delta++)
            {
                probe.Drag(drag, origin + 30, boundaryY + delta);
                var rects = probe.Panels();
                if (Math.Round(rects[^1].Y + rects[^1].Height) != Math.Round(probe.Cell.Y + probe.Cell.Height)) fixedEdgeErrors++;
            }
        }
        Console.WriteLine($"DIAGNOSTIC fixed-bottom rounding discrepancies: {fixedEdgeErrors} / 804 samples.");
        Require(fixedEdgeErrors == 0, "half-pixel origins cannot move the fixed bottom edge");
        var state = new PanelLayoutState();
        var original = new LayoutRect(-900, -100, 600, 1042);
        state.Configure(original, 3, SplitDirection.Vertical);
        Require(state.HitTest(-899, -99)?.Edges == (ResizeEdges.Left | ResizeEdges.Top), "negative-monitor corner hit");
        Require(state.HitTest(-700, 100) is null, "content clicks are not resize gestures");
        var first = state.Panels()[0]; var y = first.Y + first.Height;
        Require(state.HitTest(-700, y)?.Boundary == 0, "vertical divider hit");
        var gesture = state.Begin(new(ResizeEdges.None, 0), -700, y);
        state.Drag(gesture, -700, y + 100);
        var changed = state.Panels();
        Require(Math.Abs(changed[0].Height - first.Height - 100) < .01 && Math.Abs(changed[0].Y + changed[0].Height - changed[1].Y) < .01,
            "divider transfers height only between neighbors without gaps");
        Require(Math.Abs(changed[2].Height - first.Height) < .01, "other neighbors unchanged");
        state.Drag(gesture, -700, y + 10000);
        Require(state.Panels()[1].Height >= PanelLayoutState.MinimumPanelHeight - .01, "divider minimum height includes capacity bar");
        state.Configure(original, 3, SplitDirection.Vertical);
        Require(Math.Abs(state.Panels()[1].Height - PanelLayoutState.MinimumPanelHeight) < .01, "poll Configure does not reset resize");
        state.Reset(); state.Configure(original, 3, SplitDirection.Vertical);
        state.Drag(gesture, -700, y + 200);
        Require(Math.Abs(state.Panels()[0].Height - first.Height) < .01, "reset invalidates stale gestures");
        state.Configure(new(-900, 0, 1000, 600), 2, SplitDirection.Horizontal);
        var split = state.Panels()[0].X + state.Panels()[0].Width;
        Require(state.HitTest(split, 100)?.Boundary == 0, "horizontal divider hit");
        state.Drag(state.Begin(new(ResizeEdges.None, 0), split, 100), split + 100, 100);
        Require(Math.Abs(state.Panels()[0].Width - 600) < .01 && Math.Abs(state.Panels()[1].Width - 400) < .01, "horizontal divider resize");
        var outer = state.Begin(new(ResizeEdges.Left | ResizeEdges.Top), -900, 0);
        state.Drag(outer, -1000, -40);
        Require(state.Cell == new LayoutRect(-1000, -40, 1100, 640), "outer corner changes origin and size together");
        state.Drag(outer, 10000, 10000);
        Require(state.Cell.Width >= 600 && state.Cell.Height >= 182, "outer minimum bounds");
        state.Configure(new(0, 0, 800, 900), 1, SplitDirection.Vertical);
        Require(state.Cell.X == 0 && state.Panels().Count == 1, "monitor/default/count changes recover layout");
        state.Configure(new(0, 0, 800, 900), 0, SplitDirection.Vertical);
        Require(state.Panels().Count == 0, "no-drive layout");
        Console.WriteLine("PASS model: edge/divider hit tests, both split directions, minima, negative coordinates, polling, reset, count/monitor changes.");
    }

    private static async Task TestContinuousResize(App app, TopBarWindow top, DrivePanelWindow[] panels)
    {
        var zone = Field<ZoneSettings>(app, "_zoneSettings");
        var original = zone.Clone();
        try
        {
            for (var monitor = 0; monitor < new MonitorService().GetMonitors().Count; monitor++)
            foreach (var direction in new[] { SplitDirection.Vertical, SplitDirection.Horizontal })
            {
                zone.MonitorIndex = monitor; zone.SplitDirection = direction;
                // Three horizontal panels need more than the default quarter-screen
                // cell to fit their navigation controls. Test valid usable widths.
                zone.GridCols = direction == SplitDirection.Horizontal ? 1 : original.GridCols;
                zone.CellCol = direction == SplitDirection.Horizontal ? 0 : original.CellCol;
                Call(app, "ResetManualLayout");
                await Task.Delay(60);
                await TestContinuousResizeOnMonitor(app, top, panels);
                await TestFixedEdges(app, top, panels);
                Console.WriteLine($"PASS monitor {monitor}, DPI {System.Windows.Media.VisualTreeHelper.GetDpi(top).DpiScaleX}, {direction}: live resize + anchored geometry regression.");
            }
        }
        finally
        {
            zone.CopyFrom(original);
            Call(app, "ResetManualLayout");
        }
    }

    private static async Task TestContinuousResizeOnMonitor(App app, TopBarWindow top, DrivePanelWindow[] panels)
    {
        var state = Field<PanelLayoutState>(app, "_panelLayout");
        var type = typeof(App).Assembly.GetType("ExternalDrivePanel.LayoutResizeController")!;
        Point? cursor = null; var applications = 0;
        var controller = Activator.CreateInstance(type, state,
            (Action)(() => { applications++; Call(app, "RepositionWindows"); }),
            (Func<Point?>)(() => cursor))!;
        var windows = new Window[] { top }.Concat(panels).ToArray();
        Field<HashSet<Window>>(controller, "_windows").UnionWith(windows);
        var timer = Field<DispatcherTimer>(controller, "_timer");
        Require(timer.Dispatcher == app.Dispatcher && !timer.IsEnabled, "resize sampling is inactive outside a gesture");
        foreach (var hit in new[] { new LayoutResizeHit(ResizeEdges.Left | ResizeEdges.Top),
                     new LayoutResizeHit(ResizeEdges.Right | ResizeEdges.Bottom), new LayoutResizeHit(ResizeEdges.None, 0) })
        {
            var cell = state.Cell;
            var origin = hit.Boundary >= 0
                ? state.Direction == SplitDirection.Vertical ? new Point(cell.X + 30, state.Panels()[1].Y)
                    : new Point(state.Panels()[1].X, state.Panels()[1].Y + 30)
                : new Point(hit.Edges.HasFlag(ResizeEdges.Left) ? cell.X : cell.X + cell.Width,
                    hit.Edges.HasFlag(ResizeEdges.Top) ? cell.Y : cell.Y + cell.Height);
            var movement = new Vector(hit.Boundary < 0 || state.Direction == SplitDirection.Horizontal ? 1 : 0,
                hit.Boundary < 0 || state.Direction == SplitDirection.Vertical ? 1 : 0);
            SetField(controller, "_gesture", state.Begin(hit, origin.X, origin.Y));
            SetField(controller, "_lastPoint", origin);
            Call(controller, "BeginInteractiveResize");
            Require(windows.All(UserInputResize), "every affected WPF HWND enables synchronous user-resize presentation");
            var before = applications;
            for (var frame = 1; frame <= 24; frame++)
            {
                cursor = origin + movement * frame; Call(controller, "ApplyCursorPosition");
                await Task.Delay(18);
                RequireBounds(top, new LayoutCalculator().CalculateTopBar(state.Cell));
                foreach (var pair in panels.Zip(state.Panels())) RequireBounds(pair.First, pair.Second);
                Require(top.IsMeasureValid && top.IsArrangeValid && panels.All(p => p.IsMeasureValid && p.IsArrangeValid),
                    "WPF layout is settled before the next resize sample");
                var expected = state.Cell;
                for (var repeat = 0; repeat < 4; repeat++) Call(controller, "ApplyCursorPosition");
                Require(state.Cell == expected && applications == before + frame,
                    "moving/resizing HWND cannot feed back into stationary physical cursor input");
            }
            cursor = origin + movement * 25; Call(controller, "Complete");
            Require(applications == before + 25, "release flushes final unrendered pointer position exactly once");
            Require(!timer.IsEnabled && FieldOrNull(controller, "_gesture") is null, "release clears gesture and sampling");
            Require(windows.All(w => !UserInputResize(w)), "release balances native sizing notifications for every HWND");
        }
        Call(controller, "BeginInteractiveResize"); Call(controller, "Cancel"); Call(controller, "Cancel");
        Require(windows.All(w => !UserInputResize(w)), "cancellation is idempotent and restores normal rendering");
        Require(top.UseLayoutRounding && top.SnapsToDevicePixels && panels.All(p => p.UseLayoutRounding && p.SnapsToDevicePixels),
            "all panel chrome snaps to physical pixels");
        Console.WriteLine("PASS continuous resize: 72 rendered outer/divider frames, synchronous presentation, stable stationary cursor, final release flush, cancellation.");
    }

    private static bool UserInputResize(Window window) => Field<bool>(HwndSource.FromHwnd(new WindowInteropHelper(window).Handle).CompositionTarget, "_userInputResize");

    private static async Task TestFixedEdges(App app, TopBarWindow top, DrivePanelWindow[] panels)
    {
        await app.Dispatcher.InvokeAsync(() => { top.UpdateLayout(); foreach (var panel in panels) panel.UpdateLayout(); }, DispatcherPriority.ApplicationIdle);
        var state = Field<PanelLayoutState>(app, "_panelLayout");
        var windows = new Window[] { top }.Concat(panels).ToArray();
        var sources = windows.Select(w => HwndSource.FromHwnd(new WindowInteropHelper(w).Handle)).ToArray();
        var type = typeof(App).Assembly.GetType("ExternalDrivePanel.LayoutResizeController")!;
        var controller = Activator.CreateInstance(type, state, (Action)(() => Call(app, "RepositionWindows")), (Func<Point?>)(() => null))!;
        Field<HashSet<Window>>(controller, "_windows").UnionWith(windows);
        try
        {
            // Last divider targets the lower window moving up and growing, the
            // exact direction reported by the user. Left resize has a fixed right.
            foreach (var hit in new[] { new LayoutResizeHit(ResizeEdges.Left), new LayoutResizeHit(ResizeEdges.None, panels.Length - 2) })
            {
                var before = windows.Select(Bounds).ToArray();
                var iconRight = windows.Select(w => ((FrameworkElement)w.FindName("ViewModeButton")).PointToScreen(new Point())).ToArray();
                var rectangles = state.Panels();
                var cell = state.Cell;
                var origin = hit.Boundary < 0 ? new Point(cell.X, cell.Y + 20)
                    : state.Direction == SplitDirection.Vertical ? new Point(cell.X + 20, rectangles[^1].Y)
                    : new Point(rectangles[^1].X, rectangles[^1].Y + 20);
                var gesture = state.Begin(hit, origin.X, origin.Y);
                Call(controller, "BeginInteractiveResize");
                foreach (var step in Enumerable.Range(1, 32).Concat(Enumerable.Range(0, 32).Reverse()))
                {
                    var horizontal = hit.Boundary < 0 || state.Direction == SplitDirection.Horizontal;
                    state.Drag(gesture, origin.X - (horizontal ? step : 0), origin.Y - (horizontal ? 0 : step));
                    Call(app, "RepositionWindows");
                    // Check native bounds immediately, not just after WPF settles.
                    for (var i = 0; i < windows.Length; i++)
                    {
                        var now = Bounds(windows[i]);
                        var rightMayMove = state.Direction == SplitDirection.Horizontal
                            && (hit.Boundary < 0 ? i > 0 && i < windows.Length - 1 : i == windows.Length - 2);
                        var bottomMayMove = hit.Boundary >= 0 && state.Direction == SplitDirection.Vertical && i == windows.Length - 2;
                        Require((rightMayMove || now.Right == before[i].Right) && (bottomMayMove || now.Bottom == before[i].Bottom),
                            $"fixed right/bottom edges remain exact throughout native resizing: direction={state.Direction}, boundary={hit.Boundary}, step={step}, window={i}, before=({before[i].Left},{before[i].Top},{before[i].Right},{before[i].Bottom}), now=({now.Left},{now.Top},{now.Right},{now.Bottom}), cell={state.Cell}");
                    }
                    await Task.Delay(16);
                    // On left resizing the right-anchored header buttons must also
                    // retain their screen coordinates after every presented layout.
                    if (hit.Boundary < 0)
                        for (var i = 0; i < windows.Length; i++)
                        {
                            if (state.Direction == SplitDirection.Horizontal && i > 0 && i < windows.Length - 1) continue;
                            var point = ((FrameworkElement)windows[i].FindName("ViewModeButton")).PointToScreen(new Point());
                            Require(Math.Abs(point.X - iconRight[i].X) < .01 && Math.Abs(point.Y - iconRight[i].Y) < .01,
                                $"right-aligned header controls stay fixed in screen pixels: window {i}, step {step}, before {iconRight[i]}, now {point}, native {Bounds(windows[i])}");
                        }
                }
                Call(controller, "Cancel");
            }
            Console.WriteLine("PASS fixed edges: 128 forward/reverse frames, native/header anchors. Visual jitter is a known limitation, not a pass criterion.");
        }
        finally { Call(controller, "Cancel"); }
    }

    private static object? FieldOrNull(object instance, string name) => instance.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(instance);
    private static void SetField(object instance, string name, object value) => instance.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(instance, value);

    private static void RequireBounds(Window window, LayoutRect rect)
    {
        var bounds = Bounds(window);
        Require(bounds.Left == Math.Round(rect.X) && bounds.Top == Math.Round(rect.Y)
            && bounds.Right == Math.Round(rect.X + rect.Width) && bounds.Bottom == Math.Round(rect.Y + rect.Height), "physical bounds match model");
    }
    private static NativeRect Bounds(Window window) { GetWindowRect(new WindowInteropHelper(window).Handle, out var rect); return rect; }
    private static T Field<T>(object instance, string name) => (T)instance.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(instance)!;
    private static void Call(object instance, string name, params object[] args) => instance.GetType().GetMethod(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(instance, args);
    private static void Require(bool value, string message) { if (!value) throw new Exception(message); }
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect
    {
        public int Left, Top, Right, Bottom;
        public override string ToString() => $"[{Left},{Top},{Right - Left},{Bottom - Top}]";
    }
    [StructLayout(LayoutKind.Sequential)] private struct WindowPosition
    {
        public IntPtr Window, InsertAfter;
        public int X, Y, Width, Height;
        public uint Flags;
    }
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr window, out NativeRect rect);
}
