using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Span.Helpers;
using Span.Services;

namespace Span.Tests.Services;

/// <summary>
/// "Zuletzt verwendet" auf der Startseite: Verknüpfungen lesen (Unicode), neueste zuerst,
/// Ordner/gelöschte Ziele und Doppelte überspringen. Arbeitet mit einem Temp-Ordner statt Windows' Recent.
/// </summary>
[TestClass]
public class RecentFilesServiceTests
{
    private string _dir = null!;
    private string _recent = null!;

    [TestInitialize]
    public void Setup()
    {
        _dir = Path.Combine(Path.GetTempPath(), "SpanTests_Recent_" + Guid.NewGuid().ToString("N")[..8]);
        _recent = Path.Combine(_dir, "Recent");
        Directory.CreateDirectory(_recent);
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private string MakeFile(string name)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, "x");
        return path;
    }

    private void MakeLink(string linkName, string target, DateTime lastUsed)
    {
        // Über IShellLinkW anlegen: WScript.Shell kann keine Ziele außerhalb der ANSI-Codepage speichern
        var lnk = Path.Combine(_recent, linkName + ".lnk");
        var link = (IShellLinkW)new ShellLinkCoClass();
        try
        {
            Marshal.ThrowExceptionForHR(link.SetPath(target));
            Marshal.ThrowExceptionForHR(((IPersistFile)link).Save(lnk, true));
        }
        finally
        {
            Marshal.FinalReleaseComObject(link);
        }
        File.SetLastWriteTime(lnk, lastUsed);
    }

    [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
    private class ShellLinkCoClass { }

    [ComImport, Guid("000214F9-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellLinkW
    {
        [PreserveSig] int GetPath(IntPtr pszFile, int cch, IntPtr pfd, uint fFlags);
        [PreserveSig] int GetIDList(out IntPtr ppidl);
        [PreserveSig] int SetIDList(IntPtr pidl);
        [PreserveSig] int GetDescription(IntPtr pszName, int cch);
        [PreserveSig] int SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
        [PreserveSig] int GetWorkingDirectory(IntPtr pszDir, int cch);
        [PreserveSig] int SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
        [PreserveSig] int GetArguments(IntPtr pszArgs, int cch);
        [PreserveSig] int SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);
        [PreserveSig] int GetHotkey(out short pwHotkey);
        [PreserveSig] int SetHotkey(short wHotkey);
        [PreserveSig] int GetShowCmd(out int piShowCmd);
        [PreserveSig] int SetShowCmd(int iShowCmd);
        [PreserveSig] int GetIconLocation(IntPtr pszIconPath, int cch, out int piIcon);
        [PreserveSig] int SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
        [PreserveSig] int SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, uint dwReserved);
        [PreserveSig] int Resolve(IntPtr hwnd, uint fFlags);
        [PreserveSig] int SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
    }

    [ComImport, Guid("0000010b-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPersistFile
    {
        [PreserveSig] int GetClassID(out Guid pClassID);
        [PreserveSig] int IsDirty();
        [PreserveSig] int Load([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, uint dwMode);
        [PreserveSig] int Save([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, [MarshalAs(UnmanagedType.Bool)] bool fRemember);
    }

    [TestMethod]
    public void ShellLinkReader_ReadsUnicodeTarget()
    {
        var target = MakeFile("Übersicht 한글.txt");
        MakeLink("u", target, DateTime.Now);
        Assert.AreEqual(target, ShellLinkReader.GetTarget(Path.Combine(_recent, "u.lnk")), true);
    }

    [TestMethod]
    public void Load_NewestFirst_SkipsFoldersMissingAndDuplicates()
    {
        var a = MakeFile("a.txt");
        var b = MakeFile("b.docx");
        var folder = Path.Combine(_dir, "Ordner");
        Directory.CreateDirectory(folder);

        MakeLink("a", a, DateTime.Now.AddHours(-3));
        MakeLink("b", b, DateTime.Now.AddHours(-1));
        MakeLink("a-again", a, DateTime.Now.AddHours(-5));
        MakeLink("ordner", folder, DateTime.Now);
        MakeLink("weg", Path.Combine(_dir, "geloescht.txt"), DateTime.Now.AddMinutes(-10));

        var items = RecentFilesService.Load(_recent, 10);

        CollectionAssert.AreEqual(new[] { "b.docx", "a.txt" }, items.Select(i => i.Name).ToArray());
        Assert.AreEqual(_dir, items[0].Folder, true);
    }

    [TestMethod]
    public void Load_RespectsMax()
    {
        for (int i = 0; i < 5; i++)
            MakeLink("f" + i, MakeFile($"f{i}.txt"), DateTime.Now.AddMinutes(-i));
        Assert.AreEqual(3, RecentFilesService.Load(_recent, 3).Count);
    }

    [TestMethod]
    public void Load_MissingDirectory_Empty()
    {
        Assert.AreEqual(0, RecentFilesService.Load(Path.Combine(_dir, "gibtsnicht"), 10).Count);
    }
}
