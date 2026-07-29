using System.Text;

namespace ING_eBay_AutoLister.Services;

/// <summary>
/// Saving that survives the machine dying mid-write.
///
/// The problem this replaces: <c>File.WriteAllText</c> opens the file with truncate — it empties
/// the file, THEN writes the new contents. Between those two steps the file is zero bytes on disk.
/// A power cut, a crash, a force-quit or an installer restarting the app in that window leaves the
/// seller with an empty <c>credentials.json</c>: eBay tokens, Anthropic key and every marketplace
/// connection gone, with nothing to recover from. It is a narrow window, but it is on the one file
/// whose loss costs the most, and it is written every time a setting changes.
///
/// What happens instead:
///   1. Write the new contents to <c>&lt;file&gt;.tmp</c> and flush it to the physical disk.
///   2. Atomically swap it into place, and keep what was there as <c>&lt;file&gt;.bak</c>.
///
/// Step 2 is a single filesystem operation on NTFS, so at every instant the real path holds either
/// the complete old file or the complete new one — never a half of either. The <c>.bak</c> then
/// covers the case the swap cannot: contents that were themselves wrong when written.
///
/// <see cref="ReadWithRecovery"/> is the other half. A file that is missing, empty or unreadable
/// falls back to its backup rather than being reported as "you have nothing saved", which is what
/// turns a bad write into a lost account.
/// </summary>
public static class AtomicFile
{
    public static string TempPathFor(string path)   => path + ".tmp";
    public static string BackupPathFor(string path) => path + ".bak";

    /// <summary>
    /// Writes <paramref name="contents"/> to <paramref name="path"/> so that the path never
    /// contains a partial file, keeping the previous contents as a <c>.bak</c>.
    /// </summary>
    public static void WriteAllText(string path, string contents)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        var temp = TempPathFor(path);

        // Flushed all the way to the device, not just to the OS cache. Without this the swap can
        // land while the new bytes are still in a write-back buffer, which is how a crash produces
        // a correctly-named file full of nulls.
        using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
        using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
        {
            writer.Write(contents);
            writer.Flush();
            stream.Flush(flushToDisk: true);
        }

        if (File.Exists(path))
        {
            // Replace keeps a copy of what it displaced. ignoreMetadataErrors: a failure to carry
            // ACLs across must not fail the save itself.
            File.Replace(temp, path, BackupPathFor(path), ignoreMetadataErrors: true);
        }
        else
        {
            // Nothing to replace — first ever write.
            File.Move(temp, path);
        }
    }

    /// <summary>
    /// Reads <paramref name="path"/>, falling back to its backup when the main file is missing,
    /// empty or unreadable. Returns null only when neither has anything.
    /// </summary>
    /// <param name="isValid">
    /// Optional check applied to the contents — pass a JSON parse here. A file can be complete and
    /// still be garbage (a truncated write from before this class existed), and the backup is only
    /// useful if that case falls through to it too.
    /// </param>
    public static string? ReadWithRecovery(string path, Func<string, bool>? isValid = null)
    {
        foreach (var candidate in new[] { path, BackupPathFor(path) })
        {
            try
            {
                if (!File.Exists(candidate)) continue;
                var text = File.ReadAllText(candidate);
                if (string.IsNullOrWhiteSpace(text)) continue;
                if (isValid is not null && !isValid(text)) continue;
                return text;
            }
            catch
            {
                // Locked or unreadable — try the backup rather than failing the load.
            }
        }
        return null;
    }

    /// <summary>
    /// The JavaScript half, for the Playwright scripts that save browser sessions. Same two steps
    /// as <see cref="WriteAllText"/>, expressed for Node — a session file truncated by a crash
    /// mid-write means the seller silently has to log into Facebook or eBay again.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The temp name carries the process id, which is not cosmetic. Two login windows open at once —
    /// a double-click on Connect, or the app restarted while a login was still running — both wrote
    /// to the same <c>.tmp</c>, and the second write landing inside the first one's rename produced
    /// exactly the truncated session file this class exists to prevent. A per-process temp means the
    /// worst two concurrent saves can do is have one complete session replace another.
    /// </para>
    /// <para>
    /// The write is also flushed to the device before the rename. <c>writeFileSync</c> returns once
    /// the bytes are in the OS cache; a machine that loses power in that window comes back to a
    /// correctly-named file full of zeros, which is the same lost login by a slower route.
    /// </para>
    /// </remarks>
    /// <param name="jsPath">Already-escaped path, as produced by <see cref="NodeRuntime.JsPath"/>.</param>
    /// <param name="jsValueExpression">JavaScript expression producing the string to write.</param>
    public static string NodeWriteJs(string jsPath, string jsValueExpression) =>
        $$"""
          (() => {
            const fs = require('fs');
            const path = require('path');
            const target = '{{jsPath}}';
            const tmp = target + '.' + process.pid + '.tmp';
            try { fs.mkdirSync(path.dirname(target), { recursive: true }); } catch (_) {}
            const fd = fs.openSync(tmp, 'w');
            try {
              fs.writeFileSync(fd, {{jsValueExpression}});
              // All the way to the device, not just to the OS cache.
              try { fs.fsyncSync(fd); } catch (_) {}
            } finally {
              try { fs.closeSync(fd); } catch (_) {}
            }
            // Keep the previous session as .bak, then swap. rename() is atomic within a volume, so
            // the real path is never a half-written file.
            try { if (fs.existsSync(target)) fs.copyFileSync(target, target + '.bak'); } catch (_) {}
            fs.renameSync(tmp, target);
          })();
          """;
}
