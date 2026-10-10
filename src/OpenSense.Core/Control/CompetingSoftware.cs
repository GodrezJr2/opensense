using System.ComponentModel;
using System.Diagnostics;

namespace OpenSense.Core.Control;

/// <summary>
/// Acer's own gaming software running next to OpenSense. When PredatorSense or NitroSense starts it sets the operating
/// mode and the fans it last had, over what OpenSense set.
/// </summary>
public interface ICompetingSoftware
{
    /// <summary>The programs running now, by name, each once.</summary>
    IReadOnlyList<string> Running();

    /// <summary>Closes them; the names of the ones that were closed.</summary>
    IReadOnlyList<string> Close();
}

/// <summary>
/// PredatorSense and NitroSense on this PC. Only their windows' processes are touched: the Acer services behind them stay,
/// because the keyboard's lighting still goes through those.
/// </summary>
public sealed class WindowsCompetingSoftware : ICompetingSoftware
{
    /// <summary>The process each program runs as (<c>PredatorSense.exe</c>), and the name to show for it.</summary>
    private static readonly (string Process, string Name)[] Known =
        [("PredatorSense", "PredatorSense"), ("NitroSense", "NitroSense")];

    public IReadOnlyList<string> Running()
    {
        List<string> found = [];
        foreach (var (process, name) in Known)
        {
            var running = Find(process);
            if (running.Length > 0)
                found.Add(name);
            foreach (var p in running)
                p.Dispose();
        }
        return found;
    }

    public IReadOnlyList<string> Close()
    {
        List<string> closed = [];
        foreach (var (process, name) in Known)
        {
            var killed = false;
            foreach (var p in Find(process))
            {
                try
                {
                    // Electron-based: a main process with its GPU, network and renderer children.
                    p.Kill(entireProcessTree: true);
                    killed = true;
                }
                catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException)
                {
                    // Gone already, or not ours to close.
                }
                finally
                {
                    p.Dispose();
                }
            }
            if (killed)
                closed.Add(name);
        }
        return closed;
    }

    private static Process[] Find(string process)
    {
        try
        {
            return Process.GetProcessesByName(process);
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
        {
            return [];
        }
    }
}
