using System;
using System.Collections.Generic;
using System.IO;
using WindowRail;

internal static class Program
{
    private static int count;
    private static void Check(bool condition, string name) { if (!condition) throw new Exception("FAIL: " + name); Console.WriteLine("PASS: " + name); count++; }
    [STAThread]
    private static int Main(string[] args)
    {
        string path = @"C:\Office\WINWORD.EXE";
        var a = new WindowInfo(new((nint)100, 42, 1000), "合同.docx - Word", path, "Word", true, false);
        var b = new WindowInfo(new((nint)101, 42, 1000), "会议纪要.docx - Word", path, "Word", true, false);
        var hidden = new WindowInfo(new((nint)102, 42, 1000), "其他桌面.docx - Word", path, "Word", false, false);
        var saved = new EntryConfig { Kind = EntryKind.Window, ExePath = path.ToLowerInvariant(), LastTitle = a.Title };
        Check(WindowRules.Restore(saved, new[] { b, a })?.Identity == a.Identity, "same process documents restore by exact caption and executable");
        Check(WindowRules.Restore(saved, new[] { a, a with { Identity = new((nint)103, 42, 1000) } }) == null, "ambiguous same-name documents never choose arbitrarily");
        Check(WindowRules.Restore(saved, new[] { a with { ExePath = @"C:\Other\WINWORD.EXE" } }) == null, "same executable basename in another folder is not a match");
        Check(WindowRules.Restore(saved, new[] { b }) == null, "closed document never rebinds to another Word document");
        Check(WindowRules.CleanTitle("预算 - 第三版.docx - Microsoft Word") == "预算 - 第三版.docx", "title cleaning preserves hyphens inside document names");
        Check(WindowRules.CleanTitle("合同.docx - Word") == "合同.docx", "Word caption yields document label");
        Check(WindowRules.CleanTitle("项目方案  -  兼容性模式 - Word") == "项目方案", "Word compatibility suffix does not crowd the document name");
        Check(WindowRules.CleanTitle("Report - Browser") == "Report - Browser", "unrecognized suffix is preserved");
        Check(WindowRules.WordDocumentReturnedHome(saved, a with { Title = "Word" }), "closing the last Word document removes its entry even if Word reuses the window for Home");
        Check(!WindowRules.WordDocumentReturnedHome(saved, b), "renaming or switching a live Word document is not treated as closing");
        Check(!WindowRules.WordDocumentReturnedHome(new EntryConfig { ExePath = path, LastTitle = "Word" }, a with { Title = "Word" }), "a deliberately added Word Home window is retained");
        var program = new EntryConfig { Kind = EntryKind.Program, ExePath = path };
        var mru = new Dictionary<WindowIdentity, long> { [a.Identity] = 1, [b.Identity] = 4, [hidden.Identity] = 10 };
        Check(WindowRules.SelectProgram(program, new[] { a, hidden, b }, mru)?.Identity == b.Identity, "program selection uses MRU on the current virtual desktop");
        Check(WindowRules.SelectProgram(program, new[] { hidden }, mru) == null, "another virtual desktop is not silently activated");
        var entry = new RailEntry(saved, a);
        entry.Update(a with { Title = "另存为.docx - Word" }, false);
        Check(entry.Label == "另存为.docx", "document rename updates visible label");
        entry.Config.Alias = "原稿";
        entry.Update(a with { Title = "新版.docx - Word" }, true);
        Check(entry.Label == "原稿" && entry.Active, "user alias survives title changes");
        string directory = Path.Combine(args.Length > 0 ? args[0] : AppContext.BaseDirectory, "config-" + Guid.NewGuid().ToString("N"));
        var store = new ConfigStore(directory);
        var config = new RailConfig { Width = 300, Entries = new() { saved, program } };
        store.Save(config);
        Check(new ConfigStore(directory).Load().Entries.Count == 2, "window selections and pinned programs survive configuration roundtrip");
        Check(!File.ReadAllText(Path.Combine(directory, "settings.json")).Contains("ProcessId"), "transient process identities are not persisted");
        config.Width = 224; store.Save(config);
        File.WriteAllText(Path.Combine(directory, "settings.json"), "{ broken");
        var recovered = new ConfigStore(directory);
        Check(recovered.Load().Width == 300 && recovered.RecoveryMessage != null, "invalid configuration recovers the previous atomic backup");
        File.WriteAllText(Path.Combine(directory, "settings.json"), "{\"Width\":9999,\"AnchorX\":-3,\"SurfaceOpacity\":9}");
        var bounded = new ConfigStore(directory).Load();
        Check(bounded.Width == 360 && bounded.AnchorX == 0 && bounded.SurfaceOpacity == 0.8, "invalid saved dimensions are bounded");
        Console.WriteLine($"{count} checks passed."); return 0;
    }
}
