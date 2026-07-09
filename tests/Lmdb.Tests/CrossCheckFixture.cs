using System.Diagnostics;
using Lmdb.Tests;

namespace Lmdb.Tests;

/// <summary>
/// Lazily generates the cross-check fixtures (real LMDB files from the Python
/// `lmdb` wheel) if they are not already present. Locates gen_fixtures.py by
/// walking up from the test assembly directory to the solution root.
/// </summary>
internal static class CrossCheckFixture
{
    private static readonly object _gate = new();
    private static string? _dir;

    public static string EnsureFixtures()
    {
        if (_dir != null) return _dir;
        lock (_gate)
        {
            if (_dir != null) return _dir;
            _dir = GenerateIfNeeded();
            return _dir;
        }
    }

    private static string GenerateIfNeeded()
    {
        string dir = "/tmp/lmdb-ref";
        if (System.IO.Directory.Exists(dir + "/seq")) return dir;  // assume complete

        string script = FindScript();
        var psi = new ProcessStartInfo("python3", script)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException("python3 not found on PATH");
        string stdout = proc.StandardOutput.ReadToEnd();
        string stderr = proc.StandardError.ReadToEnd();
        if (!proc.WaitForExit(60_000) || proc.ExitCode != 0)
            throw new InvalidOperationException(
                $"gen_fixtures.py failed (exit {proc.ExitCode})\n{stderr}");
        if (!System.IO.Directory.Exists(dir + "/seq"))
            throw new InvalidOperationException($"fixtures not generated at {dir}\n{stdout}\n{stderr}");
        return dir;
    }

    private static string FindScript()
    {
        // Walk up from the assembly directory to find the .sln, then test/crosscheck/gen_fixtures.py.
        string dir = AppContext.BaseDirectory;
        for (int i = 0; i < 8; i++)
        {
            if (System.IO.Directory.GetFiles(dir, "LmdbSharp.sln").Length > 0)
            {
                string script = System.IO.Path.Combine(dir, "test", "crosscheck", "gen_fixtures.py");
                if (System.IO.File.Exists(script)) return script;
            }
            var parent = System.IO.Directory.GetParent(dir)?.FullName;
            if (parent == null || parent == dir) break;
            dir = parent;
        }
        throw new FileNotFoundException("test/crosscheck/gen_fixtures.py not found relative to solution root");
    }
}
