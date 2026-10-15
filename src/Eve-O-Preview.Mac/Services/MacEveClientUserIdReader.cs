using System;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace EveOPreview.Services.Implementation;

/// <summary>
/// Reads the launcher-provided account subject of an EVE client from its arguments (KERN_PROCARGS2),
/// then parses it with the shared upstream parser. Arguments never reach logs or the UI.
/// </summary>
public sealed class MacEveClientUserIdReader
{
    public long? Read(int processId, string expectedTitle)
    {
        // Resolved lazily: discovery itself depends on the identity cache that uses this reader.
        var client = MacProcessMonitor.Current?.GetAllProcesses();
        if (client == null) return null;
        bool matches = false;
        foreach (var process in client)
            if (process.ProcessId == processId && process.IsEveClient && process.Title == expectedTitle) { matches = true; break; }
        if (!matches) return null;
        byte[] buffer = ReadArguments(processId);
        if (buffer == null) return null;
        try
        {
            // Layout: int argc, executable path, padding NULs, then argc NUL-terminated arguments.
            int argc = BitConverter.ToInt32(buffer, 0);
            int offset = 4;
            while (offset < buffer.Length && buffer[offset] != 0) offset++;
            while (offset < buffer.Length && buffer[offset] == 0) offset++;
            var line = new StringBuilder();
            for (int i = 0; i < argc && offset < buffer.Length; i++)
            {
                int start = offset;
                while (offset < buffer.Length && buffer[offset] != 0) offset++;
                if (line.Length > 0) line.Append(' ');
                line.Append(Encoding.UTF8.GetString(buffer, start, offset - start));
                offset++;
            }
            return EveClientUserIdReader.ExtractUserId(line.ToString());
        }
        finally { CryptographicOperations.ZeroMemory(buffer); }
    }

    private static unsafe byte[] ReadArguments(int pid)
    {
        int* mib = stackalloc int[3] { 1 /* CTL_KERN */, 49 /* KERN_PROCARGS2 */, pid };
        nuint size = 0;
        if (sysctl(mib, 3, null, &size, null, 0) != 0 || size == 0 || size > 262144) return null;
        var buffer = new byte[(int)size];
        fixed (byte* data = buffer)
            if (sysctl(mib, 3, data, &size, null, 0) != 0) return null;
        return buffer;
    }

    [DllImport("libc", SetLastError = true)]
    private static extern unsafe int sysctl(int* name, uint length, void* oldValue, nuint* oldLength, void* newValue, nuint newLength);
}
