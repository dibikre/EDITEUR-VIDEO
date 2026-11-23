using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace VideoSplitter.Services;

public sealed class CacheService : ICacheService
{
    private const string LastUsedFileName = ".lastused";
    private readonly IAppLogger _logger;
    private readonly string _cacheRoot;

    public CacheService(IAppLogger logger)
    {
        _logger = logger;
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        _cacheRoot = Path.Combine(localAppData, "VideoSplitter", "cache");
    }

    public string GetCacheDirectory(string mediaPath)
    {
        string fullPath = Path.GetFullPath(mediaPath);
        long length = 0;
        long lastWriteTicks = 0;

        try
        {
            var info = new FileInfo(fullPath);
            if (info.Exists)
            {
                length = info.Length;
                lastWriteTicks = info.LastWriteTimeUtc.Ticks;
            }
        }
        catch (Exception ex)
        {
            _logger.Warn($"Impossible de lire les attributs du fichier pour la cle de cache ({fullPath}) : {ex.Message}");
        }

        string rawKey = $"{fullPath}|{length}|{lastWriteTicks}";
        byte[] hashBytes = SHA1.HashData(Encoding.UTF8.GetBytes(rawKey));
        string key = Convert.ToHexString(hashBytes).ToLowerInvariant();
        string directory = Path.Combine(_cacheRoot, key);

        TouchCacheDirectory(directory);
        return directory;
    }

    public void TouchCacheDirectory(string cacheDirectory)
    {
        try
        {
            Directory.CreateDirectory(cacheDirectory);
            string markerPath = Path.Combine(cacheDirectory, LastUsedFileName);
            File.WriteAllText(markerPath, DateTime.UtcNow.ToString("O"));
        }
        catch (Exception ex)
        {
            _logger.Warn($"Impossible de mettre a jour {LastUsedFileName} dans {cacheDirectory} : {ex.Message}");
        }
    }

    public Task CleanupAsync(TimeSpan maxAge, CancellationToken ct)
    {
        return Task.Run(() =>
        {
            if (!Directory.Exists(_cacheRoot))
            {
                return;
            }

            DateTime cutoffUtc = DateTime.UtcNow - maxAge;
            int removedCount = 0;

            string[] directories;
            try
            {
                directories = Directory.GetDirectories(_cacheRoot);
            }
            catch (Exception ex)
            {
                _logger.Warn($"Impossible d'enumerer le cache ({_cacheRoot}) : {ex.Message}");
                return;
            }

            foreach (string dir in directories)
            {
                if (ct.IsCancellationRequested)
                {
                    break;
                }

                try
                {
                    string markerPath = Path.Combine(dir, LastUsedFileName);
                    DateTime lastUsedUtc = File.Exists(markerPath)
                        ? File.GetLastWriteTimeUtc(markerPath)
                        : Directory.GetLastWriteTimeUtc(dir);

                    if (lastUsedUtc < cutoffUtc)
                    {
                        Directory.Delete(dir, recursive: true);
                        removedCount++;
                    }
                }
                catch (Exception ex)
                {
                    _logger.Warn($"Echec de suppression du dossier de cache {dir} : {ex.Message}");
                }
            }

            if (removedCount > 0)
            {
                _logger.Info($"Nettoyage du cache termine : {removedCount} dossier(s) supprime(s).");
            }
        }, ct);
    }
}
